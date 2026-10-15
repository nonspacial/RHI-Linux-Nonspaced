using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.Views;

/// <summary>
/// w5/update-policy Settings rows. #177: Settings → DLSS/Streamline "Follow newest DLSS (SR/RR/FG)" and "Follow newest
/// Streamline" (upstream 2.8.7 AutoUpdateDlss / AutoUpdateStreamline, both off by default). The Settings dialog reads
/// the controls into the settings it saves (<c>ReadControls</c>) and resets them with the rest.
/// </summary>
internal sealed class UpdatePolicySettingsRows
{
    private readonly CheckBox _followDlss;
    private readonly CheckBox _followStreamline;

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
    }

    /// <summary>The two Follow newest toggles (DLSS/Streamline tab).</summary>
    public Control FollowControl { get; }

    public bool FollowDlss => _followDlss.IsChecked == true;

    public bool FollowStreamline => _followStreamline.IsChecked == true;

    public void Reset(LinuxAppSettings defaults)
    {
        _followDlss.IsChecked = defaults.FollowNewestDlss;
        _followStreamline.IsChecked = defaults.FollowNewestStreamline;
    }

    private static string T(string key) => UiLocalizationService.Text(key);
}
