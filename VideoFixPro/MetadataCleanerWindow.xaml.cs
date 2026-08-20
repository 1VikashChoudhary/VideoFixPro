using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using WinForms = System.Windows.Forms;

namespace VideoFixPro;

public class MetadataItem
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public partial class MetadataCleanerWindow : Window
{
    private string _filePath = string.Empty;
    private double _durationSeconds;
    private long _fileSizeBytes;
    private int _sourceWidth;
    private int _sourceHeight;

    private readonly ObservableCollection<MetadataItem> _metadataList = new();
    private string _detectedGpsCoords = string.Empty;
    private double? _gpsLat;
    private double? _gpsLon;

    // Rendering & Progress
    private CancellationTokenSource? _cts;
    private Process? _ffmpegProcess;
    private bool _isRendering;
    private string _customOutputFolder = string.Empty;
    private string _lastOutputFolder = string.Empty;

    private static string AppDir => AppDomain.CurrentDomain.BaseDirectory;
    private static string FFmpeg => GetBinPath("ffmpeg.exe");
    private static string FFprobe => GetBinPath("ffprobe.exe");

    private static string GetBinPath(string name)
    {
        var appBin = Path.Combine(AppDir, "ffmpeg", name);
        if (File.Exists(appBin)) return appBin;
        var localBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoFixPro", "ffmpeg", name);
        return File.Exists(localBin) ? localBin : appBin;
    }

    public MetadataCleanerWindow(string? preloadPath = null)
    {
        InitializeComponent();
        Loaded += (_, _) => UiTextSanitizer.Apply(this);

        MetadataGrid.ItemsSource = _metadataList;

        if (!string.IsNullOrEmpty(preloadPath) && File.Exists(preloadPath))
            _ = LoadFileAsync(preloadPath);
    }

    // ── Window Chrome ─────────────────────────────────────────────────────────
    private void TitleBar_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount == 2) MaxBtn_Click(s, e);
            else DragMove();
        }
    }
    private void MinBtn_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxBtn_Click(object s, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        if (MaxBtn != null) MaxBtn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }
    private void CloseBtn_Click(object s, RoutedEventArgs e) => Close();

    // ── Drag & Drop ───────────────────────────────────────────────────────────
    private void Window_DragEnter(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
    }
    private void Window_DragOver(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
    }
    private void Window_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0 && File.Exists(files[0]))
                _ = LoadFileAsync(files[0]);
        }
    }
    private void DropZone_Click(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) ChangeFile_Click(s, e);
    }
    private void ChangeFile_Click(object s, RoutedEventArgs e)
    {
        var dlg = new WinForms.OpenFileDialog
        {
            Title = "Select Video to Clean Metadata",
            Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.ts|All Files|*.*"
        };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            _ = LoadFileAsync(dlg.FileName);
    }
    private void RemoveFile_Click(object s, RoutedEventArgs e) => UnloadVideo();

    private void UnloadVideo()
    {
        _filePath = string.Empty;
        _metadataList.Clear();
        _detectedGpsCoords = string.Empty;
        _gpsLat = null;
        _gpsLon = null;

        if (TitleFileName != null) TitleFileName.Text = "No file loaded";
        if (DropZone != null) DropZone.Visibility = Visibility.Visible;
        if (FileHeader != null) FileHeader.Visibility = Visibility.Collapsed;
        if (MetadataCard != null) MetadataCard.Visibility = Visibility.Collapsed;
        if (GpsAlertBorder != null) GpsAlertBorder.Visibility = Visibility.Collapsed;
        SetStatus("Ready", "#8B949E");
    }

    private async Task LoadFileAsync(string path)
    {
        if (!File.Exists(path)) return;
        _filePath = path;

        var fi = new FileInfo(path);
        _fileSizeBytes = fi.Length;
        double mb = _fileSizeBytes / (1024.0 * 1024.0);

        if (TitleFileName != null) TitleFileName.Text = Path.GetFileName(path);
        if (HeaderFileName != null) HeaderFileName.Text = Path.GetFileName(path);
        if (HeaderFileSize != null) HeaderFileSize.Text = $"{mb:F1} MB";

        if (DropZone != null) DropZone.Visibility = Visibility.Collapsed;
        if (FileHeader != null) FileHeader.Visibility = Visibility.Visible;
        if (MetadataCard != null) MetadataCard.Visibility = Visibility.Visible;

        SetStatus($"Inspecting metadata for {Path.GetFileName(path)}...", "#388BFD");
        await ProbeMetadataAsync(path);
        SetStatus($"Inspected: {_metadataList.Count} tags detected", "#3FB950");
    }

    private async Task ProbeMetadataAsync(string path)
    {
        _metadataList.Clear();
        _detectedGpsCoords = string.Empty;
        _gpsLat = null;
        _gpsLon = null;

        if (!File.Exists(FFprobe)) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFprobe,
                Arguments = $"-v error -show_entries format=duration:stream=width,height -show_entries format_tags:stream_tags -of default=noprint_wrappers=1 \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var iso6709Regex = new Regex(@"([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)", RegexOptions.Compiled);

            foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                int eqIdx = line.IndexOf('=');
                if (eqIdx <= 0) continue;
                string k = line.Substring(0, eqIdx).Trim();
                string v = line.Substring(eqIdx + 1).Trim();

                if (k == "duration" && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                    _durationSeconds = d;
                else if (k == "width" && int.TryParse(v, out int w))
                    _sourceWidth = w;
                else if (k == "height" && int.TryParse(v, out int h))
                    _sourceHeight = h;
                else
                {
                    // Clean tag prefix
                    if (k.StartsWith("TAG:")) k = k.Substring(4);
                    _metadataList.Add(new MetadataItem { Key = k, Value = v });

                    // Check for GPS Location coordinates
                    if (k.Contains("location", StringComparison.OrdinalIgnoreCase) || k.Contains("gps", StringComparison.OrdinalIgnoreCase))
                    {
                        var match = iso6709Regex.Match(v);
                        if (match.Success)
                        {
                            if (double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double lat) &&
                                double.TryParse(match.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double lon))
                            {
                                _gpsLat = lat;
                                _gpsLon = lon;
                                _detectedGpsCoords = $"Lat: {lat:F5}, Lon: {lon:F5}";
                            }
                        }
                        else
                        {
                            _detectedGpsCoords = v;
                        }
                    }
                }
            }

            Dispatcher.Invoke(() =>
            {
                if (HeaderDuration != null) HeaderDuration.Text = TimeSpan.FromSeconds(_durationSeconds).ToString(@"hh\:mm\:ss");
                if (HeaderResolution != null) HeaderResolution.Text = $"{_sourceWidth}x{_sourceHeight}";

                if (!string.IsNullOrEmpty(_detectedGpsCoords))
                {
                    if (GpsAlertBorder != null) GpsAlertBorder.Visibility = Visibility.Visible;
                    if (GpsCoordsText != null) GpsCoordsText.Text = _detectedGpsCoords;
                }
                else
                {
                    if (GpsAlertBorder != null) GpsAlertBorder.Visibility = Visibility.Collapsed;
                }
            });
        }
        catch { }
    }

    private void OpenMap_Click(object s, RoutedEventArgs e)
    {
        if (_gpsLat.HasValue && _gpsLon.HasValue)
        {
            string url = $"https://www.google.com/maps?q={_gpsLat.Value.ToString(CultureInfo.InvariantCulture)},{_gpsLon.Value.ToString(CultureInfo.InvariantCulture)}";
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
    }

    // ── Output Management ─────────────────────────────────────────────────────
    private void BrowseOutput_Click(object s, RoutedEventArgs e)
    {
        var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Select Output Folder for Cleaned Video",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
        {
            _customOutputFolder = dlg.SelectedPath;
            if (OutputPathText != null)
            {
                OutputPathText.Text = _customOutputFolder;
                OutputPathText.Foreground = (Brush)FindResource("TextBrush");
            }
        }
    }
    private void ResetOutput_Click(object s, RoutedEventArgs e)
    {
        _customOutputFolder = string.Empty;
        if (OutputPathText != null)
        {
            OutputPathText.Text = "Same as source file";
            OutputPathText.Foreground = (Brush)FindResource("MutedBrush");
        }
    }
    private void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        string dir = !string.IsNullOrEmpty(_lastOutputFolder) ? _lastOutputFolder :
                     !string.IsNullOrEmpty(_customOutputFolder) ? _customOutputFolder :
                     Path.GetDirectoryName(_filePath) ?? "";
        if (Directory.Exists(dir))
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private void ToggleLog_Click(object s, RoutedEventArgs e)
    {
        if (LogBox == null || ToggleLogBtn == null) return;
        if (LogBox.Visibility == Visibility.Visible)
        {
            LogBox.Visibility = Visibility.Collapsed;
            ToggleLogBtn.Content = "Show";
        }
        else
        {
            LogBox.Visibility = Visibility.Visible;
            ToggleLogBtn.Content = "Hide";
        }
    }

    // ── Sanitization Execution ────────────────────────────────────────────────
    private async void Sanitize_Click(object s, RoutedEventArgs e)
    {
        if (_isRendering) return;
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
        {
            MessageBox.Show("Please load a video file first.", "No File", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ExecuteSanitizeAsync();
    }

    private void Cancel_Click(object s, RoutedEventArgs e)
    {
        _cts?.Cancel();
        try { _ffmpegProcess?.Kill(); } catch { }
    }

    private async Task ExecuteSanitizeAsync()
    {
        _isRendering = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        string dir = !string.IsNullOrEmpty(_customOutputFolder) ? _customOutputFolder : Path.GetDirectoryName(_filePath) ?? ".";
        string safeName = Path.GetFileNameWithoutExtension(_filePath);
        string ext = Path.GetExtension(_filePath);
        string outputPath = Path.Combine(dir, $"{safeName}_sanitized{ext}");
        outputPath = GetUniqueFilePath(outputPath);
        _lastOutputFolder = dir;

        if (_durationSeconds <= 0)
        {
            await ProbeMetadataAsync(_filePath);
            if (_durationSeconds <= 0) _durationSeconds = 1.0;
        }

        SetRenderingUI(true);
        SetStatus("Sanitizing metadata & privacy tags...", "#388BFD");
        Log($"\n[PRIVACY SHIELD] Source: {Path.GetFileName(_filePath)}");
        Log($"[PRIVACY SHIELD] Output: {outputPath}");

        bool stripChapters = StripChaptersCheck?.IsChecked == true;
        string chapterArgs = stripChapters ? "-map_chapters -1 " : "";

        // -map_metadata -1 strips all global, stream, and format tags completely in lossless mode
        string args = $"-y -i \"{_filePath}\" -map 0 -map_metadata -1 {chapterArgs}-c copy \"{outputPath}\"";
        Log($"[CMD] ffmpeg {args}");

        bool success = await RunFFmpegAsync(args, _durationSeconds, _cts.Token);

        _isRendering = false;
        SetRenderingUI(false);

        if (_cts.Token.IsCancellationRequested)
        {
            SetStatus("Cancelled", "#D29922");
            try { File.Delete(outputPath); } catch { }
        }
        else if (success && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
        {
            var finalFi = new FileInfo(outputPath);
            double finalMb = finalFi.Length / (1024.0 * 1024.0);
            SetRenderProgress(100);
            SetStatus($"Done! Cleaned: {finalMb:F1} MB", "#3FB950");
            Log($"[SUCCESS] Sanitized: All privacy tags purged! ({outputPath})");
            ShowNotification("Privacy Shield Complete", $"Sanitized: {Path.GetFileName(outputPath)}");
        }
        else
        {
            SetStatus("Sanitization failed", "#F85149");
        }
    }

    private async Task<bool> RunFFmpegAsync(string args, double totalDuration, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FFmpeg,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };

        var tcs = new TaskCompletionSource<bool>();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _ffmpegProcess = proc;

        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            Dispatcher.Invoke(() => Log(e.Data));
        };

        proc.Exited += (_, _) =>
        {
            ProcessGuard.Unwatch(proc);
            tcs.TrySetResult(proc.ExitCode == 0);
            proc.Dispose();
        };

        try
        {
            proc.Start();
            ProcessGuard.Watch(proc);
            proc.BeginErrorReadLine();
            using (token.Register(() => { try { proc.Kill(); } catch { } }))
            {
                return await tcs.Task;
            }
        }
        catch { return false; }
    }

    private void SetRenderingUI(bool rendering)
    {
        if (RenderProgressPanel != null) RenderProgressPanel.Visibility = rendering ? Visibility.Visible : Visibility.Collapsed;
        if (CancelBtn != null) CancelBtn.Visibility = rendering ? Visibility.Visible : Visibility.Collapsed;
        if (SanitizeBtn != null) SanitizeBtn.IsEnabled = !rendering;
        if (OpenFolderBtn != null) OpenFolderBtn.IsEnabled = !rendering && !string.IsNullOrEmpty(_lastOutputFolder);

        if (TaskbarProgress != null)
            TaskbarProgress.ProgressState = rendering ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.None;

        if (rendering) SetRenderProgress(50);
    }

    private void SetRenderProgress(double percent)
    {
        if (RenderProgressBar != null) RenderProgressBar.Value = percent;
        if (RenderProgressText != null) RenderProgressText.Text = $"{(int)percent}%";
        if (TaskbarProgress != null) TaskbarProgress.ProgressValue = percent / 100.0;
    }

    private void SetStatus(string text, string hexColor)
    {
        if (StatusText != null) StatusText.Text = text;
        if (StatusDot != null)
        {
            try { StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(hexColor)!; }
            catch { }
        }
    }

    private void Log(string text)
    {
        if (LogBox == null) return;
        LogBox.AppendText(text + "\n");
        LogBox.ScrollToEnd();
    }

    private static string GetUniqueFilePath(string dest)
    {
        if (!File.Exists(dest)) return dest;
        string dir = Path.GetDirectoryName(dest) ?? ".";
        string name = Path.GetFileNameWithoutExtension(dest);
        string ext = Path.GetExtension(dest);
        int i = 1;
        while (File.Exists(Path.Combine(dir, $"{name}_{i}{ext}"))) i++;
        return Path.Combine(dir, $"{name}_{i}{ext}");
    }

    private void ShowNotification(string title, string message)
    {
        try
        {
            var notif = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                Visible = true,
                BalloonTipTitle = title,
                BalloonTipText = message
            };
            notif.ShowBalloonTip(3000);
            Task.Delay(4000).ContinueWith(_ => notif.Dispose());
        }
        catch { }
    }
}
