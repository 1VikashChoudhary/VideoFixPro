namespace VideoFixPro.Models;

public class AudioMuxJob
{
    public string InputPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public List<AudioMuxTrack> AudioTracks { get; set; } = new();
    public AudioMuxJobOptions Options { get; set; } = new();
    public string StatusText { get; set; } = "Queued";
    public string ErrorText { get; set; } = string.Empty;
}

public class AudioMuxJobOptions
{
    public string Container { get; set; } = "mp4";
    public string AudioMode { get; set; } = "fast";
    public bool ReencodeVideo { get; set; } = true;
    public string HardwareEncoder { get; set; } = "None";
    public int Crf { get; set; } = 23;
    public string Resolution { get; set; } = "same";
    public string MaxBitrate { get; set; } = string.Empty;
    public bool KeepOriginalAudio { get; set; } = true;
    public int DefaultAudioIndex { get; set; } = -1;

    public AudioMuxJobOptions Clone() => new()
    {
        Container = Container,
        AudioMode = AudioMode,
        ReencodeVideo = ReencodeVideo,
        HardwareEncoder = HardwareEncoder,
        Crf = Crf,
        Resolution = Resolution,
        MaxBitrate = MaxBitrate,
        KeepOriginalAudio = KeepOriginalAudio,
        DefaultAudioIndex = DefaultAudioIndex
    };
}

public class AudioMuxerSettings
{
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public int DefaultCrf { get; set; } = 23;
    public string Theme { get; set; } = "dark";
}
