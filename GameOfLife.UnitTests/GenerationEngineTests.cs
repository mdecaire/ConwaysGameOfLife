using GameOfLife.Api.Domain;
using Xunit;

namespace GameOfLife.UnitTests;

public sealed class GenerationEngineTests
{
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
