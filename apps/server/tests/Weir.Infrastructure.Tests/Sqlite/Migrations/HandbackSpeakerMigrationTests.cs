using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite.Migrations;

/// <summary>
/// Migration <c>0045_handback_speaker.sql</c>: a copy keeps who spoke for it (the kind of manager, the connection, and whether the
/// message proved who sent it), worked out for words recorded before from the name the copy kept and the hand-off that recorded the
/// same word at the same moment. A word that cannot be matched to a connection keeps none, so it is never taken for another's.
/// <para>Each test builds a database at head, takes what the migration adds back out, puts the rows the way an earlier version
/// stored them, and runs the migration's own SQL.</para>
/// </summary>
public sealed class HandbackSpeakerMigrationTests : IDisposable
{
    private const string At = "2026-10-08 10:00:00.000000";

    private readonly TempDirectory _temp = new();
    private readonly SqliteDatabase _database;

    public HandbackSpeakerMigrationTests()
    {
        _database = new SqliteDatabase(_temp.Join("weir.sqlite3"));
        new SchemaMigrator(_database).EnsureAtHead();
        Execute("DROP TABLE media_manager_handoff_riders");
        Execute("ALTER TABLE handbacks DROP COLUMN outcome_source_key");
        Execute("ALTER TABLE handbacks DROP COLUMN outcome_connection_id");
        Execute("ALTER TABLE handbacks DROP COLUMN outcome_authenticated");
        Execute("DELETE FROM libraries");
        Execute("INSERT INTO libraries (id, name, media_type, watched_folder, display_order) VALUES (1, 'Movies', 'movie', '/in', 1)");
        Execute("INSERT INTO media_manager_connections (id, kind, name, base_url) VALUES (7, 'deluno', 'Deluno on the NAS', 'http://192.0.2.30:5000')");
    }

    public void Dispose()
    {
        _database.ClearPool();
        _temp.Dispose();
    }

    private void Migrate() => Execute(SchemaMigrator.ReadMigrationSql(SchemaMigrator.Migrations.Single(migration => migration.Number == 45)));

    private void Handback(string path, string? by, string? outcome, string? note = null, string? at = At) =>
        Execute(
            "INSERT INTO handbacks (library_id, relative_path, output_path, output_size, output_mtime_ns, written_at, outcome, outcome_by, outcome_at, release_note) " +
            $"VALUES (1, '{path}', '/out/{path}', 300, 5, '2026-10-07 04:00:00.000000', {Quoted(outcome)}, {Quoted(by)}, {Quoted(at)}, {Quoted(note)})");

    private void Handoff(string id, string path, string source, string outcome, long? connection, string at = At) =>
        Execute(
            "INSERT INTO media_manager_handoffs (source_key, handoff_id, library_id, relative_path, state, outcome, outcome_at, connection_id) " +
            $"VALUES ('{source}', '{id}', 1, '{path}', 'completed', '{outcome}', '{at}', {(connection is null ? "NULL" : connection.ToString())})");

    private static string Quoted(string? text) => text is null ? "NULL" : $"'{text.Replace("'", "''", StringComparison.Ordinal)}'";

    [Fact]
    public void A_word_takes_the_kind_of_manager_from_the_name_it_kept_and_the_connection_from_the_hand_off_that_recorded_it()
    {
        Handback("a.mkv", "Deluno", "not-imported");
        Handoff("h1", "a.mkv", "deluno", "not-imported", connection: 7);

        Migrate();

        Assert.Equal(("deluno", 7L, 1L), Speaker("a.mkv"));
    }

    [Fact]
    public void A_word_no_hand_off_can_be_matched_to_keeps_no_connection()
    {
        Handback("a.mkv", "Deluno", "not-imported");
        Handoff("other-moment", "a.mkv", "deluno", "not-imported", connection: 7, at: "2026-10-08 11:00:00.000000");
        Handback("b.mkv", "Radarr", "imported");

        Migrate();

        Assert.Equal(("deluno", null, 1L), Speaker("a.mkv"));
        Assert.Equal(("radarr", null, 1L), Speaker("b.mkv"));
    }

    [Fact]
    public void A_name_that_is_not_a_known_manager_and_a_copy_nobody_spoke_for_keep_no_speaker()
    {
        Handback("a.mkv", "Your media manager", "imported");
        Handback("b.mkv", null, null, at: null);

        Migrate();

        Assert.Equal((null, null, 1L), Speaker("a.mkv"));
        Assert.Equal((null, null, 1L), Speaker("b.mkv"));
    }

    [Fact]
    public void An_import_that_was_only_recorded_because_it_carried_no_secret_is_unsigned()
    {
        Handback("a.mkv", "Radarr", "imported", note: "Weir kept its copy, because Radarr's messages to Weir carry no webhook secret, so Weir cannot be sure.");
        Handback("b.mkv", "Radarr", "imported", note: "Weir removed its copy from the hand-back folder, because Radarr has the file now.");

        Migrate();

        Assert.Equal(("radarr", null, 0L), Speaker("a.mkv"));
        Assert.Equal(("radarr", null, 1L), Speaker("b.mkv"));
    }

    [Fact]
    public void The_riders_table_starts_empty_and_goes_with_its_hand_off()
    {
        Handoff("h1", "a.mkv", "deluno", "imported", connection: 7);
        Migrate();
        Execute("INSERT INTO media_manager_handoff_riders (handoff_row_id, relative_path, owner_row_id) SELECT id, 'a.mkv', id FROM media_manager_handoffs");
        Assert.Equal(1L, Count("media_manager_handoff_riders"));

        Execute("DELETE FROM media_manager_handoffs");

        Assert.Equal(0L, Count("media_manager_handoff_riders"));
    }

    private (string? Source, long? Connection, long Authenticated) Speaker(string path)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT outcome_source_key, outcome_connection_id, outcome_authenticated FROM handbacks WHERE relative_path = $path";
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt64(2));
    }

    private long Count(string table)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    private void Execute(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
