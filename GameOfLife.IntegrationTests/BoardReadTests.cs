using System.Net;
using System.Net.Http.Json;
using GameOfLife.Api.Application;
using GameOfLife.Api.Domain;
using GameOfLife.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameOfLife.IntegrationTests;

public sealed class BoardReadTests
{
    // A glider's first three distinct phases, verified by counting neighbors by hand.
    // The board is asymmetric to expose row/column transposition.
    private static readonly (int X, int Y)[][] Phases =
    [
        [(2, 1), (3, 2), (1, 3), (2, 3), (3, 3)],
        [(1, 2), (3, 2), (2, 3), (3, 3), (2, 4)],
        [(3, 2), (1, 3), (3, 3), (2, 4), (3, 4)]
    ];

    [Fact]
    public async Task PreviewZeroReturnsReopenedCurrentSnapshotWithoutEngineWork()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[1], 1);
        var before = fixture.StoredValues();

        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/preview?stepsAhead=0");

        await AssertBoard(response, fixture.Id, 1, Phases[1]);
        Assert.Equal(0, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 2)]
    public async Task PreviewCalculatesOffsetFromCurrentAndDoesNotPersist(int currentGeneration, int steps)
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[currentGeneration], currentGeneration);
        var before = fixture.StoredValues();

        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/preview?stepsAhead={steps}");

        await AssertBoard(response, fixture.Id, 2, Phases[2]);
        Assert.Equal(steps, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Theory]
    [InlineData("?stepsAhead=-1")]
    [InlineData("?stepsAhead=abc")]
    [InlineData("?stepsAhead=1.5")]
    [InlineData("?stepsAhead=9223372036854775808")]
    [InlineData("?stepsAhead=")]
    [InlineData("")]
    public async Task InvalidPreviewOffsetReturnsProblemWithoutCalculationOrWrites(string query)
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[1], 1);
        var before = fixture.StoredValues();
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/preview{query}");
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Fact]
    public async Task PreviewRejectsAdditionOverflowBeforeEngineButAllowsZeroAtMaximum()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[1], long.MaxValue);
        var before = fixture.StoredValues();
        using var zero = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/preview?stepsAhead=0");
        await AssertBoard(zero, fixture.Id, long.MaxValue, Phases[1]);
        using var overflow = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/preview?stepsAhead=1");
        await AssertProblem(overflow, HttpStatusCode.BadRequest);
        Assert.Equal(0, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Fact]
    public async Task AbsoluteZeroReturnsOriginalAfterProgressAndReopenWithoutEngineWork()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[2], 2);
        var before = fixture.StoredValues();
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/generations/0");
        await AssertBoard(response, fixture.Id, 0, Phases[0]);
        Assert.Equal(0, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public async Task AbsolutePositiveAlwaysReconstructsFromOriginal(int currentGeneration, int target)
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[currentGeneration], currentGeneration);
        var before = fixture.StoredValues();
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/generations/{target}");
        await AssertBoard(response, fixture.Id, target, Phases[target]);
        Assert.Equal(target, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    public async Task InvalidAbsoluteGenerationReturnsProblemWithoutCalculationOrWrites(string target)
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[1], 1);
        var before = fixture.StoredValues();
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/generations/{target}");
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Theory]
    [InlineData("preview?stepsAhead=0")]
    [InlineData("preview?stepsAhead=2")]
    [InlineData("generations/0")]
    [InlineData("generations/2")]
    public async Task MissingBoardReturnsNotFoundWithoutEngineWork(string route)
    {
        using var fixture = new BoardFixture();
        using var response = await fixture.Client.GetAsync($"/api/boards/{Guid.NewGuid()}/{route}");
        await AssertProblem(response, HttpStatusCode.NotFound);
        Assert.Equal(0, fixture.Engine.Calls);
    }

    [Theory]
    [InlineData("preview?stepsAhead=0")]
    [InlineData("generations/0")]
    public async Task InvalidBoardIdUsesStandardBinding(string route)
    {
        using var fixture = new BoardFixture();
        using var response = await fixture.Client.GetAsync($"/api/boards/invalid/{route}");
        await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, fixture.Engine.Calls);
    }

    [Fact]
    public async Task UploadLocationResolvesToLiveCurrentStateAfterProgressAndReopen()
    {
        using var fixture = new BoardFixture();
        var cells = Enumerable.Range(0, 6).Select(y => Enumerable.Range(0, 7)
            .Select(x => Phases[0].Contains((x, y))).ToArray()).ToArray();
        Uri location;
        Guid id;
        using (var upload = await fixture.Client.PostAsJsonAsync("/api/boards", new { cells }))
        {
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
            var board = (await upload.Content.ReadFromJsonAsync<BoardResponse>())!;
            id = board.BoardId;
            Assert.NotNull(upload.Headers.Location);
            location = upload.Headers.Location;
            Assert.Equal($"/api/boards/{id}/preview?stepsAhead=0", location.OriginalString);
        }
        using (var original = await fixture.Client.GetAsync(location))
            await AssertBoard(original, id, 0, Phases[0]);
        fixture.SetCurrent(Phases[1], 1, id);
        fixture.Reopen();
        using var current = await fixture.Client.GetAsync(location);
        await AssertBoard(current, id, 1, Phases[1]);
        using var initial = await fixture.Client.GetAsync($"/api/boards/{id}/generations/0");
        await AssertBoard(initial, id, 0, Phases[0]);
        Assert.Equal(0, fixture.Engine.Calls);
    }

    [Fact]
    public async Task PreviewKeepsItsSnapshotWhenStorageChangesDuringCalculation()
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], 0);
        fixture.Engine.AfterCalculation = _ =>
        {
            if (fixture.Engine.Calls == 1) fixture.SetCurrent(Phases[1], 99);
        };
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/preview?stepsAhead=2");
        await AssertBoard(response, fixture.Id, 2, Phases[2]);
        Assert.Equal(2, fixture.Engine.Calls);
        var stored = fixture.StoredValues();
        Assert.Equal(99, stored.Generation);
        var codec = new BoardStateCodec();
        Assert.True(codec.Decode(7, 6, stored.Initial).LivingCells.SetEquals(Phases[0]));
        Assert.True(codec.Decode(7, 6, stored.Current).LivingCells.SetEquals(Phases[1]));
    }

    [Theory]
    [InlineData("preview?stepsAhead=10001")]
    [InlineData("generations/10001")]
    public async Task RetrievalDoesNotEnforceTheOldProvisionalTenThousandCap(string route)
    {
        using var fixture = new BoardFixture();
        fixture.Seed([], [], 0);
        var before = fixture.StoredValues();
        using var response = await fixture.Client.GetAsync($"/api/boards/{fixture.Id}/{route}");
        await AssertBoard(response, fixture.Id, 10001, []);
        Assert.Equal(10001, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationStopsRetrievalBeforeAnotherEngineCallAndLeavesStorageUnchanged(bool absolute)
    {
        using var fixture = new BoardFixture();
        fixture.Seed(Phases[0], Phases[0], 0);
        var before = fixture.StoredValues();
        using var cancellation = new CancellationTokenSource();
        fixture.Engine.AfterCalculation = token =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
        };
        var service = new BoardService(new SqliteBoardStore(fixture.ConnectionString, new BoardStateCodec()), fixture.Engine);
        // The largest representable target is accepted; cancellation stops work after one call.
        Assert.Throws<OperationCanceledException>(() => absolute
            ? service.GetGeneration(fixture.Id, long.MaxValue, cancellation.Token)
            : service.Preview(fixture.Id, long.MaxValue, cancellation.Token));
        Assert.Equal(1, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());

        Assert.Throws<OperationCanceledException>(() => absolute
            ? service.GetGeneration(fixture.Id, 0, cancellation.Token)
            : service.Preview(fixture.Id, 0, cancellation.Token));
        Assert.Equal(1, fixture.Engine.Calls);
        Assert.Equal(before, fixture.StoredValues());
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal((int)status, problem!.Status);
    }

    private static async Task AssertBoard(HttpResponseMessage response, Guid id, long generation,
        IEnumerable<(int X, int Y)> expected)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var board = (await response.Content.ReadFromJsonAsync<BoardResponse>())!;
        Assert.Equal(id, board.BoardId);
        Assert.Equal(generation, board.Generation);
        Assert.Equal(7, board.Width);
        Assert.Equal(6, board.Height);
        Assert.Equal(6, board.Cells.Length);
        var actual = new HashSet<(int X, int Y)>();
        for (var y = 0; y < board.Cells.Length; y++)
        {
            Assert.Equal(7, board.Cells[y].Length);
            for (var x = 0; x < board.Cells[y].Length; x++)
                if (board.Cells[y][x]) actual.Add((x, y));
        }
        Assert.True(actual.SetEquals(expected), "Returned cells must match the independently verified phase.");
    }

    private sealed class BoardFixture : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"game-of-life-{Guid.NewGuid():N}.db");
        private WebApplicationFactory<Program>? factory;
        private HttpClient? client;
        public Guid Id { get; } = Guid.NewGuid();
        public CountingEngine Engine { get; } = new();
        public string ConnectionString => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        public HttpClient Client => client ??= (factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:Boards"] = ConnectionString }));
                builder.ConfigureServices(services => services.AddSingleton<IGenerationEngine>(Engine));
            })).CreateClient();

        public void Seed((int X, int Y)[] initial, (int X, int Y)[] current, long generation)
        {
            var codec = new BoardStateCodec();
            var store = new SqliteBoardStore(ConnectionString, codec);
            store.Initialize();
            store.Insert(Id, new BoardState(7, 6, initial.ToHashSet()));
            SetCurrent(current, generation);
        }

        public void SetCurrent((int X, int Y)[] current, long generation, Guid? id = null)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Boards SET CurrentState=$state, CurrentGeneration=$generation WHERE Id=$id";
            command.Parameters.AddWithValue("$state", new BoardStateCodec().Encode(new BoardState(7, 6, current.ToHashSet())));
            command.Parameters.AddWithValue("$generation", generation);
            command.Parameters.AddWithValue("$id", (id ?? Id).ToString());
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public (string Initial, string Current, long Generation) StoredValues()
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT InitialState, CurrentState, CurrentGeneration FROM Boards WHERE Id=$id";
            command.Parameters.AddWithValue("$id", Id.ToString());
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
        }

        public void Dispose()
        {
            Reopen();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }

        public void Reopen()
        {
            client?.Dispose();
            factory?.Dispose();
            client = null;
            factory = null;
        }
    }

    private sealed class CountingEngine : IGenerationEngine
    {
        public int Calls { get; private set; }
        public Action<CancellationToken>? AfterCalculation { get; set; }
        public BoardState CalculateNextGeneration(BoardState state, CancellationToken cancellationToken = default)
        {
            Calls++;
            var next = new GenerationEngine().CalculateNextGeneration(state, cancellationToken);
            AfterCalculation?.Invoke(cancellationToken);
            return next;
        }
    }
}
