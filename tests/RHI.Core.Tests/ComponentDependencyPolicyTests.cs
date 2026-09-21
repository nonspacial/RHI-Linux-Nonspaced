using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class ComponentDependencyPolicyTests
{
    [Theory]
    [InlineData(ManagedComponentKind.RenoDX)]
    [InlineData(ManagedComponentKind.RenoDXUpgrade)]
    [InlineData(ManagedComponentKind.ReLimiter)]
    [InlineData(ManagedComponentKind.DisplayCommander)]
    [InlineData(ManagedComponentKind.MfgAdaUnlock)]
    [InlineData(ManagedComponentKind.Dlss5Feeder)]
    [InlineData(ManagedComponentKind.Dlss5Dx11Bridge)]
    [InlineData(ManagedComponentKind.ShortFuse)]
    [InlineData(ManagedComponentKind.DofFix)]
    [InlineData(ManagedComponentKind.LumaFramework)]
    public void ReShadeAddonComponents_DeclareReShadeDependency(ManagedComponentKind component)
    {
        var game = new DetectedGame { Name = "Game", InstallPath = "/tmp/game", Runner = "Wine" };

        var dependencies = LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, component);

        Assert.Equal(new[] { ManagedComponentKind.ReShade }, dependencies.ToArray());
        Assert.True(LinuxComponentDependencyPolicy.IsReShadeAddon(component));
    }

    [Fact]
    public void ReShade_OnReEngine_DeclaresReFrameworkDependency()
    {
        var game = new DetectedGame
        {
            Name = "RE Game",
            InstallPath = "/tmp/re-game",
            Runner = "Wine",
            EngineHint = "RE Engine",
            ForcedArchitecture = PeArchitecture.X64
        };

        var dependencies = LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, ManagedComponentKind.ReShade);

        Assert.Equal(new[] { ManagedComponentKind.REFramework }, dependencies.ToArray());
    }

    [Fact]
    public void ReShade_OnNonReEngine_HasNoManagedDependency()
    {
        var game = new DetectedGame { Name = "Game", InstallPath = "/tmp/game", Runner = "Wine", EngineHint = "Unreal Engine 5.4" };

        Assert.Empty(LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public void LumaAndShortFuse_RequireRhiOwnedReShade()
    {
        Assert.True(LinuxComponentDependencyPolicy.RequiresRhiOwnedDependency(ManagedComponentKind.LumaFramework, ManagedComponentKind.ReShade));
        Assert.True(LinuxComponentDependencyPolicy.RequiresRhiOwnedDependency(ManagedComponentKind.ShortFuse, ManagedComponentKind.ReShade));
    }

    [Fact]
    public void OrdinaryReShadeAddons_CanUseValidExternalReShadeWithoutAdoption()
    {
        Assert.False(LinuxComponentDependencyPolicy.RequiresRhiOwnedDependency(ManagedComponentKind.RenoDX, ManagedComponentKind.ReShade));
        Assert.False(LinuxComponentDependencyPolicy.RequiresRhiOwnedDependency(ManagedComponentKind.ReLimiter, ManagedComponentKind.ReShade));
        Assert.False(LinuxComponentDependencyPolicy.RequiresRhiOwnedDependency(ManagedComponentKind.DofFix, ManagedComponentKind.ReShade));
    }

    [Fact]
    public void StandaloneComponents_DoNotInventManagedDependencies()
    {
        var game = new DetectedGame { Name = "Game", InstallPath = "/tmp/game", Runner = "Wine" };
        var standalone = new[]
        {
            ManagedComponentKind.OptiScaler,
            ManagedComponentKind.Dlssg2030Unlock,
            ManagedComponentKind.DlssEnablerStandalone,
            ManagedComponentKind.Rtx40MfgStandalone,
            ManagedComponentKind.REFramework,
            ManagedComponentKind.UltimateAsiLoader
        };

        foreach (var component in standalone)
            Assert.Empty(LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, component));
    }

    [Fact]
    public void ReEngineDependencyGraph_IsAcyclicAndOrdered()
    {
        var game = new DetectedGame
        {
            Name = "RE Game",
            InstallPath = "/tmp/re-game",
            Runner = "Wine",
            EngineHint = "RE Engine",
            ForcedArchitecture = PeArchitecture.X64
        };

        var target = LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, ManagedComponentKind.RenoDX);
        var reshade = LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, target.Single());
        var reframework = LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, reshade.Single());

        Assert.Equal(ManagedComponentKind.ReShade, target.Single());
        Assert.Equal(ManagedComponentKind.REFramework, reshade.Single());
        Assert.Empty(reframework);
    }

    [Fact]
    public void DofFix_IsExplicitlyClassifiedAsReShadeAddonDependency()
    {
        Assert.True(LinuxComponentDependencyPolicy.IsReShadeAddon(ManagedComponentKind.DofFix));
    }
    [Fact]
    public void EveryManagedComponent_DependencyGraphIsAcyclicAndProvisionable()
    {
        var games = new[]
        {
            new DetectedGame { Name = "Generic", InstallPath = "/tmp/generic", Runner = "Wine" },
            new DetectedGame
            {
                Name = "RE Game",
                InstallPath = "/tmp/re-game",
                Runner = "Wine",
                EngineHint = "RE Engine",
                ForcedArchitecture = PeArchitecture.X64
            }
        };

        foreach (var game in games)
        {
            foreach (var component in Enum.GetValues<ManagedComponentKind>().Where(kind => kind != ManagedComponentKind.Unknown))
            {
                var path = new HashSet<ManagedComponentKind>();
                Visit(component);

                void Visit(ManagedComponentKind current)
                {
                    Assert.True(path.Add(current), $"Dependency cycle detected at {current} for {component}.");
                    foreach (var dependency in LinuxComponentDependencyPolicy.ManagedDependenciesFor(game, current))
                    {
                        Assert.Contains(dependency, new[] { ManagedComponentKind.ReShade, ManagedComponentKind.REFramework });
                        Visit(dependency);
                    }
                    path.Remove(current);
                }
            }
        }
    }

}
