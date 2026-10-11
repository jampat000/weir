namespace Weir.Core.MediaManagers;

/// <summary>
/// What became of a file Weir handed back (#652): the words for a manager's "imported" or "will not import", and how a
/// path the manager reports is matched to Weir's own copy. No filesystem or database access here.
/// </summary>
public static class HandbackRules
{
    /// <summary>The intake capability that says Weir takes <c>POST …/intake/handoffs/{source}/{id}/outcome</c>.</summary>
    public const string OutcomeCapability = "handoff-outcome";

    /// <summary>
    /// The intake capability that says every 409 from the outcome route carries a <c>code</c> naming its case (#664), so a
    /// manager can tell a final refusal from one worth retrying without reading the wording.
    /// </summary>
    public const string OutcomeCodesCapability = "handoff-outcome-codes";

    /// <summary>
    /// 409 code: a different outcome is already recorded for this hand-off and cannot be replaced (see <see cref="Supersedes"/>).
    /// Final; sending it again never succeeds.
    /// </summary>
    public const string OutcomeAlreadyRecordedCode = "outcome_already_recorded";

    /// <summary>409 code: Weir is still working on the hand-off. Worth sending again later.</summary>
    public const string HandoffNotFinishedCode = "handoff_not_finished";

    /// <summary>
    /// 409 code: the hand-off ended without handing back a file to import (failed, rejected or cancelled). Weir works out a hand-off's
    /// state from its jobs and files each time it is asked, so this can still change; a manager may send the outcome again.
    /// </summary>
    public const string HandoffEndedCode = "handoff_ended";

    /// <summary>The manager imported the file.</summary>
    public const string Imported = "imported";

    /// <summary>The manager will not import the file. A manager does not send it for a failure it will retry.</summary>
    public const string NotImported = "not-imported";

    /// <summary>The outcomes a manager may send, in the spelling agreed with Deluno.</summary>
    public static readonly IReadOnlyList<string> Outcomes = [Imported, NotImported];

    /// <summary>
    /// Whether <paramref name="outcome"/> replaces the <paramref name="recorded"/> outcome of the same manager for a hand-off.
    /// Only a later "imported" replaces a "not imported", which changed nothing irreversible; an import is never undone by a
    /// later refusal, and every other repeat keeps the first answer.
    /// </summary>
    public static bool Supersedes(string? recorded, string outcome) => recorded == NotImported && outcome == Imported;

    /// <summary>
    /// Whether a copy takes a manager's new word, given what it already says. Hand-offs of one file, from several connections and
    /// several managers, share the copy, so the word on it is one speaker's at a time:
    /// <list type="bullet">
    /// <item>a copy nobody has spoken for takes any word;</item>
    /// <item>a copy only an unsigned message has spoken for takes any signed word, and an unsigned message changes nothing a
    /// manager has said, since anybody could have sent it;</item>
    /// <item>an import is never replaced by a "will not import", and one that settled the copy is final (an import not yet
    /// settled, because Weir could not remove the copy, may be retried by another import);</item>
    /// <item>a "will not import" is replaced by any signed import, since only the manager that refused can ever lift it
    /// otherwise and the copy would be kept for good, and by the same connection's next refusal (the same hand-off's, or one of
    /// a connection Weir cannot tell apart from it), never by another connection's refusal.</item>
    /// </list>
    /// </summary>
    public static bool Hears(string? copyOutcome, bool copySettled, ManagerSpeaker? copySpeaker, ManagerSpeaker speaker, string outcome, bool sameHandoff)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        if (copyOutcome is null)
        {
            return true;
        }

        if (copySpeaker is { Authenticated: false })
        {
            return speaker.Authenticated;
        }

        if (!speaker.Authenticated)
        {
            return false;
        }

        return copyOutcome == Imported
            ? outcome == Imported && !copySettled
            : outcome == Imported || sameHandoff || speaker.IsSameConnectionAs(copySpeaker);
    }

    /// <summary>How long an unclaimed copy waits before the Cleanup job may remove it, unless a person changes it.</summary>
    public const int DefaultUnclaimedWindowDays = 14;

    /// <summary>The shortest and longest wait Setup › Performance › Cleanup offers.</summary>
    public const int MinUnclaimedWindowDays = 1;

    public const int MaxUnclaimedWindowDays = 365;

    /// <summary>How often the unclaimed hand-back cleanup runs when nobody has chosen: every six hours.</summary>
    public const int DefaultUnclaimedIntervalSeconds = 6 * 3600;

    /// <summary>
    /// Whether <paramref name="managerPath"/>, a path as a manager reports it, names the copy whose path under Weir's output
    /// folder is <paramref name="relativeParts"/>. This reverses <c>HandoffCompletionReporter.TranslateOutputPath</c> when
    /// the manager's own name for the folder is not known, which is the usual case for Sonarr and Radarr: they see Weir's
    /// output folder through a remote path mapping Weir cannot read. The copy's whole path below the output folder (its
    /// folders and its name) must end the manager's path, so a file of the same name in another folder is not it.
    /// </summary>
    public static bool ManagerPathEndsWith(string managerPath, IReadOnlyList<string> relativeParts)
    {
        ArgumentNullException.ThrowIfNull(relativeParts);
        var parts = Parts(managerPath);
        if (relativeParts.Count == 0 || parts.Count <= relativeParts.Count)
        {
            return false;
        }

        var comparison = Comparison(managerPath);
        var offset = parts.Count - relativeParts.Count;
        for (var index = 0; index < relativeParts.Count; index++)
        {
            if (!string.Equals(parts[offset + index], relativeParts[index], comparison))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Two paths as a manager writes them name the same file: separators and a trailing slash do not count.</summary>
    public static bool SameManagerPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var a = Parts(left);
        var b = Parts(right);
        var comparison = Comparison(left);
        return a.Count == b.Count && a.Zip(b).All(pair => string.Equals(pair.First, pair.Second, comparison));
    }

    /// <summary>The last part of a path, in either style.</summary>
    public static string FileName(string path) => Parts(path) is { Count: > 0 } parts ? parts[^1] : string.Empty;

    private static List<string> Parts(string path) =>
        [.. (path ?? string.Empty).Trim().Split('/', '\\').Where(part => part.Length > 0 && part != ".")];

    /// <summary>A Windows-style path (a drive letter or a backslash) ignores case, as Windows does; any other does not.</summary>
    private static StringComparison Comparison(string path)
    {
        var text = (path ?? string.Empty).Trim();
        var windows = text.Contains('\\', StringComparison.Ordinal) || (text.Length >= 2 && text[1] == ':' && char.IsLetter(text[0]));
        return windows || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    // --- words ----------------------------------------------------------------------------------------------------------

    /// <summary>The Activity title for a manager's word on a file.</summary>
    public static string OutcomeTitle(string manager, string outcome, string fileName, bool afterAll = false) =>
        outcome == NotImported ? $"{manager} will not import {fileName}"
        : afterAll ? $"{manager} imported {fileName} after all"
        : $"{manager} imported {fileName}";

    /// <summary>What Activity says when a manager imports a file it had said it would not: <paramref name="message"/> is what Weir did about it.</summary>
    public static string AfterAllMessage(string manager, string message) =>
        $"{manager} had said it would not import this file, and then imported it after all. {message}";

    public static string RemovedNote(string manager) =>
        $"Weir removed its copy from the hand-back folder, because {manager} has the file now.";

    public static string AlreadyGoneNote(string manager) =>
        $"{manager} moved Weir's copy into its library, so there was nothing for Weir to remove.";

    public const string ChangedNote =
        "Weir's copy has changed since Weir wrote it, so Weir left it alone.";

    public const string OutsideNote =
        "Weir's copy is not inside this workflow's output folder any more, so Weir left it alone.";

    public const string UnrecordedNote =
        "Weir kept its copy, because it has no record of exactly which file it wrote.";

    public const string SameAsLibraryNote =
        "The manager's library file is Weir's copy itself, so Weir left it where it is.";

    public static string InUseNote(string reason) =>
        $"Weir could not remove its copy ({reason}). It is still in the hand-back folder; remove it by hand if you want the space back.";

    public static string UnsignedNote(string manager) =>
        $"Weir kept its copy, because {manager}'s messages to Weir carry no webhook secret, so Weir cannot be sure this one came from {manager}. " +
        "Set a webhook secret for it in Setup › Connections › Media managers and Weir will tidy up after its imports.";

    public static string NotImportedNote(string manager, string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? $"{manager} will not import this file. Weir kept its copy in the hand-back folder."
            : $"{manager} will not import this file: {reason.Trim().TrimEnd('.')}. Weir kept its copy in the hand-back folder.";

    /// <summary>Why a word left the copy as it stands: what the copy already says, never the note of the word that put it there.</summary>
    public static string StandingNote(bool released) =>
        released
            ? "Weir had already released its copy, so there was nothing for it to remove."
            : "Weir had already settled its copy, so it left it as it is.";

    public static string UnclaimedNote(long days) =>
        $"No media manager imported it within {days} {(days == 1 ? "day" : "days")}, so Weir removed its copy.";

    public const string UnclaimedGoneNote =
        "Weir's copy had already left the hand-back folder, so there was nothing to remove.";

    /// <summary>What the outcome endpoint says about a hand-off whose files Weir released, kept, or found gone.</summary>
    public static string OutcomeMessage(string manager, string outcome, int removed, int gone, int kept, string? firstKeptNote, string? importedBy = null)
    {
        if (outcome == NotImported)
        {
            return importedBy is not null && kept == 0
                ? $"Weir recorded that the file will not be imported. {importedBy} has already imported it, so Weir left what it recorded about the file as it is."
                : "Weir recorded that the file will not be imported, and kept its copy.";
        }

        if (removed + gone + kept == 0)
        {
            return $"Weir recorded that {manager} imported the file. {UnrecordedNote}";
        }

        if (kept > 0)
        {
            return $"Weir recorded that {manager} imported the file. {firstKeptNote ?? UnrecordedNote}";
        }

        return removed > 0
            ? $"Weir recorded that {manager} imported the file and released its copy."
            : $"Weir recorded that {manager} imported the file. {AlreadyGoneNote(manager)}";
    }
}

/// <summary>
/// The plain words for work held up because a linked media manager is not answering. The heartbeat
/// (<c>ManagerHeartbeatTask</c>) is what notices it answering again.
/// </summary>
public static class ManagerWaitMessages
{
    /// <summary>A hand-back Weir could not report, which the heartbeat sends once the manager answers.</summary>
    public static string ReportWaiting(string manager) =>
        $"Waiting for {manager}, which is not answering. Weir will tell it this file is ready when it answers.";

    /// <summary>The same report, delivered once the manager answered again.</summary>
    public static string ReportDelivered(string manager) =>
        $"{manager} is answering again, and Weir has told it this file is ready.";

    /// <summary>A rejection Weir could not make because the manager did not answer; the original was handed back instead.</summary>
    public static string RejectNotAnswering(string manager) =>
        $"{manager} is not answering, so Weir could not reject this release.";
}
