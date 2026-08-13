#Requires -Version 7.4
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '6.0.0' }

param(
    [string]$ModulePath = (Join-Path $PSScriptRoot '..\..\..\artifacts\powershell-modules\Devolutions.Ahtola.Sqlite')
)

BeforeAll {
    $script:ModulePath = [System.IO.Path]::GetFullPath($ModulePath)
    $script:ManifestPath = Join-Path $script:ModulePath 'Devolutions.Ahtola.Sqlite.psd1'
    $script:ModuleName = 'Devolutions.Ahtola.Sqlite'

    Get-Module $script:ModuleName -All | Remove-Module -Force -ErrorAction SilentlyContinue
    Import-Module $script:ManifestPath -Force -ErrorAction Stop
}

AfterAll {
    try {
        [Ahtola.Data.Sqlite.SqliteConnection]::ClearAllPools()
    }
    catch {
        # Best effort when type already unloaded.
    }

    Get-Module $script:ModuleName -All | Remove-Module -Force -ErrorAction SilentlyContinue
}

Describe 'Devolutions.Ahtola.Sqlite module packaging' {
    It 'stages a loadable manifest' {
        Test-Path -LiteralPath $script:ManifestPath | Should-BeTrue
    }

    It 'exports the expected PSSqlite cmdlets' {
        $commands = @(Get-Command -Module $script:ModuleName | Select-Object -ExpandProperty Name)
        $expected = @(
            'Close-PSSqliteConnection'
            'Compare-PSSqliteDBVersion'
            'Get-ExpandedString'
            'Get-PSSqliteAbsolutePath'
            'Get-PSSqliteDBConfig'
            'Get-PSSqliteDBConfigFile'
            'Get-PSSqliteDBMetadata'
            'Get-PSSqliteRow'
            'Initialize-PSSqliteDatabase'
            'Invoke-PSSqliteQuery'
            'New-PSSqliteConnection'
            'New-PSSqliteRow'
            'Remove-PSSqliteRow'
            'Set-PSSqliteRow'
        )

        foreach ($name in $expected) {
            $commands | Should-ContainCollection $name
        }

        $commands.Count | Should-Be $expected.Count
    }

    It 'loads the binary assembly Devolutions.Ahtola.PowerShell' {
        $assembly = [AppDomain]::CurrentDomain.GetAssemblies() |
            Where-Object { $_.GetName().Name -eq 'Devolutions.Ahtola.PowerShell' } |
            Select-Object -First 1

        $assembly | Should-NotBeNull
    }

    It 'registers module type accelerators for SQLiteDBConfig' {
        $type = [Devolutions.Ahtola.Sqlite.SqliteDBConfig]
        $type.FullName | Should-BeString 'Ahtola.PSSqlite.SQLiteDBConfig'
    }
}

Describe 'Devolutions.Ahtola.Sqlite connections and queries' {
    BeforeEach {
        $script:Connection = New-PSSqliteConnection -ConnectionString 'Data Source=:memory:'
    }

    AfterEach {
        if ($null -ne $script:Connection) {
            try {
                if ($script:Connection.State -eq [System.Data.ConnectionState]::Open) {
                    $script:Connection.Close()
                }

                $script:Connection.Dispose()
            }
            catch {
                # ignore dispose races
            }

            $script:Connection = $null
        }

        Close-PSSqliteConnection
    }

    It 'opens an in-memory connection and runs a scalar query' {
        $table = Invoke-PSSqliteQuery `
            -SqliteConnection $script:Connection `
            -CommandText 'SELECT 1 AS value;' `
            -KeepAlive `
            -As DataTable

        $table | Should-NotBeNull
        $table.Rows.Count | Should-Be 1
        [int]$table.Rows[0]['value'] | Should-Be 1
    }

    It 'binds parameters with $name style' {
        $null = Invoke-PSSqliteQuery `
            -SqliteConnection $script:Connection `
            -CommandText 'CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t(name) VALUES (''a''), (''b'');' `
            -KeepAlive `
            -As DataTable

        $table = Invoke-PSSqliteQuery `
            -SqliteConnection $script:Connection `
            -CommandText 'SELECT id, name FROM t WHERE name = $name ORDER BY id;' `
            -Parameters @{ '$name' = 'b' } `
            -KeepAlive `
            -As DataTable

        $table.Rows.Count | Should-Be 1
        [string]$table.Rows[0]['name'] | Should-BeString 'b'
    }
}

Describe 'Devolutions.Ahtola.Sqlite YAML initialize and CRUD' {
    BeforeAll {
        $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ahtola-sqlite-pester-" + [guid]::NewGuid().ToString('N'))
        $script:DbPath = Join-Path $script:TempRoot 'db'
        New-Item -ItemType Directory -Path $script:DbPath -Force | Out-Null
        $script:ConfigPath = Join-Path $script:TempRoot 'Database.yml'

        @"
DatabasePath: '$($script:DbPath.Replace('\', '/'))'
DatabaseFile: 'pester.sqlite'
Version: '1.0.0'
Schema:
  Tables:
    Items:
      Columns:
        Id:
          Type: INTEGER
          PrimaryKey: true
          AllowNull: false
        Name:
          Type: TEXT
        Qty:
          Type: INTEGER
"@ | Set-Content -Path $script:ConfigPath -Encoding utf8
    }

    AfterAll {
        try {
            [Ahtola.Data.Sqlite.SqliteConnection]::ClearAllPools()
        }
        catch {
            # Best effort when type already unloaded.
        }

        if (Test-Path -LiteralPath $script:TempRoot) {
            Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'initializes a database from YAML with CREATE migration' {
        Initialize-PSSqliteDatabase -Path $script:ConfigPath -MigrationMode CREATE

        $dbFile = Join-Path $script:DbPath 'pester.sqlite'
        Test-Path -LiteralPath $dbFile | Should-BeTrue

        $config = Get-PSSqliteDBConfig -Path $script:ConfigPath
        $compare = Compare-PSSqliteDBVersion -DatabaseConfig $config -ExpectedVersion '1.0.0'
        $compare.IsDeployed | Should-BeTrue
        $compare.CurrentVersion | Should-BeString '1.0.0'
    }

    It 'round-trips insert, select, update, and delete' {
        $config = Get-PSSqliteDBConfig -Path $script:ConfigPath
        $connection = New-PSSqliteConnection -DatabasePath $config.DatabasePath -DatabaseFile $config.DatabaseFile

        try {
            $null = New-PSSqliteRow `
                -SqliteDBConfig $config `
                -TableName Items `
                -RowData @{ Id = 1; Name = 'widget'; Qty = 3 } `
                -SqliteConnection $connection `
                -KeepAlive

            $rows = @(Get-PSSqliteRow `
                    -SqliteDBConfig $config `
                    -TableName Items `
                    -ClauseData @{ Id = 1 } `
                    -SqliteConnection $connection `
                    -KeepAlive `
                    -As OrderedDictionary)

            $rows.Count | Should-Be 1
            [string]$rows[0]['Name'] | Should-BeString 'widget'
            [int]$rows[0]['Qty'] | Should-Be 3

            Set-PSSqliteRow `
                -SqliteDBConfig $config `
                -TableName Items `
                -RowData @{ Qty = 9 } `
                -ClauseData @{ Id = 1 } `
                -SqliteConnection $connection `
                -KeepAlive

            $updated = @(Get-PSSqliteRow `
                    -SqliteDBConfig $config `
                    -TableName Items `
                    -ClauseData @{ Id = 1 } `
                    -SqliteConnection $connection `
                    -KeepAlive `
                    -As OrderedDictionary)

            [int]$updated[0]['Qty'] | Should-Be 9

            Remove-PSSqliteRow `
                -SqliteDBConfig $config `
                -TableName Items `
                -ClauseData @{ Id = 1 } `
                -SqliteConnection $connection `
                -KeepAlive

            $afterDelete = @(Get-PSSqliteRow `
                    -SqliteDBConfig $config `
                    -TableName Items `
                    -SqliteConnection $connection `
                    -KeepAlive `
                    -As OrderedDictionary)

            $afterDelete.Count | Should-Be 0
        }
        finally {
            if ($null -ne $connection) {
                if ($connection.State -eq [System.Data.ConnectionState]::Open) {
                    $connection.Close()
                }

                $connection.Dispose()
            }

            Close-PSSqliteConnection
        }
    }

    It 'reads version metadata' {
        $config = Get-PSSqliteDBConfig -Path $script:ConfigPath
        $connection = New-PSSqliteConnection -DatabasePath $config.DatabasePath -DatabaseFile $config.DatabaseFile
        try {
            $metadata = Get-PSSqliteDBMetadata -SqliteConnection $connection -MetadataKey version
            $metadata | Should-NotBeNull
            [string]$metadata['version'] | Should-BeString '1.0.0'
        }
        finally {
            if ($null -ne $connection) {
                if ($connection.State -eq [System.Data.ConnectionState]::Open) {
                    $connection.Close()
                }

                $connection.Dispose()
            }

            Close-PSSqliteConnection
        }
    }
}

Describe 'Devolutions.Ahtola.Sqlite path helpers' {
    It 'resolves relative paths with Get-PSSqliteAbsolutePath' {
        $base = [System.IO.Path]::GetTempPath().TrimEnd('\', '/')
        $resolved = Get-PSSqliteAbsolutePath -Path 'child' -RelativeTo $base
        $expected = [System.IO.Path]::GetFullPath((Join-Path $base 'child'))
        $resolved | Should-BeString $expected
    }

    It 'expands environment variables with Get-ExpandedString' {
        $expanded = Get-ExpandedString -String '$env:TEMP'
        $expanded | Should-NotBeNull
        $expanded.Length | Should-BeGreaterThan 0
    }
}
