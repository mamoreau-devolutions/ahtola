using System.Data;
using System.Data.Common;
using System.Reflection;
using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Data.Sqlite;

namespace Ahtola.Tests;

public sealed class ManagedCoreParameterContractRegressionTests
{
    [Test]
    public void CoreParameterContractExposesMetadataAndPreservesClearResetRebind()
    {
        typeof(IManagedStatementAdapter).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Should().NotContain("Turso.Raw");

        using var database = ManagedDatabaseAdapter.Open(":memory:");
        using var connection = database.Connect();
        using var statement = connection.Prepare("SELECT ?1, $name, ?;");

        var parameters = statement.ParameterMetadata;
        parameters.Count.Should().Be(3);
        parameters.GetParameter(1).Should().Be(new ManagedParameter(1, "?1"));
        parameters.GetParameter(2).Should().Be(new ManagedParameter(2, "$name"));
        parameters.GetParameter(3).Should().Be(new ManagedParameter(3, null));
        parameters.GetParameterIndex("$name").Should().Be(2);

        statement.Bind(1, SqlValue.Integer(7));
        statement.Bind(2, SqlValue.Text("first"));
        statement.Bind(3, SqlValue.Blob([1, 2]));
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).AsInteger().Should().Be(7);
        statement.GetValue(1).AsText().Should().Be("first");
        statement.GetValue(2).AsBlob().ToArray().Should().Equal(1, 2);

        statement.ClearBindings();
        statement.GetValue(0).AsInteger().Should().Be(7);
        statement.Reset();

        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).Kind.Should().Be(SqlValueKind.Null);
        statement.GetValue(1).Kind.Should().Be(SqlValueKind.Null);
        statement.GetValue(2).Kind.Should().Be(SqlValueKind.Null);
        statement.Reset();

        statement.Bind(1, SqlValue.Integer(8));
        statement.Bind(2, SqlValue.Text("second"));
        statement.Bind(3, SqlValue.Null);
        statement.Step().Should().Be(StatementStepResult.Row);
        statement.GetValue(0).AsInteger().Should().Be(8);
        statement.GetValue(1).AsText().Should().Be("second");
        statement.GetValue(2).Kind.Should().Be(SqlValueKind.Null);
    }

    [Test]
    public void ManagedSqliteFacadeBindsTypedNumberedNamedAndPositionalValuesAfterRebind()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        GetPrivateField(connection, "_database").Should().BeNull();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ?1, :name, ?, $blob, $nullable;";
        BindSqliteParameters(command, 7L, "first", "positional", [1, 2, 3], DBNull.Value);

        AssertSqliteValues(command, 7L, "first", "positional", [1, 2, 3], isNull: true);

        command.Parameters.Clear();
        Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar())!
            .Message.Should().Be("Missing parameter values for ?1.");

        BindSqliteParameters(command, 8L, "second", "rebound", [4, 5], DBNull.Value);
        AssertSqliteValues(command, 8L, "second", "rebound", [4, 5], isNull: true);

        var outputParameter = new SqliteParameter();
        Assert.Throws<ArgumentException>(() => outputParameter.Direction = ParameterDirection.Output);
    }

    [Test]
    public void ManagedSqliteFacadeMapsCoreStatementErrorsWithoutRawExceptionTranslation()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT missing_managed_parameter_function(?1);";
        command.Parameters.AddWithValue("?1", 1L);

        Assert.Throws<SqliteException>(() => command.ExecuteScalar())!
            .SqliteErrorCode.Should().Be(1);
    }

    [Test]
    public void ManagedSqliteFacadeBindsGuidValuesAsBlobs()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        var id = new Guid("09b4a80a-cb65-4f23-a388-f2c7af681fec");

        using var inferredCommand = connection.CreateCommand();
        inferredCommand.CommandText = "SELECT typeof($id), $id;";
        inferredCommand.Parameters.AddWithValue("$id", id);
        using var inferredReader = inferredCommand.ExecuteReader();
        inferredReader.Read().Should().BeTrue();
        inferredReader.GetString(0).Should().Be("blob");
        inferredReader.GetFieldValue<byte[]>(1).Should().Equal(id.ToByteArray());

        using var typedCommand = connection.CreateCommand();
        typedCommand.CommandText = "SELECT typeof($id), $id;";
        var parameter = typedCommand.Parameters.Add("$id", SqliteType.Text);
        parameter.DbType = DbType.Guid;
        parameter.Value = id;
        using var typedReader = typedCommand.ExecuteReader();
        typedReader.Read().Should().BeTrue();
        typedReader.GetString(0).Should().Be("blob");
        typedReader.GetFieldValue<byte[]>(1).Should().Equal(id.ToByteArray());
    }

    [Test]
    public void ManagedSqliteFacadeReportsGuidReaderStorageWithoutValue()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE users(id GUID PRIMARY KEY); INSERT INTO users(id) VALUES ('not-a-guid');";
            create.ExecuteNonQuery();
        }

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id FROM users;";
        using var reader = select.ExecuteReader();
        reader.Read().Should().BeTrue();

        var exception = Assert.Throws<InvalidOperationException>(() => reader.GetValue(0))!;
        exception.Message.Should().Be("Unable to parse GUID for column 'id' (ordinal 0, declared type 'GUID', storage TEXT).");
        exception.Message.Should().NotContain("not-a-guid");
    }

    [Test]
    public void ManagedSqliteFacadeAdapterUpdatesGuidAndTextColumns()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE users(id GUID PRIMARY KEY, name STRING NOT NULL);";
            create.ExecuteNonQuery();
        }

        using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = "SELECT id, name FROM users;";
            using var schemaReader = schemaCommand.ExecuteReader();
            var schema = schemaReader.GetSchemaTable()!;
            schema.Rows[0][SchemaTableColumn.ProviderType].Should().Be((int)SqliteType.Blob);
            schema.Rows[1][SchemaTableColumn.ProviderType].Should().Be((int)SqliteType.Text);
        }

        using var adapter = new Ahtola.AhtolaDataAdapter("SELECT id, name FROM users", connection);
        using var builder = new Ahtola.AhtolaCommandBuilder(adapter);
        var users = new DataTable();
        adapter.Fill(users);
        users.Columns["id"]!.DataType.Should().Be(typeof(Guid));
        users.Columns["name"]!.DataType.Should().Be(typeof(string));

        var id = new Guid("09b4a80a-cb65-4f23-a388-f2c7af681fec");
        users.Rows.Add(id, "dvls-admin");
        adapter.Update(users).Should().Be(1);

        using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT id, name FROM users;";
        using var reader = verify.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetFieldValue<Guid>(0).Should().Be(id);
        reader.GetString(1).Should().Be("dvls-admin");
    }

    [Test]
    public void ManagedSqliteFacadeAdapterResolvesJoinedTextColumnMetadata()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE UserAccount(id GUID PRIMARY KEY);
                CREATE TABLE UserSecurity(id GUID PRIMARY KEY, name NOT NULL, usertype INTEGER NOT NULL, isdeleted INTEGER NOT NULL);
                """;
            create.ExecuteNonQuery();
        }

        var id = new Guid("09b4a80a-cb65-4f23-a388-f2c7af681fec");
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO UserAccount(id) VALUES ($id);
                INSERT INTO UserSecurity(id, name, usertype, isdeleted) VALUES ($id, 'dvls-admin', 0, 0);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.ExecuteNonQuery();
        }

        using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText =
            """
            SELECT a.*, s.name
            FROM UserAccount a
            LEFT OUTER JOIN UserSecurity s ON a.id = s.id
            WHERE s.usertype = $userType AND a.id = $id AND s.isdeleted = 0;
            """;
        selectCommand.Parameters.AddWithValue("$userType", 0);
        selectCommand.Parameters.AddWithValue("$id", id);

        using var adapter = new GenericDataAdapter();
        ((IDbDataAdapter)adapter).SelectCommand = selectCommand;
        using (var reader = selectCommand.ExecuteReader())
        {
            var schema = reader.GetSchemaTable()!;
            schema.Rows[1][SchemaTableColumn.DataType].Should().Be(typeof(string));
            reader.GetFieldType(1).Should().Be(typeof(string));
            reader.GetDataTypeName(1).Should().Be("TEXT");
        }

        var users = new DataTable();
        adapter.Fill(users).Should().Be(1);
        users.Columns["id"]!.DataType.Should().Be(typeof(Guid));
        users.Columns["name"]!.DataType.Should().Be(typeof(string));
        users.Rows[0]["name"].Should().BeOfType<string>();
        users.Rows[0]["name"].Should().Be("dvls-admin");
    }

    [Test]
    public void ManagedSqliteFacadeAdapterRetainsUntypedNotifierName()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE Notifier(id GUID PRIMARY KEY, name NOT NULL, content TEXT NOT NULL, type TEXT NOT NULL);
                CREATE TABLE NotifierGroupToNotifier(notifierid GUID NOT NULL);
                """;
            create.ExecuteNonQuery();
        }

        var id = new Guid("09b4a80a-cb65-4f23-a388-f2c7af681fec");
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO Notifier(id, name, content, type) VALUES ($id, 'dvls-admin', 'user:admin', 'User');";
            insert.Parameters.AddWithValue("$id", id);
            insert.ExecuteNonQuery();
        }

        using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText = """
            SELECT DISTINCT
                Notifier.*,
                NULL AS Groups,
                NULL AS Subscriptions
            FROM Notifier
            LEFT JOIN NotifierGroupToNotifier ON NotifierGroupToNotifier.NotifierId = Notifier.ID
            WHERE Content LIKE $userId AND Type IN ($subscriberType);
            """;
        selectCommand.Parameters.AddWithValue("$userId", "%admin%");
        selectCommand.Parameters.AddWithValue("$subscriberType", "User");

        using var adapter = new GenericDataAdapter();
        ((IDbDataAdapter)adapter).SelectCommand = selectCommand;
        var notifiers = new DataTable();
        adapter.Fill(notifiers).Should().Be(1);
        notifiers.Columns["name"]!.DataType.Should().Be(typeof(string));
        notifiers.Rows[0]["name"].Should().BeOfType<string>().Which.Should().Be("dvls-admin");
    }

    [Test]
    public void ManagedAhtolaFacadeBindsNumberedNamedAndPositionalValuesAfterRebind()
    {
        using var connection = new AhtolaConnection("Data Source=:memory:;Local Provider=Managed");
        connection.Open();
        GetPrivateField(connection, "_nativeDatabase").Should().BeNull();

        using var command = new AhtolaCommand(connection);
        command.CommandText = "SELECT ?1, $name, ?;";
        BindAhtolaParameters(command, 7L, "first", "positional");
        AssertAhtolaValues(command, 7L, "first", "positional");

        command.Parameters.Clear();
        Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar())!
            .Message.Should().Be("Missing value for parameter ?1.");

        BindAhtolaParameters(command, 8L, "second", "rebound");
        AssertAhtolaValues(command, 8L, "second", "rebound");

        var outputParameter = new AhtolaParameter();
        Assert.Throws<ArgumentException>(() => outputParameter.Direction = ParameterDirection.Output);
    }

    private static void BindSqliteParameters(
        SqliteCommand command,
        long numbered,
        string named,
        string positional,
        byte[] blob,
        object nullable)
    {
        command.Parameters.AddWithValue(null, positional);
        command.Parameters.AddWithValue("name", named);
        command.Parameters.AddWithValue("?1", numbered);
        var blobParameter = command.Parameters.Add("$blob", SqliteType.Blob);
        blobParameter.DbType = DbType.Binary;
        blobParameter.Value = blob;
        command.Parameters.AddWithValue("$nullable", nullable);
    }

    private static void AssertSqliteValues(
        SqliteCommand command,
        long numbered,
        string named,
        string positional,
        byte[] blob,
        bool isNull)
    {
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(numbered);
        reader.GetString(1).Should().Be(named);
        reader.GetString(2).Should().Be(positional);
        reader.GetFieldValue<byte[]>(3).Should().Equal(blob);
        reader.IsDBNull(4).Should().Be(isNull);
        reader.Read().Should().BeFalse();
    }

    private static void BindAhtolaParameters(AhtolaCommand command, long numbered, string named, string positional)
    {
        command.Parameters.Add(new AhtolaParameter("?1", numbered));
        command.Parameters.Add(new AhtolaParameter("$name", named));
        command.Parameters.Add(new AhtolaParameter(positional));
    }

    private static void AssertAhtolaValues(AhtolaCommand command, long numbered, string named, string positional)
    {
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(numbered);
        reader.GetString(1).Should().Be(named);
        reader.GetString(2).Should().Be(positional);
        reader.Read().Should().BeFalse();
    }

    private static object? GetPrivateField(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Expected {instance.GetType().Name}.{fieldName}.");
        return field.GetValue(instance);
    }

    private sealed class GenericDataAdapter : DbDataAdapter
    {
    }
}
