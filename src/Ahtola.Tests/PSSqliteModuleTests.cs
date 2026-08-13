using System.Collections;
using System.Collections.Specialized;
using System.Data;
using AwesomeAssertions;
using Ahtola.Data.Sqlite;
using Ahtola.PSSqlite;

namespace Ahtola.Tests;

/// <summary>
/// Smoke coverage for the Ahtola-backed clone of the synedgy.PSSqlite C# module.
/// Exercises library services directly (not the PowerShell host).
/// </summary>
public sealed class PSSqliteModuleTests
{
    [Test]
    public void ConnectionFactory_Creates_Openable_Memory_Connection()
    {
        using var connection = ConnectionFactory.Create("Data Source=:memory:");
        connection.Open();
        connection.State.Should().Be(ConnectionState.Open);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        Convert.ToInt64(command.ExecuteScalar()).Should().Be(1);
    }

    [Test]
    public void QueryExecutor_Returns_DataTable_And_Parameters()
    {
        using var connection = ConnectionFactory.Create("Data Source=:memory:");
        connection.Open();

        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t(name) VALUES ('a'), ('b');";
            setup.ExecuteNonQuery();
        }

        var table = QueryExecutor.Execute(
            connection,
            "SELECT id, name FROM t WHERE name = $name ORDER BY id;",
            new Hashtable { ["$name"] = "b" },
            new QueryOptions { OutputFormat = "DataTable", KeepAlive = true }) as DataTable;

        table.Should().NotBeNull();
        table!.Rows.Count.Should().Be(1);
        table.Rows[0]["name"].Should().Be("b");
    }

    [Test]
    public void Initialize_And_Crud_RoundTrip_From_Yaml_Config()
    {
        var root = Path.Combine(Path.GetTempPath(), "ahtola-pssqlite-" + Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(root, "db");
        var configPath = Path.Combine(root, "Pester.PSSqliteConfig.yml");
        Directory.CreateDirectory(dbPath);

        try
        {
            File.WriteAllText(configPath, $"""
                DatabasePath: '{dbPath.Replace('\\', '/')}'
                DatabaseFile: 'Pester.sqlite'
                Version: '1.2.3'
                Schema:
                  Tables:
                    Cars:
                      Columns:
                        Id:
                          Type: INTEGER
                          PrimaryKey: true
                          AllowNull: false
                        Make:
                          Type: TEXT
                        Colour:
                          Type: TEXT
                        Year:
                          Type: INTEGER
                """);

            var config = SQLiteDBConfig.Load(configPath);
            config.Schema.Should().NotBeNull();
            config.Schema!.ValidateDefinition();

            DatabaseInitializer.Initialize(config, DBMigrationMode.CREATE, force: false);

            var dbFile = Path.Combine(config.DatabasePath!, config.DatabaseFile!);
            File.Exists(dbFile).Should().BeTrue();

            var compare = DatabaseVersion.Compare(config, "1.2.3");
            compare.IsDeployed.Should().BeTrue();
            compare.CurrentVersion.Should().Be("1.2.3");
            compare.ExpectedVersion.Should().Be("1.2.3");

            using var connection = ConnectionFactory.Create(config.ConnectionString!);
            connection.Open();

            var inserted = CrudSqlBuilder.ExecuteInsert(
                config,
                "Cars",
                new Hashtable
                {
                    ["Id"] = 1,
                    ["Make"] = "DeLorean",
                    ["Colour"] = "Silver",
                    ["Year"] = 1981
                },
                connection,
                keepAlive: true,
                warning: _ => { });

            inserted.Should().NotBeNull();

            var selected = CrudSqlBuilder.ExecuteSelect(
                config,
                "Cars",
                new Hashtable { ["Make"] = "DeLorean" },
                connection,
                outputFormat: "OrderedDictionary",
                caseSensitive: false,
                keepAlive: true,
                warning: _ => { });

            selected.Should().NotBeNull();
            var rows = ((IEnumerable)selected!).Cast<object>().ToArray();
            rows.Length.Should().Be(1);
            var row = (OrderedDictionary)rows[0];
            row["Make"].Should().Be("DeLorean");
            Convert.ToInt64(row["Year"]).Should().Be(1981);

            CrudSqlBuilder.ExecuteUpdate(
                config,
                "Cars",
                new Hashtable { ["Colour"] = "Stainless" },
                new Hashtable { ["Id"] = 1 },
                connection,
                caseSensitive: false,
                keepAlive: true,
                warning: _ => { });

            var metadata = MetadataStore.Get(config.ConnectionString!, new[] { "version" });
            metadata.Should().NotBeNull();
            metadata!["version"].Should().Be("1.2.3");

            CrudSqlBuilder.ExecuteDelete(
                config,
                "Cars",
                new Hashtable { ["Id"] = 1 },
                connection,
                caseSensitive: false,
                keepAlive: true,
                warning: _ => { });

            var afterDelete = CrudSqlBuilder.ExecuteSelect(
                config,
                "Cars",
                null,
                connection,
                outputFormat: "DataTable",
                caseSensitive: false,
                keepAlive: true,
                warning: _ => { }) as DataTable;

            afterDelete.Should().NotBeNull();
            afterDelete!.Rows.Count.Should().Be(0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public void PathUtilities_Resolves_Relative_Paths()
    {
        var absolute = PathUtilities.GetPSSqliteAbsolutePath("child", Path.GetTempPath());
        absolute.Should().Be(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "child")));
    }
}
