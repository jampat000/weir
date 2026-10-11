using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;

namespace Weir.Contract.Tests.Processing;

/// <summary>
/// What Weir knows about a copy it handed back does not depend on the file being listed. A file taken off the list (Remove from the
/// list, or any other way its row goes) and handed over again, unchanged, is not cleaned a second time, so the manager's earlier
/// "imported" is not lost to a new copy that nobody has answered. A manager's answer that comes after the file left the list is
/// still recorded and still releases the copy.
/// </summary>
[ContractArea("processing")]
public sealed class ForgottenFileTests
{
    private const string FileName = "Pulp.Fiction.1994.mkv";
    private const string ImportedPath = "/deluno/library/movies/Pulp Fiction (1994)/Pulp Fiction (1994).mkv";

    /// <summary>A file directly in the watched folder (no release folder), handed over by a fake Deluno and finished.</summary>
    private static async Task<(FakeManager Fake, JsonObject Library, string Source)> FinishedHandoffAsync(Scenario scenario, string handoffId)
    {
        var (fake, library) = await scenario.DelunoSetupAsync();
        var source = Path.Combine(scenario.Folders.Watched, FileName);
        await File.WriteAllBytesAsync(source, FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        await scenario.PostHandoffAsync(handoffId, source);
        await scenario.WaitForHandoffStateAsync(handoffId, "completed");
        await Poll.UntilAsync(() => Task.FromResult(Scenario.Callbacks(fake, handoffId).FirstOrDefault()), $"the report for {handoffId}");
        return (fake, library, source);
    }

    private static async Task ForgetAsync(Scenario scenario, JsonObject library)
    {
        var row = await scenario.FileRowAsync(library, FileName);
        var forgotten = await scenario.Admin.DeleteWithCsrfBodyAsync($"{WeirClient.Api}/processing/files/{(int)row!["id"]!}");
        Assert.Equal(HttpStatusCode.NoContent, forgotten.Status);
        Assert.Null(await scenario.FileRowAsync(library, FileName));
    }

    [Fact]
    public async Task A_forgotten_file_handed_over_again_is_not_cleaned_twice_and_still_reads_imported()
    {
        await using var scenario = await Scenario.StartAsync();
        var (fake, library, source) = await FinishedHandoffAsync(scenario, "first");
        var output = Path.Combine(scenario.Folders.Output, FileName);
        var imported = await scenario.PostOutcomeAsync("first", "imported", ImportedPath);
        Assert.True(imported.Status == HttpStatusCode.OK, imported.ToString());
        Assert.False(File.Exists(output), "Weir's copy is released once Deluno has the file");
        await ForgetAsync(scenario, library);

        await scenario.PostHandoffAsync("again", source);
        await scenario.WaitForHandoffStateAsync("again", "completed");

        var report = await Poll.UntilAsync(() => Task.FromResult(Scenario.Callbacks(fake, "again").FirstOrDefault()), "the report for the repeat");
        Assert.Equal("completed", (string)report["status"]!);
        Assert.Equal((string)Scenario.Callbacks(fake, "first")[0]["outputPath"]!, (string)report["outputPath"]!);
        Assert.Null(report["disposition"]);
        Assert.Single(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Single(await scenario.JobsAsync(Scenario.RemuxKind));
        Assert.False(File.Exists(output), "no second copy is written");

        var skip = Assert.Single(await scenario.ActivityAsync("processing.file_skipped_repeat"));
        Assert.Equal($"Skipped: already imported ({FileName})", (string)skip["title"]!);

        var row = await scenario.FileRowAsync(library, FileName);
        Assert.NotNull(row);
        Assert.Equal("processed", (string)row["status"]!);
        Assert.StartsWith("Already imported: Deluno collected the cleaned copy on ", (string)row["status_reason"]!, StringComparison.Ordinal);
        var handback = row["handback"]!;
        Assert.Equal(("imported", "Deluno"), ((string)handback["outcome"]!, (string)handback["outcome_by"]!));

        // Activity lists the last week by default, by when each file was last seen.
        var recent = await scenario.Admin.GetAsync($"{WeirClient.Api}/processing/files", ("within_days", 7), ("path_contains", FileName));
        Assert.Single(recent.Fields["files"]!.AsArray());

        // Deluno answers the repeat as it answers any hand-off.
        var again = await scenario.PostOutcomeAsync("again", "imported", ImportedPath);
        Assert.True(again.Status == HttpStatusCode.OK, again.ToString());
        Assert.Equal("imported", (string)(await scenario.FileRowAsync(library, FileName))!["handback"]!["outcome"]!);
    }

    [Fact]
    public async Task A_managers_answer_after_the_file_left_the_list_is_recorded_and_releases_the_copy()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library, source) = await FinishedHandoffAsync(scenario, "first");
        var output = Path.Combine(scenario.Folders.Output, FileName);
        Assert.True(File.Exists(output));
        await ForgetAsync(scenario, library);

        var imported = await scenario.PostOutcomeAsync("first", "imported", ImportedPath);

        Assert.True(imported.Status == HttpStatusCode.OK, imported.ToString());
        Assert.True((bool)imported.Fields["released"]!, imported.ToString());
        Assert.False(File.Exists(output), "Weir's own copy is released once Deluno has the file");
        var line = Assert.Single(await scenario.ActivityAsync("processing.handback_outcome"));
        Assert.Equal($"Deluno imported {FileName}", (string)line["title"]!);

        // The answer is on the copy, so the file reads as imported when it comes back.
        await scenario.PostHandoffAsync("again", source);
        await scenario.WaitForHandoffStateAsync("again", "completed");
        Assert.Single(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Equal("imported", (string)(await scenario.FileRowAsync(library, FileName))!["handback"]!["outcome"]!);
    }

    [Fact]
    public async Task A_scanned_file_Radarr_collected_and_taken_off_the_list_is_not_cleaned_again_when_the_scan_finds_it()
    {
        await using var scenario = await Scenario.StartAsync();
        var (_, library) = await scenario.RadarrSetupAsync();
        scenario.WriteRelease("Radarr.Film.2024", "film.mkv", FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"])));
        const string relative = "Radarr.Film.2024/film.mkv";
        await scenario.EnqueueScanAsync(library);
        var cleaned = await scenario.WaitForFileStatusAsync(library, relative, "processed");
        var output = Path.Combine(scenario.Folders.Output, "Radarr.Film.2024", "film.mkv");
        var collected = await scenario.Admin.PostAsync(
            $"{WeirClient.Api}/intake/webhook/radarr",
            new JsonObject
            {
                ["eventType"] = "Download",
                ["movie"] = new JsonObject { ["id"] = 7, ["title"] = "Radarr Film", ["year"] = 2024 },
                ["movieFile"] = new JsonObject { ["id"] = 12, ["path"] = "/movies/Radarr Film (2024)/Radarr Film (2024).mkv", ["sourcePath"] = output },
                ["downloadClient"] = "qBittorrent",
                ["downloadId"] = "RADARR-1",
            },
            new Dictionary<string, string> { ["X-Webhook-Secret"] = Scenario.WebhookSecret });
        Assert.True(collected.Status == HttpStatusCode.OK, collected.ToString());
        Assert.False(File.Exists(output), "Weir's copy is released once Radarr has the file");
        var forgotten = await scenario.Admin.DeleteWithCsrfBodyAsync($"{WeirClient.Api}/processing/files/{(int)cleaned["id"]!}");
        Assert.Equal(HttpStatusCode.NoContent, forgotten.Status);

        await scenario.WaitForJobFinishedAsync(await scenario.EnqueueScanAsync(library));

        var row = await scenario.WaitForFileStatusAsync(library, relative, "processed");
        Assert.StartsWith("Already imported: Radarr collected the cleaned copy on ", (string)row["status_reason"]!, StringComparison.Ordinal);
        Assert.Equal("imported", (string)row["handback"]!["outcome"]!);
        Assert.Single(scenario.FakeTools.Calls(tool: "ffmpeg", step: "remux"));
        Assert.Single(await scenario.JobsAsync(Scenario.RemuxKind));
    }
}
