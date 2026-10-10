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
            "Weir recorded that the file will not be imported. Deluno has already imported it from another hand-off, so Weir left what it recorded about the file as it is.",
            HandbackRules.OutcomeMessage("Deluno", HandbackRules.NotImported, removed: 0, gone: 0, kept: 0, firstKeptNote: null, alreadyImported: 1));
        Assert.Equal(
            "Deluno had said it would not import this file, and then imported it after all. Weir recorded that Deluno imported the file and released its copy.",
            HandbackRules.AfterAllMessage("Deluno", "Weir recorded that Deluno imported the file and released its copy."));
        Assert.Equal(
            "Waiting for Radarr, which is not answering. Weir will tell it this file is ready when it answers.",
            ManagerWaitMessages.ReportWaiting("Radarr"));
        Assert.Contains("handoff-outcome", IntakeRules.HandoffCapabilities);
    }
}
