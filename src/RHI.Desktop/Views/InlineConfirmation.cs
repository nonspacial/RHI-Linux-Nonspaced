using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;

namespace RHI.Desktop.Views;

/// <summary>
/// w5/ini-presets (review finding 5): the inline "N games?" question an "Apply to all games" button asks before it
/// writes into every game (the mass deploy's way of asking): a line and Apply / Cancel under the button. Apply runs the
/// action once; Cancel, or asking again, drops it.
/// </summary>
internal sealed class InlineConfirmation
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _panel = new() { Spacing = 6, IsVisible = false };
    private Func<Task>? _pending;

    /// <param name="name">Control names: <c>{name}Ask</c> (the line), <c>{name}Confirm</c>, <c>{name}Cancel</c>.</param>
    public InlineConfirmation(string name, string owner)
    {
        _text.Name = name + "Ask";
        var yes = new Button { Name = name + "Confirm", Content = UiLocalizationService.Text("Win.Apply"), Padding = new Thickness(12, 6), Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Name = name + "Cancel", Content = UiLocalizationService.Text("Win.Cancel"), Padding = new Thickness(12, 6) };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        _panel.Children.Add(_text);
        _panel.Children.Add(buttons);
        no.Click += (_, _) => Hide();
        yes.Click += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            var action = _pending;
            Hide();
            if (action is not null) await action();
        }, owner);
    }

    public Control Control => _panel;

    /// <summary>Shows <paramref name="question"/>; <paramref name="onYes"/> runs only when Apply is clicked.</summary>
    public void Ask(string question, Func<Task> onYes)
    {
        _text.Text = question;
        _pending = onYes;
        _panel.IsVisible = true;
    }

    public void Hide()
    {
        _pending = null;
        _panel.IsVisible = false;
    }
}
