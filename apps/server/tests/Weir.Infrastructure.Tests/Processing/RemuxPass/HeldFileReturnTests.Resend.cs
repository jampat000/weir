using Weir.Core.Json;
using Weir.Core.MediaManagers;
using Weir.Core.Processing.RemuxPass;
using Weir.Infrastructure.MediaManagers;

namespace Weir.Infrastructure.Tests.Processing.RemuxPass;

/// <summary>
/// The release sent again at the moments it is hardest to answer: as the first send's pass finishes, as the wait for a gone file
/// ends, from another connection or another manager, and with a pass-through look already started. Every send gets one true answer.
/// </summary>
public sealed partial class HeldFileReturnTests
{
    private static string ResultOfFirstTarget() =>
        "SELECT result FROM media_manager_handoff_targets WHERE handoff_row_id = (SELECT id FROM media_manager_handoffs WHERE handoff_id = 'first')";

    private Task<long> RemuxJobsKeyedAsync(string handoffId) =>
        _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE dedupe_key = '{IntakeRules.RemuxDedupeKey("deluno", handoffId)}'");

    // --- as the first send's pass finishes ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_resend_does_not_take_over_a_pass_whose_send_has_been_told_how_it_ended()
    {
        await SetUpAsync(releaseFolder: false);
        await HandOffAsync("first");
        // The pass has told its send and is still leased, finishing.
        await _fixture.Store.Execute($"UPDATE jobs SET status = 'leased' WHERE job_kind = '{RemuxPassOutcomes.JobKind}'");
        await _fixture.Store.Execute("UPDATE media_manager_handoffs SET state = 'completed', reported_status = 'completed' WHERE handoff_id = 'first'");
        await _fixture.Store.Execute("UPDATE media_manager_handoff_targets SET result = 'completed', message = 'Done.' WHERE handoff_row_id = (SELECT id FROM media_manager_handoffs WHERE handoff_id = 'first')");

        await HandOffAsync("second");
        await FlushOwedReportsAsync();

        Assert.Equal(1, await RemuxJobsKeyedAsync("first"));
        Assert.Equal(1, await RemuxJobsKeyedAsync("second"));
        Assert.Equal("completed", await ScalarTextAsync(ResultOfFirstTarget()));
        Assert.Empty(ReportsFor("first"));
    }

    [Fact]
    public async Task A_replaced_sends_answer_never_overwrites_the_result_a_file_already_has()
    {
        await SetUpAsync(releaseFolder: false);
        await HandOffAsync("first");
        var completed = new WireObject().Set("ok", true).Set("outcome", "live_output_written").Set("relative_media_path", Relative).Set("output_file", _folders.Out(Relative));

        var targets = await _fixture.Db(async uow =>
        {
            var row = (await HandoffLedgerStore.FindAsync(uow, "deluno", "first"))!;
            await _fixture.Targets.FinishAsync(uow, row, Relative, completed);
            await _fixture.Targets.FinishAsync(uow, row, Relative, CompletionReports.SupersededResult(Relative));
            return await _fixture.Targets.ListAsync(uow, row.Id);
        });

        Assert.Equal("completed", Assert.Single(targets).Result);
    }

    [Fact]
    public async Task A_result_never_overwrites_the_answer_a_replaced_send_has_had()
    {
        await SetUpAsync(releaseFolder: false);
        await HandOffAsync("first");
        var completed = new WireObject().Set("ok", true).Set("outcome", "live_output_written").Set("relative_media_path", Relative).Set("output_file", _folders.Out(Relative));

        var targets = await _fixture.Db(async uow =>
        {
            var row = (await HandoffLedgerStore.FindAsync(uow, "deluno", "first"))!;
            await _fixture.Targets.FinishAsync(uow, row, Relative, CompletionReports.SupersededResult(Relative));
            await _fixture.Targets.FinishAsync(uow, row, Relative, completed);
            return await _fixture.Targets.ListAsync(uow, row.Id);
        });

        Assert.Equal("failed", Assert.Single(targets).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pass_that_finishes_after_its_send_was_replaced_answers_the_new_send(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        _beforeReport = async () =>
        {
            // An outcome that leaves no cleaned source behind, so the resend is a new send for the pass and not a repeat.
            await _fixture.Store.Execute("UPDATE files SET processed_source_size = NULL, processed_source_mtime_ns = NULL");
            await HandOffAsync("second");
        };

        await DrainAsync();

        await AssertCleanedAndToldAsync("second");
        await AssertSupersededAsync("first");
    }

    // --- as the wait for a gone file ends -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_resend_that_arrives_as_the_wait_for_a_gone_file_ends_has_its_look_started_at_once(bool releaseFolder)
    {
        await SetUpAsync(releaseFolder);
        await HandOffAsync("first");
        TakeAway();
        // The second look finds the file gone; the file is back, and the release sent again, a moment later.
        _goneConfirmed = async () =>
        {
            BringBack();
            await HandOffAsync("second");
        };

        await DrainAsync();

        await AssertCleanedAndToldAsync("second");
        await AssertSupersededAsync("first");
    }

    // --- from another connection or another manager ------------------------------------------------------------------------

    private async Task AssertRefusedAsync(string handoffId, string host)
    {
        await FlushOwedReportsAsync();
        var sent = Assert.Single(
            _fixture.Http.RequestsTo(HttpMethod.Post, EventsPath), request => WireConvert.Str(((WireObject)request.Json!)["handoffId"]) == handoffId);
        var body = (WireObject)sent.Json!;
        Assert.Equal(host, sent.Uri.Host);
        Assert.Equal("failed", WireConvert.Str(body["status"]));
        Assert.Equal("not_taken", WireConvert.Str(body["failureClass"]));
        Assert.False(body.ContainsKey("disposition"));
        Assert.False(((WireBool)body["sourceRemoved"]).Value);
        Assert.Contains("another connection", WireConvert.Str(body["message"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_release_sent_by_another_connection_of_the_same_manager_is_refused_and_told_to_that_connection()
    {
        await SetUpAsync(releaseFolder: false);
        var other = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.31:5099", "k2");
        await HandOffAsync("first", _connectionId);

        await HandOffAsync("second", other);

        await AssertRefusedAsync("second", "192.0.2.31");
        Assert.Equal(1, await RemuxJobsKeyedAsync("first"));
        Assert.Equal(0, await RemuxJobsKeyedAsync("second"));

        await DrainAsync();

        var done = Assert.Single(ReportsFor("first"));
        Assert.Equal("completed", WireConvert.Str(done["status"]));
        Assert.Equal("192.0.2.30", Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath), request => WireConvert.Str(((WireObject)request.Json!)["handoffId"]) == "first").Uri.Host);
    }

    [Fact]
    public async Task A_release_sent_by_another_manager_is_refused_and_told_to_that_manager()
    {
        await SetUpAsync(releaseFolder: false);
        await _fixture.AddConnectionAsync("radarr", "http://192.0.2.32:7878", "k3");
        await HandOffAsync("first");

        await HandOffAsync("radarr-send", sourceKey: "radarr");

        await AssertRefusedAsync("radarr-send", "192.0.2.32");
        Assert.Equal(1, await RemuxJobsKeyedAsync("first"));
    }

    // --- the replaced send is told after the request, not inside it -----------------------------------------------------

    [Fact]
    public async Task The_replaced_send_is_told_after_the_request_that_replaced_it_and_not_inside_it()
    {
        await SetUpAsync(releaseFolder: false);
        await HandOffAsync("first");

        await HandOffAsync("second");

        Assert.Empty(Reports());
        await FlushOwedReportsAsync();
        Assert.Single(ReportsFor("first"));
    }

    // --- a pass-through look that has been started ------------------------------------------------------------------------

    [Fact]
    public async Task A_pass_through_look_that_has_been_started_still_holds_its_file_until_a_worker_runs_it()
    {
        await SetUpAsync(releaseFolder: false);
        await BookPassThroughLookAsync();
        Advance(TimeSpan.FromMinutes(2));
        BringBack();
        await SweepAsync();

        // The look is started and no worker has leased it yet; a second sweep must not release the file to be scanned beside it.
        await SweepAsync();

        Assert.Equal("on_hold", await StatusAsync());
        Assert.Equal(0, await _fixture.Store.Scalar($"SELECT count(*) FROM jobs WHERE job_kind = '{RemuxPassOutcomes.JobKind}'"));

        await DrainAsync();

        await AssertHandedBackAndToldAsync("first");
    }

    // --- who a report goes to ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_report_for_a_send_whose_connection_is_switched_off_stays_owed_and_is_not_given_to_a_sibling()
    {
        await SetUpAsync(releaseFolder: false, linkedToDeluno: false);
        await _fixture.AddConnectionAsync("deluno", "http://192.0.2.31:5099", "k2");
        await HandOffAsync("first", _connectionId);
        await _fixture.Store.Execute($"UPDATE media_manager_connections SET enabled = 0 WHERE id = {_connectionId}");

        await DrainAsync();

        Assert.Empty(Reports());
        Assert.Equal(1, await _fixture.Store.Scalar("SELECT count(*) FROM media_manager_handoffs WHERE handoff_id = 'first' AND pending_report_json IS NOT NULL"));

        await _fixture.Store.Execute($"UPDATE media_manager_connections SET enabled = 1 WHERE id = {_connectionId}");
        await FlushOwedReportsAsync();

        var sent = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath));
        Assert.Equal("192.0.2.30", sent.Uri.Host);
        Assert.Equal("completed", WireConvert.Str(((WireObject)sent.Json!)["status"]));
    }

    [Fact]
    public async Task A_refusal_never_overwrites_the_result_a_file_already_has()
    {
        await SetUpAsync(releaseFolder: false);
        await HandOffAsync("first");
        var completed = new WireObject().Set("ok", true).Set("outcome", "live_output_written").Set("relative_media_path", Relative).Set("output_file", _folders.Out(Relative));

        var targets = await _fixture.Db(async uow =>
        {
            var row = (await HandoffLedgerStore.FindAsync(uow, "deluno", "first"))!;
            await _fixture.Targets.FinishAsync(uow, row, Relative, completed);
            await _fixture.Targets.FinishAsync(uow, row, Relative, CompletionReports.NotTakenResult(Relative));
            return await _fixture.Targets.ListAsync(uow, row.Id);
        });

        Assert.Equal("completed", Assert.Single(targets).Result);
    }

    [Fact]
    public async Task A_file_cleaned_for_one_connection_answers_another_connections_send_as_a_completed_repeat()
    {
        await SetUpAsync(releaseFolder: false);
        var other = await _fixture.AddConnectionAsync("deluno", "http://192.0.2.31:5099", "k2");
        await HandOffAsync("first", _connectionId);
        await DrainAsync();

        await HandOffAsync("second", other);

        var repeat = Assert.Single(_fixture.Http.RequestsTo(HttpMethod.Post, EventsPath), request => WireConvert.Str(((WireObject)request.Json!)["handoffId"]) == "second");
        var body = (WireObject)repeat.Json!;
        Assert.Equal("192.0.2.31", repeat.Uri.Host);
        Assert.Equal("completed", WireConvert.Str(body["status"]));
        Assert.Equal(WireConvert.Str(Assert.Single(ReportsFor("first"))["outputPath"]), WireConvert.Str(body["outputPath"]));
        Assert.Single(_media.Remuxes);
    }

    // --- a folder send some of whose files were already done ------------------------------------------------------------------

    [Fact]
    public async Task A_folder_resent_after_one_of_its_files_was_done_announces_that_output_and_cleans_nothing_twice()
    {
        await SetUpAsync(releaseFolder: true);
        var second = $"{ReleaseName}.B.mkv";
        _folders.Source($"{ReleaseName}/{second}");
        _media.Probes[second] = FakeMediaRunner.EnglishAndJapanese;
        await HandOffAsync("first");
        // The first video waits; the second is cleaned and reported to the first send.
        await _fixture.Store.Execute(
            $"UPDATE jobs SET not_before = '2099-01-01 00:00:00.000000' WHERE job_kind = '{RemuxPassOutcomes.JobKind}' AND json_extract(payload_json, '$.relative_media_path') = '{Relative}'");
        await DrainAsync();
        Assert.Single(_media.Remuxes);

        await HandOffAsync("second");
        await _fixture.Store.Execute($"UPDATE jobs SET not_before = NULL WHERE job_kind = '{RemuxPassOutcomes.JobKind}'");
        await DrainAsync();

        Assert.Equal(2, _media.Remuxes.Count());
        var report = Assert.Single(ReportsFor("second"));
        Assert.Equal("completed", WireConvert.Str(report["status"]));
        Assert.Equal(2, ((WireArray)report["outputFiles"]).Items.Count);
        await AssertSupersededAsync("first");
    }
}
