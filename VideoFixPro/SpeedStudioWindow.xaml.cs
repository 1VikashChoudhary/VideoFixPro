using System;
using System.Collections.Generic;
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

public partial class SpeedStudioWindow : Window
{
    private bool _isInitialized;
    private string _filePath = string.Empty;
    private double _durationSeconds;
    private int _sourceWidth;
    private int _sourceHeight;

    private double _speedMultiplier = 1.0;
    private string _activePreset = "Speed100";

    // Player state
    private readonly System.Windows.Threading.DispatcherTimer _playheadTimer = new();
    private bool _isPlayerPlaying;
    private bool _isSeeking;

    // Rendering & Progress
    private CancellationTokenSource? _cts;
    private Process? _ffmpegProcess;
    private bool _isRendering;
    private string _customOutputFolder = string.Empty;
    private string _lastOutputFolder = string.Empty;

    // Hardware encoder support
    private bool _hasNvidia;
    private bool _hasAmd;
    private bool _hasIntel;

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

    public SpeedStudioWindow(string? preloadPath = null, bool hasNvidia = false, bool hasAmd = false, bool hasIntel = false)
    {
        InitializeComponent();
        Loaded += (_, _) => UiTextSanitizer.Apply(this);
        _hasNvidia = hasNvidia;
        _hasAmd = hasAmd;
        _hasIntel = hasIntel;

        GpuCheck.IsChecked = _hasNvidia || _hasAmd || _hasIntel;

        _playheadTimer.Interval = TimeSpan.FromMilliseconds(40);
        _playheadTimer.Tick += (_, _) => { if (!_isSeeking) UpdateSeekFromPlayer(); };

        _isInitialized = true;

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
    private void CloseBtn_Click(object s, RoutedEventArgs e)
    {
        _playheadTimer.Stop();
        try { Player?.Stop(); } catch { }
        Close();
    }

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
            Title = "Select Video to Adjust Speed",
            Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.ts|All Files|*.*"
        };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            _ = LoadFileAsync(dlg.FileName);
    }
    private void RemoveFile_Click(object s, RoutedEventArgs e) => UnloadVideo();

    private void UnloadVideo()
    {
        _playheadTimer.Stop();
        try { Player?.Stop(); } catch { }
        if (Player != null) Player.Source = null;

        _filePath = string.Empty;
        _durationSeconds = 0;

        if (TitleFileName != null) TitleFileName.Text = "No file loaded";
        if (DropZone != null) DropZone.Visibility = Visibility.Visible;
        if (FileHeader != null) FileHeader.Visibility = Visibility.Collapsed;
        if (PlayerBorder != null) PlayerBorder.Visibility = Visibility.Collapsed;
        if (SeekPanel != null) SeekPanel.Visibility = Visibility.Collapsed;
        SetStatus("Ready", "#8B949E");
    }

    private async Task LoadFileAsync(string path)
    {
        if (!File.Exists(path)) return;
        _filePath = path;

        if (TitleFileName != null) TitleFileName.Text = Path.GetFileName(path);
        if (HeaderFileName != null) HeaderFileName.Text = Path.GetFileName(path);

        if (DropZone != null) DropZone.Visibility = Visibility.Collapsed;
        if (FileHeader != null) FileHeader.Visibility = Visibility.Visible;
        if (PlayerBorder != null) PlayerBorder.Visibility = Visibility.Visible;
        if (SeekPanel != null) SeekPanel.Visibility = Visibility.Visible;

        SetStatus($"Loading {Path.GetFileName(path)}...", "#388BFD");

        try
        {
            Player.Source = new Uri(path);
            Player.SpeedRatio = _speedMultiplier;
            Player.Play();
            Player.Pause();
            _isPlayerPlaying = false;
            if (SeekPlayBtn != null) SeekPlayBtn.Content = "▶";
            if (PlayPauseBtn != null) PlayPauseBtn.Content = "▶";
        }
        catch { }

        await ProbeVideoAsync(path);
        UpdateCalculatedDuration();
        if (OpenFolderBtn != null) OpenFolderBtn.IsEnabled = true;
        SetStatus($"Loaded: {Path.GetFileName(path)}", "#3FB950");
    }

    private async Task ProbeVideoAsync(string path)
    {
        if (!File.Exists(FFprobe)) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFprobe,
                Arguments = $"-v error -show_entries format=duration:stream=width,height -of default=noprint_wrappers=1 \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('=');
                if (parts.Length != 2) continue;
                string k = parts[0].Trim(), v = parts[1].Trim();

                if (k == "duration" && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                    _durationSeconds = d;
                else if (k == "width" && int.TryParse(v, out int w))
                    _sourceWidth = w;
                else if (k == "height" && int.TryParse(v, out int h))
                    _sourceHeight = h;
            }

            Dispatcher.Invoke(() =>
            {
                if (HeaderDuration != null) HeaderDuration.Text = TimeSpan.FromSeconds(_durationSeconds).ToString(@"hh\:mm\:ss");
                if (HeaderResolution != null) HeaderResolution.Text = $"{_sourceWidth}x{_sourceHeight}";
            });
        }
        catch { }
    }

    // ── Player Controls ───────────────────────────────────────────────────────
    private void Player_MediaOpened(object s, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan && _durationSeconds <= 0)
            _durationSeconds = Player.NaturalDuration.TimeSpan.TotalSeconds;
        UpdateCalculatedDuration();
    }
    private void Player_MediaEnded(object s, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.Zero;
        _isPlayerPlaying = false;
        if (SeekPlayBtn != null) SeekPlayBtn.Content = "▶";
        if (PlayPauseBtn != null) PlayPauseBtn.Content = "▶";
        _playheadTimer.Stop();
    }
    private void TogglePlay_Click(object s, RoutedEventArgs e)
    {
        if (_isPlayerPlaying)
        {
            Player.Pause();
            _isPlayerPlaying = false;
            _playheadTimer.Stop();
            if (SeekPlayBtn != null) SeekPlayBtn.Content = "▶";
            if (PlayPauseBtn != null) PlayPauseBtn.Content = "▶";
        }
        else
        {
            Player.SpeedRatio = Math.Clamp(_speedMultiplier, 0.25, 4.0); // WPF MediaElement supports 0.25x to 4x live
            Player.Play();
            _isPlayerPlaying = true;
            _playheadTimer.Start();
            if (SeekPlayBtn != null) SeekPlayBtn.Content = "⏸";
            if (PlayPauseBtn != null) PlayPauseBtn.Content = "⏸";
        }
    }
    private void SeekSlider_MouseDown(object s, MouseButtonEventArgs e) => _isSeeking = true;
    private void SeekSlider_MouseUp(object s, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        if (_durationSeconds > 0)
        {
            double pos = (SeekSlider.Value / 100.0) * _durationSeconds;
            Player.Position = TimeSpan.FromSeconds(pos);
        }
    }
    private void SeekSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking && _durationSeconds > 0)
        {
            double pos = (SeekSlider.Value / 100.0) * _durationSeconds;
            Player.Position = TimeSpan.FromSeconds(pos);
            UpdateSeekTimeDisplay(pos);
        }
    }
    private void UpdateSeekFromPlayer()
    {
        if (_durationSeconds <= 0) return;
        double cur = Player.Position.TotalSeconds;
        SeekSlider.Value = Math.Clamp((cur / _durationSeconds) * 100.0, 0, 100);
        UpdateSeekTimeDisplay(cur);
    }
    private void UpdateSeekTimeDisplay(double curSec)
    {
        if (SeekTimeText != null)
        {
            var cur = TimeSpan.FromSeconds(curSec);
            var tot = TimeSpan.FromSeconds(_durationSeconds);
            SeekTimeText.Text = $"{cur:mm\\:ss} / {tot:mm\\:ss}";
        }
    }

    // ── Speed Multiplier Controls ─────────────────────────────────────────────
    private void Speed025_Click(object s, RoutedEventArgs e) => SetSpeed("Speed025", 0.25);
    private void Speed050_Click(object s, RoutedEventArgs e) => SetSpeed("Speed050", 0.50);
    private void Speed075_Click(object s, RoutedEventArgs e) => SetSpeed("Speed075", 0.75);
    private void Speed100_Click(object s, RoutedEventArgs e) => SetSpeed("Speed100", 1.00);
    private void Speed150_Click(object s, RoutedEventArgs e) => SetSpeed("Speed150", 1.50);
    private void Speed200_Click(object s, RoutedEventArgs e) => SetSpeed("Speed200", 2.00);
    private void Speed400_Click(object s, RoutedEventArgs e) => SetSpeed("Speed400", 4.00);
    private void Speed800_Click(object s, RoutedEventArgs e) => SetSpeed("Speed800", 8.00);
    private void Speed1600_Click(object s, RoutedEventArgs e) => SetSpeed("Speed1600", 16.00);

    private void SetSpeed(string name, double mult)
    {
        _activePreset = name;
        _speedMultiplier = mult;
        if (SpeedSlider != null) SpeedSlider.Value = mult;
        if (SpeedBox != null) SpeedBox.Text = mult.ToString("F2", CultureInfo.InvariantCulture);
        UpdateSpeedButtons();
        UpdateCalculatedDuration();
        try { Player.SpeedRatio = Math.Clamp(_speedMultiplier, 0.25, 4.0); } catch { }
    }

    private void SpeedSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        _speedMultiplier = Math.Round(SpeedSlider.Value, 2);
        if (SpeedBox != null) SpeedBox.Text = _speedMultiplier.ToString("F2", CultureInfo.InvariantCulture);
        _activePreset = "Custom";
        UpdateSpeedButtons();
        UpdateCalculatedDuration();
        try { Player.SpeedRatio = Math.Clamp(_speedMultiplier, 0.25, 4.0); } catch { }
    }

    private void SpeedBox_LostFocus(object s, RoutedEventArgs e) => ApplySpeedBoxValue();
    private void SpeedBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplySpeedBoxValue();
    }

    private void ApplySpeedBoxValue()
    {
        if (!_isInitialized || SpeedBox == null) return;
        if (double.TryParse(SpeedBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) && v > 0.05)
        {
            _speedMultiplier = Math.Round(v, 2);
            if (SpeedSlider != null) SpeedSlider.Value = Math.Clamp(_speedMultiplier, 0.1, 16.0);
            _activePreset = "Custom";
            UpdateSpeedButtons();
            UpdateCalculatedDuration();
            try { Player.SpeedRatio = Math.Clamp(_speedMultiplier, 0.25, 4.0); } catch { }
        }
    }

    private void UpdateSpeedButtons()
    {
        var active = (Style)FindResource("ActiveToolButton");
        var ghost = (Style)FindResource("GhostButton");

        if (Speed025_Click != null && Speed025Btn != null) Speed025Btn.Style = _activePreset == "Speed025" ? active : ghost;
        if (Speed050Btn != null) Speed050Btn.Style = _activePreset == "Speed050" ? active : ghost;
        if (Speed075Btn != null) Speed075Btn.Style = _activePreset == "Speed075" ? active : ghost;
        if (Speed100Btn != null) Speed100Btn.Style = _activePreset == "Speed100" ? active : ghost;
        if (Speed150Btn != null) Speed150Btn.Style = _activePreset == "Speed150" ? active : ghost;
        if (Speed200Btn != null) Speed200Btn.Style = _activePreset == "Speed200" ? active : ghost;
        if (Speed400Btn != null) Speed400Btn.Style = _activePreset == "Speed400" ? active : ghost;
        if (Speed800Btn != null) Speed800Btn.Style = _activePreset == "Speed800" ? active : ghost;
        if (Speed1600Btn != null) Speed1600Btn.Style = _activePreset == "Speed1600" ? active : ghost;
    }

    private void UpdateCalculatedDuration()
    {
        if (_durationSeconds <= 0 || _speedMultiplier <= 0) return;
        double newSec = _durationSeconds / _speedMultiplier;
        if (HeaderNewDuration != null)
            HeaderNewDuration.Text = $"Result: {TimeSpan.FromSeconds(newSec):hh\\:mm\\:ss}";
    }

    // ── Output Management ─────────────────────────────────────────────────────
    private void BrowseOutput_Click(object s, RoutedEventArgs e)
    {
        var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Select Output Folder for Speed-Adjusted Video",
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
        try
        {
            string dir = !string.IsNullOrEmpty(_lastOutputFolder) ? _lastOutputFolder :
                         !string.IsNullOrEmpty(_customOutputFolder) ? _customOutputFolder :
                         Path.GetDirectoryName(_filePath) ?? "";
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Process.Start("explorer.exe", dir);
            else
                MessageBox.Show("Output folder not found or no video loaded yet.", "VideoFixPro", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

    // ── Execution Pipeline ────────────────────────────────────────────────────
    private async void Process_Click(object s, RoutedEventArgs e)
    {
            try
            {
        if (_isRendering) return;
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
        {
            MessageBox.Show("Please load a video file first.", "No File", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ExecuteSpeedProcessAsync();
    }
            catch (Exception ex)
            {
                MessageBox.Show("An unexpected error occurred: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    private void Cancel_Click(object s, RoutedEventArgs e)
    {
        _cts?.Cancel();
        try { _ffmpegProcess?.Kill(); } catch { }
    }

    private async Task ExecuteSpeedProcessAsync()
    {
        _isRendering = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        string dir = !string.IsNullOrEmpty(_customOutputFolder) ? _customOutputFolder : Path.GetDirectoryName(_filePath) ?? ".";
        string safeName = Path.GetFileNameWithoutExtension(_filePath);
        string ext = Path.GetExtension(_filePath);
        string speedTag = _speedMultiplier >= 1.0 ? $"{_speedMultiplier:F1}x_fast" : $"{1.0 / _speedMultiplier:F1}x_slow";
        string outputPath = Path.Combine(dir, $"{safeName}_{speedTag}{ext}");
        outputPath = GetUniqueFilePath(outputPath);
        _lastOutputFolder = dir;

        if (_durationSeconds <= 0)
        {
            await ProbeVideoAsync(_filePath);
            if (_durationSeconds <= 0) _durationSeconds = 1.0;
        }

        SetRenderingUI(true);
        SetStatus($"Processing speed {_speedMultiplier:F2}x...", "#388BFD");
        Log($"\n[SPEED] Source: {Path.GetFileName(_filePath)}");
        Log($"[SPEED] Multiplier: {_speedMultiplier:F2}x");
        Log($"[SPEED] Output: {outputPath}");

        double outDuration = _durationSeconds / _speedMultiplier;

        // Video Filter: setpts=(1/speed)*PTS
        double setptsFactor = 1.0 / _speedMultiplier;
        string vf = $"setpts={setptsFactor.ToString("F6", CultureInfo.InvariantCulture)}*PTS";

        // Audio Filter: chain of atempo filters (each between 0.5 and 2.0) or asetrate/aresample for non-pitch-preserved
        bool muteAudio = MuteAudioCheck?.IsChecked == true;
        bool preservePitch = PreservePitchCheck?.IsChecked == true;
        string? af = null;

        if (!muteAudio)
        {
            af = BuildAudioSpeedFilter(_speedMultiplier, preservePitch);
        }

        bool useGpu = (GpuCheck.IsChecked == true) && (_hasNvidia || _hasAmd || _hasIntel);
        bool isAv1 = Av1Check.IsChecked == true;
        string vCodecArgs;
        if (isAv1)
        {
            vCodecArgs = useGpu && _hasNvidia ? "-c:v av1_nvenc -preset p5 -cq 22 -pix_fmt yuv420p" :
                         useGpu && _hasAmd ? "-c:v av1_amf -qp_i 22 -qp_p 22 -qp_b 22 -pix_fmt yuv420p" :
                         useGpu && _hasIntel ? "-c:v av1_qsv -global_quality 22 -pix_fmt nv12" :
                         "-c:v libsvtav1 -preset 8 -crf 22 -pix_fmt yuv420p";
        }
        else
        {
            vCodecArgs = useGpu && _hasNvidia ? "-c:v h264_nvenc -pix_fmt yuv420p" :
                         useGpu && _hasAmd ? "-c:v h264_amf -pix_fmt yuv420p" :
                         useGpu && _hasIntel ? "-c:v h264_qsv -pix_fmt nv12" :
                         "-c:v libx264 -preset fast -crf 19 -pix_fmt yuv420p";
        }

        string audioArgs = muteAudio ? "-an" : af != null ? $"-af \"{af}\" -c:a aac -b:a 192k" : "-c:a aac -b:a 192k";

        string args = $"-y -i \"{_filePath}\" -vf \"{vf}\" -map 0:v -map 0:a? {vCodecArgs} {audioArgs} \"{outputPath}\"";
        Log($"[CMD] ffmpeg {args}");

        bool success = await RunFFmpegAsync(args, outDuration, _cts.Token);

        // Auto CPU Fallback if GPU fails
        if (!success && !_cts.Token.IsCancellationRequested && useGpu)
        {
            Log("[WARN] GPU encoding failed. Retrying on CPU (libx264)...");
            SetStatus("Retrying on CPU...", "#D29922");
            args = $"-y -i \"{_filePath}\" -vf \"{vf}\" -map 0:v -map 0:a? -c:v libx264 -preset fast -crf 19 -pix_fmt yuv420p {audioArgs} \"{outputPath}\"";
            Log($"[CMD Fallback] ffmpeg {args}");
            success = await RunFFmpegAsync(args, outDuration, _cts.Token);
        }

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
            SetStatus($"Done! Output: {finalMb:F1} MB", "#3FB950");
            Log($"[SUCCESS] Speed adjusted to {_speedMultiplier:F2}x ({outputPath})");
            ShowNotification("Speed Adjustment Complete", $"Saved: {Path.GetFileName(outputPath)}");
        }
        else
        {
            SetStatus("Speed render failed", "#F85149");
        }
    }

    private static string? BuildAudioSpeedFilter(double speed, bool preservePitch)
    {
        if (Math.Abs(speed - 1.0) < 0.001) return null;

        if (!preservePitch)
        {
            // Resample method: alters playback speed and shifts pitch naturally (like classic tape/vinyl)
            int sampleRate = 44100;
            int newRate = (int)Math.Round(sampleRate * speed);
            return $"asetrate={newRate},aresample={sampleRate}";
        }

        var filters = new List<string>();
        double current = speed;

        if (current > 1.0)
        {
            while (current > 2.0)
            {
                filters.Add("atempo=2.0");
                current /= 2.0;
            }
            if (current > 1.001)
                filters.Add($"atempo={current.ToString("F4", CultureInfo.InvariantCulture)}");
        }
        else
        {
            while (current < 0.5)
            {
                filters.Add("atempo=0.5");
                current /= 0.5;
            }
            if (current < 0.999)
                filters.Add($"atempo={current.ToString("F4", CultureInfo.InvariantCulture)}");
        }

        return filters.Count > 0 ? string.Join(",", filters) : null;
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
        if (_hasNvidia) GpuHelper.InjectNvCudaPath(psi);

        var tcs = new TaskCompletionSource<bool>();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _ffmpegProcess = proc;

        var timeRegex = new Regex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            string line = e.Data;
            Dispatcher.Invoke(() => Log(line));

            var match = timeRegex.Match(line);
            if (match.Success && totalDuration > 0)
            {
                if (double.TryParse(match.Groups[1].Value, out double h) &&
                    double.TryParse(match.Groups[2].Value, out double m) &&
                    double.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double s))
                {
                    double curSec = h * 3600 + m * 60 + s;
                    double percent = Math.Clamp((curSec / totalDuration) * 100.0, 0, 99.5);
                    Dispatcher.Invoke(() => SetRenderProgress(percent));
                }
            }
        };

        proc.Exited += (_, _) =>
        {
            ProcessGuard.Unwatch(proc);
            tcs.TrySetResult(proc.ExitCode == 0);
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
        finally
        {
            ProcessGuard.Unwatch(proc);
        }
    }

    private void SetRenderingUI(bool rendering)
    {
        if (RenderProgressPanel != null) RenderProgressPanel.Visibility = rendering ? Visibility.Visible : Visibility.Collapsed;
        if (CancelBtn != null) CancelBtn.Visibility = rendering ? Visibility.Visible : Visibility.Collapsed;
        if (ProcessBtn != null) ProcessBtn.IsEnabled = !rendering;
        if (OpenFolderBtn != null) OpenFolderBtn.IsEnabled = !rendering && (!string.IsNullOrEmpty(_lastOutputFolder) || !string.IsNullOrEmpty(_filePath) || !string.IsNullOrEmpty(_customOutputFolder));

        if (TaskbarProgress != null)
            TaskbarProgress.ProgressState = rendering ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.None;

        if (rendering) SetRenderProgress(0);
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
