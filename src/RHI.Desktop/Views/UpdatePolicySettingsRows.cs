using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RHI.Core.Models;
using RHI.Desktop.Localization;
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
    private readonly CheckBox _followDlss;
    private readonly CheckBox _followStreamline;
    private readonly Dictionary<ManagedComponentKind, CheckBox> _included = [];

    public UpdatePolicySettingsRows(LinuxAppSettings settings)
    {
        _followDlss = new CheckBox { Name = "FollowNewestDlss", Content = T("Upd.FollowDlss"), IsChecked = settings.FollowNewestDlss };
        _followStreamline = new CheckBox { Name = "FollowNewestStreamline", Content = T("Upd.FollowStreamline"), IsChecked = settings.FollowNewestStreamline };
        foreach (var box in new[] { _followDlss, _followStreamline })
        {
            ToolTip.SetTip(box, T("Upd.FollowTip"));
            ToolTip.SetShowDelay(box, 2000);
        }

        var follow = new StackPanel { Spacing = 6 };
        follow.Children.Add(new TextBlock { Text = T("Upd.FollowHeading"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        follow.Children.Add(_followDlss);
        follow.Children.Add(_followStreamline);
        follow.Children.Add(new TextBlock { Text = T("Upd.FollowNote"), FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
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
        var inclusion = new StackPanel { Spacing = 6 };
        inclusion.Children.Add(new TextBlock { Text = T("Upd.GlobalHeading"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        inclusion.Children.Add(boxes);
        inclusion.Children.Add(new TextBlock { Text = T("Upd.GlobalNote"), FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        InclusionControl = inclusion;
    }

    /// <summary>"Update All includes" (Maintenance tab).</summary>
    public Control InclusionControl { get; }

    /// <summary>True when Update All leaves <paramref name="kind"/> alone in every game (its box is cleared).</summary>
    public bool Skips(ManagedComponentKind kind) => _included.TryGetValue(kind, out var box) && box.IsChecked != true;

    /// <summary>The two Follow newest toggles (DLSS/Streamline tab).</summary>
    public Control FollowControl { get; }

    public bool FollowDlss => _followDlss.IsChecked == true;

    public bool FollowStreamline => _followStreamline.IsChecked == true;

    public void Reset(LinuxAppSettings defaults)
    {
        _followDlss.IsChecked = defaults.FollowNewestDlss;
        _followStreamline.IsChecked = defaults.FollowNewestStreamline;
        var skips = UpdateInclusionStore.GlobalSkipsFrom(defaults);
        foreach (var (kind, box) in _included) box.IsChecked = !skips.Contains(kind);
    }

    private static string T(string key) => UiLocalizationService.Text(key);
}
