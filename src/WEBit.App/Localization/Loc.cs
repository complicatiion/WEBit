using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using WEBit.Core.Models;

namespace WEBit.App.Localization;

/// <summary>Runtime language switch (English/German). Texts are app resources bound via DynamicResource.</summary>
public static class Loc
{
    private static IReadOnlyDictionary<string, string> _current = Strings.En;
    private static ResourceDictionary? _applied;

    public static string Language { get; private set; } = "en";

    public static event Action? LanguageChanged;

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
    [
        ("en", "English"),
        ("de", "German")
    ];

    /// <summary>English unless the user picked another language.</summary>
    public const string DefaultLanguage = "en";

    public static void Apply(string? language)
    {
        Language = string.Equals(language, "de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
        _current = Language == "de" ? Strings.De : Strings.En;

        // Swap one dictionary: a single change notification instead of hundreds.
        var resources = Application.Current?.Resources;
        if (resources is not null)
        {
            var dictionary = new ResourceDictionary();
            foreach (var pair in _current)
            {
                dictionary[pair.Key] = pair.Value;
            }

            if (_applied is not null)
            {
                resources.MergedDictionaries.Remove(_applied);
            }

            resources.MergedDictionaries.Add(dictionary);
            _applied = dictionary;
        }

        LanguageChanged?.Invoke();
    }

    public static string T(string key) =>
        _current.TryGetValue(key, out var value) ? value
        : Strings.En.TryGetValue(key, out var fallback) ? fallback
        : key;

    public static string F(string key, params object?[] args)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, T(key), args);
        }
        catch (FormatException)
        {
            return T(key);
        }
    }

    /// <summary>Translates a service message (code + args).</summary>
    public static string Message(LocalizedMessage? message)
    {
        if (message is null || string.IsNullOrEmpty(message.Code))
        {
            return "";
        }

        var key = "Msg" + message.Code;
        if (!_current.ContainsKey(key) && !Strings.En.ContainsKey(key))
        {
            return message.ToString();
        }

        return F(key, message.Args.Cast<object?>().ToArray());
    }
}
