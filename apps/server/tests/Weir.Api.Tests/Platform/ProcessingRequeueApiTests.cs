using System.Net;
using System.Text.Json.Nodes;
using Weir.Core.Activity;
using Weir.Infrastructure.Processing.RemuxPass;

namespace Weir.Api.Tests.Platform;

/// <summary>
/// Queueing files again from Activity: the exact files a person chose, and a finished file only while its original is
/// still in the watched folder. A file Weir already cleaned, unchanged or with its original gone, is skipped and Activity says so.
/// </summary>
public sealed class ProcessingRequeueApiTests
{
    private static async Task<(ApiTestClient Client, long LibraryId, string WatchedFolder)> SignInWithALibraryAsync(WeirTestServer server)
    {
        await TestDatabase.SeedAdminAsync(server);
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        var watched = Directory.CreateDirectory(Path.Join(server.Home, "watched")).FullName;
        var libraryId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO libraries (name, media_type, watched_folder) VALUES ('Films', 'movie', $watched) RETURNING id",
            ("$watched", watched));
        return (client, libraryId, watched);
    }

    private static Task<long> SeedFileAsync(WeirTestServer server, long libraryId, string relativePath, string status) =>
        TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, last_seen_at) VALUES ($lib, $path, $status, CURRENT_TIMESTAMP) RETURNING id",
            ("$lib", libraryId), ("$path", relativePath), ("$status", status));

    private static async Task<JsonNode> RequeueAsync(ApiTestClient client, long fileId)
    {
        using var response = await client.PostAsync(
            $"/api/v1/processing/files/{fileId}/requeue",
            new { csrf_token = await client.CsrfAsync() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestClient.Json(response);
    }

    [Fact]
    public async Task A_finished_file_whose_original_is_gone_is_not_queued_again()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        var (client, libraryId, _) = await SignInWithALibraryAsync(server);
        var fileId = await SeedFileAsync(server, libraryId, "Heat/heat.mkv", "processed");

        var body = await RequeueAsync(client, fileId);

        Assert.Equal(0, body["requeued"]!.GetValue<int>());
        Assert.Equal(
            "The original of this file is no longer in the watched folder, so Weir has nothing to process again.",
            body["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_finished_file_whose_original_is_still_there_is_queued_again()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        var (client, libraryId, watched) = await SignInWithALibraryAsync(server);
        Directory.CreateDirectory(Path.Join(watched, "Heat"));
        await File.WriteAllTextAsync(Path.Join(watched, "Heat", "heat.mkv"), "not really a film");
        var fileId = await SeedFileAsync(server, libraryId, "Heat/heat.mkv", "cancelled");

        var body = await RequeueAsync(client, fileId);

        Assert.Equal(1, body["requeued"]!.GetValue<int>());
    }

    /// <summary>A finished file as a pass leaves it: the original it cleaned, the fingerprint of that original, and the copy it handed back.</summary>
    private static async Task<(long FileId, string Source)> SeedCleanedFileAsync(WeirTestServer server, long libraryId, string watched, string relativePath)
    {
        var source = Path.Join(watched, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "not really a film");
        var output = Path.Join(server.Home, "output", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "cleaned");
        var fingerprint = SourceFiles.Fingerprint(source);
        var fileId = await TestDatabase.ScalarAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, last_seen_at, processed_source_size, processed_source_mtime_ns) " +
            "VALUES ($lib, $path, 'processed', CURRENT_TIMESTAMP, $size, $mtime) RETURNING id",
            ("$lib", libraryId), ("$path", relativePath), ("$size", fingerprint.SizeBytes), ("$mtime", fingerprint.ModifiedTimeNs));
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO handbacks (library_id, relative_path, output_path, output_size, output_mtime_ns, written_at, source_size, source_mtime_ns) " +
            "VALUES ($lib, $path, $output, 7, 0, CURRENT_TIMESTAMP, $size, $mtime)",
            ("$lib", libraryId), ("$path", relativePath), ("$output", output), ("$size", fingerprint.SizeBytes), ("$mtime", fingerprint.ModifiedTimeNs));
        return (fileId, source);
    }

    private static Task<long> ActivityCountAsync(WeirTestServer server, string eventType) =>
        TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = $type", ("$type", eventType));

    private static Task<long> RemuxJobCountAsync(WeirTestServer server) =>
        TestDatabase.ScalarAsync(server, "SELECT count(*) FROM jobs WHERE job_kind = 'processing.file.remux_pass.v1'");

    [Fact]
    public async Task A_cleaned_file_whose_original_has_not_changed_is_skipped_with_an_activity_entry_and_queues_nothing()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        var (client, libraryId, watched) = await SignInWithALibraryAsync(server);
        var (fileId, _) = await SeedCleanedFileAsync(server, libraryId, watched, "Heat/heat.mkv");

        var body = await RequeueAsync(client, fileId);

        Assert.Equal((0, 1), (body["requeued"]!.GetValue<int>(), body["skipped"]!.GetValue<int>()));
        Assert.StartsWith("Weir already cleaned this file, so it skipped it. Already done: cleaned on ", body["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, await ActivityCountAsync(server, ActivityEventTypes.ProcessingFileSkippedRepeat));
        Assert.Equal(
            "Skipped: already done (heat.mkv)",
            await TestDatabase.ScalarStringAsync(server, "SELECT title FROM activity_events WHERE event_type = $type", ("$type", ActivityEventTypes.ProcessingFileSkippedRepeat)));
        Assert.Equal(0, await RemuxJobCountAsync(server));
        Assert.Equal("processed", await TestDatabase.ScalarStringAsync(server, "SELECT status FROM files WHERE id = $id", ("$id", fileId)));
    }

    [Fact]
    public async Task A_cleaned_file_whose_original_is_gone_is_skipped_with_an_activity_entry_too()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        var (client, libraryId, watched) = await SignInWithALibraryAsync(server);
        var (fileId, source) = await SeedCleanedFileAsync(server, libraryId, watched, "Heat/heat.mkv");
        File.Delete(source);

        var body = await RequeueAsync(client, fileId);

        Assert.Equal((0, 1), (body["requeued"]!.GetValue<int>(), body["skipped"]!.GetValue<int>()));
        Assert.StartsWith("Weir already cleaned this file, so it skipped it. Already done: cleaned on ", body["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, await ActivityCountAsync(server, ActivityEventTypes.ProcessingFileSkippedRepeat));
        Assert.Equal(0, await RemuxJobCountAsync(server));
    }

    [Fact]
    public async Task A_cleaned_file_whose_original_has_changed_is_queued_again_without_a_skip_entry()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        var (client, libraryId, watched) = await SignInWithALibraryAsync(server);
        var (fileId, source) = await SeedCleanedFileAsync(server, libraryId, watched, "Heat/heat.mkv");
        await File.WriteAllTextAsync(source, "an upgraded release, a different size");

        var body = await RequeueAsync(client, fileId);

        Assert.Equal(1, body["requeued"]!.GetValue<int>());
        Assert.Equal(0, await ActivityCountAsync(server, ActivityEventTypes.ProcessingFileSkippedRepeat));
        Assert.Equal(1, await RemuxJobCountAsync(server));
    }

    [Fact]
    public async Task A_bulk_requeue_with_file_ids_queues_only_those_files()
    {
        await using var server = await ApiTestClient.StartServerAsync();
        var (client, libraryId, _) = await SignInWithALibraryAsync(server);
        var chosen = await SeedFileAsync(server, libraryId, "Heat/heat.mkv", "processing_failed");
        var other = await SeedFileAsync(server, libraryId, "Up/up.mkv", "processing_failed");

        using var response = await client.PostAsync(
            "/api/v1/processing/files/requeue",
            new { csrf_token = await client.CsrfAsync(), file_status = "processing_failed", file_ids = new[] { chosen } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await ApiTestClient.Json(response))["requeued"]!.GetValue<int>());
        Assert.Equal(
            "processing_failed",
            await TestDatabase.ScalarStringAsync(server, "SELECT status FROM files WHERE id = $id", ("$id", other)));
    }
}
