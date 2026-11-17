using System.Reflection;
using System.Text.Json;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review M6: the saves that start from <c>GetLinuxSettingsSnapshot</c> (the OptiScaler menu key, the GITC Uplift
/// key, and "Apply to all" in Peak Brightness, ReLimiter and ReShade ini through <c>IniPresetsPresenter.SavedSettings</c>)
/// keep every other setting as it was. Every <see cref="LinuxAppSettings"/> property is set to a value other than its
/// default (one Normalize keeps), saved through each caller, and read back.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class SettingsSnapshotRoundTripTests
{
    // Properties whose value the test leaves alone: the UI language applies itself to the whole process.
    private static readonly HashSet<string> Untouched = [nameof(LinuxAppSettings.UiLanguage)];

    private static readonly object[] Candidates =
    [
        7, 150, 60, 2, 12, 3u, 60u, 1u, 700.0, 1.5, 14.0,
        "Tabs", "Compact", "Nightly", "AMD", "0x2D", "45,0,0,0", "/tmp/rhi-shots", "F11", "Kiro", "Custom", "nightly", "overview",
        "310.2.1", "display", "#112233"
    ];

    /// <summary>Settings with every property but <see cref="Untouched"/> away from its default; the names that could be set.</summary>
    internal static (LinuxAppSettings Settings, IReadOnlyList<string> Changed) NonDefault()
    {
        var defaults = new LinuxAppSettings().Normalize();
        var settings = new LinuxAppSettings();
        var changed = new List<string>();
        foreach (var property in Properties())
        {
            if (Untouched.Contains(property.Name)) continue;
            var before = property.GetValue(defaults);
            foreach (var candidate in ValuesFor(property.PropertyType, before))
            {
                var trial = Clone(settings);
                property.SetValue(trial, candidate);
                if (property.Name == nameof(LinuxAppSettings.CloseToTray)) trial.TrayIconEnabled = true;
                trial.Normalize();
                if (Json(property.GetValue(trial)) == Json(before)) continue;
                property.SetValue(settings, property.GetValue(trial));
                if (property.Name == nameof(LinuxAppSettings.CloseToTray)) settings.TrayIconEnabled = true;
                changed.Add(property.Name);
                break;
            }
        }
        return (settings.Normalize(), changed);
    }

    private static IEnumerable<PropertyInfo> Properties()
        => typeof(LinuxAppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p is { CanRead: true, CanWrite: true });

    private static IEnumerable<object?> ValuesFor(Type type, object? current)
    {
        if (type == typeof(bool)) return [!(bool)current!];
        if (type == typeof(List<int>)) return [new List<int> { 2 }, new List<int> { 1, 3 }];
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return Candidates.Where(c => c.GetType() == underlying);
    }

    private static LinuxAppSettings Clone(LinuxAppSettings settings) => JsonSerializer.Deserialize<LinuxAppSettings>(JsonSerializer.Serialize(settings))!;

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    [Fact]
    public void EveryPropertyCanBeSetAwayFromItsDefault()
    {
        var (_, changed) = NonDefault();
        var missing = Properties().Select(p => p.Name).Where(name => !Untouched.Contains(name) && !changed.Contains(name)).ToList();
        Assert.True(missing.Count == 0, "add a candidate value for: " + string.Join(", ", missing));
    }

    public static TheoryData<string> Callers => new() { "OptiScalerMenuKey", "UpliftEnableKey", "IniPresetsApplyToAll" };

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task ASaveFromTheSnapshot_KeepsEverySetting(string caller)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var (settings, _) = NonDefault();
        settings.UiLanguage = "en-US";
        fx.Settings.Save(settings);
        fx.Vm.AdoptSettings(fx.Settings.Load(), restartLoop: false, runImmediate: false);
        var expected = Clone(settings);

        switch (caller)
        {
            case "OptiScalerMenuKey":
                Assert.Null(fx.Vm.Settings.SaveOptiScalerMenuKey("0x2E"));
                expected.OptiScalerMenuKey = "0x2E";
                break;
            case "UpliftEnableKey":
                Assert.Null(fx.Vm.Settings.SaveUpliftEnableKey(0x71));
                expected.UpliftEnableKey = 0x71;
                break;
            default:
                // Peak Brightness, ReLimiter and ReShade ini "Apply to all": SavedSettings, one change, Save.
                var presets = fx.Vm.IniPresets;
                var current = presets.SavedSettings();
                current.PeakNits = 650;
                presets.Save(current);
                expected.PeakNits = 650;
                break;
        }

        var saved = fx.Settings.Load();
        var lost = Properties().Where(p => Json(p.GetValue(saved)) != Json(p.GetValue(expected.Normalize())))
            .Select(p => $"{p.Name}: {Json(p.GetValue(saved))} instead of {Json(p.GetValue(expected))}").ToList();
        Assert.True(lost.Count == 0, "reset by the save: " + string.Join("; ", lost));
    }
}
