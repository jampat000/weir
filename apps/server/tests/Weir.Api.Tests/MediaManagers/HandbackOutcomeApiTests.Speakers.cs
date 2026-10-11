using System.Net;
using Weir.Api.Tests.Platform;
using static Weir.Api.Tests.Platform.ApiTestClient;

namespace Weir.Api.Tests.MediaManagers;

/// <summary>
/// Who said it. Several hand-offs, from several connections and several managers, can name one copy, so what a copy says is the
/// word of one speaker at a time: a connection's "will not import" is undone only by the same connection, an unsigned message
/// never overrides a signed one, an answer that cannot be tied to the copy's generation changes nothing the manager has already
/// said, and every reply is built from the answer it is for.
/// </summary>
public sealed partial class HandbackOutcomeApiTests
{
    private const string ConnectionRoute = "/api/v1/media-managers/connections";
    private const string ImportedElsewhere = "/media/movies/Film/film.mkv";

    private static async Task<long> DelunoConnectionAsync(WeirTestServer server, string name)
    {
        await TestDatabase.ExecuteAsync(
            server, "INSERT INTO media_manager_connections (kind, name, base_url) VALUES ('deluno', $name, 'http://192.0.2.30:5000')", ("$name", name));
        return await TestDatabase.ScalarAsync(server, "SELECT id FROM media_manager_connections WHERE name = $name", ("$name", name));
    }

    private static Task OwnedByAsync(WeirTestServer server, string handoffId, long connectionId) =>
        TestDatabase.ExecuteAsync(
            server, "UPDATE media_manager_handoffs SET connection_id = $c WHERE handoff_id = $id", ("$c", connectionId), ("$id", handoffId));

    /// <summary>
    /// A second hand-off of the file the first one handed back, finished and reported as the first is: it names the copy the
    /// first one's pass wrote.
    /// </summary>
    private async Task SecondHandoffAsync(WeirTestServer server, string handoffId, string copy)
    {
        var source = Path.Join(Watched, "Film", "film.mkv");
        var handoff = new { eventType = "deluno.processor-handoff", handoffId, libraryId = "lib-1", mediaType = "movies", sourcePath = source, callbackPath = "/api/integrations/processors/events" };
        using (var queued = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/deluno", handoff, SecretHeader))
        {
            Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        }

        await TestDatabase.ExecuteAsync(server, "UPDATE jobs SET status = 'completed' WHERE job_kind = 'processing.file.remux_pass.v1'");
        await TestDatabase.ExecuteAsync(
            server,
            "UPDATE media_manager_handoff_targets SET result = 'completed', output_file = $copy, " +
            "output_written_at = (SELECT written_at FROM handbacks WHERE relative_path = 'Film/film.mkv') WHERE relative_path = 'Film/film.mkv' " +
            "AND handoff_row_id = (SELECT id FROM media_manager_handoffs WHERE source_key = 'deluno' AND handoff_id = $id)",
            ("$copy", copy),
            ("$id", handoffId));
        await TestDatabase.ExecuteAsync(
            server, "UPDATE media_manager_handoffs SET reported_status = 'completed' WHERE source_key = 'deluno' AND handoff_id = $id", ("$id", handoffId));
    }

    private static async Task<HttpResponseMessage> PostOutcomeWithAsync(
        WeirTestServer server, string handoffId, string outcome, string? importedPath, string? reason, IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = await new ApiTestClient(server).SendAsync(
            HttpMethod.Post, $"/api/v1/intake/handoffs/deluno/{handoffId}/outcome", headers: headers ?? SecretHeader, content: DelunoOutcome(outcome, importedPath, reason));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response;
    }

    private static async Task<(bool Released, string Message)> OutcomeAnswerAsync(
        WeirTestServer server, string handoffId, string outcome, string? importedPath = null, string? reason = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var response = await PostOutcomeWithAsync(server, handoffId, outcome, importedPath, reason, headers);
        var body = await Json(response);
        return (body["released"]!.GetValue<bool>(), body["message"]!.GetValue<string>());
    }

    private static async Task AssertTheRefusalStandsAsync(WeirTestServer server, string copy)
    {
        Assert.True(File.Exists(copy), "Weir's copy is kept while a refusal stands");
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome = 'not-imported' AND released_at IS NULL"));
        Assert.StartsWith(
            "Deluno will not import this file: The import dead-lettered.",
            await TestDatabase.ScalarStringAsync(server, "SELECT release_note FROM handbacks"),
            StringComparison.Ordinal);
    }

    // --- identity ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_refusal_is_lifted_by_the_signed_import_of_another_Deluno_connection()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        await SecondHandoffAsync(server, "h2", copy);
        await OwnedByAsync(server, "h1", await DelunoConnectionAsync(server, "Deluno on the NAS"));
        await OwnedByAsync(server, "h2", await DelunoConnectionAsync(server, "Deluno upstairs"));
        await RefuseAsync(server);

        var imported = await OutcomeAnswerAsync(server, "h2", "imported", ImportedElsewhere);

        Assert.Equal((true, "Weir recorded that Deluno imported the file and released its copy."), imported);
        Assert.False(File.Exists(copy));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND released_at IS NOT NULL"));
    }

    [Fact]
    public async Task A_refusal_is_lifted_by_the_signed_import_of_another_manager_without_saying_it_changed_its_mind()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        await RefuseAsync(server);

        using var response = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/radarr", RadarrImport(copy), SecretHeader);

        Assert.Equal(
            """{"status":"ok","source":"radarr","event":"imported","matched":true,"released":true,"message":"Weir removed its copy from the hand-back folder, because Radarr has the file now."}""",
            await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(copy));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND outcome_by = 'Radarr' AND released_at IS NOT NULL"));
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE title LIKE '%after all'"));
    }

    [Fact]
    public async Task A_refusal_from_a_hand_off_Weir_could_not_attribute_to_a_connection_can_be_replaced_by_the_same_kind_of_manager()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        await SecondHandoffAsync(server, "h2", copy);
        await RefuseAsync(server);

        var refused = await OutcomeAnswerAsync(server, "h2", "not-imported", reason: "The second download was removed.");

        Assert.Equal((false, "Weir recorded that the file will not be imported, and kept its copy."), refused);
        Assert.Equal(
            "Deluno will not import this file: The second download was removed. Weir kept its copy in the hand-back folder.",
            await TestDatabase.ScalarStringAsync(server, "SELECT release_note FROM handbacks"));
    }

    [Fact]
    public async Task A_refusal_never_replaces_an_import_that_Weir_could_not_finish_releasing()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        using (var collected = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/radarr", RadarrImport(copy), SecretHeader))
        {
            Assert.Contains("\"released\":true", await collected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // As an import leaves the copy when it could not be removed (in use): recorded, not settled.
        await TestDatabase.ExecuteAsync(server, "UPDATE handbacks SET settled_at = NULL, released_at = NULL");

        var refused = await OutcomeAnswerAsync(server, "h1", "not-imported", reason: "Changed its mind.");

        Assert.Equal(
            (false, "Weir recorded that the file will not be imported. Radarr has already imported it, so Weir left what it recorded about the file as it is."),
            refused);
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND outcome_by = 'Radarr' AND outcome_reason IS NULL"));
    }

    [Fact]
    public async Task A_second_import_leaves_the_first_and_says_only_what_became_of_the_copy()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        await SecondHandoffAsync(server, "h2", copy);
        await OwnedByAsync(server, "h1", await DelunoConnectionAsync(server, "Deluno on the NAS"));
        await OwnedByAsync(server, "h2", await DelunoConnectionAsync(server, "Deluno upstairs"));
        Assert.True((await OutcomeAnswerAsync(server, "h1", "imported", ImportedElsewhere)).Released);

        var second = await OutcomeAnswerAsync(server, "h2", "imported", "/media/elsewhere/Film.mkv");

        Assert.Equal(
            (false, "Weir recorded that Deluno imported the file. Weir had already released its copy, so there was nothing for it to remove."),
            second);
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, $"SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND imported_path = '{ImportedElsewhere}'"));
    }

    [Fact]
    public async Task A_refusal_after_an_import_names_the_manager_who_imported_not_the_one_who_refused()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        using (var collected = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/radarr", RadarrImport(copy), SecretHeader))
        {
            Assert.Contains("\"released\":true", await collected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var refused = await OutcomeAnswerAsync(server, "h1", "not-imported", reason: "Changed its mind.");

        Assert.Equal(
            (false, "Weir recorded that the file will not be imported. Radarr has already imported it, so Weir left what it recorded about the file as it is."),
            refused);
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND outcome_by = 'Radarr' AND outcome_reason IS NULL"));
    }

    // --- an unsigned message ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A server with no instance-wide secret, a Deluno connection that has its own, and a Radarr connection that has none, so
    /// Radarr's messages are accepted unsigned. Returns Deluno's headers.
    /// </summary>
    private static async Task<Dictionary<string, string>> UnsignedRadarrAsync(WeirTestServer server)
    {
        await TestDatabase.SeedAdminAsync(server);
        var admin = new ApiTestClient(server);
        await admin.SignInAsync();
        var deluno = await DelunoConnectionAsync(server, "Deluno");
        await TestDatabase.ExecuteAsync(server, "INSERT INTO media_manager_connections (kind, name, base_url) VALUES ('radarr', 'Radarr', 'http://192.0.2.20:7878')");
        using var generated = await admin.PostAsync($"{ConnectionRoute}/{deluno}/webhook-secret", new { csrf_token = await admin.CsrfAsync() });
        return new Dictionary<string, string> { ["X-Webhook-Secret"] = (await Json(generated))["webhook_secret"]!.GetValue<string>() };
    }

    [Fact]
    public async Task An_unsigned_import_never_erases_a_refusal_Deluno_signed()
    {
        await using var server = await StartAsync(webhookSecret: string.Empty);
        var deluno = await UnsignedRadarrAsync(server);
        var copy = await FinishedHandoffAsync(server, headers: deluno);
        Assert.False((await OutcomeAnswerAsync(server, "h1", "not-imported", reason: "The import dead-lettered.", headers: deluno)).Released);

        using var forged = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/radarr", RadarrImport(copy));

        Assert.Contains("\"matched\":true,\"released\":false", await forged.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await AssertTheRefusalStandsAsync(server, copy);
        Assert.Equal(0, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM activity_events WHERE title = 'Radarr imported film.mkv'"));
    }

    [Fact]
    public async Task A_signed_import_settles_a_copy_an_unsigned_one_only_recorded()
    {
        await using var server = await StartAsync(webhookSecret: string.Empty);
        var deluno = await UnsignedRadarrAsync(server);
        var copy = await FinishedHandoffAsync(server, headers: deluno);
        using (var unsigned = await new ApiTestClient(server).PostAsync("/api/v1/intake/webhook/radarr", RadarrImport(copy)))
        {
            Assert.Contains("\"released\":false", await unsigned.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var signed = await OutcomeAnswerAsync(server, "h1", "imported", ImportedElsewhere, headers: deluno);

        Assert.Equal((true, "Weir recorded that Deluno imported the file and released its copy."), signed);
        Assert.False(File.Exists(copy));
        Assert.Equal(1, await TestDatabase.ScalarAsync(server, "SELECT count(*) FROM handbacks WHERE outcome = 'imported' AND outcome_by = 'Deluno' AND released_at IS NOT NULL"));
    }

    // --- an answer that cannot be tied to the copy's generation --------------------------------------------------------------

    [Theory]
    [InlineData("DELETE FROM media_manager_handoff_targets WHERE handoff_row_id = (SELECT id FROM media_manager_handoffs WHERE handoff_id = 'h2')")]
    [InlineData("UPDATE media_manager_handoff_targets SET output_written_at = NULL WHERE handoff_row_id = (SELECT id FROM media_manager_handoffs WHERE handoff_id = 'h2')")]
    public async Task An_answer_that_names_no_copy_generation_cannot_change_a_copy_the_manager_has_answered(string recordsNoGeneration)
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        await SecondHandoffAsync(server, "h2", copy);
        await TestDatabase.ExecuteAsync(server, recordsNoGeneration);
        await RefuseAsync(server);

        var imported = await OutcomeAnswerAsync(server, "h2", "imported", ImportedElsewhere);

        Assert.Equal(
            (false, "Weir recorded that Deluno imported the file. Weir kept its copy, because it has no record of exactly which file it wrote."),
            imported);
        await AssertTheRefusalStandsAsync(server, copy);
    }

    [Fact]
    public async Task An_answer_that_names_no_copy_generation_still_settles_a_copy_nobody_has_answered()
    {
        await using var server = await StartAsync();
        var copy = await FinishedHandoffAsync(server);
        await SecondHandoffAsync(server, "h2", copy);
        await TestDatabase.ExecuteAsync(server, "UPDATE media_manager_handoff_targets SET output_written_at = NULL WHERE handoff_row_id = (SELECT id FROM media_manager_handoffs WHERE handoff_id = 'h2')");

        var imported = await OutcomeAnswerAsync(server, "h2", "imported", ImportedElsewhere);

        Assert.Equal((true, "Weir recorded that Deluno imported the file and released its copy."), imported);
        Assert.False(File.Exists(copy));
    }
}
