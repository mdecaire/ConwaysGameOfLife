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
