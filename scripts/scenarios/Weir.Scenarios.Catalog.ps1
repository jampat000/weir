<#
    The scenarios, in run order: what each one does and what it takes to pass. Data only, so the orchestrator, the runner,
    the plan mode, the record and the tests (scripts/scenarios.test.mjs) all read the same list.

    Phase `Fresh` runs on a clean machine after installing the build under test. Phase `Update` runs on a second clean
    machine, after installing the previous release, using it, and then installing the build under test over it.
    `Required = $false` is the one scenario that may be recorded `not-applicable`, and only for the reason it names.
#>

$ScenarioCatalog = @(
    [pscustomobject]@{
        Id = 'fresh-install'; Phase = 'Fresh'; Required = $true
        Title = 'Fresh install'
        Does = 'Runs Weir-win-Setup.exe --silent on the clean machine, as another program installing Weir does, then starts the installed Weir.exe --port 9347 --silent.'
        Passes = 'Setup exits 0; Weir answers /ready; the installed server reports the version under test, with its commit; the tray and the server are running; the server listens on this PC only; the tray shows no window; the tray log says it started.'
    }
    [pscustomobject]@{
        Id = 'first-visit-account'; Phase = 'Fresh'; Required = $true
        Title = 'First visit and account creation'
        Does = 'Opens Weir in a brand-new Microsoft Edge profile (headless) before any account exists, creates the account through Weir''s API, then opens Weir again in another brand-new profile, and signs in.'
        Passes = 'The first page is account creation and the second is the sign-in page; neither ever says a session expired; the new account signs in and Weir knows who it is.'
    }
    [pscustomobject]@{
        Id = 'handoff-film'; Phase = 'Fresh'; Required = $true
        Title = 'A film handed over'
        Does = 'Makes a small film (video, three audio languages, two subtitle tracks, an .nfo beside it), connects a stand-in for Deluno, makes a workflow over folders of its own linked to it, and hands the film to Weir''s intake webhook as Deluno does. Watches the hand-off until it finishes, then the stand-in says it imported the copy.'
        Passes = 'The hand-off reaches completed; the cleaned file is in the output folder with the video and the English audio kept and the other audio and the subtitles removed, and is smaller; the original and its .nfo are still where they were; the stand-in received one completed report naming that file; Activity says it was processed and the report was sent; after the stand-in''s imported answer the file shows Imported.'
    }
    [pscustomobject]@{
        Id = 'handoff-release-with-extra'; Phase = 'Fresh'; Required = $true
        Title = 'A release with a small extra'
        Does = 'Hands over a release folder holding the film, a small extra under the workflow''s minimum size, and an .nfo.'
        Passes = 'The film is cleaned and reported once; the extra is left alone where it was, is not in the output and is not reported, and does not appear as a file that needs the person; every original is still in place.'
    }
    [pscustomobject]@{
        Id = 'handoff-resend'; Phase = 'Fresh'; Required = $true
        Title = 'A hand-off sent again'
        Does = 'Sends a finished hand-off to the webhook a second time, and sends a second hand-off id for the same download.'
        Passes = 'Neither is cleaned a second time: the output file is not rewritten and there is still one job for it; each is answered with the same completed report and output path as the first; Activity says the repeat was left alone.'
    }
    [pscustomobject]@{
        Id = 'pause-resume'; Phase = 'Fresh'; Required = $true
        Title = 'Pause and resume from the tray'
        Does = 'Writes the tray''s pause-request file to ask Weir to pause until resumed, hands over a film during the pause, watches for 20 seconds, then writes the resume request.'
        Passes = 'Weir takes the request and deletes the file; Pause reads paused; the hand-off is accepted and waits, and nothing is cleaned, written or removed while paused; after the resume request it is processed once; Activity shows Paused and Resumed by the tray.'
    }
    [pscustomobject]@{
        Id = 'process-again'; Phase = 'Fresh'; Required = $true
        Title = 'Process again'
        Does = 'Asks Weir to process a file it has already cleaned again, as Process again does in the file''s drawer.'
        Passes = 'The answer says Weir already cleaned this file and skipped it; Activity gets a "Skipped: already done" line; the output is not rewritten and no second job is made.'
    }
    [pscustomobject]@{
        Id = 'deleted-queued-file'; Phase = 'Fresh'; Required = $true
        Title = 'A deleted file that was waiting'
        Does = 'Puts a file in a watched folder, lets Weir find it and wait for it to settle, deletes it, and lets Weir look again.'
        Passes = 'Weir says the file is no longer there and stops waiting for it; it is never reported as failed or needing the person, and adds no warning or error to the log.'
    }
    [pscustomobject]@{
        Id = 'update-requests'; Phase = 'Fresh'; Required = $true
        Title = 'The update buttons'
        Does = 'Presses Check for updates and Download update the way System > About does (their requests to Weir), with the real tray running.'
        Passes = 'Weir offers the buttons only while the tray is alive; each request leaves its flag file, the tray takes it within 30 seconds, and the update state comes to rest with an answer (nothing newer, an update found, or a plain failure message) rather than staying on checking or downloading.'
    }
    [pscustomobject]@{
        Id = 'pre-update-copy'; Phase = 'Fresh'; Required = $true
        Title = 'The copy saved before an update'
        Does = 'Asks the running server for the copy of Weir''s data the tray asks for before it applies an update, through the tray''s request file.'
        Passes = 'The server says it can take the request, answers started and then saved, the copy exists under backups\pre-update and is not empty, and System overview names it as the last update backup.'
    }
    [pscustomobject]@{
        Id = 'workflows-from-deluno'; Phase = 'Fresh'; Required = $false
        Title = 'Workflows set up from Deluno'
        Does = 'Finds the Deluno inside the machine (http://127.0.0.1:7879), gets an API key for it inside the machine by minting one through Deluno''s own local API, connects Weir to it, and waits for Weir to set up its workflows from Deluno''s own libraries and folders. No secret is passed in from outside and none is written down. Recorded not-applicable, with the exact reason, when no Deluno answers or Deluno already has an account (so no key can be minted without its password).'
        Passes = 'Weir''s workflows are linked to Deluno with its folders and the folder chain check shows every step ready, with nothing typed in Weir.'
    }
    [pscustomobject]@{
        Id = 'logs-clean'; Phase = 'Fresh'; Required = $true
        Title = 'Logs with no unexpected warnings'
        Does = 'Reads System > Logs for warnings and errors, and the server and tray log files on disk, after everything above.'
        Passes = 'The only warnings or errors are ones this run caused on purpose and the suite lists by name; none is unexplained.'
    }
    [pscustomobject]@{
        Id = 'update-over-previous'; Phase = 'Update'; Required = $true
        Title = 'An update over the previous release'
        Does = 'On a second clean machine, installs the previous release, creates the account, a workflow and a finished hand-off, then installs the build under test over the running Weir with Setup --silent.'
        Passes = 'Weir is running again on the new version without being started by hand; the account still signs in; the workflow and the Activity from before are still there; when the update changed the database, the copy saved before it exists and System overview names it; a new hand-off is cleaned; the log has no unexpected warnings since the update.'
    }
)
