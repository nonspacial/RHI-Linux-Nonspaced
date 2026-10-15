using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Ux;

namespace RHI.Desktop.Views;

/// <summary>
/// w3/parity window glue (new file; vmsplit re-homes it with the dialogs it calls): the nxm:// link confirmation and
/// the hand-over of a downloaded Nexus file to the same confirmed prompts a dropped file gets (#99). App calls
/// <see cref="AttachParity"/> once the window exists.
/// </summary>
public partial class MainWindow
{
    internal void AttachParity(ParityFeatures parity)
    {
        parity.GameLinks.Confirm = ConfirmNxmAsync;
        parity.GameLinks.OfferFile = OfferDownloadedFileAsync;
        if (parity.ComponentInfo is { } info) info.Show = ShowComponentInfoAsync; // w5/catalogue-info (#184)
    }

    private async Task<bool> ConfirmNxmAsync(NxmConfirmation confirmation)
    {
        // A link from the browser may arrive while RHI sits in the tray: show the window (size and state untouched).
        if (!IsVisible) Show();
        Activate();
        var link = confirmation.Link;
        var file = confirmation.File is { } f
            ? (string.IsNullOrWhiteSpace(f.Version) ? f.Name : $"{f.Name} {f.Version}") + (f.SizeKb > 0 ? $" ({f.SizeKb / 1024.0:0.#} MB)" : "")
            : TF("Parity.NxmFileNumber", link.FileId);
        var message = TF("Parity.NxmConfirmText", file, link.ModId, link.GameDomain)
                      + (confirmation.ForGame is { } game ? "\n\n" + TF("Parity.NxmConfirmForGame", game) : "")
                      + "\n\n" + T("Parity.NxmConfirmAfter");
        return await ShowConfirmationAsync(T("Parity.NxmConfirmTitle"), message, T("Parity.NxmConfirmAccept"), double.NaN,
            [new InstallWarningSection(null, new RHI.Core.Services.ManifestInstallWarning("", [new Uri(link.ModPageUrl)]))]);
    }

    /// <summary>A downloaded Nexus file goes to the prompt a dropped file of its kind gets; each asks before installing.</summary>
    private async Task OfferDownloadedFileAsync(string path)
    {
        switch (LinuxDroppedFileClassifier.Classify(path))
        {
            case LinuxDroppedFileKind.LumaAddon:
                await PromptInstallLumaAddonAsync(path, T("Parity.NxmLumaTitle"));
                break;
            case LinuxDroppedFileKind.LumaArchive:
                await PromptInstallLumaAddonAsync(path, T("Parity.NxmLumaTitle"));
                break;
            case LinuxDroppedFileKind.ReShadeAddon:
                await PromptImportCustomAddonAsync(path, T("Parity.NxmAddonTitle"));
                break;
        }
    }
}
