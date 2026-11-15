using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace MaxPlanner.Presentation.Common.Controls;

/// <summary>
/// The UIA peer of an inline on-demand <see cref="TokenPicker"/> (the task editor's tags): an editable field named by the
/// picker's header ("Tags"). Its Value is the typed search text; setting it reveals the input with that text. It is
/// read-only while the picker is. The chips and the revealed input are its children.
/// </summary>
internal sealed partial class TagFieldAutomationPeer(TokenPicker owner) : FrameworkElementAutomationPeer(owner), IValueProvider
{
    public bool IsReadOnly => !owner.IsEditable;

    public string Value => owner.FieldValue;

    public void SetValue(string value) => owner.SetFieldValueFromAutomation(value);

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

    protected override string GetClassNameCore() => nameof(TokenPicker);
}
