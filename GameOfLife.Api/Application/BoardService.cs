using GameOfLife.Api.Domain;
using GameOfLife.Api.Infrastructure;

namespace GameOfLife.Api.Application;

/// <summary>Coordinates board upload and read-only generation calculations.</summary>
/// <param name="store">The SQLite store for uploaded boards.</param>
/// <param name="engine">The finite-board generation engine.</param>
public sealed class BoardService(SqliteBoardStore store, IGenerationEngine engine)
{
    /// <summary>Calculates an offset from one committed current snapshot without modifying storage.</summary>
    public BoardResponse? Preview(Guid boardId, long stepsAhead, CancellationToken cancellationToken = default)
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

    /// <summary>Reconstructs an absolute generation from the original upload without modifying storage.</summary>
    public BoardResponse? GetGeneration(Guid boardId, long generationNumber, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(generationNumber);
        var state = store.FindInitial(boardId);
        cancellationToken.ThrowIfCancellationRequested();
        return state is null ? null : ToResponse(boardId,
            Calculate(state, generationNumber, cancellationToken), generationNumber, cancellationToken);
    }

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
