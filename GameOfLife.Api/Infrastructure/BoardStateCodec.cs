using System.Text.Json;
using GameOfLife.Api.Domain;

namespace GameOfLife.Api.Infrastructure;

/// <summary>Serializes living coordinates as JSON; dimensions are persisted separately.</summary>
public sealed class BoardStateCodec
{
    /// <summary>Serializes living-cell coordinates without including board dimensions.</summary>
    /// <param name="state">The board whose living cells are serialized.</param>
    /// <returns>A JSON array of objects containing X and Y coordinates.</returns>
    public string Encode(BoardState state)
        => JsonSerializer.Serialize(state.LivingCells.Select(cell => new StoredCell
        {
            X = cell.X,
            Y = cell.Y
        }));

    /// <summary>Reconstructs a board from its stored dimensions and coordinate JSON.</summary>
    /// <param name="width">The separately stored number of columns.</param>
    /// <param name="height">The separately stored number of rows.</param>
    /// <param name="json">The JSON array of living-cell coordinates.</param>
    /// <returns>A board with the supplied dimensions and deserialized living cells.</returns>
    /// <exception cref="JsonException">The JSON is invalid, null, or missing required coordinates.</exception>
    public BoardState Decode(int width, int height, string json)
    {
        var cells = JsonSerializer.Deserialize<StoredCell[]>(json)
            ?? throw new JsonException("Stored board coordinates must be a JSON array.");

        return new BoardState(width, height, cells.Select(cell => (cell.X, cell.Y)).ToHashSet());
    }

    private readonly record struct StoredCell
    {
        public required int X { get; init; }
        public required int Y { get; init; }
    }
}
