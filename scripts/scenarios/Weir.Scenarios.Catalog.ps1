<#
    The scenarios, in run order: what each one does and what it takes to pass. Data only, so the orchestrator, the runner,
    the plan mode, the record and the tests (scripts/scenarios.test.mjs) all read the same list.

    Phase `WithDeluno` runs FIRST, with no checkpoint restore, on the VM exactly as Deluno's suite leaves it: Deluno set up, Weir
    installed by Deluno's picker (the previous release), and the key file for Deluno's API in the VM. Phase `Fresh` runs SECOND on
    the clean checkpoint, with the build under test installed alone.

    Every scenario is required. A scenario whose preconditions are not met is recorded `not-applicable` with the reason, and a
    record with any not-applicable scenario sets no success status: a release cannot pass without the real-Deluno scenarios.
#>

$ScenarioCatalog = @(
    [pscustomobject]@{
        Id = 'update-over-installed-weir'; Phase = 'WithDeluno'; Required = $true
        Title = 'An update over the Weir Deluno installed'
        Does = 'Takes the Weir Deluno''s picker installed (the previous release, running), signs in (or creates the account when it has none), makes a Weir-only workflow and has Weir clean a film in it, then installs the build under test over the running Weir with Setup --silent. Asks the updated server for the copy of Weir''s data the tray asks for before an update.'
        Passes = 'Weir is running again on the build under test, with its commit, without being started by hand; the tray log says it was running before the install and started again; the account, the workflow and every Activity entry from before are still there; when the update changed the database the copy saved before it exists and System overview names it; the updated server saves the tray''s requested copy under backups\pre-update; a film dropped in afterwards is cleaned.'
    }
    [pscustomobject]@{
        Id = 'workflows-from-deluno'; Phase = 'WithDeluno'; Required = $true
        Title = 'Workflows set up from Deluno'
        Does = 'Gets an API key for the Deluno in the VM from the key file the Deluno session left there (read inside the VM, then deleted), connects Weir to Deluno, or finds the connection Deluno''s Connect Weir already made, and waits for Weir to set up its workflows from Deluno''s libraries and folders.'
        Passes = 'Weir''s workflows are linked to Deluno with their folders from Deluno and the folder chain check shows every step ready, with nothing typed in Weir.'
    }
    [pscustomobject]@{
        Id = 'deluno-film-handoff'; Phase = 'WithDeluno'; Required = $true
        Title = 'A film Deluno handed over'
        Does = 'Lists the hand-offs Deluno really made (its own API, with the key) and follows each film through Weir.'
        Passes = 'At least one film hand-off finished; for every finished film, Weir shows the cleaned file as processed with Deluno''s imported answer recorded against it, and Deluno shows its own import done.'
    }
    [pscustomobject]@{
        Id = 'deluno-release-with-extra'; Phase = 'WithDeluno'; Required = $true
        Title = 'A release with a small extra'
        Does = 'Looks in the workflows Deluno feeds for a release folder holding a film and an extra under the workflow''s minimum size.'
        Passes = 'Such a release exists; its film was cleaned and its extra was left alone, skipped and not failed; and nothing in the workflow needs the person.'
    }
    [pscustomobject]@{
        Id = 'deluno-resend'; Phase = 'WithDeluno'; Required = $true
        Title = 'A hand-off sent again from Deluno'
        Does = 'Asks Deluno to tell Weir again what became of an imported hand-off (Deluno''s own send-again), then reads both sides.'
        Passes = 'Deluno records the repeat as delivered or settled, never refused; Weir keeps the one imported answer and the file it already settled, and raises no failure.'
    }
    [pscustomobject]@{
        Id = 'deluno-outcomes'; Phase = 'WithDeluno'; Required = $true
        Title = 'What Deluno was told about every outcome'
        Does = 'Reads the outcome Deluno recorded for each hand-off and the outcome Weir recorded for the same file.'
        Passes = 'No outcome is pending, refused, never received or given up on; imported on one side is imported on the other, and not-imported on one is not-imported on the other.'
    }
    [pscustomobject]@{
        Id = 'logs-clean-deluno'; Phase = 'WithDeluno'; Required = $true
        Title = 'Logs with no unexpected warnings, with Deluno'
        Does = 'Reads System > Logs, Weir''s server log file and the tray log for warnings and errors since this phase began.'
        Passes = 'None is unexplained: the only ones are ones this run caused on purpose and names.'
    }
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
        Id = 'weir-only-film'; Phase = 'Fresh'; Required = $true
        Title = 'A film dropped into a Weir-only workflow'
        Does = 'Makes a small film (video, three audio languages, two subtitle tracks, an .nfo beside it), makes a workflow with no manager over folders of its own, drops the release into its watched folder and asks Weir to look.'
        Passes = 'Weir cleans the film: the cleaned file is in the output folder with the video and the English audio kept and the other audio and the subtitles removed, and is smaller; the original and its .nfo are still where they were; Activity says it was processed.'
    }
    [pscustomobject]@{
        Id = 'process-again'; Phase = 'Fresh'; Required = $true
        Title = 'Process again'
        Does = 'Asks Weir to process the film it has already cleaned again, as Process again does in the file''s drawer.'
        Passes = 'The answer says Weir already cleaned this file and skipped it; Activity gets a "Skipped: already done" line; the output is not rewritten and no second job is made.'
    }
    [pscustomobject]@{
        Id = 'pause-resume'; Phase = 'Fresh'; Required = $true
        Title = 'Pause and resume from the tray'
        Does = 'Writes the tray''s pause-request file to ask Weir to pause until resumed, with "keep looking for new files" on, drops a film into the watched folder during the pause, watches for 20 seconds, then writes the resume request.'
        Passes = 'Weir takes the request and deletes the file; Pause reads paused; Weir finds the film and holds it, and nothing is cleaned, written or removed while paused; after the resume request it is processed once; Activity shows Paused and Resumed by the tray.'
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
        Passes = 'Weir offers the buttons only while the tray is alive; each request leaves its flag file, the tray takes it, and the update state comes to rest with an answer (nothing newer, an update found, or a plain failure message) rather than staying on checking or downloading.'
    }
    [pscustomobject]@{
        Id = 'logs-clean'; Phase = 'Fresh'; Required = $true
        Title = 'Logs with no unexpected warnings'
        Does = 'Reads System > Logs for warnings and errors, and the server and tray log files on disk, after everything above.'
        Passes = 'The only warnings or errors are ones this run caused on purpose and the suite lists by name; none is unexplained.'
    }
)
