namespace VideoFixPro.Models;

public class AudioMuxTrack
{
    public string Path { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public double DelaySeconds { get; set; }

    public AudioMuxTrack Clone() => new()
    {
        Path = Path,
        Title = Title,
        Language = Language,
        DelaySeconds = DelaySeconds
    };
}
