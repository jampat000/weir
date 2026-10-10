using Microsoft.Data.Sqlite;
using Weir.Core.Jobs;
using Weir.Core.Json;
using Weir.Infrastructure.Jobs;
using Weir.Infrastructure.Sqlite;

namespace Weir.Infrastructure.Processing.RemuxPass;

/// <summary>
/// The look a pass books at a file it found gone: a pending pass held back until the scan's grace for a vanished file is over
/// (<see cref="GoneSources.LookAgainAfter"/>). Until then it is the file's pass, so nothing else queues or starts one. A file that
/// comes back before the look is due has nothing left to wait for, so whatever sees it back starts the look at once.
/// </summary>
internal static class GoneLooks
{
    /// <summary>The payload and result key counting how many times a pass has found its file gone.</summary>
    public const string Key = "gone_looks";

    private const string StartSql =
        "UPDATE jobs SET not_before = NULL, updated_at = CURRENT_TIMESTAMP " +
        "WHERE id = @id AND status = '" + ProcessingJobStatus.Pending + "' AND not_before IS NOT NULL";

    /// <summary>Whether <paramref name="pass"/> is a look booked at a gone file and still waiting for its time.</summary>
    public static bool IsWaiting(ProcessingJob pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        if (pass.Status != ProcessingJobStatus.Pending || pass.NotBefore is null)
        {
            return false;
        }

        try
        {
            return WireJsonParser.Parse(pass.PayloadJson ?? string.Empty) is WireObject payload &&
                   payload.Get(Key) is WireInteger { Value.Sign: > 0 };
        }
        catch (WireJsonDecodeException)
        {
            return false;
        }
    }

    /// <summary>Starts the look now if <paramref name="pass"/> is a waiting look, inside the caller's write transaction. True when it did.</summary>
    public static bool StartIfWaiting(SqliteConnection connection, SqliteTransaction transaction, ProcessingJob pass) =>
        IsWaiting(pass) && ProcessingJobStore.Execute(connection, transaction, StartSql, ("@id", pass.Id)) > 0;

    /// <summary>Starts the waiting look at this file, if it has one, inside the caller's unit of work. True when one was started.</summary>
    public static async Task<bool> StartForFileAsync(UnitOfWork uow, string relativePath, string mediaScope, long libraryId)
    {
        ArgumentNullException.ThrowIfNull(uow);
        var started = false;
        foreach (var pass in await ActiveRemuxPasses.ForRelativePathAsync(uow, relativePath, mediaScope, libraryId).ConfigureAwait(false))
        {
            if (IsWaiting(pass) && await uow.ExecuteAsync(StartSql, ("@id", pass.Id)).ConfigureAwait(false) > 0)
            {
                started = true;
            }
        }

        return started;
    }
}
