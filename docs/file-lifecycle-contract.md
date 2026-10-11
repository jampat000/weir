# File lifecycle contract

Weir must never report a partial media mutation as successful, and must never expose a partial file at a final path.

## Required mutation pattern

- Write or copy into a staged file first.
- Validate the staged file before final placement where validation is available.
- Expose the final path only through a rename within the destination directory, so the final name only ever appears complete.
- For cross-volume placement, copy into a hidden partial file in the destination directory first, then rename it onto the final path. A plain `File.Move` must not be used across volumes: .NET silently turns it into a copy to the final name, which exposes a half-written file.
- Only record job or Activity success after the final file exists and passes the relevant safety checks.
- Do not delete watched-folder or output-folder material unless the operation has traceable intent and an output safety check has passed.

## Seams

Server code places final media files through these seams only, never through direct final-path copies, direct cross-volume moves or delete-then-copy patterns.

### Processing output: `Weir.Infrastructure.Processing.RemuxPass.FileLifecycle`

- `SafeCopyToFinalAsync` copies the source into a hidden `.{name}.XXXXXXXX.partial` file beside the destination, runs the optional staged-file validation, then renames the partial onto the destination. A failed or cancelled copy, or a failed validation, deletes the partial and leaves the destination untouched.
- `TryHardlinkToFinalAsync` is the same-volume fast path: it creates a hidden `.{name}.XXXXXXXX.link` hard link beside the destination, validates it and renames it onto the destination. It returns false when the link is refused, and the caller falls back to `SafeCopyToFinalAsync`.
- `SafeFinalizeFile` publishes a file Weir has already written in its work folder: the staged file is moved to a hidden `.partial` beside the destination (copied instead if it cannot be moved), then renamed onto the destination. On the same volume this is two renames and no copy.

Remux pass publishing and pass-through delivery both go through these methods. Failures raise `FileLifecycleException` after removing the partial.

### Library mode: `Weir.Infrastructure.LibraryMode.SafeSwap`

`SafeSwap` replaces a file inside a library with its cleaned copy so that a crash, power cut, locked file or concurrent change can never lose the file or overwrite a newer one:

1. Leftovers of an earlier interrupted swap of the same file are put right first.
2. Preflight refuses the swap when the file is missing, hardlinked (unless the workflow allows it), short of free space (file size plus the space the workflow keeps free, 5 GiB unless set), in a folder that is not writable, read-only, or in use.
3. The cleaned copy is written as `<name>.weir-tmp<ext>` beside the original and validated.
4. The original is fingerprinted again. If it changed, the copy is discarded and nothing is replaced.
5. The original is renamed to `<name>.weir-bak<ext>`, then the copy is renamed to the original name. That second rename is the commit.
6. The backup is deleted — or, when the workflow's "keep the original after clean" setting (#735) is on, moved into its originals folder instead (`Weir.Infrastructure.LibraryMode.OriginalsMover`). Either way, a backup that cannot be dealt with is retried by the startup sweep.

Every rename is a same-directory rename that never overwrites: on Windows `MoveFileExW` is called without `MOVEFILE_REPLACE_EXISTING` or `MOVEFILE_COPY_ALLOWED`, and elsewhere `File.Move` is called without overwrite. A file that appears at the destination while Weir works (for example, a media manager importing a newer copy) is never replaced, and a rename can never silently become a copy.

Each step is journalled. A failure before the commit rolls back from the files on disk: the backup is renamed back and the temporary copy is deleted. A crash skips the rollback, and `SwapRecoverySweep` applies the same rules at the next start. Either way exactly one intact file is left under the original name.

**Keeping the original (#735).** Where the backup goes when the setting is on — a per-workflow originals folder, or a `.weir-originals` folder inside whichever library folder held the file, by default — is atomically reserved (`OriginalsMover.Reserve`, an exclusive create, never a check-then-write) and journalled before the commit rename, so a crash after that point always finds the same destination rather than losing the setting's intent, and two cleans reserving a name at the same moment can never both write to it. Filling the reservation is a same-volume rename when possible; otherwise (`OriginalsMover.Fill`) a copy verified against the backup's whole content — not just its size, which a same-size corrupt or preallocated copy would pass — before the backup is deleted; the same copy-verify-delete shape this document's cross-volume rule asks for elsewhere. A name already at the destination is never overwritten: a numbered suffix is reserved instead. A kept original the recovery sweep finds already at its destination but not matching the backup's content (a crash mid-copy, not just an unfilled reservation) is never guessed at: it is recorded once as an Activity event and left for a person.

The originals folder is always excluded from the library scan (`LibraryFileWalker`), whether or not the setting is currently on — an upgrade note, since a library that already had a `.weir-originals` folder (from files placed there by hand, say) will see it disappear from the Library screen even with the setting off. A custom folder inside a library folder must be dot-prefixed, for the same reason a media manager watching that folder must not import it; outside a library folder it can be excluded from the media manager directly, or any name.

A file held by another program is not a failure: the swap reports it as in use and the job is requeued.

## Once per source

A source file is cleaned once. A source is the file at a path with a size and a modification time; once a successful pass has cleaned it and written its copy (`files.processed_source_size` and `processed_source_mtime_ns`, with the `handbacks` row naming the copy), a repeat of the same source is never processed again. It settles as a skip with a reason, never a failure: `CleanedSources.FindAsync` is the one check, and every route that could queue or start a remux pass makes it:

- hand-off intake (`MediaManagerIntake.EnqueueRefineAsync`), for the same hand-off sent again and for another hand-off naming the same download;
- a manual requeue, "Process again" and "Try again" (`RequeueStore`);
- the watched-folder scan's enqueue (`WatchedFolderScanRun`);
- the start of the pass itself (`RemuxPassHandler.SettleRepeatAsync`), before it claims anything, which catches a job queued by any other route or before the check existed. An operator's own track choice or pass-through-unchanged is different work and is not a repeat.

The copy decides what the skip says: while it still exists the file is "Already done: cleaned on <date> into <output path>"; when it is gone because a media manager collected it (the `handbacks` row's outcome is `imported`) it is "Already imported: <manager> collected the cleaned copy on <date>". A copy that is gone with nobody saying why is not a repeat: there is nothing to hand over, so the file is processed again. A changed size or modification time, or a different path, is a new source and goes through.

A skipped repeat of a hand-off is answered like any finished file: a normal `completed` report naming the same output path the first completion named. It carries no `disposition` and never reads as a failure, so the manager does not refuse a good release or search again. Activity records one grey "Skipped: already done" or "Skipped: already imported" line (`processing.file_skipped_repeat`, result `skipped`).

## Keeping space free

Each workflow keeps at least a set amount free (5 GB unless set; 0 turns the check off) on the drive it writes to. Every write checks it before starting:

- the remux pass, on the output drive before the source is read and again before the output is published, and on the work folder's drive before the new copy is written;
- an unchanged publish, on the output drive;
- a pass-through copy handed back after retries run out, on the output drive (`PassThroughDelivery`);
- a library clean, in `SafeSwap` preflight: the file's size plus the space kept free must be free beside the file.

A write that would leave less does not fail and is never recorded as done. The file is put on hold with a plain reason ("Waiting: the output drive has less than X free"), nothing is written, and Weir looks again after 10, 30, then every 60 minutes (`DiskSpaceWaits`), until there is room.

## Output ownership

When the optional ownership settings (`WEIR_CHOWN_OUTPUT`, `WEIR_FILE_MODE_OUTPUT`, `WEIR_DIR_MODE_OUTPUT`) are set on Linux, they are applied at these same publish points: the three `FileLifecycle` methods and the `SafeSwap` commit. A failure to apply them never fails the job.

## Deletion rules

- Missing files are already absent, not success with hidden work.
- A file, or a folder holding it, that is deleted while Weir has work queued for it is nothing to do, not a failure. The remux pass (`RemuxPassHandler`) and pass-through (`ProcessingPassThroughHandler`) settle it as nothing to do, with an Information row ("… is no longer there, so there is nothing to do") and no error, warning, retry or failed job. A share that drops can leave its empty mount point behind, so "gone" is never one look: the file is re-checked after a few seconds (back means processed or handed back as normal), then held with its row, attempts, reason and any hand-picked plan intact, and looked at again after the scan's grace for a vanished file (`GoneSources.LookAgainAfter`). A file still gone then is forgotten, unless its row carries the source Weir last cleaned (`processed_source_size`), which is never dropped: that row goes back to processed with the cleaned source kept and its attempts, reason and retry and hold times cleared, so it is history again and waits on no one. The scan's vanished-file sweep (`VanishedFiles`) does the same, and also covers a file seen gone with no pass queued (waiting to settle, paused, outside its hours, or waiting on a manager): after the same few-second re-check the file is held with the same reason, so its old label is replaced at once, and the file shows `source_gone` in the file list, which offers nothing that needs the file but removing it from the list. The scan, the file watcher's delete events and the sweep all reach it. A file is recorded as no longer there once: the entry is written when it is first held, and the look that forgets it adds none. No row is held or forgotten on a single look, and two looks at once change a row once. A held file that is back before it is forgotten is released to unprocessed, by the scan or, for a workflow whose files are never scanned, by the sweep. A held file whose pass is booked to look again does not wait for that look once it is back: the scan, the sweep or a hand-off for the same file starts the look at once (`GoneLooks`), which processes the file and answers the hand-off that owns the pass (a pass-through look is started the same way). A release the same manager sends again takes over a pass that is queued, waiting or running (`MediaManagerIntake.AdoptActivePass`): the new send is the one the pass reports to, with the output, and the replaced send is closed with a `failed` report that has `failureClass: superseded`, `sourceRemoved: false`, no `disposition` and no output (the manager answers it `202`), and reads as cancelled. A send whose pass has told its manager already is not given that pass: it is a repeat of a cleaned file or has a pass of its own, and a replaced send only ever fills a file with no result. A send that comes from another connection or another manager than the one that owns the pass is refused with `failureClass: not_taken` and `disposition: held`, and each report goes to the connection that sent the hand-off. Both reports are owed and go out with the next delivery, not inside the intake request; a started pass-through counts as the file's pass until a worker runs it. A file sitting directly in the watched folder, with no release folder to remove, is expected and logged at Debug. Only that final look tells the manager that handed the file over: `status: failed`, `failureClass: source_gone`, `sourceRemoved: false`, no `disposition`. A watched folder that is itself missing is a real problem (`GoneSources.HasLeft` is false for it): the scan warns once in plain words until the folder is back, a manual scan says so in its response, and the watcher warns.
- Locked or in-use files must produce an operator-readable skipped or failed reason.
- A file Weir could not read is never deleted as a rejected file (`rejection_kind: unreadable_file`; `RemuxPassHandler.ApplyRejectedFileAction`). Weir cannot tell a damaged file from one that is still arriving or a share that hiccuped, so it waits, looks again, and only then refuses it, leaving the original where it is. No route hands such a file on: under the reject policy the reject job carries `rejection_kind: unreadable_file`, and when no manager can take the rejection it leaves the file rejected where it is instead of falling back to pass-through (`ProcessingRejectHandler`).

## Originals that belong to a media manager

A download in the watched folder is not Weir's. A download client may still be seeding it, and a media manager may still be waiting to import from it. Two rules follow, and both are decided from the workflow's links to media managers (`library_manager_links`, read through `LibraryStore.ManagerLinksAsync` into `WorkflowManagerLinks`) every time work starts, never from a stored setting, so an existing workflow is covered the moment Weir is updated.

**A linked workflow never has its original removed or moved.** A workflow linked to Deluno, Sonarr, Radarr or another media manager keeps its source file, its sidecars and its release or season folder after a successful pass, whatever `remove_original_after_success` says. The saved setting is left as it is, so unlinking the workflow gives it back. Every place that removes a source goes through `ProcessingPathRuntime.RemovesOriginals` or `WorkflowManagerLinks.KeepsOriginals`:

- the post-success cleanup in `RemuxPassRunner.HandleCleanupAfterSuccessAsync`, which covers Movies' release-folder removal and hands TV to `TvSeasonFolderCleanup` (which checks again before it deletes a season);
- the scan's retry of an interrupted movie removal, `WatchedFolderScanOps.RetryCompletedMovieSourceCleanup`, and the scan's decision to attempt it;
- deleting a rejected file: the workflow's "delete rejected files" choice (in the pass and in the scan), Activity's "delete the file" choice (which tells the person to remove the download from their download client instead), and the reject route (`RejectRoutes.ThroughHandoffAsync`);

The reject route has no exception. When Weir tells the manager a release is bad and the manager accepts, a linked workflow's download is left in place: the report says `sourceRemoved: false`, the manager and its download client remove the download, and Activity says "Deluno will remove the download; Weir left it in place."

The sentence a person reads names the manager: "This workflow is linked to Deluno, so the original stays with your download client, which may still be seeding." It is the pass's `source_folder_skip_reason` (Movies) or `tv_season_folder_skip_reason` (TV), and the file's Processed reason says the original was kept for the download client. A workflow that is Weir only, and has the setting off, keeps its own sentence (`RemuxPassRunner.KeptOriginalReason`).

**A workflow linked to Deluno is processed only from Deluno's hand-off.** Deluno sends each finished download to `POST /api/v1/intake/webhook/deluno`, which queues the pass. Weir's own watched-folder scan must never queue work for such a workflow, because the scan cannot know whether the file is still being downloaded, seeded or imported. So no scan is ever queued for it: not by the timer, not by the folder watcher or a save that changes the watched folder, not by "scan now" or "process again" (the server refuses them with "Deluno hands this workflow its downloads, so Weir does not scan its watched folder."), and a scan job that runs for it anyway stops before it looks at the folder. A hand-off that fails is retried by itself (`HandoffRetries`): the failed pass queues its next attempt to start when its backoff ends, carrying the hand-off's origin, instead of waiting for a scan to notice it. Sonarr, Radarr and other managers do not hand files over, so their workflows are still scanned as before, with the first rule applied. A Weir-only workflow is unchanged, remove-original option included.

## Pause

While processing is paused (the header's **Pause processing**, `PUT /api/v1/pause`) nothing changes a media file: no pass starts, no original is removed, no rejected file is deleted, nothing is written to an output folder and no library clean runs. Work that is already running may finish.

- File jobs (remux pass, pass-through, reject, library clean) are gated where a worker claims a job (`ProcessingJobStore.AdmissionPredicate`), so a paused Weir leases none. The library scan and the maintenance sweeps are claimed under the same rule.
- **Keep looking for new files while paused** lets only the watched-folder scan job be claimed. That scan may discover: it records files as waiting, and queues a pass that is due (a failed file's retry), which waits like any other. It never changes a file itself. A file is not judged while paused (the file shows as waiting), so a rejected file is not deleted, and `WatchedFolderScanRun` skips the one removal a scan can still reach, finishing a cleaned movie's original (`CompletedMovieRemoval`), while `ScanAdmissionWindow.Paused`. The first scan after the pause does both.
- A hand-off received while paused is accepted and queued, and its pass waits.
- When the pause ends, what waited runs once.
- The claim is not the only check. Right after a worker claims a job, `ProcessingJobProcessor` asks the pause again, separately (`ProcessingJobStore.PauseForbidsAsync`); a job claimed while paused goes back to pending with its attempt given back, is logged as an error, and never reaches its handler.
- Every change to the pause is an Activity entry (`system.processing_paused`, `system.processing_resumed`) with who made it and until when, written with the change by `SuitePauseService`. A timed pause that runs out is lifted and recorded by `SuitePauseExpiryTask`, or by the next read of the pause, whichever comes first.

A new code path that can change a media file either runs as a claimed file job or checks the pause itself. `ScanWhilePausedTests` and the processing contract `PauseTests` pin this.
