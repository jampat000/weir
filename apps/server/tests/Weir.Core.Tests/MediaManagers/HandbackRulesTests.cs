using Weir.Core.Json;
using Weir.Core.MediaManagers;

namespace Weir.Core.Tests.MediaManagers;

/// <summary>
/// #652: Sonarr's and Radarr's import messages carry the file they imported from and the download's id, and Weir matches
/// that path to the copy it handed back without knowing the manager's name for the folder.
/// </summary>
public sealed class HandbackRulesTests
{
    private static WireObject Dict(string json) => (WireObject)WireJsonParser.Parse(json);

    [Fact]
    public void A_sonarr_download_payload_yields_the_library_path_the_source_path_and_the_download_id()
    {
        // The field names Sonarr's WebhookImportPayload / WebhookEpisodeFile send (v5-develop), camelCase on the wire.
        var imported = ImportEvents.DialectForSource("sonarr")!.Normalize(Dict("""
            {"eventType":"Download","series":{"id":3,"title":"Paper Lanterns"},
             "episodes":[{"id":41,"seasonNumber":1,"episodeNumber":2,"title":"The Second"}],
             "episodeFile":{"id":9,"path":"/tv/Paper Lanterns/Season 01/Paper Lanterns - S01E02.mkv",
                            "sourcePath":"/weir/hand-back/Paper.Lanterns.S01E02/Paper.Lanterns.S01E02.mkv"},
             "downloadClient":"qBittorrent","downloadClientType":"qBittorrent","downloadId":"A1B2C3D4"}
            """))!;

        Assert.Equal(MediaManagerImportEvent.Imported, imported.EventKind);
        Assert.Equal("/tv/Paper Lanterns/Season 01/Paper Lanterns - S01E02.mkv", imported.FilePath);
        Assert.Equal("/weir/hand-back/Paper.Lanterns.S01E02/Paper.Lanterns.S01E02.mkv", imported.SourcePath);
        Assert.Equal("A1B2C3D4", imported.DownloadId);
    }

    [Fact]
    public void A_radarr_download_payload_yields_the_library_path_the_source_path_and_the_download_id()
    {
        // Radarr's WebhookImportPayload / WebhookMovieFile (develop).
        var imported = ImportEvents.DialectForSource("radarr")!.Normalize(Dict("""
            {"eventType":"Download","movie":{"id":7,"title":"The Long Tide","year":2024},
             "movieFile":{"id":12,"path":"/movies/The Long Tide (2024)/The Long Tide (2024).mkv",
                          "sourcePath":"/weir/hand-back/The.Long.Tide.2024/The.Long.Tide.2024.mkv"},
             "downloadId":"SABnzbd_nzo_x1"}
            """))!;

        Assert.Equal("/movies/The Long Tide (2024)/The Long Tide (2024).mkv", imported.FilePath);
        Assert.Equal("/weir/hand-back/The.Long.Tide.2024/The.Long.Tide.2024.mkv", imported.SourcePath);
        Assert.Equal("SABnzbd_nzo_x1", imported.DownloadId);
    }

    [Fact]
    public void An_older_payload_without_a_source_path_still_reads_as_an_import()
    {
        var imported = ImportEvents.DialectForSource("radarr")!.Normalize(Dict(
            """{"eventType":"Download","movie":{"id":7,"title":"Film"},"movieFile":{"path":"/movies/Film/film.mkv"}}"""))!;

        Assert.Equal("/movies/Film/film.mkv", imported.FilePath);
        Assert.Null(imported.SourcePath);
        Assert.Null(imported.DownloadId);
    }

    [Theory]
    [InlineData("/data/hand-back/Film/film.mkv", true)]
    [InlineData("\\\\nas\\share\\hand-back\\Film\\film.mkv", true)]
    [InlineData("D:\\Hand-back\\Film\\film.mkv", true)]
    [InlineData("/data/hand-back/Other/film.mkv", false)]
    [InlineData("/data/hand-back/film.mkv", false)]
    [InlineData("Film/film.mkv", false)]
    public void A_managers_path_names_the_copy_only_when_it_ends_with_the_copys_whole_path_below_the_output_folder(string managerPath, bool matches)
    {
        Assert.Equal(matches, HandbackRules.ManagerPathEndsWith(managerPath, ["Film", "film.mkv"]));
    }

    [Fact]
    public void Paths_as_a_manager_writes_them_compare_by_their_parts()
    {
        Assert.True(HandbackRules.SameManagerPath("/data/Film/film.mkv", "/data/Film/film.mkv/"));
        Assert.True(HandbackRules.SameManagerPath("D:\\Data\\Film\\film.mkv", "d:/data/film/FILM.mkv"));
        Assert.False(HandbackRules.SameManagerPath("/data/Film/film.mkv", "/data/Film/other.mkv"));
        Assert.False(HandbackRules.SameManagerPath(null, "/data/Film/film.mkv"));
        Assert.Equal("film.mkv", HandbackRules.FileName("D:\\Data\\Film\\film.mkv"));
    }

    [Theory]
    [InlineData(HandbackRules.NotImported, HandbackRules.Imported, true)]
    [InlineData(HandbackRules.Imported, HandbackRules.NotImported, false)]
    [InlineData(HandbackRules.Imported, HandbackRules.Imported, false)]
    [InlineData(HandbackRules.NotImported, HandbackRules.NotImported, false)]
    [InlineData(null, HandbackRules.Imported, false)]
    public void Only_an_import_replaces_a_refusal(string? recorded, string outcome, bool replaces) =>
        Assert.Equal(replaces, HandbackRules.Supersedes(recorded, outcome));

    private static readonly ManagerSpeaker DelunoNas = new("deluno", 1, true);
    private static readonly ManagerSpeaker DelunoUpstairs = new("deluno", 2, true);
    private static readonly ManagerSpeaker DelunoUnknown = new("deluno", null, true);
    private static readonly ManagerSpeaker Radarr = new("radarr", 1, true);
    private static readonly ManagerSpeaker RadarrUnsigned = new("radarr", 1, false);

    [Theory]
    // Nobody has spoken for the copy.
    [InlineData(null, false, "nas", "nas", HandbackRules.Imported, false, true)]
    [InlineData(null, false, "nas", "unsigned", HandbackRules.Imported, false, true)]
    // A refusal is lifted by any signed import, and replaced by a refusal of the same connection, the same hand-off, or one it
    // cannot tell apart from it.
    [InlineData(HandbackRules.NotImported, true, "nas", "nas", HandbackRules.NotImported, false, true)]
    [InlineData(HandbackRules.NotImported, true, "nas", "upstairs", HandbackRules.NotImported, false, false)]
    [InlineData(HandbackRules.NotImported, true, "nas", "upstairs", HandbackRules.Imported, false, true)]
    [InlineData(HandbackRules.NotImported, true, "nas", "radarr", HandbackRules.Imported, false, true)]
    [InlineData(HandbackRules.NotImported, true, "nas", "unsigned", HandbackRules.Imported, false, false)]
    [InlineData(HandbackRules.NotImported, true, "nas", "unknown", HandbackRules.NotImported, false, false)]
    [InlineData(HandbackRules.NotImported, true, "nas", "unknown", HandbackRules.NotImported, true, true)]
    [InlineData(HandbackRules.NotImported, true, "unknown", "nas", HandbackRules.NotImported, false, true)]
    [InlineData(HandbackRules.NotImported, true, "unknown", "radarr", HandbackRules.NotImported, false, false)]
    // An import is never replaced by a refusal; one that settled the copy is final, and one that could not remove the copy may be
    // retried by another signed import.
    [InlineData(HandbackRules.Imported, true, "nas", "nas", HandbackRules.Imported, false, false)]
    [InlineData(HandbackRules.Imported, true, "nas", "upstairs", HandbackRules.Imported, false, false)]
    [InlineData(HandbackRules.Imported, false, "nas", "upstairs", HandbackRules.Imported, false, true)]
    [InlineData(HandbackRules.Imported, false, "nas", "nas", HandbackRules.NotImported, true, false)]
    [InlineData(HandbackRules.Imported, false, "nas", "upstairs", HandbackRules.NotImported, false, false)]
    [InlineData(HandbackRules.Imported, false, "nas", "unsigned", HandbackRules.Imported, false, false)]
    // What an unsigned message recorded is replaced by any signed word, and by no unsigned one.
    [InlineData(HandbackRules.Imported, true, "unsigned", "radarr", HandbackRules.Imported, false, true)]
    [InlineData(HandbackRules.Imported, true, "unsigned", "radarr", HandbackRules.NotImported, false, true)]
    [InlineData(HandbackRules.Imported, true, "unsigned", "unsigned", HandbackRules.Imported, false, false)]
    public void A_copy_takes_a_word_only_from_the_speaker_whose_it_is_to_change(
        string? copyOutcome, bool settled, string copySpeaker, string speaker, string outcome, bool sameHandoff, bool heard)
    {
        ManagerSpeaker Named(string name) => name switch
        {
            "nas" => DelunoNas,
            "upstairs" => DelunoUpstairs,
            "unknown" => DelunoUnknown,
            "radarr" => Radarr,
            _ => RadarrUnsigned,
        };

        Assert.Equal(heard, HandbackRules.Hears(copyOutcome, settled, copyOutcome is null ? null : Named(copySpeaker), Named(speaker), outcome, sameHandoff));
    }

    [Fact]
    public void A_speaker_Weir_cannot_tell_which_connection_of_is_taken_for_the_same_kind_of_manager_only_where_the_other_is_unattributed()
    {
        Assert.True(DelunoNas.IsSameConnectionAs(new ManagerSpeaker("deluno", 1, true)));
        Assert.False(DelunoNas.IsSameConnectionAs(DelunoUpstairs));
        Assert.True(DelunoNas.IsSameConnectionAs(DelunoUnknown));
        Assert.False(DelunoUnknown.IsSameConnectionAs(DelunoNas));
        Assert.False(Radarr.IsSameConnectionAs(DelunoUnknown));
        Assert.True(DelunoUnknown.IsSameCallerAs(DelunoUnknown));
        Assert.False(DelunoNas.IsSameCallerAs(Radarr));
    }

    [Fact]
    public void The_words_say_what_happened_to_the_copy()
    {
        Assert.Equal("Sonarr imported film.mkv", HandbackRules.OutcomeTitle("Sonarr", HandbackRules.Imported, "film.mkv"));
        Assert.Equal("Deluno will not import film.mkv", HandbackRules.OutcomeTitle("Deluno", HandbackRules.NotImported, "film.mkv"));
        Assert.Equal("Deluno imported film.mkv after all", HandbackRules.OutcomeTitle("Deluno", HandbackRules.Imported, "film.mkv", afterAll: true));
        Assert.Equal(
            "Deluno will not import this file: The release is a sample. Weir kept its copy in the hand-back folder.",
            HandbackRules.NotImportedNote("Deluno", "The release is a sample."));
        Assert.Equal(
            "Weir recorded that Deluno imported the file and released its copy.",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.Imported, removed: 1, gone: 0, kept: 0, firstKeptNote: null));
        Assert.Equal(
            "Weir recorded that the file will not be imported, and kept its copy.",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.NotImported, removed: 0, gone: 0, kept: 1, firstKeptNote: null));
        Assert.Equal(
            "Weir recorded that the file will not be imported. Radarr has already imported it, so Weir left what it recorded about the file as it is.",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.NotImported, removed: 0, gone: 0, kept: 0, firstKeptNote: null, importedBy: "Radarr"));
        Assert.Equal(
            "Deluno had said it would not import this file, and then imported it after all. Weir recorded that Deluno imported the file and released its copy.",
            HandbackRules.AfterAllMessage("Deluno", "Weir recorded that Deluno imported the file and released its copy."));
        Assert.Equal(
            "Waiting for Radarr, which is not answering. Weir will tell it this file is ready when it answers.",
            ManagerWaitMessages.ReportWaiting("Radarr"));
        Assert.Contains("handoff-outcome", IntakeRules.HandoffCapabilities);
    }
}
