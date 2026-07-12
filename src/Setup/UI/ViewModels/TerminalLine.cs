using System.Collections.Generic;
using System.Windows.Media;
using Setup.Core.Models;

namespace Setup.UI.ViewModels;

/// <summary>
/// One rendered line in the live terminal log. Immutable. The <see cref="Brush"/>
/// is resolved from <see cref="Level"/> via a static, frozen brush map so a line
/// can be created and colored on any thread without a live ResourceDictionary.
/// </summary>
public sealed class TerminalLine
{
    /// <summary>Palette brushes keyed by log level. Frozen for cross-thread use.</summary>
    private static readonly IReadOnlyDictionary<LogLevel, Brush> BrushMap = BuildBrushMap();

    /// <summary>Creates a terminal line.</summary>
    /// <param name="text">The line text (already formatted, no tag prefix required).</param>
    /// <param name="level">Severity, controls the color.</param>
    /// <param name="timestamp">Optional pre-formatted timestamp shown dimmed at the start.</param>
    public TerminalLine(string text, LogLevel level = LogLevel.Normal, string? timestamp = null)
    {
        Text = text ?? string.Empty;
        Level = level;
        Timestamp = timestamp;
        Brush = BrushFor(level);
    }

    /// <summary>The visible text of the line.</summary>
    public string Text { get; }

    /// <summary>Severity level driving the color.</summary>
    public LogLevel Level { get; }

    /// <summary>Optional timestamp string (e.g. "12:04:31").</summary>
    public string? Timestamp { get; }

    /// <summary>Foreground brush mapped from <see cref="Level"/>.</summary>
    public Brush Brush { get; }

    /// <summary>True when a timestamp is present (used to toggle its column in XAML).</summary>
    public bool HasTimestamp => !string.IsNullOrEmpty(Timestamp);

    /// <summary>Resolves the palette brush for a log level.</summary>
    public static Brush BrushFor(LogLevel level) =>
        BrushMap.TryGetValue(level, out var brush) ? brush : BrushMap[LogLevel.Normal];

    private static IReadOnlyDictionary<LogLevel, Brush> BuildBrushMap()
    {
        // Mirrors Themes/Colors.xaml so colors match whether or not a
        // ResourceDictionary is loaded (e.g. designer / background threads).
        var map = new Dictionary<LogLevel, Brush>
        {
            [LogLevel.Normal] = Freeze(Color.FromRgb(0xE6, 0xED, 0xF3)),   // text
            [LogLevel.Info] = Freeze(Color.FromRgb(0x58, 0xA6, 0xFF)),     // blue
            [LogLevel.Success] = Freeze(Color.FromRgb(0x3F, 0xB9, 0x50)),  // green
            [LogLevel.Warning] = Freeze(Color.FromRgb(0xD2, 0x99, 0x22)),  // yellow
            [LogLevel.Error] = Freeze(Color.FromRgb(0xF8, 0x51, 0x49)),    // red
            [LogLevel.Download] = Freeze(Color.FromRgb(0x39, 0xC5, 0xCF)), // cyan
        };
        return map;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
