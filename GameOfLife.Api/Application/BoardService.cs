using GameOfLife.Api.Domain;
using GameOfLife.Api.Infrastructure;

namespace GameOfLife.Api.Application;

/// <summary>Coordinates mapping and persistence of uploaded boards.</summary>
/// <param name="store">The SQLite store for uploaded boards.</param>
public sealed class BoardService(SqliteBoardStore store)
{
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
