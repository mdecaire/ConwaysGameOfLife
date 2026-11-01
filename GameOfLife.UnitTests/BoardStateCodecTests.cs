using System.Text.Json;
using GameOfLife.Api.Domain;
using GameOfLife.Api.Infrastructure;
using Xunit;

namespace GameOfLife.UnitTests;

public sealed class BoardStateCodecTests
{
    [Fact]
    public void EmptyBoardRoundTrips()
    {
        var codec = new BoardStateCodec();
        var board = new BoardState(5, 3, []);

        var restored = codec.Decode(board.Width, board.Height, codec.Encode(board));

        Assert.Equal(5, restored.Width);
        Assert.Equal(3, restored.Height);
        Assert.Empty(restored.LivingCells);
    }

    [Fact]
    public void PopulatedBoardRoundTripsWithoutLosingCoordinates()
    {
        var codec = new BoardStateCodec();
        var cells = new[] { (4, 0), (1, 2), (0, 1) };
        var board = new BoardState(5, 3, new HashSet<(int X, int Y)>(cells));

        var restored = codec.Decode(board.Width, board.Height, codec.Encode(board));

        Assert.Equal(5, restored.Width);
        Assert.Equal(3, restored.Height);
        Assert.True(restored.LivingCells.SetEquals(cells));
    }

    [Theory]
    [InlineData("[")]
    [InlineData("null")]
    [InlineData("[{}]")]
    [InlineData("[{\"X\":1}]")]
    public void InvalidStoredJsonThrowsJsonException(string json)
    {
        var codec = new BoardStateCodec();

        Assert.Throws<JsonException>(() => codec.Decode(5, 3, json));
    }
}
