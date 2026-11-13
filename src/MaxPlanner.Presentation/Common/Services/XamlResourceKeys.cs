namespace MaxPlanner.Presentation.Common.Services;

/// <summary>
/// The names under which the resource map holds the strings an <c>x:Uid</c> applies, for controls that read them in
/// code. A resw entry <c>Foo.Text</c> is <c>Foo/Text</c>, and an attached property such as
/// <c>Foo.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name</c> is
/// <c>Foo/[using:Microsoft.UI.Xaml.Automation]AutomationProperties/Name</c>.
/// </summary>
public static class XamlResourceKeys
{
    /// <summary>The <c>{uid}.Text</c> string.</summary>
    public static string Text(string uid) => uid + "/Text";

    /// <summary>The <c>{uid}.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name</c> string.</summary>
    public static string AutomationName(string uid) => uid + "/[using:Microsoft.UI.Xaml.Automation]AutomationProperties/Name";

    /// <summary>The <c>{uid}.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip</c> string.</summary>
    public static string ToolTip(string uid) => uid + "/[using:Microsoft.UI.Xaml.Controls]ToolTipService/ToolTip";
}
