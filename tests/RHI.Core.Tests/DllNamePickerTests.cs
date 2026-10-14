using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #120: the ReShade "DLL name" dropdown. Automatic shows the name it stands for; the offered names suit the game's
/// bitness; a choice is saved through the shell's store-save helper (and reverts when it cannot be saved); while RHI's
/// ReShade is installed the dropdown shows the installed name and cannot change it.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class DllNamePickerTests
{
    [Fact]
    public async Task Picker_OffersAutomaticAndTheNames_SavesAChoice_ThenShowsTheInstalledNameLocked()
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

        // RHI installs ReShade (here: its record and payload) under another name: the dropdown shows that name, locked.
        var payload = Path.Combine(game.BinaryDirectory!, "d3d11.dll");
        File.WriteAllBytes(payload, [0x4D, 0x5A]);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!, Version = "6.8.0", Files = [new LinuxDeployedFile { Path = payload }]
        });
        fx.Vm.SelectedGame = null;
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == game.Name).Game;
        await SettledAsync(picker);

        Assert.Equal(["d3d11.dll"], picker.Options.Select(o => o.Display));
        Assert.Equal("d3d11.dll", picker.SelectedOption!.Value);
        Assert.False(picker.CanChoose);
        Assert.Contains("Remove ReShade", picker.Hint);
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

    private static async Task<(ViewModelFixture Fixture, DetectedGame Game)> StartAsync(string name, string language = "en-US", string runner = "Proton")
    {
        var root = Path.Combine(Path.GetTempPath(), $"rhi-dll-picker-{Guid.NewGuid():N}");
        var bin = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(root, "pfx")).FullName;
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
