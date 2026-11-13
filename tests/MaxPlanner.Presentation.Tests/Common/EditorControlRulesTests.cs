using AwesomeAssertions;
using MaxPlanner.Presentation.Common.Dialogs;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// The display decisions of the shared editor controls. x:Bind hands a null string to a string dependency property as
/// an empty one, so each decision must treat null and empty the same.
/// </summary>
public sealed class EditorControlRulesTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Fix 1 field to save", true)]
    public void A_message_bar_opens_only_with_text(string? message, bool shown) =>
        EditorControlRules.IsMessageShown(message).Should().Be(shown);

    [Theory]
    [InlineData(null, "Conflict_ReloadMessage")]
    [InlineData("", "Conflict_ReloadMessage")]
    [InlineData("CrEditor_ReloadMessage", "CrEditor_ReloadMessage")]
    public void A_missing_reload_message_key_falls_back_to_the_default(string? key, string expected) =>
        EditorControlRules.ReloadMessageKeyOrDefault(key).Should().Be(expected);
}
