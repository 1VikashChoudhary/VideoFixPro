using System.Globalization;

namespace VideoFixPro.Models;

/// <summary>
/// Tracks accumulated edit operations for the Video Toolbox.
/// Builds FFmpeg -vf and -af filter chains from the current state.
/// </summary>
public class VideoToolboxSettings
{
    // ── Rotation & Flip ──
    public int RotationDegrees { get; set; }      // 0, 90, 180, 270
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }

    // ── Audio ──
    public double VolumeAdjustmentDb { get; set; } // cumulative dB adjustment
    public bool MuteAudio { get; set; }
    public bool ConvertToMono { get; set; }

    // ── Crop (pixels) ──
    public int CropTop { get; set; }
    public int CropBottom { get; set; }
    public int CropLeft { get; set; }
    public int CropRight { get; set; }

    // ── Aspect Ratio ──
    public string AspectRatio { get; set; } = string.Empty; // e.g. "16:9", "9:16", "4:3", "1:1", or empty for original

    // ── Source video dimensions (set after probe) ──
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }

    public bool HasVideoFilters =>
        RotationDegrees != 0 || FlipHorizontal || FlipVertical ||
        CropTop > 0 || CropBottom > 0 || CropLeft > 0 || CropRight > 0 ||
        !string.IsNullOrEmpty(AspectRatio);

    public bool HasAudioFilters =>
        VolumeAdjustmentDb != 0 || ConvertToMono;

    public bool HasAnyEdits =>
        HasVideoFilters || HasAudioFilters || MuteAudio;

    /// <summary>
    /// Builds the -vf filter chain string for FFmpeg.
    /// Returns null if no video filters are needed.
    /// </summary>
    public string? BuildVideoFilterChain()
    {
        var filters = new List<string>();

        // Crop (applied first, before rotation)
        if (CropTop > 0 || CropBottom > 0 || CropLeft > 0 || CropRight > 0)
        {
            int w = SourceWidth - CropLeft - CropRight;
            int h = SourceHeight - CropTop - CropBottom;
            
            // Ensure crop width and height are even to prevent encoder errors
            w = (w / 2) * 2;
            h = (h / 2) * 2;
            int x = (CropLeft / 2) * 2;
            int y = (CropTop / 2) * 2;

            if (w > 0 && h > 0)
                filters.Add($"crop={w}:{h}:{x}:{y}");
        }

        // Rotation
        switch (RotationDegrees)
        {
            case 90:
                filters.Add("transpose=1");
                break;
            case 180:
                filters.Add("transpose=1,transpose=1");
                break;
            case 270:
                filters.Add("transpose=2");
                break;
        }

        // Flip
        if (FlipHorizontal) filters.Add("hflip");
        if (FlipVertical) filters.Add("vflip");

        // Aspect ratio scaling/padding with guaranteed even dimensions
        if (!string.IsNullOrEmpty(AspectRatio))
        {
            var parts = AspectRatio.Split(':');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int aw) &&
                int.TryParse(parts[1], out int ah) &&
                aw > 0 && ah > 0)
            {
                // Pad to target aspect ratio while ensuring even dimensions on both axes
                filters.Add($"pad=w='ceil(max(iw,ih*{aw}/{ah})/2)*2':h='ceil(max(ih,iw*{ah}/{aw})/2)*2':x='(ow-iw)/2':y='(oh-ih)/2':color=black,setsar=1");
            }
        }

        // Safety: ensure output dimensions are always even numbers for yuv420p / H.264 / HEVC encoders
        if (filters.Count > 0)
        {
            filters.Add("pad=ceil(iw/2)*2:ceil(ih/2)*2:(ow-iw)/2:(oh-ih)/2");
            return string.Join(",", filters);
        }

        return null;
    }

    /// <summary>
    /// Builds the -af filter chain string for FFmpeg.
    /// Returns null if no audio filters are needed (or audio is muted).
    /// </summary>
    public string? BuildAudioFilterChain()
    {
        if (MuteAudio) return null; // handled via -an flag

        var filters = new List<string>();

        if (VolumeAdjustmentDb != 0)
        {
            filters.Add($"volume={VolumeAdjustmentDb.ToString("F1", CultureInfo.InvariantCulture)}dB");
        }

        if (ConvertToMono)
        {
            // Standard channel layout filter that safely downmixes any layout (stereo, 5.1, 7.1)
            filters.Add("aformat=channel_layouts=mono");
        }

        return filters.Count > 0 ? string.Join(",", filters) : null;
    }

    /// <summary>
    /// Returns a human-readable summary of all active edits.
    /// </summary>
    public string GetEditSummary()
    {
        var parts = new List<string>();

        if (RotationDegrees != 0) parts.Add($"Rotate {RotationDegrees}°");
        if (FlipHorizontal) parts.Add("Flip H");
        if (FlipVertical) parts.Add("Flip V");
        if (VolumeAdjustmentDb != 0) parts.Add($"Vol {(VolumeAdjustmentDb > 0 ? "+" : "")}{VolumeAdjustmentDb:F0}dB");
        if (MuteAudio) parts.Add("Muted");
        if (ConvertToMono) parts.Add("Mono");
        if (CropTop > 0 || CropBottom > 0 || CropLeft > 0 || CropRight > 0)
            parts.Add($"Crop T{CropTop}/B{CropBottom}/L{CropLeft}/R{CropRight}");
        if (!string.IsNullOrEmpty(AspectRatio)) parts.Add($"AR {AspectRatio}");

        return parts.Count > 0 ? string.Join(" | ", parts) : "No edits";
    }

    /// <summary>
    /// Resets all settings to defaults.
    /// </summary>
    public void Reset()
    {
        RotationDegrees = 0;
        FlipHorizontal = false;
        FlipVertical = false;
        VolumeAdjustmentDb = 0;
        MuteAudio = false;
        ConvertToMono = false;
        CropTop = CropBottom = CropLeft = CropRight = 0;
        AspectRatio = string.Empty;
    }
}
