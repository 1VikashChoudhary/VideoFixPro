using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using WinForms = System.Windows.Forms;

namespace VideoFixPro;

public partial class CompressorWindow : Window
{
    private bool _isInitialized;
    private string _filePath = string.Empty;
    private double _durationSeconds;
    private long _originalSizeBytes;
    private int _sourceWidth;
    private int _sourceHeight;
    private string _videoCodec = "-";
    private string _audioCodec = "-";

    private double _targetSizeMb = 25.0;
    private string _activePreset = "Discord25";

    // Player state
    private readonly System.Windows.Threading.DispatcherTimer _playheadTimer = new();
    private bool _isPlayerPlaying;
    private bool _isSeeking;

    // Rendering & Progress
    private CancellationTokenSource? _cts;
    private Process? _ffmpegProcess;
    private bool _isRendering;
    private DateTime _renderStartTime;
    private string _customOutputFolder = string.Empty;
    private string _lastOutputFolder = string.Empty;

    // Hardware encoder support
    private bool _hasNvidia;
    private bool _hasAmd;
    private bool _hasIntel;

    private static string AppDir  => AppDomain.CurrentDomain.BaseDirectory;
    private static string FFmpeg  => GetBinPath("ffmpeg.exe");
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

    public CompressorWindow(string? preloadPath = null, bool hasNvidia = false, bool hasAmd = false, bool hasIntel = false)
    {
        InitializeComponent();
        Loaded += (_, _) => UiTextSanitizer.Apply(this);
        _hasNvidia = hasNvidia;
        _hasAmd = hasAmd;
        _hasIntel = hasIntel;

        GpuCheck.IsChecked = _hasNvidia || _hasAmd || _hasIntel;
        _ = DetectGpuAsync();

        _playheadTimer.Interval = TimeSpan.FromMilliseconds(50);
        _playheadTimer.Tick += (_, _) => { if (!_isSeeking) UpdateSeekFromPlayer(); };

        _isInitialized = true;

        if (!string.IsNullOrEmpty(preloadPath) && File.Exists(preloadPath))
            _ = LoadFileAsync(preloadPath);
    }

    private async Task DetectGpuAsync()
    {
        if (_hasNvidia || _hasAmd || _hasIntel) return;
        try
        {
            if (File.Exists(FFmpeg))
            {
                if (await TestHardwareEncoderAsync("h264_nvenc")) _hasNvidia = true;
                if (await TestHardwareEncoderAsync("h264_amf")) _hasAmd = true;
                if (await TestHardwareEncoderAsync("h264_qsv")) _hasIntel = true;
            }
        }
        catch { }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                if (GpuCheck != null)
                {
                    GpuCheck.IsChecked = _hasNvidia || _hasAmd || _hasIntel;
                    GpuCheck.ToolTip = _hasNvidia ? "NVIDIA NVENC active" :
                                      _hasAmd ? "AMD AMF active" :
                                      _hasIntel ? "Intel QuickSync active" : "GPU acceleration (libx264 fallback)";
                }
            });
        }
    }

    private static string? _nvCudaDir;
    private static bool _nvCudaDirSearched;

    private static string? FindNvCudaDir()
    {
        if (_nvCudaDirSearched) return _nvCudaDir;
        _nvCudaDirSearched = true;
        try
        {
            var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcuda.dll");
            if (File.Exists(sys32)) { _nvCudaDir = Path.GetDirectoryName(sys32); return _nvCudaDir; }

            var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrEmpty(cudaPath))
            {
                var cudaBin = Path.Combine(cudaPath, "bin", "nvcuda.dll");
                if (File.Exists(cudaBin)) { _nvCudaDir = Path.GetDirectoryName(cudaBin); return _nvCudaDir; }
            }

            var driverStore = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                           "System32", "DriverStore", "FileRepository");
            if (Directory.Exists(driverStore))
            {
                foreach (var pattern in new[] { "nv_disp*", "nvdsp*", "nvlt*", "nvmi*" })
                    foreach (var dir in Directory.GetDirectories(driverStore, pattern, SearchOption.TopDirectoryOnly))
                        foreach (var name in new[] { "nvcuda64.dll", "nvcuda.dll" })
                            if (File.Exists(Path.Combine(dir, name))) { _nvCudaDir = dir; return _nvCudaDir; }
            }

            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                       Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                var nvDir = Path.Combine(pf, "NVIDIA Corporation");
                if (Directory.Exists(nvDir))
                    try { foreach (var f in Directory.GetFiles(nvDir, "nvcuda*.dll", SearchOption.AllDirectories))
                        { _nvCudaDir = Path.GetDirectoryName(f); return _nvCudaDir; } } catch { }
            }
        }
        catch { }
        return null;
    }

    private static void InjectNvCudaPath(ProcessStartInfo psi)
    {
        var nvDir = FindNvCudaDir();
        if (nvDir != null)
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = nvDir + ";" + currentPath;
        }
    }

    private static async Task<bool> TestHardwareEncoderAsync(string encoder)
    {
        try
        {
            string pixFmt = encoder == "h264_qsv" ? "-pix_fmt nv12" : "-pix_fmt yuv420p";
            var psi = new ProcessStartInfo
            {
                FileName = FFmpeg,
                Arguments = $"-v error -f lavfi -i color=c=black:s=320x240:d=0.04 {pixFmt} -c:v {encoder} -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            if (encoder.Contains("nvenc")) InjectNvCudaPath(psi);

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            ProcessGuard.Watch(proc);
            try
            {
                await proc.WaitForExitAsync();
                return proc.ExitCode == 0;
            }
            finally
            {
                ProcessGuard.Unwatch(proc);
            }
        }
        catch { return false; }
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
            Title = "Select Video to Compress",
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
        _originalSizeBytes = 0;

        if (TitleFileName != null) TitleFileName.Text = "No file loaded";
        if (DropZone != null) DropZone.Visibility = Visibility.Visible;
        if (FileHeader != null) FileHeader.Visibility = Visibility.Collapsed;
        if (PlayerBorder != null) PlayerBorder.Visibility = Visibility.Collapsed;
        if (SeekPanel != null) SeekPanel.Visibility = Visibility.Collapsed;
        SetStatus("Ready", "#8B949E");
    }

    // ── Load & Probe ──────────────────────────────────────────────────────────
    private async Task LoadFileAsync(string path)
    {
        if (!File.Exists(path)) return;
        _filePath = path;

        var fi = new FileInfo(path);
        _originalSizeBytes = fi.Length;
        double origMb = _originalSizeBytes / (1024.0 * 1024.0);

        if (TitleFileName != null) TitleFileName.Text = Path.GetFileName(path);
        if (HeaderFileName != null) HeaderFileName.Text = Path.GetFileName(path);
        if (HeaderOriginalSize != null) HeaderOriginalSize.Text = $"{origMb:F1} MB";

        // Setup default target size (e.g. 25MB or 50% of file if file is < 25MB)
        if (origMb < 25.0)
        {
            _targetSizeMb = Math.Max(1.0, Math.Round(origMb * 0.6, 1));
        }
        else
        {
            _targetSizeMb = 25.0;
        }

        if (TargetSizeSlider != null) TargetSizeSlider.Value = _targetSizeMb;
        if (TargetSizeBox != null) TargetSizeBox.Text = _targetSizeMb.ToString("F1", CultureInfo.InvariantCulture);

        if (DropZone != null) DropZone.Visibility = Visibility.Collapsed;
        if (FileHeader != null) FileHeader.Visibility = Visibility.Visible;
        if (PlayerBorder != null) PlayerBorder.Visibility = Visibility.Visible;
        if (SeekPanel != null) SeekPanel.Visibility = Visibility.Visible;

        SetStatus($"Loading {Path.GetFileName(path)}...", "#388BFD");

        try
        {
            Player.Source = new Uri(path);
            Player.Play();
            Player.Pause();
            _isPlayerPlaying = false;
            if (SeekPlayBtn != null) SeekPlayBtn.Content = "▶";
            if (PlayPauseBtn != null) PlayPauseBtn.Content = "▶";
        }
        catch { }

        await ProbeVideoAsync(path);
        RecalculateBitrates();
        SetStatus($"Loaded: {Path.GetFileName(path)} ({origMb:F1} MB)", "#3FB950");
    }

    private async Task ProbeVideoAsync(string path)
    {
        if (!File.Exists(FFprobe)) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFprobe,
                Arguments = $"-v error -show_entries format=duration:stream=width,height,codec_name,codec_type -of default=noprint_wrappers=1 \"{path}\"",
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
                else if (k == "codec_name")
                {
                    if (_videoCodec == "-") _videoCodec = v.ToUpperInvariant();
                    else if (_audioCodec == "-") _audioCodec = v.ToUpperInvariant();
                }
            }

            Dispatcher.Invoke(() =>
            {
                if (HeaderDuration != null) HeaderDuration.Text = TimeSpan.FromSeconds(_durationSeconds).ToString(@"hh\:mm\:ss");
                if (HeaderResolution != null) HeaderResolution.Text = $"{_sourceWidth}x{_sourceHeight}";
                if (_durationSeconds > 0 && _originalSizeBytes > 0)
                {
                    double avgKbps = (_originalSizeBytes * 8.0 / _durationSeconds) / 1000.0;
                    if (HeaderBitrate != null) HeaderBitrate.Text = $"~{(int)avgKbps} kbps";
                }
            });
        }
        catch { }
    }

    // ── Player Controls ───────────────────────────────────────────────────────
    private void Player_MediaOpened(object s, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan)
        {
            double sec = Player.NaturalDuration.TimeSpan.TotalSeconds;
            if (sec > 0 && _durationSeconds <= 0) _durationSeconds = sec;
        }
        RecalculateBitrates();
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

    // ── Target Size & Bitrate Math ────────────────────────────────────────────
    private void PresetDiscord8_Click(object s, RoutedEventArgs e) => SetPreset("Discord8", 8.0);
    private void PresetDiscord25_Click(object s, RoutedEventArgs e) => SetPreset("Discord25", 25.0);
    private void PresetEmail10_Click(object s, RoutedEventArgs e) => SetPreset("Email10", 10.0);
    private void PresetWhatsApp64_Click(object s, RoutedEventArgs e) => SetPreset("WhatsApp64", 64.0);
    private void PresetNitro100_Click(object s, RoutedEventArgs e) => SetPreset("Nitro100", 100.0);

    private void PresetReduce25_Click(object s, RoutedEventArgs e)
    {
        if (_originalSizeBytes <= 0) return;
        double curMb = _originalSizeBytes / (1024.0 * 1024.0);
        SetPreset("Reduce25", Math.Max(1.0, Math.Round(curMb * 0.75, 1)));
    }
    private void PresetReduce50_Click(object s, RoutedEventArgs e)
    {
        if (_originalSizeBytes <= 0) return;
        double curMb = _originalSizeBytes / (1024.0 * 1024.0);
        SetPreset("Reduce50", Math.Max(1.0, Math.Round(curMb * 0.50, 1)));
    }
    private void PresetReduce75_Click(object s, RoutedEventArgs e)
    {
        if (_originalSizeBytes <= 0) return;
        double curMb = _originalSizeBytes / (1024.0 * 1024.0);
        SetPreset("Reduce75", Math.Max(1.0, Math.Round(curMb * 0.25, 1)));
    }

    private void SetPreset(string name, double mb)
    {
        _activePreset = name;
        _targetSizeMb = mb;
        if (TargetSizeSlider != null) TargetSizeSlider.Value = mb;
        if (TargetSizeBox != null) TargetSizeBox.Text = mb.ToString("F1", CultureInfo.InvariantCulture);
        UpdatePresetButtons();
        RecalculateBitrates();
    }

    private void TargetSizeSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        _targetSizeMb = Math.Round(TargetSizeSlider.Value, 1);
        if (TargetSizeBox != null) TargetSizeBox.Text = _targetSizeMb.ToString("F1", CultureInfo.InvariantCulture);
        _activePreset = "Custom";
        UpdatePresetButtons();
        RecalculateBitrates();
    }

    private void TargetSizeBox_LostFocus(object s, RoutedEventArgs e) => ApplyTargetBoxValue();
    private void TargetSizeBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyTargetBoxValue();
    }

    private void ApplyTargetBoxValue()
    {
        if (!_isInitialized || TargetSizeBox == null) return;
        if (double.TryParse(TargetSizeBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) && v > 0)
        {
            _targetSizeMb = Math.Round(v, 1);
            if (TargetSizeSlider != null) TargetSizeSlider.Value = Math.Clamp(_targetSizeMb, 1, 500);
            _activePreset = "Custom";
            UpdatePresetButtons();
            RecalculateBitrates();
        }
    }

    private void UpdatePresetButtons()
    {
        var active = (Style)FindResource("ActiveToolButton");
        var ghost = (Style)FindResource("GhostButton");

        if (PresetDiscord8Btn != null) PresetDiscord8Btn.Style = _activePreset == "Discord8" ? active : ghost;
        if (PresetDiscord25Btn != null) PresetDiscord25Btn.Style = _activePreset == "Discord25" ? active : ghost;
        if (PresetEmail10Btn != null) PresetEmail10Btn.Style = _activePreset == "Email10" ? active : ghost;
        if (PresetWhatsApp64Btn != null) PresetWhatsApp64Btn.Style = _activePreset == "WhatsApp64" ? active : ghost;
        if (PresetNitro100Btn != null) PresetNitro100Btn.Style = _activePreset == "Nitro100" ? active : ghost;
    }

    private void ResolutionBox_SelectionChanged(object s, SelectionChangedEventArgs e) => RecalculateBitrates();

    private void RecalculateBitrates()
    {
        if (!_isInitialized || _durationSeconds <= 0) return;

        // Reserve 4% overhead for MP4 container / moov atom safety
        double safeTargetBytes = _targetSizeMb * 1024.0 * 1024.0 * 0.96;
        double totalKbps = (safeTargetBytes * 8.0 / _durationSeconds) / 1000.0;

        int audioKbps = 128;
        if (totalKbps < 500) audioKbps = 96;
        if (totalKbps < 300) audioKbps = 64;

        int videoKbps = Math.Max(50, (int)(totalKbps - audioKbps));

        if (CalcVideoBitrateText != null) CalcVideoBitrateText.Text = $"~{videoKbps} kbps";
        if (CalcAudioBitrateText != null) CalcAudioBitrateText.Text = $"{audioKbps} kbps (AAC)";
    }

    // ── Output Path & Dialogs ─────────────────────────────────────────────────
    private void BrowseOutput_Click(object s, RoutedEventArgs e)
    {
        var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Select Output Folder for Compressed Video",
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

    // ── Compression Pipeline Execution ────────────────────────────────────────
    private async void Compress_Click(object s, RoutedEventArgs e)
    {
        if (_isRendering) return;
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
        {
            MessageBox.Show("Please load a video file first.", "No File", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_durationSeconds <= 0)
        {
            MessageBox.Show("Could not determine video duration for size calculations.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await ExecuteCompressionAsync();
    }

    private void Cancel_Click(object s, RoutedEventArgs e)
    {
        _cts?.Cancel();
        try { _ffmpegProcess?.Kill(); } catch { }
    }

    private async Task ExecuteCompressionAsync()
    {
        _isRendering = true;
        _renderStartTime = DateTime.Now;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        string dir = !string.IsNullOrEmpty(_customOutputFolder) ? _customOutputFolder : Path.GetDirectoryName(_filePath) ?? ".";
        string safeName = Path.GetFileNameWithoutExtension(_filePath);
        string outputPath = Path.Combine(dir, $"{safeName}_compressed_{_targetSizeMb:F0}MB.mp4");
        outputPath = GetUniqueFilePath(outputPath);
        _lastOutputFolder = dir;

        if (_durationSeconds <= 0)
        {
            await ProbeVideoAsync(_filePath);
            if (_durationSeconds <= 0) _durationSeconds = 1.0;
        }

        SetRenderingUI(true);
        SetStatus($"Compressing to {_targetSizeMb:F1} MB...", "#388BFD");
        Log($"\n[COMPRESS] Source: {Path.GetFileName(_filePath)} ({_originalSizeBytes / (1024.0 * 1024.0):F1} MB)");
        Log($"[COMPRESS] Target Size: {_targetSizeMb:F1} MB");
        Log($"[COMPRESS] Output: {outputPath}");

        // Bitrates
        double safeTargetBytes = _targetSizeMb * 1024.0 * 1024.0 * 0.96;
        double totalKbps = (safeTargetBytes * 8.0 / _durationSeconds) / 1000.0;
        int audioKbps = totalKbps < 300 ? 64 : totalKbps < 500 ? 96 : 128;
        int videoKbps = Math.Max(50, (int)(totalKbps - audioKbps));

        // Resolution Scaling Filter
        string? vf = BuildResolutionFilter(videoKbps);
        bool useGpu = (GpuCheck.IsChecked == true) && (_hasNvidia || _hasAmd || _hasIntel);
        bool twoPass = (EncodingModeBox.SelectedIndex == 0) && !useGpu; // 2-pass is CPU only in FFmpeg

        bool success = false;

        if (twoPass)
        {
            // Pass 1
            Log("[PASS 1/2] Analyzing video bitrate distribution...");
            SetStatus("Pass 1 of 2 (Analyzing)...", "#388BFD");
            string passLogPrefix = Path.Combine(Path.GetTempPath(), $"vfp_pass_{Guid.NewGuid():N}");
            string pass1Args = $"-y -i \"{_filePath}\" {(vf != null ? $"-vf \"{vf}\" " : "")}-c:v libx264 -b:v {videoKbps}k -pass 1 -passlogfile \"{passLogPrefix}\" -an -f null NUL";
            Log($"[CMD Pass 1] ffmpeg {pass1Args}");
            bool p1 = await RunFFmpegAsync(pass1Args, _durationSeconds, _cts.Token, pass: 1);

            if (p1 && !_cts.Token.IsCancellationRequested)
            {
                // Pass 2
                Log("[PASS 2/2] Encoding optimized frames...");
                SetStatus("Pass 2 of 2 (Encoding)...", "#388BFD");
                string pass2Args = $"-y -i \"{_filePath}\" {(vf != null ? $"-vf \"{vf}\" " : "")}-map 0:v -map 0:a? -c:v libx264 -b:v {videoKbps}k -pass 2 -passlogfile \"{passLogPrefix}\" -c:a aac -b:a {audioKbps}k \"{outputPath}\"";
                Log($"[CMD Pass 2] ffmpeg {pass2Args}");
                success = await RunFFmpegAsync(pass2Args, _durationSeconds, _cts.Token, pass: 2);
            }

            // Cleanup pass log files
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(passLogPrefix) + "*"))
                    File.Delete(f);
            }
            catch { }
        }
        else
        {
            // 1-Pass Mode (with GPU acceleration if enabled)
            string vCodecArgs = useGpu && _hasNvidia ? $"-c:v h264_nvenc -b:v {videoKbps}k -pix_fmt yuv420p" :
                                useGpu && _hasAmd ? $"-c:v h264_amf -b:v {videoKbps}k -pix_fmt yuv420p" :
                                useGpu && _hasIntel ? $"-c:v h264_qsv -b:v {videoKbps}k -pix_fmt nv12" :
                                $"-c:v libx264 -preset fast -b:v {videoKbps}k -pix_fmt yuv420p";

            string args = $"-y -i \"{_filePath}\" {(vf != null ? $"-vf \"{vf}\" " : "")}-map 0:v -map 0:a? {vCodecArgs} -c:a aac -b:a {audioKbps}k \"{outputPath}\"";
            Log($"[CMD] ffmpeg {args}");
            success = await RunFFmpegAsync(args, _durationSeconds, _cts.Token, pass: 0);

            // CPU Fallback if GPU fails
            if (!success && !_cts.Token.IsCancellationRequested && useGpu)
            {
                Log("[WARN] GPU encoder failed. Retrying on CPU (libx264)...");
                SetStatus("Retrying on CPU...", "#D29922");
                args = $"-y -i \"{_filePath}\" {(vf != null ? $"-vf \"{vf}\" " : "")}-map 0:v -map 0:a? -c:v libx264 -preset fast -b:v {videoKbps}k -c:a aac -b:a {audioKbps}k -pix_fmt yuv420p \"{outputPath}\"";
                Log($"[CMD Fallback] ffmpeg {args}");
                success = await RunFFmpegAsync(args, _durationSeconds, _cts.Token, pass: 0);
            }
        }

        _isRendering = false;
        SetRenderingUI(false);

        if (_cts.Token.IsCancellationRequested)
        {
            SetStatus("Cancelled", "#D29922");
            Log("[COMPRESS] Operation cancelled.");
            try { File.Delete(outputPath); } catch { }
        }
        else if (success && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
        {
            var finalFi = new FileInfo(outputPath);
            double finalMb = finalFi.Length / (1024.0 * 1024.0);
            SetRenderProgress(100);
            SetStatus($"Done! Output: {finalMb:F1} MB", "#3FB950");
            Log($"[COMPRESS] Success! Output Size: {finalMb:F2} MB ({outputPath})");
            ShowNotification("Video Compressor Complete", $"Saved: {Path.GetFileName(outputPath)} ({finalMb:F1} MB)");
        }
        else
        {
            SetStatus("Compression failed", "#F85149");
            Log("[COMPRESS] Failed. Check log for details.");
        }
    }

    private string? BuildResolutionFilter(int videoKbps)
    {
        int sel = ResolutionBox?.SelectedIndex ?? 0;
        int targetH = -2;

        if (sel == 0) // Auto
        {
            if (videoKbps < 350 && _sourceHeight > 480) { targetH = 480; }
            else if (videoKbps < 850 && _sourceHeight > 720) { targetH = 720; }
            else if (videoKbps < 1800 && _sourceHeight > 1080) { targetH = 1080; }
            else return null;
        }
        else if (sel == 2) targetH = 1080;
        else if (sel == 3) targetH = 720;
        else if (sel == 4) targetH = 480;
        else return null;

        return $"scale=-2:{targetH}";
    }

    private async Task<bool> RunFFmpegAsync(string args, double totalDuration, CancellationToken token, int pass = 0)
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
        if (_hasNvidia) InjectNvCudaPath(psi);

        var tcs = new TaskCompletionSource<bool>();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
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
                    if (pass == 1) percent *= 0.5;
                    else if (pass == 2) percent = 50.0 + (percent * 0.5);

                    Dispatcher.Invoke(() => SetRenderProgress(percent));
                }
            }
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
        if (CompressBtn != null) CompressBtn.IsEnabled = !rendering;
        if (OpenFolderBtn != null) OpenFolderBtn.IsEnabled = !rendering && !string.IsNullOrEmpty(_lastOutputFolder);

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
