using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Weir.Infrastructure.Sqlite;

/// <summary>One numbered SQL migration and the schema revision it leaves behind.</summary>
/// <param name="Number">Order of application.</param>
/// <param name="Revision">The revision recorded once the script has run.</param>
/// <param name="ResourceName">The embedded <c>Migrations/*.sql</c> file.</param>
public sealed record SchemaMigration(int Number, string Revision, string ResourceName);

/// <summary>Why the database could not be used.</summary>
public enum SchemaMismatchKind
{
    /// <summary>The file exists but no revision is recorded (including an empty file).</summary>
    Unversioned,

    /// <summary>A revision this build has never heard of: a newer Weir release, or a database Weir did not create.</summary>
    UnknownRevision,

    /// <summary>The version table is malformed.</summary>
    Incompatible,
}

/// <summary>The database cannot be opened by this build. The message is for the operator.</summary>
public sealed class DatabaseSchemaMismatchException : Exception
{
    public DatabaseSchemaMismatchException()
    {
    }

    public DatabaseSchemaMismatchException(string message)
        : base(message)
    {
    }

    public DatabaseSchemaMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DatabaseSchemaMismatchException(string message, SchemaMismatchKind kind)
        : base(message)
    {
        Kind = kind;
    }

    public SchemaMismatchKind Kind { get; }
}

/// <summary>What startup did to the database.</summary>
public enum SchemaStartupOutcome
{
    /// <summary>The file did not exist; the schema was created and seeded.</summary>
    Created,

    /// <summary>The database was already at this build's revision; nothing was written.</summary>
    AlreadyCurrent,

    /// <summary>
    /// The database was at an earlier revision in <see cref="SchemaMigrator.Migrations"/> (the baseline or a
    /// later migration): every migration after that revision, in order, was applied to bring it to head.
    /// </summary>
    Upgraded,
}

/// <summary>
/// Brings the database to this build's schema, or refuses to touch it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Version ledger:</b> the revision is recorded in the <c>schema_version</c> table (one <c>revision</c>
/// row). A database from before migration 39 holds that row in <c>alembic_version</c> (<c>version_num</c>);
/// reading falls back to it, and migration 39 renames it. A database holding both tables is refused. An
/// install already at head is adopted with no write at all. Each migration names the revision it leaves
/// behind.
/// </para>
/// <para>
/// <b>On startup:</b> a missing database file is created at head; a database whose recorded revision is
/// head is adopted unchanged; a database recorded at any earlier revision in <see cref="Migrations"/> (the
/// baseline or a later migration) is upgraded in place by applying every migration after it, in order, in
/// one transaction (#557), after the caller's <c>beforeUpgrade</c> step (the pre-update backup) has succeeded; anything else, including an existing file with no schema or a revision this
/// build does not list, is refused with a message and no change. Weir only opens a database that Weir
/// created.
/// </para>
/// </remarks>
public sealed class SchemaMigrator
{
    public static readonly IReadOnlyList<SchemaMigration> Migrations =
    [
        new(1, "0036_drop_pruner_tables", "Weir.Infrastructure.Migrations.0001_baseline_0036_drop_pruner_tables.sql"),
        new(2, "0037_refiner_rule_set_extra_columns", "Weir.Infrastructure.Migrations.0002_refiner_rule_set_extra_columns.sql"),
        new(3, "0038_library_mode_settings", "Weir.Infrastructure.Migrations.0003_library_mode_settings.sql"),
        new(4, "0039_library_files", "Weir.Infrastructure.Migrations.0004_library_files.sql"),
        new(5, "0040_library_swaps", "Weir.Infrastructure.Migrations.0005_library_swaps.sql"),
        new(6, "0041_removed_tracks", "Weir.Infrastructure.Migrations.0006_removed_tracks.sql"),
        new(7, "0042_library_file_facets", "Weir.Infrastructure.Migrations.0007_library_file_facets.sql"),
        new(8, "0043_remux_writer", "Weir.Infrastructure.Migrations.0008_remux_writer.sql"),
        new(9, "0044_drop_the_refiner_name", "Weir.Infrastructure.Migrations.0009_drop_the_refiner_name.sql"),
        new(10, "0045_keep_original_download", "Weir.Infrastructure.Migrations.0010_keep_original_download.sql"),
        new(11, "0046_files_at_once", "Weir.Infrastructure.Migrations.0011_files_at_once.sql"),
        new(12, "0047_library_file_marks", "Weir.Infrastructure.Migrations.0012_library_file_marks.sql"),
        new(13, "0048_cleanup_intervals", "Weir.Infrastructure.Migrations.0013_cleanup_intervals.sql"),
        new(14, "0049_link_imported_libraries", "Weir.Infrastructure.Migrations.0014_link_imported_libraries.sql"),
        new(15, "0050_handback_outcomes", "Weir.Infrastructure.Migrations.0015_handback_outcomes.sql"),
        new(16, "0051_handoff_owning_connection", "Weir.Infrastructure.Migrations.0016_handoff_owning_connection.sql"),
        new(18, "0053_query_indexes", "Weir.Infrastructure.Migrations.0018_query_indexes.sql"),
        new(19, "0054_handoff_targets", "Weir.Infrastructure.Migrations.0019_handoff_targets.sql"),
        new(20, "0055_library_file_probes", "Weir.Infrastructure.Migrations.0020_library_file_probes.sql"),
        new(21, "0056_keep_original_after_clean", "Weir.Infrastructure.Migrations.0021_keep_original_after_clean.sql"),
        new(22, "0057_downloaded_scan_setting", "Weir.Infrastructure.Migrations.0022_downloaded_scan_setting.sql"),
        new(23, "0058_download_client_connections", "Weir.Infrastructure.Migrations.0023_download_client_connections.sql"),
        new(24, "0059_file_skip_markers", "Weir.Infrastructure.Migrations.0024_file_skip_markers.sql"),
        new(25, "0060_file_content_fingerprint", "Weir.Infrastructure.Migrations.0025_file_content_fingerprint.sql"),
        new(26, "0061_user_app_theme", "Weir.Infrastructure.Migrations.0026_user_app_theme.sql"),
        new(27, "0062_library_intake_follows_performance", "Weir.Infrastructure.Migrations.0027_library_intake_follows_performance.sql"),
        new(28, "0063_connection_nickname", "Weir.Infrastructure.Migrations.0028_connection_nickname.sql"),
        new(29, "0064_workflow_readiness_and_minimum_size", "Weir.Infrastructure.Migrations.0029_workflow_readiness_and_minimum_size.sql"),
        new(30, "0065_library_clean_rules_profile", "Weir.Infrastructure.Migrations.0030_library_clean_rules_profile.sql"),
        new(31, "0066_workflow_free_space", "Weir.Infrastructure.Migrations.0031_workflow_free_space.sql"),
        new(32, "0067_failed_files_are_never_held", "Weir.Infrastructure.Migrations.0032_failed_files_are_never_held.sql"),
        new(33, "0068_remove_failed_download_cleanup_jobs", "Weir.Infrastructure.Migrations.0033_remove_failed_download_cleanup_jobs.sql"),
        new(34, "0069_file_history_orphans", "Weir.Infrastructure.Migrations.0034_file_history_orphans.sql"),
        new(35, "0070_artwork", "Weir.Infrastructure.Migrations.0035_artwork.sql"),
        new(36, "0071_connection_usage", "Weir.Infrastructure.Migrations.0036_connection_usage.sql"),
        new(37, "0072_server_starts", "Weir.Infrastructure.Migrations.0037_server_starts.sql"),
        new(38, "0073_library_change_reason", "Weir.Infrastructure.Migrations.0038_library_change_reason.sql"),
        new(39, "0074_schema_version_table", "Weir.Infrastructure.Migrations.0039_schema_version_table.sql"),
        new(40, "0075_handoff_target_copy", "Weir.Infrastructure.Migrations.0040_handoff_target_copy.sql"),
        new(41, "0076_handoff_skipped_extras", "Weir.Infrastructure.Migrations.0041_handoff_skipped_extras.sql"),
        new(42, "0077_file_skip_kind", "Weir.Infrastructure.Migrations.0042_file_skip_kind.sql"),
        new(43, "0078_activity_current_index", "Weir.Infrastructure.Migrations.0043_activity_current_index.sql"),
        new(44, "0079_handback_source", "Weir.Infrastructure.Migrations.0044_handback_source.sql"),
    ];

    /// <summary>The first migration's revision: the oldest schema this build can start from.</summary>
    public static string BaselineRevision => Migrations[0].Revision;

    public static string HeadRevision => Migrations[^1].Revision;

    private sealed record VersionLedger(string Table, string Column);

    private static readonly VersionLedger CurrentLedger = new("schema_version", "revision");

    private static readonly VersionLedger PreviousLedger = new("alembic_version", "version_num");

    private readonly SqliteDatabase _database;

    public SchemaMigrator(SqliteDatabase database)
    {
        _database = database;
    }

    /// <param name="beforeUpgrade">
    /// Called with the open connection and the recorded revision once a database is known to need migrating, before any
    /// migration runs. When it throws, the migrations are not run and the database is left as it was.
    /// </param>
    public SchemaStartupOutcome EnsureAtHead(Action<SqliteConnection, string>? beforeUpgrade = null)
    {
        // Only a missing file is a new install. A file that exists but holds no schema was made by
        // something else (or by a start that failed half-way), so it is refused as unversioned without
        // being opened (opening would switch it to WAL).
        var existed = File.Exists(_database.DatabasePath);
        if (existed && new FileInfo(_database.DatabasePath).Length == 0)
        {
            throw UnversionedError();
        }

        using var connection = _database.Open();
        var userObjects = ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'");
        if (userObjects == 0)
        {
            if (existed)
            {
                throw UnversionedError();
            }

            ApplyAll(connection);
            return SchemaStartupOutcome.Created;
        }

        var current = ReadRecordedRevision(connection);
        if (current == HeadRevision)
        {
            return SchemaStartupOutcome.AlreadyCurrent;
        }

        var currentIndex = Migrations.ToList().FindIndex(m => m.Revision == current);
        if (currentIndex >= 0)
        {
            beforeUpgrade?.Invoke(connection, current!);
            ApplyRange(connection, Migrations.Skip(currentIndex + 1), HeadRevision);
            return SchemaStartupOutcome.Upgraded;
        }

        throw new DatabaseSchemaMismatchException(
            $"Database revision {Quote(current!)} is not recognized by this Weir build " +
            $"(expected head {Quote(HeadRevision)}). The database may come from a newer release, " +
            "or it was not created by Weir. Upgrade the application, restore a backup that matches this version, " +
            "or move this file aside so Weir creates a new one.",
            SchemaMismatchKind.UnknownRevision);
    }

    internal static string ReadMigrationSql(SchemaMigration migration)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(migration.ResourceName)
            ?? throw new InvalidOperationException($"Migration resource {migration.ResourceName} is missing from this build.");
        using var reader = new StreamReader(stream);
        // Line endings are normalized so sqlite_master holds the same schema text as existing databases on every platform.
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a fresh database at exactly <see cref="BaselineRevision"/>, ignoring every later migration.
    /// Test-only: the migration tests start from this oldest schema, write rows in its shape, and upgrade.
    /// </summary>
    public SchemaStartupOutcome EnsureAtBaseline()
    {
        using var connection = _database.Open();
        ApplyRange(connection, Migrations.Take(1), BaselineRevision);
        return SchemaStartupOutcome.Created;
    }

    private static void ApplyAll(SqliteConnection connection) => ApplyRange(connection, Migrations.OrderBy(m => m.Number), HeadRevision);

    /// <summary>
    /// Runs <paramref name="migrations"/> in one transaction and records <paramref name="revision"/>.
    /// <para>
    /// Foreign keys are turned off <b>before</b> the transaction opens, which is SQLite's documented
    /// procedure for schema changes. <c>PRAGMA foreign_keys</c> is a no-op inside a transaction, and
    /// <c>defer_foreign_keys</c> only defers constraint <i>violations</i> to commit: neither stops
    /// <c>ON DELETE CASCADE</c>, which fires immediately, so a migration that rebuilds a table by dropping it
    /// would silently delete its children's rows, transitively (#578: dropping <c>refiner_files</c> would
    /// empty <c>library_files</c> and, through it, <c>library_file_facets</c>).
    /// </para>
    /// <para>
    /// A <c>foreign_key_check</c> runs before the commit so turning enforcement off cannot hide a migration
    /// that genuinely left the database inconsistent.
    /// </para>
    /// </summary>
    private static void ApplyRange(SqliteConnection connection, IEnumerable<SchemaMigration> migrations, string revision)
    {
        Pragma(connection, "PRAGMA foreign_keys=off");
        try
        {
            ApplyRangeCore(connection, migrations, revision);
        }
        finally
        {
            Pragma(connection, "PRAGMA foreign_keys=on");
        }
    }

    private static void Pragma(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ApplyRangeCore(SqliteConnection connection, IEnumerable<SchemaMigration> migrations, string revision)
    {
        using var transaction = connection.BeginTransaction();

        foreach (var migration in migrations)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = ReadMigrationSql(migration);
            command.ExecuteNonQuery();
        }

        using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            var ledger = FindLedger(connection)
                ?? throw new InvalidOperationException("No schema version table exists after the migrations ran.");
            record.CommandText = $"DELETE FROM {ledger.Table}; INSERT INTO {ledger.Table} ({ledger.Column}) VALUES ($revision);";
            record.Parameters.AddWithValue("$revision", revision);
            record.ExecuteNonQuery();
        }

        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "PRAGMA foreign_key_check";
            using var violations = check.ExecuteReader();
            if (violations.Read())
            {
                throw new InvalidOperationException(
                    $"Migration to {revision} left a foreign key violation in table '{violations.GetValue(0)}'.");
            }
        }

        transaction.Commit();
    }

    internal static string? ReadRecordedRevision(SqliteConnection connection)
    {
        var ledger = FindLedger(connection);
        var revisions = new List<string>();
        if (ledger is not null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {ledger.Column} FROM {ledger.Table}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                revisions.Add(reader.GetString(0));
            }
        }

        return revisions.Count switch
        {
            0 => throw UnversionedError(),
            1 => revisions[0],
            _ => throw new DatabaseSchemaMismatchException(
                $"Database records several schema revisions ({string.Join(", ", revisions.Select(Quote))}); " +
                $"this build requires exactly {Quote(HeadRevision)}. Restore a backup that matches this version.",
                SchemaMismatchKind.Incompatible),
        };
    }

    /// <summary>
    /// The table that records the revision: <c>schema_version</c>, or <c>alembic_version</c> in a database from
    /// before migration 39. Null when neither exists. A database holding both is malformed and is refused.
    /// </summary>
    private static VersionLedger? FindLedger(SqliteConnection connection)
    {
        var hasCurrent = TableExists(connection, CurrentLedger.Table);
        var hasPrevious = TableExists(connection, PreviousLedger.Table);
        if (hasCurrent && hasPrevious)
        {
            throw new DatabaseSchemaMismatchException(
                $"Database holds both a '{CurrentLedger.Table}' and an '{PreviousLedger.Table}' table, so its schema revision is ambiguous. " +
                "Weir changed nothing. Restore a backup that matches this version, " +
                "or move this file aside so Weir creates a new one.",
                SchemaMismatchKind.Incompatible);
        }

        if (hasCurrent)
        {
            return CurrentLedger;
        }

        return hasPrevious ? PreviousLedger : null;
    }

    private static bool TableExists(SqliteConnection connection, string name) =>
        ScalarLong(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{name}'") > 0;

    /// <summary>The refusal for a database with no recorded revision, with the operator's options.</summary>
    private static DatabaseSchemaMismatchException UnversionedError() => new(
        "No schema revision is recorded for this database (Weir has not set it up). " +
        $"This build requires schema revision {Quote(HeadRevision)}. " +
        "Weir only opens a database that Weir created: point WEIR_DB_PATH at the right file, " +
        "restore a backup, or move this file aside so Weir creates a new one.",
        SchemaMismatchKind.Unversioned);

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A revision in single quotes, as the operator messages print it.</summary>
    private static string Quote(string value) => $"'{value}'";
}
