using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Tests.Sqlite.Migrations;

/// <summary>
/// Migration <c>0044_handback_source.sql</c>: each hand-back copy carries the source it was cleaned from, so a file taken off the
/// list is still recognised when it is handed over again. A copy whose file is still listed takes the source its row recorded; one
/// whose file is gone takes it from the record of the pass that wrote it, kept in the file's history.
/// <para>Each test builds a database at head, takes the new columns back out, puts the rows the way an earlier version stored
/// them, and runs the migration's own SQL.</para>
/// </summary>
public sealed class HandbackSourceMigrationTests : IDisposable
{
    private const string Output = "/hand-back/Film (2020)/Film (2020).mkv";
    private const long Size = 4_100_222_333;
    private const long Mtime = 1_791_633_787_882_003_200;

    private readonly TempDirectory _temp = new();
    private readonly SqliteDatabase _database;

    public HandbackSourceMigrationTests()
    {
        _database = new SqliteDatabase(_temp.Join("weir.sqlite3"));
        new SchemaMigrator(_database).EnsureAtHead();
        Execute("ALTER TABLE handbacks DROP COLUMN source_size");
        Execute("ALTER TABLE handbacks DROP COLUMN source_mtime_ns");
        Execute("DELETE FROM libraries");
        Execute("INSERT INTO libraries (id, name, media_type, watched_folder, display_order) VALUES (1, 'Movies', 'movie', '/in', 1)");
    }

    public void Dispose()
    {
        _database.ClearPool();
        _temp.Dispose();
    }

    private void Migrate() => Execute(SchemaMigrator.ReadMigrationSql(SchemaMigrator.Migrations.Single(migration => migration.Number == 44)));

    private void Handback(string path = "Film.mkv", string output = Output) =>
        Execute(
            $"INSERT INTO handbacks (library_id, relative_path, output_path, output_size, output_mtime_ns, written_at) VALUES (1, '{path}', '{output}', 300, 5, '2026-10-07 04:00:00.000000')");

    private void History(string detail, string path = "Film.mkv") =>
        Execute($"INSERT INTO file_logs (library_id, relative_path, detail_json) VALUES (1, '{path}', '{detail.Replace("'", "''")}')");

    private static string PassRecord(string output = Output, string collision = "write", long size = Size, long mtime = Mtime) =>
        $$"""{"ok":true,"outcome":"live_output_written","source_fingerprint_size":{{size}},"source_fingerprint_mtime_ns":{{mtime}},"output_file":"{{output}}","output_collision_action":"{{collision}}"}""";

    [Fact]
    public void A_copy_whose_file_is_still_listed_takes_the_source_its_row_recorded()
    {
        Handback();
        Execute("INSERT INTO files (library_id, relative_path, status, processed_source_size, processed_source_mtime_ns) VALUES (1, 'Film.mkv', 'processed', 111, 222)");
        History(PassRecord());

        Migrate();

        Assert.Equal((111L, 222L), Source());
    }

    [Fact]
    public void A_copy_whose_file_was_taken_off_the_list_takes_the_source_the_pass_that_wrote_it_recorded()
    {
        Handback();
        History(PassRecord(size: 1, mtime: 2));
        History(PassRecord());

        Migrate();

        Assert.Equal((Size, Mtime), Source());
    }

    [Fact]
    public void A_listed_row_that_recorded_no_source_falls_back_to_the_history()
    {
        Handback();
        Execute("INSERT INTO files (library_id, relative_path, status) VALUES (1, 'Film.mkv', 'processed')");
        History(PassRecord());

        Migrate();

        Assert.Equal((Size, Mtime), Source());
    }

    [Fact]
    public void A_record_of_another_copy_or_of_a_pass_that_wrote_nothing_is_not_used()
    {
        Handback();
        History(PassRecord(output: "/hand-back/Other/Other.mkv"));
        History(PassRecord(collision: "skip"));
        History(PassRecord(), path: "Other.mkv");

        Migrate();

        Assert.Equal((null, null), Source());
    }

    [Fact]
    public void A_record_that_was_shortened_or_is_not_json_leaves_the_copy_as_it_is_and_does_not_stop_the_upgrade()
    {
        Handback();
        History("""{"truncated":true,"outcome":"live_output_written","detail_excerpt":"{\"ok\":true,"}""");
        History("not json at all");

        Migrate();

        Assert.Equal((null, null), Source());
    }

    [Fact]
    public void Only_one_of_the_two_is_never_kept()
    {
        Handback();
        History($$"""{"source_fingerprint_size":{{Size}},"output_file":"{{Output}}","output_collision_action":"write"}""");

        Migrate();

        Assert.Equal((null, null), Source());
    }

    private (long? Size, long? Mtime) Source()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_size, source_mtime_ns FROM handbacks";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    private void Execute(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
