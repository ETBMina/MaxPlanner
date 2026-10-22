using System.Globalization;

namespace MaxPlanner.Presentation.Common.Services;

/// <summary>
/// Localized UI text for code (ADR 0014). XAML uses <c>x:Uid</c>; ViewModels use this so they stay
/// testable without the Windows resource runtime.
/// </summary>
public interface ILocalizedStrings
{
    /// <summary>The en-US text for <paramref name="key"/> from the Presentation <c>Resources.resw</c>.</summary>
    string GetString(string key);
}

public static class LocalizedStringsExtensions
{
    /// <summary>Formats the resource with the user's culture (T-08: visible numbers and dates).</summary>
    public static string Format(this ILocalizedStrings strings, string key, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return string.Format(CultureInfo.CurrentCulture, strings.GetString(key), args);
    }
}
