using System.Text.Json;
using System.Net;
using GameOfLife.Api.Application;
using GameOfLife.Api.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
using static GameOfLife.IntegrationTests.BoardFixture;

namespace GameOfLife.IntegrationTests;

public sealed class BoardConcurrencyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WriterLockUsesFiniteProviderWaitingWithoutOptimisticRetries(bool releaseLock)
    {
        using var fixture = new BoardFixture { ProviderTimeoutSeconds = 1 };
        fixture.Seed(Phases[0], Phases[0], 0);
        var before = fixture.StoredValues();
        var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = new System.Collections.Concurrent.ConcurrentBag<BusyObservedConnection>();
        fixture.CreateStore = value => new SqliteBoardStore(value, new BoardStateCodec(),
            cs =>
            {
                Assert.Equal(SqliteCacheMode.Private, new SqliteConnectionStringBuilder(cs).Cache);
                var connection = new BusyObservedConnection(cs, () => busy.TrySetResult());
                connections.Add(connection);
                return connection;
            });
        var client = fixture.Client;
        using var writer = new SqliteConnection(fixture.ConnectionString);
        writer.Open();
        using var held = writer.BeginTransaction(deferred: false);

        var request = Task.Run(() => client.PostAsync($"/api/boards/{fixture.Id}/next", null));
        await busy.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (releaseLock) held.Rollback();
        using var response = await request.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, fixture.Engine.Calls);
        Assert.All(connections, connection => Assert.Equal(System.Data.ConnectionState.Closed, connection.State));
        if (releaseLock)
        {
            await AssertBoard(response, fixture.Id, 1, Phases[1]);
        }
        else
        {
            var failure = Assert.Single(connections, connection => connection.BeginFailure is not null).BeginFailure!;
            Assert.Equal(5, failure.SqliteErrorCode); // SQLITE_BUSY, not an expected-generation conflict.
            Assert.Equal(5, failure.SqliteExtendedErrorCode);
            await AssertProblem(response, HttpStatusCode.InternalServerError);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("locked", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sqlite", body);
            Assert.Equal(before, fixture.StoredValues());
            held.Rollback();
            using var later = await client.PostAsync($"/api/boards/{fixture.Id}/next", null);
            await AssertBoard(later, fixture.Id, 1, Phases[1]);
            Assert.Equal(2, fixture.Engine.Calls);
        }
    }

    [Fact]
    public async Task CancellationDuringProviderWaitRollsBackAndStartsNoNewCalculation()
    {
        using var fixture = new BoardFixture { ProviderTimeoutSeconds = 1 };
        fixture.Seed(Phases[0], Phases[0], 0);
        var before = fixture.StoredValues();
        using var cancellation = new CancellationTokenSource();
        var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new SqliteBoardStore(fixture.ConnectionString, new BoardStateCodec(),
            cs => new BusyObservedConnection(cs, () => busy.TrySetResult()));
        var service = new BoardService(store, fixture.Engine);
        using var writer = new SqliteConnection(fixture.ConnectionString);
        writer.Open();
        using var held = writer.BeginTransaction(deferred: false);
        var request = Task.Run(() => service.Next(fixture.Id, cancellation.Token));
        await busy.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        held.Rollback();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
        fixture.Engine.AfterCalculation = null;
        Assert.Equal(1, service.Next(fixture.Id)!.Generation);
    }

    [Fact]
    public async Task LostAcknowledgementAfterRealCommitNeverAutomaticallyAdvancesAgain()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], 0);
        fixture.CreateStore = value => new SqliteBoardStore(value, new BoardStateCodec(),
            cs => new LostAcknowledgementConnection(cs));

        using var response = await fixture.Client.PostAsync($"/api/boards/{fixture.Id}/next", null);

        await AssertProblem(response, HttpStatusCode.InternalServerError);
        Assert.DoesNotContain("lost-acknowledgement-secret", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, fixture.Engine.Calls);
        fixture.Reopen();
        using var current = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/states-ahead?stepsAhead=0");
        await AssertBoard(current, fixture.Id, 1, Phases[1]);
        Assert.Equal(1, fixture.Engine.Calls);
    }

    // Repeated BEGIN execution on the same connection observes the provider's retry
    // after a real busy result. The trace only signals; it adds no waiting or retries.
    private sealed class BusyObservedConnection(string value, Action onBusy) : SqliteConnection(value)
    {
        private int begins;
        public SqliteException? BeginFailure { get; private set; }
        public override SqliteTransaction BeginTransaction(bool deferred)
        {
            try { return base.BeginTransaction(deferred); }
            catch (SqliteException exception)
            {
                BeginFailure = exception;
                throw;
            }
        }

        public override void Open()
        {
            base.Open();
            SQLitePCL.raw.sqlite3_trace(Handle, (SQLitePCL.strdelegate_trace)((_, sql) =>
            {
                if (sql.StartsWith("BEGIN IMMEDIATE", StringComparison.OrdinalIgnoreCase)
                    && ++begins >= 2) onBusy();
            }), null);
        }
    }

    // A real transaction commits normally, then the storage call loses its acknowledgement
    // during connection disposal. This models ambiguity at the service/store boundary,
    // not every possible disk or native COMMIT failure.
    private sealed class LostAcknowledgementConnection(string value) : SqliteConnection(value)
    {
        private bool wrote;
        public override SqliteTransaction BeginTransaction(bool deferred)
        {
            var transaction = base.BeginTransaction(deferred);
            wrote = true;
            return transaction;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && wrote)
            {
                wrote = false;
                throw new IOException("lost-acknowledgement-secret");
            }
        }
    }

    [Fact]
    public void UnlimitedProviderWaitingIsRejected()
    {
        using var fixture = new BoardFixture();
        var settings = new SqliteConnectionStringBuilder(fixture.ConnectionString) { DefaultTimeout = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteBoardStore(settings.ToString(), new BoardStateCodec()));
    }

    [Fact]
    public void ConflictAttemptOverrideIsBoundedAndCancellationStartsNoRetry()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], 0);
        var store = new SqliteBoardStore(fixture.ConnectionString, new BoardStateCodec());
        var service = new BoardService(store, fixture.Engine, maxNextAttempts: 1);
        fixture.Engine.AfterCalculation = _ => fixture.SetCurrent(Phases[1], 1);
        Assert.Throws<BoardConflictException>(() => service.Next(fixture.Id));
        Assert.Equal(1, fixture.Engine.Calls);
        Assert.Equal(1, fixture.StoredValues().Generation);

        using var cancellation = new CancellationTokenSource();
        fixture.Engine.AfterCalculation = _ =>
        {
            fixture.SetCurrent(Phases[2], 2);
            cancellation.Cancel();
        };
        Assert.Throws<OperationCanceledException>(() => new BoardService(store, fixture.Engine)
            .Next(fixture.Id, cancellation.Token));
        Assert.Equal(2, fixture.Engine.Calls);
        Assert.Equal(2, fixture.StoredValues().Generation);
    }

    [Fact]
    public async Task ConcurrentNextReloadsAndRecalculatesForConsecutiveCommittedStates()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], 5);
        var initial = fixture.StoredValues().Initial;
        var client = fixture.Client;
        using var calculated = new Barrier(2);
        var calls = 0;
        fixture.Engine.AfterCalculation = _ =>
        {
            if (Interlocked.Increment(ref calls) <= 2)
                Assert.True(calculated.SignalAndWait(TimeSpan.FromSeconds(10)), "Both requests must calculate before either writes.");
        };

        var requests = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            client.PostAsync($"/api/boards/{fixture.Id}/next", null))).ToArray();
        var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var generations = new List<long>();
            foreach (var response in responses)
            {
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var generation = json.RootElement.GetProperty("generation").GetInt64();
                generations.Add(generation);
                Assert.InRange(generation, 6, 7);
                await AssertBoard(response, fixture.Id, generation, Phases[(int)generation - 5]);
            }
            Assert.Equal(new long[] { 6, 7 }, generations.Order());
            Assert.Equal(3, fixture.Engine.Calls);
            Assert.Equal(initial, fixture.StoredValues().Initial);
            fixture.Reopen();
            using var current = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/states-ahead?stepsAhead=0");
            await AssertBoard(current, fixture.Id, 7, Phases[2]);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }
}
