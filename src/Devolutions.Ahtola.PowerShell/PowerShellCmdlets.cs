using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.IO;
using System.Linq;
using System.Management.Automation;
using Ahtola.Data.Sqlite;

namespace Ahtola.PSSqlite;

public abstract class PSSqliteCmdlet : PSCmdlet
{
    protected static bool IsSingleResult(object? value)
    {
        return value is null or DataTable or DataSet or IDataReader or IDictionary or string;
    }

    protected void WriteResult(object? value)
    {
        if (value is null)
        {
            return;
        }

        if (IsSingleResult(value))
        {
            WriteObject(value);
            return;
        }

        if (value is IEnumerable enumerable)
        {
            WriteObject(enumerable, true);
            return;
        }

        WriteObject(value);
    }

    protected string ExpandString(string value)
    {
        return SessionState.InvokeCommand.ExpandString(value);
    }

    protected void CloseIfNeeded(SqliteConnection? connection, bool keepAlive)
    {
        if (connection is null || keepAlive)
        {
            return;
        }

        if (connection.State == System.Data.ConnectionState.Open)
        {
            connection.Close();
        }

        SqliteConnection.ClearPool(connection);
    }
}

[Cmdlet(VerbsCommon.New, "PSSqliteConnection")]
[OutputType(typeof(SqliteConnection))]
public sealed class NewPSSqliteConnectionCommand : PSSqliteCmdlet
{
    [Parameter(ParameterSetName = "byConnectionString")]
    public string ConnectionString { get; set; } = "Data Source=:memory:;Cache=Shared;";

    [Parameter(ParameterSetName = "byDatabasePath")]
    public string DatabasePath { get; set; } = Directory.GetCurrentDirectory();

    [Parameter(Mandatory = true, ParameterSetName = "byDatabasePath")]
    public string? DatabaseFile { get; set; }

    protected override void ProcessRecord()
    {
        var connection = ParameterSetName == "byDatabasePath"
            ? ConnectionFactory.Create(ExpandString(DatabasePath), ExpandString(DatabaseFile!))
            : ConnectionFactory.Create(ExpandString(ConnectionString));
        WriteObject(connection);
    }
}

[Cmdlet(VerbsLifecycle.Invoke, "PSSqliteQuery")]
public sealed class InvokePSSqliteQueryCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true)]
    public SqliteConnection SqliteConnection { get; set; } = null!;

    [Parameter(Mandatory = true)]
    [Alias("Query")]
    public string CommandText { get; set; } = string.Empty;

    [Parameter]
    [ValidateSet("DataTable", "DataReader", "DataSet", "OrderedDictionary", "PSCustomObject")]
    public string As { get; set; } = "DataTable";

    [Parameter]
    public Type? CastAs { get; set; }

    [Parameter]
    public IDictionary? Parameters { get; set; }

    [Parameter]
    public int CommandTimeout { get; set; } = 30;

    [Parameter]
    public SwitchParameter KeepAlive { get; set; }

    protected override void ProcessRecord()
    {
        var result = QueryExecutor.Execute(
            SqliteConnection,
            CommandText,
            Parameters,
            new QueryOptions
            {
                OutputFormat = As,
                CommandTimeout = CommandTimeout,
                KeepAlive = KeepAlive.IsPresent
            });

        if (CastAs is not null && result is not null)
        {
            result = LanguagePrimitives.ConvertTo(result, CastAs);
        }

        WriteResult(result);
    }
}

[Cmdlet(VerbsCommon.Get, "PSSqliteRow")]
public sealed class GetPSSqliteRowCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true)]
    public SQLiteDBConfig SqliteDBConfig { get; set; } = null!;

    [Parameter(Mandatory = true)]
    public string TableName { get; set; } = string.Empty;

    [Parameter]
    public IDictionary? ClauseData { get; set; }

    [Parameter]
    [ValidateNotNull]
    public SqliteConnection? SqliteConnection { get; set; }

    [Parameter]
    public SwitchParameter KeepAlive { get; set; }

    [Parameter]
    public SwitchParameter CaseSensitive { get; set; }

    [Parameter(DontShow = true)]
    [ValidateSet("DataTable", "DataReader", "DataSet", "OrderedDictionary", "PSCustomObject")]
    public string As { get; set; } = "PSCustomObject";

    protected override void ProcessRecord()
    {
        var connection = SqliteConnection ?? ConnectionFactory.Create(SqliteDBConfig.ConnectionString!);
        var result = CrudSqlBuilder.ExecuteSelect(
            SqliteDBConfig,
            TableName,
            ClauseData,
            connection,
            As,
            CaseSensitive.IsPresent,
            KeepAlive.IsPresent,
            message => WriteWarning(message + $" for table or view '{TableName}'."));

        try
        {
            WriteResult(result);
        }
        finally
        {
            CloseIfNeeded(connection, KeepAlive.IsPresent);
        }
    }
}

[Cmdlet(VerbsCommon.New, "PSSqliteRow")]
public sealed class NewPSSqliteRowCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true)]
    public SQLiteDBConfig SqliteDBConfig { get; set; } = null!;

    [Parameter(Mandatory = true)]
    public string TableName { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public IDictionary RowData { get; set; } = null!;

    [Parameter]
    public SqliteConnection? SqliteConnection { get; set; }

    [Parameter]
    public SwitchParameter KeepAlive { get; set; }

    protected override void ProcessRecord()
    {
        var connection = SqliteConnection ?? ConnectionFactory.Create(SqliteDBConfig.ConnectionString!);
        try
        {
            var result = CrudSqlBuilder.ExecuteInsert(
                SqliteDBConfig,
                TableName,
                RowData,
                connection,
                KeepAlive.IsPresent,
                message => WriteWarning(message + $" for table '{TableName}'."));
            WriteResult(result);
        }
        finally
        {
            CloseIfNeeded(connection, KeepAlive.IsPresent);
        }
    }
}

[Cmdlet(VerbsCommon.Set, "PSSqliteRow")]
public sealed class SetPSSqliteRowCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true)]
    public SQLiteDBConfig SqliteDBConfig { get; set; } = null!;

    [Parameter(Mandatory = true)]
    public string TableName { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public IDictionary RowData { get; set; } = null!;

    [Parameter]
    public IDictionary? ClauseData { get; set; }

    [Parameter]
    public SwitchParameter CaseSensitive { get; set; }

    [Parameter]
    public SqliteConnection? SqliteConnection { get; set; }

    [Parameter]
    public SwitchParameter KeepAlive { get; set; }

    [Parameter]
    [ValidateSet("UPDATE", "UPSERT")]
    public string OnConflict { get; set; } = "UPDATE";

    protected override void ProcessRecord()
    {
        var connection = SqliteConnection ?? ConnectionFactory.Create(SqliteDBConfig.ConnectionString!);
        try
        {
            CrudSqlBuilder.ExecuteUpdate(
                SqliteDBConfig,
                TableName,
                RowData,
                ClauseData,
                connection,
                CaseSensitive.IsPresent,
                KeepAlive.IsPresent,
                message => WriteWarning(message + $" for table '{TableName}'."));
        }
        finally
        {
            CloseIfNeeded(connection, KeepAlive.IsPresent);
        }
    }
}

[Cmdlet(VerbsCommon.Remove, "PSSqliteRow")]
public sealed class RemovePSSqliteRowCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true)]
    public SQLiteDBConfig SqliteDBConfig { get; set; } = null!;

    [Parameter(Mandatory = true)]
    public string TableName { get; set; } = string.Empty;

    [Parameter]
    public IDictionary? ClauseData { get; set; }

    [Parameter]
    public SwitchParameter CaseSensitive { get; set; }

    [Parameter]
    public SqliteConnection? SqliteConnection { get; set; }

    [Parameter]
    public SwitchParameter KeepAlive { get; set; }

    protected override void ProcessRecord()
    {
        var connection = SqliteConnection ?? ConnectionFactory.Create(SqliteDBConfig.ConnectionString!);
        try
        {
            CrudSqlBuilder.ExecuteDelete(
                SqliteDBConfig,
                TableName,
                ClauseData,
                connection,
                CaseSensitive.IsPresent,
                KeepAlive.IsPresent,
                message => WriteWarning(message + $" for table '{TableName}'."));
        }
        finally
        {
            CloseIfNeeded(connection, KeepAlive.IsPresent);
        }
    }
}

[Cmdlet(VerbsCommon.Get, "PSSqliteDBConfig")]
public sealed class GetPSSqliteDBConfigCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [Alias("ConfigFile")]
    public string Path { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        if (!File.Exists(Path))
        {
            throw new FileNotFoundException($"Configuration file not found: {Path}", Path);
        }

        WriteObject(SQLiteDBConfig.Load(Path, ExpandString));
    }
}

[Cmdlet(VerbsCommon.Get, "PSSqliteDBConfigFile")]
[OutputType(typeof(string))]
public sealed class GetPSSqliteDBConfigFileCommand : PSSqliteCmdlet
{
    [Parameter(DontShow = true)]
    public string? ParentModuleBaseFolder { get; set; }

    [Parameter]
    public string? ConfigFolder { get; set; }

    [Parameter]
    public string? ConfigFileName { get; set; }

    protected override void ProcessRecord()
    {
        var baseFolder = string.IsNullOrWhiteSpace(ParentModuleBaseFolder)
            ? Directory.GetCurrentDirectory()
            : PathUtilities.GetPSSqliteAbsolutePath(ParentModuleBaseFolder!, null);
        var folder = string.IsNullOrWhiteSpace(ConfigFolder)
            ? System.IO.Path.Combine(baseFolder, "config")
            : PathUtilities.GetPSSqliteAbsolutePath(ConfigFolder!, baseFolder);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Configuration folder not found: {folder}");
        }

        var moduleName = SessionState.Module?.Name;
        var pattern = string.IsNullOrWhiteSpace(ConfigFileName)
            ? $"{(string.IsNullOrWhiteSpace(moduleName) ? "*" : moduleName)}.PSSqliteConfig.y*ml"
            : ConfigFileName!;
        var wildcard = new WildcardPattern(pattern, WildcardOptions.IgnoreCase);
        var match = Directory.EnumerateFiles(folder)
            .FirstOrDefault(file => wildcard.IsMatch(System.IO.Path.GetFileName(file)));
        if (match is null)
        {
            throw new FileNotFoundException($"Configuration file not found: {System.IO.Path.Combine(folder, pattern)}");
        }

        WriteObject(match);
    }
}

[Cmdlet("Initialize", "PSSqliteDatabase")]
public sealed class InitializePSSqliteDatabaseCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, ParameterSetName = "byPath")]
    [Alias("DatabaseConfigPath")]
    public string? Path { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = "byConfig")]
    [Alias("SqliteDBConfig")]
    public SQLiteDBConfig? DatabaseConfig { get; set; }

    [Parameter]
    public DBMigrationMode MigrationMode { get; set; } = DBMigrationMode.INCREMENTAL;

    [Parameter]
    public SwitchParameter Force { get; set; }

    protected override void ProcessRecord()
    {
        var config = ParameterSetName == "byPath"
            ? SQLiteDBConfig.Load(Path!, ExpandString)
            : DatabaseConfig ?? throw new ArgumentException("DatabaseConfig is required.");
        if (config.Schema is null)
        {
            throw new ArgumentException("Invalid SQLiteDBConfig object provided.");
        }

        config.Schema.ValidateDefinition();
        DatabaseInitializer.Initialize(config, MigrationMode, Force.IsPresent);
    }
}

[Cmdlet(VerbsCommon.Close, "PSSqliteConnection")]
public sealed class ClosePSSqliteConnectionCommand : PSSqliteCmdlet
{
    protected override void ProcessRecord()
    {
        SqliteConnection.ClearAllPools();
    }
}

[Cmdlet(VerbsCommon.Get, "PSSqliteDBMetadata")]
public sealed class GetPSSqliteDBMetadataCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true)]
    public SqliteConnection SqliteConnection { get; set; } = null!;

    [Parameter]
    public string[] MetadataKey { get; set; } = new[] { "*" };

    protected override void ProcessRecord()
    {
        var connectionString = SqliteConnection.ConnectionString;
        var metadata = MetadataStore.Get(connectionString, MetadataKey);
        if (metadata is not null)
        {
            WriteObject(metadata);
        }
    }
}

[Cmdlet("Compare", "PSSqliteDBVersion")]
public sealed class ComparePSSqliteDBVersionCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public SQLiteDBConfig DatabaseConfig { get; set; } = null!;

    [Parameter]
    public string? ExpectedVersion { get; set; }

    protected override void ProcessRecord()
    {
        WriteObject(DatabaseVersion.Compare(DatabaseConfig, ExpectedVersion ?? DatabaseConfig.Version));
    }
}

[Cmdlet(VerbsCommon.Get, "ExpandedString")]
[OutputType(typeof(string))]
public sealed class GetExpandedStringCommand : PSSqliteCmdlet
{
    [Parameter(Mandatory = true)]
    public string String { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        WriteObject(ExpandString(String));
    }
}

[Cmdlet(VerbsCommon.Get, "PSSqliteAbsolutePath")]
[OutputType(typeof(string))]
public sealed class GetPSSqliteAbsolutePathCommand : PSSqliteCmdlet
{
    [Parameter]
    [AllowNull]
    public string? Path { get; set; }

    [Parameter]
    public string? RelativeTo { get; set; }

    protected override void ProcessRecord()
    {
        WriteObject(PathUtilities.GetPSSqliteAbsolutePath(Path, RelativeTo));
    }
}

public static class PathUtilities
{
    public static string GetPSSqliteAbsolutePath(string? path, string? relativeTo)
    {
        var basePath = string.IsNullOrWhiteSpace(relativeTo)
            ? Directory.GetCurrentDirectory()
            : relativeTo!;
        if (!System.IO.Path.IsPathRooted(basePath))
        {
            basePath = System.IO.Path.GetFullPath(basePath);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return System.IO.Path.GetFullPath(basePath);
        }

        return System.IO.Path.GetFullPath(
            System.IO.Path.IsPathRooted(path!)
                ? path!
                : System.IO.Path.Combine(basePath, path!));
    }
}
