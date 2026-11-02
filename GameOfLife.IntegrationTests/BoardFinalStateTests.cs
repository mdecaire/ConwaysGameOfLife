using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameOfLife.Api.Application;
using GameOfLife.Api.Domain;
using GameOfLife.Api.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
using static GameOfLife.IntegrationTests.BoardFixture;

namespace GameOfLife.IntegrationTests;

public sealed class BoardFinalStateTests
{
    private static readonly (int X, int Y)[] Block = [(2, 1), (3, 1), (2, 2), (3, 2)];
    private static readonly (int X, int Y)[] HorizontalBlinker = [(2, 2), (3, 2), (4, 2)];
    private static readonly (int X, int Y)[] VerticalBlinker = [(3, 1), (3, 2), (3, 3)];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentFirstDiscoveriesReturnOneDurableWinner(bool advanceBetweenSnapshots)
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], HorizontalBlinker, 5);
        var before = fixture.StoredValues();
        var client = fixture.Client;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        fixture.Engine.AfterCalculation = _ =>
        {
            var call = Interlocked.Increment(ref calls);
            // Both discoveries stop on their first calculation, after lookup and snapshot read.
            // In the second case call 2 belongs to Next, which must commit without waiting.
            if (call == 1) firstEntered.TrySetResult();
            else if (call == (advanceBetweenSnapshots ? 3 : 2)) secondEntered.TrySetResult();
            else return;
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)), "Discovery must be released before timeout.");
        };
        var requests = new List<Task<HttpResponseMessage>>();
        try
        {
            requests.Add(Task.Run(() => client.GetAsync($"/api/boards/{fixture.Id}/final-state")));
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (advanceBetweenSnapshots)
            {
                using var next = await client.PostAsync($"/api/boards/{fixture.Id}/next", null)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                await AssertBoard(next, fixture.Id, 6, VerticalBlinker);
            }
            var committed = fixture.StoredValues();
            requests.Add(Task.Run(() => client.GetAsync($"/api/boards/{fixture.Id}/final-state")));
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            AssertEmpty(fixture);
            Assert.All(requests, request => Assert.False(request.IsCompleted));
            Assert.Equal(advanceBetweenSnapshots ? 3 : 2, fixture.Engine.Calls);

            release.Set();
            var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(20));
            var original = await responses[0].Content.ReadAsStringAsync();
            Assert.Equal(original, await responses[1].Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(original);
            var start = json.RootElement.GetProperty("startingGeneration").GetInt64();
            Assert.Contains(start, advanceBetweenSnapshots ? new long[] { 5, 6 } : new long[] { 5 });
            var cells = start == 5 ? HorizontalBlinker : VerticalBlinker;
            foreach (var response in responses)
                await AssertBoard(response, fixture.Id, start + 2, cells);
            Assert.Equal("Cycle", json.RootElement.GetProperty("conclusionKind").GetString());
            Assert.Equal(2, json.RootElement.GetProperty("cycleLength").GetInt32());
            Assert.Equal(2, json.RootElement.GetProperty("iterationsUsed").GetInt32());
            AssertSaved(fixture, start, start + 2, cells, 2, 2);
            Assert.Equal(committed, fixture.StoredValues());
            Assert.Equal(before.Initial, committed.Initial);
            if (!advanceBetweenSnapshots) Assert.Equal(before, committed);
            var expectedCalls = advanceBetweenSnapshots ? 5 : 4;
            Assert.Equal(expectedCalls, fixture.Engine.Calls);

            fixture.Reopen();
            AssertSaved(fixture, start, start + 2, cells, 2, 2);
            using var reused = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/final-state")
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
            Assert.Equal(original, await reused.Content.ReadAsStringAsync());
            Assert.Equal(expectedCalls, fixture.Engine.Calls);
            Assert.Equal(committed, fixture.StoredValues());
        }
        finally
        {
            release.Set();
            foreach (var request in requests)
                (await request.WaitAsync(TimeSpan.FromSeconds(20))).Dispose();
        }
    }

    [Fact]
    public async Task InFlightDiscoveryKeepsOriginalSnapshotWhileNextCommits()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], HorizontalBlinker, 5);
        var before = fixture.StoredValues();
        var client = fixture.Client;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        fixture.Engine.AfterCalculation = _ =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)), "Discovery must be released before timeout.");
        };
        var request = Task.Run(() => client.GetAsync($"/api/boards/{fixture.Id}/final-state"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var next = await client.PostAsync($"/api/boards/{fixture.Id}/next", null)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await AssertBoard(next, fixture.Id, 6, VerticalBlinker);
            var committed = fixture.StoredValues();
            Assert.Equal(6, committed.Generation);
            Assert.Equal(before.Initial, committed.Initial);
            Assert.True(new BoardStateCodec().Decode(7, 6, committed.Current).LivingCells.SetEquals(VerticalBlinker));
            Assert.False(request.IsCompleted);
            AssertEmpty(fixture);

            release.Set();
            var response = await request.WaitAsync(TimeSpan.FromSeconds(20));
            var original = await response.Content.ReadAsStringAsync();
            await AssertBoard(response, fixture.Id, 7, HorizontalBlinker);
            using var json = JsonDocument.Parse(original);
            Assert.Equal(5, json.RootElement.GetProperty("startingGeneration").GetInt64());
            Assert.Equal("Cycle", json.RootElement.GetProperty("conclusionKind").GetString());
            Assert.Equal(2, json.RootElement.GetProperty("cycleLength").GetInt32());
            Assert.Equal(2, json.RootElement.GetProperty("iterationsUsed").GetInt32());
            AssertSaved(fixture, 5, 7, HorizontalBlinker, 2, 2);
            Assert.Equal(committed, fixture.StoredValues());
            Assert.Equal(3, fixture.Engine.Calls); // Two discovery calls plus the overlapping Next.
        }
        finally
        {
            release.Set();
            (await request.WaitAsync(TimeSpan.FromSeconds(20))).Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedStableOrCycleReplaysExactlyAfterSecondRequestRestartAndNext(bool oscillator)
    {
        using var fixture = new BoardFixture();
        (int X, int Y)[] cells = oscillator ? [(2, 2), (3, 2), (4, 2)] : Block;
        var period = oscillator ? 2 : 1;
        fixture.Seed(Phases[0], cells, 5);
        var before = fixture.StoredValues();
        using var first = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/final-state");
        await AssertBoard(first, fixture.Id, 5 + period, cells);
        var original = await first.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(original);
        Assert.Equal(5, json.RootElement.GetProperty("startingGeneration").GetInt64());
        Assert.Equal(oscillator ? "Cycle" : "Stable", json.RootElement.GetProperty("conclusionKind").GetString());
        Assert.Equal(period, json.RootElement.GetProperty("cycleLength").GetInt32());
        Assert.Equal(period, json.RootElement.GetProperty("iterationsUsed").GetInt32());
        Assert.Equal(period, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());

        await AssertReplay(period);
        fixture.Reopen();
        await AssertReplay(period);
        for (var i = 0; i < 3; i++)
        {
            using var next = await fixture.Client.PostAsync($"/api/boards/{fixture.Id}/next", null);
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        }
        Assert.Equal(8, fixture.StoredValues().Generation);
        await AssertReplay(period + 3);
        fixture.Reopen();
        await AssertReplay(period + 3);
        AssertSaved(fixture, 5, 5 + period, cells, period, period);

        async Task AssertReplay(int expectedCalls)
        {
            using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/final-state");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(original, await response.Content.ReadAsStringAsync());
            Assert.Equal(expectedCalls, fixture.Engine.Calls);
        }
    }

    [Fact]
    public async Task ExhaustionUsesExactInternalBudgetAndSavesNothingOrAdvancesNothing()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], 5);
        // The glider stays clear of all boundaries for more than 10,000 generations.
        Execute(fixture, "UPDATE Boards SET Width=3000, Height=3000");
        var before = fixture.StoredValues();
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/final-state");
        await AssertProblem(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(10_000, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
        AssertEmpty(fixture);
    }

    [Fact]
    public async Task MissingBoardReturns404WithoutEngineWork()
    {
        using var fixture = new BoardFixture();
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/final-state");
        await AssertProblem(response, HttpStatusCode.NotFound);
        Assert.Equal(0, fixture.Engine.Calls);
        AssertEmpty(fixture);
    }

    [Theory]
    [InlineData("")]
    [InlineData("=1")]
    [InlineData("=invalid")]
    public async Task SuppliedMaxIterationsIsRejectedBeforeWork(string value)
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Block, Block, 0);
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/final-state?maxIterations{value}");
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, fixture.Engine.Calls);
        AssertEmpty(fixture);
    }

    [Fact]
    public async Task OtherOperationsDoNotDiscoverOrPopulateResults()
    {
        using var fixture = new BoardFixture();
        using var upload = await fixture.Client.PostAsJsonAsync("/api/boards", new { cells = new[] { new[] { true } } });
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var board = (await upload.Content.ReadFromJsonAsync<BoardResponse>())!;
        Assert.Equal(0, fixture.Engine.Calls);
        AssertEmpty(fixture);
        using var next = await fixture.Client.PostAsync($"/api/boards/{board.BoardId}/next", null);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(1, fixture.Engine.Calls);
        AssertEmpty(fixture);
        using var preview = await fixture.Client.GetAsync($"/api/boards/{board.BoardId}/states-ahead?stepsAhead=1");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(2, fixture.Engine.Calls);
        AssertEmpty(fixture);
        using var historical = await fixture.Client.GetAsync($"/api/boards/{board.BoardId}/generations/1");
        Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        Assert.Equal(3, fixture.Engine.Calls);
        AssertEmpty(fixture);
    }

    [Fact]
    public async Task FailureAfterInsertRollsBackAndReturnsSanitizedError()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Block, Block, 0);
        var before = fixture.StoredValues();
        Execute(fixture, """
            CREATE TRIGGER RejectFinal AFTER INSERT ON FinalStateResults
            BEGIN SELECT RAISE(FAIL, 'final-write-secret'); END;
            """);
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/final-state");
        await AssertProblem(response, HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("final-write-secret", body);
        Assert.DoesNotContain("Sqlite", body);
        Assert.DoesNotContain("INSERT", body);
        Assert.Equal(1, fixture.Engine.Calls);
        fixture.Reopen();
        AssertEmpty(fixture);
        Assert.Equal(before, fixture.StoredValues());
        Execute(fixture, "DROP TRIGGER RejectFinal");
        using var retry = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/final-state");
        await AssertBoard(retry, fixture.Id, 1, Block);
        AssertSaved(fixture, 0, 1, Block, 1, 1);
    }

    [Fact]
    public void CancellationAtEntryAndAfterCalculationSavesNothing()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Block, Block, 0);
        var before = fixture.StoredValues();
        var service = new BoardService(new SqliteBoardStore(fixture.ConnectionString, new BoardStateCodec()), fixture.Engine);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.FinalState(fixture.Id, cancellation.Token));
        Assert.Equal(0, fixture.Engine.Calls);
        using var duringCalculation = new CancellationTokenSource();
        fixture.Engine.AfterCalculation = token =>
        {
            Assert.Equal(duringCalculation.Token, token);
            duringCalculation.Cancel();
        };
        Assert.Throws<OperationCanceledException>(() => service.FinalState(fixture.Id, duringCalculation.Token));
        Assert.Equal(1, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
        AssertEmpty(fixture);
    }

    [Fact]
    public void CancellationAfterInsertBeforeCommitRollsBack()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Block, Block, 0);
        var before = fixture.StoredValues();
        using var cancellation = new CancellationTokenSource();
        var observed = false;
        var store = new SqliteBoardStore(fixture.ConnectionString, new BoardStateCodec(), value =>
        {
            var connection = new SqliteConnection(value);
            connection.CreateFunction("cancel_final", () => { observed = true; cancellation.Cancel(); return 0; });
            return connection;
        });
        Execute(fixture, "CREATE TRIGGER CancelFinal AFTER INSERT ON FinalStateResults BEGIN SELECT cancel_final(); END;");
        Assert.Throws<OperationCanceledException>(() => new BoardService(store, fixture.Engine)
            .FinalState(fixture.Id, cancellation.Token));
        Assert.True(observed);
        AssertEmpty(fixture);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Fact]
    public void SameBoardInsertReturnsOriginalWinnerWithoutOverwritingMetadata()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Block, Block, 0);
        var store = new SqliteBoardStore(fixture.ConnectionString, new BoardStateCodec());
        var first = new FinalStateDiscovery(fixture.Engine).Discover(new BoardState(7, 6, Block.ToHashSet()), 0);
        store.SaveFinalState(fixture.Id, first);
        var winner = store.SaveFinalState(fixture.Id, first with { StartingGeneration = 8, Generation = 9, IterationsUsed = 2 });
        Assert.Equal(0, winner.StartingGeneration);
        Assert.Equal(1, winner.Generation);
        Assert.Equal(1, winner.IterationsUsed);
        Assert.True(winner.State.LivingCells.SetEquals(Block));
        AssertSaved(fixture, 0, 1, Block, 1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedUniqueAndDeferredCommitFailuresAreNotTreatedAsSavedWinners(bool deferred)
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Block, Block, 0);
        var before = fixture.StoredValues();
        Execute(fixture, deferred ? """
            CREATE TABLE FinalGuard (Id TEXT REFERENCES Boards(Id) DEFERRABLE INITIALLY DEFERRED);
            CREATE TRIGGER BreakFinal AFTER INSERT ON FinalStateResults
            BEGIN INSERT INTO FinalGuard VALUES ('missing-board'); END;
            """ : """
            CREATE TABLE FinalGuard (Id TEXT UNIQUE);
            INSERT INTO FinalGuard VALUES ('duplicate');
            CREATE TRIGGER BreakFinal AFTER INSERT ON FinalStateResults
            BEGIN INSERT INTO FinalGuard VALUES ('duplicate'); END;
            """);
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/final-state");
        await AssertProblem(response, HttpStatusCode.InternalServerError);
        Assert.DoesNotContain("constraint", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Engine.Calls);
        fixture.Reopen();
        AssertEmpty(fixture);
        Assert.Equal(before, fixture.StoredValues());
    }

    private static void Execute(BoardFixture fixture, string sql)
    {
        using var connection = new SqliteConnection(fixture.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AssertEmpty(BoardFixture fixture)
    {
        using var connection = new SqliteConnection(fixture.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM FinalStateResults";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    private static void AssertSaved(BoardFixture fixture, long start, long generation,
        (int X, int Y)[] cells, int period, int iterations)
    {
        using var connection = new SqliteConnection(fixture.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT BoardId, StartingGeneration, ResultGeneration, ResultState, CycleLength, IterationsUsed FROM FinalStateResults";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(fixture.Id.ToString(), reader.GetString(0));
        Assert.Equal(start, reader.GetInt64(1));
        Assert.Equal(generation, reader.GetInt64(2));
        Assert.True(new BoardStateCodec().Decode(7, 6, reader.GetString(3)).LivingCells.SetEquals(cells));
        Assert.Equal(period, reader.GetInt32(4));
        Assert.Equal(iterations, reader.GetInt32(5));
        Assert.False(reader.Read());
    }
}
