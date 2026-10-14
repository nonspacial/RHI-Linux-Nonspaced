using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

public sealed partial class MainWindow
{
    // Upstream 2.7.6 collapsed-section summaries. DlssStreamline already shows its summary line in
    // the header at all times; ReShadeProfile has no upstream counterpart.
    private static readonly string[] SummarySectionKeys = ["Runtime", "ManagedComponents", "Extras"];
    private static readonly IBrush SummaryLabelBrush = new SolidColorBrush(Color.Parse("#A0AABB"));
    private static readonly IBrush SummarySeparatorBrush = new SolidColorBrush(Color.Parse("#6B7A8E"));
    private static readonly IBrush SummaryVersionBrush = new SolidColorBrush(Color.Parse("#5ECB7D"));

    private MainWindowViewModel? _summaryViewModel;
    private bool _summaryRefreshQueued;

    private void InitializeSectionSummaries()
        => DataContextChanged += (_, _) => AttachSummaryViewModel(DataContext as MainWindowViewModel);

    private void AttachSummaryViewModel(MainWindowViewModel? vm)
    {
        if (_summaryViewModel is not null)
        {
            _summaryViewModel.PropertyChanged -= SummaryViewModel_PropertyChanged;
            _summaryViewModel.Extras.PropertyChanged -= SummaryViewModel_PropertyChanged;
            _summaryViewModel.Components.CollectionChanged -= SummaryComponents_CollectionChanged;
        }
        _summaryViewModel = vm;
        if (vm is not null)
        {
            vm.PropertyChanged += SummaryViewModel_PropertyChanged;
            vm.Extras.PropertyChanged += SummaryViewModel_PropertyChanged;
            vm.Components.CollectionChanged += SummaryComponents_CollectionChanged;
        }
        QueueSectionSummaryRefresh();
    }

    private void SummaryViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or ""
            or nameof(MainWindowViewModel.SelectedGame)
            or nameof(MainWindowViewModel.SelectedGraphicsApiOverride)
            or nameof(ExtrasViewModel.UalStatusText)
            or nameof(ExtrasViewModel.ShortFuseStatusText))
            QueueSectionSummaryRefresh();
    }

    private void SummaryComponents_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => QueueSectionSummaryRefresh();

    // Component rows are cleared and re-added one by one; coalesce into a single refresh.
    private void QueueSectionSummaryRefresh()
    {
        if (_summaryRefreshQueued) return;
        _summaryRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _summaryRefreshQueued = false;
            UpdateSectionSummaries();
        });
    }

    private void UpdateSectionSummaries()
    {
        var collapsed = _detailLayout.CollapsedSections.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vm = DataContext as MainWindowViewModel;
        foreach (var key in SummarySectionKeys)
        {
            var block = this.FindControl<TextBlock>($"{key}SectionSummary");
            if (block is null) continue;
            // Only collapsed sections need the summary, so expanded ones never query the records.
            var items = vm is null || !collapsed.Contains(key) ? [] : SummaryItems(key, vm);
            block.Inlines ??= [];
            block.Inlines.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) block.Inlines.Add(new Run(DetailSectionSummaryPresenter.Separator) { Foreground = SummarySeparatorBrush });
                block.Inlines.Add(new Run(items[i].Label) { Foreground = SummaryLabelBrush });
                if (items[i].Version is { } version)
                    block.Inlines.Add(new Run(" " + version) { Foreground = SummaryVersionBrush });
            }
            ToolTip.SetTip(block, items.Count == 0 ? null : DetailSectionSummaryPresenter.Text(items));
            block.IsVisible = items.Count > 0;
        }
    }

    private static IReadOnlyList<SectionSummaryItem> SummaryItems(string key, MainWindowViewModel vm) => key switch
    {
        "Runtime" => DetailSectionSummaryPresenter.Runtime(vm.SelectedGame),
        "ManagedComponents" => DetailSectionSummaryPresenter.ManagedComponents(vm.Components),
        "Extras" => DetailSectionSummaryPresenter.Extras(vm.Extras.UalInstalled, vm.Extras.ShortFuseInstalled, vm.Extras.ShortFuseInstalledVersion),
        _ => []
    };
}
