using GameOfLife.Api.Domain;
using GameOfLife.Api.Infrastructure;

namespace GameOfLife.Api.Application;

public sealed class BoardService(SqliteBoardStore store, IGenerationEngine engine, int maxNextAttempts = 3)
{
    private readonly int maxNextAttempts = maxNextAttempts > 0
        ? maxNextAttempts : throw new ArgumentOutOfRangeException(nameof(maxNextAttempts));

    /// <summary>Reuses a durable conclusion before calculating from one committed snapshot.
    /// Successful discovery is saved without advancing the board; exhaustion saves nothing.</summary>
    /// <returns>The original saved conclusion, or null if the board does not exist.</returns>
    /// <exception cref="FinalStateInconclusiveException">The internal work limit could not prove a conclusion.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed before persistence completed.</exception>
    public FinalStateResponse? FinalState(Guid boardId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = store.FindFinalState(boardId);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is null)
        {
            var snapshot = store.Find(boardId);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot is not { } board) return null;
            result = new FinalStateDiscovery(engine).Discover(board.State, board.Generation, cancellationToken);
            if (result.Outcome == FinalStateOutcome.Inconclusive) throw new FinalStateInconclusiveException();
            cancellationToken.ThrowIfCancellationRequested();
            result = store.SaveFinalState(boardId, result, cancellationToken);
        }
        // Once persistence succeeds, return its result without deliberately canceling response mapping.
        var response = ToResponse(boardId, result.State, result.Generation, CancellationToken.None);
        return new(boardId, response.Generation, response.Width, response.Height, response.Cells,
            result.StartingGeneration, result.Outcome.ToString(), result.CycleLength!.Value, result.IterationsUsed);
    }

    /// <summary>Reloads and recalculates on verified conflicts, returning only its own committed state.</summary>
    /// <param name="boardId">The identifier of the board to advance.</param>
    /// <param name="cancellationToken">The token checked before calculation and transaction commit.</param>
    /// <returns>The committed next state, or null if the board was not found.</returns>
    /// <exception cref="BoardConflictException">Verified conflicts exhausted the attempt allowance.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The current generation is already at the maximum supported value.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed before a successful commit.</exception>
    public BoardResponse? Next(Guid boardId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < maxNextAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = store.Find(boardId);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot is not { } board) return null;
            if (board.Generation == long.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(board.Generation), "The board has reached the maximum generation.");
            var nextState = engine.CalculateNextGeneration(board.State, cancellationToken);
            // TryAdvance disposes the attempt before returning false. Exceptions (including
            // busy/locked and uncertain commits) propagate; they never authorize a retry.
            if (store.TryAdvance(boardId, nextState, board.Generation, cancellationToken))
                return ToResponse(boardId, nextState, board.Generation + 1, CancellationToken.None);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var latest = store.Find(boardId);
        cancellationToken.ThrowIfCancellationRequested();
        return latest is null ? null : throw new BoardConflictException();
    }

    /// <summary>Gets the board state N generations ahead of the current committed generation without advancing the board.</summary>
    /// <param name="boardId">The identifier of the board to read.</param>
    /// <param name="stepsAhead">N: the nonnegative number of generations ahead of the current generation. Zero returns the current state.</param>
    /// <param name="cancellationToken">The token checked around the read, during calculation, and during response mapping.</param>
    /// <returns>The calculated state labeled with current generation plus N, or null if the board was not found.</returns>
    /// <remarks>Uses one consistent current snapshot. For example, from current generation 5, N = 3 returns generation 8. No state is persisted.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The offset is negative or its addition would overflow the generation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed.</exception>
    public BoardResponse? GetNStatesAhead(Guid boardId, long stepsAhead, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(stepsAhead);
        var snapshot = store.Find(boardId);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is not { } board) return null;
        if (stepsAhead > long.MaxValue - board.Generation)
            throw new ArgumentOutOfRangeException(nameof(stepsAhead), "The requested generation exceeds the supported integer range.");
        var state = Calculate(board.State, stepsAhead, cancellationToken);
        return ToResponse(boardId, state, board.Generation + stepsAhead, cancellationToken);
    }

    /// <summary>Gets an absolute generation by calculating N generations from the initial uploaded board state.</summary>
    /// <param name="boardId">The identifier of the uploaded board.</param>
    /// <param name="generationNumber">N: the nonnegative absolute generation number. Zero returns the initial board state at generation 0.</param>
    /// <param name="cancellationToken">The token checked around the read, during calculation, and during response mapping.</param>
    /// <returns>The reconstructed state labeled with absolute generation N, or null if the board was not found.</returns>
    /// <remarks>Always starts from the initial board, regardless of current progress. For example, N = 3 returns generation 3 even when the current generation is 5. No state is persisted.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The requested generation is negative.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed.</exception>
    public BoardResponse? GetGeneration(Guid boardId, long generationNumber, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(generationNumber);
        var state = store.FindInitial(boardId);
        cancellationToken.ThrowIfCancellationRequested();
        return state is null ? null : ToResponse(boardId,
            Calculate(state, generationNumber, cancellationToken), generationNumber, cancellationToken);
    }

    /// <summary>Applies the generation engine the requested number of times without persisting results.</summary>
    /// <param name="state">The starting state.</param>
    /// <param name="steps">The validated nonnegative number of engine calls; zero returns the starting state.</param>
    /// <param name="cancellationToken">The token checked between calls and passed to the engine.</param>
    /// <returns>The calculated state after the requested number of steps.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was observed.</exception>
    private BoardState Calculate(BoardState state, long steps, CancellationToken cancellationToken)
    {
        for (long step = 0; step < steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state = engine.CalculateNextGeneration(state, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return state;
    }

    private static BoardResponse ToResponse(Guid boardId, BoardState state, long generation,
        CancellationToken cancellationToken)
    {
        var cells = new bool[state.Height][];
        for (var y = 0; y < state.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cells[y] = new bool[state.Width];
        }
        foreach (var (x, y) in state.LivingCells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cells[y][x] = true;
        }
        return new BoardResponse(boardId, generation, state.Width, state.Height, cells);
    }

    /// <summary>Maps a valid grid to living coordinates and persists it at generation zero.</summary>
    /// <param name="cells">A nonempty rectangular grid indexed by row, then column.</param>
    /// <param name="cancellationToken">The token checked during mapping and before persistence.</param>
    /// <returns>The generated board identifier and persisted board state.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was observed before persistence.</exception>
    public (Guid BoardId, BoardState State) Upload(
        bool[][] cells, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var livingCells = new HashSet<(int X, int Y)>();
        for (var y = 0; y < cells.Length; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < cells[y].Length; x++)
            {
                if (cells[y][x])
                {
                    livingCells.Add((x, y));
                }
            }
        }

        var state = new BoardState(cells[0].Length, cells.Length, livingCells);
        var boardId = Guid.NewGuid();
        store.Insert(boardId, state, cancellationToken);
        return (boardId, state);
    }
}
