using GameOfLife.Api.Domain;
using Microsoft.Data.Sqlite;

namespace GameOfLife.Api.Infrastructure;

/// <summary>Initializes SQLite storage and persists uploaded boards.</summary>
/// <param name="connectionString">The connection string used for each database operation.</param>
/// <param name="codec">The codec used to serialize living-cell coordinates.</param>
public sealed class SqliteBoardStore(string connectionString, BoardStateCodec codec)
{
    /// <summary>Enables write-ahead logging and creates the Boards table if it does not exist.</summary>
    /// <exception cref="SqliteException">SQLite could not configure or initialize the database.</exception>
    public void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Boards (
                Id TEXT NOT NULL PRIMARY KEY,
                Width INTEGER NOT NULL,
                Height INTEGER NOT NULL,
                CurrentState TEXT NOT NULL,
                CurrentGeneration INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Inserts and commits a new board's current state at generation zero.</summary>
    /// <param name="boardId">The unique identifier assigned to the upload.</param>
    /// <param name="state">The board dimensions and living-cell coordinates to persist.</param>
    /// <param name="cancellationToken">The token checked at entry and before executing the insert.</param>
    /// <remarks>Database calls are synchronous; the token does not interrupt an executing SQLite command.</remarks>
    /// <exception cref="OperationCanceledException">Cancellation was observed before the insert.</exception>
    /// <exception cref="SqliteException">SQLite could not open, configure, or write to the database.</exception>
    public void Insert(Guid boardId, BoardState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Boards (Id, Width, Height, CurrentState, CurrentGeneration)
            VALUES ($id, $width, $height, $state, $generation);
            """;
        command.Parameters.AddWithValue("$id", boardId.ToString());
        command.Parameters.AddWithValue("$width", state.Width);
        command.Parameters.AddWithValue("$height", state.Height);
        command.Parameters.AddWithValue("$state", codec.Encode(state));
        command.Parameters.AddWithValue("$generation", 0L);
        cancellationToken.ThrowIfCancellationRequested();
        // SQLite commits this single INSERT atomically before ExecuteNonQuery returns.
        command.ExecuteNonQuery();
    }

    /// <summary>Reads the current board state and generation from SQLite by identifier.</summary>
    /// <param name="boardId">The identifier of the board to retrieve.</param>
    /// <returns>The reconstructed state and generation, or null if the board does not exist.</returns>
    /// <exception cref="SqliteException">SQLite could not open, configure, or read the database.</exception>
    /// <exception cref="System.Text.Json.JsonException">The stored coordinate JSON is invalid.</exception>
    public (BoardState State, long Generation)? Find(Guid boardId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Width, Height, CurrentState, CurrentGeneration
            FROM Boards WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", boardId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var state = codec.Decode(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2));
        return (state, reader.GetInt64(3));
    }

    /// <summary>Opens a connection with foreign-key enforcement and full synchronization enabled.</summary>
    /// <returns>An open connection that the caller must dispose.</returns>
    /// <exception cref="SqliteException">SQLite could not open or configure the connection.</exception>
    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
