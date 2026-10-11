using Weir.Core.MediaManagers;
using Weir.Core.Processing;
using Weir.Infrastructure.Processing;
using Weir.Infrastructure.Processing.RemuxPass;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.MediaManagers;

public sealed partial class MediaManagerIntake
{
    /// <summary>
    /// A hand-off target whose source Weir has already cleaned is not queued (see <see cref="CleanedSources"/>). It still
    /// ends: Activity says it was left alone, and the manager is answered like any finished file, with the completion
    /// naming the copy Weir wrote the first time, so it never reads a repeat as a failure.
    /// </summary>
    private async Task SettleRepeatsAsync(
        UnitOfWork uow, MediaManagerImportEvent importEvent, IntakeLibrary library, string relativePath, List<(string Target, CleanedEarlier Earlier)> repeats)
    {
        if (repeats.Count == 0)
        {
            return;
        }

        var outputFolder = await OutputFolderAsync(uow, library.Id).ConfigureAwait(false);
        foreach (var (target, earlier) in repeats)
        {
            await CleanedSources.RecordSkipAsync(uow, library.Id, target, earlier, "webhook").ConfigureAwait(false);
            await CleanedSources.SettleRowAsync(uow, library.Id, target, earlier, _time.GetUtcNow()).ConfigureAwait(false);
            if (string.IsNullOrEmpty(importEvent.HandoffId))
            {
                continue;
            }

            var payload = IntakeRules.Payload(importEvent, library, relativePath, target);
            await _reporter.ReportHandoffCompletionAsync(uow, IntakeRules.PayloadJson(payload), earlier.ToResult(target, library.Id, outputFolder)).ConfigureAwait(false);
        }
    }

    private static async Task<string?> OutputFolderAsync(UnitOfWork uow, long libraryId) =>
        await uow.ScalarAsync("SELECT output_folder FROM libraries WHERE id = $id", ("$id", libraryId)).ConfigureAwait(false) is string { Length: > 0 } folder && !string.IsNullOrWhiteSpace(folder)
            ? RemuxPassPaths.Resolve(folder.Trim())
            : null;
}
