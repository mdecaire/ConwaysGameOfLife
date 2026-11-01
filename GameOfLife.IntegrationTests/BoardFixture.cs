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

internal sealed class BoardFixture : IDisposable
{
    // A glider's first three distinct phases, verified by counting neighbors by hand.
    // The board is asymmetric to expose row/column transposition.
    public static readonly (int X, int Y)[][] Phases =
    [
        [(2, 1), (3, 2), (1, 3), (2, 3), (3, 3)],
        [(1, 2), (3, 2), (2, 3), (3, 3), (2, 4)],
        [(3, 2), (1, 3), (3, 3), (2, 4), (3, 4)]
    ];

    public static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Equal((int)status, problem!.Status);
    }

    public static async Task AssertBoard(HttpResponseMessage response, Guid id, long generation,
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

    private readonly string path = Path.Combine(Path.GetTempPath(), $"game-of-life-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program>? factory;
    private HttpClient? client;
    public Guid Id { get; } = Guid.NewGuid();
    public CountingEngine Engine { get; } = new();
    public int ProviderTimeoutSeconds { get; set; } = 30;
    public Func<string, SqliteBoardStore>? CreateStore { get; set; }
    /// <summary>Disables pooling so connections close before temporary-file cleanup.</summary>
    public string ConnectionString => new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false, DefaultTimeout = ProviderTimeoutSeconds }.ToString();
    /// <summary>Creates a Production test host against the fixture database on first access or after Reopen.</summary>
    public HttpClient Client => client ??= (factory = new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Boards"] = ConnectionString }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IGenerationEngine>(Engine);
                if (CreateStore is not null) services.AddSingleton(CreateStore(ConnectionString));
            });
        })).CreateClient();

    /// <summary>Creates a 7-by-6 board with original cells and independently seeded current progress.</summary>
    /// <param name="initial">The original uploaded living coordinates.</param>
    /// <param name="current">The current living coordinates to seed directly in SQLite.</param>
    /// <param name="generation">The current absolute generation to persist.</param>
    public void Seed((int X, int Y)[] initial, (int X, int Y)[] current, long generation)
    {
        var codec = new BoardStateCodec();
        var store = new SqliteBoardStore(ConnectionString, codec);
        store.Initialize();
        store.Insert(Id, new BoardState(7, 6, initial.ToHashSet()));
        SetCurrent(current, generation);
    }

    /// <summary>Updates current state and generation through a separate connection, leaving initial state unchanged.</summary>
    /// <param name="current">The living coordinates to persist on the 7-by-6 board.</param>
    /// <param name="generation">The absolute generation to persist.</param>
    /// <param name="id">The board to update, or null to use the seeded board identifier.</param>
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

    /// <summary>Reads the seeded board's raw initial/current JSON and generation through a new connection.</summary>
    /// <returns>The stored values used to assert persistence and detect unintended writes.</returns>
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

    /// <summary>Closes the HTTP host and deletes the fixture's temporary SQLite files.</summary>
    public void Dispose()
    {
        Reopen();
        File.Delete(path);
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
    }

    /// <summary>Closes the client and host so the next Client access starts a new app against the same database.</summary>
    public void Reopen()
    {
        client?.Dispose();
        factory?.Dispose();
        client = null;
        factory = null;
    }

    public sealed class CountingEngine : IGenerationEngine
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        /// <summary>Gets or sets a callback invoked with the supplied token after a successful calculation.</summary>
        public Action<CancellationToken>? AfterCalculation { get; set; }
        public BoardState CalculateNextGeneration(BoardState state, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            var next = new GenerationEngine().CalculateNextGeneration(state, cancellationToken);
            AfterCalculation?.Invoke(cancellationToken);
            return next;
        }
    }
}
