using Microsoft.Playwright;
using Weir.E2E.Tests.Harness;
using Weir.E2E.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Weir.E2E.Tests;

/// <summary>A file taken off Activity's list and handed over again, unchanged, comes back as imported and is not cleaned a second time.</summary>
public sealed class ForgottenFileLiveTests(E2EServer server) : E2ETestBase(server)
{
    private const float PushMs = 10_000;
    private const float FinishMs = 30_000;
    private const string FileName = "Pulp Fiction 1994.mkv";

    [E2EFact]
    public async Task A_forgotten_file_handed_over_again_reads_imported_in_Activity_and_is_not_cleaned_twice()
    {
        await using var rig = await ProcessingRig.StartAsync();
        await using var context = await NewContextAsync(new ViewportSize { Width = 1280, Height = 720 });
        var activity = await context.NewPageAsync();
        ApplyDefaultTimeout(activity);
        await Navigation.EnsureSignedInAsync(activity, rig.BaseUrl);
        await Navigation.OpenSidebarAsync(activity, "Activity");
        var row = activity.GetByRole(AriaRole.Row).Filter(new() { HasText = FileName });
        await rig.HandOffRootFileAsync("h-root-1", FileName);
        rig.ReleasePass(FileName);
        await Expect(row).ToContainTextAsync("Done", new() { Timeout = FinishMs });
        await rig.WaitForReportAsync();
        await rig.ReportImportedAsync("h-root-1");
        await Expect(row).ToContainTextAsync("Imported by Deluno", new() { Timeout = PushMs });

        await rig.ForgetAsync(FileName);
        await Expect(row).ToHaveCountAsync(0, new() { Timeout = PushMs });

        await rig.HandOffRootFileAgainAsync("h-root-2", FileName);

        await Expect(row).ToContainTextAsync("Imported by Deluno", new() { Timeout = PushMs });
        await Expect(row).Not.ToContainTextAsync("Waiting");
        Assert.Equal(1, rig.RemuxCount);
    }
}
