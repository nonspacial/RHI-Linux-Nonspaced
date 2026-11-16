using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #309 (upstream 2.8.9 MainWindow.FaqBuilder): one Quick Start step as upstream lays it out — a round badge and the title,
/// what to do, and the tips in a box underneath. Colours are App.axaml's (<c>quickStart*</c> styles).
/// </summary>
internal static class QuickStartView
{
    public static Control Step(QuickStartStep step)
    {
        var badge = new Border
        {
            Classes = { "quickStartBadge" }, Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
            Child = new TextBlock
            {
                Classes = { "quickStartBadge" }, Text = step.Badge, FontSize = 12, FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(badge);
        header.Children.Add(new TextBlock
        {
            Text = UiLocalizationService.Text(step.TitleKey), FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center
        });
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(header);
        panel.Children.Add(new SelectableTextBlock { Text = UiLocalizationService.Text(step.BodyKey), TextWrapping = TextWrapping.Wrap, Opacity = 0.86 });
        if (step.TipKey is { } tip)
            panel.Children.Add(new Border
            {
                Classes = { "quickStartTip" },
                Child = new SelectableTextBlock { Text = UiLocalizationService.Text(tip), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 }
            });
        return panel;
    }
}
