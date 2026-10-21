using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #120: the ReShade "DLL name" dropdown. Automatic shows the name it stands for; the offered names suit the game's
/// bitness; a choice is saved through the shell's store-save helper (and reverts when it cannot be saved); while RHI's
/// ReShade is installed the dropdown shows the installed name and a choice renames it in place (#196), unless another
/// component decides the name; Reset DLL names shows only when a name is not automatic.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class DllNamePickerTests
{
    [Fact]
    public async Task Picker_OffersAutomaticAndTheNames_SavesAChoice_ThenOffersRenamingTheInstalledName()
    {
        var (fx, game) = await StartAsync("Picker Game");
        using var _ = fx;
        var picker = fx.Vm.DllNamePicker;
        await SettledAsync(picker);

        Assert.True(picker.IsAvailable);
        Assert.True(picker.CanChoose);
        Assert.Equal("Automatic (dxgi.dll)", picker.Options[0].Display);
        Assert.Null(picker.Options[0].Value);
        Assert.Contains(picker.Options, o => o.Value == "winmm.dll");
        Assert.DoesNotContain(picker.Options, o => o.Value == "ReShade32.dll"); // a 64-bit game
        Assert.Contains("dxgi.dll", picker.Hint);

        picker.SelectedOption = picker.Options.Single(o => o.Value == "winmm.dll");
        await SettledAsync(picker);

        Assert.Equal("winmm.dll", fx.Managed.ReShadeDllNames.GetReShade(game));
        Assert.Equal("winmm.dll", picker.SelectedOption!.Value);
        Assert.Contains("winmm.dll", picker.Hint);

        // RHI installs ReShade (here: its record and payload) under another name: the dropdown shows that name and offers
        // the free names to rename it to (#196); version.dll is taken by another file, so it is left out.
        SeedReShade(fx, game, "d3d11.dll");
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "version.dll"), [0x4D, 0x5A]);
        await ReselectAsync(fx, game, picker);

        Assert.Equal("Automatic (dxgi.dll)", picker.Options[0].Display);
        Assert.Contains(picker.Options, o => o.Value == "winmm.dll");
        Assert.DoesNotContain(picker.Options, o => o.Value == "version.dll");
        Assert.Equal("d3d11.dll", picker.SelectedOption!.Value);
        Assert.True(picker.CanChoose);
        Assert.Contains("rename it in place", picker.Hint);
        Assert.True(picker.CanReset); // winmm.dll is saved and d3d11.dll is not the automatic name
    }

    [Fact]
    public async Task Picker_ChoosingAnotherName_RenamesTheInstalledReShade_AndResetPutsItBack()
    {
        var (fx, game) = await StartAsync("Rename Game");
        using var _ = fx;
        var picker = fx.Vm.DllNamePicker;
        SeedReShade(fx, game, "dxgi.dll");
        await ReselectAsync(fx, game, picker);
        Assert.False(picker.CanReset); // installed under its automatic name, nothing saved
        Assert.Null(picker.SelectedOption!.Value); // Automatic (dxgi.dll)
        var lines = new List<string>();
        fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Add(fx.Vm.StatusText); };

        picker.SelectedOption = picker.Options.Single(o => o.Value == "winmm.dll");
        await picker.Changed.WaitAsync(TimeSpan.FromSeconds(30));
        await SettledAsync(picker);

        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "winmm.dll")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal("winmm.dll", fx.Managed.InstalledReShadeProxyName(game));
        Assert.Equal("winmm.dll", fx.Managed.ReShadeDllNames.GetReShade(game));
        Assert.Contains(lines, line => line.StartsWith(UiLocalizationService.Present(ProxyRename.RenamedPrefix), StringComparison.Ordinal));
        Assert.Equal("winmm.dll", picker.SelectedOption!.Value);
        Assert.True(picker.CanReset);

        await picker.ResetCommand.ExecuteAsync(null);
        await SettledAsync(picker);

        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Null(fx.Managed.ReShadeDllNames.GetReShade(game));
        Assert.False(picker.CanReset);
        Assert.Contains(lines, line => line.StartsWith(UiLocalizationService.Present(LinuxManagedComponentService.DllNamesResetMessage), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Picker_WhenAnotherComponentDecidesTheName_ShowsOnlyThatNameLocked_WithTheReason()
    {
        var (fx, game) = await StartAsync("Chained Game");
        using var _ = fx;
        var picker = fx.Vm.DllNamePicker;
        SeedReShade(fx, game, "ReShade64.dll");
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.OptiScaler,
            BinaryDirectory = game.BinaryDirectory!, Version = "0.9", Files = [new LinuxDeployedFile { Path = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini") }]
        });
        await ReselectAsync(fx, game, picker);

        Assert.Equal(["ReShade64.dll"], picker.Options.Select(o => o.Display));
        Assert.False(picker.CanChoose);
        Assert.Equal(UiLocalizationService.Present(ReShadeDeploymentService.RenameNotInPlacePrefix + "ReShade64.dll"), picker.Hint);
    }

    [Fact]
    public async Task Picker_DllChangedSinceInstall_LockedTipNamesWhatLetsRhiManageItAgain()
    {
        // Review 4: no control in RHI changes a DLL that changed outside it; the tip says so and names the row's Remove for
        // after the file is put back or deleted. A DLL that is gone needs only that Remove.
        var (fx, game) = await StartAsync("Changed Game");
        using var _ = fx;
        var picker = fx.Vm.DllNamePicker;
        SeedReShade(fx, game, "winmm.dll");
        var payload = Path.Combine(game.BinaryDirectory!, "winmm.dll");
        File.AppendAllText(payload, "changed by hand");
        await ReselectAsync(fx, game, picker);

        Assert.False(picker.CanChoose);
        Assert.Equal(UiLocalizationService.Present(ProxyRename.ChangedPrefix + payload) + " "
                     + UiLocalizationService.Format("Rx.DllChangedTip", "ReShade"), picker.Hint);
        Assert.Contains(UiLocalizationService.Text("Vm.Remove"), picker.Hint);

        File.Delete(payload);
        await ReselectAsync(fx, game, picker);

        Assert.Equal(UiLocalizationService.Present(ProxyRename.ChangedPrefix + payload) + " "
                     + UiLocalizationService.Format("Rx.DllMissingTip", "ReShade"), picker.Hint);
        Assert.Contains(UiLocalizationService.Text("Vm.Remove"), picker.Hint);
    }

    private static void SeedReShade(ViewModelFixture fx, DetectedGame game, string name)
    {
        var payload = Path.Combine(game.BinaryDirectory!, name);
        File.WriteAllBytes(payload, [0x4D, 0x5A, 7, 7]);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!, Version = "6.8.0",
            Files = [new LinuxDeployedFile { Path = payload, Sha256 = RHI.Core.Deployment.FileHash.Sha256Hex(payload) }]
        });
    }

    private static async Task ReselectAsync(ViewModelFixture fx, DetectedGame game, DllNamePickerViewModel picker)
    {
        fx.Vm.SelectedGame = null;
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == game.Name).Game;
        await SettledAsync(picker);
    }

    [Fact]
    public async Task Picker_InPortuguese_LabelsAutomaticAndTheRow()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            var (fx, _) = await StartAsync("Jogo Picker", "pt-BR");
            using var __ = fx;
            var picker = fx.Vm.DllNamePicker;
            await SettledAsync(picker);

            Assert.Equal("Automático (dxgi.dll)", picker.Options[0].Display);
            Assert.Equal("Nome da DLL", picker.Label);
            Assert.StartsWith("O ReShade será instalado como dxgi.dll", picker.Hint);
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
        }
    }

    [Fact]
    public async Task Picker_WhenTheChoiceCannotBeSaved_GoesBackToTheSavedOne_AndSaysSo()
    {
        if (Environment.UserName == "root") return; // directory modes do not stop root
        var (fx, game) = await StartAsync("Locked Config Game");
        using var _ = fx;
        var picker = fx.Vm.DllNamePicker;
        await SettledAsync(picker);
        File.SetUnixFileMode(fx.ConfigDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            picker.SelectedOption = picker.Options.Single(o => o.Value == "version.dll");
            await SettledAsync(picker);
        }
        finally
        {
            File.SetUnixFileMode(fx.ConfigDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Null(picker.SelectedOption!.Value);
        Assert.Null(fx.Managed.ReShadeDllNames.GetReShade(game));
        Assert.Contains(UiLocalizationService.Text("Ui.WhatGamePreferences"), fx.Vm.StatusText);
    }

    // Automatic follows the graphics API: a user API override changes the name it stands for.
    [Fact]
    public async Task Picker_Automatic_FollowsTheGraphicsApiOverride()
    {
        var (fx, _) = await StartAsync("Api Override Game");
        using var __ = fx;
        var picker = fx.Vm.DllNamePicker;
        await SettledAsync(picker);
        Assert.Equal("Automatic (dxgi.dll)", picker.Options[0].Display);

        fx.Vm.Runtime.SelectedGraphicsApiOverride = fx.Vm.Runtime.GraphicsApiOverrideOptions.ForDisplay("DirectX 9");
        await SettledAsync(picker);

        Assert.Equal("Automatic (d3d9.dll)", picker.Options[0].Display);
    }

    [Fact]
    public async Task Picker_IsHidden_ForANativeLinuxGame()
    {
        var (fx, _) = await StartAsync("Native Game", runner: "Native Linux");
        using var __ = fx;
        var picker = fx.Vm.DllNamePicker;
        await SettledAsync(picker);

        Assert.False(picker.IsAvailable);
        Assert.Empty(picker.Options);
    }

    // Merge review nit 5: the list is bound to the UI, so a change raised on another thread (here the graphics API override
    // set from a pool thread) rebuilds it on the UI thread the picker lives on, never on the raising thread.
    [Fact]
    public async Task Picker_AChangeRaisedOffTheUiThread_RebuildsTheListOnTheUiThread()
    {
        using var ui = new UiThreadStandIn();
        var (fx, picker, threads) = await ui.RunAsync(async () =>
        {
            var (f, _) = await StartAsync("Ui Thread Game");
            var p = f.Vm.DllNamePicker;
            p.UiThread = (() => ui.IsCurrent, ui.Post);
            await SettledAsync(p);
            var seen = new System.Collections.Concurrent.ConcurrentBag<int>();
            p.Options.CollectionChanged += (_, _) => seen.Add(Environment.CurrentManagedThreadId);
            return (f, p, seen);
        });
        using var __ = fx;
        Assert.Equal("Automatic (dxgi.dll)", picker.Options[0].Display);

        await Task.Run(() => fx.Vm.Runtime.SelectedGraphicsApiOverride = fx.Vm.Runtime.GraphicsApiOverrideOptions.ForDisplay("DirectX 9"));
        for (var i = 0; i < 400 && (picker.Options.Count == 0 || picker.Options[0].Display != "Automatic (d3d9.dll)"); i++) await Task.Delay(10);

        Assert.Equal("Automatic (d3d9.dll)", picker.Options[0].Display);
        Assert.NotEmpty(threads);
        Assert.All(threads, id => Assert.Equal(ui.ThreadId, id));
    }

    // #172 review minor 8b: OptiScaler's name is saved before install only; while RHI's OptiScaler is installed the shell
    // refuses a save from any caller (not only through the locked dropdown) and says why.
    [Fact]
    public async Task OptiScalerDllName_SavedBeforeInstall_RefusedWhileInstalled()
    {
        var (fx, game) = await StartAsync("OptiScaler Name Game");
        using var _ = fx;

        Assert.True(fx.Vm.SaveOptiScalerDllName("winmm.dll"));
        Assert.Equal("winmm.dll", fx.Managed.ReShadeDllNames.GetOptiScaler(game));

        var proxy = Path.Combine(game.BinaryDirectory!, "winmm.dll");
        File.WriteAllBytes(proxy, [0x4D, 0x5A]);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.OptiScaler,
            BinaryDirectory = game.BinaryDirectory!, Version = "0.9.4", Files = [new LinuxDeployedFile { Path = proxy }],
            Metadata = { ["OptiScaler.ProxyName"] = "winmm.dll" }
        });

        // The lines this call writes (read as they are set: the selection analysis may write "Ready —" afterwards).
        var lines = new List<string>();
        void Collect(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Add(fx.Vm.StatusText);
        }
        fx.Vm.PropertyChanged += Collect;
        bool saved;
        try { saved = fx.Vm.SaveOptiScalerDllName("version.dll"); }
        finally { fx.Vm.PropertyChanged -= Collect; }

        Assert.False(saved);
        Assert.Equal("winmm.dll", fx.Managed.ReShadeDllNames.GetOptiScaler(game));
        Assert.Contains(lines, line => line.Contains(UiLocalizationService.Format("Names.OptiScalerDllNameTipInstalled", "winmm.dll"), StringComparison.Ordinal));
        Assert.False(fx.Vm.SaveOptiScalerDllName(null));
        Assert.Equal("winmm.dll", fx.Managed.ReShadeDllNames.GetOptiScaler(game));
    }

    private static async Task<(ViewModelFixture Fixture, DetectedGame Game)> StartAsync(string name, string language = "en-US", string runner = "Proton")
    {
        var root = Path.Combine(Path.GetTempPath(), $"rhi-dll-picker-{Guid.NewGuid():N}");
        var bin = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(root, "pfx")).FullName;
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n"); // #196: a rename moves the Wine override
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        var game = new DetectedGame
        {
            Name = name, InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
            Runner = runner, Source = "Heroic", GraphicsApi = GraphicsApi.DirectX12
        };
        var fx = await ViewModelFixture.StartAsync([game], language);
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == name).Game;
        return (fx, fx.Vm.SelectedGame!);
    }

    // The picker refreshes off the UI thread after each selection, analysis or save: wait until it has settled.
    private static async Task SettledAsync(DllNamePickerViewModel picker)
    {
        for (var i = 0; i < 50; i++)
        {
            var refreshed = picker.Refreshed;
            await refreshed.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(20);
            if (ReferenceEquals(refreshed, picker.Refreshed)) return;
        }
    }
}
