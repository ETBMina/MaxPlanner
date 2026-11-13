using System.Xml.Linq;
using AwesomeAssertions;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Tests.Common;

/// <summary>
/// The x:Uid strings that <c>ClearableDateField</c> and <c>ConflictReviewPanel</c> read in code resolve through the
/// production loader (<see cref="ResourceStrings"/>) over the built PRI to the resw text, never to the key itself.
/// </summary>
public sealed class XamlResourceKeysTests
{
    private const string AutomationNameProperty = "[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";
    private const string ToolTipProperty = "[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Resw = new(LoadResw);

    public static TheoryData<string> ClearButtonUids =>
    [
        "TaskEditor_ClearStartDateButton",
        "TaskEditor_ClearEndDateButton",
        "CrEditor_ClearPlannedStartButton",
        "CrEditor_ClearPlannedEndButton",
    ];

    public static TheoryData<string> ReloadMessageUids =>
    [
        "Conflict_ReloadMessage",
        "CrEditor_ReloadMessage",
        "SubtaskEditor_ReloadMessage",
    ];

    [Fact]
    public void The_keys_follow_the_resource_map_names_of_xuid_properties()
    {
        XamlResourceKeys.Text("Foo").Should().Be("Foo/Text");
        XamlResourceKeys.AutomationName("Foo").Should().Be("Foo/[using:Microsoft.UI.Xaml.Automation]AutomationProperties/Name");
        XamlResourceKeys.ToolTip("Foo").Should().Be("Foo/[using:Microsoft.UI.Xaml.Controls]ToolTipService/ToolTip");
    }

    // The Subtask card editor reuses the task editor's two uids, so these four cover all six Clear buttons.
    [Theory]
    [MemberData(nameof(ClearButtonUids))]
    public void A_clear_buttons_name_and_tooltip_resolve_to_the_resw_text(string uid)
    {
        Resolve(XamlResourceKeys.AutomationName(uid)).Should().Be(Resw.Value[uid + "." + AutomationNameProperty]);
        Resolve(XamlResourceKeys.ToolTip(uid)).Should().Be(Resw.Value[uid + "." + ToolTipProperty]);
    }

    [Theory]
    [MemberData(nameof(ReloadMessageUids))]
    public void A_reload_message_resolves_to_the_resw_text(string uid) =>
        Resolve(XamlResourceKeys.Text(uid)).Should().Be(Resw.Value[uid + ".Text"]);

    private static string Resolve(string key)
    {
        var text = ResourceStrings.Shared.GetString(key);
        text.Should().NotBe(key, "a missing resource comes back as its key");
        return text;
    }

    private static Dictionary<string, string> LoadResw()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MaxPlanner.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new InvalidOperationException("Repository root (MaxPlanner.slnx) not found.");
        var resw = XDocument.Load(Path.Combine(root, "src", "MaxPlanner.Presentation", "Strings", "en-US", "Resources.resw"));
        return resw.Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!, StringComparer.Ordinal);
    }
}
