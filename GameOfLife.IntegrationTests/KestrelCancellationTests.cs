using GameOfLife.Api.Domain;
using Xunit;

namespace GameOfLife.IntegrationTests;

public sealed class KestrelCancellationTests
{
    [Theory]
    [InlineData("states-ahead?stepsAhead=1000000")]
    [InlineData("generations/1000000")]
    [InlineData("final-state")]
    public async Task DisconnectReachesRunningCalculationAndLeavesPersistenceUnchanged(string route)
    {
        var engine = new PausedEngine();
        using var fixture = new KestrelFixture(engine: engine);
        using var client = fixture.OpenClient();
        var id = Guid.NewGuid();
        // Exercise real engine work before the synchronization point. No timing
        // assumption determines whether the server has started calculating.
        var cells = (from y in Enumerable.Range(0, 128)
                     from x in Enumerable.Range(0, 128)
                     select (x, y)).ToHashSet();
        fixture.Store.Insert(id, new BoardState(128, 128, cells));
        using var cancellation = new CancellationTokenSource();
        var request = client.GetAsync($"/api/boards/{id}/{route}", cancellation.Token);
        try
        {
            await engine.Calculated.Task.WaitAsync(TimeSpan.FromSeconds(20));
            cancellation.Cancel();
            await engine.ObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Wait for the actual server pipeline to unwind before checking SQLite.
            await fixture.RequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(1, engine.Calls);
            var saved = fixture.Store.Find(id)!.Value;
            Assert.Equal(0, saved.Generation);
            Assert.Equal(128, saved.State.Width);
            Assert.Equal(128, saved.State.Height);
            Assert.True(cells.SetEquals(saved.State.LivingCells));
            Assert.True(cells.SetEquals(fixture.Store.FindInitial(id)!.LivingCells));
            Assert.Null(fixture.Store.FindFinalState(id));
            Assert.Equal(0, fixture.Count("FinalStateResults"));
        }
        finally
        {
            cancellation.Cancel();
            try { using var response = await request; }
            catch (OperationCanceledException) { }
        }
    }

    // Narrow synchronization at the existing engine seam: do one real calculation,
    // then keep that call in flight until RequestAborted reaches the server token.
    // Existing engine unit tests separately cancel within candidate/evaluation work.
    private sealed class PausedEngine : IGenerationEngine
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public TaskCompletionSource Calculated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ObservedCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BoardState CalculateNextGeneration(BoardState state, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            var next = new GenerationEngine().CalculateNextGeneration(state, cancellationToken);
            Calculated.TrySetResult();
            if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("Server calculation did not receive request cancellation.");
            try { cancellationToken.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException)
            {
                ObservedCancellation.TrySetResult();
                throw;
            }
            return next;
        }
    }
}
