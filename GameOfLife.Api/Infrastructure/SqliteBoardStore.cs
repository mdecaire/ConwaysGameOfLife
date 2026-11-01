using GameOfLife.Api.Domain;
using Microsoft.Data.Sqlite;

namespace GameOfLife.Api.Infrastructure;

/// <summary>Persists board progress and successful final-state discoveries in SQLite.</summary>
/// <param name="connectionString">The connection string used for each database operation.</param>
/// <param name="codec">The codec used to serialize living-cell coordinates.</param>
public sealed class SqliteBoardStore
{
    private readonly string connectionString;
    private readonly BoardStateCodec codec;
    private readonly Func<string, SqliteConnection> createConnection;

    public SqliteBoardStore(string connectionString, BoardStateCodec codec)
        : this(connectionString, codec, value => new SqliteConnection(value)) { }

    // Enables narrowly scoped real-provider observations/faults in integration tests.
    internal SqliteBoardStore(string connectionString, BoardStateCodec codec,
        Func<string, SqliteConnection> createConnection)
    {
        var settings = new SqliteConnectionStringBuilder(connectionString) { Cache = SqliteCacheMode.Private };
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.DefaultTimeout);
        this.connectionString = settings.ToString();
        this.codec = codec;
        this.createConnection = createConnection;
    }

    /// <summary>Enables write-ahead logging and creates missing board and final-result tables.</summary>
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
                InitialState TEXT NOT NULL,
                CurrentState TEXT NOT NULL,
                CurrentGeneration INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS FinalStateResults (
                BoardId TEXT NOT NULL PRIMARY KEY REFERENCES Boards(Id),
                StartingGeneration INTEGER NOT NULL,
                ResultGeneration INTEGER NOT NULL,
                ResultState TEXT NOT NULL,
                CycleLength INTEGER NOT NULL CHECK (CycleLength > 0),
                IterationsUsed INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Atomically inserts identical initial/current states at generation zero.</summary>
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
            INSERT INTO Boards (Id, Width, Height, InitialState, CurrentState, CurrentGeneration)
            VALUES ($id, $width, $height, $state, $state, $generation);
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

    /// <summary>Reconstructs the original uploaded state, independently of current progress.</summary>
    public BoardState? FindInitial(Guid boardId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Width, Height, InitialState FROM Boards WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", boardId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? codec.Decode(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2))
            : null;
    }

    /// <summary>Reads the original discovery metadata and representative independently of current progress.</summary>
    public FinalStateResult? FindFinalState(Guid boardId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.Width, b.Height, f.ResultState, f.StartingGeneration,
                   f.ResultGeneration, f.CycleLength, f.IterationsUsed
            FROM FinalStateResults f JOIN Boards b ON b.Id = f.BoardId WHERE f.BoardId = $id;
            """;
        command.Parameters.AddWithValue("$id", boardId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var period = reader.GetInt32(5);
        var generation = reader.GetInt64(4);
        return new(period == 1 ? FinalStateOutcome.Stable : FinalStateOutcome.Cycle,
            codec.Decode(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2)),
            reader.GetInt64(3), generation, period, generation - period, reader.GetInt32(6));
    }

    /// <summary>Commits a successful discovery, or returns the unchanged same-board winner.</summary>
    /// <remarks>Only the BoardId conflict is ignored. Other constraints and database failures propagate.
    /// Cancellation observed before commit rolls back; a completed commit remains durable.</remarks>
    public FinalStateResult SaveFinalState(Guid boardId, FinalStateResult result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Outcome == FinalStateOutcome.Inconclusive || result.CycleLength is not > 0)
            throw new ArgumentException("Only successful discoveries can be persisted.", nameof(result));
        var encodedState = codec.Encode(result.State);
        int inserted;
        using (var connection = OpenConnection())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO FinalStateResults
                    (BoardId, StartingGeneration, ResultGeneration, ResultState, CycleLength, IterationsUsed)
                VALUES ($id, $start, $generation, $state, $period, $iterations)
                ON CONFLICT(BoardId) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", boardId.ToString());
            command.Parameters.AddWithValue("$start", result.StartingGeneration);
            command.Parameters.AddWithValue("$generation", result.Generation);
            command.Parameters.AddWithValue("$state", encodedState);
            command.Parameters.AddWithValue("$period", result.CycleLength.Value);
            command.Parameters.AddWithValue("$iterations", result.IterationsUsed);
            cancellationToken.ThrowIfCancellationRequested();
            inserted = command.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        return inserted == 1 ? result : FindFinalState(boardId)
            ?? throw new InvalidOperationException("The saved final-state winner could not be read.");
    }

    public bool TryAdvance(Guid boardId, BoardState nextState, long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nextGeneration = checked(expectedGeneration + 1);
        var encodedState = codec.Encode(nextState);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection();
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Boards SET CurrentState = $state, CurrentGeneration = $generation
            WHERE Id = $id AND CurrentGeneration = $expectedGeneration;
            """;
        command.Parameters.AddWithValue("$id", boardId.ToString());
        command.Parameters.AddWithValue("$state", encodedState);
        command.Parameters.AddWithValue("$generation", nextGeneration);
        command.Parameters.AddWithValue("$expectedGeneration", expectedGeneration);
        cancellationToken.ThrowIfCancellationRequested();
        if (command.ExecuteNonQuery() == 0) return false;
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return true;
    }

    /// <summary>Opens a connection with foreign-key enforcement and full synchronization enabled.</summary>
    /// <returns>An open connection that the caller must dispose.</returns>
    /// <exception cref="SqliteException">SQLite could not open or configure the connection.</exception>
    private SqliteConnection OpenConnection()
    {
        var connection = createConnection(connectionString);
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
