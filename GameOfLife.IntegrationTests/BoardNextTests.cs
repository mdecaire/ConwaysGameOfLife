using System.Net;
using GameOfLife.Api.Application;
using GameOfLife.Api.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
using static GameOfLife.IntegrationTests.BoardFixture;

namespace GameOfLife.IntegrationTests;

public sealed class BoardNextTests
{
    [Fact]
    public async Task SequentialNextSurvivesReopenAndKeepsGenerationZeroOriginal()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], 0);
        var initial = fixture.StoredValues().Initial;
        for (var generation = 1; generation <= 2; generation++)
        {
            using var response = await fixture.Client.PostAsync($"/api/boards/{fixture.Id}/next", null);
            await AssertBoard(response, fixture.Id, generation, Phases[generation]);
            Assert.Equal(generation, fixture.Engine.Calls);
            fixture.Reopen();
            using var current = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/states-ahead?stepsAhead=0");
            await AssertBoard(current, fixture.Id, generation, Phases[generation]);
            Assert.Equal(generation, fixture.Engine.Calls);
            Assert.Equal(initial, fixture.StoredValues().Initial);
        }
        using var original = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/generations/0");
        await AssertBoard(original, fixture.Id, 0, Phases[0]);
        Assert.Equal(2, fixture.Engine.Calls);
    }

    [Fact]
    public async Task MissingNextBoardReturnsNotFoundWithoutEngineWork()
    {
        using var fixture = new BoardFixture();
        using var response = await fixture.Client.PostAsync($"/api/boards/{fixture.Id}/next", null);
        await AssertProblem(response, HttpStatusCode.NotFound);
        Assert.Equal(0, fixture.Engine.Calls);
    }

    [Fact]
    public async Task MaximumGenerationIsRejectedBeforeCalculatingOrWriting()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[1], long.MaxValue);
        var before = fixture.StoredValues();
        using var response = await fixture.Client.PostAsync($"/api/boards/{fixture.Id}/next", null);
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Fact]
    public async Task NextCanCommitTheMaximumRepresentableGeneration()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], long.MaxValue - 1);
        using var response = await fixture.Client.PostAsync($"/api/boards/{fixture.Id}/next", null);
        await AssertBoard(response, fixture.Id, long.MaxValue, Phases[1]);
        Assert.Equal(1, fixture.Engine.Calls);
        fixture.Reopen();
        using var current = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/states-ahead?stepsAhead=0");
        await AssertBoard(current, fixture.Id, long.MaxValue, Phases[1]);
    }

    [Fact]
    public async Task InvalidNextBoardIdReturnsBadRequestWithoutEngineWork()
    {
        using var fixture = new BoardFixture();
        using var response = await fixture.Client.PostAsync("/api/boards/invalid/next", null);
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, fixture.Engine.Calls);
    }

    [Fact]
    public void CancellationAfterCalculationStopsBeforePersistence()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], 0);
        var before = fixture.StoredValues();
        using var cancellation = new CancellationTokenSource();
        var service = new BoardService(new SqliteBoardStore(fixture.ConnectionString, new BoardStateCodec()), fixture.Engine);
        fixture.Engine.AfterCalculation = token =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
        };

        Assert.Throws<OperationCanceledException>(() => service.Next(fixture.Id, cancellation.Token));
        Assert.Equal(1, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());

        // An already-canceled request starts no additional calculation or write.
        Assert.Throws<OperationCanceledException>(() => service.Next(fixture.Id, cancellation.Token));
        Assert.Equal(1, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Fact]
    public async Task DeferredConstraintFailureAtCommitReturnsErrorAndRollsBackWithoutRetry()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], 0);
        var before = fixture.StoredValues();
        using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // A test-only self-reference defers the failure until COMMIT, after UPDATE succeeds.
            command.CommandText = """
                ALTER TABLE Boards ADD COLUMN CommitGuard TEXT REFERENCES Boards(Id) DEFERRABLE INITIALLY DEFERRED;
                CREATE TRIGGER BreakNextCommit AFTER UPDATE OF CurrentState, CurrentGeneration ON Boards
                BEGIN
                    UPDATE Boards SET CommitGuard = 'missing-board' WHERE Id = NEW.Id;
                END;
                """;
            command.ExecuteNonQuery();
        }

        using var response = await fixture.Client.PostAsync($"/api/boards/{fixture.Id}/next", null);
        await AssertProblem(response, HttpStatusCode.InternalServerError);
        Assert.DoesNotContain("FOREIGN KEY", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, fixture.Engine.Calls);
        fixture.Reopen();
        Assert.Equal(before, fixture.StoredValues());
        using var current = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/states-ahead?stepsAhead=0");
        await AssertBoard(current, fixture.Id, 0, Phases[0]);
        using var verification = new SqliteConnection(fixture.ConnectionString);
        verification.Open();
        using var check = verification.CreateCommand();
        check.CommandText = "SELECT CommitGuard FROM Boards WHERE Id=$id";
        check.Parameters.AddWithValue("$id", fixture.Id.ToString());
        Assert.Equal(DBNull.Value, check.ExecuteScalar());
    }

    [Fact]
    public async Task RepeatedGenerationConflictsExhaustThreeAttemptsWithoutOverwritingTheWinner()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], 0);
        var winner = fixture.StoredValues();
        fixture.Engine.AfterCalculation = _ =>
        {
            // A real SQLite write between snapshot calculation and the conditional update.
            fixture.SetCurrent(Phases[2], fixture.Engine.Calls + 1);
            winner = fixture.StoredValues();
        };

        using var response = await fixture.Client.PostAsync($"/api/boards/{fixture.Id}/next", null);

        await AssertProblem(response, HttpStatusCode.Conflict);
        Assert.Equal(3, fixture.Engine.Calls);
        Assert.Equal(winner, fixture.StoredValues());
        using var current = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/states-ahead?stepsAhead=0");
        await AssertBoard(current, fixture.Id, 4, Phases[2]);
    }

    [Fact]
    public async Task FailureAfterUpdateRollsBackBothFieldsAndReturnsSanitizedProblem()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], 0);
        var before = fixture.StoredValues();
        using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // FAIL preserves the statement's changes until the surrounding transaction rolls back.
            command.CommandText = """
                CREATE TRIGGER RejectNext AFTER UPDATE ON Boards
                WHEN NEW.CurrentGeneration = OLD.CurrentGeneration + 1
                    AND NEW.CurrentState <> OLD.CurrentState
                BEGIN
                    SELECT RAISE(FAIL, 'next-write-failure-secret');
                END;
                """;
            command.ExecuteNonQuery();
        }

        using var response = await fixture.Client.PostAsync($"/api/boards/{fixture.Id}/next", null);

        await AssertProblem(response, HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("next-write-failure-secret", body);
        Assert.DoesNotContain("RejectNext", body);
        Assert.DoesNotContain("SqliteException", body);
        Assert.DoesNotContain("UPDATE", body);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("boardId", body);
        Assert.Equal(1, fixture.Engine.Calls);
        fixture.Reopen();
        Assert.Equal(before, fixture.StoredValues());
        using var current = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/states-ahead?stepsAhead=0");
        await AssertBoard(current, fixture.Id, 0, Phases[0]);

        // A later independent request succeeds after removing the fault: no lock or failed transaction remains.
        using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER RejectNext";
            command.ExecuteNonQuery();
        }
        using var retry = await fixture.Client.PostAsync($"/api/boards/{fixture.Id}/next", null);
        await AssertBoard(retry, fixture.Id, 1, Phases[1]);
        Assert.Equal(2, fixture.Engine.Calls);
    }
}
