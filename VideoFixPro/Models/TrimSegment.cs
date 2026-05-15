using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VideoFixPro.Models;

/// <summary>
/// A single trim segment (start → end), used for both single-trim and multi-segment concat.
/// </summary>
public class TrimSegment : INotifyPropertyChanged
{
    // ── Backing fields ───────────────────────────────────────────
    private int    _index = 1;
    private double _startSeconds;
    private double _endSeconds;

    // ── Properties ───────────────────────────────────────────────
    public int Index
    {
        get => _index;
        set { _index = value; OnPropertyChanged(); OnPropertyChanged(nameof(Label)); }
    }

    public double StartSeconds
    {
        get => _startSeconds;
        set
        {
            _startSeconds = Math.Max(0, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(StartFormatted));
            OnPropertyChanged(nameof(DurationFormatted));
            OnPropertyChanged(nameof(DurationSeconds));
        }
    }

    public double EndSeconds
    {
        get => _endSeconds;
        set
        {
            _endSeconds = Math.Max(0, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(EndFormatted));
            OnPropertyChanged(nameof(DurationFormatted));
            OnPropertyChanged(nameof(DurationSeconds));
        }
    }

    // ── Derived / display ─────────────────────────────────────────
    public string Label           => $"Segment {Index}";
    public string StartFormatted  => FormatTime(StartSeconds);
    public string EndFormatted    => FormatTime(EndSeconds);
    public string DurationFormatted => FormatTime(DurationSeconds);
    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);

    // ── Formatting helper ─────────────────────────────────────────
    public static string FormatTime(double totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100}";
    }

    /// <summary>Parse "mm:ss.f", "hh:mm:ss.f", or bare seconds. Returns -1 on failure.</summary>
    public static double ParseTime(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return -1;
        input = input.Trim();

        // bare number → seconds
        if (double.TryParse(input, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double bare))
            return bare;

        // split on colon
        var colonParts = input.Split(':');
        if (colonParts.Length == 2) // mm:ss.f
        {
            if (int.TryParse(colonParts[0], out int mm) &&
                double.TryParse(colonParts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double ss))
                return mm * 60 + ss;
        }
        else if (colonParts.Length == 3) // hh:mm:ss.f
        {
            if (int.TryParse(colonParts[0], out int hh) &&
                int.TryParse(colonParts[1], out int mm2) &&
                double.TryParse(colonParts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double ss2))
                return hh * 3600 + mm2 * 60 + ss2;
        }

        return -1;
    }

    // ── INotifyPropertyChanged ─────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
