using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Setup.Core.Models;

namespace Setup.UI.ViewModels;

/// <summary>
/// A single row in the checklist / steps panel. Its <see cref="Status"/> is
/// mutable; changing it raises change notifications for the derived
/// <see cref="Icon"/> and <see cref="IconBrush"/> so the glyph and color update
/// live as an operation moves Pending → InProgress → Success/Failed/Skipped.
/// </summary>
public sealed class TrackedItem : INotifyPropertyChanged
{
    private OperationStatus _status;

    /// <summary>Creates a tracked item.</summary>
    /// <param name="key">Stable identifier used to find and update the row.</param>
    /// <param name="displayName">Text shown next to the status glyph.</param>
    /// <param name="status">Initial status.</param>
    public TrackedItem(string key, string displayName, OperationStatus status = OperationStatus.Pending)
    {
        Key = key ?? string.Empty;
        DisplayName = displayName ?? string.Empty;
        _status = status;
    }

    /// <summary>Stable identifier (not shown).</summary>
    public string Key { get; }

    /// <summary>Human-readable label.</summary>
    public string DisplayName { get; }

    /// <summary>Current status; setting it refreshes the icon and its color.</summary>
    public OperationStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(IconBrush));
        }
    }

    /// <summary>Status glyph.</summary>
    public string Icon => _status switch
    {
        OperationStatus.InProgress => "⏳", // ⏳
        OperationStatus.Success => "✔",    // ✔
        OperationStatus.Failed => "✖",     // ✖
        OperationStatus.Skipped => "↷",    // ↷
        _ => "•"                            // • (Pending)
    };

    /// <summary>Palette brush for the current status glyph.</summary>
    public Brush IconBrush => _status switch
    {
        OperationStatus.InProgress => TerminalLine.BrushFor(LogLevel.Download), // cyan
        OperationStatus.Success => TerminalLine.BrushFor(LogLevel.Success),     // green
        OperationStatus.Failed => TerminalLine.BrushFor(LogLevel.Error),        // red
        OperationStatus.Skipped => TerminalLine.BrushFor(LogLevel.Warning),     // yellow
        _ => MutedBrush                                                         // muted (pending)
    };

    private static readonly Brush MutedBrush = CreateMuted();

    private static Brush CreateMuted()
    {
        var b = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
        b.Freeze();
        return b;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
