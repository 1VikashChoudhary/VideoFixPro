using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.IO.Compression;
using System.Net.Http;
using VideoFixPro.Models;
// Add reference for WinForms dialog
using WinForms = System.Windows.Forms;

namespace VideoFixPro;


// â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
//  Main Window
// â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
public partial class MainWindow : Window
{
    private static readonly HttpClient _httpClient = new();
    private readonly ObservableCollection<VideoJob> _queue = new();
    private CancellationTokenSource? _cts;
    private Process? _ffmpegProcess;
    private bool _isRunning;
    private bool _logCollapsed;
    private string _customOutputFolder = string.Empty;
    private string _lastOutputFolder   = string.Empty;
    private bool _hasNvidia;
    private bool _hasAmd;

    // â”€â”€ FFmpeg paths â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static string AppDir => AppDomain.CurrentDomain.BaseDirectory;
    
    private static string FFmpeg => GetBinPath("ffmpeg.exe");
    private static string FFprobe => GetBinPath("ffprobe.exe");

    private static string GetBinPath(string name)
    {
        // 1. Check App folder (Portable mode)
        var appBin = Path.Combine(AppDir, "ffmpeg", name);
        if (File.Exists(appBin)) return appBin;

        // 2. Check LocalAppData
        var localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoFixPro", "ffmpeg", name);
        if (File.Exists(localData)) return localData;

        // 3. Default to App folder if writable, else LocalAppData
        if (IsFolderWritable(AppDir))
            return appBin;
        
        return localData;
    }

    private static bool IsFolderWritable(string path)
    {
        try
        {
            var testFile = Path.Combine(path, Guid.NewGuid().ToString());
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch { return false; }
    }

    // â”€â”€ Supported extensions â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".m2ts" };

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UiTextSanitizer.Apply(this);

        // Bind queue list
        QueueList.DataContext = _queue;

        // Wire radio-button description updates
        ModeAuto.Checked         += ModeChanged;
        ModeStreamCopy.Checked   += ModeChanged;
        ModeReEncode.Checked     += ModeChanged;
        ModeDeepRecover.Checked  += ModeChanged;

        // Verify ffmpeg and detect GPU on start
        CheckFFmpeg();
        CleanupTempThumbs();
        _ = DetectGpuAsync();

        Log("[INFO] Video Fix Pro ready. Add files to the queue and press Fix All.");
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  TITLE BAR
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (e.ClickCount == 2)
            MaximizeRestore();
        else
            DragMove();
    }

    private void MinBtn_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxBtn_Click(object s, RoutedEventArgs e) => MaximizeRestore();

    private void CloseBtn_Click(object s, RoutedEventArgs e)
    {
        CancelCurrentJob();
        Close();
    }

    private void TopmostBtn_Click(object sender, RoutedEventArgs e)
    {
        this.Topmost = TopmostBtn.IsChecked == true;
        Log($"[INFO] Always on Top {(this.Topmost ? "enabled" : "disabled")}");
    }

    private void MaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        MaxBtn.Content = WindowState == WindowState.Maximized ? "O" : "[ ]";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  DROP ZONE
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Copy;
        DropZoneBorder.BorderBrush = (Brush)FindResource("AccentBrush");
        DropZoneBorder.Background  = new SolidColorBrush(Color.FromRgb(0x1C, 0x21, 0x28));
        DropIcon.Text              = "v";
        DropMainText.Text          = "Release to add to queue";
        e.Handled = true;
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        ResetDropZone();
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        foreach (var path in paths)
            ProcessPath(path);
    }

    private void ProcessPath(string path)
    {
        if (Directory.Exists(path))
        {
            try
            {
                var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                                     .Where(f => IsVideoFile(f))
                                     .ToArray();
                
                if (files.Length > 20)
                {
                    var result = MessageBox.Show($"Found {files.Length} video files in folder. Add all of them to the queue?", 
                        "Large Import", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result != MessageBoxResult.Yes) return;
                }

                foreach (var f in files)
                    AddFileToQueue(f);
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Could not read folder: {ex.Message}");
            }
        }
        else if (File.Exists(path))
        {
            if (IsVideoFile(path)) AddFileToQueue(path);
        }
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title       = "Select Video Files",
            Multiselect = true,
            Filter      = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.ts;*.m2ts|All Files|*.*"
        };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FileNames)
                AddFileToQueue(f);
    }

    private void ResetDropZone()
    {
        DropZoneBorder.BorderBrush = (Brush)FindResource("MutedBrush");
        DropZoneBorder.Background  = (Brush)FindResource("SurfaceBrush");
        DropIcon.Text              = "DIR";
        DropMainText.Text          = "Drop video files here";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  QUEUE MANAGEMENT
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void AddFileToQueue(string path)
    {
        // Avoid duplicates
        if (_queue.Any(j => j.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"[SKIP] Already in queue: {Path.GetFileName(path)}");
            return;
        }

        var fi   = new FileInfo(path);
        var size = FormatFileSize(fi.Length);

        var job = new VideoJob
        {
            FilePath = path,
            FileSize = size
        };

        _queue.Add(job);
        UpdateQueueCount();

        Log($"[ADD]  {Path.GetFileName(path)}  ({size})");

        // Probe the first added file and show info card
        if (_queue.Count == 1)
            _ = LoadVideoInfoAsync(job);

        _ = GenerateThumbnailAsync(job);
    }

    private async Task GenerateThumbnailAsync(VideoJob job)
    {
        // Fix D: use LocalAppData when AppDir is not writable (e.g. Program Files)
        var thumbBase = IsFolderWritable(AppDir)
            ? AppDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoFixPro");
        var thumbDir = Path.Combine(thumbBase, "temp_thumbs");
        if (!Directory.Exists(thumbDir)) Directory.CreateDirectory(thumbDir);

        var thumbPath = Path.Combine(thumbDir, Guid.NewGuid().ToString("N") + ".jpg");
        
        // Wait for duration to be known
        int retries = 0;
        while (job.DurationSeconds <= 0 && retries < 10)
        {
            await Task.Delay(500);
            retries++;
        }

        // Take a screenshot at 10% of duration or 1 second
        var seek = job.DurationSeconds > 5 ? (job.DurationSeconds * 0.1).ToString("F2", CultureInfo.InvariantCulture) : "1";
        
        var args = $"-y -ss {seek} -i \"{job.FilePath}\" -frames:v 1 -q:v 2 -vf \"scale=160:-1\" \"{thumbPath}\"";
        
        try
        {
            await RunProcessAsync(FFmpeg, args);
            if (File.Exists(thumbPath))
            {
                Dispatcher.Invoke(() => job.ThumbnailPath = thumbPath);
            }
        }
        catch { }
    }

    private void RemoveJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is VideoJob job)
        {
            if (job.Status == JobStatus.Running)
            {
                MessageBox.Show("Cannot remove a job that is currently running.\nCancel the operation first.",
                    "Job Running", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            TryDeleteThumbnail(job.ThumbnailPath);
            _queue.Remove(job);
            UpdateQueueCount();
        }
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        foreach (var job in _queue)
        {
            TryDeleteThumbnail(job.ThumbnailPath);
        }

        _queue.Clear();
        UpdateQueueCount();
        VideoInfoCard.Visibility = Visibility.Collapsed;
        Log("[INFO] Queue cleared.");
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Select folder to import all videos from",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
        {
            ProcessPath(dlg.SelectedPath);
        }
    }

    private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QueueList.SelectedItem is VideoJob job)
        {
            ShowVideoInfo(job);
        }
    }

    private void UpdateQueueCount()
    {
        QueueCountText.Text = _queue.Count.ToString();
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  VIDEO INFO  (ffprobe)
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private async Task LoadVideoInfoAsync(VideoJob job)
    {
        if (!File.Exists(FFprobe))
        {
            // Fallback: parse ffmpeg -i stderr
            await LoadInfoViaFFmpegAsync(job);
            return;
        }

        try
        {
            var args = $"-v quiet -print_format json -show_streams -show_format \"{job.FilePath}\"";
            var output = await RunProcessAsync(FFprobe, args);
            ParseFFprobeJson(job, output);
        }
        catch (Exception ex)
        {
            Log($"[WARN] ffprobe failed: {ex.Message}");
        }

        Dispatcher.Invoke(() => ShowVideoInfo(job));
    }

    private void ParseFFprobeJson(VideoJob job, string json)
    {
        const string unknown = "-";
        try
        {
            var root = JsonNode.Parse(json);
            var streams = root?["streams"]?.AsArray();
            var format = root?["format"];
            if (double.TryParse(format?["duration"]?.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dur))
            {
                job.DurationSeconds = dur;
                var ts = TimeSpan.FromSeconds(dur);
                job.Duration = ts.Hours > 0
                    ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
            }
            if (long.TryParse(format?["bit_rate"]?.GetValue<string>(), out long br))
                job.Bitrate = $"{br / 1000} kbps";
            if (streams == null)
                return;
            foreach (var stream in streams)
            {
                var type = stream?["codec_type"]?.GetValue<string>();
                if (type == "video" && job.VideoCodec == unknown)
                {
                    job.VideoCodec = stream?["codec_name"]?.GetValue<string>()?.ToUpperInvariant() ?? unknown;
                    var width = stream?["width"]?.GetValue<int>() ?? 0;
                    var height = stream?["height"]?.GetValue<int>() ?? 0;
                    if (width > 0 && height > 0)
                        job.Resolution = $"{width}x{height}";
                    var fpsStr = stream?["r_frame_rate"]?.GetValue<string>() ?? string.Empty;
                    if (!fpsStr.Contains('/'))
                        continue;
                    var parts = fpsStr.Split('/');
                    if (double.TryParse(parts[0], out double numerator) &&
                        double.TryParse(parts[1], out double denominator) &&
                        denominator != 0)
                    {
                        job.FrameRate = $"{numerator / denominator:F2} fps";
                    }
                }
                else if (type == "audio" && job.AudioCodec == unknown)
                {
                    job.AudioCodec = stream?["codec_name"]?.GetValue<string>()?.ToUpperInvariant() ?? unknown;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[WARN] JSON parse error: {ex.Message}");
        }
    }

    private async Task LoadInfoViaFFmpegAsync(VideoJob job)
    {
        // Parse duration from ffmpeg -i stderr
        try
        {
            // Fix B: 'using' ensures Process handle is always released
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = FFmpeg,
                    Arguments              = $"-i \"{job.FilePath}\"",
                    UseShellExecute        = false,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                }
            };
            p.Start();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            foreach (var line in stderr.Split('\n'))
            {
                if (line.Contains("Duration:"))
                {
                    var t = line.Split("Duration:")[1].Split(',')[0].Trim();
                    if (TimeSpan.TryParse(t, out var ts))
                    {
                        job.DurationSeconds = ts.TotalSeconds;
                        job.Duration        = t;
                    }
                }
                if (line.Contains("Video:"))
                    job.VideoCodec = line.Split("Video:")[1].Trim().Split(' ')[0].Trim(',').ToUpper();
                if (line.Contains("Audio:"))
                    job.AudioCodec = line.Split("Audio:")[1].Trim().Split(' ')[0].Trim(',').ToUpper();
            }
        }
        catch { /* non-critical */ }

        Dispatcher.Invoke(() => ShowVideoInfo(job));
    }

    private void ShowVideoInfo(VideoJob job)
    {
        InfoFileName.Text     = job.FileName;
        InfoDuration.Text     = job.Duration;
        InfoSize.Text         = job.FileSize;
        InfoVideoCodec.Text   = job.VideoCodec;
        InfoAudioCodec.Text   = job.AudioCodec;
        InfoResolution.Text   = job.Resolution;
        InfoBitrateAndFps.Text = $"{job.Bitrate}  {job.FrameRate}".Trim();
        VideoInfoCard.Visibility = Visibility.Visible;
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  REPAIR MODE  â€“ radio button handlers
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (ModeDescription == null) return;
        ModeDescription.Text = GetModeDescription();
    }

    private string GetModeDescription() =>
        (ModeStreamCopy.IsChecked == true) ? "Re-muxes the video container without re-encoding. Extremely fast and lossless. Best for corrupted headers or timestamps." :
        (ModeReEncode.IsChecked   == true) ? "Full re-encode to H.264 (video) + AAC (audio). Fixes most corruption issues. Moderate speed." :
        (ModeDeepRecover.IsChecked== true) ? "Error-tolerant deep recovery. Ignores decode errors and re-encodes everything. Slowest but handles severely damaged files." :
        "Automatically selects the best strategy. Starts with a fast lossless copy; if that fails, escalates to a deep error-tolerant re-encode.";

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  OUTPUT SETTINGS
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description         = "Select output folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _customOutputFolder     = dlg.SelectedPath;
            OutputFolderText.Text   = _customOutputFolder;
            OutputFolderText.Foreground = (Brush)FindResource("TextBrush");
        }
    }

    private void ResetOutput_Click(object sender, RoutedEventArgs e)
    {
        _customOutputFolder         = string.Empty;
        OutputFolderText.Text       = "Same as source file";
        OutputFolderText.Foreground = (Brush)FindResource("MutedBrush");
    }

    private string GetOutputPath(VideoJob job)
    {
        var ext    = ((ComboBoxItem)FormatBox.SelectedItem)?.Content?.ToString()?.ToLower() ?? "mp4";
        var dir    = string.IsNullOrEmpty(_customOutputFolder)
                     ? Path.GetDirectoryName(job.FilePath) ?? "."
                     : _customOutputFolder;
        var baseN  = Path.GetFileNameWithoutExtension(job.FilePath) + "_fixed";
        var path   = Path.Combine(dir, baseN + "." + ext);
        
        return GetUniqueFilePath(path);
    }

    private string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;

        string dir  = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext  = Path.GetExtension(path);
        int i = 1;

        while (File.Exists(path))
        {
            path = Path.Combine(dir, $"{name} ({i++}){ext}");
        }
        return path;
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  FIX ALL  â€“  main pipeline
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private async void FixAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        if (_queue.Count == 0)
        {
            MessageBox.Show("Add at least one video to the queue first.", "Queue Empty",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(FFmpeg))
        {
            MessageBox.Show("FFmpeg binaries are missing. Please download them first.", "FFmpeg Missing", 
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _cts?.Dispose();
        _cts        = new CancellationTokenSource();
        _isRunning  = true;
        SetRunningUI(true);

        int done = 0, failed = 0;
        var pendingJobs = _queue.Where(j => j.Status == JobStatus.Waiting).ToList();

        Log($"[START] Processing {pendingJobs.Count} file(s)â€¦");
        SetStatus($"Processing 0 / {pendingJobs.Count}", "#388BFD");

        foreach (var job in pendingJobs)
        {
            if (_cts.Token.IsCancellationRequested) break;

            var idx = pendingJobs.IndexOf(job) + 1;
            SetStatus($"Processing {idx} / {pendingJobs.Count}", "#388BFD");

            // Use per-job preferred mode if not 'Auto', otherwise use global setting
            RepairMode jobMode;
            bool useGpu = false;
            int qualityPct = 70;

            if (job.PreferredMode != RepairMode.Auto)
            {
                jobMode = job.PreferredMode;
                // Still need GPU/Quality settings from UI
                Dispatcher.Invoke(() => {
                    useGpu = GpuCheck.IsChecked == true;
                    qualityPct = (int)QualitySlider.Value;
                });
            }
            else
            {
                // Global settings from UI â€” read ALL radio buttons including Auto
                (jobMode, useGpu, qualityPct) = Dispatcher.Invoke(() => {
                    // Fix A: ModeAuto must be checked first; without it the ternary
                    // chain fell through to StreamCopy when Auto was selected.
                    var m = ModeAuto.IsChecked        == true ? RepairMode.Auto        :
                            ModeStreamCopy.IsChecked  == true ? RepairMode.StreamCopy  :
                            ModeReEncode.IsChecked    == true ? RepairMode.ReEncode    :
                            ModeDeepRecover.IsChecked == true ? RepairMode.DeepRecover :
                                                                RepairMode.Auto;        // safe default
                    return (m, GpuCheck.IsChecked == true, (int)QualitySlider.Value);
                });
            }

            string output = GetOutputPath(job);
            bool success = await RepairJobAsync(job, output, jobMode, useGpu, qualityPct, idx, pendingJobs.Count, _cts.Token);

            if (success)
            {
                done++;
                _lastOutputFolder = Path.GetDirectoryName(output) ?? "";

                // Auto-delete source if enabled
                if (AutoDeleteCheck.IsChecked == true)
                {
                    TryDeleteSource(job.FilePath);
                }
            }
            else { failed++; }

            GlobalProgress.Value = idx * 100.0 / pendingJobs.Count;
            TaskbarProgress.ProgressValue = idx * 1.0 / pendingJobs.Count;
        }

        _isRunning = false;
        SetRunningUI(false);

        var summary = $"[DONE] {done} succeeded, {failed} failed.";
        Log(summary);

        if (_cts.IsCancellationRequested)
            SetStatus("Cancelled", "#D29922");
        else if (failed == 0)
            SetStatus($"All {done} file(s) repaired âœ”", "#3FB950");
        else
            SetStatus($"{done} done  Â·  {failed} failed", "#F85149");

        if (!string.IsNullOrEmpty(_lastOutputFolder))
            OpenFolderBtn.IsEnabled = true;

        ShowCompletionNotification(done, failed);
    }

    private void ShowCompletionNotification(int done, int failed)
    {
        try
        {
            var ni = new WinForms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule?.FileName ?? ""),
                Visible = true,
                BalloonTipTitle = "Video Fix Pro - Done",
                BalloonTipText = $"Processed {done + failed} files.\n{done} Success, {failed} Failed.",
                BalloonTipIcon = failed == 0 ? WinForms.ToolTipIcon.Info : WinForms.ToolTipIcon.Warning
            };
            ni.ShowBalloonTip(3000);
            // Clean up after tip shows
            Task.Delay(5000).ContinueWith(_ => { ni.Dispose(); });
        }
        catch { }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  REPAIR ONE JOB
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private async Task<bool> RepairJobAsync(VideoJob job, string output, RepairMode mode, bool useGpu, int qualityPct, int jobIndex, int totalJobs, CancellationToken ct)
    {
        job.Status     = JobStatus.Running;
        job.StatusText = "Runningâ€¦";
        job.Progress   = 0;
        job.ETA        = "Calculating...";
        var startTime  = DateTime.Now;

        Log($"\n[JOB]  {job.FileName}  â†’  {Path.GetFileName(output)}");

        bool success;

        if (mode == RepairMode.Auto)
        {
            Log("[AUTO] Trying Stream Copyâ€¦");
            success = await RunFFmpegRepairAsync(job, startTime, output, RepairMode.StreamCopy, useGpu, qualityPct, jobIndex, totalJobs, ct);
            if (!success && !ct.IsCancellationRequested)
            {
                Log("[AUTO] Stream Copy failed â†’ Deep Recoverâ€¦");
                job.Progress = 0;
                success = await RunFFmpegRepairAsync(job, startTime, output, RepairMode.DeepRecover, useGpu, qualityPct, jobIndex, totalJobs, ct);
            }
        }
        else
        {
            success = await RunFFmpegRepairAsync(job, startTime, output, mode, useGpu, qualityPct, jobIndex, totalJobs, ct);
        }

        if (ct.IsCancellationRequested)
        {
            job.Status     = JobStatus.Cancelled;
            job.StatusText = "Cancelled";
            job.Progress   = 0;
            return false;
        }

        if (success)
        {
            job.Status     = JobStatus.Done;
            job.StatusText = "Done";
            job.Progress   = 100;
            job.OutputPath = output;
            Log($"[OK]   Saved â†’ {output}");
        }
        else
        {
            job.Status     = JobStatus.Failed;
            job.StatusText = "Failed";
            Log($"[FAIL] Could not repair {job.FileName}");
        }

        return success;
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  FFMPEG EXECUTION
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    
    private async Task<bool> RunFFmpegRepairAsync(VideoJob job, DateTime startTime, string output,
                                                   RepairMode mode, bool useGpu, int qualityPct, 
                                                   int jobIndex, int totalJobs, CancellationToken ct)
    {
        var args = BuildArgs(job.FilePath, output, mode, useGpu, _hasNvidia, _hasAmd, qualityPct, job.VideoCodec,
                              job.HasTrim ? job.TrimStart : (double?)null,
                              job.HasTrim ? job.TrimEnd   : (double?)null);
        if (job.HasTrim)
            Log($"[TRIM] Applied trim: {TrimSegment.FormatTime(job.TrimStart)} â†’ {TrimSegment.FormatTime(job.TrimEnd)}");
        Log($"[CMD]  ffmpeg {args}");

        var psi = new ProcessStartInfo
        {
            FileName               = FFmpeg,
            Arguments              = args,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
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

        // Read stdout for progress
        await Task.Run(async () =>
        {
            string? line;
            while ((line = await _ffmpegProcess.StandardOutput.ReadLineAsync()) != null)
            {
                if (ct.IsCancellationRequested) break;

                if (line.StartsWith("out_time_ms=") &&
                    long.TryParse(line[12..], out long ms))
                {
                    var currentSecs = ms / 1_000_000.0;
                    double targetDuration = job.HasTrim ? (job.TrimEnd - job.TrimStart) : job.DurationSeconds;
                    if (targetDuration <= 0) targetDuration = 1; // avoid div by zero

                    var pct = Math.Min(currentSecs / targetDuration * 100.0, 99.9);
                    var globalPct = ((jobIndex - 1) + (pct / 100.0)) / totalJobs * 100.0;
                    
                    Dispatcher.Invoke(() => 
                    { 
                        job.Progress = pct; 
                        GlobalProgress.Value = globalPct; 
                        TaskbarProgress.ProgressValue = globalPct / 100.0;

                        // Calculate ETA
                        if (currentSecs > 1)
                        {
                            var elapsed = DateTime.Now - startTime;
                            var totalSecsEst = (elapsed.TotalSeconds / currentSecs) * targetDuration;
                            var remaining = TimeSpan.FromSeconds(Math.Max(0, totalSecsEst - elapsed.TotalSeconds));
                            job.ETA = remaining.TotalHours >= 1 ? remaining.ToString(@"hh\:mm\:ss") : remaining.ToString(@"mm\:ss");
                        }
                    });
                }
            }
        }, ct);

        if (ct.IsCancellationRequested)
        {
            try { _ffmpegProcess.Kill(); } catch { }
            return false;
        }

        try 
        { 
            await _ffmpegProcess.WaitForExitAsync(ct); 
        } 
        catch (OperationCanceledException) { /* Handled via Kill() */ }
        
        int code = _ffmpegProcess.ExitCode;
        _ffmpegProcess.Dispose();
        _ffmpegProcess = null;

        if (code != 0)
        {
            var errLines = stderrBuffer.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(5);
            foreach (var l in errLines)
                Log($"  !! {l.Trim()}");
        }

        return code == 0;
    }

    private static string BuildArgs(string input, string output, RepairMode mode,
        bool useGpu, bool hasNvidia, bool hasAmd, int qualityPercent, string? videoCodec,
        double? trimStart = null, double? trimEnd = null)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("-y ");   // overwrite output

        // Trim: stream-copy uses input-seek (before -i) for speed
        bool hasTrim = trimStart.HasValue && trimEnd.HasValue && trimEnd > trimStart;
        if (hasTrim && mode == RepairMode.StreamCopy)
        {
            string ssSC = trimStart!.Value.ToString("F3", ic);
            sb.Append("-ss " + ssSC + " ");
        }

        if (mode == RepairMode.DeepRecover)
            sb.Append("-err_detect ignore_err ");
        sb.Append("-i \"" + input + "\" ");

        // Re-encode path: seek after -i for frame accuracy; stream-copy appends -t only
        if (hasTrim && mode != RepairMode.StreamCopy)
        {
            string ssRE = trimStart!.Value.ToString("F3", ic);
            string tRE  = (trimEnd!.Value - trimStart!.Value).ToString("F3", ic);
            sb.Append("-ss " + ssRE + " ");
            sb.Append("-t "  + tRE  + " ");
        }
        else if (hasTrim)
        {
            string tSC = (trimEnd!.Value - trimStart!.Value).ToString("F3", ic);
            sb.Append("-t " + tSC + " ");
        }

        sb.Append("-progress pipe:1 ");   // send progress to stdout

        // Map percentage (0-100) to CRF/QP (40-18)
        int quality = (int)(40 - (qualityPercent * (40 - 18) / 100.0));

        switch (mode)
        {
            case RepairMode.StreamCopy:
                sb.Append("-fflags +genpts -c copy -map_metadata 0 -map_chapters 0 -dn -ignore_unknown -copyinkf -flags +global_header ");
                AppendExplorerFriendlyStreamMap(sb, output);
                AppendExplorerFriendlyContainerFlags(sb, output, videoCodec);
                break;
            case RepairMode.ReEncode:
                if (useGpu && hasAmd)
                {
                    sb.Append($"-c:v h264_amf -rc 0 -qp_i {quality} -qp_p {quality} -qp_b {quality} -pix_fmt yuv420p -c:a aac -b:a 192k ");
                }
                else if (useGpu && hasNvidia)
                    sb.Append($"-c:v h264_nvenc -preset fast -rc vbr -cq {quality} -b:v 0 -pix_fmt yuv420p -c:a aac -b:a 192k ");
                else
                    sb.Append($"-c:v libx264 -preset fast -crf {quality} -pix_fmt yuv420p -c:a aac -b:a 192k ");
                break;
            case RepairMode.DeepRecover:
                sb.Append("-fflags +discardcorrupt ");
                if (useGpu && hasAmd)
                {
                    sb.Append($"-c:v h264_amf -rc 0 -qp_i {quality} -qp_p {quality} -qp_b {quality} -pix_fmt yuv420p -c:a aac -b:a 192k ");
                }
                else if (useGpu && hasNvidia)
                    sb.Append($"-c:v h264_nvenc -preset slow -rc vbr -cq {quality} -b:v 0 -pix_fmt yuv420p -c:a aac -b:a 192k ");
                else
                    sb.Append($"-c:v libx264 -preset slow -crf {quality} -pix_fmt yuv420p -c:a aac -b:a 192k ");
                break;
        }

        sb.Append($"\"{output}\"");
        return sb.ToString();
    }

    private static void AppendExplorerFriendlyContainerFlags(StringBuilder sb, string output, string? videoCodec)
    {
        var ext = Path.GetExtension(output).ToLowerInvariant();
        switch (ext)
        {
            case ".mp4":
            case ".m4v":
            case ".mov":
                sb.Append("-movflags +faststart+use_metadata_tags ");
                sb.Append("-brand mp42 ");

                var codec = videoCodec?.Trim().ToUpperInvariant();
                if (codec == "HEVC" || codec == "H265" || codec == "H.265")
                {
                    sb.Append("-tag:v hvc1 ");
                }
                else if (codec == "H264" || codec == "AVC" || codec == "H.264")
                {
                    sb.Append("-tag:v avc1 ");
                }
                break;
        }
    }

    private static void AppendExplorerFriendlyStreamMap(StringBuilder sb, string output)
    {
        var ext = Path.GetExtension(output).ToLowerInvariant();
        switch (ext)
        {
            case ".mp4":
            case ".m4v":
            case ".mov":
                sb.Append("-map 0:v:0 -map 0:a? ");
                sb.Append("-disposition:v:0 default ");
                break;
            default:
                sb.Append("-map 0 ");
                break;
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  CANCEL
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelCurrentJob();

    private void CancelCurrentJob()
    {
        _cts?.Cancel();
        try { _ffmpegProcess?.Kill(); } catch { }
        Log("[STOP] Cancellation requested.");
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  LOG HELPERS
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void Log(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var safeMessage = UiTextSanitizer.Normalize(msg);
            LogBox.AppendText($"[{timestamp}] {safeMessage}\n");
            LogBox.ScrollToEnd();
        });
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogBox.Text); Log("[INFO] Log copied to clipboard."); }
        catch { }
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"VideoFixPro_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                File.WriteAllText(dlg.FileName, LogBox.Text);
                Log($"[INFO] Log saved to {dlg.FileName}");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Could not save log: {ex.Message}");
            }
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        Log("[INFO] Log cleared.");
    }

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        _logCollapsed = !_logCollapsed;
        LogRow.Height        = _logCollapsed ? new GridLength(0) : new GridLength(160);
        ToggleLogBtn.Content = _logCollapsed ? "^" : "v";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  STATUS BAR
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void SetStatus(string text, string hexColor = "#8B949E")
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text    = UiTextSanitizer.Normalize(text);
            StatusDot.Fill     = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
        });
    }

    private void SetRunningUI(bool running)
    {
        Dispatcher.Invoke(() =>
        {
            FixAllBtn.IsEnabled    = !running;
            CancelBtn.IsEnabled    = running;
            GlobalProgress.Visibility = running ? Visibility.Visible : Visibility.Hidden;
            
            if (running)
                TaskbarProgress.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
            else
            {
                GlobalProgress.Value = 0;
                TaskbarProgress.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
                TaskbarProgress.ProgressValue = 0;
            }
        });
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  OPEN OUTPUT FOLDER
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void OpenTrimTool_Click(object sender, RoutedEventArgs e)
    {
        // If a file is selected in the queue, pre-load it in the trim window
        string? preloadPath = null;
        if (QueueList.SelectedItem is VideoJob selectedJob)
            preloadPath = selectedJob.FilePath;

        var trim = new TrimWindow(preloadPath, _hasNvidia, _hasAmd, (int)QualitySlider.Value);
        trim.Owner = this;
        trim.Show();
    }

        /// <summary>
    /// Called by TrimWindow when the user clicks "Add to Main Queue".
    /// Creates a VideoJob pre-configured with trim start/end times.
    /// </summary>
    public void AddTrimJobToQueue(string filePath, double inPoint, double outPoint, RepairMode mode = RepairMode.Auto)
    {
        Dispatcher.Invoke(() =>
        {
            // Reuse existing queue entry if the same file is already there
            var existing = _queue.FirstOrDefault(j =>
                j.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.TrimStart = inPoint;
                existing.TrimEnd   = outPoint;
                existing.PreferredMode = mode;
                existing.Status    = JobStatus.Waiting;
                existing.StatusText = "Waiting";
                existing.Progress   = 0;
                Log($"[TRIM]  Updated existing queue item: {existing.FileName}  " +
                    $"{TrimSegment.FormatTime(inPoint)} â†’ {TrimSegment.FormatTime(outPoint)}");
            }
            else
            {
                var fi   = new FileInfo(filePath);
                var job  = new VideoJob
                {
                    FilePath  = filePath,
                    FileSize  = FormatFileSize(fi.Length),
                    TrimStart = inPoint,
                    TrimEnd   = outPoint,
                    PreferredMode = mode
                };
                _queue.Add(job);
                UpdateQueueCount();
                Log($"[TRIM]  Added to queue: {job.FileName}  " +
                    $"{TrimSegment.FormatTime(inPoint)} â†’ {TrimSegment.FormatTime(outPoint)}");
                _ = LoadVideoInfoAsync(job);
            }
        });
    }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = string.IsNullOrEmpty(_lastOutputFolder)
                     ? (string.IsNullOrEmpty(_customOutputFolder)
                         ? (_queue.FirstOrDefault(j => j.Status == JobStatus.Done)?.FilePath is string fp
                             ? Path.GetDirectoryName(fp) : null)
                         : _customOutputFolder)
                     : _lastOutputFolder;

        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            Process.Start("explorer.exe", folder);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  CLEANUP
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    public void CleanupTempThumbs()
    {
        try
        {
            var thumbBase = IsFolderWritable(AppDir)
                ? AppDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoFixPro");

            // Clean main repair thumbnails
            CleanDirectory(Path.Combine(thumbBase, "temp_thumbs"));
            // Clean trim tool filmstrip thumbnails
            CleanDirectory(Path.Combine(thumbBase, "trim_thumbs"));
        }
        catch { }
    }

    private void CleanDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir))
        {
            try { File.Delete(file); } catch { }
        }
    }

    private void TryDeleteThumbnail(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  HELPERS
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void TryDeleteSource(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                WinForms.DialogResult res = WinForms.MessageBox.Show(
                    $"Are you sure you want to delete the source file?\n{Path.GetFileName(path)}",
                    "Confirm Delete", WinForms.MessageBoxButtons.YesNo, WinForms.MessageBoxIcon.Warning);

                if (res == WinForms.DialogResult.Yes)
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    Log($"[CLEAN] Source sent to Recycle Bin: {Path.GetFileName(path)}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[WARN] Could not delete source: {ex.Message}");
        }
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path);
        return VideoExtensions.Contains(ext);
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
        return $"{bytes / 1024.0:F1} KB";
    }

    private static async Task<string> RunProcessAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            CreateNoWindow         = true
        };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output;
    }

    private void CheckFFmpeg()
    {
        if (!File.Exists(FFmpeg) || !File.Exists(FFprobe))
        {
            Log("[WARN] ffmpeg/ffprobe binaries are missing.");
            DownloadModal.Visibility = Visibility.Visible;
        }
        else
        {
            Log($"[INFO] ffmpeg found: {FFmpeg}");
        }
    }

    private void StartDownload_Click(object sender, RoutedEventArgs e)
    {
        DownloadModal.Visibility = Visibility.Collapsed;
        _ = DownloadFFmpegAsync();
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        DownloadModal.Visibility = Visibility.Collapsed;
        SetStatus("FFmpeg missing!", "#FF4444");
    }

    private async Task DownloadFFmpegAsync()
    {
        string zipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        
        // Use AppDir if writable (Portable), otherwise LocalAppData
        string targetBase = IsFolderWritable(AppDir) 
            ? AppDir 
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoFixPro");
            
        string ffmpegDir = Path.Combine(targetBase, "ffmpeg");
        string zipPath = Path.Combine(targetBase, "ffmpeg.zip");

        try
        {
            if (!Directory.Exists(ffmpegDir)) Directory.CreateDirectory(ffmpegDir);

            Log("[INFO] Starting FFmpeg download...");
            SetStatus("Downloading FFmpeg...", "#388BFD");
            GlobalProgress.Visibility = Visibility.Visible;
            GlobalProgress.IsIndeterminate = false;
            TaskbarProgress.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;

            using (var response = await _httpClient.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1L;

                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var buffer = new byte[8192];
                    var totalRead = 0L;
                    int read;
                    while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read);
                        totalRead += read;
                        if (totalBytes != -1)
                        {
                            int progress = (int)((double)totalRead / totalBytes * 100);
                            Dispatcher.Invoke(() => {
                                GlobalProgress.Value = progress;
                                TaskbarProgress.ProgressValue = progress / 100.0;
                            });
                        }
                    }
                }
            }

            Log("[INFO] Download complete. Extracting...");
            SetStatus("Extracting FFmpeg...", "#388BFD");
            GlobalProgress.IsIndeterminate = true;
            TaskbarProgress.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Indeterminate;

            await Task.Run(() =>
            {
                string extractTemp = Path.Combine(targetBase, "ffmpeg_temp");
                if (Directory.Exists(extractTemp)) Directory.Delete(extractTemp, true);
                
                ZipFile.ExtractToDirectory(zipPath, extractTemp);

                var exeFiles = Directory.GetFiles(extractTemp, "*.exe", SearchOption.AllDirectories);
                foreach (var exe in exeFiles)
                {
                    string name = Path.GetFileName(exe).ToLower();
                    if (name == "ffmpeg.exe" || name == "ffprobe.exe")
                    {
                        File.Copy(exe, Path.Combine(ffmpegDir, Path.GetFileName(exe)), true);
                    }
                }

                Directory.Delete(extractTemp, true);
                File.Delete(zipPath);
            });

            Log("[SUCCESS] FFmpeg installed successfully!");
            SetStatus("Ready", "#3FB950");
            _ = DetectGpuAsync();
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Failed to download FFmpeg: {ex.Message}");
            SetStatus("Download Failed", "#FF4444");
        }
        finally
        {
            // Fix E: cleanup always runs â€” even if new code paths or early returns are added
            Dispatcher.Invoke(() =>
            {
                GlobalProgress.Visibility      = Visibility.Hidden;
                GlobalProgress.IsIndeterminate = false;
                GlobalProgress.Value           = 0;
                TaskbarProgress.ProgressState  = System.Windows.Shell.TaskbarItemProgressState.None;
                TaskbarProgress.ProgressValue  = 0;
            });
        }
    }

    private async Task DetectGpuAsync()
    {
        if (!File.Exists(FFmpeg)) return;

        try
        {
            var encoders = await RunProcessAsync(FFmpeg, "-v quiet -encoders");
            _hasNvidia = encoders.Contains("h264_nvenc");
            _hasAmd    = encoders.Contains("h264_amf");

            Dispatcher.Invoke(() =>
            {
                if (_hasNvidia || _hasAmd)
                {
                    GpuCheck.Visibility = Visibility.Visible;
                    GpuCheck.IsChecked  = true;
                    var gpuType = _hasNvidia && _hasAmd ? "Nvidia & AMD" : (_hasNvidia ? "Nvidia NVENC" : "AMD AMF");
                    Log($"[INFO] GPU Acceleration available: {gpuType}");
                }
                else
                {
                    Log("[INFO] No compatible GPU hardware acceleration (NVENC/AMF) detected. Using CPU.");
                }
            });
        }
        catch { }
    }
}



