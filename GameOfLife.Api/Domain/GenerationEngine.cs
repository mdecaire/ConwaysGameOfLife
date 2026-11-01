namespace GameOfLife.Api.Domain;

public sealed class GenerationEngine : IGenerationEngine
{
    /// <summary>
    /// Calculates a new board state without modifying the supplied state.
    /// </summary>
    /// <param name="state">The current finite board state.</param>
    /// <param name="cancellationToken">The cancellation token supplied by the caller.</param>
    /// <returns>A new state with the same dimensions.</returns>
    public BoardState CalculateNextGeneration(
        BoardState state,
        CancellationToken cancellationToken = default)
    {
        var nextLivingCells = new HashSet<(int X, int Y)>();

        foreach (var cell in GetCandidateCells(state))
        {
            var livingNeighbors = CountLivingNeighbors(state, cell);
            if (WillBeAlive(state.LivingCells.Contains(cell), livingNeighbors))
            {
                nextLivingCells.Add(cell);
            }
        }

        return new BoardState(state.Width, state.Height, nextLivingCells);
    }

    /// <summary>
    /// Collects living cells and their in-bounds neighbors for survival and birth evaluation.
    /// </summary>
    /// <param name="state">The current board state.</param>
    /// <returns>A distinct set of coordinates that could be alive in the next generation.</returns>
    private static HashSet<(int X, int Y)> GetCandidateCells(BoardState state)
    {
        var candidates = new HashSet<(int X, int Y)>(state.LivingCells);
        foreach (var cell in state.LivingCells)
        {
            candidates.UnionWith(GetNeighborPositions(state, cell));
        }

        return candidates;
    }

    /// <summary>
    /// Applies survival to living cells and birth at exactly three neighbors to dead cells.
    /// </summary>
    /// <param name="isAlive">Whether the cell is alive in the current generation.</param>
    /// <param name="livingNeighbors">The number of living neighbors in the current generation.</param>
    /// <returns>Whether the cell will be alive in the next generation.</returns>
    private static bool WillBeAlive(bool isAlive, int livingNeighbors)
        => isAlive ? ShouldSurvive(livingNeighbors) : livingNeighbors == 3;

    /// <summary>
    /// Counts living cells in the eight neighboring positions, excluding the cell itself.
    /// Positions outside the board are treated as dead.
    /// </summary>
    /// <param name="state">The board containing the living cells.</param>
    /// <param name="cell">The coordinate whose neighbors are counted.</param>
    /// <returns>The number of living neighbors within the board.</returns>
    private static int CountLivingNeighbors(BoardState state, (int X, int Y) cell)
        => GetNeighborPositions(state, cell).Count(state.LivingCells.Contains);

    /// <summary>
    /// Enumerates neighboring coordinates within the board, excluding the cell itself.
    /// </summary>
    /// <param name="state">The board defining the coordinate bounds.</param>
    /// <param name="cell">The coordinate whose neighbors are enumerated.</param>
    /// <returns>The neighboring positions without wrapping across board edges.</returns>
    private static IEnumerable<(int X, int Y)> GetNeighborPositions(
        BoardState state, (int X, int Y) cell)
    {
        for (var yOffset = -1; yOffset <= 1; yOffset++)
        {
            for (var xOffset = -1; xOffset <= 1; xOffset++)
            {
                if (xOffset == 0 && yOffset == 0)
                {
                    continue;
                }

                var x = cell.X + xOffset;
                var y = cell.Y + yOffset;
                if (IsWithinBoard(state, x, y))
                {
                    yield return (x, y);
                }
            }
        }
    }

    /// <summary>
    /// Determines whether a coordinate lies within the finite board without wrapping.
    /// </summary>
    /// <param name="state">The board defining the coordinate bounds.</param>
    /// <param name="x">The zero-based column coordinate.</param>
    /// <param name="y">The zero-based row coordinate.</param>
    /// <returns>Whether the coordinate is inside the board.</returns>
    private static bool IsWithinBoard(BoardState state, int x, int y)
        => x >= 0 && x < state.Width && y >= 0 && y < state.Height;

    /// <summary>
    /// Determines whether an already living cell survives with its current neighbor count.
    /// </summary>
    /// <param name="livingNeighbors">The number of living neighbors.</param>
    /// <returns>True for two or three living neighbors; otherwise, false.</returns>
    private static bool ShouldSurvive(int livingNeighbors)
        => livingNeighbors == 2 || livingNeighbors == 3;
}
