using AwesomeAssertions;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

/// <summary>
/// Pins Stage 6 main-file SHARED locking vs connection pooling, and live WAL
/// multi-engine coexistence (shared <c>-shm</c> DMS + reader locks).
/// Managed physical pagers hold SQLite SHARED (not exclusive 512-byte ownership),
/// so ordinary SQLite readers may open the same database while managed is live.
/// Pooling still retains the managed physical handle until <c>ClearAllPools</c>.
/// </summary>
[NonParallelizable]
public sealed class ManagedOwnershipHandoffPoolingTests
{
    [SetUp]
    public void SetUp() => SqliteConnection.ClearAllPools();

    [TearDown]
    public void TearDown() => SqliteConnection.ClearAllPools();

    [Test]
    public void DisposingAPooledManagedConnectionRetainsOwnershipUntilThePoolIsCleared()
    {
        var path = CreateDatabasePath();
        try
        {
            SeedWithOrdinarySqlite(path);

            using (var managed = OpenManaged(path, pooling: true))
            {
                managed.ExecuteScalarLong("SELECT COUNT(*) FROM t;").Should().Be(1);
            }

            // Stage 6 SHARED coexists with ordinary SQLite even while pooled.
            TryOpenWithOrdinarySqlite(path).Should().BeNull(
                "Stage 6 SHARED lock must allow ordinary SQLite readers while managed is pooled");

            SqliteConnection.ClearAllPools();

            TryOpenWithOrdinarySqlite(path).Should().BeNull(
                "clearing the pool still leaves a clean handoff for ordinary SQLite");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void PoolingFalseReleasesOwnershipOnDisposeSoSqliteCanReopen()
    {
        var path = CreateDatabasePath();
        try
        {
            SeedWithOrdinarySqlite(path);

            using (var managed = OpenManaged(path, pooling: false))
            {
                managed.ExecuteNonQueryText("INSERT INTO t (v) VALUES ('managed');");
            }

            TryOpenWithOrdinarySqlite(path).Should().BeNull(
                "Pooling=False must release ownership when the connection is disposed");

            ReadCountWithOrdinarySqlite(path).Should().Be(2, "the managed write must be durable");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    [Test]
    public void LiveManagedSharedLockAllowsConcurrentOrdinarySqliteReaders()
    {
        var path = CreateDatabasePath();
        try
        {
            SeedWithOrdinarySqlite(path);

            using (var managed = OpenManaged(path, pooling: true))
            {
                managed.ExecuteScalarLong("SELECT COUNT(*) FROM t;").Should().Be(1);
                TryOpenWithOrdinarySqlite(path).Should().BeNull(
                    "ordinary SQLite must share the database under Stage 6 SHARED locks");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

        [Test]
        public void LiveManagedWalAllowsConcurrentOrdinarySqliteReaders()
        {
            var path = CreateDatabasePath();
            try
            {
                SeedWithOrdinarySqliteWal(path);
                var walLenBefore = File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : -1L;
                var shmLenBefore = File.Exists(path + "-shm") ? new FileInfo(path + "-shm").Length : -1L;

                using (var managed = OpenManaged(path, pooling: false))
                {
                    managed.ExecuteScalarLong("SELECT COUNT(*) FROM t;").Should().Be(1);
                    var walLen = File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : -1L;
                    var shmLen = File.Exists(path + "-shm") ? new FileInfo(path + "-shm").Length : -1L;

                    var failure = TryOpenWithOrdinarySqlite(path);
                    if (failure is not null)
                    {
                        // Second chance: read-only stock open (still multi-engine WAL).
                        failure = TryOpenWithOrdinarySqlite(path, readOnly: true) ?? failure;
                    }

                    failure.Should().BeNull(
                        "shared -shm reader locks must let ordinary SQLite open a managed-held WAL database"
                        + $"; walBefore={walLenBefore} shmBefore={shmLenBefore} wal={walLen} shm={shmLen}"
                        + (failure is null ? string.Empty : $"; ordinary SQLite failed: {failure}"));

                    ReadCountWithOrdinarySqlite(path).Should().Be(1);
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                DeleteDatabase(path);
            }
        }

    [Test]
    public void OwnershipRoundTripsBetweenManagedAndOrdinarySqliteWhenPoolingIsDisabled()
    {
        var path = CreateDatabasePath();
        try
        {
            SeedWithOrdinarySqlite(path);

            using (var managed = OpenManaged(path, pooling: false))
                managed.ExecuteNonQueryText("INSERT INTO t (v) VALUES ('first');");

            ReadCountWithOrdinarySqlite(path).Should().Be(2);

            using (var managed = OpenManaged(path, pooling: false))
            {
                managed.ExecuteScalarLong("SELECT COUNT(*) FROM t;").Should().Be(2);
                managed.ExecuteNonQueryText("INSERT INTO t (v) VALUES ('second');");
            }

            ReadCountWithOrdinarySqlite(path).Should().Be(3);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(path);
        }
    }

    private static SqliteConnection OpenManaged(string path, bool pooling)
    {
        var connection = new SqliteConnection(
            $"Data Source={path};Pooling={(pooling ? "True" : "False")};Local Provider=Managed");
        connection.Open();
        return connection;
    }

    private static void SeedWithOrdinarySqlite(string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO t (v) VALUES ('seed');";
        command.ExecuteNonQuery();
        connection.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

        private static void SeedWithOrdinarySqliteWal(string path)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "PRAGMA journal_mode=WAL;"
                + "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);"
                + "INSERT INTO t (v) VALUES ('seed');";
            command.ExecuteNonQuery();
            connection.Close();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

    /// <summary>
    /// Returns the failure message when ordinary SQLite cannot open the database,
    /// or <see langword="null"/> when the open succeeds.
    /// </summary>
    private static string? TryOpenWithOrdinarySqlite(string path, bool readOnly = false)
    {
        try
        {
                ReadCountWithOrdinarySqlite(path, readOnly);
            return null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
                return $"{exception.Message} (SqliteErrorCode={exception.SqliteErrorCode}, Extended={exception.SqliteExtendedErrorCode})";
        }
    }

        private static long ReadCountWithOrdinarySqlite(string path, bool readOnly = false)
    {
        try
        {
                var cs = readOnly
                    ? $"Data Source={path};Mode=ReadOnly"
                    : $"Data Source={path}";
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(cs);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM t;";
                return (long)command.ExecuteScalar()!;
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "managed-ownership-handoff-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"handoff-{Guid.NewGuid():N}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try
            {
                File.Delete(candidate);
            }
            catch (IOException)
            {
                // A retained handle is not a test failure during cleanup.
            }
        }
    }
}

internal static class ManagedOwnershipHandoffCommandExtensions
{
    internal static long ExecuteScalarLong(this SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    internal static void ExecuteNonQueryText(this SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
