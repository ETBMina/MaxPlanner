using System.Collections.Specialized;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace PlatformControls.Projection;

public sealed partial class ProjectionPage : Page, IMutationContext
{
    // Strong, reference-keyed ids: a ConditionalWeakTable could hand out a new id when a CsWinRT wrapper
    // is collected and recreated for the same native element, which would fake a "new container".
    private readonly Dictionary<object, int> _elementIds = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _disposedCollections = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<NotifyCollectionChangedAction, int> _changeCounts = [];
    private readonly DispatcherTimer _diagnosticsTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ProjectionView? _view;
    private ItemViewModel? _trackedItem;
    private ScrollViewer? _scrollViewer;
    private int _buildCount;
    private int _changesTouchingTracked;
    private string _lastTrackedChange = "none";
    private bool _isLoaded;
    private ItemViewModel? _desiredSelection;
    private bool _isRestoringSelection;
    private int _selectionRestores;

    public ProjectionPage()
    {
        InitializeComponent();
        _diagnosticsTimer.Tick += (_, _) => RefreshDiagnostics();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ItemViewModel? FocusedItem =>
        XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is FrameworkElement { DataContext: ItemViewModel item }
            ? item
            : null;

    public ItemViewModel? SelectedItem => ItemsList.SelectedItem as ItemViewModel;

    public IReadOnlyList<ItemViewModel> Items => _view is { IsDisposed: false } view ? view.Items : [];

    public void Sample(int tick, MutationMode mode) =>
        SpikeLog.Write(string.Create(CultureInfo.InvariantCulture, $"sample tick={tick} mode={mode} | {DescribeFocus()} | {DescribeSelection()} | {DescribeScroll()} | {DescribeChanges()}"));

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        BuildView(UpdatesAsRefreshToggle.IsOn, MovesAsRemoveInsertToggle.IsOn);
        SpikeHost.Mutator.Context = this;
        _diagnosticsTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        if (ReferenceEquals(SpikeHost.Mutator.Context, this))
        {
            SpikeHost.Mutator.Context = null;
        }

        DisposeView("page unloaded (navigated away)");
        _diagnosticsTimer.Stop();
    }

    private void BuildView(bool updatesAsRefresh, bool movesAsRemoveInsert)
    {
        DisposeView("rebuild");
        _buildCount++;
        _changeCounts.Clear();
        _changesTouchingTracked = 0;
        _lastTrackedChange = "none";
        _trackedItem = null;

        var view = new ProjectionView(SpikeHost.Store, SpikeHost.UiScheduler, updatesAsRefresh, movesAsRemoveInsert);
        view.InitialBindCompleted += (_, _) => SpikeLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"View build #{_buildCount} variant={(updatesAsRefresh ? "B(Update->Refresh)" : "A(plain)")} moves={(movesAsRemoveInsert ? "Remove+Insert" : "Move")}: initial Connect+Filter+ObserveOn+Transform+SortAndBind of {view.Items.Count} items in {view.InitialBindMilliseconds:F1} ms"));
        ((INotifyCollectionChanged)view.Items).CollectionChanged += OnItemsCollectionChanged;
        view.ChangeSetApplied += OnChangeSetApplied;
        _view = view;
        ItemsList.ItemsSource = view.Items;
        RefreshDiagnostics();
    }

    private void DisposeView(string reason)
    {
        if (_view is null || _view.IsDisposed)
        {
            return;
        }

        _view.Dispose();
        _disposedCollections.Add(_view.Items);
        SpikeLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"View disposed ({reason}): store Connect() subscriptions={SpikeHost.Store.ActiveSubscriptions} filterHasObservers={_view.FilterHasObservers} comparerHasObservers={_view.ComparerHasObservers}"));
        RefreshDiagnostics();
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is not null && _disposedCollections.Contains(sender))
        {
            SpikeHost.ChangesAfterDispose++;
            SpikeLog.Write($"UNEXPECTED collection change after dispose: {e.Action}");
            return;
        }

        _changeCounts[e.Action] = _changeCounts.GetValueOrDefault(e.Action) + 1;
        var tracked = _trackedItem;
        if (tracked is not null && (Contains(e.OldItems, tracked) || Contains(e.NewItems, tracked)))
        {
            _changesTouchingTracked++;
            _lastTrackedChange = string.Create(CultureInfo.InvariantCulture, $"{e.Action} old@{e.OldStartingIndex} new@{e.NewStartingIndex}");
            SpikeLog.Write($"Collection change touching tracked item {tracked.Number}: {_lastTrackedChange} | {DescribeFocus()}");
        }
    }

    private static bool Contains(System.Collections.IList? items, ItemViewModel target)
    {
        if (items is null)
        {
            return false;
        }

        foreach (var item in items)
        {
            if (ReferenceEquals(item, target))
            {
                return true;
            }
        }

        return false;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ItemViewModel item)
        {
            return;
        }

        var containerId = ElementId(args.ItemContainer);
        item.ContainerPrepareCount++;
        item.LastContainerId = containerId;
        AutomationProperties.SetAutomationId(args.ItemContainer, $"Projection_Row_{item.Key}");
        AutomationProperties.SetName(args.ItemContainer, string.Create(CultureInfo.InvariantCulture, $"Item {item.Number:D5}"));
        if (ReferenceEquals(item, _trackedItem))
        {
            SpikeLog.Write(string.Create(CultureInfo.InvariantCulture, $"Container prepared for tracked item {item.Number}: container#{containerId} prepares={item.ContainerPrepareCount}"));
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The desired selection is view-model state; a collection-induced clear does not overwrite it.
        if (!_isRestoringSelection && ItemsList.SelectedItem is ItemViewModel selected)
        {
            _desiredSelection = selected;
        }

        SpikeLog.Write($"SelectionChanged: {DescribeSelection()}");
    }

    /// <summary>
    /// Spike 5 mitigation: a Move notification for another row can clear the ListView selection, so after each
    /// applied change set the desired selection is re-applied (setting SelectedItem does not move keyboard focus).
    /// </summary>
    private void OnChangeSetApplied(object? sender, EventArgs e)
    {
        if (!RestoreSelectionToggle.IsOn || _desiredSelection is null || ReferenceEquals(ItemsList.SelectedItem, _desiredSelection)
            || sender is not ProjectionView { IsDisposed: false } view || view.Items.IndexOf(_desiredSelection) < 0)
        {
            return;
        }

        _isRestoringSelection = true;
        ItemsList.SelectedItem = _desiredSelection;
        _isRestoringSelection = false;
        _selectionRestores++;
        SpikeLog.Write($"Selection restored ({_selectionRestores}): {DescribeSelection()} | {DescribeFocus()}");
    }

    private void OnStartOtherRowsClick(object sender, RoutedEventArgs e) => SpikeHost.Mutator.Start(MutationMode.OtherRows);

    private void OnStartMoveFocusedClick(object sender, RoutedEventArgs e) => SpikeHost.Mutator.Start(MutationMode.MoveFocused);

    private void OnStopClick(object sender, RoutedEventArgs e) => SpikeHost.Mutator.Stop();

    private void OnDisposeClick(object sender, RoutedEventArgs e) => DisposeView("Dispose button");

    private void OnRebuildClick(object sender, RoutedEventArgs e) => BuildView(UpdatesAsRefreshToggle.IsOn, MovesAsRemoveInsertToggle.IsOn);

    private void OnFocusTargetClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FocusTargetBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            || Items.FirstOrDefault(item => item.Number == number) is not { } target)
        {
            return;
        }

        ItemsList.ScrollIntoView(target);
        ItemsList.UpdateLayout();
        if (ItemsList.ContainerFromItem(target) is ListViewItem container && FindDescendant<TextBox>(container) is { } box)
        {
            var focused = box.Focus(FocusState.Keyboard);
            SpikeLog.Write(string.Create(CultureInfo.InvariantCulture, $"Harness focused draft of item {number}: {focused}"));
        }
    }

    private void OnUpdatesAsRefreshToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            BuildView(UpdatesAsRefreshToggle.IsOn, MovesAsRemoveInsertToggle.IsOn);
        }
    }

    private void RefreshDiagnostics()
    {
        if (FocusedItem is { } focused)
        {
            _trackedItem = focused;
        }

        var view = _view;
        PipelineText.Text = view is null
            ? "pipeline: none"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"pipeline build #{_buildCount}: variant {(view.UpdatesAsRefresh ? "B (Update→Refresh)" : "A (plain)")} moves={(view.MovesAsRemoveInsert ? "Remove+Insert" : "Move")} | disposed={view.IsDisposed} | items={view.Items.Count} | initial bind {view.InitialBindMilliseconds:F1} ms | change sets bound={view.ChangeSetsBound} | filterHasObservers={view.FilterHasObservers} comparerHasObservers={view.ComparerHasObservers} | store Connect() subscriptions={SpikeHost.Store.ActiveSubscriptions}");
        FocusText.Text = DescribeFocus();
        SelectionText.Text = DescribeSelectionWithRestores();
        ScrollText.Text = DescribeScroll();
        ChangesText.Text = DescribeChanges();
        LatencyText.Text = view is null ? "latency: n/a" : $"store→UI inline-update latency: {view.DescribeLatency()}";
    }

    private string DescribeFocus()
    {
        var builder = new System.Text.StringBuilder("focused: ");
        var element = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot);
        if (element is null)
        {
            builder.Append("none");
        }
        else
        {
            var frameworkElement = element as FrameworkElement;
            builder.Append(CultureInfo.InvariantCulture, $"{element.GetType().Name} '{(frameworkElement is null ? string.Empty : AutomationProperties.GetAutomationId(frameworkElement))}' element#{ElementId(element)}");
            if (frameworkElement?.DataContext is ItemViewModel item)
            {
                var container = FindAncestor<ListViewItem>(frameworkElement);
                builder.Append(CultureInfo.InvariantCulture, $" container#{(container is null ? 0 : ElementId(container))}");
            }
        }

        var tracked = _trackedItem;
        if (tracked is not null)
        {
            var index = _view?.Items.IndexOf(tracked) ?? -1;
            builder.Append(CultureInfo.InvariantCulture, $" | tracked item {tracked.Number:D5} {tracked.InstanceText} index={index} lastContainer#{tracked.LastContainerId} containerPrepares={tracked.ContainerPrepareCount} inlineUpdates={tracked.InlineUpdateCount} title='{tracked.Title}' draft='{tracked.Draft}'");
        }

        return builder.ToString();
    }

    private string DescribeSelection() =>
        ItemsList.SelectedItem is ItemViewModel selected
            ? string.Create(CultureInfo.InvariantCulture, $"selected: index={ItemsList.SelectedIndex} item {selected.Number:D5} {selected.InstanceText}")
            : string.Create(CultureInfo.InvariantCulture, $"selected: none (index={ItemsList.SelectedIndex})");

    private string DescribeSelectionWithRestores() => string.Create(CultureInfo.InvariantCulture, $"{DescribeSelection()} | restores={_selectionRestores}");

    private string DescribeScroll()
    {
        _scrollViewer ??= FindDescendant<ScrollViewer>(ItemsList);
        var panel = ItemsList.ItemsPanelRoot as ItemsStackPanel;
        return _scrollViewer is null
            ? "scroll: no ScrollViewer yet"
            : string.Create(CultureInfo.InvariantCulture, $"scroll: VerticalOffset={_scrollViewer.VerticalOffset:F1} ViewportHeight={_scrollViewer.ViewportHeight:F0} ExtentHeight={_scrollViewer.ExtentHeight:F0} firstVisible={panel?.FirstVisibleIndex} lastVisible={panel?.LastVisibleIndex}");
    }

    private string DescribeChanges() => string.Create(
        CultureInfo.InvariantCulture,
        $"collection changes: add={_changeCounts.GetValueOrDefault(NotifyCollectionChangedAction.Add)} remove={_changeCounts.GetValueOrDefault(NotifyCollectionChangedAction.Remove)} replace={_changeCounts.GetValueOrDefault(NotifyCollectionChangedAction.Replace)} move={_changeCounts.GetValueOrDefault(NotifyCollectionChangedAction.Move)} reset={_changeCounts.GetValueOrDefault(NotifyCollectionChangedAction.Reset)} | touching tracked row={_changesTouchingTracked} last={_lastTrackedChange}");

    private int ElementId(object element)
    {
        if (!_elementIds.TryGetValue(element, out var id))
        {
            id = _elementIds.Count + 1;
            _elementIds[element] = id;
        }

        return id;
    }

    private static T? FindAncestor<T>(DependencyObject start)
        where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(start); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
