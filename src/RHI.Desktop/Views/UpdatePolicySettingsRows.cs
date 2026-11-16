using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.Views;

/// <summary>
/// w5/update-policy Settings rows. #177: Settings → DLSS/Streamline "Follow newest DLSS (SR/RR/FG)" and "Follow newest
/// Streamline" (upstream 2.8.7 AutoUpdateDlss / AutoUpdateStreamline, both off by default). The Settings dialog reads
/// the controls into the settings it saves (<c>ReadControls</c>) and resets them with the rest. #178: Settings →
/// Maintenance "Update All includes" (upstream's Global Update Inclusion: ReShade, RenoDX, ReLimiter, Display Commander,
/// OptiScaler, RE Framework), one box per component, ticked = included.
/// </summary>
internal sealed class UpdatePolicySettingsRows
{
    private readonly ComboBox _followDlss;
    private readonly ComboBox _followStreamline;
    private readonly Dictionary<ManagedComponentKind, CheckBox> _included = [];

    public UpdatePolicySettingsRows(LinuxAppSettings settings)
    {
        // #296 (upstream 2.8.9): General › Updates › Automatic updates, "DLSS" and "Streamline", Off / On.
        _followDlss = Combo("FollowNewestDlss", settings.FollowNewestDlss);
        _followStreamline = Combo("FollowNewestStreamline", settings.FollowNewestStreamline);
        var follow = new StackPanel { Spacing = 8 };
        follow.Children.Add(SettingsLayout.Row(T("Set.AutoDlss"), _followDlss, tip: T("Upd.FollowTip")));
        follow.Children.Add(SettingsLayout.Row(T("Set.AutoStreamline"), _followStreamline, tip: T("Upd.FollowTip")));
        FollowControl = follow;

        var skips = UpdateInclusionStore.GlobalSkipsFrom(settings);
        var boxes = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        foreach (var kind in UpdateInclusionStore.GlobalComponents)
        {
            var box = new CheckBox
            {
                Name = "UpdateAllIncludes" + kind,
                Content = LinuxManagedComponentService.DisplayDependencyName(kind),
                IsChecked = !skips.Contains(kind),
                Margin = new Thickness(0, 0, 16, 4)
            };
            _included[kind] = box;
            boxes.Children.Add(box);
        }
        // #296: upstream's "Components to check" (its Global Update Inclusion).
        var inclusion = new StackPanel { Spacing = 6 };
        inclusion.Children.Add(SettingsLayout.SubHeading(T("Upd.GlobalHeading"), T("Upd.GlobalNote")));
        inclusion.Children.Add(SettingsLayout.Block(boxes));
        inclusion.Children.Add(SettingsLayout.Help(T("Upd.GlobalNote")));
        InclusionControl = inclusion;
    }

    /// <summary>"Components to check" (General › Updates).</summary>
    public Control InclusionControl { get; }

    /// <summary>True when Update All leaves <paramref name="kind"/> alone in every game (its box is cleared).</summary>
    public bool Skips(ManagedComponentKind kind) => _included.TryGetValue(kind, out var box) && box.IsChecked != true;

    /// <summary>The two Follow newest rows (General › Updates › Automatic updates).</summary>
    public Control FollowControl { get; }

    public bool FollowDlss => OptionLists.OffOn.Value(_followDlss.SelectedItem as string);

    public bool FollowStreamline => OptionLists.OffOn.Value(_followStreamline.SelectedItem as string);

    public void Reset(LinuxAppSettings defaults)
    {
        _followDlss.SelectedItem = OptionLists.OffOn.Display(defaults.FollowNewestDlss);
        _followStreamline.SelectedItem = OptionLists.OffOn.Display(defaults.FollowNewestStreamline);
        var skips = UpdateInclusionStore.GlobalSkipsFrom(defaults);
        foreach (var (kind, box) in _included) box.IsChecked = !skips.Contains(kind);
    }

    private ComboBox Combo(string name, bool value)
    {
        var combo = new ComboBox { Name = name, ItemsSource = OptionLists.OffOn.Items, SelectedItem = OptionLists.OffOn.Display(value), MinWidth = 140 };
        return combo;
    }

    private static string T(string key) => UiLocalizationService.Text(key);
}
