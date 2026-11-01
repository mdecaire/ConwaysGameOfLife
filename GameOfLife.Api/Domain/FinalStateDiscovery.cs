namespace GameOfLife.Api.Domain;

public sealed class FinalStateDiscovery(IGenerationEngine engine)
{
    internal const int DefaultIterationBudget = 10_000;

    /// <summary>
    /// Searches without mutating the input, using a provisional limit of 10,000 engine calls.
    /// Repeats require exact board equality; exhaustion returns the last calculated state.
    /// Cancellation throws rather than returning a partial conclusion.
    /// </summary>
    public FinalStateResult Discover(BoardState state, long startingGeneration,
        CancellationToken cancellationToken = default)
        => Discover(state, startingGeneration, DefaultIterationBudget, cancellationToken);

    internal FinalStateResult Discover(BoardState state, long startingGeneration,
        int iterationBudget, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(startingGeneration);
        ArgumentOutOfRangeException.ThrowIfNegative(iterationBudget);
        var observed = new Dictionary<(int Width, int Height, int Count, ulong Cells),
            List<(BoardState State, int Iteration)>> { [Fingerprint(state, cancellationToken)] = [(state, 0)] };
        var iterations = 0;
        while (iterations < iterationBudget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (startingGeneration == long.MaxValue - iterations)
                throw new ArgumentOutOfRangeException(nameof(startingGeneration),
                    "The next generation exceeds the supported integer range.");
            state = engine.CalculateNextGeneration(state, cancellationToken);
            iterations++;
            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = Fingerprint(state, cancellationToken);
            if (observed.TryGetValue(fingerprint, out var bucket))
            {
                foreach (var previous in bucket)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (state.Width != previous.State.Width || state.Height != previous.State.Height
                        || !state.LivingCells.SetEquals(previous.State.LivingCells))
                        continue;

                    var period = iterations - previous.Iteration;
                    cancellationToken.ThrowIfCancellationRequested();
                    return new(period == 1 ? FinalStateOutcome.Stable : FinalStateOutcome.Cycle,
                        state, startingGeneration, startingGeneration + iterations, period,
                        startingGeneration + previous.Iteration, iterations);
                }
            }
            else
            {
                bucket = [];
                observed.Add(fingerprint, bucket);
            }
            bucket.Add((state, iterations));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(FinalStateOutcome.Inconclusive, state, startingGeneration,
            startingGeneration + iterations, null, null, iterations);
    }

    // Order-independent lookup hint only. Exact set equality establishes a repeat.
    internal static (int Width, int Height, int Count, ulong Cells) Fingerprint(
        BoardState state, CancellationToken cancellationToken = default)
    {
        ulong cells = 0;
        foreach (var (x, y) in state.LivingCells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cells ^= ((ulong)(uint)x << 32) | (uint)y;
        }
        return (state.Width, state.Height, state.LivingCells.Count, cells);
    }
}
