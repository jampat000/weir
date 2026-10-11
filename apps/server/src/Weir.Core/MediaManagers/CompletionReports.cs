using Weir.Core.Json;
using Weir.Core.Processing;
using Weir.Core.Processing.RemuxPass;
using Weir.Core.Text;

namespace Weir.Core.MediaManagers;

/// <summary>The manager-supplied half of a hand-off, carried on the job payload.</summary>
public sealed record HandoffOrigin(string SourceKey, string? HandoffId, string? CallbackPath, string? ReleaseName, string? LibraryId = null)
{
    /// <summary>Reads the payload's <c>origin</c>; null unless it names its source.</summary>
    public static HandoffOrigin? FromPayload(WireValue? payload)
    {
        if (payload is not WireObject dict || dict.Get("origin") is not WireObject origin)
        {
            return null;
        }

        var sourceKey = WireStrings.Strip(Truthy(origin.Get("source_key")) is { } value ? WireConvert.Str(value) : string.Empty);
        if (sourceKey.Length == 0)
        {
            return null;
        }

        return new HandoffOrigin(
            sourceKey,
            OptionalText(origin.Get("handoff_id")),
            OptionalText(origin.Get("callback_path")),
            OptionalText(origin.Get("release_name")),
            OptionalText(origin.Get("library_id")));
    }

    /// <summary>A truthy value as stripped text, or null when absent, falsy or blank.</summary>
    public static string? OptionalText(WireValue? value)
    {
        if (Truthy(value) is not { } present)
        {
            return null;
        }

        var text = WireStrings.Strip(WireConvert.Str(present));
        return text.Length == 0 ? null : text;
    }

    private static WireValue? Truthy(WireValue? value) => value is not null && value.IsTruthy ? value : null;
}

/// <summary>Whether a manager accepted a report. <see cref="Accepted"/> is only ever true on a 2xx answer.</summary>
public sealed record HandoffReportDelivery(bool Accepted, string Status);

/// <summary>The completion report body a manager receives, and its wording.</summary>
public static class CompletionReports
{
    public const string ProcessorName = "Weir";

    public const string PassThroughAfterFailureMessage =
        "Weir could not process this file, so it handed the original back unchanged; it is ready to import.";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly HashSet<string> SuccessOutcomes = new(StringComparer.Ordinal) { "live_output_written", "live_skipped_not_required" };

    /// <summary>The <c>failureClass</c> of a report for a download that is no longer where the manager put it.</summary>
    public const string SourceGoneFailureClass = "source_gone";

    public const string SourceGoneMessage = "The download is no longer there, so Weir had nothing to do.";

    /// <summary>The <c>outcome</c> of the result Weir files for a hand-off the manager has since replaced by sending the same release again.</summary>
    public const string SupersededOutcome = "superseded";

    /// <summary>The <c>failureClass</c> of a report to a replaced hand-off: it settles that hand-off and asks the manager to do nothing.</summary>
    public const string SupersededFailureClass = "superseded";

    public const string SupersededMessage = "This release was sent again, so Weir is answering the newer send and has nothing to add here.";

    /// <summary>The result that closes a hand-off a newer one for the same file has taken over.</summary>
    public static WireObject SupersededResult(string relativeMediaPath) =>
        new WireObject().Set("ok", false).Set("outcome", SupersededOutcome).Set("relative_media_path", relativeMediaPath);

    public static bool IsSuperseded(WireObject result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Outcome(result) == SupersededOutcome;
    }

    /// <summary>The <c>outcome</c> of the result Weir files for a send it did not start, because it is already working on that file for another media manager.</summary>
    public const string NotTakenOutcome = "not_taken";

    public const string NotTakenFailureClass = "not_taken";

    public const string NotTakenMessage = "Weir is already working on this file for another media manager, so it did not start this send.";

    /// <summary>The result that answers a send Weir did not take because the file's pass belongs to another manager.</summary>
    public static WireObject NotTakenResult(string relativeMediaPath) =>
        new WireObject().Set("ok", false).Set("outcome", NotTakenOutcome).Set("relative_media_path", relativeMediaPath);

    public static bool IsNotTaken(WireObject result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Outcome(result) == NotTakenOutcome;
    }

    /// <summary>A pass that ended because its file left the watched folder (and stayed gone).</summary>
    public static bool IsSourceGone(WireObject result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Outcome(result) == RemuxPassOutcomes.SourceGone;
    }

    private static string Outcome(WireObject result) =>
        WireStrings.Strip(result.Get("outcome") is { IsTruthy: true } value ? WireConvert.Str(value) : string.Empty);

    /// <summary><c>ok</c> and an outcome that actually wrote or verified a file.</summary>
    public static bool IsSucceeded(WireObject result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Get("ok") is { IsTruthy: true } && SuccessOutcomes.Contains(Outcome(result));
    }

    /// <summary>
    /// The body reported to the manager when a hand-off of one file finishes. <c>outputFiles</c> lists the output file, or
    /// nothing when there is none, the same field a hand-off of several files reports (<see cref="FolderHandoffReports"/>). A rejection reports
    /// <paramref name="sourceRemoved"/>: false when Weir leaves the download for the manager and its client to remove.
    /// </summary>
    public static WireObject BuildCompletionBody(HandoffOrigin origin, WireObject result, string? outputPath = null, bool rejected = false, bool sourceRemoved = true)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(result);
        var succeeded = IsSucceeded(result);
        var body = ReportHeader(origin, succeeded ? "completed" : "failed");
        var outputFiles = new List<string>();
        if (succeeded)
        {
            WireValue? outputFile = !string.IsNullOrEmpty(outputPath) ? new WireString(outputPath) : result.Get("output_file");
            if (outputFile is WireString file && WireStrings.Strip(file.Value).Length > 0)
            {
                body.Set("outputPath", WireStrings.Strip(file.Value));
                outputFiles.Add(WireStrings.Strip(file.Value));
            }

            body.Set("message", MessageFor(result));
        }
        else
        {
            body.Set("message", MessageFor(result));
            if (IsSuperseded(result))
            {
                body.Set("sourceRemoved", false);
                body.Set("failureClass", SupersededFailureClass);
            }
            else if (IsNotTaken(result))
            {
                body.Set("disposition", "held");
                body.Set("sourceRemoved", false);
                body.Set("failureClass", NotTakenFailureClass);
            }
            else if (IsSourceGone(result))
            {
                // The one report with no disposition: the download left the watched folder, so there is nothing to hold or reject.
                body.Set("sourceRemoved", false);
                body.Set("failureClass", SourceGoneFailureClass);
            }
            else
            {
                if (rejected)
                {
                    body.Set("disposition", "rejected");
                    body.Set("sourceRemoved", sourceRemoved);
                }
                else if (result.Get("rejected_cleanup_status") is WireString { Value: "deleted" })
                {
                    body.Set("sourceRemoved", true);
                }
                else
                {
                    body.Set("disposition", "held");
                    body.Set("sourceRemoved", false);
                }

                if (result.Get("failure_class") is WireString failureClass && WireStrings.Strip(failureClass.Value).Length > 0)
                {
                    body.Set("failureClass", WireStrings.Strip(failureClass.Value));
                }
            }
        }

        return body.Set("outputFiles", HandoffOutputFiles.ToJson(outputFiles));
    }

    /// <summary>The fields every report starts with: which hand-off, how it ended, and who is reporting.</summary>
    public static WireObject ReportHeader(HandoffOrigin origin, string status)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var body = new WireObject()
            .Set("handoffId", origin.HandoffId)
            .Set("status", status)
            .Set("processorName", ProcessorName);
        if (!string.IsNullOrEmpty(origin.LibraryId))
        {
            body.Set("libraryId", origin.LibraryId);
        }

        if (!string.IsNullOrEmpty(origin.ReleaseName))
        {
            body.Set("releaseName", origin.ReleaseName);
        }

        return body;
    }

    /// <summary>What one pass came to, in the words a report uses: what changed when it succeeded, else why it failed.</summary>
    public static string MessageFor(WireObject result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return IsSucceeded(result) ? SuccessMessage(Outcome(result), result) : IsSuperseded(result) ? SupersededMessage : IsNotTaken(result) ? NotTakenMessage : IsSourceGone(result) ? SourceGoneMessage : FailureMessage(result);
    }

    private static string SuccessMessage(string outcome, WireObject result)
    {
        if (result.Get(CleanedEarlier.ResultKey) is WireBool { Value: true })
        {
            return CleanedEarlier.ReportMessage;
        }

        if (result.Get("passed_through_after_failure") is WireBool { Value: true })
        {
            return PassThroughAfterFailureMessage;
        }

        if (result.Get("pass_through_unchanged") is WireBool { Value: true })
        {
            return "The operator passed this file through unchanged; it is ready in the output folder.";
        }

        if (outcome == "live_skipped_not_required")
        {
            return "No remux was needed; the file was already in the wanted shape.";
        }

        var parts = new List<string>();
        if (result.Get("removed_audio") is WireArray { Items.Count: > 0 } audio)
        {
            parts.Add(Plural.Of(audio.Items.Count, "audio track"));
        }

        if (result.Get("removed_subtitles") is WireArray { Items.Count: > 0 } subtitles)
        {
            parts.Add(Plural.Of(subtitles.Items.Count, "subtitle track"));
        }

        return parts.Count == 0 ? "Remux finished." : "Removed " + string.Join(" and ", parts) + ".";
    }

    private static string FailureMessage(WireObject result)
    {
        foreach (var key in new[] { "reason", "output_completeness_note", "source_folder_skip_reason" })
        {
            if (result.Get(key) is WireString text && WireStrings.Strip(text.Value).Length > 0)
            {
                return WireStrings.Strip(text.Value);
            }
        }

        return "The processing pass did not produce a usable output.";
    }

    /// <summary>
    /// Joins <paramref name="parts"/> onto the manager's folder in the manager's own path style (Windows when the
    /// folder has a backslash or drive letter), since its host is not necessarily this one.
    /// </summary>
    public static string ManagerPathJoin(string managerFolder, IEnumerable<string> parts)
    {
        ArgumentNullException.ThrowIfNull(managerFolder);
        var text = WireStrings.Strip(managerFolder);
        var windows = text.Contains('\\', StringComparison.Ordinal) || (text.Length >= 2 && text[1] == ':' && char.IsLetter(text[0]));
        var separator = windows ? '\\' : '/';
        var normalized = windows ? text.Replace('/', '\\') : text;
        string anchor;
        string rest;
        if (windows)
        {
            if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var pieces = normalized[2..].Split('\\');
                var share = pieces.Length > 1 ? $@"\\{pieces[0]}\{pieces[1]}\" : @"\\" + normalized[2..];
                anchor = share;
                rest = pieces.Length > 2 ? string.Join('\\', pieces.Skip(2)) : string.Empty;
            }
            else if (normalized.Length >= 2 && normalized[1] == ':')
            {
                var rooted = normalized.Length >= 3 && normalized[2] == '\\';
                anchor = normalized[..2] + (rooted ? "\\" : string.Empty);
                rest = normalized[(rooted ? 3 : 2)..];
            }
            else if (normalized.StartsWith('\\'))
            {
                anchor = "\\";
                rest = normalized[1..];
            }
            else
            {
                anchor = string.Empty;
                rest = normalized;
            }
        }
        else if (normalized.StartsWith("//", StringComparison.Ordinal) && !normalized.StartsWith("///", StringComparison.Ordinal))
        {
            anchor = "//";
            rest = normalized[2..];
        }
        else if (normalized.StartsWith('/'))
        {
            anchor = "/";
            rest = normalized.TrimStart('/');
        }
        else
        {
            anchor = string.Empty;
            rest = normalized;
        }

        var components = rest.Split(separator).Concat(parts).Where(part => part.Length > 0 && part != ".").ToList();
        var joined = string.Join(separator, components);
        var result = anchor + joined;
        return result.Length == 0 ? "." : result;
    }
}
