using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameOfLife.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GameOfLife.IntegrationTests;

public sealed class BoardUploadTests
{
    /// <summary>Verifies that a new store reconstructs the requested upload after the application closes.</summary>
    /// <returns>A task representing the uploads, application shutdown, and storage reads.</returns>
    [Fact]
    public async Task NewStoreRestoresUploadedBoardsAfterApplicationCloses()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"game-of-life-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
        bool[][] firstCells = [[false, true, false, false], [true, false, false, true]];
        bool[][] secondCells = [[false, false, true]];
        var boardIds = new List<Guid>();

        try
        {
            using (var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Boards"] = connectionString
                    }))))
            using (var client = factory.CreateClient())
            {
                foreach (var cells in new[] { firstCells, secondCells })
                {
                    using var response = await client.PostAsJsonAsync("/api/boards", new { cells });
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    boardIds.Add(body.RootElement.GetProperty("boardId").GetGuid());
                }
            }

            var store = new SqliteBoardStore(connectionString, new BoardStateCodec());
            var second = store.Find(boardIds[1]);
            Assert.True(second.HasValue);
            Assert.Equal(3, second.Value.State.Width);
            Assert.Equal(1, second.Value.State.Height);
            Assert.True(second.Value.State.LivingCells.SetEquals(new[] { (2, 0) }));
            Assert.Equal(0L, second.Value.Generation);

            var first = store.Find(boardIds[0]);
            Assert.True(first.HasValue);
            Assert.Equal(4, first.Value.State.Width);
            Assert.Equal(2, first.Value.State.Height);
            Assert.True(first.Value.State.LivingCells.SetEquals(new[] { (1, 0), (0, 1), (3, 1) }));
            Assert.Equal(0L, first.Value.Generation);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
        }
    }

    /// <summary>Verifies that a real SQLite insert failure produces a sanitized error and no saved board.</summary>
    /// <param name="environment">The hosting environment in which error sanitization is verified.</param>
    /// <returns>A task representing the failed upload and database assertions.</returns>
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task FailedInsertReturnsSanitizedProblemWithoutPersistingBoard(string environment)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"game-of-life-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();

        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Boards"] = connectionString
                    }));
            });
            using var client = factory.CreateClient();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER RejectBoardInsert BEFORE INSERT ON Boards
                    BEGIN
                        SELECT RAISE(ABORT, 'upload-write-failure-secret');
                    END;
                    """;
                command.ExecuteNonQuery();
            }

            using var response = await client.PostAsJsonAsync("/api/boards", new
            {
                cells = new[] { new[] { true, false }, new[] { false, true } }
            });

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var responseText = await response.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(responseText);
            Assert.Equal(500, body.RootElement.GetProperty("status").GetInt32());
            Assert.False(body.RootElement.TryGetProperty("boardId", out _));
            Assert.DoesNotContain("upload-write-failure-secret", responseText);
            Assert.DoesNotContain(Path.GetFileName(databasePath), responseText);
            Assert.DoesNotContain("SqliteException", responseText);
            Assert.DoesNotContain("INSERT INTO", responseText);
            Assert.DoesNotContain("RejectBoardInsert", responseText);
            Assert.DoesNotContain("stackTrace", responseText, StringComparison.OrdinalIgnoreCase);

            using var verification = new SqliteConnection(connectionString);
            verification.Open();
            using var count = verification.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM Boards";
            Assert.Equal(0L, count.ExecuteScalar());
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
        }
    }

    /// <summary>Verifies that JSON binding failures return a bad-request problem without persistence in Production.</summary>
    /// <param name="json">Malformed JSON or an upload containing a non-boolean cell value.</param>
    /// <returns>A task representing the invalid upload and database assertions.</returns>
    [Theory]
    [InlineData("{")]
    [InlineData("""{"cells":[[1]]}""")]
    [InlineData("""{"cells":[["true"]]}""")]
    public async Task InvalidJsonReturnsBadRequestProblemWithoutPersistingBoard(string json)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"game-of-life-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();

        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Boards"] = connectionString
                    }));
            });
            using var client = factory.CreateClient();
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            using var response = await client.PostAsync("/api/boards", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
            Assert.False(body.RootElement.TryGetProperty("boardId", out _));

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Boards";
            Assert.Equal(0L, command.ExecuteScalar());
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
        }
    }

    /// <summary>Verifies that invalid grid structures return the appropriate problem without persistence.</summary>
    /// <param name="json">The upload JSON containing an invalid grid structure.</param>
    /// <param name="expectedDetail">The validation message expected in the problem response.</param>
    /// <returns>A task representing the invalid upload and persistence assertions.</returns>
    [Theory]
    [InlineData("""{"cells":null}""", "The cells grid must not be null.")]
    [InlineData("""{"cells":[]}""", "The cells grid must contain at least one row.")]
    [InlineData("""{"cells":[[]]}""", "The cells grid must contain at least one column.")]
    [InlineData("""{"cells":[[],[]]}""", "The cells grid must contain at least one column.")]
    [InlineData("""{"cells":[null]}""", "The cells grid must not contain null rows.")]
    [InlineData("""{"cells":[null,[true]]}""", "The cells grid must not contain null rows.")]
    [InlineData("""{"cells":[[true],null]}""", "The cells grid must not contain null rows.")]
    [InlineData("""{"cells":[[true],[false,true]]}""", "All rows must have the same length.")]
    [InlineData("""{"cells":[[true,false],[true]]}""", "All rows must have the same length.")]
    [InlineData("""{"cells":[[true],[]]}""", "All rows must have the same length.")]
    public async Task InvalidCellsReturnsBadRequestProblemWithoutPersistingBoard(string json, string expectedDetail)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"game-of-life-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();

        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Boards"] = connectionString
                    })));
            using var client = factory.CreateClient();
            using var content = new StringContent(
                json, System.Text.Encoding.UTF8, "application/json");

            using var response = await client.PostAsync("/api/boards", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("Invalid board", body.RootElement.GetProperty("title").GetString());
            Assert.Equal(expectedDetail, body.RootElement.GetProperty("detail").GetString());
            Assert.False(body.RootElement.TryGetProperty("boardId", out _));

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Boards";
            Assert.Equal(0L, command.ExecuteScalar());
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
        }
    }

    /// <summary>Verifies unique upload responses and exact board persistence after reopening SQLite.</summary>
    /// <returns>A task representing the HTTP uploads and persistence assertions.</returns>
    [Fact]
    public async Task ValidUploadsReturnUniqueIdsAndPersistAsymmetricBoardsAtGenerationZero()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"game-of-life-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
        bool[][] cells = [[false, true, false, false], [true, false, false, true]];
        var boardIds = new HashSet<Guid>();

        try
        {
            using (var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Boards"] = connectionString
                    }))))
            using (var client = factory.CreateClient())
            {
                for (var upload = 0; upload < 2; upload++)
                {
                    using var response = await client.PostAsJsonAsync("/api/boards", new { cells });

                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var root = body.RootElement;
                    var boardId = root.GetProperty("boardId").GetGuid();
                    Assert.NotEqual(Guid.Empty, boardId);
                    Assert.True(boardIds.Add(boardId), "Each upload must receive a distinct board ID.");
                    Assert.Equal(0, root.GetProperty("generation").GetInt64());
                    Assert.Equal(4, root.GetProperty("width").GetInt32());
                    Assert.Equal(2, root.GetProperty("height").GetInt32());
                    var returnedCells = root.GetProperty("cells").Deserialize<bool[][]>()!;
                    Assert.Equal(cells.Length, returnedCells.Length);
                    for (var y = 0; y < cells.Length; y++)
                    {
                        Assert.Equal(cells[y], returnedCells[y]);
                    }
                }
            }

            // Reopen after the application and all its connections have been disposed.
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Width, Height, CurrentState, CurrentGeneration FROM Boards";
            using var reader = command.ExecuteReader();
            var storedCount = 0;
            while (reader.Read())
            {
                Assert.True(boardIds.Remove(Guid.Parse(reader.GetString(0))));
                Assert.Equal(4, reader.GetInt32(1));
                Assert.Equal(2, reader.GetInt32(2));
                var restored = new BoardStateCodec().Decode(
                    reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3));
                Assert.True(restored.LivingCells.SetEquals(new[] { (1, 0), (0, 1), (3, 1) }));
                Assert.Equal(0, reader.GetInt64(4));
                storedCount++;
            }
            Assert.Equal(2, storedCount);
            Assert.Empty(boardIds);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");
        }
    }
}
