using GameOfLife.Api.Domain;
using Xunit;

namespace GameOfLife.UnitTests;

public sealed class FinalStateDiscoveryTests
{
    [Fact]
    public void Discover_NearMaximumGenerationCanConcludeBeforeItsBudgetExpires()
    {
        var engine = new CountingEngine();

        var result = new FinalStateDiscovery(engine).Discover(new BoardState(1, 1, []),
            long.MaxValue - 1, 2);

        Assert.Equal(FinalStateOutcome.Stable, result.Outcome);
        Assert.Equal(long.MaxValue, result.Generation);
        Assert.Equal(long.MaxValue - 1, result.StartingGeneration);
        Assert.Equal(long.MaxValue - 1, result.CycleStartGeneration);
        Assert.Equal(1, result.CycleLength);
        Assert.Equal(1, result.IterationsUsed);
        Assert.Equal(1, engine.Calls);
        AssertState(result.State, 1, 1);
    }

    [Fact]
    public void Discover_OverflowDuringSearchStopsBeforeTheUnrepresentableCall()
    {
        var input = new BoardState(1, 1, [(0, 0)]);
        var engine = new CountingEngine();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FinalStateDiscovery(engine).Discover(input, long.MaxValue - 1, 2));

        Assert.Equal(1, engine.Calls);
        AssertState(input, 1, 1, (0, 0));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(long.MaxValue, 1)]
    public void Discover_InvalidOrUnrepresentableGenerationRejectsBeforeEngineWork(long start, int budget)
    {
        var input = new BoardState(1, 1, []);
        var engine = new CountingEngine();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FinalStateDiscovery(engine).Discover(input, start, budget));

        Assert.Equal(0, engine.Calls);
        AssertState(input, 1, 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Discover_MaximumRepresentableResultGenerationIsAllowed(int budget)
    {
        var input = new BoardState(1, 1, []);
        var engine = new CountingEngine();

        var result = new FinalStateDiscovery(engine).Discover(input, long.MaxValue - budget, budget);

        Assert.Equal(budget == 1 ? FinalStateOutcome.Stable : FinalStateOutcome.Inconclusive, result.Outcome);
        Assert.Equal(long.MaxValue - budget, result.StartingGeneration);
        Assert.Equal(long.MaxValue, result.Generation);
        Assert.Equal(budget == 1 ? (int?)1 : null, result.CycleLength);
        Assert.Equal(budget == 1 ? (long?)(long.MaxValue - 1) : null, result.CycleStartGeneration);
        Assert.Equal(budget, result.IterationsUsed);
        Assert.Equal(budget, engine.Calls);
        AssertState(result.State, 1, 1);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Discover_TransientThenBlinkerReportsCycleStartAfterInitialGeneration(int budget)
    {
        var cells = new[] { (1, 2), (2, 2), (3, 2), (7, 7) };
        var input = new BoardState(9, 9, cells.ToHashSet());
        var engine = new CountingEngine();

        var result = new FinalStateDiscovery(engine).Discover(input, 50, budget);

        Assert.Equal(budget == 3 ? FinalStateOutcome.Cycle : FinalStateOutcome.Inconclusive, result.Outcome);
        Assert.Equal(50, result.StartingGeneration);
        Assert.Equal(50 + budget, result.Generation);
        Assert.Equal(budget == 3 ? (int?)2 : null, result.CycleLength);
        Assert.Equal(budget == 3 ? (long?)51 : null, result.CycleStartGeneration);
        Assert.Equal(budget, result.IterationsUsed);
        Assert.Equal(budget, engine.Calls);
        AssertState(result.State, 9, 9, budget == 3
            ? [(2, 1), (2, 2), (2, 3)] : [(1, 2), (2, 2), (3, 2)]);
        AssertState(input, 9, 9, cells);
    }

    [Fact]
    public void Discover_PulsarRepeatsAfterThreeCallsAtANonzeroGeneration()
    {
        // Standard 13-by-13 pulsar, padded two cells away from each edge.
        string[] rows =
        [
            "..OOO...OOO..",
            ".............",
            "O....O.O....O",
            "O....O.O....O",
            "O....O.O....O",
            "..OOO...OOO..",
            ".............",
            "..OOO...OOO..",
            "O....O.O....O",
            "O....O.O....O",
            "O....O.O....O",
            ".............",
            "..OOO...OOO.."
        ];
        var cells = rows.SelectMany((row, y) => row.Select((cell, x) => (cell, x, y)))
            .Where(item => item.cell == 'O').Select(item => (item.x + 2, item.y + 2)).ToArray();
        var input = new BoardState(17, 17, cells.ToHashSet());
        var engine = new CountingEngine();

        var result = new FinalStateDiscovery(engine).Discover(input, 101, 3);

        Assert.Equal(FinalStateOutcome.Cycle, result.Outcome);
        Assert.Equal(101, result.StartingGeneration);
        Assert.Equal(104, result.Generation);
        Assert.Equal(3, result.CycleLength);
        Assert.Equal(101, result.CycleStartGeneration);
        Assert.Equal(3, result.IterationsUsed);
        Assert.Equal(3, engine.Calls);
        AssertState(result.State, 17, 17, cells);
        AssertState(input, 17, 17, cells);
    }

    [Fact]
    public void Discover_DefaultBudgetStopsGliderAfterTenThousandCalls()
    {
        var cells = new[] { (2, 1), (3, 2), (1, 3), (2, 3), (3, 3) };
        var input = new BoardState(3005, 3005, cells.ToHashSet());
        var engine = new CountingEngine();

        var result = new FinalStateDiscovery(engine).Discover(input, 7);

        // A glider moves diagonally one cell every four generations.
        Assert.Equal(FinalStateOutcome.Inconclusive, result.Outcome);
        Assert.Equal(7, result.StartingGeneration);
        Assert.Equal(10007, result.Generation);
        Assert.Null(result.CycleLength);
        Assert.Null(result.CycleStartGeneration);
        Assert.Equal(10000, result.IterationsUsed);
        Assert.Equal(10000, engine.Calls);
        AssertState(result.State, 3005, 3005,
            (2502, 2501), (2503, 2502), (2501, 2503), (2502, 2503), (2503, 2503));
        AssertState(input, 3005, 3005, cells);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Discover_PreCanceledTokenStopsBeforeEngineCalls(int budget)
    {
        var input = new BoardState(3, 2, [(1, 0)]);
        var engine = new CountingEngine();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() =>
            new FinalStateDiscovery(engine).Discover(input, 0, budget, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, engine.Calls);
        AssertState(input, 3, 2, (1, 0));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void Discover_CancellationAfterEngineCallStopsBeforeResultOrFurtherCalls(bool stable, int budget)
    {
        var cells = stable ? Array.Empty<(int, int)>() : [(1, 0)];
        var input = new BoardState(3, 2, cells.ToHashSet());
        using var cancellation = new CancellationTokenSource();
        var engine = new CountingEngine { AfterCall = cancellation.Cancel };

        var exception = Assert.Throws<OperationCanceledException>(() =>
            new FinalStateDiscovery(engine).Discover(input, 0, budget, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, engine.Calls);
        Assert.Equal(cancellation.Token, engine.LastCancellationToken);
        AssertState(input, 3, 2, cells);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void Discover_CollidingClockPhasesRequireExactEquality(bool reverse, int budget)
    {
        // Clock's two phases: each has six cells and the same XOR fingerprint.
        var first = new[] { (1, 2), (2, 2), (2, 4), (3, 1), (3, 3), (4, 3) };
        var second = new[] { (1, 3), (2, 1), (2, 2), (3, 3), (3, 4), (4, 2) };
        var cells = reverse ? second : first;
        var other = reverse ? first : second;
        var input = new BoardState(6, 6, cells.Reverse().ToHashSet());
        Assert.Equal(FinalStateDiscovery.Fingerprint(input),
            FinalStateDiscovery.Fingerprint(new BoardState(6, 6, other.ToHashSet())));
        Assert.False(input.LivingCells.SetEquals(other));
        var engine = new CountingEngine();

        var result = new FinalStateDiscovery(engine).Discover(input, 30, budget);

        Assert.Equal(budget == 2 ? FinalStateOutcome.Cycle : FinalStateOutcome.Inconclusive, result.Outcome);
        Assert.Equal(30, result.StartingGeneration);
        Assert.Equal(30 + budget, result.Generation);
        Assert.Equal(budget == 2 ? (int?)2 : null, result.CycleLength);
        Assert.Equal(budget == 2 ? (long?)30 : null, result.CycleStartGeneration);
        Assert.Equal(budget, result.IterationsUsed);
        Assert.Equal(budget, engine.Calls);
        AssertState(result.State, 6, 6, budget == 2 ? cells : other);
        AssertState(input, 6, 6, cells);
    }

    [Theory]
    [InlineData(1, 41)]
    [InlineData(2, 0)]
    [InlineData(2, 41)]
    [InlineData(3, 41)]
    public void Discover_BlinkerRespectsBudgetAndLabelsItsPhase(int budget, long start)
    {
        var horizontal = new[] { (1, 2), (2, 2), (3, 2) };
        var vertical = new[] { (2, 1), (2, 2), (2, 3) };
        var input = new BoardState(5, 5, horizontal.ToHashSet());
        var engine = new CountingEngine();

        var result = new FinalStateDiscovery(engine).Discover(input, start, budget);

        var detected = budget >= 2;
        Assert.Equal(detected ? FinalStateOutcome.Cycle : FinalStateOutcome.Inconclusive, result.Outcome);
        Assert.Equal(start, result.StartingGeneration);
        Assert.Equal(start + (detected ? 2 : 1), result.Generation);
        Assert.Equal(detected ? (int?)2 : null, result.CycleLength);
        Assert.Equal(detected ? (long?)start : null, result.CycleStartGeneration);
        Assert.Equal(detected ? 2 : 1, result.IterationsUsed);
        Assert.Equal(result.IterationsUsed, engine.Calls);
        AssertState(result.State, 5, 5, detected ? horizontal : vertical);
        AssertState(input, 5, 5, horizontal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Discover_TransientSingleCellRequiresASeparateStabilityCall(int budget)
    {
        var input = new BoardState(3, 2, [(1, 0)]);
        var engine = new CountingEngine();

        var result = new FinalStateDiscovery(engine).Discover(input, 19, budget);

        Assert.Equal(budget == 2 ? FinalStateOutcome.Stable : FinalStateOutcome.Inconclusive, result.Outcome);
        Assert.Equal(19, result.StartingGeneration);
        Assert.Equal(19 + budget, result.Generation);
        Assert.Equal(budget == 2 ? (int?)1 : null, result.CycleLength);
        Assert.Equal(budget == 2 ? (long?)20 : null, result.CycleStartGeneration);
        Assert.Equal(budget, result.IterationsUsed);
        Assert.Equal(budget, engine.Calls);
        AssertState(result.State, 3, 2);
        AssertState(input, 3, 2, (1, 0));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 23)]
    public void Discover_StableStateRequiresOneCall(bool block, long startingGeneration)
    {
        var cells = block ? new[] { (1, 1), (2, 1), (1, 2), (2, 2) } : [];
        var input = new BoardState(5, 4, cells.ToHashSet());
        var engine = new CountingEngine();

        var result = new FinalStateDiscovery(engine).Discover(input, startingGeneration, 1);

        Assert.Equal(FinalStateOutcome.Stable, result.Outcome);
        Assert.Equal(startingGeneration, result.StartingGeneration);
        Assert.Equal(startingGeneration + 1, result.Generation);
        Assert.Equal(1, result.CycleLength);
        Assert.Equal(startingGeneration, result.CycleStartGeneration);
        Assert.Equal(1, result.IterationsUsed);
        Assert.Equal(1, engine.Calls);
        AssertState(result.State, 5, 4, cells);
        AssertState(input, 5, 4, cells);
        Assert.NotSame(input.LivingCells, result.State.LivingCells);
    }

    [Fact]
    public void Discover_ZeroBudgetReturnsInitialStateWithoutEngineCalls()
    {
        var input = new BoardState(5, 4, [(1, 1)]);
        var engine = new CountingEngine();

        var result = new FinalStateDiscovery(engine).Discover(input, 17, 0);

        Assert.Equal(FinalStateOutcome.Inconclusive, result.Outcome);
        Assert.Equal(17, result.StartingGeneration);
        Assert.Equal(17, result.Generation);
        Assert.Null(result.CycleLength);
        Assert.Null(result.CycleStartGeneration);
        Assert.Equal(0, result.IterationsUsed);
        Assert.Equal(0, engine.Calls);
        AssertState(result.State, 5, 4, (1, 1));
        AssertState(input, 5, 4, (1, 1));
    }

    private static void AssertState(BoardState state, int width, int height,
        params (int X, int Y)[] cells)
    {
        Assert.Equal(width, state.Width);
        Assert.Equal(height, state.Height);
        Assert.True(state.LivingCells.SetEquals(cells));
    }

    private sealed class CountingEngine : IGenerationEngine
    {
        public int Calls { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public Action? AfterCall { get; init; }

        public BoardState CalculateNextGeneration(BoardState state,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastCancellationToken = cancellationToken;
            var next = new GenerationEngine().CalculateNextGeneration(state, cancellationToken);
            AfterCall?.Invoke();
            return next;
        }
    }
}
