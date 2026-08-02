using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoFixPro.Models;

namespace VideoFixPro;

public partial class AudioExtractorWindow : Window
{
    private readonly ObservableCollection<AudioExtractorJob> _queue = new();
    private CancellationTokenSource? _cts;
    private Process? _ffmpegProcess;
    private bool _isRunning;
    private string _customOutputFolder = string.Empty;
    private string _lastOutputFolder = string.Empty;

    private static string AppDir => AppDomain.CurrentDomain.BaseDirectory;
    private static string FFmpeg => GetBinPath("ffmpeg.exe");
    private static string FFprobe => GetBinPath("ffprobe.exe");

    private static string GetBinPath(string name)
    {
        var appBin = Path.Combine(AppDir, "ffmpeg", name);
        if (File.Exists(appBin)) return appBin;
        var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoFixPro", "ffmpeg", name);
        return File.Exists(localData) ? localData : appBin;
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".m2ts" };

    public AudioExtractorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UiTextSanitizer.Apply(this);
        JobGrid.DataContext = _queue;
        JobGrid.ItemsSource = _queue;
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) { }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        foreach (var p in paths) ProcessPath(p);
    }

    private void ProcessPath(string path)
    {
        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                                 .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
                                 .ToArray();
            foreach (var f in files) AddFileToQueue(f);
        }
        else if (File.Exists(path) && VideoExtensions.Contains(Path.GetExtension(path)))
        {
            AddFileToQueue(path);
        }
    }

    private void AddVideos_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Video Files",
            Multiselect = true,
            Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.ts;*.m2ts|All Files|*.*"
        };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FileNames) AddFileToQueue(f);
    }

    private void AddFileToQueue(string path)
    {
        if (_queue.Any(j => j.InputPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"[SKIP] Already in queue: {Path.GetFileName(path)}");
            return;
        }

        var job = new AudioExtractorJob { InputPath = path };
        _queue.Add(job);
        Log($"[ADD] {job.FileName}");
        _ = LoadAudioInfoAsync(job);
    }

    private async Task LoadAudioInfoAsync(AudioExtractorJob job)
    {
        if (!File.Exists(FFprobe)) return;
        try
        {
            var args = $"-v quiet -print_format json -show_streams \"{job.InputPath}\"";
            var output = await RunProcessAsync(FFprobe, args);
            var root = JsonNode.Parse(output);
            var streams = root?["streams"]?.AsArray();
            if (streams != null)
            {
                int audioIndex = 0;
                foreach (var stream in streams)
                {
                    if (stream?["codec_type"]?.GetValue<string>() == "audio")
                    {
                        var info = new AudioStreamInfo
                        {
                            GlobalIndex = stream?["index"]?.GetValue<int>() ?? 0,
                            Index = audioIndex++,
                            Codec = stream?["codec_name"]?.GetValue<string>() ?? "-",
                            Language = stream?["tags"]?["language"]?.GetValue<string>() ?? "und",
                            Title = stream?["tags"]?["title"]?.GetValue<string>() ?? "",
                        };
                        job.Streams.Add(info);
                    }
                }
            }
            Log($"[INFO] {job.FileName} has {job.Streams.Count} audio track(s).");
        }
        catch (Exception ex)
        {
            Log($"[WARN] Could not read streams for {job.FileName}: {ex.Message}");
        }
    }

    private void RemoveSelectedJob_Click(object sender, RoutedEventArgs e)
    {
        if (JobGrid.SelectedItem is AudioExtractorJob job)
        {
            if (job.Status == JobStatus.Running) return;
            _queue.Remove(job);
        }
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        _queue.Clear();
        Log("[INFO] Queue cleared.");
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select output folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _customOutputFolder = dlg.SelectedPath;
            OutputPathText.Text = _customOutputFolder;
            OutputPathText.Foreground = (Brush)FindResource("TextBrush");
        }
    }

    private void ResetOutput_Click(object sender, RoutedEventArgs e)
    {
        _customOutputFolder = string.Empty;
        OutputPathText.Text = "Same as source file";
        OutputPathText.Foreground = (Brush)FindResource("MutedBrush");
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = string.IsNullOrEmpty(_lastOutputFolder)
                     ? (string.IsNullOrEmpty(_customOutputFolder)
                         ? (_queue.FirstOrDefault(j => j.Status == JobStatus.Done)?.InputPath is string fp
                             ? Path.GetDirectoryName(fp) : null)
                         : _customOutputFolder)
                     : _lastOutputFolder;

        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            Process.Start("explorer.exe", folder);
    }

    private async void StartQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        if (_queue.Count == 0) return;
        if (!File.Exists(FFmpeg))
        {
            MessageBox.Show("FFmpeg not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _isRunning = true;

        bool extractAll = ExtractAllRadio.IsChecked == true;
        string formatStr = (FormatBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Original Copy (Fastest)";
        string targetExt = GetTargetExtension(formatStr);
        string audioCodec = GetAudioCodecArg(formatStr);

        int done = 0, failed = 0;
        var pendingJobs = _queue.Where(j => j.Status == JobStatus.Waiting).ToList();

        Log($"[START] Extracting audio from {pendingJobs.Count} file(s)...");
        SetStatus($"Processing 0 / {pendingJobs.Count}");

        foreach (var job in pendingJobs)
        {
            if (_cts.Token.IsCancellationRequested) break;
            int idx = pendingJobs.IndexOf(job) + 1;
            SetStatus($"Processing {idx} / {pendingJobs.Count}");

            bool success = await ProcessJobAsync(job, extractAll, targetExt, audioCodec, _cts.Token);
            if (success) done++;
            else failed++;
            
            MuxProgressBar.Value = idx * 100.0 / pendingJobs.Count;
            MuxProgressText.Text = $"{MuxProgressBar.Value:F0}%";
        }

        _isRunning = false;
        if (_cts.Token.IsCancellationRequested) SetStatus("Cancelled", "#D29922");
        else SetStatus($"Done: {done} succeeded, {failed} failed.", "#3FB950");
        Log($"[DONE] {done} succeeded, {failed} failed.");
    }

    private void StopQueue_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        try { _ffmpegProcess?.Kill(); } catch { }
        Log("[STOP] Cancellation requested.");
    }

    private async Task<bool> ProcessJobAsync(AudioExtractorJob job, bool extractAll, string ext, string codecArg, CancellationToken ct)
    {
        job.Status = JobStatus.Running;
        job.StatusText = "Extracting...";
        Log($"\n[JOB] Extracting from: {job.FileName}");

        if (job.Streams.Count == 0)
        {
            // Fallback if ffprobe didn't run or found nothing: just map first audio track
            job.Streams.Add(new AudioStreamInfo { Index = 0, GlobalIndex = -1 });
        }

        var streamsToProcess = extractAll ? job.Streams : new List<AudioStreamInfo> { job.Streams.First() };
        
        string dir = string.IsNullOrEmpty(_customOutputFolder) ? (Path.GetDirectoryName(job.InputPath) ?? ".") : _customOutputFolder;
        string baseN = Path.GetFileNameWithoutExtension(job.InputPath);
        _lastOutputFolder = dir;

        var sbArgs = new StringBuilder();
        sbArgs.Append($"-y -i \"{job.InputPath}\" ");

        int outIndex = 0;
        foreach (var stream in streamsToProcess)
        {
            // If extracting multiple, suffix with track index. 
            string suffix = streamsToProcess.Count > 1 ? $"_Track{stream.Index + 1}" : "_Audio";
            
            // Fix: Cleanse ext and codec when copying original
            string outExt = ext;
            if (ext == "copy")
            {
                outExt = stream.Codec.ToLower() switch
                {
                    "aac" => "m4a",
                    "mp3" => "mp3",
                    "flac" => "flac",
                    "vorbis" => "ogg",
                    "opus" => "opus",
                    "ac3" => "ac3",
                    "eac3" => "eac3",
                    "dts" => "dts",
                    _ => "mka" // fallback generic audio container
                };
            }

            string outPath = Path.Combine(dir, $"{baseN}{suffix}.{outExt}");
            outPath = GetUniqueFilePath(outPath);

            string mapArg = stream.GlobalIndex >= 0 ? $"-map 0:{stream.GlobalIndex}" : "-map 0:a:0";
            
            // Apply map and metadata copying
            sbArgs.Append($"{mapArg} {codecArg} -map_metadata 0 ");
            sbArgs.Append($"\"{outPath}\" ");
            
            if (outIndex == 0) job.OutputPath = outPath; // Just save the first one for reference
            outIndex++;
        }

        Log($"[CMD] ffmpeg {sbArgs.ToString().Trim()}");

        var psi = new ProcessStartInfo
        {
            FileName = FFmpeg,
            Arguments = sbArgs.ToString(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _ffmpegProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderrBuffer = new StringBuilder();
        _ffmpegProcess.ErrorDataReceived += (_, ev) =>
        {
            if (ev.Data != null) stderrBuffer.AppendLine(ev.Data);
        };

        _ffmpegProcess.Start();
        ProcessGuard.Watch(_ffmpegProcess);
        _ffmpegProcess.BeginErrorReadLine();

        await Task.Run(async () =>
        {
            while (await _ffmpegProcess.StandardOutput.ReadLineAsync() != null)
            {
                if (ct.IsCancellationRequested) break;
            }
        }, ct);

        if (ct.IsCancellationRequested)
        {
            try { _ffmpegProcess.Kill(); } catch { }
            job.Status = JobStatus.Cancelled;
            job.StatusText = "Cancelled";
            return false;
        }

        try { await _ffmpegProcess.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { }

        int code = _ffmpegProcess.ExitCode;
        _ffmpegProcess.Dispose();
        _ffmpegProcess = null;

        if (code == 0)
        {
            job.Status = JobStatus.Done;
            job.StatusText = "Done";
            job.Progress = 100;
            Log($"[OK] Successfully extracted audio for {job.FileName}");
            return true;
        }
        else
        {
            job.Status = JobStatus.Failed;
            job.StatusText = "Failed";
            Log($"[FAIL] Extraction failed for {job.FileName}");
            var errLines = stderrBuffer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(5);
            foreach (var l in errLines) Log($"  !! {l.Trim()}");
            return false;
        }
    }

    private string GetTargetExtension(string format) => format switch
    {
        "MP3" => "mp3",
        "AAC" => "aac",
        "FLAC" => "flac",
        "WAV" => "wav",
        "M4A" => "m4a",
        _ => "copy"
    };

    private string GetAudioCodecArg(string format) => format switch
    {
        "MP3" => "-c:a libmp3lame -q:a 2",
        "AAC" => "-c:a aac -b:a 256k",
        "FLAC" => "-c:a flac",
        "WAV" => "-c:a pcm_s16le",
        "M4A" => "-c:a aac -b:a 256k",
        _ => "-c:a copy"
    };

    private string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        int i = 1;
        while (File.Exists(path)) path = Path.Combine(dir, $"{name} ({i++}){ext}");
        return path;
    }

    private void Log(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {UiTextSanitizer.Normalize(msg)}\n");
            LogBox.ScrollToEnd();
        });
    }

    private void SetStatus(string text, string colorHex = "#8B949E")
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
            StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        });
    }

    private static async Task<string> RunProcessAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output;
    }
}
