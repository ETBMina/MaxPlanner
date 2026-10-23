using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace MaxPlanner.Presentation.Board;

/// <summary>A Board column's card list; its containers name each card for UI Automation (design §8.1–8.2).</summary>
public sealed partial class BoardCardList : ListView
{
    protected override DependencyObject GetContainerForItemOverride() => new BoardCardContainer();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is BoardCardContainer;
}

/// <summary>
/// A card container whose peer reads the card's current summary and record-keyed AutomationId
/// (<c>Board_TaskCard_{Id:N}</c> / <c>Board_SubtaskCard_{Id:N}</c>), so both follow incremental updates.
/// </summary>
public sealed partial class BoardCardContainer : ListViewItem
{
    protected override AutomationPeer OnCreateAutomationPeer() => new BoardCardAutomationPeer(this);
}

internal sealed partial class BoardCardAutomationPeer(BoardCardContainer owner) : ListViewItemAutomationPeer(owner)
{
    protected override string GetAutomationIdCore() => owner.Content is BoardCardViewModel card
        ? (card.IsTask ? "Board_TaskCard_" : "Board_SubtaskCard_") + card.Key.ToString("N")
        : base.GetAutomationIdCore();

    protected override string GetNameCore() => owner.Content is BoardCardViewModel card ? card.AccessibleName : base.GetNameCore();

    // Design §8.2: the card's help text points to its actions menu.
    protected override string GetHelpTextCore() => HelpText.Value.GetString("Board_CardHelpText");

    private static readonly Lazy<Common.Services.ResourceStrings> HelpText = new(() => new Common.Services.ResourceStrings());
}

/// <summary>Chooses the task card or the Subtask card template (design §4.2 anatomies).</summary>
public sealed partial class BoardCardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TaskTemplate { get; set; }

    public DataTemplate? SubtaskTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is BoardCardViewModel { IsSubtaskCard: true } ? SubtaskTemplate : TaskTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
