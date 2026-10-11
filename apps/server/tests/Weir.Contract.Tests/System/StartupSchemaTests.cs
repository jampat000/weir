using System.Net;
using Microsoft.Data.Sqlite;
using Weir.Contract.Tests.Harness;

namespace Weir.Contract.Tests.SystemArea;

/// <summary>Schema checks at startup: the migrated schema's tables, and a server that refuses a database it did not set up.</summary>
[ContractArea("system")]
public sealed class StartupSchemaTests(StartupSchemaTests.HeadSchemaFixture fixture) : IClassFixture<StartupSchemaTests.HeadSchemaFixture>
{
    /// <summary>The last revision that recorded itself in the alembic_version table.</summary>
    private const string PreviousRevision = "0073_library_change_reason";

    /// <summary>A server on its own data folder, with the tables and columns its database was brought to read once.</summary>
    public sealed class HeadSchemaFixture : ServerFixture, IAsyncLifetime
    {
        public IReadOnlyDictionary<string, HashSet<string>> Schema { get; private set; } = new Dictionary<string, HashSet<string>>();

        public new async Task InitializeAsync()
        {
            await base.InitializeAsync();
            await using var stopped = await Server.StopForDatabaseAsync();
            Schema = SchemaOf(stopped.Connection);
        }
    }

    /// <summary>A server restarted on its own, already current, database starts and leaves the revision alone.</summary>
    [Fact]
    public async Task Ensure_database_at_application_head_ok_on_migrated_db()
    {
        string? before;
        await using (var stopped = await fixture.Server.StopForDatabaseAsync())
        {
            before = Revision(stopped.Connection);
        }

        Assert.False(string.IsNullOrEmpty(before));
        using (var client = fixture.Server.CreateClient())
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).Status);
        }

        await using (var stopped = await fixture.Server.StopForDatabaseAsync())
        {
            Assert.Equal(before, Revision(stopped.Connection));
        }
    }

    /// <summary>
    /// The libraries are the only store for these settings (#363). Asserted rather than assumed: a table recreated
    /// by a later migration would bring back two stores that drift apart.
    /// </summary>
    [Fact]
    public void Head_schema_no_longer_carries_the_processing_singleton_settings_tables()
    {
        Assert.DoesNotContain("processing_path_settings", fixture.Schema.Keys);
        Assert.DoesNotContain("processing_remux_rules_settings", fixture.Schema.Keys);
    }

    [Fact]
    public void Head_schema_carries_the_libraries_that_replaced_them()
    {
        Assert.Contains("libraries", fixture.Schema.Keys);
        foreach (var name in new[] { "watched_folder", "work_folder", "output_folder", "scan_interval_seconds" })
        {
            Assert.Contains(name, fixture.Schema["libraries"]);
        }

        Assert.Contains("rule_sets", fixture.Schema.Keys);
        Assert.Contains("primary_audio_lang", fixture.Schema["rule_sets"]);
        Assert.Contains("subtitle_mode", fixture.Schema["rule_sets"]);
    }

    [Fact]
    public void Head_schema_includes_suite_settings_table()
    {
        Assert.Contains("suite_settings", fixture.Schema.Keys);
        var names = fixture.Schema["suite_settings"];
        Assert.Contains("product_display_name", names);
        Assert.Contains("signed_in_home_notice", names);
        Assert.DoesNotContain("application_logs_enabled", names);
        Assert.Contains("configuration_backup_enabled", names);
        Assert.Contains("configuration_backup_interval_hours", names);
        Assert.Contains("configuration_backup_preferred_time", names);
    }

    [Fact]
    public void Head_schema_includes_arr_library_operator_settings_table()
    {
        Assert.Contains("arr_library_operator_settings", fixture.Schema.Keys);
        var names = fixture.Schema["arr_library_operator_settings"];
        Assert.Contains("sonarr_missing_search_enabled", names);
        Assert.Contains("radarr_upgrade_search_schedule_interval_seconds", names);
    }

    /// <summary>A database whose revision is recorded in the old table is upgraded on start, and carries only schema_version afterwards.</summary>
    [Fact]
    public async Task A_database_recording_its_revision_in_the_old_table_is_upgraded_on_start()
    {
        await using var server = await WeirServer.StartNewAsync();
        string? head;
        await using (var stopped = await server.StopForDatabaseAsync(restart: false))
        {
            head = Revision(stopped.Connection);
            MakeOlder(stopped.Connection);
            Assert.DoesNotContain("schema_version", Tables(stopped.Connection));
        }

        await server.RestartAsync();
        using (var client = server.CreateClient())
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).Status);
        }

        await using var upgraded = await server.StopForDatabaseAsync(restart: false);
        Assert.Equal(head, Revision(upgraded.Connection));
        Assert.DoesNotContain("alembic_version", Tables(upgraded.Connection));
        Assert.Single(SeedSql.Rows(upgraded.Connection, "SELECT revision FROM schema_version"));
    }

    /// <summary>The update keeps a copy of the data as it was, which opens at the old revision, and the log says where it is.</summary>
    [Fact]
    public async Task An_upgrade_first_saves_a_copy_of_the_database_at_its_old_revision()
    {
        await using var server = await WeirServer.StartNewAsync();
        await using (var stopped = await server.StopForDatabaseAsync(restart: false))
        {
            MakeOlder(stopped.Connection);
        }

        await server.RestartAsync();

        var copy = Assert.Single(Directory.GetFiles(Path.Combine(server.Home, "backups", "pre-update"), "weir-0073-to-*.db"));
        using var opened = new SqliteConnection($"Data Source={copy};Mode=ReadOnly;Pooling=False");
        opened.Open();
        Assert.Equal(PreviousRevision, (string?)SeedSql.Scalar(opened, "SELECT version_num FROM alembic_version"));
        Assert.Contains("Before updating, Weir saved a copy of its data", server.LogText(), StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(copy), server.LogText(), StringComparison.Ordinal);
    }

    /// <summary>A copy that cannot be saved stops the start before any migration, with the reason, and the next start can still update.</summary>
    [Fact]
    public async Task An_upgrade_that_cannot_save_its_copy_does_not_start_and_changes_nothing()
    {
        await using var server = await WeirServer.StartNewAsync();
        await using (var stopped = await server.StopForDatabaseAsync(restart: false))
        {
            MakeOlder(stopped.Connection);
        }

        var inTheWay = Path.Combine(server.Home, "backups", "pre-update");
        Directory.CreateDirectory(Path.GetDirectoryName(inTheWay)!);
        await File.WriteAllTextAsync(inTheWay, "in the way");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => server.RestartAsync());
        Assert.Contains("Weir couldn't save a copy of its data before updating, so it didn't change anything:", error.Message, StringComparison.Ordinal);
        Assert.False(server.IsRunning);

        // The reason is left for the tray, which shows it instead of a bare "couldn't start".
        var note = (await File.ReadAllTextAsync(Path.Combine(server.Home, "startup-error.txt"))).Split('\n', 2);
        Assert.Equal("Couldn't save a copy of its data before updating", note[0]);
        Assert.StartsWith("Weir couldn't save a copy of its data before updating, so it didn't change anything:", note[1], StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(server.Home, "startup-progress.txt")));

        await using (var after = await server.StopForDatabaseAsync(restart: false))
        {
            Assert.Equal(PreviousRevision, (string?)SeedSql.Scalar(after.Connection, "SELECT version_num FROM alembic_version"));
            Assert.DoesNotContain("schema_version", Tables(after.Connection));
        }

        File.Delete(inTheWay);
        await server.RestartAsync();
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).Status);
        Assert.False(File.Exists(Path.Combine(server.Home, "startup-error.txt")));
    }

    /// <summary>A revision this build has never heard of is refused whichever table holds it, and the file is left as it was.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_database_at_an_unknown_revision_is_refused_and_left_unchanged(bool inOldTable)
    {
        await using var server = await WeirServer.StartNewAsync();
        await using (var stopped = await server.StopForDatabaseAsync(restart: false))
        {
            if (inOldTable)
            {
                RecordRevisionInOldTable(stopped.Connection, "0999_from_the_future");
            }
            else
            {
                SeedSql.Execute(stopped.Connection, "UPDATE schema_version SET revision = '0999_from_the_future'");
            }
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => server.RestartAsync());
        Assert.Contains("0999_from_the_future", error.Message, StringComparison.Ordinal);

        await using var after = await server.StopForDatabaseAsync(restart: false);
        var ledger = inOldTable ? "alembic_version" : "schema_version";
        Assert.Contains(ledger, Tables(after.Connection));
        Assert.Equal("0999_from_the_future", (string?)SeedSql.Scalar(after.Connection, $"SELECT * FROM {ledger}"));
    }

    /// <summary>Both version tables at once leave the revision ambiguous: the server refuses the database and changes nothing.</summary>
    [Fact]
    public async Task A_database_with_both_version_tables_is_refused_and_left_unchanged()
    {
        await using var server = await WeirServer.StartNewAsync();
        await using (var stopped = await server.StopForDatabaseAsync(restart: false))
        {
            SeedSql.Execute(stopped.Connection, "CREATE TABLE alembic_version (version_num VARCHAR(32) NOT NULL)");
            SeedSql.Execute(stopped.Connection, "INSERT INTO alembic_version VALUES ($revision)", ("$revision", PreviousRevision));
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => server.RestartAsync());
        Assert.Contains("both", error.Message, StringComparison.Ordinal);

        await using var after = await server.StopForDatabaseAsync(restart: false);
        Assert.Contains("alembic_version", Tables(after.Connection));
        Assert.Contains("schema_version", Tables(after.Connection));
        Assert.Equal(PreviousRevision, (string?)SeedSql.Scalar(after.Connection, "SELECT version_num FROM alembic_version"));
    }

    /// <summary>A data folder whose database Weir never set up: the server refuses to start and changes nothing.</summary>
    [Fact]
    public async Task Api_startup_fails_without_migrations()
    {
        await using var server = await WeirServer.StartNewAsync();
        await server.StopAsync();
        await StoppedDatabase.WaitForReleaseAsync(server.DatabasePath);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(server.DatabasePath + suffix);
        }

        // An empty, unversioned SQLite file.
        using (var empty = new SqliteConnection($"Data Source={server.DatabasePath};Pooling=False"))
        {
            empty.Open();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.RestartAsync());
        Assert.False(server.IsRunning);

        await using var stopped = await server.StopForDatabaseAsync(restart: false);
        Assert.Empty(SeedSql.Rows(stopped.Connection, "SELECT name FROM sqlite_master WHERE type = 'table'"));
    }

    private static Dictionary<string, HashSet<string>> SchemaOf(SqliteConnection connection)
    {
        var tables = SeedSql.Rows(connection, "SELECT name FROM sqlite_master WHERE type = 'table'")
            .Select(row => (string)row["name"]!);
        return tables.ToDictionary(
            table => table,
            table => SeedSql.Rows(connection, $"PRAGMA table_info(\"{table}\")").Select(column => (string)column["name"]!).ToHashSet());
    }

    private static string? Revision(SqliteConnection connection) =>
        (string?)SeedSql.Scalar(connection, "SELECT revision FROM schema_version");

    private static List<string> Tables(SqliteConnection connection) =>
        SeedSql.Rows(connection, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")
            .Select(row => (string)row["name"]!)
            .ToList();

    /// <summary>Takes a database at head back to <see cref="PreviousRevision"/>, recorded the way that release recorded it.</summary>
    private static void MakeOlder(SqliteConnection connection)
    {
        RecordRevisionInOldTable(connection, PreviousRevision);
        // A real database at that revision has none of what later migrations added; take those back out, or the upgrade
        // would add them a second time.
        SeedSql.Execute(connection, "ALTER TABLE media_manager_handoff_targets DROP COLUMN output_written_at");
        SeedSql.Execute(connection, "ALTER TABLE files DROP COLUMN skip_kind");
        SeedSql.Execute(connection, "DROP INDEX ix_activity_events_current");
        SeedSql.Execute(connection, "ALTER TABLE handbacks DROP COLUMN source_size");
        SeedSql.Execute(connection, "ALTER TABLE handbacks DROP COLUMN source_mtime_ns");
        SeedSql.Execute(connection, "ALTER TABLE handbacks DROP COLUMN outcome_source_key");
        SeedSql.Execute(connection, "ALTER TABLE handbacks DROP COLUMN outcome_connection_id");
        SeedSql.Execute(connection, "ALTER TABLE handbacks DROP COLUMN outcome_authenticated");
    }

    /// <summary>
    /// Puts the version record back the way a release before the schema_version table wrote it. Migration 39 only
    /// renames that table, so the rest of the schema at head is exactly the schema of <see cref="PreviousRevision"/>.
    /// </summary>
    private static void RecordRevisionInOldTable(SqliteConnection connection, string revision)
    {
        SeedSql.Execute(connection, "ALTER TABLE schema_version RENAME COLUMN revision TO version_num");
        SeedSql.Execute(connection, "ALTER TABLE schema_version RENAME TO alembic_version");
        SeedSql.Execute(connection, "UPDATE alembic_version SET version_num = $revision", ("$revision", revision));
    }
}
