using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace MaxPlanner.Presentation.Board;

/// <summary>A Board bucket's card list; its containers name each card for UI Automation (design §8.1–8.2).</summary>
public sealed partial class BoardCardList : ListView
{
    public BoardCardList()
    {
        // ItemClick is raised only for a press and release that did not start a drag, and not when a child
        // button or link handled the press, so it is the whole-card open target.
        ItemClick += (_, e) =>
        {
            if (e.ClickedItem is BoardCardViewModel card)
            {
                RaiseCardInvoked(card);
            }
        };
    }

    /// <summary>Raised when a card is clicked or invoked through UI Automation; the view opens it.</summary>
    public event EventHandler<BoardCardViewModel>? CardInvoked;

    internal void RaiseCardInvoked(BoardCardViewModel card) => CardInvoked?.Invoke(this, card);

    protected override DependencyObject GetContainerForItemOverride() => new BoardCardContainer();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is BoardCardContainer;
}

/// <summary>
/// A card container whose peer reads the card's current summary and record-keyed AutomationId
/// (<c>Board_TaskCard_{Id:N}</c> / <c>Board_SubtaskCard_{Id:N}</c>), so both follow incremental updates.
/// </summary>
public sealed partial class BoardCardContainer : ListViewItem
{
    // The card's corner radius (design §4.2). The default template binds its presenter's CornerRadius to the
    // ListViewItemCornerRadius theme resource, not to this control, so a style setter or a resource override in the list
    // does not reach it. The presenter draws the pointer-over, pressed and selected backplates and the focus ring with it.
    private static readonly CornerRadius CardCornerRadius = new(8);

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("Root") is ListViewItemPresenter presenter)
        {
            presenter.CornerRadius = CardCornerRadius;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new BoardCardAutomationPeer(this);

    internal void Invoke()
    {
        if (Content is BoardCardViewModel card && ItemsControl.ItemsControlFromItemContainer(this) is BoardCardList list)
        {
            list.RaiseCardInvoked(card);
        }
    }
}

internal sealed partial class BoardCardAutomationPeer(BoardCardContainer owner) : ListViewItemAutomationPeer(owner), IInvokeProvider
{
    protected override string GetAutomationIdCore() => owner.Content is BoardCardViewModel card
        ? (card.IsTask ? "Board_TaskCard_" : "Board_SubtaskCard_") + card.Key.ToString("N")
        : base.GetAutomationIdCore();

    protected override string GetNameCore() => owner.Content is BoardCardViewModel card ? card.AccessibleName : base.GetNameCore();

    // Design §8.2: the card's help text points to its actions menu.
    protected override string GetHelpTextCore() => Common.Services.ResourceStrings.Shared.GetString("Board_CardHelpText");

    // Invoke opens the card, the same route as a click or Enter.
    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

    public void Invoke() => owner.Invoke();
}

/// <summary>
/// One line of card chips (design §4.2 ID row and chip row) that never wraps or overflows the card: each visible child
/// gets its natural width while they all fit; otherwise the width is shared out so the narrow ones keep theirs and the
/// wide ones are trimmed (their text ellipsizes). Collapsed children take no space or spacing.
/// </summary>
public sealed partial class BoardChipRow : Panel
{
    public double Spacing { get; set; } = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        var visible = Children.Where(c => c.Visibility == Visibility.Visible).ToList();
        foreach (var child in Children)
        {
            child.Measure(child.Visibility == Visibility.Visible ? unbounded : default);
        }

        var spacing = Spacing * Math.Max(0, visible.Count - 1);
        var natural = visible.Sum(c => c.DesiredSize.Width) + spacing;
        if (natural > availableSize.Width)
        {
            // Water-filling: the narrowest children keep their width; the rest share what is left equally.
            var left = Math.Max(0, availableSize.Width - spacing);
            var pending = visible.OrderBy(c => c.DesiredSize.Width).ToList();
            for (var i = 0; i < pending.Count; i++)
            {
                var share = left / (pending.Count - i);
                var width = Math.Min(pending[i].DesiredSize.Width, share);
                pending[i].Measure(new Size(width, availableSize.Height));
                left -= pending[i].DesiredSize.Width;
            }
        }

        var height = visible.Count == 0 ? 0 : visible.Max(c => c.DesiredSize.Height);
        return new Size(Math.Min(visible.Sum(c => c.DesiredSize.Width) + spacing, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        foreach (var child in Children)
        {
            if (child.Visibility != Visibility.Visible)
            {
                child.Arrange(default);
                continue;
            }

            var size = child.DesiredSize;
            child.Arrange(new Rect(x, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
            x += size.Width + Spacing;
        }

        return finalSize;
    }
}
