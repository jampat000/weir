using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>What a folder hand-off is told when a newer send took over some of its files, or when Weir would not start it.</summary>
public sealed class ReplacedHandoffReportTests
{
    private static readonly HandoffOrigin Origin = new("deluno", "first", "/api/integrations/processors/events", "Show.S01", "lib-1");

    private static HandoffTarget Done(string path) => new(path, HandoffLedgerRules.Completed, $"/out/{path}", "Weir finished it.");

    private static HandoffTarget Replaced(string path) =>
        new(path, HandoffLedgerRules.Failed, null, CompletionReports.SupersededMessage);

    [Fact]
    public void A_folder_send_some_of_whose_files_were_taken_over_is_replaced_as_a_whole_and_is_not_a_failure()
    {
        var body = FolderHandoffReports.BuildBody(Origin, [Done("a.mkv"), Replaced("b.mkv")], "/out", ["/out/a.mkv"]);

        Assert.Equal("failed", WireConvert.Str(body["status"]));
        Assert.Equal(CompletionReports.SupersededFailureClass, WireConvert.Str(body["failureClass"]));
        Assert.Equal(CompletionReports.SupersededMessage, WireConvert.Str(body["message"]));
        Assert.False(((WireBool)body["sourceRemoved"]).Value);
        Assert.False(body.ContainsKey("disposition"));
        Assert.False(body.ContainsKey("outputPath"));
    }

    [Fact]
    public void A_folder_send_Weir_would_not_start_is_refused_with_the_reason_and_nothing_held()
    {
        var refused = new HandoffTarget("a.mkv", HandoffLedgerRules.Failed, null, CompletionReports.NotTakenMessage);

        var body = FolderHandoffReports.BuildBody(Origin, [refused], null, []);

        Assert.Equal("failed", WireConvert.Str(body["status"]));
        Assert.Equal("not_taken", WireConvert.Str(body["failureClass"]));
        Assert.Equal(CompletionReports.NotTakenMessage, WireConvert.Str(body["message"]));
        Assert.False(body.ContainsKey("disposition"));
        Assert.False(((WireBool)body["sourceRemoved"]).Value);
        Assert.Contains("another connection", CompletionReports.NotTakenMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_to_start_a_single_file_send_is_exactly_failed_not_taken_with_no_disposition()
    {
        var body = CompletionReports.BuildCompletionBody(Origin, CompletionReports.NotTakenResult("a.mkv"));

        Assert.Equal("failed", WireConvert.Str(body["status"]));
        Assert.Equal("not_taken", WireConvert.Str(body["failureClass"]));
        Assert.Equal(CompletionReports.NotTakenMessage, WireConvert.Str(body["message"]));
        Assert.False(body.ContainsKey("disposition"));
        Assert.False(((WireBool)body["sourceRemoved"]).Value);
    }

    [Fact]
    public void A_real_failure_of_another_file_stays_a_failure_when_some_files_were_taken_over()
    {
        var broken = new HandoffTarget("c.mkv", HandoffLedgerRules.Failed, null, "ffmpeg died");

        var body = FolderHandoffReports.BuildBody(Origin, [Done("a.mkv"), Replaced("b.mkv"), broken], "/out", ["/out/a.mkv"]);

        Assert.Equal("failed", WireConvert.Str(body["status"]));
        Assert.Equal("held", WireConvert.Str(body["disposition"]));
        Assert.False(body.ContainsKey("failureClass"));
        var message = WireConvert.Str(body["message"]);
        Assert.Contains("c.mkv", message, StringComparison.Ordinal);
        Assert.Contains("ffmpeg died", message, StringComparison.Ordinal);
        Assert.DoesNotContain("b.mkv", message, StringComparison.Ordinal);
    }
}
