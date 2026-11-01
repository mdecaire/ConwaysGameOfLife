using System.Text.Json;
using GameOfLife.Api.Domain;

namespace GameOfLife.Api.Infrastructure;

/// <summary>Serializes living coordinates as JSON; dimensions are persisted separately.</summary>
public sealed class BoardStateCodec
{
    public string Encode(BoardState state)
        => JsonSerializer.Serialize(state.LivingCells.Select(cell => new StoredCell
        {
            X = cell.X,
            Y = cell.Y
        }));

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
