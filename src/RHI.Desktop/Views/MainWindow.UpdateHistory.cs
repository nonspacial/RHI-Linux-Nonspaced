using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.Views;

public sealed partial class MainWindow
{
    /// <summary>Shared with the component service by App; falls back to the default path.</summary>
    public ComponentUpdateLogStore? UpdateLogStore { get; set; }

    private void UpdateHistory_Click(object? sender, RoutedEventArgs e) => RunUiSafe(async () =>
    {
        var store = UpdateLogStore ??= new ComponentUpdateLogStore();
        var list = new StackPanel { Spacing = 0 };
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Opacity = 0.6 };
        var clear = new Button { Content = L("Limpar histórico", "Clear history"), Padding = new Thickness(14, 7) };
        var close = new Button { Content = L("Fechar", "Close"), Padding = new Thickness(14, 7) };

        void Rebuild()
        {
            list.Children.Clear();
            IReadOnlyList<ComponentUpdateLogEntry> entries;
            string? corrupt;
            try
            {
                entries = store.GetAll();
                corrupt = store.CorruptLogPath;
            }
            catch (Exception ex)
            {
                entries = [];
                corrupt = null;
                list.Children.Add(new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed });
            }

            if (corrupt is not null)
            {
                list.Children.Add(new TextBlock
                {
                    Text = LF("O histórico de atualizações não pôde ser lido e foi copiado para {0}. Nada novo será registrado até que você limpe o histórico.", "The update history could not be read and was copied to {0}. Nothing new is recorded until you clear the history.", corrupt),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.OrangeRed,
                    Margin = new Thickness(0, 8, 0, 0)
                });
            }

            count.Text = entries.Count == 0
                ? L("Nenhuma atualização registrada.", "No updates recorded yet.")
                : LF("{0} registro(s)", "{0} event(s) recorded", entries.Count);
            clear.IsEnabled = entries.Count > 0 || corrupt is not null;

            if (entries.Count == 0 && corrupt is null)
            {
                list.Children.Add(new TextBlock
                {
                    Text = L("Nenhuma atualização de componente foi registrada ainda.\n\nO RHI registra cada instalação, atualização e remoção de componentes gerenciados (ReShade, RenoDX, OptiScaler, ReLimiter, Display Commander, Luma e outros) — por ação manual, Atualizar tudo ou a verificação em segundo plano.",
                        "No component updates have been recorded yet.\n\nRHI records every install, update and removal of managed components (ReShade, RenoDX, OptiScaler, ReLimiter, Display Commander, Luma and others) — from manual actions, Update all or the background check."),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                    Margin = new Thickness(0, 8, 0, 0)
                });
                return;
            }

            var today = DateTime.Today;
            foreach (var group in ComponentUpdateLogStore.GroupByLocalDate(entries))
            {
                list.Children.Add(new TextBlock
                {
                    Text = ComponentUpdateHistoryPresenter.DayLabel(group.LocalDate, today),
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Opacity = 0.7,
                    Margin = new Thickness(0, 16, 0, 4)
                });
                list.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#1E2630")) });
                foreach (var entry in group.Entries)
                    list.Children.Add(BuildUpdateHistoryRow(entry));
            }
        }

        Rebuild();

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(count); Grid.SetColumn(count, 0);
        footer.Children.Add(clear); Grid.SetColumn(clear, 1);
        footer.Children.Add(close); Grid.SetColumn(close, 2);

        var panel = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(18) };
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        panel.Children.Add(scroll);
        panel.Children.Add(footer); Grid.SetRow(footer, 1);

        var dialog = new Window
        {
            Title = L("RHI-Linux — Atualizações de componentes", "RHI-Linux — Component updates"),
            Width = 820,
            Height = 540,
            MinWidth = 620,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        close.Click += (_, _) => dialog.Close();
        clear.Click += (_, _) => RunUiSafe(async () =>
        {
            var confirmed = await ShowConfirmationAsync(
                L("Limpar histórico", "Clear history"),
                L("Apagar todo o histórico de atualizações de componentes? Os componentes instalados não são alterados.", "Delete the whole component update history? Installed components are not changed."),
                L("Limpar", "Clear"));
            if (!confirmed) return;
            try
            {
                store.Clear();
            }
            catch (Exception ex)
            {
                count.Text = ex.Message;
                return;
            }
            Rebuild();
        });
        await ShowFittedDialogAsync(dialog);
    });

    private static Control BuildUpdateHistoryRow(ComponentUpdateLogEntry entry)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*,210,140"), Margin = new Thickness(0, 5) };
        var time = new TextBlock { Text = entry.TimestampUtc.ToLocalTime().ToString("HH:mm"), FontSize = 11, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
        var name = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = entry.ComponentName, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        name.Children.Add(new TextBlock { Text = entry.GameName, FontSize = 10, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis });
        var version = new TextBlock { Text = ComponentUpdateHistoryPresenter.VersionText(entry), FontSize = 11, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var action = new TextBlock
        {
            Text = ComponentUpdateHistoryPresenter.ActionText(entry),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = entry.Success ? Brushes.LightSkyBlue : Brushes.OrangeRed
        };
        if (!string.IsNullOrWhiteSpace(entry.Message))
            ToolTip.SetTip(row, UiLocalizationService.Present(entry.Message));
        row.Children.Add(time); Grid.SetColumn(time, 0);
        row.Children.Add(name); Grid.SetColumn(name, 1);
        row.Children.Add(version); Grid.SetColumn(version, 2);
        row.Children.Add(action); Grid.SetColumn(action, 3);
        return row;
    }
}
