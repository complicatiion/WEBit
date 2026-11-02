using Avalonia;
using Avalonia.Media;
using WEBit.Contracts;
using WEBit.Core.Models;

namespace WEBit.App.ViewModels;

/// <summary>Theme resources (brushes, icons) for view models.</summary>
public static class Res
{
    public static IBrush Brush(string key)
    {
        if (Application.Current is { } app && app.TryGetResource(key, null, out var value) && value is IBrush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    public static Geometry? Icon(string key)
    {
        if (Application.Current is { } app && app.TryGetResource(key, null, out var value) && value is Geometry geometry)
        {
            return geometry;
        }

        return null;
    }

    public static IBrush ParseBrush(string? hex, string fallbackKey = "BrushAccent")
    {
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color))
        {
            return new SolidColorBrush(color);
        }

        return Brush(fallbackKey);
    }

    public static IBrush LevelBrush(IssueLevel level) => level switch
    {
        IssueLevel.Ok => Brush("BrushSecondary"),
        IssueLevel.Info => Brush("BrushAccentHover"),
        IssueLevel.Warning => Brush("BrushWarning"),
        _ => Brush("BrushDanger")
    };

    public static Geometry? LevelIcon(IssueLevel level) => level switch
    {
        IssueLevel.Ok => Icon("IconCheckCircle"),
        IssueLevel.Info => Icon("IconInfo"),
        IssueLevel.Warning => Icon("IconAlert"),
        _ => Icon("IconCloseCircle")
    };

    public static IBrush StateBrush(SiteRunState state) => state switch
    {
        SiteRunState.Running => Brush("BrushSecondary"),
        SiteRunState.Starting => Brush("BrushAccentHover"),
        SiteRunState.Error => Brush("BrushDanger"),
        _ => Brush("BrushTextDisabled")
    };

    public static IBrush StateSoftBrush(SiteRunState state) => state switch
    {
        SiteRunState.Running => Brush("BrushSecondarySoft"),
        SiteRunState.Starting => Brush("BrushAccentSoft"),
        SiteRunState.Error => Brush("BrushDangerSoft"),
        _ => Brush("BrushNeutralSoft")
    };
}
