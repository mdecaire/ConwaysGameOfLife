using GameOfLife.Api.Domain;
using Xunit;

namespace GameOfLife.UnitTests;

public sealed class GenerationEngineTests
{
    /// <summary>
    /// Verifies that a dead center with four living neighbors stays dead, leaving the diamond unchanged.
    /// </summary>
    [Fact]
    public void CalculateNextGeneration_DeadCellWithFourNeighborsStaysDead()
    {
        var originalCells = new[] { (1, 0), (0, 1), (2, 1), (1, 2) };
        var input = new BoardState(3, 3, new HashSet<(int X, int Y)>(originalCells));
        var engine = new GenerationEngine();

        var next = engine.CalculateNextGeneration(input);

        Assert.Equal(3, next.Width);
        Assert.Equal(3, next.Height);
        Assert.True(next.LivingCells.SetEquals(originalCells));
        Assert.True(input.LivingCells.SetEquals(originalCells));
        Assert.NotSame(input.LivingCells, next.LivingCells);
    }

    /// <summary>
    /// Verifies that cells with five or eight neighbors die on a full board, leaving its four corners.
    /// </summary>
    [Fact]
    public void CalculateNextGeneration_FullThreeByThreeBoardRetainsOnlyCorners()
    {
        var originalCells = new[]
        {
            (0, 0), (1, 0), (2, 0),
            (0, 1), (1, 1), (2, 1),
            (0, 2), (1, 2), (2, 2)
        };
        var input = new BoardState(3, 3, new HashSet<(int X, int Y)>(originalCells));
        var engine = new GenerationEngine();

        var next = engine.CalculateNextGeneration(input);

        Assert.Equal(3, next.Width);
        Assert.Equal(3, next.Height);
        Assert.True(next.LivingCells.SetEquals(new[] { (0, 0), (2, 0), (0, 2), (2, 2) }));
        Assert.True(input.LivingCells.SetEquals(originalCells));
        Assert.NotSame(input.LivingCells, next.LivingCells);
    }

    /// <summary>
    /// Verifies that a full single-column board retains only its middle cell without out-of-bounds births.
    /// </summary>
    [Fact]
    public void CalculateNextGeneration_SingleColumnRetainsOnlyMiddleCell()
    {
        var originalCells = new[] { (0, 0), (0, 1), (0, 2) };
        var input = new BoardState(1, 3, new HashSet<(int X, int Y)>(originalCells));
        var engine = new GenerationEngine();

        var next = engine.CalculateNextGeneration(input);

        Assert.Equal(1, next.Width);
        Assert.Equal(3, next.Height);
        Assert.Equal((0, 1), Assert.Single(next.LivingCells));
        Assert.True(input.LivingCells.SetEquals(originalCells));
        Assert.NotSame(input.LivingCells, next.LivingCells);
    }

    /// <summary>
    /// Verifies that candidate collection observes cancellation before another neighbor or source cell.
    /// </summary>
    /// <param name="candidatesBeforeCancellation">
    /// One to cancel before the first neighbor; nine to cancel before the second living cell.
    /// </param>
    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void EnumerateCandidateCells_CancellationDuringCollectionThrows(int candidatesBeforeCancellation)
    {
        var originalCells = new[] { (1, 1), (4, 4) };
        var input = new BoardState(6, 6, new HashSet<(int X, int Y)>(originalCells));
        using var cancellation = new CancellationTokenSource();
        using var candidates = GenerationEngine.EnumerateCandidateCells(input, cancellation.Token).GetEnumerator();
        for (var index = 0; index < candidatesBeforeCancellation; index++)
        {
            Assert.True(candidates.MoveNext());
        }
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() => candidates.MoveNext());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(6, input.Width);
        Assert.Equal(6, input.Height);
        Assert.True(input.LivingCells.SetEquals(originalCells));
    }

    /// <summary>
    /// Verifies that a one-cell board becomes or remains empty without counting itself as a neighbor.
    /// </summary>
    /// <param name="isAlive">Whether the board's only cell starts alive.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CalculateNextGeneration_OneCellBoardBecomesOrRemainsEmpty(bool isAlive)
    {
        var originalCells = isAlive ? new[] { (0, 0) } : Array.Empty<(int X, int Y)>();
        var input = new BoardState(1, 1, new HashSet<(int X, int Y)>(originalCells));
        var engine = new GenerationEngine();

        var next = engine.CalculateNextGeneration(input);

        Assert.Equal(1, next.Width);
        Assert.Equal(1, next.Height);
        Assert.Empty(next.LivingCells);
        Assert.True(input.LivingCells.SetEquals(originalCells));
        Assert.NotSame(input.LivingCells, next.LivingCells);
    }

    /// <summary>
    /// Verifies that cancellation during a living-cell lookup stops further cell evaluation.
    /// </summary>
    [Fact]
    public void CalculateNextGeneration_CancellationDuringEvaluationThrows()
    {
        using var cancellation = new CancellationTokenSource();
        var comparer = new CancelOnLookupComparer(cancellation);
        var input = new BoardState(3, 3, new HashSet<(int X, int Y)>(comparer) { (1, 1) });
        var engine = new GenerationEngine();
        comparer.CancelOnNextLookup = true;

        var exception = Assert.Throws<OperationCanceledException>(
            () => engine.CalculateNextGeneration(input, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        // At most the current cell's remaining eight lookups may finish after cancellation.
        Assert.InRange(comparer.LookupsAfterCancellation, 0, 8);
        Assert.Equal(3, input.Width);
        Assert.Equal(3, input.Height);
        Assert.Equal((1, 1), Assert.Single(input.LivingCells));
    }

    /// <summary>
    /// Verifies that cancellation requested before calculation throws without changing the input.
    /// </summary>
    /// <param name="hasLivingCell">Whether the board contains a living cell.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CalculateNextGeneration_PreCanceledTokenThrows(bool hasLivingCell)
    {
        var originalCells = hasLivingCell ? new[] { (1, 0) } : Array.Empty<(int X, int Y)>();
        var input = new BoardState(3, 2, new HashSet<(int X, int Y)>(originalCells));
        var engine = new GenerationEngine();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(
            () => engine.CalculateNextGeneration(input, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(3, input.Width);
        Assert.Equal(2, input.Height);
        Assert.True(input.LivingCells.SetEquals(originalCells));
    }

    /// <summary>
    /// Verifies that cells across opposite edges do not form a wrapping block.
    /// Each finite-board fixture has no survivors or births.
    /// </summary>
    /// <param name="acrossLeftAndRightEdges">
    /// True to check left/right wrapping; false to check top/bottom wrapping.
    /// </param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CalculateNextGeneration_OppositeEdgesDoNotWrap(bool acrossLeftAndRightEdges)
    {
        var originalCells = acrossLeftAndRightEdges
            ? new[] { (0, 1), (0, 2), (4, 1) }
            : new[] { (1, 0), (2, 0), (1, 3) };
        var input = new BoardState(5, 4, new HashSet<(int X, int Y)>(originalCells));
        var engine = new GenerationEngine();

        var next = engine.CalculateNextGeneration(input);

        Assert.Equal(5, next.Width);
        Assert.Equal(4, next.Height);
        Assert.Empty(next.LivingCells);
        Assert.True(input.LivingCells.SetEquals(originalCells));
    }

    /// <summary>
    /// Verifies that the overcrowded center of a cross dies, leaving an eight-cell ring.
    /// </summary>
    [Fact]
    public void CalculateNextGeneration_LiveCellWithFourNeighborsDies()
    {
        var originalCells = new[] { (1, 0), (0, 1), (1, 1), (2, 1), (1, 2) };
        var input = new BoardState(3, 3, new HashSet<(int X, int Y)>(originalCells));
        var engine = new GenerationEngine();

        var next = engine.CalculateNextGeneration(input);

        var expectedCells = new[]
        {
            (0, 0), (1, 0), (2, 0),
            (0, 1),         (2, 1),
            (0, 2), (1, 2), (2, 2)
        };
        Assert.Equal(3, next.Width);
        Assert.Equal(3, next.Height);
        Assert.True(next.LivingCells.SetEquals(expectedCells));
        Assert.True(input.LivingCells.SetEquals(originalCells));
    }

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
    /// <summary>
    /// Preserves coordinate equality while triggering cancellation on an armed hash lookup.
    /// </summary>
    /// <param name="cancellation">The source to cancel during evaluation.</param>
    private sealed class CancelOnLookupComparer(CancellationTokenSource cancellation)
        : IEqualityComparer<(int X, int Y)>
    {
        /// <summary>Gets or sets whether the next hash lookup requests cancellation.</summary>
        public bool CancelOnNextLookup { get; set; }

        /// <summary>Gets the number of hash lookups performed after cancellation.</summary>
        public int LookupsAfterCancellation { get; private set; }

        /// <inheritdoc />
        public bool Equals((int X, int Y) left, (int X, int Y) right) => left == right;

        /// <summary>
        /// Returns the normal coordinate hash and requests cancellation when armed.
        /// </summary>
        /// <param name="cell">The coordinate being hashed.</param>
        /// <returns>The coordinate's standard hash code.</returns>
        public int GetHashCode((int X, int Y) cell)
        {
            if (cancellation.IsCancellationRequested)
            {
                LookupsAfterCancellation++;
            }
            else if (CancelOnNextLookup)
            {
                CancelOnNextLookup = false;
                cancellation.Cancel();
            }

            return cell.GetHashCode();
        }
    }
}
