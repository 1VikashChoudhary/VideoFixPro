using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoFixPro.Models;

namespace VideoFixPro;

public partial class AudioMuxerWindow : Window
{
    private static readonly Dictionary<string, int> BitratePresets = new()
    {
        ["Small (good compression)"] = 2500,
        ["Balanced (recommended)"] = 4500,
        ["Quality (bigger, better)"] = 8000
    };

    private static readonly (string Name, string Friendly)[] HardwareEncoders =
    {
        ("h264_nvenc", "NVIDIA NVENC H.264"),
        ("hevc_nvenc", "NVIDIA NVENC HEVC"),
        ("h264_qsv", "Intel QSV H.264"),
        ("hevc_qsv", "Intel QSV HEVC"),
        ("h264_amf", "AMD AMF H.264"),
        ("hevc_amf", "AMD AMF HEVC")
    };

    private static readonly string[] PreferredHardwareOrder =
    {
        "h264_nvenc",
        "h264_qsv",
        "h264_amf",
        "hevc_nvenc",
        "hevc_qsv",
        "hevc_amf"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".flv", ".ts", ".m2ts", ".wmv", ".webm", ".m4v"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".m4a", ".mp3", ".wav", ".ogg", ".ac3", ".flac", ".opus", ".wma", ".mka"
    };

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".videofixpro_audio_muxer_state.json");

    public ObservableCollection<AudioMuxTrack> AudioTracks { get; } = new();
    public ObservableCollection<AudioMuxJob> JobQueue { get; } = new();

    private AudioMuxerSettings _settings = new();
    private string? _videoPath;
    private string? _outputPath;
    private string? _lastAutoOutputPath;
    private string? _lastOutputFolder;
    private int _defaultAudioIndex = -1;
    private CancellationTokenSource? _queueCts;
    private Process? _activeProcess;
    private bool _stopRequested;
    private List<string> _workingHardwareEncoders = new();
    private bool _isDetectingHardware;
    private int _lastQueueSuccessCount;
    private int _lastQueueFailedCount;

    public AudioMuxerWindow(string? preloadVideoPath = null)
    {
        InitializeComponent();
        Loaded += (_, _) => UiTextSanitizer.Apply(this);
        DataContext = this;
        BitratePresetBox.ItemsSource = BitratePresets.Keys;
        HardwareEncoderBox.ItemsSource = new[] { "None" };
        HardwareEncoderBox.SelectedIndex = 0;
        ReencodeCheck.Checked += (_, _) => UpdateEncodeUi();
        ReencodeCheck.Unchecked += (_, _) => UpdateEncodeUi();
        HardwareEncoderBox.SelectionChanged += (_, _) => UpdateEncodeUi();

        _settings = LoadSettings();
        CrfBox.Text = _settings.DefaultCrf.ToString(CultureInfo.InvariantCulture);

        UpdateDefaultTrackText();
        if (!string.IsNullOrWhiteSpace(preloadVideoPath) && File.Exists(preloadVideoPath))
        {
            SetCurrentVideo(preloadVideoPath);
        }

        UpdateEncodeUi();
        _ = DetectHardwareEncodersAsync();
    }

    private static AudioMuxerSettings LoadSettings()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                var json = File.ReadAllText(StateFile);
                var loaded = JsonSerializer.Deserialize<AudioMuxerSettings>(json);
                if (loaded != null)
                {
                    return loaded;
                }
            }
        }
        catch { }

        return new AudioMuxerSettings
        {
            FfmpegPath = FindBundledTool("ffmpeg.exe"),
            FfprobePath = FindBundledTool("ffprobe.exe"),
            DefaultCrf = 23
        };
    }

    private static void SaveSettings(AudioMuxerSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StateFile, json);
        }
        catch { }
    }

    private static string FindBundledTool(string fileName)
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var local = Path.Combine(appDir, "ffmpeg", fileName);
        if (File.Exists(local))
        {
            return local;
        }

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoFixPro",
            "ffmpeg",
            fileName);
        return File.Exists(appData) ? appData : fileName;
    }

    private string FfmpegPath => string.IsNullOrWhiteSpace(_settings.FfmpegPath) ? "ffmpeg" : _settings.FfmpegPath;
    private string FfprobePath => string.IsNullOrWhiteSpace(_settings.FfprobePath) ? "ffprobe" : _settings.FfprobePath;

    private void BrowseVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select video file",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.flv;*.ts;*.m2ts|All files|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            SetCurrentVideo(dlg.FileName);
        }
    }

    private void SetCurrentVideo(string path)
    {
        _videoPath = path;
        VideoPathText.Text = path;
        UpdateSuggestedBitrateAsync(path);
        UpdateAutoOutputPath(forceReplace: true);
        MuxerTitleHint.Text = $"Ready to mux audio into {Path.GetFileName(path)}";
        SetStatus("Video selected", "#388BFD");
    }

    private async void UpdateSuggestedBitrateAsync(string path)
    {
        var info = await Task.Run(() => ProbeOverallBitrateAndResolution(path));
        if (info.BitrateKbps.HasValue)
        {
            var suggested = Math.Max(500, (int)(info.BitrateKbps.Value * 0.6));
            Dispatcher.Invoke(() => SuggestedBitrateText.Text = $"Suggested: {suggested}k");
            return;
        }

        var fallback = info.Height switch
        {
            >= 1080 => 4500,
            >= 720 => 2500,
            _ => 1200
        };
        Dispatcher.Invoke(() => SuggestedBitrateText.Text = $"Suggested: {fallback}k");
    }

    private void UpdateAutoOutputPath(bool forceReplace = false)
    {
        if (string.IsNullOrWhiteSpace(_videoPath))
        {
            return;
        }

        var ext = ContainerMkvRadio.IsChecked == true ? ".mkv" : ".mp4";
        var basePath = Path.Combine(
            Path.GetDirectoryName(_videoPath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(_videoPath) + "_multi_audio" + ext);

        if (forceReplace || string.IsNullOrWhiteSpace(_outputPath) || _outputPath == _lastAutoOutputPath)
        {
            _outputPath = basePath;
            _lastAutoOutputPath = basePath;
            OutputPathText.Text = _outputPath;
        }
    }

    private void Container_Changed(object sender, RoutedEventArgs e) => UpdateAutoOutputPath();

    private void AddAudio_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select audio file",
            Filter = "Audio files|*.aac;*.m4a;*.mp3;*.wav;*.ogg;*.ac3;*.flac|All files|*.*"
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        AudioTracks.Add(new AudioMuxTrack
        {
            Path = dlg.FileName,
            Title = Path.GetFileNameWithoutExtension(dlg.FileName)
        });
        UpdateDefaultTrackText();
    }

    private void AddDualPreset_Click(object sender, RoutedEventArgs e)
    {
        var hindi = new OpenFileDialog { Title = "Select Hindi audio", Filter = "Audio files|*.aac;*.m4a;*.mp3;*.wav;*.ogg;*.ac3;*.flac|All files|*.*" };
        if (hindi.ShowDialog() != true) return;
        var english = new OpenFileDialog { Title = "Select English audio", Filter = "Audio files|*.aac;*.m4a;*.mp3;*.wav;*.ogg;*.ac3;*.flac|All files|*.*" };
        if (english.ShowDialog() != true) return;

        AudioTracks.Add(new AudioMuxTrack { Path = hindi.FileName, Title = "Hindi", Language = "hin" });
        AudioTracks.Add(new AudioMuxTrack { Path = english.FileName, Title = "English", Language = "eng" });
        UpdateDefaultTrackText();
    }

    private AudioMuxTrack? SelectedTrack => TrackGrid.SelectedItem as AudioMuxTrack;

    private void RenameTrack_Click(object sender, RoutedEventArgs e)
    {
        var track = SelectedTrack;
        if (track == null) return;
        if (PromptWindow.TryShow(this, "Rename Track", "Audio tag / display title", track.Title, out var value))
        {
            track.Title = value.Trim();
            TrackGrid.Items.Refresh();
            UpdateDefaultTrackText();
        }
    }

    private void SetLanguage_Click(object sender, RoutedEventArgs e)
    {
        var track = SelectedTrack;
        if (track == null) return;
        if (PromptWindow.TryShow(this, "Language Code", "Enter 3-letter ISO code like eng or hin", track.Language, out var value))
        {
            track.Language = value.Trim().ToLowerInvariant();
            TrackGrid.Items.Refresh();
        }
    }

    private void SetDelay_Click(object sender, RoutedEventArgs e)
    {
        var track = SelectedTrack;
        if (track == null) return;
        if (!PromptWindow.TryShow(this, "Delay (seconds)", "Enter seconds. Negative values are allowed.", track.DelaySeconds.ToString("F3", CultureInfo.InvariantCulture), out var value))
        {
            return;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var delay))
        {
            MessageBox.Show(this, "Enter a valid number.", "Invalid delay", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        track.DelaySeconds = delay;
        TrackGrid.Items.Refresh();
    }

    private void MoveTrackUp_Click(object sender, RoutedEventArgs e) => MoveTrack(-1);
    private void MoveTrackDown_Click(object sender, RoutedEventArgs e) => MoveTrack(1);

    private void MoveTrack(int delta)
    {
        var track = SelectedTrack;
        if (track == null) return;
        var index = AudioTracks.IndexOf(track);
        var next = index + delta;
        if (next < 0 || next >= AudioTracks.Count) return;
        AudioTracks.Move(index, next);
        TrackGrid.SelectedItem = track;
        if (_defaultAudioIndex == index) _defaultAudioIndex = next;
        else if (_defaultAudioIndex == next) _defaultAudioIndex = index;
        UpdateDefaultTrackText();
    }

    private void RemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        var track = SelectedTrack;
        if (track == null) return;
        var index = AudioTracks.IndexOf(track);
        AudioTracks.Remove(track);
        if (_defaultAudioIndex == index) _defaultAudioIndex = -1;
        else if (_defaultAudioIndex > index) _defaultAudioIndex--;
        UpdateDefaultTrackText();
    }

    private void ClearTracks_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Clear all appended audio tracks?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        AudioTracks.Clear();
        _defaultAudioIndex = -1;
        UpdateDefaultTrackText();
    }

    private void SetDefaultTrack_Click(object sender, RoutedEventArgs e)
    {
        var track = SelectedTrack;
        if (track == null) return;
        _defaultAudioIndex = AudioTracks.IndexOf(track);
        UpdateDefaultTrackText();
    }

    private void SetOriginalDefault_Click(object sender, RoutedEventArgs e)
    {
        _defaultAudioIndex = -1;
        UpdateDefaultTrackText();
    }

    private void UpdateDefaultTrackText()
    {
        if (KeepOriginalAudioCheck.IsChecked == true && _defaultAudioIndex < 0)
        {
            DefaultTrackText.Text = "Default: Original";
        }
        else if (_defaultAudioIndex >= 0 && _defaultAudioIndex < AudioTracks.Count)
        {
            DefaultTrackText.Text = $"Default: {AudioTracks[_defaultAudioIndex].Title}";
        }
        else
        {
            DefaultTrackText.Text = "Default: First appended track";
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var ext = ContainerMkvRadio.IsChecked == true ? ".mkv" : ".mp4";
        var dlg = new SaveFileDialog
        {
            DefaultExt = ext,
            Filter = "MP4|*.mp4|MKV|*.mkv|All files|*.*",
            FileName = string.IsNullOrWhiteSpace(_outputPath) ? string.Empty : Path.GetFileName(_outputPath)
        };
        if (dlg.ShowDialog() == true)
        {
            _outputPath = dlg.FileName;
            OutputPathText.Text = _outputPath;
            _lastOutputFolder = Path.GetDirectoryName(_outputPath);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AudioMuxerSettingsWindow(_settings)
        {
            Owner = this
        };
        if (dlg.ShowDialog() == true)
        {
            _settings = dlg.Settings;
            SaveSettings(_settings);
            CrfBox.Text = _settings.DefaultCrf.ToString(CultureInfo.InvariantCulture);
            MessageBox.Show(this, "Settings saved. Re-run hardware detection if you changed the FFmpeg path.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            _ = DetectHardwareEncodersAsync();
        }
    }

    private async Task DetectHardwareEncodersAsync()
    {
        if (_isDetectingHardware)
        {
            return;
        }

        _isDetectingHardware = true;
        SetStatus("Detecting hardware encoders...", "#388BFD");
        AppendLog("Detecting hardware encoders using ffmpeg...\n");

        string output;
        try
        {
            output = await RunCaptureAsync(FfmpegPath, "-hide_banner -encoders", 12000);
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to run ffmpeg -encoders: {ex.Message}\n");
            SetStatus("HW detection failed", "#F85149");
            _isDetectingHardware = false;
            return;
        }

        var detected = HardwareEncoders
            .Where(enc => Regex.IsMatch(output, $@"\b{Regex.Escape(enc.Name)}\b", RegexOptions.IgnoreCase))
            .Select(enc => enc.Name)
            .ToList();

        var working = new List<string>();
        foreach (var encoder in detected)
        {
            AppendLog($"Testing {encoder}...\n");
            if (await TestEncoderRuntimeAsync(encoder))
            {
                working.Add(encoder);
                AppendLog($"{encoder} is usable.\n");
            }
            else
            {
                AppendLog($"{encoder} failed runtime test and will be skipped.\n");
            }
        }

        var values = new List<string> { "None" };
        values.AddRange(working);
        _workingHardwareEncoders = working;
        Dispatcher.Invoke(() =>
        {
            HardwareEncoderBox.ItemsSource = values;
            HardwareEncoderBox.SelectedItem = working
                .OrderBy(v => Array.IndexOf(PreferredHardwareOrder, v))
                .FirstOrDefault() ?? "None";
            UpdateEncodeUi();
        });

        AppendLog($"Detected hardware encoders: {(detected.Count == 0 ? "None" : string.Join(", ", detected))}\n");
        AppendLog($"Working hardware encoders: {(working.Count == 0 ? "None" : string.Join(", ", working))}\n");
        SetStatus(
            working.Count == 0
                ? "Fast copy ready. No working hardware encoder found."
                : $"Fast copy ready. Hardware available: {ToFriendlyEncoderName(HardwareEncoderBox.SelectedItem?.ToString())}",
            working.Count == 0 ? "#D29922" : "#3FB950");
        _isDetectingHardware = false;
    }

    private async void DetectHw_Click(object sender, RoutedEventArgs e) => await DetectHardwareEncodersAsync();

    private async void TestEncoder_Click(object sender, RoutedEventArgs e)
    {
        var encoder = HardwareEncoderBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(encoder) || encoder == "None")
        {
            MessageBox.Show(this, "No hardware encoder selected.", "Test encoder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var ok = await TestEncoderRuntimeAsync(encoder);
            if (!ok)
            {
                MessageBox.Show(this, $"Encoder {encoder} is listed by FFmpeg but is not usable right now.", "Test encoder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var output = await RunCaptureAsync(FfmpegPath, $"-hide_banner -h encoder={encoder}", 8000);
            MessageBox.Show(this, $"Encoder {encoder} is working.\n\n{output[..Math.Min(1200, output.Length)]}", "Test encoder", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to test encoder {encoder}.\n\n{ex.Message}", "Test encoder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private AudioMuxJobOptions CaptureCurrentOptions()
    {
        var resolution = ((ComboBoxItem?)ResolutionBox.SelectedItem)?.Content?.ToString() ?? "same";
        if (!int.TryParse(CrfBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var crf))
        {
            crf = _settings.DefaultCrf;
        }

        return new AudioMuxJobOptions
        {
            Container = ContainerMkvRadio.IsChecked == true ? "mkv" : "mp4",
            AudioMode = AudioSafeRadio.IsChecked == true ? "safe" : "fast",
            ReencodeVideo = ReencodeCheck.IsChecked == true,
            HardwareEncoder = HardwareEncoderBox.SelectedItem?.ToString() ?? "None",
            Crf = crf,
            Resolution = resolution,
            MaxBitrate = MaxBitrateBox.Text.Trim(),
            KeepOriginalAudio = KeepOriginalAudioCheck.IsChecked == true,
            DefaultAudioIndex = _defaultAudioIndex
        };
    }

    private AudioMuxJob BuildJob(string inputPath, string outputPath, AudioMuxJobOptions options)
    {
        return new AudioMuxJob
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            AudioTracks = AudioTracks.Select(t => t.Clone()).ToList(),
            Options = options.Clone(),
            StatusText = "Queued"
        };
    }

    private bool PrepareJobForMux(AudioMuxJob job, bool interactive, out string message)
    {
        message = string.Empty;
        var notes = new List<string>();
        var originalAudioCount = ProbeAudioStreamCount(job.InputPath);

        if (job.Options.KeepOriginalAudio && originalAudioCount == null)
        {
            notes.Add("Could not verify original audio streams. Original-audio retention will be skipped unless ffprobe succeeds during processing.");
            job.Options.KeepOriginalAudio = false;
        }

        var incompatibleAudioCodecs = new List<string>();
        if (string.Equals(job.Options.Container, "mp4", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(job.Options.AudioMode, "fast", StringComparison.OrdinalIgnoreCase))
        {
            if (job.Options.KeepOriginalAudio && originalAudioCount.GetValueOrDefault() > 0)
            {
                var sourceAudioCodecs = ProbeAudioCodecNames(job.InputPath, null);
                incompatibleAudioCodecs.AddRange(sourceAudioCodecs.Where(IsIncompatibleMp4AudioCodec));
            }

            foreach (var track in job.AudioTracks)
            {
                var codec = ProbeAudioCodecName(track.Path);
                if (IsIncompatibleMp4AudioCodec(codec))
                {
                    incompatibleAudioCodecs.Add(codec!);
                }
            }

            if (incompatibleAudioCodecs.Count > 0)
            {
                if (interactive)
                {
                    var result = MessageBox.Show(
                        this,
                        "One or more audio tracks are not MP4-friendly for fast stream copy.\n\n" +
                        $"Detected codecs: {string.Join(", ", incompatibleAudioCodecs.Distinct(StringComparer.OrdinalIgnoreCase))}\n\n" +
                        "Switch this job to Safe (AAC re-encode) automatically?",
                        "MP4 audio compatibility",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                    {
                        message = "Job was not added because MP4 fast-copy audio was incompatible.";
                        return false;
                    }
                }

                job.Options.AudioMode = "safe";
                notes.Add("Switched audio mode to Safe (AAC re-encode) for MP4 compatibility.");
            }
        }

        if (!string.IsNullOrWhiteSpace(job.OutputPath))
        {
            var wantedExt = string.Equals(job.Options.Container, "mkv", StringComparison.OrdinalIgnoreCase) ? ".mkv" : ".mp4";
            if (!string.Equals(Path.GetExtension(job.OutputPath), wantedExt, StringComparison.OrdinalIgnoreCase))
            {
                job.OutputPath = Path.ChangeExtension(job.OutputPath, wantedExt);
            }
        }

        message = string.Join("\n", notes);
        return true;
    }

    private void AddCurrentJob_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_videoPath))
        {
            MessageBox.Show(this, "Select a source video first.", "No input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_outputPath))
        {
            BrowseOutput_Click(sender, e);
            if (string.IsNullOrWhiteSpace(_outputPath))
            {
                return;
            }
        }

        if (File.Exists(_outputPath) &&
            MessageBox.Show(this, $"File already exists:\n{_outputPath}\n\nOverwrite?", "Overwrite?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var job = BuildJob(_videoPath!, _outputPath!, CaptureCurrentOptions());
        if (!PrepareJobForMux(job, interactive: true, out var prepMessage))
        {
            if (!string.IsNullOrWhiteSpace(prepMessage))
            {
                MessageBox.Show(this, prepMessage, "Job not added", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        JobQueue.Add(job);
        OpenFolderBtn.IsEnabled = true;
        if (!string.IsNullOrWhiteSpace(prepMessage))
        {
            AppendLog(prepMessage + "\n");
            MessageBox.Show(this, prepMessage, "Job updated", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        SetStatus("Job added to queue", "#388BFD");
    }

    private void AddFilesToQueue_Click(object sender, RoutedEventArgs e)
    {
        var currentOptions = CaptureCurrentOptions();
        var wizard = new BatchMuxWizardWindow(currentOptions.Container == "mkv" ? ".mkv" : ".mp4")
        {
            Owner = this
        };

        if (wizard.ShowDialog() != true)
        {
            return;
        }

        var readyRows = wizard.ReadyRows;
        if (readyRows.Count == 0)
        {
            return;
        }

        var adjustedCount = 0;
        var warnedOriginalCount = 0;
        foreach (var row in readyRows)
        {
            var options = currentOptions.Clone();
            var job = new AudioMuxJob
            {
                InputPath = row.VideoPath,
                OutputPath = row.OutputPath,
                Options = options,
                StatusText = "Queued",
                AudioTracks = new List<AudioMuxTrack>
                {
                    new()
                    {
                        Path = row.AudioPath,
                        Title = string.IsNullOrWhiteSpace(row.Title) ? Path.GetFileNameWithoutExtension(row.AudioPath) : row.Title,
                        Language = row.Language?.Trim().ToLowerInvariant() ?? string.Empty,
                        DelaySeconds = row.DelaySeconds
                    }
                }
            };

            if (!PrepareJobForMux(job, interactive: false, out var prepMessage))
            {
                continue;
            }

            if (prepMessage.Contains("Safe", StringComparison.OrdinalIgnoreCase)) adjustedCount++;
            if (prepMessage.Contains("original audio streams", StringComparison.OrdinalIgnoreCase)) warnedOriginalCount++;
            if (!string.IsNullOrWhiteSpace(prepMessage)) AppendLog($"{Path.GetFileName(row.VideoPath)}: {prepMessage}\n");
            JobQueue.Add(job);
        }
        OpenFolderBtn.IsEnabled = JobQueue.Count > 0;
        if (adjustedCount > 0 || warnedOriginalCount > 0)
        {
            var adjustmentSummary = $"{adjustedCount} job(s) auto-switched to Safe mode, {warnedOriginalCount} job(s) skipped original-audio retention.";
            AppendLog(adjustmentSummary + "\n");
            MessageBox.Show(this, adjustmentSummary, "Queue adjusted", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        SetStatus($"Batch wizard queued {readyRows.Count} video(s)", "#388BFD");
    }

    private AudioMuxJob? SelectedJob => JobGrid.SelectedItem as AudioMuxJob;

    private void RemoveSelectedJob_Click(object sender, RoutedEventArgs e)
    {
        var job = SelectedJob;
        if (job != null) JobQueue.Remove(job);
        OpenFolderBtn.IsEnabled = JobQueue.Count > 0 || !string.IsNullOrWhiteSpace(_lastOutputFolder);
    }

    private void MoveJobUp_Click(object sender, RoutedEventArgs e) => MoveJob(-1);
    private void MoveJobDown_Click(object sender, RoutedEventArgs e) => MoveJob(1);

    private void MoveJob(int delta)
    {
        var job = SelectedJob;
        if (job == null) return;
        var index = JobQueue.IndexOf(job);
        var next = index + delta;
        if (next < 0 || next >= JobQueue.Count) return;
        JobQueue.Move(index, next);
        JobGrid.SelectedItem = job;
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Clear the job queue?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            JobQueue.Clear();
            OpenFolderBtn.IsEnabled = !string.IsNullOrWhiteSpace(_lastOutputFolder);
        }
    }

    private async void StartQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_queueCts != null)
        {
            return;
        }

        if (JobQueue.Count == 0)
        {
            AddCurrentJob_Click(sender, e);
            if (JobQueue.Count == 0) return;
        }

        _stopRequested = false;
        _queueCts = new CancellationTokenSource();
        _lastQueueSuccessCount = 0;
        _lastQueueFailedCount = 0;
        SetStatus("Running queue...", "#388BFD");

        try
        {
            await Task.Run(() => WorkerQueue(_queueCts.Token));
        }
        finally
        {
            _queueCts.Dispose();
            _queueCts = null;
            _activeProcess = null;
        }
    }

    private void StopQueue_Click(object sender, RoutedEventArgs e)
    {
        _stopRequested = true;
        _queueCts?.Cancel();
        try { _activeProcess?.Kill(); } catch { }
        AppendLog("\nStop requested. Attempting to terminate ffmpeg...\n");
        SetStatus("Stopping...", "#D29922");
    }

    private void WorkerQueue(CancellationToken ct)
    {
        while (true)
        {
            AudioMuxJob? job = null;
            Dispatcher.Invoke(() =>
            {
                job = JobQueue.FirstOrDefault(j => string.Equals(j.StatusText, "Queued", StringComparison.OrdinalIgnoreCase));
            });

            if (job == null || _stopRequested)
            {
                break;
            }

            AppendLog($"\n=== Running job: {job.InputPath} ===\n");
            try
            {
                Dispatcher.Invoke(() =>
                {
                    job.StatusText = "Running";
                    job.ErrorText = string.Empty;
                    JobGrid.Items.Refresh();
                });
                RunSingleJob(job, ct);
                if (!_stopRequested)
                {
                    _lastQueueSuccessCount++;
                    Dispatcher.Invoke(() =>
                    {
                        job.StatusText = "Done";
                        JobQueue.Remove(job);
                    });
                }
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    job.StatusText = "Stopped";
                    JobGrid.Items.Refresh();
                });
                AppendLog("\nOperation stopped by user.\n");
                break;
            }
            catch (Exception ex)
            {
                _lastQueueFailedCount++;
                AppendLog($"\nJob failed: {ex.Message}\n");
                Dispatcher.Invoke(() =>
                {
                    job.StatusText = "Failed";
                    job.ErrorText = ex.Message;
                    JobGrid.Items.Refresh();
                });
            }
        }

        AppendLog("\nQueue finished.\n");
        Dispatcher.Invoke(() =>
        {
            if (!_stopRequested)
            {
                ShowCompletionNotification(
                    "Audio muxer queue complete",
                    $"Processed {_lastQueueSuccessCount + _lastQueueFailedCount} job(s). {_lastQueueSuccessCount} succeeded, {_lastQueueFailedCount} failed.",
                    _lastQueueFailedCount != 0);
                SetStatus(
                    _lastQueueFailedCount == 0
                        ? $"Idle - {_lastQueueSuccessCount} job(s) succeeded"
                        : $"Idle - {_lastQueueSuccessCount} succeeded, {_lastQueueFailedCount} failed",
                    _lastQueueFailedCount == 0 ? "#3FB950" : "#D29922");
            }
            else
            {
                SetStatus("Stopped", "#D29922");
            }
        });
    }

    private void RunSingleJob(AudioMuxJob job, CancellationToken ct)
    {
        var duration = ProbeDuration(job.InputPath);
        var originalAudioCount = ProbeAudioStreamCount(job.InputPath);
        var bitrateInfo = ProbeOverallBitrateAndResolution(job.InputPath);
        var suggested = bitrateInfo.BitrateKbps.HasValue
            ? Math.Max(500, (int)(bitrateInfo.BitrateKbps.Value * 0.6))
            : bitrateInfo.Height switch
            {
                >= 1080 => 4500,
                >= 720 => 2500,
                _ => 1200
            };

        Dispatcher.Invoke(() => SuggestedBitrateText.Text = $"Suggested: {suggested}k");
        if (job.Options.KeepOriginalAudio && originalAudioCount == null)
        {
            AppendLog("Warning: Could not verify original audio streams for this file. Original-audio retention will be skipped.\n");
        }

        var (command, hardwareActive) = BuildFfmpegCommand(job.InputPath, job.OutputPath, job.AudioTracks, originalAudioCount ?? 0, job.Options, suggested, false);
        var result = RunFfmpegProcess(command, duration, ct);

        if (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        if (!result.Success && hardwareActive)
        {
            AppendLog("\nSelected hardware encoder failed. Retrying automatically with software encoding.\n");
            var (fallbackCommand, _) = BuildFfmpegCommand(job.InputPath, job.OutputPath, job.AudioTracks, originalAudioCount ?? 0, job.Options, suggested, true);
            result = RunFfmpegProcess(fallbackCommand, duration, ct);
        }

        if (!result.Success)
        {
            throw new InvalidOperationException($"FFmpeg exited with code {result.ExitCode}.");
        }

        Dispatcher.Invoke(() =>
        {
            _lastOutputFolder = Path.GetDirectoryName(job.OutputPath);
            OpenFolderBtn.IsEnabled = !string.IsNullOrWhiteSpace(_lastOutputFolder);
            SetProgress(100, "100%");
            SetStatus($"Completed: {Path.GetFileName(job.OutputPath)}", "#3FB950");
            ShowCompletionNotification("Audio mux complete", Path.GetFileName(job.OutputPath));
        });
    }

    private void HandleFfmpegLogLine(string line, double? duration)
    {
        AppendLog(line + "\n");
        var stats = ParseFfmpegStats(line);
        Dispatcher.Invoke(() =>
        {
            if (stats.Fps != null) StatFpsText.Text = $"FPS: {stats.Fps.Value:F1}";
            if (!string.IsNullOrWhiteSpace(stats.Speed)) StatSpeedText.Text = $"Speed: {stats.Speed}";
            if (!string.IsNullOrWhiteSpace(stats.Bitrate)) StatBitrateText.Text = $"Bitrate: {stats.Bitrate}";
            StatGpuText.Text = $"GPU: {TryNvidiaSmi() ?? "N/A"}";
        });

        var seconds = ParseFfmpegTime(line);
        if (seconds.HasValue && duration.HasValue && duration.Value > 0)
        {
            var percent = Math.Min(99.9, seconds.Value / duration.Value * 100.0);
            Dispatcher.Invoke(() => SetProgress(percent, $"{percent:F1}%"));
        }
    }

    private async void Sample10_Click(object sender, RoutedEventArgs e) => await RunSampleEncodeAsync(10);
    private async void Sample5_Click(object sender, RoutedEventArgs e) => await RunSampleEncodeAsync(5);

    private async Task RunSampleEncodeAsync(int seconds)
    {
        if (string.IsNullOrWhiteSpace(_videoPath))
        {
            MessageBox.Show(this, "Select a source video first.", "No input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var tempDir = Directory.CreateTempSubdirectory("muxer_sample_");
        try
        {
            var tempOut = Path.Combine(tempDir.FullName, Path.GetFileNameWithoutExtension(_videoPath) + $"_sample_{seconds}s.mp4");
            var duration = ProbeDuration(_videoPath) ?? seconds;
            var originalAudioCount = ProbeAudioStreamCount(_videoPath) ?? 0;
            var bitrateInfo = ProbeOverallBitrateAndResolution(_videoPath);
            var suggested = bitrateInfo.BitrateKbps.HasValue ? Math.Max(500, (int)(bitrateInfo.BitrateKbps.Value * 0.6)) : 2500;

            var options = CaptureCurrentOptions();
            options.Container = "mp4";
            var (command, _) = BuildFfmpegCommand(_videoPath, tempOut, AudioTracks.ToList(), originalAudioCount, options, suggested, false);
            command.Insert(command.Count - 1, seconds.ToString(CultureInfo.InvariantCulture));
            command.Insert(command.Count - 1, "-t");

            AppendLog("\nRunning sample encode:\n" + string.Join(" ", command.Select(QuoteArg)) + "\n\n");

            var psi = new ProcessStartInfo
            {
                FileName = command[0],
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in command.Skip(1))
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            ProcessGuard.Watch(proc);
            while (!proc.StandardOutput.EndOfStream)
            {
                var line = await proc.StandardOutput.ReadLineAsync();
                if (line != null) AppendLog(line + "\n");
            }
            while (!proc.StandardError.EndOfStream)
            {
                var line = await proc.StandardError.ReadLineAsync();
                if (line != null) AppendLog(line + "\n");
            }
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                MessageBox.Show(this, "Sample encode failed. See log for details.", "Sample failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var sizeMb = File.Exists(tempOut) ? new FileInfo(tempOut).Length / 1024d / 1024d : 0;
            if (MessageBox.Show(this, $"Sample encode finished.\n\nFile: {tempOut}\nSize: {sizeMb:F2} MB\n\nMove it to a permanent location?", "Sample complete", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            {
                return;
            }

            var save = new SaveFileDialog
            {
                DefaultExt = ".mp4",
                Filter = "MP4|*.mp4|All files|*.*",
                FileName = Path.GetFileName(tempOut)
            };
            if (save.ShowDialog() == true)
            {
                File.Copy(tempOut, save.FileName, true);
                MessageBox.Show(this, $"Sample saved to:\n{save.FileName}", "Sample saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            try { tempDir.Delete(true); } catch { }
        }
    }

    private void BitratePresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var key = BitratePresetBox.SelectedItem?.ToString();
        if (key != null && BitratePresets.TryGetValue(key, out var value))
        {
            MaxBitrateBox.Text = value + "k";
        }
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = _lastOutputFolder;
        if (SelectedJob != null)
        {
            folder = Path.GetDirectoryName(SelectedJob.OutputPath);
        }

        if (string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(_outputPath))
        {
            folder = Path.GetDirectoryName(_outputPath);
        }

        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            Process.Start("explorer.exe", folder);
        }
    }

    private (List<string> Command, bool HardwareActive) BuildFfmpegCommand(
        string inputFile,
        string outputFile,
        IList<AudioMuxTrack> audioTracks,
        int originalAudioCount,
        AudioMuxJobOptions options,
        int? suggestedBitrateK,
        bool forceSoftware)
    {
        var cmd = new List<string> { FfmpegPath, "-y", "-hide_banner", "-loglevel", "info", "-i", inputFile };
        foreach (var track in audioTracks)
        {
            if (track.DelaySeconds != 0)
            {
                cmd.Add("-itsoffset");
                cmd.Add(track.DelaySeconds.ToString(CultureInfo.InvariantCulture));
            }

            cmd.Add("-i");
            cmd.Add(track.Path);
        }

        cmd.AddRange(new[] { "-map", "0:v:0" });
        if (options.KeepOriginalAudio)
        {
            for (var i = 0; i < originalAudioCount; i++)
            {
                cmd.Add("-map");
                cmd.Add($"0:a:{i}");
            }
        }

        for (var i = 0; i < audioTracks.Count; i++)
        {
            cmd.Add("-map");
            cmd.Add($"{i + 1}:a:0");
        }

        var encoder = forceSoftware ? "None" : options.HardwareEncoder;
        var hardwareActive = options.ReencodeVideo && !string.IsNullOrWhiteSpace(encoder) && encoder != "None";
        var suggestedBitrate = suggestedBitrateK.HasValue ? $"{suggestedBitrateK.Value}k" : null;

        if (!options.ReencodeVideo)
        {
            cmd.AddRange(new[] { "-c:v", "copy" });
        }
        else if (hardwareActive)
        {
            cmd.AddRange(new[] { "-c:v", encoder! });
            if (!string.Equals(options.Resolution, "same", StringComparison.OrdinalIgnoreCase) &&
                options.Resolution.Contains('x', StringComparison.OrdinalIgnoreCase))
            {
                cmd.AddRange(new[] { "-vf", $"scale={options.Resolution.Replace('x', ':')}" });
            }
            if (!string.IsNullOrWhiteSpace(options.MaxBitrate))
            {
                cmd.AddRange(new[] { "-b:v", options.MaxBitrate });
            }
            else if (!string.IsNullOrWhiteSpace(suggestedBitrate))
            {
                cmd.AddRange(new[] { "-b:v", suggestedBitrate });
            }
        }
        else
        {
            cmd.AddRange(new[] { "-c:v", "libx264", "-crf", options.Crf.ToString(CultureInfo.InvariantCulture) });
            if (!string.Equals(options.Resolution, "same", StringComparison.OrdinalIgnoreCase) &&
                options.Resolution.Contains('x', StringComparison.OrdinalIgnoreCase))
            {
                cmd.AddRange(new[] { "-vf", $"scale={options.Resolution.Replace('x', ':')}" });
            }
        }

        cmd.AddRange(new[] { "-c:a", options.AudioMode == "safe" ? "aac" : "copy" });

        var outIndex = options.KeepOriginalAudio ? originalAudioCount : 0;
        for (var i = 0; i < audioTracks.Count; i++)
        {
            var title = string.IsNullOrWhiteSpace(audioTracks[i].Title) ? $"Track{i + 1}" : audioTracks[i].Title;
            cmd.Add($"-metadata:s:a:{outIndex + i}");
            cmd.Add($"title={title}");
            if (!string.IsNullOrWhiteSpace(audioTracks[i].Language))
            {
                cmd.Add($"-metadata:s:a:{outIndex + i}");
                cmd.Add($"language={audioTracks[i].Language}");
            }
        }

        var totalAudio = (options.KeepOriginalAudio ? originalAudioCount : 0) + audioTracks.Count;
        if (totalAudio > 0)
        {
            int defaultIndex;
            if (options.DefaultAudioIndex < 0 && options.KeepOriginalAudio)
            {
                defaultIndex = 0;
            }
            else if (options.KeepOriginalAudio)
            {
                defaultIndex = originalAudioCount + Math.Max(0, options.DefaultAudioIndex);
            }
            else
            {
                defaultIndex = Math.Max(0, options.DefaultAudioIndex);
            }

            for (var i = 0; i < totalAudio; i++)
            {
                cmd.Add($"-disposition:a:{i}");
                cmd.Add(i == defaultIndex ? "default" : "0");
            }
        }

        cmd.Add(outputFile);
        return (cmd, hardwareActive);
    }

    private (bool Success, int ExitCode) RunFfmpegProcess(List<string> command, double? duration, CancellationToken ct)
    {
        AppendLog("\nRunning FFmpeg:\n" + string.Join(" ", command.Select(QuoteArg)) + "\n\n");

        var psi = new ProcessStartInfo
        {
            FileName = command[0],
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in command.Skip(1))
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        ProcessGuard.Watch(proc);
        _activeProcess = proc;

        var readError = Task.Run(async () =>
        {
            while (!proc.StandardError.EndOfStream)
            {
                var line = await proc.StandardError.ReadLineAsync();
                if (line == null) break;
                HandleFfmpegLogLine(line, duration);
            }
        }, ct);

        while (!proc.StandardOutput.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = proc.StandardOutput.ReadLine();
            if (line == null) break;
            HandleFfmpegLogLine(line, duration);
        }

        readError.Wait(ct);
        proc.WaitForExit();
        _activeProcess = null;
        return (proc.ExitCode == 0, proc.ExitCode);
    }

    private static string QuoteArg(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static double? ParseFfmpegTime(string line)
    {
        var match = Regex.Match(line, @"time=(\d+):(\d+):([\d\.]+)");
        if (match.Success)
        {
            return int.Parse(match.Groups[1].Value) * 3600
                 + int.Parse(match.Groups[2].Value) * 60
                 + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        var shortMatch = Regex.Match(line, @"time=(\d+):([\d\.]+)\b");
        if (shortMatch.Success)
        {
            return int.Parse(shortMatch.Groups[1].Value) * 60
                 + double.Parse(shortMatch.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static (double? Fps, string? Speed, string? Bitrate) ParseFfmpegStats(string line)
    {
        double? fps = null;
        string? speed = null;
        string? bitrate = null;

        var fpsMatch = Regex.Match(line, @"fps=\s*([\d\.]+)");
        if (fpsMatch.Success) fps = double.Parse(fpsMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        var speedMatch = Regex.Match(line, @"speed=\s*([\d\.x]+)");
        if (speedMatch.Success) speed = speedMatch.Groups[1].Value;
        var bitrateMatch = Regex.Match(line, @"bitrate=\s*([^\s]+)");
        if (bitrateMatch.Success) bitrate = bitrateMatch.Groups[1].Value;

        return (fps, speed, bitrate);
    }

    private static string? TryNvidiaSmi()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=utilization.gpu,memory.used --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (p == null) return null;
            var output = p.StandardOutput.ReadLine();
            p.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> RunCaptureAsync(string fileName, string arguments, int timeoutMs)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        proc.Start();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(); } catch { }
            throw new TimeoutException("The process timed out.");
        }

        return (await outTask) + "\n" + (await errTask);
    }

    private double? ProbeDuration(string path)
    {
        try
        {
            var output = RunCaptureAsync(FfprobePath, $"-v error -select_streams v:0 -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"", 10000).GetAwaiter().GetResult();
            var first = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private int? ProbeAudioStreamCount(string path)
    {
        try
        {
            var output = RunCaptureAsync(FfprobePath, $"-v error -select_streams a -show_entries stream=index -of csv=p=0 \"{path}\"", 10000).GetAwaiter().GetResult();
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }
        catch
        {
            return null;
        }
    }

    private string? ProbeAudioCodecName(string path)
    {
        try
        {
            var output = RunCaptureAsync(FfprobePath, $"-v error -select_streams a:0 -show_entries stream=codec_name -of default=noprint_wrappers=1:nokey=1 \"{path}\"", 10000).GetAwaiter().GetResult();
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private List<string> ProbeAudioCodecNames(string path, int? expectedCount)
    {
        try
        {
            var output = RunCaptureAsync(FfprobePath, $"-v error -select_streams a -show_entries stream=codec_name -of csv=p=0 \"{path}\"", 10000).GetAwaiter().GetResult();
            var codecs = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToLowerInvariant())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (expectedCount.HasValue && codecs.Count > expectedCount.Value)
            {
                return codecs.Take(expectedCount.Value).ToList();
            }

            return codecs;
        }
        catch
        {
            return new List<string>();
        }
    }

    private static bool IsIncompatibleMp4AudioCodec(string? codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName))
        {
            return false;
        }

        return codecName is "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_u8" or "flac" or "vorbis" or "opus";
    }

    private (int? BitrateKbps, int? Width, int? Height) ProbeOverallBitrateAndResolution(string path)
    {
        int? bitrateKbps = null;
        int? width = null;
        int? height = null;

        try
        {
            var output = RunCaptureAsync(FfprobePath, $"-v error -show_entries format=bit_rate -of default=noprint_wrappers=1:nokey=1 \"{path}\"", 10000).GetAwaiter().GetResult();
            var first = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (int.TryParse(first, out var bitrate))
            {
                bitrateKbps = (int)Math.Round(bitrate / 1000d);
            }
        }
        catch { }

        try
        {
            var output = RunCaptureAsync(FfprobePath, $"-v error -select_streams v:0 -show_entries stream=width,height -of default=noprint_wrappers=1:nokey=1 \"{path}\"", 10000).GetAwaiter().GetResult();
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 2)
            {
                if (int.TryParse(lines[0], out var parsedWidth)) width = parsedWidth;
                if (int.TryParse(lines[1], out var parsedHeight)) height = parsedHeight;
            }
        }
        catch { }

        return (bitrateKbps, width, height);
    }

    private void AppendLog(string text)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(text);
            LogBox.ScrollToEnd();
        });
    }

    private void SetProgress(double percent, string label)
    {
        Dispatcher.Invoke(() =>
        {
            MuxProgressBar.Value = Math.Clamp(percent, 0, 100);
            MuxProgressText.Text = UiTextSanitizer.Normalize(label);
        });
    }

    private void SetStatus(string text, string colorHex)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = UiTextSanitizer.Normalize(text);
            StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
            Title = $"Multi Audio Muxer - {UiTextSanitizer.Normalize(text)}";
        });
    }

    private void ShowCompletionNotification(string title, string text, bool warning = false)
    {
        DesktopNotification.Show(title, text, warning);
    }

    private async Task<bool> TestEncoderRuntimeAsync(string encoder)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FfmpegPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            foreach (var arg in new[]
            {
                "-hide_banner", "-f", "lavfi", "-i", "color=c=black:s=128x128:r=1", "-frames:v", "1", "-an", "-c:v", encoder, "-f", "null", "-"
            })
            {
                proc.StartInfo.ArgumentList.Add(arg);
            }

            proc.Start();
            var stdOut = proc.StandardOutput.ReadToEndAsync();
            var stdErr = proc.StandardError.ReadToEndAsync();
            var completed = await Task.Run(() => proc.WaitForExit(12000));
            var output = (await stdOut) + "\n" + (await stdErr);
            return completed && proc.ExitCode == 0 && !output.Contains("Cannot load nvcuda.dll", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string ToFriendlyEncoderName(string? encoder)
    {
        if (string.IsNullOrWhiteSpace(encoder) || encoder == "None")
        {
            return "software encode";
        }

        return HardwareEncoders.FirstOrDefault(x => x.Name == encoder).Friendly ?? encoder;
    }

    private void UpdateEncodeUi()
    {
        var reencode = ReencodeCheck.IsChecked == true;
        HardwareEncoderBox.IsEnabled = reencode && _workingHardwareEncoders.Count > 0;
        CrfBox.IsEnabled = reencode;
        ResolutionBox.IsEnabled = reencode;
        MaxBitrateBox.IsEnabled = reencode;
        BitratePresetBox.IsEnabled = reencode;
        SuggestedBitrateText.Visibility = reencode ? Visibility.Visible : Visibility.Collapsed;

        if (!reencode)
        {
            HardwareEncoderBox.SelectedItem = "None";
            SetStatus("Fast copy mode ready", "#3FB950");
        }
        else if (_workingHardwareEncoders.Count == 0)
        {
            HardwareEncoderBox.SelectedItem = "None";
            SetStatus("Software re-encode active", "#D29922");
        }
        else if (HardwareEncoderBox.SelectedItem?.ToString() is string encoder && encoder != "None")
        {
            SetStatus($"Video re-encode enabled: {ToFriendlyEncoderName(encoder)}", "#3FB950");
        }
        else
        {
            SetStatus("Video re-encode enabled: software", "#D29922");
        }
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        StatusDot.Fill = (Brush)FindResource("AccentBrush");
        MuxerTitleHint.Text = "Release to add video/audio files to the muxer";
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        ResetDropHint();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        ResetDropHint();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var paths = ((string[])e.Data.GetData(DataFormats.FileDrop)!)
            .Where(File.Exists)
            .ToList();

        var videos = paths.Where(p => VideoExtensions.Contains(Path.GetExtension(p))).ToList();
        var audios = paths.Where(p => AudioExtensions.Contains(Path.GetExtension(p))).ToList();

        if (videos.Count > 0)
        {
            SetCurrentVideo(videos[0]);
        }

        foreach (var audio in audios)
        {
            AudioTracks.Add(new AudioMuxTrack
            {
                Path = audio,
                Title = Path.GetFileNameWithoutExtension(audio)
            });
        }

        if (videos.Count > 1)
        {
            var options = CaptureCurrentOptions();
            var ext = options.Container == "mkv" ? ".mkv" : ".mp4";
            foreach (var extraVideo in videos.Skip(1))
            {
                JobQueue.Add(new AudioMuxJob
                {
                    InputPath = extraVideo,
                    OutputPath = Path.Combine(Path.GetDirectoryName(extraVideo) ?? string.Empty, Path.GetFileNameWithoutExtension(extraVideo) + "_multi_audio" + ext),
                    AudioTracks = AudioTracks.Select(t => t.Clone()).ToList(),
                    Options = options.Clone()
                });
            }
        }

        if (videos.Count == 0 && audios.Count == 0)
        {
            MessageBox.Show(this, "Drop video or audio files here.", "Unsupported files", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        UpdateDefaultTrackText();
        OpenFolderBtn.IsEnabled = JobQueue.Count > 0 || !string.IsNullOrWhiteSpace(_lastOutputFolder);
        SetStatus($"Added {videos.Count} video(s) and {audios.Count} audio track(s)", "#388BFD");
    }

    private void ResetDropHint()
    {
        MuxerTitleHint.Text = string.IsNullOrWhiteSpace(_videoPath)
            ? "Drop video/audio files anywhere, then mux, queue, sample, and export"
            : $"Ready to mux audio into {Path.GetFileName(_videoPath)}";
    }
}
