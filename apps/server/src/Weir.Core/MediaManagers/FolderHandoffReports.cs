using Weir.Core.Json;
using Weir.Core.Media;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Text;

namespace Weir.Core.MediaManagers;

/// <summary>
/// One file a hand-off covers and what its pass came to. <see cref="Result"/> is null until the file has a final result,
/// then one of <see cref="HandoffLedgerRules.Completed"/>, <see cref="HandoffLedgerRules.PassedThrough"/>,
/// <see cref="HandoffLedgerRules.Failed"/>, <see cref="HandoffLedgerRules.Skipped"/> or <see cref="HandoffLedgerRules.Cancelled"/>.
/// <see cref="OutputFile"/> is the copy Weir wrote, as Weir sees it, and <see cref="OutputWrittenAt"/> when that copy was written
/// (null when the report did not record it). <see cref="SourceSize"/> is the size of the file Weir was handed, when it is known.
/// </summary>
public sealed record HandoffTarget(
    string RelativePath, string? Result, string? OutputFile, string? Message, DateTimeOffset? OutputWrittenAt = null, long? SourceSize = null)
{
    public bool Delivered => Result is HandoffLedgerRules.Completed or HandoffLedgerRules.PassedThrough;
}

/// <summary>The <c>outputFiles</c> list a report and the hand-off status carry, and how the ledger stores it.</summary>
public static class HandoffOutputFiles
{
    public static WireArray ToJson(IEnumerable<string> files) => new(files.Select(file => (WireValue)new WireString(file)));

    public static WireValue ToJsonOrNull(IEnumerable<string>? files) => files is null ? WireNull.Instance : ToJson(files);

    public static string Serialize(IEnumerable<string> files) => WireJsonWriter.Dumps(ToJson(files), WireJsonFormat.Compact);

    /// <summary>The stored list, or null when none is stored or it cannot be read.</summary>
    public static IReadOnlyList<string>? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return WireJsonParser.Parse(json) is WireArray list ? [.. list.Items.OfType<WireString>().Select(item => item.Value)] : null;
        }
        catch (WireJsonDecodeException)
        {
            return null;
        }
    }
}

/// <summary>
/// The single report a hand-off of several files gets once every one of them has a final result. The manager is told
/// about all of them at once: <c>outputPath</c> is the folder they were handed back in and <c>outputFiles</c> lists each
/// one, so it never imports the first file to finish and takes the hand-off as done.
/// </summary>
public static class FolderHandoffReports
{
    /// <summary>What one pass's result means for its file: delivered, handed back unchanged, left alone by the workflow's rules, or failed.</summary>
    public static string TargetResult(WireObject result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Get("outcome") is WireString { Value: RemuxPassOutcomes.SkippedGuardrail })
        {
            return HandoffLedgerRules.Skipped;
        }

        if (!CompletionReports.IsSucceeded(result))
        {
            return HandoffLedgerRules.Failed;
        }

        return result.Get("passed_through_after_failure") is WireBool { Value: true } ? HandoffLedgerRules.PassedThrough : HandoffLedgerRules.Completed;
    }

    /// <summary>The ledger state for the whole hand-off: as far along as its least finished file.</summary>
    public static string State(IReadOnlyCollection<HandoffTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return HandoffLedgerRules.Combine([.. targets.Select(target => Counted(target, targets))]);
    }

    /// <summary>
    /// Whether the workflow's rules may leave <paramref name="skipped"/> alone without failing the hand-off: another file was
    /// delivered and this one is smaller than every delivered file. Nothing says which file is the film, so a skipped file
    /// that is not smaller than what was delivered might be the film, and the hand-off failed. A size that is not known
    /// never qualifies.
    /// </summary>
    public static bool MayBeLeftAlone(HandoffTarget skipped, IReadOnlyCollection<HandoffTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(skipped);
        ArgumentNullException.ThrowIfNull(targets);
        var delivered = targets.Where(target => target.Delivered).ToList();
        return skipped.SourceSize is { } size && delivered.Count > 0 && delivered.All(target => target.SourceSize is { } other && size < other);
    }

    private static string Counted(HandoffTarget target, IReadOnlyCollection<HandoffTarget> targets) =>
        target.Result switch
        {
            null => HandoffLedgerRules.Queued,
            HandoffLedgerRules.Skipped when !MayBeLeftAlone(target, targets) => HandoffLedgerRules.Failed,
            var result => result,
        };

    /// <summary>
    /// The report body. Every file delivered is <c>completed</c>, and a file the workflow's rules left alone is named in the
    /// message without failing it; any file that failed makes it <c>failed</c>, and the message says how many succeeded and
    /// what went wrong with the rest, while <c>outputFiles</c> still lists the ones that were delivered. A file left alone
    /// that might be the film (see <see cref="MayBeLeftAlone"/>) fails it too.
    /// </summary>
    public static WireObject BuildBody(HandoffOrigin origin, IReadOnlyList<HandoffTarget> targets, string? outputFolder, IReadOnlyList<string> outputFiles)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(outputFiles);
        var failed = targets.Where(target => target.Result == HandoffLedgerRules.Failed).ToList();
        var skipped = targets.Where(target => target.Result == HandoffLedgerRules.Skipped).ToList();
        var notExtras = skipped.Where(target => !MayBeLeftAlone(target, targets)).ToList();
        var body = CompletionReports.ReportHeader(origin, failed.Count == 0 && notExtras.Count == 0 ? "completed" : "failed");
        if (failed.Count == 0 && notExtras.Count == 0)
        {
            if (!string.IsNullOrEmpty(outputFolder))
            {
                body.Set("outputPath", outputFolder);
            }

            body.Set("message", SuccessMessage(targets, skipped));
        }
        else if (failed.Count == targets.Count && failed.All(target => target.Message == CompletionReports.SupersededMessage))
        {
            body.Set("message", CompletionReports.SupersededMessage)
                .Set("sourceRemoved", false)
                .Set("failureClass", CompletionReports.SupersededFailureClass);
        }
        else
        {
            body.Set("message", FailureMessage(targets, failed, skipped, notExtras))
                .Set("disposition", "held")
                .Set("sourceRemoved", false);
        }

        return body.Set("outputFiles", HandoffOutputFiles.ToJson(outputFiles));
    }

    private static string SuccessMessage(IReadOnlyList<HandoffTarget> targets, List<HandoffTarget> skipped)
    {
        var delivered = targets.Count(target => target.Delivered);
        var passedThrough = targets.Count(target => target.Result == HandoffLedgerRules.PassedThrough);
        var cancelled = targets.Count(target => target.Result == HandoffLedgerRules.Cancelled);
        var message = $"Weir finished {Plural.Of(delivered, "file")}, ready to import from the output folder.";
        if (passedThrough > 0)
        {
            message += $" It could not process {Plural.Of(passedThrough, "file")}, so it handed {Plural.Noun(passedThrough, "that one", "those")} back unchanged.";
        }

        if (cancelled > 0)
        {
            message += $" {Plural.Of(cancelled, "file")} {Plural.Noun(cancelled, "was", "were")} cancelled in Weir.";
        }

        return message + LeftAloneSentence(skipped);
    }

    private static string FailureMessage(
        IReadOnlyList<HandoffTarget> targets, List<HandoffTarget> failed, List<HandoffTarget> skipped, List<HandoffTarget> notExtras)
    {
        var delivered = targets.Count(target => target.Delivered);
        var message = $"Weir finished {delivered.ToString(System.Globalization.CultureInfo.InvariantCulture)} of {Plural.Of(targets.Count, "file")}.";
        if (failed.Count > 0)
        {
            message += $" It could not process {Plural.Of(failed.Count, "file")}: {Reasons(failed)}.";
        }

        message += LeftAloneSentence(skipped);
        if (delivered > 0 && notExtras.Count > 0)
        {
            message += $" Weir did not take {Plural.Noun(notExtras.Count, "that file", "those files")} for {Plural.Noun(notExtras.Count, "an extra", "extras")}, " +
                       $"because {Plural.Noun(notExtras.Count, "it is", "they are")} not smaller than every file it delivered.";
        }

        return message;
    }

    /// <summary>The sentence naming the files the workflow's rules left alone, or nothing when there are none.</summary>
    private static string LeftAloneSentence(List<HandoffTarget> skipped) =>
        skipped.Count == 0 ? string.Empty : $" Weir left {Plural.Of(skipped.Count, "file")} alone under this workflow's rules. {Reasons(skipped)}.";

    private static string Reasons(IEnumerable<HandoffTarget> targets) =>
        string.Join("; ", targets.Select(target => $"{MediaPathNames.Name(target.RelativePath, windows: false)}: {WireStrings.Strip(target.Message ?? string.Empty).TrimEnd('.')}"));
}
