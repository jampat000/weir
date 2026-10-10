using System.Net;
using System.Text.Json.Nodes;
using Weir.Contract.Tests.Harness;
using Weir.Contract.Tests.Harness.Fakes;
using Weir.Contract.Tests.Processing;

namespace Weir.Contract.Tests.MediaManagers;

/// <summary>
/// Deluno sends a release a second time (a resend) after Weir has started the first send, so two hand-offs with their own ids name
/// one file and share the one copy Weir wrote. Deluno answers each of them with its own outcome. An outcome, and Weir's reply to
/// it, is about the hand-off it names: the reply and the file's panel never carry the note of another hand-off's outcome.
/// The shapes are the ones the rig run of 10 October 2026 recorded for Retthree, Twicefilm and Twicetwo: a release sent twice,
/// Deluno refusing Weir's report of the first send (HTTP 409), the second send answered at once with the first's output, and
/// then Deluno's "will not import" for the first send and its "imported" for the second.
/// </summary>
[ContractArea("media_managers")]
public sealed class ResentHandoffOutcomeTests
{
    private const string NotImportedReply = "Weir recorded that the file will not be imported, and kept its copy.";
    private const string ImportedReply = "Weir recorded that Deluno imported the file and released its copy.";
    private const string RemovedNote = "Weir removed its copy from the hand-back folder, because Deluno has the file now.";

    public static TheoryData<string, string, string> RecordedResends => new()
    {
        { "Retthree.2020.1080p.WEB-DL.x264-GOLDEN.mkv", "01a127e4ba027712a8dc199202fa262c", "01a127e5a5ce7db3b100b8fb534212ed" },
        { "Twicefilm.2015.1080p.WEB-DL.x264-GOLDEN.mkv", "01a12815c8007dd786efda5972984ffd", "01a12816b4e07f0c9137040e11cde087" },
    };

    [Theory]
    [MemberData(nameof(RecordedResends))]
    public async Task The_import_of_the_second_send_does_not_carry_the_refusal_of_the_first(string releaseFile, string firstId, string secondId)
    {
        await using var rig = await Rig.StartAsync(releaseFile, firstId);
        await rig.SendAsync(firstId);
        rig.TakeTheDownloadAwayAndGiveItBack();
        await rig.SendAsync(secondId);

        var refused = await rig.RefuseAsync(firstId);
        var imported = await rig.ImportAsync(secondId);

        Assert.Equal(NotImportedReply, refused.Message);
        rig.AssertImportedAndReleased(imported);
        await rig.AssertThePanelTellsOneStoryAsync();
    }

    [Fact]
    public async Task A_send_whose_report_Deluno_refused_is_still_answered_with_its_own_note()
    {
        const string firstId = "01a1281976f27f5e9e62861e4b1e141d";
        await using var rig = await Rig.StartAsync("Twicetwo.2014.1080p.WEB-DL.x264-GOLDEN.mkv", firstId);
        await rig.SendAsync(firstId);

        var refused = await rig.RefuseAsync(firstId);

        Assert.Equal(NotImportedReply, refused.Message);
        Assert.True(File.Exists(rig.Copy), "Weir keeps the copy a manager will not import");
        var handback = (await rig.PanelAsync())["handback"]!;
        Assert.Equal("not-imported", (string)handback["outcome"]!);
        Assert.Equal(
            "Deluno will not import this file: Deluno removed Twicetwo.2014.1080p.WEB-DL.x264-GOLDEN.mkv from Transmission, so it will not import it. " +
            "Weir kept its copy in the hand-back folder.",
            (string)handback["release_note"]!);
    }

    [Fact]
    public async Task A_release_folder_sent_twice_gives_the_import_of_the_second_send_only_its_own_note()
    {
        await using var rig = await Rig.StartAsync("Folder.Film.2020.1080p.WEB-DL.x264-GOLDEN.mkv", "folder-first", inReleaseFolder: true);
        await rig.SendAsync("folder-first");
        await rig.SendAsync("folder-second");

        await rig.RefuseAsync("folder-first");
        var imported = await rig.ImportAsync("folder-second");

        rig.AssertImportedAndReleased(imported);
        await rig.AssertThePanelTellsOneStoryAsync();
    }

    [Fact]
    public async Task A_refusal_of_the_first_send_before_the_second_is_sent_does_not_follow_it_into_the_import()
    {
        await using var rig = await Rig.StartAsync("Early.Film.2019.1080p.WEB-DL.x264-GOLDEN.mkv", "early-first");
        await rig.SendAsync("early-first");
        await rig.RefuseAsync("early-first");

        await rig.SendAsync("early-second");
        var imported = await rig.ImportAsync("early-second");

        rig.AssertImportedAndReleased(imported);
        await rig.AssertThePanelTellsOneStoryAsync();
    }

    [Fact]
    public async Task A_refusal_of_the_first_send_during_the_second_sends_processing_waits_and_does_not_follow_it_into_the_import()
    {
        await using var rig = await Rig.StartAsync("During.Film.2018.1080p.WEB-DL.x264-GOLDEN.mkv", "during-first");
        await rig.SendAsync("during-first");
        rig.RemoveTheCopy();
        using var hold = rig.HoldPasses();
        await rig.SendWithoutWaitingAsync("during-second");
        await rig.WaitForAPassUnderWayAsync();

        await rig.AssertWeirIsStillWorkingOnAsync("during-first");
        hold.Release();
        await rig.WaitForSendAsync("during-second");
        var refused = await rig.RefuseAsync("during-first");
        var imported = await rig.ImportAsync("during-second");

        Assert.Equal(NotImportedReply, refused.Message);
        rig.AssertImportedAndReleased(imported);
        await rig.AssertThePanelTellsOneStoryAsync();
    }

    [Fact]
    public async Task Each_sends_refusal_gives_the_copy_its_own_reason()
    {
        await using var rig = await Rig.StartAsync("Twice.Refused.2015.1080p.WEB-DL.x264-GOLDEN.mkv", "refused-first");
        await rig.SendAsync("refused-first");
        await rig.SendAsync("refused-second");

        await rig.RefuseAsync("refused-first", "The first copy was removed from Transmission.");
        var second = await rig.RefuseAsync("refused-second", "The second copy was removed from Deluge.");

        Assert.Equal(NotImportedReply, second.Message);
        Assert.True(File.Exists(rig.Copy));
        var handback = (await rig.PanelAsync())["handback"]!;
        Assert.Equal("The second copy was removed from Deluge.", (string)handback["outcome_reason"]!);
        Assert.Equal(
            "Deluno will not import this file: The second copy was removed from Deluge. Weir kept its copy in the hand-back folder.",
            (string)handback["release_note"]!);
    }

    [Fact]
    public async Task A_refusal_of_the_first_send_after_the_import_of_the_second_changes_nothing()
    {
        await using var rig = await Rig.StartAsync("Late.Film.2017.1080p.WEB-DL.x264-GOLDEN.mkv", "late-first");
        await rig.SendAsync("late-first");
        await rig.SendAsync("late-second");
        rig.AssertImportedAndReleased(await rig.ImportAsync("late-second"));

        var refused = await rig.RefuseAsync("late-first");

        Assert.Equal("not-imported", refused.Outcome);
        Assert.False(refused.Released);
        Assert.Contains("will not be imported", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("kept its copy", refused.Message, StringComparison.Ordinal);
        await rig.AssertThePanelTellsOneStoryAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_second_send_made_while_the_first_is_still_being_worked_on_is_answered_for_itself(bool importFirst)
    {
        await using var rig = await Rig.StartAsync("Busy.Film.2016.1080p.WEB-DL.x264-GOLDEN.mkv", "busy-first");
        using var hold = rig.HoldPasses();
        await rig.SendWithoutWaitingAsync("busy-first");
        await rig.WaitForAPassUnderWayAsync();
        await rig.SendWithoutWaitingAsync("busy-second");
        hold.Release();
        await rig.WaitForSendAsync("busy-first");
        await rig.WaitForSendAsync("busy-second", reported: false);

        if (importFirst)
        {
            rig.AssertImportedAndReleased(await rig.ImportAsync("busy-second"));
            var refused = await rig.RefuseAsync("busy-first");
            Assert.False(refused.Released);
            Assert.DoesNotContain("kept its copy", refused.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(NotImportedReply, (await rig.RefuseAsync("busy-first")).Message);
            rig.AssertImportedAndReleased(await rig.ImportAsync("busy-second"));
        }

        await rig.AssertThePanelTellsOneStoryAsync();
    }

    private sealed record OutcomeReply(string Outcome, bool Released, string Message);

    /// <summary>The server, a fake Deluno that hands a release over, and the one file every send of it names.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly Scenario _scenario;
        private readonly FakeManager _deluno;
        private readonly JsonObject _library;
        private readonly string _releaseName;
        private readonly string _source;
        private readonly string _sourceFile;
        private readonly string _relative;
        private readonly DateTime _sourceModified;

        private Rig(Scenario scenario, FakeManager deluno, JsonObject library, string releaseName, string source, string sourceFile, string relative, string copy)
        {
            _scenario = scenario;
            _deluno = deluno;
            _library = library;
            _releaseName = releaseName;
            _source = source;
            _sourceFile = sourceFile;
            _relative = relative;
            Copy = copy;
            _sourceModified = File.GetLastWriteTimeUtc(sourceFile);
        }

        /// <summary>Weir's copy of the file, which every send shares.</summary>
        public string Copy { get; }

        /// <summary>
        /// Deluno refuses Weir's report of <paramref name="firstId"/> (it had already closed that hand-off) and takes every other one.
        /// </summary>
        public static async Task<Rig> StartAsync(string releaseName, string firstId, bool inReleaseFolder = false)
        {
            var scenario = await Scenario.StartAsync();
            try
            {
                var (deluno, library) = await scenario.DelunoSetupAsync();
                deluno.Route(
                    "POST",
                    Scenario.EventsPath,
                    request => (request.Json as JsonObject)?["handoffId"]?.GetValue<string>() == firstId
                        ? new Reply(409)
                        : new Reply(202, new JsonObject { ["accepted"] = true }));
                var content = FakeMedia.Bytes(FakeMedia.Probe(audioLanguages: ["eng", "fre"]));
                if (!inReleaseFolder)
                {
                    var file = Path.Combine(scenario.Folders.Watched, releaseName);
                    await File.WriteAllBytesAsync(file, content);
                    return new Rig(scenario, deluno, library, releaseName, file, file, releaseName, Path.Combine(scenario.Folders.Output, releaseName));
                }

                var inFolder = scenario.WriteRelease(Path.GetFileNameWithoutExtension(releaseName), "film.mkv", content);
                var folder = Path.GetDirectoryName(inFolder)!;
                var name = Path.GetFileName(folder);
                return new Rig(scenario, deluno, library, releaseName, folder, inFolder, $"{name}/film.mkv", Path.Combine(scenario.Folders.Output, name, "film.mkv"));
            }
            catch
            {
                await scenario.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync() => _scenario.DisposeAsync();

        /// <summary>Deluno hands the release over, and waits until Weir has finished it and told Deluno.</summary>
        public async Task SendAsync(string handoffId)
        {
            await SendWithoutWaitingAsync(handoffId);
            await WaitForSendAsync(handoffId);
        }

        public async Task SendWithoutWaitingAsync(string handoffId) => await _scenario.PostHandoffAsync(handoffId, _source, _releaseName);

        /// <summary>
        /// The hand-off is completed at Weir and, when <paramref name="reported"/>, Deluno has Weir's report of it. A send that took over a
        /// pass another hand-off owns is answered from the file's state and is never reported.
        /// </summary>
        public async Task WaitForSendAsync(string handoffId, bool reported = true)
        {
            await _scenario.WaitForHandoffStateAsync(handoffId, "completed");
            if (reported)
            {
                await Poll.UntilAsync(() => Task.FromResult(Scenario.Callbacks(_deluno, handoffId).FirstOrDefault()), $"Weir's report of {handoffId}");
            }
        }

        /// <summary>"I don't want it": Deluno removes the download and its files, then the data comes back exactly as it was.</summary>
        public void TakeTheDownloadAwayAndGiveItBack()
        {
            var content = File.ReadAllBytes(_sourceFile);
            File.Delete(_sourceFile);
            File.WriteAllBytes(_sourceFile, content);
            File.SetLastWriteTimeUtc(_sourceFile, _sourceModified);
        }

        /// <summary>Nobody has Weir's copy any more and nobody said why, so the next send is cleaned afresh.</summary>
        public void RemoveTheCopy() => File.Delete(Copy);

        /// <summary>From now on a pass stays in progress until the returned hold is released.</summary>
        public Hold HoldPasses()
        {
            var hold = new Hold(Path.Combine(_scenario.Root, "release-remux"));
            _scenario.FakeTools.SetFileRule("*.mkv", new FileRule { RemuxReleaseFile = hold.Path });
            return hold;
        }

        public async Task WaitForAPassUnderWayAsync() =>
            await Poll.UntilAsync(
                async () => (await _scenario.JobsAsync(Scenario.RemuxKind)).Any(job => (string)job["status"]! == "leased"),
                "a pass to be under way");

        public Task<OutcomeReply> RefuseAsync(string handoffId, string? reason = null) =>
            OutcomeAsync(handoffId, "not-imported", null, reason ?? $"Deluno removed {_releaseName} from Transmission, so it will not import it.");

        public Task<OutcomeReply> ImportAsync(string handoffId)
        {
            var parts = _releaseName.Split('.');
            var film = $"{parts[0]} ({parts[1]})";
            return OutcomeAsync(handoffId, "imported", $"C:\\Media\\Movies\\{film}\\{film} [WEB 1080p].mkv", null);
        }

        /// <summary>A refusal sent while the file is being cleaned again is held off, for Deluno to send once Weir has finished.</summary>
        public async Task AssertWeirIsStillWorkingOnAsync(string handoffId)
        {
            var answer = await _scenario.PostOutcomeAsync(handoffId, "not-imported", reason: "Deluno removed the download.");
            Assert.Equal((HttpStatusCode.Conflict, "handoff_not_finished"), (answer.Status, (string)answer.Fields["code"]!));
        }

        private async Task<OutcomeReply> OutcomeAsync(string handoffId, string outcome, string? importedPath, string? reason)
        {
            var answer = await _scenario.PostOutcomeAsync(handoffId, outcome, importedPath, reason);
            Assert.True(answer.Status == HttpStatusCode.OK, answer.ToString());
            return new OutcomeReply((string)answer.Fields["outcome"]!, (bool)answer.Fields["released"]!, (string)answer.Fields["message"]!);
        }

        public void AssertImportedAndReleased(OutcomeReply imported)
        {
            Assert.Equal("imported", imported.Outcome);
            Assert.Equal(ImportedReply, imported.Message);
            Assert.True(imported.Released);
            Assert.False(File.Exists(Copy), "Weir's copy is released once Deluno has the file");
        }

        /// <summary>The file's panel reads that Deluno imported it and Weir released its copy, and says nothing of a refusal.</summary>
        public async Task AssertThePanelTellsOneStoryAsync()
        {
            var handback = (await PanelAsync())["handback"]!;
            Assert.Equal(("imported", "Deluno"), ((string)handback["outcome"]!, (string)handback["outcome_by"]!));
            Assert.Equal(RemovedNote, (string)handback["release_note"]!);
            Assert.Null(handback["outcome_reason"]);
        }

        public async Task<JsonObject> PanelAsync() =>
            await _scenario.FileRowAsync(_library, _relative) ?? throw new InvalidOperationException($"{_relative} has no row");
    }

    /// <summary>What keeps a pass in progress until it is released.</summary>
    private sealed class Hold(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Release() => File.WriteAllText(Path, string.Empty);

        public void Dispose() => Release();
    }
}
