using System.Net;
using Weir.Api.Tests.Platform;
using Weir.Infrastructure.Tests;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.MediaManagers;

/// <summary>
/// #652 over HTTP: Sonarr's and Radarr's import webhook, and the hand-off outcome Deluno sends, record what became of a
/// file Weir handed back, and release Weir's copy only while it is exactly the file Weir wrote. Simulated media only.
/// </summary>
public sealed partial class HandbackOutcomeApiTests : IDisposable
{
    private const string WebhookSecret = "s3cret";

    private static readonly Dictionary<string, string> SecretHeader = new() { ["X-Webhook-Secret"] = WebhookSecret };

    private readonly string _root = Path.Join(Path.GetTempPath(), "weir-handback-" + Guid.NewGuid().ToString("N"));

    public HandbackOutcomeApiTests()
    {
        Directory.CreateDirectory(Watched);
        Directory.CreateDirectory(Output);
    }

    private string Watched => Path.Join(_root, "downloads");

    private string Output => Path.Join(_root, "hand-back");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Task<WeirTestServer> StartAsync(string webhookSecret = WebhookSecret) =>
        WeirTestServer.StartAsync([("WEIR_SESSION_SECRET", Secret), ("WEIR_PROCESSING_WORKER_COUNT", "0"), ("WEIR_MEDIA_MANAGER_WEBHOOK_SECRET", webhookSecret)]);

    /// <summary>The seeded Movies library, pointed at this test's folders.</summary>
    private async Task<long> MoviesAsync(WeirTestServer server)
    {
        await TestDatabase.ExecuteAsync(
            server, "UPDATE libraries SET watched_folder = $w, output_folder = $o WHERE media_type = 'movie'", ("$w", Watched), ("$o", Output));
        return await TestDatabase.ScalarAsync(server, "SELECT id FROM libraries WHERE media_type = 'movie' ORDER BY id LIMIT 1");
    }

    /// <summary>A processed file whose cleaned copy Weir wrote into the output folder, recorded as a pass records it.</summary>
    private async Task<string> HandedBackAsync(WeirTestServer server, long library, string relative)
    {
        var source = Path.Join(Watched, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "the original download");
        var copy = Path.Join(Output, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        await File.WriteAllTextAsync(copy, "the cleaned copy");
        var info = new FileInfo(copy);
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, status_reason) VALUES ($l, $p, 'processed', 'Finished processing this file.') " +
            "ON CONFLICT (library_id, relative_path) DO UPDATE SET status = 'processed'",
            ("$l", library),
            ("$p", relative));
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO handbacks (library_id, relative_path, output_path, output_size, output_mtime_ns, written_at) VALUES ($l, $p, $o, $s, $m, '2026-09-20 10:00:00.000000')",
            ("$l", library),
            ("$p", relative),
            ("$o", copy),
            ("$s", info.Length),
            ("$m", (info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100));
        return copy;
    }

    private static object RadarrImport(string sourcePath, string? downloadId = null) => new
    {
        eventType = "Download",
        movie = new { id = 7, title = "The Long Tide", year = 2024 },
        movieFile = new { id = 12, path = "/movies/The Long Tide (2024)/The Long Tide (2024).mkv", sourcePath },
        downloadClient = "qBittorrent",
        downloadId = downloadId ?? "A1B2C3",
    };

    // --- Sonarr and Radarr ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_radarr_import_of_a_handed_back_copy_is_recorded_and_the_exact_copy_is_released()
    {
        await using var server = await StartAsync();
        await TestDatabase.SeedAdminAsync(server);
        var library = await MoviesAsync(server);
        var copy = await HandedBackAsync(server, library, "The.Long.Tide.2024/The.Long.Tide.2024.mkv");
        var manager = new ApiTestClient(server);

        // Radarr sees Weir's output folder under its own name (a remote path mapping), so only the end of the path matches.
        using var response = await manager.PostAsync(
            "/api/v1/intake/webhook/radarr", RadarrImport("/mnt/weir-out/The.Long.Tide.2024/The.Long.Tide.2024.mkv"), SecretHeader);

        Assert.Equal(
            """{"status":"ok","source":"radarr","event":"imported","matched":true,"released":true,"message":"Weir removed its copy from the hand-back folder, because Radarr has the file now."}""",
            await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(Path.Join(Watched, "The.Long.Tide.2024", "The.Long.Tide.2024.mkv")));
        Assert.Equal(1, await TestDatabase.ScalarAsync(
            server,
            "SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND outcome_by = 'Radarr' AND released_at IS NOT NULL " +
            "AND imported_path = '/movies/The Long Tide (2024)/The Long Tide (2024).mkv'"));
        Assert.Equal(1, await TestDatabase.ScalarAsync(
            server, "SELECT count(*) FROM activity_events WHERE event_type = 'processing.handback_outcome' AND title = 'Radarr imported The.Long.Tide.2024.mkv'"));

        // Activity shows it.
        var client = new ApiTestClient(server);
        await client.SignInAsync();
        using var files = await client.GetAsync("/api/v1/processing/files");
        var handback = (await Json(files))["files"]!.AsArray().Single()!["handback"]!;
        Assert.Equal(("imported", "Radarr"), (handback["outcome"]!.GetValue<string>(), handback["outcome_by"]!.GetValue<string>()));
        Assert.NotNull(handback["released_at"]!.GetValue<string>());

        // Radarr sending the same message again changes nothing.
        using var again = await manager.PostAsync(
            "/api/v1/intake/webhook/radarr", RadarrImport("/mnt/weir-out/The.Long.Tide.2024/The.Long.Tide.2024.mkv"), SecretHeader);
        Assert.Contains("\"released\":true", await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'processing.handback_outcome'"));
    }

    [Fact]
    public async Task A_copy_that_changed_since_Weir_wrote_it_is_recorded_but_kept()
    {
        await using var server = await StartAsync();
        var library = await MoviesAsync(server);
        var copy = await HandedBackAsync(server, library, "Film/film.mkv");
        await File.AppendAllTextAsync(copy, ", and then someone else wrote to it");

        using var response = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/radarr", RadarrImport(copy), SecretHeader);

        Assert.Contains("\"matched\":true,\"released\":false", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(File.Exists(copy));
        Assert.Equal("Weir's copy has changed since Weir wrote it, so Weir left it alone.", await TestDatabase.ScalarStringAsync(server, "SELECT release_note FROM handbacks"));
    }

    [Fact]
    public async Task A_moved_copy_records_the_import_and_removes_nothing()
    {
        await using var server = await StartAsync();
        var library = await MoviesAsync(server);
        var copy = await HandedBackAsync(server, library, "Film/film.mkv");
        var neighbour = Path.Join(Output, "Film", "film.nfo");
        await File.WriteAllTextAsync(neighbour, "a sidecar Weir never touches");
        File.Delete(copy);

        using var response = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/radarr", RadarrImport(copy), SecretHeader);

        Assert.Equal(
            """{"status":"ok","source":"radarr","event":"imported","matched":true,"released":false,"message":"Radarr moved Weir's copy into its library, so there was nothing for Weir to remove."}""",
            await response.Content.ReadAsStringAsync());
        Assert.True(File.Exists(neighbour));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND released_at IS NULL AND settled_at IS NOT NULL"));
    }

    [Fact]
    public async Task A_sonarr_import_of_a_file_Weir_never_handed_back_changes_nothing()
    {
        await using var server = await StartAsync();
        var library = await MoviesAsync(server);
        var copy = await HandedBackAsync(server, library, "Film/film.mkv");
        var sonarr = new
        {
            eventType = "Download",
            series = new { id = 3, title = "Paper Lanterns" },
            episodes = new[] { new { id = 41, seasonNumber = 1, episodeNumber = 2, title = "The Second" } },
            episodeFile = new { path = "/tv/Paper Lanterns/Season 01/Paper Lanterns - S01E02.mkv", sourcePath = "/downloads/Paper.Lanterns.S01E02/Paper.Lanterns.S01E02.mkv" },
            downloadId = "NOT-WEIRS",
        };

        using var response = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/sonarr", sonarr, SecretHeader);

        Assert.Equal("""{"status":"ignored","source":"sonarr","event":"imported"}""", await response.Content.ReadAsStringAsync());
        Assert.True(File.Exists(copy));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome IS NOT NULL OR settled_at IS NOT NULL"));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'processing.handback_outcome'"));
    }

    [Fact]
    public async Task An_import_with_no_webhook_secret_anywhere_is_recorded_but_never_removes_a_file()
    {
        await using var server = await StartAsync(webhookSecret: string.Empty);
        var library = await MoviesAsync(server);
        var copy = await HandedBackAsync(server, library, "Film/film.mkv");
        await TestDatabase.ExecuteAsync(server, "INSERT INTO media_manager_connections (kind, name, base_url) VALUES ('radarr', 'Radarr', 'http://192.0.2.20:7878')");

        using var response = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/radarr", RadarrImport(copy));

        Assert.Contains("\"matched\":true,\"released\":false", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(File.Exists(copy));
        Assert.StartsWith("Weir kept its copy, because Radarr's messages to Weir carry no webhook secret", await TestDatabase.ScalarStringAsync(server, "SELECT release_note FROM handbacks"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_import_is_matched_by_the_download_id_a_hand_off_recorded()
    {
        await using var server = await StartAsync();
        var library = await MoviesAsync(server);
        var copy = await HandedBackAsync(server, library, "Film/film.mkv");
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO media_manager_handoffs (source_key, handoff_id, library_id, relative_path, state, download_id) VALUES ('native', 'n1', $l, 'Film/film.mkv', 'completed', 'DL-42')",
            ("$l", library));

        using var response = await new ApiTestClient(server).PostAsync(
            "/api/v1/intake/webhook/radarr", RadarrImport("/somewhere/else/renamed.mkv", downloadId: "DL-42"), SecretHeader);

        Assert.Contains("\"matched\":true,\"released\":true", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(File.Exists(copy));
    }

    // --- the hand-off outcome ------------------------------------------------------------------------------------------

    /// <summary>
    /// A Deluno hand-off Weir received and finished, with the copy it handed back. The target row and the hand-off's
    /// <c>reported_status</c> are set as the real completion report would have left them, since this helper fakes the
    /// pass finishing rather than running one: an outcome route only releases a copy its report actually named.
    /// </summary>
    private async Task<string> FinishedHandoffAsync(WeirTestServer server, string handoffId = "h1", IReadOnlyDictionary<string, string>? headers = null)
    {
        var library = await MoviesAsync(server);
        var source = Path.Join(Watched, "Film", "film.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "the original download");
        var handoff = new { eventType = "deluno.processor-handoff", handoffId, libraryId = "lib-1", mediaType = "movies", sourcePath = source, callbackPath = "/api/integrations/processors/events" };
        using (var queued = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/deluno", handoff, headers ?? SecretHeader))
        {
            Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        }

        await TestDatabase.ExecuteAsync(server, "UPDATE jobs SET status = 'completed' WHERE job_kind = 'processing.file.remux_pass.v1'");
        var copy = await HandedBackAsync(server, library, "Film/film.mkv");
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE media_manager_handoff_targets SET result = 'completed', output_file = $copy, " +
            "output_written_at = (SELECT written_at FROM handbacks WHERE relative_path = 'Film/film.mkv') WHERE relative_path = 'Film/film.mkv' " +
            "AND handoff_row_id = (SELECT id FROM media_manager_handoffs WHERE source_key = 'deluno' AND handoff_id = $id)",
            ("$copy", copy),
            ("$id", handoffId));
        await TestDatabase.ExecuteAsync(
            server, "UPDATE media_manager_handoffs SET reported_status = 'completed' WHERE source_key = 'deluno' AND handoff_id = $id", ("$id", handoffId));
        return copy;
    }

    private static async Task RefuseAsync(WeirTestServer server)
    {
        using var refused = await PostOutcomeAsync(server, "h1", DelunoOutcome("not-imported", null, "The import dead-lettered."));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
    }

    /// <summary>Exactly what Deluno's <c>ReportOutcomeAsync</c> sends: <c>JsonContent.Create</c>, web defaults, nulls kept.</summary>
    private static StringContent DelunoOutcome(string outcome, string? importedPath, string? reason) =>
        TestDatabase.RawJson(System.Text.Json.JsonSerializer.Serialize(
            new { outcome, occurredUtc = new DateTimeOffset(2026, 9, 23, 10, 11, 12, TimeSpan.Zero).AddTicks(1234567), importedPath, reason },
            DelunoJson));

    private static readonly System.Text.Json.JsonSerializerOptions DelunoJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static Task<HttpResponseMessage> PostOutcomeAsync(WeirTestServer server, string handoffId, HttpContent content, bool withSecret = true) =>
        new ApiTestClient(server).SendAsync(
            HttpMethod.Post, $"/api/v1/intake/handoffs/deluno/{handoffId}/outcome", headers: withSecret ? SecretHeader : null, content: content);

    [Fact]
    public async Task Imported_answers_200_and_releases_the_exact_copy_and_the_same_outcome_again_answers_the_same()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        const string expected =
            """{"handoffId":"h1","outcome":"imported","released":true,"message":"Weir recorded that Deluno imported the file and released its copy."}""";

        using (var response = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film (2020)/Film (2020).mkv", null)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(expected, await response.Content.ReadAsStringAsync());
        }

        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(Path.Join(Watched, "Film", "film.mkv")));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM media_manager_handoffs WHERE outcome = 'imported' AND outcome_released = 1"));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome_by = 'Deluno' AND imported_path = '/media/movies/Film (2020)/Film (2020).mkv'"));

        using (var repeat = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film (2020)/Film (2020).mkv", null)))
        {
            Assert.Equal((HttpStatusCode.OK, expected), (repeat.StatusCode, await repeat.Content.ReadAsStringAsync()));
        }

        using var different = await PostOutcomeAsync(server, "h1", DelunoOutcome("not-imported", null, "Changed its mind."));
        Assert.Equal(
            (HttpStatusCode.Conflict, "Deluno already said it imported this file, so Weir kept that answer."),
            (different.StatusCode, await Detail(different)));
    }

    [Fact]
    public async Task A_409_says_in_its_code_whether_the_answer_is_final_or_worth_sending_again()
    {
        await using var server = await StartAsync();
        await FinishedHandoffAsync(server);
        using (var first = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null)))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var different = await PostOutcomeAsync(server, "h1", DelunoOutcome("not-imported", null, "Changed its mind.")))
        {
            Assert.Equal((HttpStatusCode.Conflict, "outcome_already_recorded"), (different.StatusCode, await Code(different)));
        }

        var source = Path.Join(Watched, "Queued", "queued.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "a download still waiting");
        var handoff = new { eventType = "deluno.processor-handoff", handoffId = "h2", libraryId = "lib-1", mediaType = "movies", sourcePath = source, callbackPath = "/cb" };
        using (var queued = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/deluno", handoff, SecretHeader))
        {
            Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        }

        using (var early = await PostOutcomeAsync(server, "h2", DelunoOutcome("imported", null, null)))
        {
            Assert.Equal((HttpStatusCode.Conflict, "handoff_not_finished"), (early.StatusCode, await Code(early)));
        }

    }

    [Fact]
    public async Task A_409_for_a_hand_off_that_ended_says_so_in_its_code()
    {
        await using var server = await StartAsync();
        await FinishedHandoffAsync(server);
        await FailedPassThroughAsync(server, "2099-01-01 00:00:00.000000");

        using var failed = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", null, null));
        Assert.Equal(
            (HttpStatusCode.Conflict, "This hand-off ended failed, so Weir handed back no file to import.", "handoff_ended"),
            (failed.StatusCode, await Detail(failed), await Code(failed)));
    }

    private static async Task<string?> Code(HttpResponseMessage response) => (await Json(response))["code"]?.GetValue<string>();

    /// <summary>
    /// Makes the hand-off one of the release folder, with an extra beside the film: the file row and the target row the extra's
    /// pass would have left.
    /// </summary>
    private static async Task AddExtraAsync(WeirTestServer server, string fileStatus, string targetResult, string reason, long extraMb = 15, long filmMb = 80)
    {
        await TestDatabase.ExecuteAsync(server, "UPDATE media_manager_handoffs SET relative_path = 'Film' WHERE handoff_id = 'h1'");
        await TestDatabase.ExecuteAsync(server, "UPDATE files SET size_bytes = $size WHERE relative_path = 'Film/film.mkv'", ("$size", filmMb * 1024 * 1024));
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, status_reason, size_bytes, updated_at) " +
            "VALUES ((SELECT id FROM libraries WHERE media_type = 'movie' ORDER BY id LIMIT 1), 'Film/Gallery.mkv', $status, $reason, $size, '2099-01-01 00:00:00.000000')",
            ("$status", fileStatus),
            ("$reason", reason),
            ("$size", extraMb * 1024 * 1024));
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO media_manager_handoff_targets (handoff_row_id, relative_path, result, message) " +
            "VALUES ((SELECT id FROM media_manager_handoffs WHERE handoff_id = 'h1'), 'Film/Gallery.mkv', $result, $reason)",
            ("$result", targetResult),
            ("$reason", reason));
    }

    [Fact]
    public async Task An_extra_the_workflows_rules_left_alone_does_not_fail_the_hand_off_so_the_import_settles_the_film()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        await AddExtraAsync(server, "skipped", "skipped", "Skipped because this file is 15.6 MB, under the 50 MB minimum.");

        using (var status = await new ApiTestClient(server).GetAsync("/api/v1/intake/handoffs/deluno/h1", SecretHeader))
        {
            Assert.Equal("completed", (await Json(status))["state"]!.GetValue<string>());
        }

        using var response = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"released\":true", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(File.Exists(copy));
        // The Activity entry is about the file that was delivered, not the folder or the extra beside it.
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE title = 'Deluno imported film.mkv'"));
    }

    private static async Task<string> HandoffStateAsync(WeirTestServer server)
    {
        using var status = await new ApiTestClient(server).GetAsync("/api/v1/intake/handoffs/deluno/h1", SecretHeader);
        return (await Json(status))["state"]!.GetValue<string>();
    }

    [Fact]
    public async Task A_hand_off_an_earlier_release_recorded_failed_for_a_skipped_extra_settles_on_the_next_import_once_upgraded()
    {
        // As release candidate 8 left it: the extra's target is 'failed', the hand-off was reported failed, and the rule that
        // refused "imported" for it is the one this build no longer applies once the migration has put the target right.
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        await AddExtraAsync(server, "skipped", "failed", "Skipped because this file is 15.6 MB, under the 50 MB minimum.");
        await TestDatabase.ExecuteAsync(server, "UPDATE media_manager_handoffs SET state = 'failed', reported_status = 'failed' WHERE handoff_id = 'h1'");
        Assert.Equal("failed", await HandoffStateAsync(server));
        using (var refused = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null)))
        {
            Assert.Equal((HttpStatusCode.Conflict, "handoff_ended"), (refused.StatusCode, await Code(refused)));
        }

        await TestDatabase.ExecuteAsync(server, MigrationScript);

        Assert.Equal(("completed", "completed"), (
            await TestDatabase.ScalarStringAsync(server, "SELECT reported_status FROM media_manager_handoffs WHERE handoff_id = 'h1'"),
            await TestDatabase.ScalarStringAsync(server, "SELECT state FROM media_manager_handoffs WHERE handoff_id = 'h1'")));
        using var response = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"released\":true", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(File.Exists(copy));
    }

    /// <summary>The upgrade's data migration for hand-offs recorded before a skipped file had a result of its own.</summary>
    private static string MigrationScript
    {
        get
        {
            using var stream = typeof(Weir.Infrastructure.Sqlite.SchemaMigrator).Assembly
                .GetManifestResourceStream("Weir.Infrastructure.Migrations.0041_handoff_skipped_extras.sql")!;
            return new StreamReader(stream).ReadToEnd();
        }
    }

    [Fact]
    public async Task A_file_Weir_rejected_beside_a_delivered_one_fails_the_hand_off_even_though_its_status_is_skipped()
    {
        // A rejected release, an unreadable file or a file with no video is recorded with the same file status as an extra the
        // workflow's rules left alone, but it is something a person must see.
        await using var server = await StartAsync();
        await FinishedHandoffAsync(server);
        await AddExtraAsync(server, "skipped", "failed", "This file could not be read, so Weir left it alone.");

        Assert.Equal("failed", await HandoffStateAsync(server));
        using var response = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));
        Assert.Equal((HttpStatusCode.Conflict, "handoff_ended"), (response.StatusCode, await Code(response)));
    }

    [Fact]
    public async Task A_skipped_file_that_is_not_smaller_than_the_delivered_one_might_be_the_film_so_the_hand_off_fails()
    {
        await using var server = await StartAsync();
        await FinishedHandoffAsync(server);
        await AddExtraAsync(
            server, "skipped", "skipped", "Skipped because this file is 90.0 MB, under the 95 MB minimum.", extraMb: 90, filmMb: 80);

        Assert.Equal("failed", await HandoffStateAsync(server));
        using var response = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));
        Assert.Equal((HttpStatusCode.Conflict, "handoff_ended"), (response.StatusCode, await Code(response)));
    }

    [Fact]
    public async Task A_skipped_file_whose_size_is_not_known_fails_the_hand_off()
    {
        await using var server = await StartAsync();
        await FinishedHandoffAsync(server);
        await AddExtraAsync(server, "skipped", "skipped", "Skipped because this file is 15.6 MB, under the 50 MB minimum.", extraMb: 0);

        Assert.Equal("failed", await HandoffStateAsync(server));
    }

    [Fact]
    public async Task An_import_repeated_after_a_retry_wrote_a_new_copy_records_the_new_copy()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        using (var first = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null)))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // A retry reports the hand-off again with a new copy; the handbacks row starts over, as a new copy always does.
        await File.WriteAllTextAsync(copy, "the cleaned copy");
        var info = new FileInfo(copy);
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE handbacks SET outcome = NULL, outcome_by = NULL, outcome_at = NULL, imported_path = NULL, released_at = NULL, settled_at = NULL, " +
            "release_note = NULL, output_size = $size, output_mtime_ns = $mtime, written_at = '2026-09-21 10:00:00.000000'",
            ("$size", info.Length),
            ("$mtime", (info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100));
        await TestDatabase.ExecuteAsync(server, "UPDATE media_manager_handoff_targets SET output_written_at = '2026-09-21 10:00:00.000000'");

        using var second = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("\"released\":true", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(File.Exists(copy));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND released_at IS NOT NULL"));

        // Once every copy has the manager's word, the same message again changes nothing.
        using var third = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(2, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'processing.handback_outcome'"));
    }

    [Fact]
    public async Task An_import_is_accepted_for_a_hand_off_that_ended_failed_once_Weir_handed_back_a_file()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        await AddExtraAsync(server, "processing_failed", "failed", "The extra could not be processed.");
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE media_manager_handoffs SET state = 'failed', reported_status = 'failed', output_files_json = $files WHERE handoff_id = 'h1'",
            ("$files", System.Text.Json.JsonSerializer.Serialize(new[] { copy })));

        using (var status = await new ApiTestClient(server).GetAsync("/api/v1/intake/handoffs/deluno/h1", SecretHeader))
        {
            Assert.Equal("failed", (await Json(status))["state"]!.GetValue<string>());
        }

        using var response = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"released\":true", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public async Task Not_imported_records_the_reason_and_keeps_the_copy()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);

        using var response = await PostOutcomeAsync(server, "h1", DelunoOutcome("not-imported", null, "The release is a sample."));

        Assert.Equal(
            """{"handoffId":"h1","outcome":"not-imported","released":false,"message":"Weir recorded that the file will not be imported, and kept its copy."}""",
            await response.Content.ReadAsStringAsync());
        Assert.True(File.Exists(copy));
        Assert.Equal(
            "Deluno will not import this file: The release is a sample. Weir kept its copy in the hand-back folder.",
            await TestDatabase.ScalarStringAsync(server, "SELECT release_note FROM handbacks WHERE outcome = 'not-imported' AND outcome_reason = 'The release is a sample.'"));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE title = 'Deluno will not import film.mkv'"));

        using var again = await PostOutcomeAsync(server, "h1", DelunoOutcome("not-imported", null, "The release is a sample."));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'processing.handback_outcome'"));
    }

    [Fact]
    public async Task An_imported_after_a_not_imported_replaces_it_and_releases_the_copy_the_refusal_kept()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        using (var refused = await PostOutcomeAsync(server, "h1", DelunoOutcome("not-imported", null, "The import dead-lettered.")))
        {
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        }

        Assert.True(File.Exists(copy));
        const string expected =
            """{"handoffId":"h1","outcome":"imported","released":true,"message":"Weir recorded that Deluno imported the file and released its copy."}""";

        using (var imported = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film (2020)/Film (2020).mkv", null)))
        {
            Assert.Equal((HttpStatusCode.OK, expected), (imported.StatusCode, await imported.Content.ReadAsStringAsync()));
        }

        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(Path.Join(Watched, "Film", "film.mkv")));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM media_manager_handoffs WHERE outcome = 'imported' AND outcome_released = 1"));
        Assert.Equal(
            1,
            await TestDatabase.ScalarAsync(
                server,
                "SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND outcome_by = 'Deluno' AND outcome_reason IS NULL " +
                "AND imported_path = '/media/movies/Film (2020)/Film (2020).mkv' AND released_at IS NOT NULL AND settled_at IS NOT NULL " +
                "AND release_note = 'Weir removed its copy from the hand-back folder, because Deluno has the file now.'"));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE title = 'Deluno will not import film.mkv'"));
        Assert.Equal(
            1,
            await TestDatabase.ScalarAsync(
                server,
                "SELECT count(*) FROM activity_events WHERE title = 'Deluno imported film.mkv after all' " +
                "AND detail LIKE '%Deluno had said it would not import this file, and then imported it after all. Weir recorded that Deluno imported the file and released its copy.%'"));

        using (var repeat = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film (2020)/Film (2020).mkv", null)))
        {
            Assert.Equal((HttpStatusCode.OK, expected), (repeat.StatusCode, await repeat.Content.ReadAsStringAsync()));
        }

        using var refusedAgain = await PostOutcomeAsync(server, "h1", DelunoOutcome("not-imported", null, "Changed its mind."));
        Assert.Equal((HttpStatusCode.Conflict, "outcome_already_recorded"), (refusedAgain.StatusCode, await Code(refusedAgain)));
        Assert.Equal(2, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE event_type = 'processing.handback_outcome'"));
    }

    [Fact]
    public async Task An_imported_after_a_not_imported_still_keeps_a_copy_that_changed_since_Weir_wrote_it()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        using (var refused = await PostOutcomeAsync(server, "h1", DelunoOutcome("not-imported", null, "The import dead-lettered.")))
        {
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        }

        await File.AppendAllTextAsync(copy, ", and then someone else wrote to it");

        using var imported = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));

        Assert.Equal(
            """{"handoffId":"h1","outcome":"imported","released":false,"message":"Weir recorded that Deluno imported the file. Weir's copy has changed since Weir wrote it, so Weir left it alone."}""",
            await imported.Content.ReadAsStringAsync());
        Assert.True(File.Exists(copy));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND released_at IS NULL AND settled_at IS NOT NULL"));
    }

    [Fact]
    public async Task An_imported_after_a_not_imported_leaves_a_copy_another_manager_settled()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        using (var refused = await PostOutcomeAsync(server, "h1", DelunoOutcome("not-imported", null, "The import dead-lettered.")))
        {
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        }

        await TestDatabase.ExecuteAsync(server, "UPDATE handbacks SET outcome_by = 'Radarr'");

        using var imported = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));

        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.True(File.Exists(copy));
    }

    [Fact]
    public async Task Another_managers_hand_off_with_the_same_id_is_never_found()
    {
        await using var server = await StartAsync();
        await FinishedHandoffAsync(server);
        using (var refused = await PostOutcomeAsync(server, "h1", DelunoOutcome("not-imported", null, "The import dead-lettered.")))
        {
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        }

        using var other = await new ApiTestClient(server).SendAsync(
            HttpMethod.Post, "/api/v1/intake/handoffs/sonarr/h1/outcome", headers: SecretHeader, content: DelunoOutcome("imported", "/tv/film.mkv", null));

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task A_refusal_and_an_import_sent_together_end_as_the_import_with_the_copy_released()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);

        var refusal = PostOutcomeAsync(server, "h1", DelunoOutcome("not-imported", null, "The import dead-lettered."));
        var import = PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));
        using var refused = await refusal;
        using var imported = await import;

        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Contains(refused.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        Assert.False(File.Exists(copy));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM media_manager_handoffs WHERE outcome = 'imported' AND outcome_released = 1"));
        Assert.Equal(
            1,
            await TestDatabase.ScalarAsync(
                server, "SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND released_at IS NOT NULL AND release_note LIKE 'Weir removed its copy%'"));
    }

    [Fact]
    public async Task Two_imports_sent_together_after_a_refusal_release_the_copy_once()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        await RefuseAsync(server);

        var first = PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));
        var second = PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));
        using var one = await first;
        using var two = await second;

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (one.StatusCode, two.StatusCode));
        Assert.Equal(await one.Content.ReadAsStringAsync(), await two.Content.ReadAsStringAsync());
        Assert.False(File.Exists(copy));
        Assert.Equal(
            1,
            await TestDatabase.ScalarAsync(
                server, "SELECT count(*) FROM handbacks WHERE released_at IS NOT NULL AND release_note LIKE 'Weir removed its copy%'"));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE title = 'Deluno imported film.mkv after all'"));
    }

    /// <summary>
    /// A later pass wrote a fresh copy for the same file, which the first hand-off's report never named. Whatever the manager
    /// says about the first hand-off, that copy is not released.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_copy_written_after_the_hand_off_was_reported_is_not_released(bool refusedFirst)
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        if (refusedFirst)
        {
            await RefuseAsync(server);
        }

        await File.WriteAllTextAsync(copy, "the cleaned copy of a newer download");
        var info = new FileInfo(copy);
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE handbacks SET output_size = $s, output_mtime_ns = $m, written_at = '2026-09-25 10:00:00.000000', outcome = NULL, outcome_by = NULL, " +
            "outcome_at = NULL, outcome_reason = NULL, imported_path = NULL, released_at = NULL, settled_at = NULL, release_note = NULL",
            ("$s", info.Length),
            ("$m", (info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100));

        using var imported = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));

        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.True(File.Exists(copy));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome IS NULL AND released_at IS NULL AND settled_at IS NULL"));
    }

    [Fact]
    public async Task Refused_then_imported_in_a_manager_linked_workflow_releases_only_Weirs_copy_and_never_the_original()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        await TestDatabase.ExecuteAsync(server, "INSERT INTO media_manager_connections (kind, name, base_url) VALUES ('deluno', 'Deluno', 'http://192.0.2.30:5000')");
        await TestDatabase.ExecuteAsync(server, "INSERT INTO library_manager_links (library_id, connection_id) SELECT l.id, c.id FROM libraries l, media_manager_connections c WHERE l.media_type = 'movie'");
        var original = Path.Join(Watched, "Film", "film.mkv");
        var sidecar = Path.Join(Watched, "Film", "film.nfo");
        await File.WriteAllTextAsync(sidecar, "the download's sidecar");
        await RefuseAsync(server);

        using var imported = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film (2020)/Film (2020).mkv", null));

        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.False(File.Exists(copy));
        Assert.Equal("the original download", await File.ReadAllTextAsync(original));
        Assert.True(File.Exists(sidecar));
        Assert.True(Directory.Exists(Path.Join(Watched, "Film")));
    }

    [Fact]
    public async Task A_copy_the_cleanup_job_already_settled_is_left_as_it_was()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        const string note = "No media manager imported it within 14 days, so Weir removed its copy.";
        File.Delete(copy);
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE handbacks SET released_at = '2026-10-01 10:00:00.000000', settled_at = '2026-10-01 10:00:00.000000', release_note = $note",
            ("$note", note));

        using var imported = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));

        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Equal(note, await TestDatabase.ScalarStringAsync(server, "SELECT release_note FROM handbacks WHERE outcome = 'imported'"));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE released_at = '2026-10-01 10:00:00.000000'"));
    }

    [WindowsFact("FileShare.None only blocks a delete on Windows; POSIX has no equivalent share-mode lock.")]
    public async Task A_copy_in_use_when_the_manager_imports_is_recorded_as_kept_with_the_reason()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        await RefuseAsync(server);

        string message;
        using (new FileStream(copy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var imported = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
            message = (await Json(imported))["message"]!.GetValue<string>();
        }

        Assert.StartsWith("Weir recorded that Deluno imported the file. Weir could not remove its copy (", message, StringComparison.Ordinal);
        Assert.True(File.Exists(copy));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND released_at IS NULL AND settled_at IS NOT NULL"));
    }

    /// <summary>
    /// A season pack's completion report names only the episodes it actually covers (#667): if one episode's copy was
    /// never among the files that report named — whatever the reason — Deluno's "imported" for the pack releases only
    /// the copies the report did name, never every file that happens to sit under the pack's folder.
    /// </summary>
    [Fact]
    public async Task An_imported_outcome_for_a_pack_releases_only_the_episode_its_report_named()
    {
        await using var server = await StartAsync();
        var library = await MoviesAsync(server);
        const string handoffId = "pack1";
        var handoff = new
        {
            eventType = "deluno.processor-handoff",
            handoffId,
            libraryId = "lib-1",
            mediaType = "movies",
            sourcePath = Path.Join(Watched, "Show.S05"),
            callbackPath = "/api/integrations/processors/events",
        };
        Directory.CreateDirectory(Path.Join(Watched, "Show.S05"));
        await File.WriteAllTextAsync(Path.Join(Watched, "Show.S05", "Show.S05E01.mkv"), "download1");
        await File.WriteAllTextAsync(Path.Join(Watched, "Show.S05", "Show.S05E02.mkv"), "download2");
        using (var queued = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/deluno", handoff, SecretHeader))
        {
            Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        }

        await TestDatabase.ExecuteAsync(server, "UPDATE jobs SET status = 'completed' WHERE job_kind = 'processing.file.remux_pass.v1'");
        var copy1 = await HandedBackAsync(server, library, "Show.S05/Show.S05E01.mkv");
        var copy2 = await HandedBackAsync(server, library, "Show.S05/Show.S05E02.mkv");

        // Only episode 1's target is marked as delivered and named in the report Weir sent; episode 2's is left as it
        // was at intake (no final result), as if its pass had not been part of what the report covered.
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE media_manager_handoff_targets SET result = 'completed', output_file = $copy WHERE relative_path = 'Show.S05/Show.S05E01.mkv' " +
            "AND handoff_row_id = (SELECT id FROM media_manager_handoffs WHERE source_key = 'deluno' AND handoff_id = $id)",
            ("$copy", copy1),
            ("$id", handoffId));
        await TestDatabase.ExecuteAsync(
            server, "UPDATE media_manager_handoffs SET reported_status = 'completed' WHERE source_key = 'deluno' AND handoff_id = $id", ("$id", handoffId));

        using var response = await PostOutcomeAsync(server, handoffId, DelunoOutcome("imported", "/media/tv/Show/Show.S05E01.mkv", null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(File.Exists(copy1));
        Assert.True(File.Exists(copy2));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND released_at IS NOT NULL"));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE relative_path = 'Show.S05/Show.S05E02.mkv' AND outcome IS NOT NULL"));
    }

    [Fact]
    public async Task A_moved_copy_is_imported_without_removing_anything()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        File.Delete(copy);

        using var response = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));

        Assert.Equal(
            """{"handoffId":"h1","outcome":"imported","released":false,"message":"Weir recorded that Deluno imported the file. Deluno moved Weir's copy into its library, so there was nothing for Weir to remove."}""",
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>A pass-through job for the film that ran out of retries, queued at <paramref name="createdAt"/>.</summary>
    private static async Task FailedPassThroughAsync(WeirTestServer server, string createdAt)
    {
        var library = await TestDatabase.ScalarAsync(server, "SELECT id FROM libraries WHERE media_type = 'movie' ORDER BY id LIMIT 1");
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO jobs (dedupe_key, job_kind, payload_json, status, last_error, created_at, updated_at) " +
            "VALUES ($key, 'processing.file.pass_through.v1', '{}', 'failed', 'Deluno stopped waiting for this file.', $at, $at)",
            ("$key", $"processing.file.pass_through.v1:{library}:Film/film.mkv:old-fingerprint"),
            ("$at", createdAt));
    }

    [Fact]
    public async Task A_failed_job_left_by_an_earlier_hand_off_of_the_same_file_does_not_fail_this_one()
    {
        // An earlier hand-off of the same file left a pass-through that ran out of retries. That job is found by the
        // file's path, so without a guard the new hand-off, which Weir has just completed, reads "failed" and Weir
        // refuses Deluno's "imported" with 409.
        await using var server = await StartAsync();
        await FailedPassThroughAsync(server, "2026-09-22 09:00:00.000000");
        await FinishedHandoffAsync(server);

        using (var status = await new ApiTestClient(server).GetAsync("/api/v1/intake/handoffs/deluno/h1", SecretHeader))
        {
            Assert.Equal("completed", (await Json(status))["state"]!.GetValue<string>());
        }

        using var response = await PostOutcomeAsync(server, "h1", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_failed_row_left_for_the_release_folder_by_an_earlier_hand_off_does_not_fail_this_one()
    {
        // A failed row for the release folder itself, left days earlier, sits under the new hand-off's path and must
        // not be counted with the file Weir has just finished inside it.
        await using var server = await StartAsync();
        var library = await MoviesAsync(server);
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status, status_reason, updated_at) " +
            "VALUES ($l, 'Film', 'processing_failed', 'Weir could not find this file under the saved watched folder.', '2026-09-19 14:48:21.000000')",
            ("$l", library));
        Directory.CreateDirectory(Path.Join(Watched, "Film"));
        await File.WriteAllTextAsync(Path.Join(Watched, "Film", "film.mkv"), "the original download");
        var handoff = new { eventType = "deluno.processor-handoff", handoffId = "h2", libraryId = "lib-1", mediaType = "movies", sourcePath = Path.Join(Watched, "Film"), callbackPath = "/api/integrations/processors/events" };
        using (var queued = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/deluno", handoff, SecretHeader))
        {
            Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        }

        await TestDatabase.ExecuteAsync(server, "UPDATE jobs SET status = 'completed' WHERE job_kind = 'processing.file.remux_pass.v1'");
        await HandedBackAsync(server, library, "Film/film.mkv");

        using (var status = await new ApiTestClient(server).GetAsync("/api/v1/intake/handoffs/deluno/h2", SecretHeader))
        {
            Assert.Equal("completed", (await Json(status))["state"]!.GetValue<string>());
        }

        using var response = await PostOutcomeAsync(server, "h2", DelunoOutcome("imported", "/media/movies/Film/film.mkv", null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_file_that_fails_during_this_hand_off_still_fails_it_though_its_row_was_written_at_receipt()
    {
        // Receiving the hand-off writes the file's row a moment before the hand-off's own; a failure that leaves that time
        // alone must still count. The contract suite caught the first version of the fix setting this aside.
        await using var server = await StartAsync();
        var library = await MoviesAsync(server);
        Directory.CreateDirectory(Path.Join(Watched, "Film"));
        await File.WriteAllTextAsync(Path.Join(Watched, "Film", "film.mkv"), "the original download");
        var handoff = new { eventType = "deluno.processor-handoff", handoffId = "h3", libraryId = "lib-1", mediaType = "movies", sourcePath = Path.Join(Watched, "Film", "film.mkv"), callbackPath = "/api/integrations/processors/events" };
        using (var queued = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/deluno", handoff, SecretHeader))
        {
            Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        }

        await TestDatabase.ExecuteAsync(server, "UPDATE jobs SET status = 'completed' WHERE job_kind = 'processing.file.remux_pass.v1'");
        await TestDatabase.ExecuteAsync(
            server,
            "INSERT INTO files (library_id, relative_path, status) VALUES ($l, 'Film/film.mkv', 'processing_failed') " +
            "ON CONFLICT (library_id, relative_path) DO UPDATE SET status = 'processing_failed'",
            ("$l", library));

        using var status = await new ApiTestClient(server).GetAsync("/api/v1/intake/handoffs/deluno/h3", SecretHeader);
        Assert.Equal("failed", (await Json(status))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_failed_job_queued_for_this_hand_off_still_fails_it()
    {
        await using var server = await StartAsync();
        await FinishedHandoffAsync(server);
        await FailedPassThroughAsync(server, "2099-01-01 00:00:00.000000");

        using var status = await new ApiTestClient(server).GetAsync("/api/v1/intake/handoffs/deluno/h1", SecretHeader);
        Assert.Equal("failed", (await Json(status))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_outcome_endpoint_answers_404_409_422_and_401_as_agreed()
    {
        await using var server = await StartAsync();
        await MoviesAsync(server);

        using (var never = await PostOutcomeAsync(server, "nobody", DelunoOutcome("imported", null, null)))
        {
            Assert.Equal((HttpStatusCode.NotFound, "Weir has never received this hand-off."), (never.StatusCode, await Detail(never)));
        }

        // Received but not finished: its pass is still queued.
        var source = Path.Join(Watched, "Queued", "queued.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "a download still waiting");
        var handoff = new { eventType = "deluno.processor-handoff", handoffId = "h2", libraryId = "lib-1", mediaType = "movies", sourcePath = source, callbackPath = "/cb" };
        using (var queued = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/deluno", handoff, SecretHeader))
        {
            Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        }

        using (var early = await PostOutcomeAsync(server, "h2", DelunoOutcome("imported", null, null)))
        {
            Assert.Equal(
                (HttpStatusCode.Conflict, "Weir has not finished this hand-off yet (it is queued), so there is no file to import."),
                (early.StatusCode, await Detail(early)));
        }

        using (var badOutcome = await PostOutcomeAsync(server, "h2", TestDatabase.RawJson("""{"outcome":"maybe","occurredUtc":"2026-09-23T10:00:00Z"}""")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, badOutcome.StatusCode);
            Assert.Equal("literal_error", (await Json(badOutcome))["detail"]![0]!["type"]!.GetValue<string>());
        }

        using (var noTime = await PostOutcomeAsync(server, "h2", TestDatabase.RawJson("""{"outcome":"imported"}""")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noTime.StatusCode);
            Assert.Equal("missing", (await Json(noTime))["detail"]![0]!["type"]!.GetValue<string>());
        }

        using (var naive = await PostOutcomeAsync(server, "h2", TestDatabase.RawJson("""{"outcome":"imported","occurredUtc":"2026-09-23T10:00:00"}""")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, naive.StatusCode);
        }

        using (var notAnObject = await PostOutcomeAsync(server, "h2", TestDatabase.RawJson("[1]")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, notAnObject.StatusCode);
        }

        using var noSecret = await PostOutcomeAsync(server, "h2", DelunoOutcome("imported", null, null), withSecret: false);
        Assert.Equal(HttpStatusCode.Unauthorized, noSecret.StatusCode);
    }
}
