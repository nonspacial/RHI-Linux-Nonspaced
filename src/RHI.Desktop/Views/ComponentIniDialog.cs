using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.Views;

/// <summary>
/// w5/ini-presets: the ⚙ of the ReShade, ReLimiter, Display Commander and Luma rows (#181; upstream 2.8.7 Rs/Ul/Dc cog),
/// on the shared ⚙ layout (#297, upstream 2.9.0 <c>CogDialog</c>): ReShade's Loading card, Display Commander's settings
/// (two per row), the Files card — "Deploy X.ini" from the user's template, its backup, Open X.ini / log as upstream and the
/// INI templates folder — and ReShade's presets; Luma's Unreal Engine rows, or a line saying there is nothing to set. Every
/// result shows in the ⚙'s status line.
/// </summary>
public static class ComponentIniDialog
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public static Window Create(MainWindowViewModel vm, ManagedComponentKind kind, string componentName, Func<string, string?> openFolder)
        => Build(vm, kind, componentName, openFolder).Build(height: kind is ManagedComponentKind.ReShade or ManagedComponentKind.DisplayCommander ? 680 : 460);

    /// <summary>The ⚙'s content (tests read it without a window).</summary>
    internal static CogDialog Build(MainWindowViewModel vm, ManagedComponentKind kind, string componentName, Func<string, string?> openFolder)
    {
        var presenter = vm.IniPresets;
        var cog = new CogDialog(componentName, vm.SelectedGame?.Name);
        if (kind == ManagedComponentKind.LumaFramework)
        {
            // #189 / #297: Luma's settings are upstream's Unreal Engine rows; any other game says it has nothing to set.
            if (presenter.LumaApplies()) LumaSection(cog, presenter);
            else cog.AddEmpty(T("Cog.LumaNotUnreal"));
            return cog;
        }

        if (kind == ManagedComponentKind.ReShade && presenter.ReShadeAsiMode().Offered) AsiSection(cog, presenter); // #283
        if (kind == ManagedComponentKind.DisplayCommander && presenter.DisplayCommanderSettings() is { } dc)
            DisplayCommanderSection(cog, presenter, dc); // #282, above the Files card as upstream
        FilesSection(cog, presenter, kind, openFolder);
        if (kind == ManagedComponentKind.ReShade && presenter.PresetsApply()) PresetSection(cog, presenter, openFolder); // #180: RHI's ReShade only
        return cog;
    }

    /// <summary>
    /// Upstream's "Files" card: Deploy X.ini (review finding 2: a changed relimiter.ini / DisplayCommander.ini is replaced only
    /// after an inline yes, and kept beside it as a backup that Restore puts back), Open X.ini and the log as upstream, and
    /// the INI templates folder.
    /// </summary>
    private static void FilesSection(CogDialog cog, IniPresetsPresenter presenter, ManagedComponentKind kind, Func<string, string?> openFolder)
    {
        cog.AddSection(T("Cog.Files"));
        var file = IniPresetsPresenter.IniFileName(kind);
        var deploy = CogDialog.StyledButton(UiLocalizationService.Format("Ini.DeployFile", file), CogButtonKind.Primary, "IniDeploy");
        CogLayout.Tip(deploy, T(kind == ManagedComponentKind.ReShade ? "Ini.DeployReShadeTip" : "Ini.DeployAddonTip"));
        var confirmText = new TextBlock { Name = "IniDeployChangedAsk", TextWrapping = TextWrapping.Wrap, Classes = { "cogWarning" } };
        var confirmYes = CogDialog.StyledButton(T("Ini.DeployReplaceConfirm"), CogButtonKind.Danger, "IniDeployReplace");
        var confirmNo = CogDialog.StyledButton(T("Win.Cancel"), CogButtonKind.Secondary, "IniDeployKeep");
        var confirmButtons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        confirmButtons.Children.Add(confirmYes);
        confirmButtons.Children.Add(confirmNo);
        var confirm = new StackPanel { Spacing = 6, IsVisible = false };
        confirm.Children.Add(confirmText);
        confirm.Children.Add(confirmButtons);

        var restore = CogDialog.StyledButton(UiLocalizationService.Format("Ini.RestoreBackup", file), CogButtonKind.Secondary, "IniRestoreBackup");
        var opens = presenter.OpenFiles(kind).Select((f, i) => cog.MakeOpenFileButton(f.Label, f.Path, $"IniOpenFile{i}")).ToArray();
        void Refresh()
        {
            var backup = presenter.AddonIniBackup(kind);
            restore.IsVisible = backup is not null;
            CogLayout.Tip(restore, backup is null ? null : UiLocalizationService.Format("Ini.RestoreBackupTip", backup));
            var now = presenter.OpenFiles(kind);
            for (var i = 0; i < opens.Length && i < now.Count; i++) opens[i].IsEnabled = now[i].Path is { } path && File.Exists(path);
        }
        Refresh();

        async Task DeployAsync(bool replaceChanged)
        {
            deploy.IsEnabled = false;
            try { cog.SetStatus(await presenter.DeployForSelectedAsync(kind, replaceChanged)); }
            finally
            {
                deploy.IsEnabled = true;
                Refresh();
            }
        }
        deploy.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            if (presenter.DeployNeedsConfirmation(kind))
            {
                confirmText.Text = UiLocalizationService.Format("Ini.DeployChangedAsk", file);
                confirm.IsVisible = true;
                return;
            }
            confirm.IsVisible = false;
            await DeployAsync(replaceChanged: false);
        }, nameof(ComponentIniDialog));
        confirmNo.Click += (_, _) => confirm.IsVisible = false;
        confirmYes.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            confirm.IsVisible = false;
            await DeployAsync(replaceChanged: true);
        }, nameof(ComponentIniDialog));
        restore.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            restore.IsEnabled = false;
            try { cog.SetStatus(await presenter.RestoreAddonIniAsync(kind)); }
            finally
            {
                restore.IsEnabled = true;
                Refresh();
            }
        }, nameof(ComponentIniDialog));

        var templates = CogDialog.StyledButton(T("Names.OpenOptiScalerTemplates"), CogButtonKind.Secondary, "IniOpenTemplates");
        CogLayout.Tip(templates, UiLocalizationService.Format("Ini.TemplatesFolderTip", presenter.TemplatesFolder));
        templates.Click += (_, _) =>
        {
            if ((presenter.PrepareTemplatesFolder() ?? openFolder(presenter.TemplatesFolder)) is { } error) cog.SetError(error);
        };

        cog.AddButtons([deploy, restore, .. opens, templates]);
        cog.AddContent(confirm);
    }

    /// <summary>
    /// #282 (upstream 2.8.9 <c>DcCogButton_Click</c>): Display Commander's main settings, with its own names — FPS Limit
    /// (No limit, the VRR presets or a custom value), then two per row as upstream: FPS Limiter Mode, Inject Reflex, Window
    /// Mode, the four Reflex options and Win+Up grace time — written to the game's DisplayCommander.ini at once; a refused
    /// change goes back to what the file holds. "Open DisplayCommander.ini" is in the Files card.
    /// </summary>
    private static void DisplayCommanderSection(CogDialog cog, IniPresetsPresenter presenter, DisplayCommanderSettings settings)
    {
        cog.AddIntro(T("Dc.Intro"));
        var current = settings;
        async Task<bool> WriteAsync(string key, string value, string label, string shown)
        {
            var (success, line) = await presenter.SetDisplayCommanderAsync(key, value, label, shown);
            cog.SetResult(success, line);
            current = presenter.DisplayCommanderSettings() ?? current;
            return success;
        }
        CogCell Pick(string name, string label, IReadOnlyList<string> items, int index, string tipKey, Func<int, (string Key, string Value)> write)
            => cog.CompactCombo(label, T(tipKey), items, index, async i =>
            {
                var (key, value) = write(i);
                return await WriteAsync(key, value, label, items[i]);
            }, name: name);
        CogCell OnOff(string name, string key, string label, string tipKey, bool on)
            => Pick(name, label, OptionLists.OffOn.Items, on ? 1 : 0, tipKey, i => (key, i == 1 ? "1" : "0"));
        static string Invariant(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // ── Frame Rate ──
        cog.AddSection(T("Dc.FrameRate"));
        FpsLimitRow();
        var limiterModes = new[] { T("Dc.LimiterDc"), "NVIDIA Reflex" };
        var windowModes = new[] { "Dc.WindowNoChanges", "Dc.WindowBorderlessFullscreen", "Dc.WindowBorderlessWindowed", "Dc.WindowNoExclusive" }
            .Select(T).ToArray();
        cog.AddCompactGrid(
            Pick("DcFpsLimiterMode", "FPS Limiter Mode", limiterModes, current.FpsLimiterMode, "Dc.FpsLimiterModeTip",
                i => (DisplayCommanderSettingsService.FpsLimiterModeKey, Invariant(i))),
            OnOff("DcInjectReflex", DisplayCommanderSettingsService.InjectReflexKey, "Inject Reflex", "Dc.InjectReflexTip", current.InjectReflex),
            Pick("DcWindowMode", "Window Mode", windowModes, current.WindowMode, "Dc.WindowModeTip",
                i => (DisplayCommanderSettingsService.WindowModeKey, Invariant(i))),
            null);

        // ── Reflex (a product name) ──
        cog.AddSection("Reflex");
        cog.AddCompactGrid(
            OnOff("DcReflexAuto", DisplayCommanderSettingsService.ReflexAutoConfigureKey, "Auto Configure Reflex", "Dc.ReflexAutoTip", current.ReflexAutoConfigure),
            OnOff("DcReflexSleep", DisplayCommanderSettingsService.ReflexEnableSleepKey, "Enable Reflex Sleep Mode", "Dc.ReflexSleepTip", current.ReflexEnableSleep),
            OnOff("DcReflexUseMarkers", DisplayCommanderSettingsService.ReflexUseMarkersKey, "Use Reflex Markers", "Dc.ReflexUseMarkersTip", current.ReflexUseMarkers),
            OnOff("DcReflexGenerateMarkers", DisplayCommanderSettingsService.ReflexGenerateMarkersKey, "Generate Reflex Markers", "Dc.ReflexGenerateMarkersTip", current.ReflexGenerateMarkers));

        // ── Other ──
        cog.AddSection(T("Dc.Other"));
        var graces = DisplayCommanderSettingsService.WinUpGraceChoices;
        string GraceLabel(int seconds) => seconds switch
        {
            0 => T("Ini.Off"),
            1 => T("Dc.GraceOneSecond"),
            61 => T("Dc.GraceAlways"),
            _ => UiLocalizationService.Format("Dc.GraceSeconds", seconds)
        };
        var graceItems = graces.Select(GraceLabel).ToList();
        if (!graces.Contains(current.WinUpGraceSeconds)) graceItems.Add(UiLocalizationService.Format("Dc.GraceSeconds", current.WinUpGraceSeconds)); // a value set by hand stays shown
        var graceIndex = graces.Contains(current.WinUpGraceSeconds) ? graces.ToList().IndexOf(current.WinUpGraceSeconds) : graceItems.Count - 1;
        var handSet = current.WinUpGraceSeconds;
        cog.AddCompactGrid(
            Pick("DcWinUpGrace", "Win+Up grace time", graceItems, graceIndex, "Dc.WinUpGraceTip",
                i => (DisplayCommanderSettingsService.WinUpGraceSecondsKey, Invariant(i < graces.Count ? graces[i] : handSet))),
            null);
        cog.AddNote(T("Dc.Note"));

        // FPS Limit: No limit, the VRR presets, the file's own value when it is none of them, Custom… (a box and Set, as upstream).
        void FpsLimitRow()
        {
            var presets = ReLimiterIniEditor.TargetFpsPresets;
            string Fmt(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            List<string> Items(double fps)
            {
                var items = new List<string> { T("Dc.NoLimit") };
                items.AddRange(presets.Select(p => $"{p.Fps} ({p.Hz}Hz VRR)"));
                if (fps > 0 && !presets.Any(p => p.Fps == fps)) items.Add(UiLocalizationService.Format("Dc.CustomValue", Fmt(fps)));
                items.Add(T("Dc.Custom"));
                return items;
            }
            int Index(List<string> items, double fps)
            {
                if (fps <= 0) return 0;
                var preset = presets.ToList().FindIndex(p => p.Fps == fps);
                return preset >= 0 ? preset + 1 : items.Count - 2;
            }
            const string label = "FPS Limit";
            var startItems = Items(current.FpsLimit);
            var combo = CogDialog.MakeCombo(startItems, Index(startItems, current.FpsLimit), name: "DcFpsLimit");
            var box = new NumericUpDown
            {
                Name = "DcCustomFps", Minimum = (decimal)DisplayCommanderSettingsService.MinCustomFps, Maximum = (decimal)DisplayCommanderSettingsService.MaxCustomFps,
                Increment = 1, FormatString = "0.###", Value = 120, MinWidth = 140
            };
            var set = CogDialog.StyledButton(T("Ini.PeakGameSetButton"), CogButtonKind.Secondary, "DcCustomFpsSet");
            var reverting = false;
            cog.AddRow(label, combo, T("Dc.FpsLimitTip"));
            var custom = cog.AddRow(T("Dc.Custom"), CogDialog.Inline(box, set), T("Dc.CustomFpsTip"));
            custom.IsVisible = false;
            void Refresh()
            {
                reverting = true;
                var items = Items(current.FpsLimit);
                combo.ItemsSource = items;
                combo.SelectedIndex = Index(items, current.FpsLimit);
                custom.IsVisible = false;
                reverting = false;
            }
            async Task SetAsync(double fps)
            {
                var shown = fps == 0 ? T("Dc.NoLimit") : $"{Fmt(fps)} FPS";
                await WriteAsync(DisplayCommanderSettingsService.FpsLimitKey, Fmt(fps), label, shown);
                Refresh();
            }
            combo.SelectionChanged += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
            {
                if (reverting || combo.SelectedIndex < 0) return;
                var items = (List<string>)combo.ItemsSource!;
                if (combo.SelectedIndex == items.Count - 1) { custom.IsVisible = true; return; } // Custom…
                custom.IsVisible = false;
                if (combo.SelectedIndex == 0) await SetAsync(0);
                else if (combo.SelectedIndex - 1 < presets.Count) await SetAsync(presets[combo.SelectedIndex - 1].Fps);
                // the "(Custom)" row is already the file's value
            }, nameof(ComponentIniDialog));
            set.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
            {
                if (box.Value is not { } value) { cog.SetError(T("Dc.FpsRange")); return; }
                await SetAsync(Math.Round((double)value, 3));
            }, nameof(ComponentIniDialog));
        }
    }

    /// <summary>
    /// #283 (upstream 2.8.9): the "Loading" card of ReShade's ⚙ with "Load ReShade as ASI" (Off / On). A change is applied
    /// at once (ReShade renamed in place); a refused one goes back to what the game has. Hidden for the Vulkan layer.
    /// </summary>
    private static void AsiSection(CogDialog cog, IniPresetsPresenter presenter)
    {
        cog.AddSection(T("Ini.LoadingSection"));
        cog.AddCombo(T("Ini.ReShadeAsi"), T("Ini.ReShadeAsiTip"), OptionLists.OffOn.Items, presenter.ReShadeAsiMode().On ? 1 : 0, async i =>
        {
            var on = i == 1;
            cog.SetStatus(await presenter.SetReShadeAsiModeAsync(on));
            return presenter.ReShadeAsiMode().On == on;
        }, name: "ReShadeAsi");
    }

    /// <summary>
    /// #180: install a preset from the presets folder (and, when ticked, the shader packs it needs), open the folder, and
    /// the presets RHI installed in this game with Remove. With no preset in the folder the picker and its buttons are
    /// hidden and one line says where presets go.
    /// </summary>
    private static void PresetSection(CogDialog cog, IniPresetsPresenter presenter, Func<string, string?> openFolder)
    {
        cog.AddSection(T("Ini.PresetSection"));
        var empty = cog.AddNote(T("Ini.PresetNone"));
        var picker = new ComboBox { Name = "PresetPicker" };
        var pickerRow = cog.AddRow(T("Ini.PresetInstall"), picker);
        var withPacks = new CheckBox { Name = "PresetWithPacks", Content = T("Ini.PresetWithPacks"), IsChecked = true };
        CogLayout.Tip(withPacks, T("Ini.PresetWithPacksTip"));
        cog.AddContent(withPacks);
        var install = CogDialog.StyledButton(T("Ini.PresetInstall"), CogButtonKind.Primary, "PresetInstall");
        var openPresets = CogDialog.StyledButton(T("Ini.PresetOpenFolder"), CogButtonKind.Secondary, "PresetOpenFolder");
        CogLayout.Tip(openPresets, UiLocalizationService.Format("Ini.TemplatesFolderTip", presenter.PresetsFolder));
        cog.AddButtons(install, openPresets);
        var installed = cog.AddContent(new StackPanel { Spacing = 6 });

        void Refresh()
        {
            var available = presenter.AvailablePresets();
            picker.ItemsSource = available.Select(path => Path.GetFileName(path)).ToList();
            if (available.Count > 0 && picker.SelectedIndex < 0) picker.SelectedIndex = 0;
            pickerRow.IsVisible = withPacks.IsVisible = install.IsVisible = available.Count > 0;
            empty.IsVisible = available.Count == 0;

            installed.Children.Clear();
            installed.Children.Add(CogLayout.Text(T("Ini.PresetInstalledHeading"), "cogIntro"));
            foreach (var preset in presenter.InstalledPresets())
            {
                var fileName = preset.FileName;
                var remove = cog.MakeButton(T("Ui.ActionRemove"), CogButtonKind.Danger, async () =>
                {
                    cog.SetStatus(await presenter.RemovePresetAsync(fileName));
                    Refresh();
                }, T("Ini.PresetRemoveTip"));
                var name = new TextBlock { Text = fileName, Classes = { "cogLabel" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                CogLayout.Tip(name, fileName);
                installed.Children.Add(CogDialog.Inline(name, remove));
            }
            installed.IsVisible = installed.Children.Count > 1;
        }

        install.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            if (picker.SelectedIndex < 0) return;
            var path = presenter.AvailablePresets()[picker.SelectedIndex];
            install.IsEnabled = false;
            try { cog.SetStatus(await presenter.InstallPresetAsync(path, withPacks.IsChecked == true)); }
            finally
            {
                install.IsEnabled = true;
                Refresh();
            }
        }, nameof(ComponentIniDialog));
        openPresets.Click += (_, _) =>
        {
            if ((presenter.PreparePresetsFolder() ?? openFolder(presenter.PresetsFolder)) is { } error) cog.SetError(error);
            Refresh();
        };
        Refresh();
    }

    /// <summary>
    /// #189 (upstream <c>LumaCogButton_Click</c>, "Unreal Engine" card): HDR on first boot (Default / Off / On) and TAA
    /// (Off / On); each change is written at once and a refused one goes back to what the files hold. TAA is hidden when no
    /// Engine.ini location can be found for the game.
    /// </summary>
    private static void LumaSection(CogDialog cog, IniPresetsPresenter presenter)
    {
        var snapshot = presenter.LumaSettings();
        cog.AddSection("Unreal Engine"); // a product name, as upstream's section
        var hdrItems = OptionLists.LumaHdrMode.Items;
        cog.AddCombo(T("Ini.LumaHdr"), T("Ini.LumaHdrTip"), hdrItems, IndexOf(hdrItems, OptionLists.LumaHdrMode.Display(snapshot?.Hdr ?? LumaHdrMode.Default)), async i =>
        {
            var chosen = OptionLists.LumaHdrMode.Value(hdrItems[i]);
            cog.SetStatus(await presenter.SetLumaHdrAsync(chosen));
            return (presenter.LumaSettings()?.Hdr ?? LumaHdrMode.Default) == chosen;
        }, name: "LumaHdr");
        if (snapshot?.EngineIniPath is null) return;
        cog.AddCombo("TAA", T("Ini.LumaTaaTip"), OptionLists.OffOn.Items, snapshot.TaaOn ? 1 : 0, async i =>
        {
            var on = i == 1;
            cog.SetStatus(await presenter.SetLumaTaaAsync(on));
            return presenter.LumaSettings()?.TaaOn == on;
        }, name: "LumaTaa");
    }

    private static int IndexOf(IReadOnlyList<string> items, string value)
    {
        for (var i = 0; i < items.Count; i++)
            if (items[i] == value) return i;
        return 0;
    }
}
