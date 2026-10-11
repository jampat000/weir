using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Weir.Infrastructure.Runtime;
using Weir.Infrastructure.Sqlite;
using Weir.Infrastructure.Tests.Platform;

namespace Weir.Infrastructure.Tests.Runtime;

/// <summary>
/// The tray has the running server save a copy of Weir's data before it applies an update, through
/// <c>update-backup-request.json</c> and <c>update-backup-result.json</c> (#951).
/// </summary>
public sealed class TrayUpdateBackupWatcherTests : IDisposable
{
    private const string Id = "0a1b2c3d";

    private readonly StoreFixture _store = new();
    private readonly CapturingLogger<TrayUpdateBackupWatcher> _log = new();
    private readonly TrayUpdateBackupWatcher _watcher;

    public TrayUpdateBackupWatcherTests()
    {
        _watcher = new TrayUpdateBackupWatcher(_store.Options, _store.Database, _store.Clock, _log);
    }

    private string RequestPath => Path.Join(_store.Options.WeirHome, TrayUpdateBackupWatcher.RequestFileName);

    private string ResultPath => Path.Join(_store.Options.WeirHome, TrayUpdateBackupWatcher.ResultFileName);

    public void Dispose()
    {
        _watcher.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _watcher.Dispose();
        _store.Dispose();
    }

    private static string Request(string id = Id, string requestedAt = "2026-01-15T10:00:00Z", string target = "1.0.0-rc.13") =>
        $"{{\"id\": \"{id}\", \"requested_at\": \"{requestedAt}\", \"target_version\": \"{target}\"}}";

    private JsonNode? ResultFor(string id)
    {
        try
        {
            using var stream = new FileStream(ResultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var node = JsonNode.Parse(stream);
            return (string?)node?["id"] == id ? node : null;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task<JsonNode> FinalResultAsync(string id = Id)
    {
        JsonNode? result = null;
        try
        {
            await Eventually.ThatAsync(() =>
            {
                result = ResultFor(id);
                return (string?)result?["state"] is "saved" or "failed";
            });
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail($"No final answer. Last seen: {result?.ToJsonString() ?? "none"}. Request still there: {File.Exists(RequestPath)}. Log: {string.Join(" | ", _log.Messages.Select(m => m.Level + " " + m.Message))}");
        }

        return result!;
    }

    [Fact]
    public async Task A_request_has_a_copy_saved_while_the_server_runs_and_is_answered_and_taken_away()
    {
        await _watcher.StartAsync(CancellationToken.None);

        await File.WriteAllTextAsync(RequestPath, Request());
        var result = await FinalResultAsync();
        await Eventually.ThatAsync(() => !File.Exists(RequestPath));

        Assert.Equal("saved", (string?)result["state"]);
        var path = (string)result["path"]!;
        Assert.StartsWith(PreUpdateBackup.FolderIn(_store.Options.BackupDir), path, StringComparison.Ordinal);
        Assert.Matches($@"weir-{SchemaMigrator.HeadRevision[..4]}-to-1\.0\.0-rc\.13-20260115T100000Z\.db$", path);
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            copy.Open();
            using var read = copy.CreateCommand();
            read.CommandText = "SELECT revision FROM schema_version";
            Assert.Equal(SchemaMigrator.HeadRevision, read.ExecuteScalar());
        }

        var latest = PreUpdateBackup.Latest(_store.Options.BackupDir);
        Assert.Equal("1.0.0-rc.13", latest?.ToVersion);
        Assert.False(string.IsNullOrEmpty(latest?.FromVersion));
    }

    [Fact]
    public async Task While_it_listens_the_server_says_it_can_take_the_request_and_stops_saying_so_when_it_stops()
    {
        var ready = Path.Join(_store.Options.WeirHome, TrayUpdateBackupWatcher.ReadyFileName);
        Assert.False(File.Exists(ready));

        await _watcher.StartAsync(CancellationToken.None);

        var marker = JsonNode.Parse(await File.ReadAllTextAsync(ready))!;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Equal(self.Id, (int)marker["pid"]!);
        Assert.InRange(
            (DateTimeOffset.Parse((string)marker["started_at"]!, System.Globalization.CultureInfo.InvariantCulture) - self.StartTime.ToUniversalTime()).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1));

        await _watcher.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(ready));
    }

    [Fact]
    public async Task A_copy_that_cannot_be_saved_is_answered_with_a_plain_reason_and_nothing_is_changed()
    {
        Directory.CreateDirectory(_store.Options.BackupDir);
        await File.WriteAllTextAsync(PreUpdateBackup.FolderIn(_store.Options.BackupDir), "in the way");
        await _watcher.StartAsync(CancellationToken.None);

        await File.WriteAllTextAsync(RequestPath, Request());
        var result = await FinalResultAsync();

        Assert.Equal("failed", (string?)result["state"]);
        Assert.Null(result["path"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)result["reason"]));
        Assert.DoesNotContain("Exception", (string)result["reason"]!, StringComparison.Ordinal);
        Assert.Equal(SchemaMigrator.HeadRevision, await ScalarTextAsync("SELECT revision FROM schema_version"));
    }

    [Fact]
    public async Task A_request_is_answered_once_however_often_the_file_is_written()
    {
        await _watcher.StartAsync(CancellationToken.None);

        await File.WriteAllTextAsync(RequestPath, Request());
        await FinalResultAsync();
        await Eventually.ThatAsync(() => !File.Exists(RequestPath));
        await File.WriteAllTextAsync(RequestPath, Request());
        await Eventually.ThatAsync(() => !File.Exists(RequestPath));

        Assert.Single(Directory.GetFiles(PreUpdateBackup.FolderIn(_store.Options.BackupDir), "*.db"));
    }

    [Fact]
    public async Task A_request_made_long_ago_is_taken_away_unanswered()
    {
        await _watcher.StartAsync(CancellationToken.None);

        await File.WriteAllTextAsync(RequestPath, Request(requestedAt: "2026-01-15T09:00:00Z"));
        await Eventually.ThatAsync(() => !File.Exists(RequestPath));

        Assert.False(File.Exists(ResultPath));
        Assert.False(Directory.Exists(PreUpdateBackup.FolderIn(_store.Options.BackupDir)));
    }

    [Fact]
    public async Task A_file_that_is_not_a_request_is_taken_away_unanswered()
    {
        await _watcher.StartAsync(CancellationToken.None);

        await File.WriteAllTextAsync(RequestPath, "not json");
        await Eventually.ThatAsync(() => !File.Exists(RequestPath));

        Assert.False(File.Exists(ResultPath));
    }

    private async Task<string?> ScalarTextAsync(string sql)
    {
        using var connection = _store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string?)await command.ExecuteScalarAsync();
    }
}
