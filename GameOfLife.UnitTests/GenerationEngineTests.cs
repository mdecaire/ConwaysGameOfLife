using GameOfLife.Api.Domain;
using Xunit;

namespace GameOfLife.UnitTests;

public sealed class GenerationEngineTests
{
    /// <summary>
    /// Verifies that a horizontal blinker produces exactly the two births and one survivor.
    /// </summary>
    [Fact]
    public void CalculateNextGeneration_DeadCellsWithThreeNeighborsBecomeAlive()
    {
        var originalCells = new[] { (1, 1), (2, 1), (3, 1) };
        var input = new BoardState(5, 3, new HashSet<(int X, int Y)>(originalCells));
        var engine = new GenerationEngine();

        var next = engine.CalculateNextGeneration(input);

        Assert.Equal(5, next.Width);
        Assert.Equal(3, next.Height);
        Assert.True(next.LivingCells.SetEquals(new[] { (2, 0), (2, 1), (2, 2) }));
        Assert.True(input.LivingCells.SetEquals(originalCells));
    }

    [Fact]
    public void CalculateNextGeneration_LiveCellsWithThreeNeighborsSurvive()
    {
        var expectedCells = new[] { (1, 1), (2, 1), (1, 2), (2, 2) };
        var input = new BoardState(4, 3, new HashSet<(int X, int Y)>(expectedCells));
        var engine = new GenerationEngine();

        var next = engine.CalculateNextGeneration(input);

        Assert.Equal(4, next.Width);
        Assert.Equal(3, next.Height);
        Assert.True(next.LivingCells.SetEquals(expectedCells));
        Assert.True(input.LivingCells.SetEquals(expectedCells));
    }

    [Fact]
    public void CalculateNextGeneration_LiveCellWithTwoNeighborsSurvives()
    {
        var input = new BoardState(3, 1, new HashSet<(int X, int Y)>
        {
            (0, 0), (1, 0), (2, 0)
        });
        var engine = new GenerationEngine();

        var next = engine.CalculateNextGeneration(input);

        Assert.Equal(3, next.Width);
        Assert.Equal(1, next.Height);
        Assert.Equal((1, 0), Assert.Single(next.LivingCells));
        Assert.True(input.LivingCells.SetEquals(new[] { (0, 0), (1, 0), (2, 0) }));
    }

    [Fact]
    public void CalculateNextGeneration_IsolatedLiveCellDiesWithoutChangingInputOrDimensions()
    {
        var input = new BoardState(3, 2, new HashSet<(int X, int Y)> { (1, 0) });
        var engine = new GenerationEngine();

        var next = engine.CalculateNextGeneration(input);

        Assert.Equal(3, next.Width);
        Assert.Equal(2, next.Height);
        Assert.Empty(next.LivingCells);
        Assert.Equal(3, input.Width);
        Assert.Equal(2, input.Height);
        Assert.Equal((1, 0), Assert.Single(input.LivingCells));
    }
}
