using System.ComponentModel;
using MaxPlanner.Presentation.Common;
using MaxPlanner.Presentation.Common.Controls;
using MaxPlanner.Presentation.Common.Fields;
using MaxPlanner.Presentation.Common.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;

namespace MaxPlanner.Presentation.Grid;

/// <summary>
/// The flyout editor of one Grid cell (design §4.3: Notes with Save Ctrl+Enter / Cancel; people and tag pickers;
/// list custom fields). Light dismiss is leaving the cell: B-07 attempts a commit, and a failure keeps the flyout
/// and its error. The page holds the open instance, so a page switch can hide it with its draft kept and reopen it.
/// </summary>
internal sealed class GridEditorFlyout
{
    private readonly GridPageViewModel _viewModel;
    private readonly GridCell _cell;
    private readonly Action<GridRowViewModel, string> _focusCell;
    private readonly Action<GridEditorFlyout> _closed;
    private readonly Flyout _flyout;
    private readonly TextBlock _error;
    private readonly ProgressRing _ring;
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly long? _tapOpenedAt;
    private bool _isCloseApproved;

    /// <summary>Builds the flyout for the cell's open edit; <see cref="Show"/> opens it.</summary>
    /// <param name="openedByTap">The edit was started by a single tap, so the double-click guard applies.</param>
    /// <param name="focusCell">Gives a cell keyboard focus after a successful commit.</param>
    /// <param name="closed">Called when the flyout has closed, before focus returns to the cell.</param>
    public GridEditorFlyout(
        GridPageViewModel viewModel,
        GridCell cell,
        GridRowViewModel row,
        GridColumnViewModel column,
        bool openedByTap,
        Action<GridRowViewModel, string> focusCell,
        Action<GridEditorFlyout> closed)
    {
        _viewModel = viewModel;
        _cell = cell;
        _focusCell = focusCell;
        _closed = closed;
        Row = row;
        Column = column;

        var strings = ResourceStrings.Shared;
        Control focus;
        FrameworkElement editor;
        switch (EditorFor(column.EditorKind, row.DraftTokens is not null, row.DraftCustom is not null))
        {
            case GridFlyoutEditorKind.TokenPicker:
                var picker = new TokenPicker { ViewModel = row.DraftTokens!, Header = column.Header, BoxAutomationId = "Grid_CellEditor", MinWidth = 280 };
                editor = picker;
                focus = picker;
                picker.Loaded += (_, _) => picker.FocusBox();
                break;
            case GridFlyoutEditorKind.CustomField:
                (editor, focus) = CustomFieldEditors.Create(row.DraftCustom!, "Grid", showHeader: true);
                break;
            default:
                var notes = new TextBox
                {
                    Header = column.Header,
                    Text = row.DraftText,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 96,
                    MaxHeight = 320,
                    Width = 360,
                    MaxLength = Domain.Common.DomainLimits.LongText,
                };
                ScrollViewer.SetVerticalScrollBarVisibility(notes, ScrollBarVisibility.Auto);
                AutomationProperties.SetAutomationId(notes, "Grid_CellEditor");
                notes.TextChanged += (_, _) => row.DraftText = notes.Text;
                editor = notes;
                focus = notes;
                break;
        }

        _error = new TextBlock { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["ErrorCaptionTextStyle"], MaxWidth = 360 };
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        AutomationProperties.SetAutomationId(_error, "Grid_CellError");
        _ring = new ProgressRing { Width = 16, Height = 16, IsActive = false };
        _save = new Button { Content = strings.GetString("Grid_FlyoutSave"), Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] };
        _cancel = new Button { Content = strings.GetString("Common_Cancel") };
        AutomationProperties.SetAutomationId(_save, "Grid_FlyoutSaveButton");
        AutomationProperties.SetAutomationId(_cancel, "Grid_FlyoutCancelButton");
        ToolTipService.SetToolTip(_save, strings.GetString("Grid_FlyoutSaveTooltip"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_ring);
        buttons.Children.Add(_save);
        buttons.Children.Add(_cancel);
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(editor);
        root.Children.Add(_error);
        root.Children.Add(buttons);

        _flyout = new Flyout { Content = root, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft, ShouldConstrainToRootBounds = true };

        // A reopened flyout (after a page switch) starts from the row's current error and saving state.
        OnRowChanged(null, null);
        row.PropertyChanged += OnRowChanged;
        _tapOpenedAt = openedByTap ? Environment.TickCount64 : null;
        _save.Click += (_, _) => CommitAsync().Forget();
        _cancel.Click += (_, _) => Cancel();

        // Design §7.4: Ctrl+Enter saves (plain Enter inserts a line break in Notes); Esc cancels.
        root.PreviewKeyDown += (sender, e) =>
        {
            if (e.Key == VirtualKey.Enter && GridInput.IsDown(VirtualKey.Control))
            {
                e.Handled = true;
                CommitAsync().Forget();
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                Cancel();
            }
        };

        // Light dismiss is leaving the cell: B-07 attempts a commit; a failure keeps the flyout and its error.
        _flyout.Closing += (sender, args) =>
        {
            if (_isCloseApproved)
            {
                return;
            }

            args.Cancel = true;

            // The second click of the double-click that opened this flyout lands on the light-dismiss layer; keep it open.
            if (!Row.IsSaving && !GridInput.IsWithinDoubleClickWindow(_tapOpenedAt))
            {
                CommitAsync().Forget();
            }
        };
        _flyout.Closed += (_, _) =>
        {
            Row.PropertyChanged -= OnRowChanged;
            _closed(this);
            _cell.Focus(FocusState.Programmatic);
        };
        _flyout.Opened += (_, _) => CustomFieldEditors.Focus(focus);
    }

    public GridRowViewModel Row { get; }

    public GridColumnViewModel Column { get; }

    public bool IsOpen => _flyout.IsOpen;

    /// <summary>Opens the flyout below its cell.</summary>
    public void Show() => _flyout.ShowAt(_cell);

    /// <summary>Closes the flyout without commit or cancel, so the row keeps its draft and edit state.</summary>
    public void HideKeepingEdit()
    {
        _isCloseApproved = true;
        _flyout.Hide();
    }

    /// <summary>
    /// The editor for a flyout column: the people or tag picker and the custom field editor need the row's draft for
    /// them; anything else edits as multi-line text.
    /// </summary>
    internal static GridFlyoutEditorKind EditorFor(GridEditorKind kind, bool hasDraftTokens, bool hasDraftCustom) => kind switch
    {
        GridEditorKind.People or GridEditorKind.Tags when hasDraftTokens => GridFlyoutEditorKind.TokenPicker,
        GridEditorKind.Custom when hasDraftCustom => GridFlyoutEditorKind.CustomField,
        _ => GridFlyoutEditorKind.Notes,
    };

    private void OnRowChanged(object? sender, PropertyChangedEventArgs? e)
    {
        // An edit that ended elsewhere (a commit started before the page was hidden and this flyout reopened)
        // leaves nothing to edit here.
        if (Row.EditingColumn != Column.Key && _flyout.IsOpen)
        {
            HideKeepingEdit();
            return;
        }

        _error.Text = Row.ErrorMessage ?? string.Empty;
        _ring.IsActive = Row.IsSaving;
        _save.IsEnabled = _cancel.IsEnabled = !Row.IsSaving;
    }

    private async Task CommitAsync()
    {
        if (Row.IsSaving)
        {
            return;
        }

        var column = Row.EditingColumn ?? _cell.Column;
        if (await _viewModel.CommitAsync(Row))
        {
            HideKeepingEdit();
            _focusCell(Row, column);
            return;
        }

        if (Row.ErrorMessage is { } message)
        {
            GridCellAutomationPeer.AnnounceError(_cell, message);
        }
    }

    private void Cancel()
    {
        if (Row.IsSaving)
        {
            return;
        }

        _viewModel.CancelEdit(Row);
        HideKeepingEdit();
    }
}
