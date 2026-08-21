using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using VideoFixPro.Models;
using Path = System.IO.Path;
using WinForms = System.Windows.Forms;

namespace VideoFixPro;

// ─────────────────────────────────────────────────────────────────────────────
//  VideoToolboxWindow  –  Live Interactive Video Editing Toolbox
// ─────────────────────────────────────────────────────────────────────────────
public partial class VideoToolboxWindow : Window
{
    private bool _isInitialized;

    // ── State ─────────────────────────────────────────────────────────────────
    private string _filePath = string.Empty;
    private double _durationSeconds;
    private string _videoCodec = "-";
    private string _audioCodec = "-";
    private int    _audioChannels;

    private readonly VideoToolboxSettings _settings = new();

    // player & live transforms
    private readonly System.Windows.Threading.DispatcherTimer _playheadTimer = new();
    private bool _isPlayerPlaying;
    private bool _isSeeking;

    // timeline & filmstrip
    private const int FilmstripBuckets = 12;
    private readonly BitmapImage?[] _filmImages = new BitmapImage?[FilmstripBuckets];
    private CancellationTokenSource? _thumbCts;

    // render & telemetry
    private CancellationTokenSource? _cts;
    private Process? _ffmpegProcess;
    private bool _isRendering;
    private DateTime _renderStartTime;

    // output
    private string _customOutputFolder = string.Empty;
    private string _lastOutputFolder = string.Empty;

    // GPU support
    private bool _hasNvidia;
    private bool _hasAmd;
    private bool _hasIntel;

    // ── FFmpeg paths ──────────────────────────────────────────────────────────
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

    private static bool IsFolderWritable(string p)
    {
        try { var t = Path.Combine(p, "__write_test__"); File.WriteAllText(t, ""); File.Delete(t); return true; }
        catch { return false; }
    }

    private static readonly HashSet<string> VideoExts =
        new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".m2ts" };

    // ── Constructor ──────────────────────────────────────────────────────────
    public VideoToolboxWindow(string? preloadPath = null, bool hasNvidia = false, bool hasAmd = false, bool hasIntel = false)
    {
        InitializeComponent();
        Loaded += (_, _) => UiTextSanitizer.Apply(this);
        _hasNvidia = hasNvidia;
        _hasAmd = hasAmd;
        _hasIntel = hasIntel;

        OutputFormatBox.ItemsSource = new[] { "MP4", "MP4 (AV1)", "MKV", "AVI", "MOV", "WebM" };
        OutputFormatBox.SelectedIndex = 0;

        QualitySlider.ValueChanged += (_, _) =>
        {
            if (QualityText != null)
                QualityText.Text = $"{(int)QualitySlider.Value}%";
        };

        // Enable checkbox and run live GPU hardware detection
        GpuCheck.IsEnabled = true;
        GpuCheck.IsChecked = _hasNvidia || _hasAmd || _hasIntel;
        _ = DetectGpuAsync();

        _playheadTimer.Interval = TimeSpan.FromMilliseconds(40);
        _playheadTimer.Tick += (_, _) => { if (!_isSeeking) UpdateSeekFromPlayer(); };

        _isInitialized = true;

        if (!string.IsNullOrEmpty(preloadPath) && File.Exists(preloadPath))
            _ = LoadFileAsync(preloadPath);
    }

    private async Task DetectGpuAsync()
    {
        if (!File.Exists(FFmpeg)) return;

        try
        {
            var encoders = await RunProcessAsync(FFmpeg, "-v quiet -encoders");
            bool nvencCompiled = encoders.Contains("h264_nvenc");
            bool amfCompiled   = encoders.Contains("h264_amf");
            bool qsvCompiled   = encoders.Contains("h264_qsv");

            // Hardware functional tests
            _hasNvidia = nvencCompiled && await TestHardwareEncoderAsync("h264_nvenc");
            _hasAmd    = amfCompiled && await TestHardwareEncoderAsync("h264_amf");
            _hasIntel  = qsvCompiled && await TestHardwareEncoderAsync("h264_qsv");

            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.Invoke(() =>
                {
                    if (GpuCheck != null)
                    {
                        if (_hasNvidia)
                        {
                            GpuCheck.Content = "GPU (Nvidia NVENC)";
                            GpuCheck.IsEnabled = true;
                            GpuCheck.IsChecked = true;
                        }
                        else if (_hasAmd)
                        {
                            GpuCheck.Content = "GPU (AMD AMF)";
                            GpuCheck.IsEnabled = true;
                            GpuCheck.IsChecked = true;
                        }
                        else if (_hasIntel)
                        {
                            GpuCheck.Content = "GPU (Intel QSV)";
                            GpuCheck.IsEnabled = true;
                            GpuCheck.IsChecked = true;
                        }
                        else
                        {
                            GpuCheck.Content = "GPU Acceleration";
                            GpuCheck.IsEnabled = true;
                            GpuCheck.IsChecked = false;
                        }
                    }
                });
            }
        }
        catch { }
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
                CreateNoWindow = true
            };
            if (encoder.Contains("nvenc")) GpuHelper.InjectNvCudaPath(psi);

            using var p = Process.Start(psi);
            if (p == null) return false;
            ProcessGuard.Watch(p);
            try
            {
                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            finally
            {
                ProcessGuard.Unwatch(p);
            }
        }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  TITLE BAR
    // ═══════════════════════════════════════════════════════════════════════════
    private void TitleBar_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (e.ClickCount == 2) MaximizeRestore();
        else DragMove();
    }
    private void MinBtn_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxBtn_Click(object s, RoutedEventArgs e) => MaximizeRestore();
    private void CloseBtn_Click(object s, RoutedEventArgs e) { Close(); }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        CancelRender();
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _playheadTimer?.Stop();
        try { Player.Source = null; } catch { }
        base.OnClosing(e);
    }

    private void MaximizeRestore()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            if (MaxBtn != null) MaxBtn.Content = "[ ]";
        }
        else
        {
            WindowState = WindowState.Maximized;
            if (MaxBtn != null) MaxBtn.Content = "❐";
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  FILE LOADING
    // ═══════════════════════════════════════════════════════════════════════════
    private void Window_DragEnter(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object s, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var file = paths.FirstOrDefault(f => File.Exists(f) && VideoExts.Contains(Path.GetExtension(f)));
        if (file != null) _ = LoadFileAsync(file);
    }

    private void DropZone_Click(object s, MouseButtonEventArgs e) => BrowseFile();
    private void ChangeFile_Click(object s, RoutedEventArgs e) => BrowseFile();

    private void BrowseFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Video File",
            Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.ts;*.m2ts|All Files|*.*"
        };
        if (dlg.ShowDialog() == true)
            _ = LoadFileAsync(dlg.FileName);
    }

    private void RemoveFile_Click(object s, RoutedEventArgs e)
    {
        _thumbCts?.Cancel();
        _filePath = string.Empty;
        _durationSeconds = 0;
        _settings.Reset();
        try { Player.Source = null; } catch { }
        _playheadTimer.Stop();
        _isPlayerPlaying = false;

        DropZone.Visibility = Visibility.Visible;
        FileHeader.Visibility = Visibility.Collapsed;
        PlayerBorder.Visibility = Visibility.Collapsed;
        SeekPanel.Visibility = Visibility.Collapsed;
        TimelineTrackBorder.Visibility = Visibility.Collapsed;
        EditSummaryBorder.Visibility = Visibility.Collapsed;
        TitleFileName.Text = "No file loaded";
        ApplyLivePreview();
        UpdateAllButtonStyles();
        SetStatus("Ready");
    }

    private async Task LoadFileAsync(string path)
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();

        _filePath = path;
        _settings.Reset();
        SetStatus("Loading...", "#388BFD");

        TitleFileName.Text = Path.GetFileName(path);
        HeaderFileName.Text = Path.GetFileName(path);

        // Show preview panels
        DropZone.Visibility = Visibility.Collapsed;
        FileHeader.Visibility = Visibility.Visible;
        PlayerBorder.Visibility = Visibility.Visible;
        SeekPanel.Visibility = Visibility.Visible;
        TimelineTrackBorder.Visibility = Visibility.Visible;
        EditSummaryBorder.Visibility = Visibility.Visible;
        PlayPauseBtn.Visibility = Visibility.Visible;

        // Reset filmstrip images
        for (int i = 0; i < FilmstripBuckets; i++) _filmImages[i] = null;
        DrawFilmstrip();
        DrawWaveform();

        // Load in MediaElement
        try
        {
            Player.Source = new Uri(path);
            Player.Play();
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Failed to load video preview: {ex.Message}");
        }

        // Probe with ffprobe
        await ProbeFileAsync(path);

        // Generate live timeline filmstrip in background
        if (_durationSeconds > 0)
            _ = GenerateFilmstripAsync(path, _durationSeconds, _thumbCts.Token);

        ApplyLivePreview();
        UpdateAllButtonStyles();
        if (OpenFolderBtn != null) OpenFolderBtn.IsEnabled = true;
        SetStatus("Ready", "#3FB950");
    }

    private async Task ProbeFileAsync(string path)
    {
        if (!File.Exists(FFprobe)) return;
        try
        {
            var output = await RunProcessAsync(FFprobe,
                $"-v quiet -print_format json -show_streams -show_format \"{path}\"");
            var root = JsonNode.Parse(output);

            // Format duration
            if (root?["format"]?["duration"]?.GetValue<string>() is string durStr &&
                double.TryParse(durStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double dur))
            {
                _durationSeconds = dur;
                if (SeekSlider != null) SeekSlider.Maximum = dur;
                if (HeaderDuration != null) HeaderDuration.Text = FormatTime(dur);
            }

            var streams = root?["streams"]?.AsArray();
            if (streams != null)
            {
                foreach (var stream in streams)
                {
                    var codecType = stream?["codec_type"]?.GetValue<string>();
                    if (codecType == "video" && _videoCodec == "-")
                    {
                        _videoCodec = stream?["codec_name"]?.GetValue<string>()?.ToUpperInvariant() ?? "-";
                        int w = stream?["width"]?.GetValue<int>() ?? 0;
                        int h = stream?["height"]?.GetValue<int>() ?? 0;
                        _settings.SourceWidth = w;
                        _settings.SourceHeight = h;
                        if (HeaderCodec != null) HeaderCodec.Text = _videoCodec;
                        if (HeaderResolution != null) HeaderResolution.Text = w > 0 ? $"{w}x{h}" : "-";
                    }
                    else if (codecType == "audio" && _audioCodec == "-")
                    {
                        _audioCodec = stream?["codec_name"]?.GetValue<string>()?.ToUpperInvariant() ?? "-";
                        _audioChannels = stream?["channels"]?.GetValue<int>() ?? 2;
                        if (HeaderAudioInfo != null) HeaderAudioInfo.Text = $"{_audioCodec} {(_audioChannels >= 2 ? "Stereo" : "Mono")}";
                    }
                }
            }

            Log($"[INFO] Loaded: {Path.GetFileName(path)} | {HeaderDuration?.Text} | {_videoCodec} | {HeaderResolution?.Text} | {HeaderAudioInfo?.Text}");
        }
        catch (Exception ex)
        {
            Log($"[WARN] Probe failed: {ex.Message}");
        }
    }

    private bool _isCroppedLivePreview = false;

    // ═══════════════════════════════════════════════════════════════════════════
    //  LIVE REAL-TIME PREVIEW & TRANSFORMS
    // ═══════════════════════════════════════════════════════════════════════════
    private void ApplyLivePreview()
    {
        if (!_isInitialized || Player == null) return;

        try
        {
            // 1. Live Video Transform (Rotation, Flips, and Live Cropped Zoom)
            if (PlayerRotate != null) PlayerRotate.Angle = _settings.RotationDegrees;
            if (PlayerScale != null && PlayerTranslate != null)
            {
                double pw = PlayerBorder?.ActualWidth ?? 0;
                double ph = PlayerBorder?.ActualHeight ?? 0;
                double fitScale = 1.0;
                if ((_settings.RotationDegrees == 90 || _settings.RotationDegrees == 270) && pw > 0 && ph > 0)
                {
                    fitScale = Math.Min(pw / ph, ph / pw);
                }

                if (_isCroppedLivePreview && _settings.SourceWidth > 0 && _settings.SourceHeight > 0 &&
                    (_settings.CropLeft > 0 || _settings.CropTop > 0 || _settings.CropRight > 0 || _settings.CropBottom > 0))
                {
                    int cw = Math.Max(10, _settings.SourceWidth - _settings.CropLeft - _settings.CropRight);
                    int ch = Math.Max(10, _settings.SourceHeight - _settings.CropTop - _settings.CropBottom);

                    var (dispX, dispY, baseScale) = GetVideoDisplayBounds();
                    if (baseScale > 0 && pw > 0 && ph > 0)
                    {
                        double croppedFitScale = Math.Min(pw / cw, ph / ch);
                        double zoom = croppedFitScale / baseScale;

                        double cropCenterX = _settings.CropLeft + (cw / 2.0);
                        double cropCenterY = _settings.CropTop + (ch / 2.0);
                        double videoCenterX = _settings.SourceWidth / 2.0;
                        double videoCenterY = _settings.SourceHeight / 2.0;

                        double offX = (videoCenterX - cropCenterX) * baseScale * zoom;
                        double offY = (videoCenterY - cropCenterY) * baseScale * zoom;

                        PlayerScale.ScaleX = (_settings.FlipHorizontal ? -1 : 1) * zoom * fitScale;
                        PlayerScale.ScaleY = (_settings.FlipVertical ? -1 : 1) * zoom * fitScale;
                        PlayerTranslate.X = offX;
                        PlayerTranslate.Y = offY;
                    }
                }
                else
                {
                    PlayerScale.ScaleX = (_settings.FlipHorizontal ? -1 : 1) * fitScale;
                    PlayerScale.ScaleY = (_settings.FlipVertical ? -1 : 1) * fitScale;
                    PlayerTranslate.X = 0;
                    PlayerTranslate.Y = 0;
                }
            }

            // 2. Live Audio Preview (Volume dB & Mute)
            Player.IsMuted = _settings.MuteAudio;
            if (!_settings.MuteAudio)
            {
                double baseVol = 0.5;
                double factor = Math.Pow(10, _settings.VolumeAdjustmentDb / 20.0);
                Player.Volume = Math.Clamp(baseVol * factor, 0.0, 1.0);
            }

            // 3. Live Badge
            if (LiveTransformBadge != null && LiveTransformBadgeText != null)
            {
                if (_settings.HasAnyEdits)
                {
                    LiveTransformBadge.Visibility = Visibility.Visible;
                    LiveTransformBadgeText.Text = $"⚡ LIVE PREVIEW: {_settings.GetEditSummary().ToUpperInvariant()}";
                }
                else
                {
                    LiveTransformBadge.Visibility = Visibility.Collapsed;
                }
            }

            // 4. Live Crop Viewport Box
            if (_isCroppedLivePreview && (_settings.CropLeft > 0 || _settings.CropTop > 0 || _settings.CropRight > 0 || _settings.CropBottom > 0))
            {
                if (CropCanvas != null) CropCanvas.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateLiveCropOverlay();
            }

            // 5. Summary
            if (EditSummaryText != null) EditSummaryText.Text = _settings.GetEditSummary();
        }
        catch { }
    }

    // ── Interactive Crop Overlay & Handles ───────────────────────────────────────
    private void UpdateLiveCropOverlay()
    {
        if (!_isInitialized || CropCanvas == null || CropBoxBorder == null || PlayerBorder == null) return;

        try
        {
            if (_settings.SourceWidth <= 0 || _settings.SourceHeight <= 0 || PlayerBorder.ActualWidth <= 0 || PlayerBorder.ActualHeight <= 0)
                return;

            bool isCropped = _settings.CropTop > 0 || _settings.CropBottom > 0 || _settings.CropLeft > 0 || _settings.CropRight > 0;
            bool isBoxMode = (CropModeBoxBtn != null && CropModeBoxBtn.IsChecked == true);

            if ((isCropped || isBoxMode) && !_isCroppedLivePreview)
            {
                CropCanvas.Visibility = Visibility.Visible;
                double pw = PlayerBorder.ActualWidth;
                double ph = PlayerBorder.ActualHeight;

                var (dispX, dispY, scale) = GetVideoDisplayBounds();
                if (scale <= 0) return;

                double cropX = dispX + (_settings.CropLeft * scale);
                double cropY = dispY + (_settings.CropTop * scale);
                double cropW = Math.Max(10, (_settings.SourceWidth - _settings.CropLeft - _settings.CropRight) * scale);
                double cropH = Math.Max(10, (_settings.SourceHeight - _settings.CropTop - _settings.CropBottom) * scale);

                // Position Main Crop Box
                Canvas.SetLeft(CropBoxBorder, cropX);
                Canvas.SetTop(CropBoxBorder, cropY);
                CropBoxBorder.Width = cropW;
                CropBoxBorder.Height = cropH;

                // Position Dimmed Masks
                if (MaskTop != null)
                {
                    Canvas.SetLeft(MaskTop, 0);
                    Canvas.SetTop(MaskTop, 0);
                    MaskTop.Width = pw;
                    MaskTop.Height = Math.Max(0, cropY);
                }
                if (MaskBottom != null)
                {
                    Canvas.SetLeft(MaskBottom, 0);
                    Canvas.SetTop(MaskBottom, cropY + cropH);
                    MaskBottom.Width = pw;
                    MaskBottom.Height = Math.Max(0, ph - (cropY + cropH));
                }
                if (MaskLeft != null)
                {
                    Canvas.SetLeft(MaskLeft, 0);
                    Canvas.SetTop(MaskLeft, cropY);
                    MaskLeft.Width = Math.Max(0, cropX);
                    MaskLeft.Height = cropH;
                }
                if (MaskRight != null)
                {
                    Canvas.SetLeft(MaskRight, cropX + cropW);
                    Canvas.SetTop(MaskRight, cropY);
                    MaskRight.Width = Math.Max(0, pw - (cropX + cropW));
                    MaskRight.Height = cropH;
                }

                // Position 8 Handles
                SetHandlePos(HandleNW, cropX - 5, cropY - 5);
                SetHandlePos(HandleN,  cropX + cropW / 2 - 5, cropY - 5);
                SetHandlePos(HandleNE, cropX + cropW - 5, cropY - 5);
                SetHandlePos(HandleE,  cropX + cropW - 5, cropY + cropH / 2 - 5);
                SetHandlePos(HandleSE, cropX + cropW - 5, cropY + cropH - 5);
                SetHandlePos(HandleS,  cropX + cropW / 2 - 5, cropY + cropH - 5);
                SetHandlePos(HandleSW, cropX - 5, cropY + cropH - 5);
                SetHandlePos(HandleW,  cropX - 5, cropY + cropH / 2 - 5);

                // Live Dimension Badge
                int resW = _settings.SourceWidth - _settings.CropLeft - _settings.CropRight;
                int resH = _settings.SourceHeight - _settings.CropTop - _settings.CropBottom;
                if (CropDimensionText != null)
                    CropDimensionText.Text = $"{resW} × {resH} px";
            }
            else
            {
                CropCanvas.Visibility = Visibility.Collapsed;
            }
        }
        catch { }
    }

    private static void SetHandlePos(FrameworkElement? handle, double left, double top)
    {
        if (handle == null) return;
        Canvas.SetLeft(handle, left);
        Canvas.SetTop(handle, top);
    }

    private void PlayerBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isInitialized) return;
        ApplyLivePreview();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  BUTTON ACTIVE STYLES
    // ═══════════════════════════════════════════════════════════════════════════
    private void UpdateAllButtonStyles()
    {
        if (!_isInitialized || Rotate90CWBtn == null) return;

        try
        {
            var activeStyle = (Style)FindResource("ActiveToolButton");
            var normalStyle = (Style)FindResource("GhostButton");

            // Rotation (Reset is an action button, not a toggle)
            if (Rotate90CWBtn != null) Rotate90CWBtn.Style  = _settings.RotationDegrees == 90  ? activeStyle : normalStyle;
            if (Rotate90CCWBtn != null) Rotate90CCWBtn.Style = _settings.RotationDegrees == 270 ? activeStyle : normalStyle;
            if (Rotate180Btn != null) Rotate180Btn.Style   = _settings.RotationDegrees == 180 ? activeStyle : normalStyle;
            if (RotateResetBtn != null) RotateResetBtn.Style = normalStyle;

            if (FlipHBtn != null) FlipHBtn.Style = _settings.FlipHorizontal ? activeStyle : normalStyle;
            if (FlipVBtn != null) FlipVBtn.Style = _settings.FlipVertical   ? activeStyle : normalStyle;

            // Audio (Reset is an action button)
            if (MuteBtn != null) MuteBtn.Style = _settings.MuteAudio ? activeStyle : normalStyle;
            if (MonoBtn != null) MonoBtn.Style = _settings.ConvertToMono ? activeStyle : normalStyle;
            if (VolDown6Btn != null) VolDown6Btn.Style = _settings.VolumeAdjustmentDb == -6 ? activeStyle : normalStyle;
            if (VolDown3Btn != null) VolDown3Btn.Style = _settings.VolumeAdjustmentDb == -3 ? activeStyle : normalStyle;
            if (VolUp3Btn != null) VolUp3Btn.Style   = _settings.VolumeAdjustmentDb == 3  ? activeStyle : normalStyle;
            if (VolUp6Btn != null) VolUp6Btn.Style   = _settings.VolumeAdjustmentDb == 6  ? activeStyle : normalStyle;
            if (ResetVolBtn != null) ResetVolBtn.Style = normalStyle;

            // Aspect Ratio
            if (AR16_9Btn != null) AR16_9Btn.Style = _settings.AspectRatio == "16:9" ? activeStyle : normalStyle;
            if (AR9_16Btn != null) AR9_16Btn.Style = _settings.AspectRatio == "9:16" ? activeStyle : normalStyle;
            if (AR4_3Btn != null) AR4_3Btn.Style  = _settings.AspectRatio == "4:3"  ? activeStyle : normalStyle;
            if (AR1_1Btn != null) AR1_1Btn.Style  = _settings.AspectRatio == "1:1"  ? activeStyle : normalStyle;
            if (AROrigBtn != null) AROrigBtn.Style = string.IsNullOrEmpty(_settings.AspectRatio) ? activeStyle : normalStyle;
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  TIMELINE, FILMSTRIP & WAVEFORM
    // ═══════════════════════════════════════════════════════════════════════════
    private async Task GenerateFilmstripAsync(string path, double duration, CancellationToken ct)
    {
        if (!File.Exists(FFmpeg) || duration <= 0) return;

        var thumbBase = IsFolderWritable(AppDir)
            ? AppDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoFixPro");
        var dir = Path.Combine(thumbBase, "toolbox_thumbs");
        try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { return; }

        for (int i = 0; i < FilmstripBuckets; i++)
        {
            if (ct.IsCancellationRequested) break;
            double t = duration * i / FilmstripBuckets;
            var outImg = Path.Combine(dir, $"tb_{GetHashCode()}_{i}.jpg");
            var args = $"-y -ss {t.ToString(CultureInfo.InvariantCulture)} -i \"{path}\" -frames:v 1 -vf scale=160:-1 -q:v 4 \"{outImg}\"";

            try
            {
                await RunProcessAsync(FFmpeg, args, ct);
                if (ct.IsCancellationRequested) break;

                if (File.Exists(outImg))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(outImg);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.EndInit();
                    bmp.Freeze();
                    _filmImages[i] = bmp;
                    try { File.Delete(outImg); } catch { }

                    if (!Dispatcher.HasShutdownStarted)
                        Dispatcher.Invoke(DrawFilmstrip);
                }
            }
            catch { }
        }
    }

    private void DrawFilmstrip()
    {
        if (!_isInitialized || FilmstripCanvas == null) return;
        try
        {
            FilmstripCanvas.Children.Clear();
            double w = FilmstripCanvas.ActualWidth;
            double h = FilmstripCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            double cellW = w / FilmstripBuckets;
            for (int i = 0; i < FilmstripBuckets; i++)
            {
                var img = _filmImages[i];
                if (img == null) continue;
                var ib = new Image
                {
                    Source = img,
                    Width = cellW,
                    Height = h,
                    Stretch = Stretch.UniformToFill,
                    ClipToBounds = true
                };
                Canvas.SetLeft(ib, i * cellW);
                Canvas.SetTop(ib, 0);
                FilmstripCanvas.Children.Add(ib);
            }
            UpdateTimelinePlayheads();
        }
        catch { }
    }

    private void DrawWaveform()
    {
        if (!_isInitialized || WaveformCanvas == null) return;
        try
        {
            WaveformCanvas.Children.Clear();
            double w = WaveformCanvas.ActualWidth;
            double h = WaveformCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            int bars = (int)(w / 4);
            var rand = new Random(42);
            for (int i = 0; i < bars; i++)
            {
                double barH = Math.Max(3, rand.NextDouble() * (h - 4));
                var rect = new Rectangle
                {
                    Width = 2,
                    Height = barH,
                    Fill = new SolidColorBrush(Color.FromArgb(0x88, 0x38, 0x8B, 0xFD)),
                    RadiusX = 1,
                    RadiusY = 1
                };
                Canvas.SetLeft(rect, i * 4);
                Canvas.SetTop(rect, (h - barH) / 2.0);
                WaveformCanvas.Children.Add(rect);
            }
            UpdateTimelinePlayheads();
        }
        catch { }
    }

    private void UpdateTimelinePlayheads()
    {
        if (!_isInitialized || _durationSeconds <= 0 || Player == null) return;
        try
        {
            double pos = Player.Position.TotalSeconds;

            if (FilmstripCanvas != null && FilmstripPlayhead != null)
            {
                double fw = FilmstripCanvas.ActualWidth;
                if (fw > 0)
                {
                    Canvas.SetLeft(FilmstripPlayhead, (pos / _durationSeconds) * fw);
                    FilmstripPlayhead.Height = FilmstripCanvas.ActualHeight;
                }
            }

            if (WaveformCanvas != null && WaveformPlayhead != null)
            {
                double ww = WaveformCanvas.ActualWidth;
                if (ww > 0)
                {
                    Canvas.SetLeft(WaveformPlayhead, (pos / _durationSeconds) * ww);
                    WaveformPlayhead.Height = WaveformCanvas.ActualHeight;
                }
            }
        }
        catch { }
    }

    private void FilmstripCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isInitialized) return;
        DrawFilmstrip();
    }

    private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isInitialized) return;
        DrawWaveform();
    }

    private void Timeline_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_isInitialized || _durationSeconds <= 0 || Player == null) return;
        try
        {
            var element = (FrameworkElement)sender;
            var p = e.GetPosition(element);
            double ratio = Math.Clamp(p.X / element.ActualWidth, 0.0, 1.0);
            double targetSec = ratio * _durationSeconds;
            Player.Position = TimeSpan.FromSeconds(targetSec);
            if (SeekSlider != null) SeekSlider.Value = targetSec;
            UpdateTimelinePlayheads();
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  VIDEO PLAYBACK
    // ═══════════════════════════════════════════════════════════════════════════
    private void Player_MediaOpened(object s, RoutedEventArgs e)
    {
        if (!_isInitialized || Player == null) return;
        try
        {
            Player.Pause();
            _isPlayerPlaying = false;
            UpdatePlayPauseUI();
        }
        catch { }
    }

    private void Player_MediaEnded(object s, RoutedEventArgs e)
    {
        if (!_isInitialized || Player == null) return;
        try
        {
            Player.Pause();
            _isPlayerPlaying = false;
            _playheadTimer.Stop();
            UpdatePlayPauseUI();
        }
        catch { }
    }

    private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        Log($"[WARN] Direct preview playback not supported for this format/codec: {e.ErrorException?.Message}. FFmpeg editing and rendering are fully functional.");
    }

    private void TogglePlay_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_filePath) || Player == null) return;

        try
        {
            if (_isPlayerPlaying)
            {
                Player.Pause();
                _isPlayerPlaying = false;
                _playheadTimer.Stop();
            }
            else
            {
                if (_durationSeconds > 0 && Player.Position.TotalSeconds >= _durationSeconds - 0.1)
                    Player.Position = TimeSpan.Zero;
                Player.Play();
                _isPlayerPlaying = true;
                _playheadTimer.Start();
            }
            UpdatePlayPauseUI();
        }
        catch { }
    }

    private void UpdatePlayPauseUI()
    {
        if (!_isInitialized) return;
        if (PlayPauseBtn != null)
        {
            PlayPauseBtn.Content = _isPlayerPlaying ? "⏸" : "▶";
            PlayPauseBtn.Opacity = _isPlayerPlaying ? 0.2 : 0.6;
        }
        if (SeekPlayBtn != null)
        {
            SeekPlayBtn.Content = _isPlayerPlaying ? "⏸" : "▶";
        }
    }

    private void PlayPauseBtn_MouseEnter(object s, MouseEventArgs e) { if (PlayPauseBtn != null) PlayPauseBtn.Opacity = 0.9; }
    private void PlayPauseBtn_MouseLeave(object s, MouseEventArgs e) { if (PlayPauseBtn != null) PlayPauseBtn.Opacity = _isPlayerPlaying ? 0.2 : 0.6; }

    private void UpdateSeekFromPlayer()
    {
        if (!_isInitialized || _isSeeking || _durationSeconds <= 0 || Player == null) return;
        try
        {
            double pos = Player.Position.TotalSeconds;
            _isSeeking = true;
            if (SeekSlider != null) SeekSlider.Value = pos;
            _isSeeking = false;
            if (SeekTimeText != null) SeekTimeText.Text = $"{FormatTime(pos)} / {FormatTime(_durationSeconds)}";
            UpdateTimelinePlayheads();
        }
        catch { }
    }

    private void SeekSlider_MouseDown(object s, MouseButtonEventArgs e) => _isSeeking = true;

    private void SeekSlider_MouseUp(object s, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        if (_durationSeconds > 0 && Player != null && SeekSlider != null)
        {
            Player.Position = TimeSpan.FromSeconds(SeekSlider.Value);
            UpdateTimelinePlayheads();
        }
    }

    private void SeekSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        if (_isSeeking && _durationSeconds > 0 && Player != null && SeekSlider != null)
        {
            Player.Position = TimeSpan.FromSeconds(SeekSlider.Value);
            if (SeekTimeText != null) SeekTimeText.Text = $"{FormatTime(SeekSlider.Value)} / {FormatTime(_durationSeconds)}";
            UpdateTimelinePlayheads();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ROTATION & FLIP
    // ═══════════════════════════════════════════════════════════════════════════
    private void Rotate90CW_Click(object s, RoutedEventArgs e)
    {
        _settings.RotationDegrees = (_settings.RotationDegrees + 90) % 360;
        UpdateRotationUI();
    }

    private void Rotate90CCW_Click(object s, RoutedEventArgs e)
    {
        _settings.RotationDegrees = (_settings.RotationDegrees + 270) % 360;
        UpdateRotationUI();
    }

    private void Rotate180_Click(object s, RoutedEventArgs e)
    {
        _settings.RotationDegrees = (_settings.RotationDegrees + 180) % 360;
        UpdateRotationUI();
    }

    private void ResetRotation_Click(object s, RoutedEventArgs e)
    {
        _settings.RotationDegrees = 0;
        _settings.FlipHorizontal = false;
        _settings.FlipVertical = false;
        UpdateRotationUI();
    }

    private void FlipH_Click(object s, RoutedEventArgs e)
    {
        _settings.FlipHorizontal = !_settings.FlipHorizontal;
        UpdateRotationUI();
    }

    private void FlipV_Click(object s, RoutedEventArgs e)
    {
        _settings.FlipVertical = !_settings.FlipVertical;
        UpdateRotationUI();
    }

    private void UpdateRotationUI()
    {
        var parts = new List<string>();
        parts.Add($"{_settings.RotationDegrees}°");
        if (_settings.FlipHorizontal) parts.Add("Flip H");
        if (_settings.FlipVertical) parts.Add("Flip V");
        if (RotationStatusText != null) RotationStatusText.Text = $"Current: {string.Join(" · ", parts)}";

        ApplyLivePreview();
        UpdateAllButtonStyles();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  AUDIO ADJUSTMENTS
    // ═══════════════════════════════════════════════════════════════════════════
    private void VolDown6_Click(object s, RoutedEventArgs e) { _settings.VolumeAdjustmentDb = -6; UpdateAudioUI(); }
    private void VolDown3_Click(object s, RoutedEventArgs e) { _settings.VolumeAdjustmentDb = -3; UpdateAudioUI(); }
    private void VolUp3_Click(object s, RoutedEventArgs e)   { _settings.VolumeAdjustmentDb = 3;  UpdateAudioUI(); }
    private void VolUp6_Click(object s, RoutedEventArgs e)   { _settings.VolumeAdjustmentDb = 6;  UpdateAudioUI(); }

    private void ResetVolume_Click(object s, RoutedEventArgs e)
    {
        _settings.VolumeAdjustmentDb = 0;
        UpdateAudioUI();
    }

    private void MuteAudio_Click(object s, RoutedEventArgs e)
    {
        _settings.MuteAudio = !_settings.MuteAudio;
        UpdateAudioUI();
    }

    private void StereoToMono_Click(object s, RoutedEventArgs e)
    {
        _settings.ConvertToMono = !_settings.ConvertToMono;
        UpdateAudioUI();
    }

    private void UpdateAudioUI()
    {
        var parts = new List<string>();
        if (_settings.MuteAudio)
            parts.Add("MUTED");
        else
        {
            var db = _settings.VolumeAdjustmentDb;
            parts.Add($"Volume: {(db >= 0 ? "+" : "")}{db:F0} dB");
        }
        parts.Add(_settings.ConvertToMono ? "Mono" : "Stereo");
        if (AudioStatusText != null) AudioStatusText.Text = string.Join(" · ", parts);

        ApplyLivePreview();
        UpdateAllButtonStyles();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  ASPECT RATIO
    // ═══════════════════════════════════════════════════════════════════════════
    private void AR_16_9_Click(object s, RoutedEventArgs e) => SetAspect("16:9");
    private void AR_9_16_Click(object s, RoutedEventArgs e) => SetAspect("9:16");
    private void AR_4_3_Click(object s, RoutedEventArgs e)  => SetAspect("4:3");
    private void AR_1_1_Click(object s, RoutedEventArgs e)  => SetAspect("1:1");
    private void AR_Original_Click(object s, RoutedEventArgs e) => SetAspect("");

    private void FlipRatio_Click(object s, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_settings.AspectRatio) && _settings.AspectRatio.Contains(':'))
        {
            var parts = _settings.AspectRatio.Split(':');
            if (parts.Length == 2) SetAspect($"{parts[1]}:{parts[0]}");
        }
    }

    private void SetAspect(string ratio)
    {
        _settings.AspectRatio = ratio;
        if (AspectStatusText != null) AspectStatusText.Text = string.IsNullOrEmpty(ratio) ? "Current: Original" : $"Current: {ratio}";
        ApplyLivePreview();
        UpdateAllButtonStyles();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  INTERACTIVE CROP CONTROLLER
    // ═══════════════════════════════════════════════════════════════════════════
    private enum CropDragMode
    {
        None,
        Create,
        Move,
        ResizeNW,
        ResizeN,
        ResizeNE,
        ResizeE,
        ResizeSE,
        ResizeS,
        ResizeSW,
        ResizeW
    }

    private CropDragMode _cropDragMode = CropDragMode.None;
    private Point _cropDragStartPoint;
    private int _startCropLeft, _startCropTop, _startCropRight, _startCropBottom;
    private string _activeCropPreset = "";

    private void CropPresetFree_Click(object s, RoutedEventArgs e)
    {
        _activeCropPreset = "Freeform";
        UpdateCropPresetButtonStyles();
    }

    private void CropPreset16_9_Click(object s, RoutedEventArgs e)
    {
        _activeCropPreset = "16:9";
        ApplyCropAspectRatio(16.0 / 9.0);
        UpdateCropPresetButtonStyles();
    }

    private void CropPreset9_16_Click(object s, RoutedEventArgs e)
    {
        _activeCropPreset = "9:16";
        ApplyCropAspectRatio(9.0 / 16.0);
        UpdateCropPresetButtonStyles();
    }

    private void CropPreset1_1_Click(object s, RoutedEventArgs e)
    {
        _activeCropPreset = "1:1";
        ApplyCropAspectRatio(1.0);
        UpdateCropPresetButtonStyles();
    }

    private void CropPreset4_3_Click(object s, RoutedEventArgs e)
    {
        _activeCropPreset = "4:3";
        ApplyCropAspectRatio(4.0 / 3.0);
        UpdateCropPresetButtonStyles();
    }

    private void ApplyCropAspectRatio(double targetRatio)
    {
        if (_settings.SourceWidth <= 0 || _settings.SourceHeight <= 0) return;
        _isCroppedLivePreview = false;
        if (CropModeBoxBtn != null) CropModeBoxBtn.IsChecked = true;
        if (CropModePreviewBtn != null) CropModePreviewBtn.IsChecked = false;

        int srcW = _settings.SourceWidth;
        int srcH = _settings.SourceHeight;
        double srcRatio = (double)srcW / srcH;

        int cropW, cropH;
        if (srcRatio > targetRatio)
        {
            // Video is wider than target: fit height, crop sides
            cropH = srcH;
            cropW = (int)Math.Round(cropH * targetRatio);
        }
        else
        {
            // Video is taller than target: fit width, crop top/bottom
            cropW = srcW;
            cropH = (int)Math.Round(cropW / targetRatio);
        }

        // Ensure even dimensions
        cropW = (cropW / 2) * 2;
        cropH = (cropH / 2) * 2;

        int left = Math.Max(0, (srcW - cropW) / 2);
        int right = Math.Max(0, srcW - cropW - left);
        int top = Math.Max(0, (srcH - cropH) / 2);
        int bottom = Math.Max(0, srcH - cropH - top);

        SetCropPixels(top, bottom, left, right);
        ApplyCropValues();
    }

    private void UpdateCropPresetButtonStyles()
    {
        var activeStyle = (Style)FindResource("ActiveToolButton");
        var ghostStyle = (Style)FindResource("GhostButton");

        bool hasCrop = (_settings.CropTop > 0 || _settings.CropBottom > 0 || _settings.CropLeft > 0 || _settings.CropRight > 0);

        if (CropPresetFreeBtn != null) CropPresetFreeBtn.Style = (hasCrop && _activeCropPreset == "Freeform") ? activeStyle : ghostStyle;
        if (CropPreset16_9Btn != null) CropPreset16_9Btn.Style = (hasCrop && _activeCropPreset == "16:9") ? activeStyle : ghostStyle;
        if (CropPreset9_16Btn != null) CropPreset9_16Btn.Style = (hasCrop && _activeCropPreset == "9:16") ? activeStyle : ghostStyle;
        if (CropPreset1_1Btn != null) CropPreset1_1Btn.Style = (hasCrop && _activeCropPreset == "1:1") ? activeStyle : ghostStyle;
        if (CropPreset4_3Btn != null) CropPreset4_3Btn.Style = (hasCrop && _activeCropPreset == "4:3") ? activeStyle : ghostStyle;
    }

    private void SetCropPixels(int top, int bottom, int left, int right)
    {
        _settings.CropTop = Math.Max(0, top);
        _settings.CropBottom = Math.Max(0, bottom);
        _settings.CropLeft = Math.Max(0, left);
        _settings.CropRight = Math.Max(0, right);

        if (CropTopBox != null) CropTopBox.Text = _settings.CropTop.ToString();
        if (CropBottomBox != null) CropBottomBox.Text = _settings.CropBottom.ToString();
        if (CropLeftBox != null) CropLeftBox.Text = _settings.CropLeft.ToString();
        if (CropRightBox != null) CropRightBox.Text = _settings.CropRight.ToString();
    }

    // ── Mouse Drag & Resize Handlers ──────────────────────────────────────────
    private void CropBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _settings.SourceWidth <= 0 || _settings.SourceHeight <= 0) return;
        _cropDragMode = CropDragMode.Move;
        _cropDragStartPoint = e.GetPosition(CropCanvas);
        _startCropLeft = _settings.CropLeft;
        _startCropTop = _settings.CropTop;
        _startCropRight = _settings.CropRight;
        _startCropBottom = _settings.CropBottom;
        CropCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void Handle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _settings.SourceWidth <= 0 || _settings.SourceHeight <= 0) return;
        if (sender is FrameworkElement elem && elem.Tag is string tag)
        {
            _cropDragMode = tag switch
            {
                "NW" => CropDragMode.ResizeNW,
                "N"  => CropDragMode.ResizeN,
                "NE" => CropDragMode.ResizeNE,
                "E"  => CropDragMode.ResizeE,
                "SE" => CropDragMode.ResizeSE,
                "S"  => CropDragMode.ResizeS,
                "SW" => CropDragMode.ResizeSW,
                "W"  => CropDragMode.ResizeW,
                _ => CropDragMode.None
            };

            _cropDragStartPoint = e.GetPosition(CropCanvas);
            _startCropLeft = _settings.CropLeft;
            _startCropTop = _settings.CropTop;
            _startCropRight = _settings.CropRight;
            _startCropBottom = _settings.CropBottom;
            CropCanvas.CaptureMouse();
            e.Handled = true;
        }
    }

    private void CropCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _settings.SourceWidth <= 0 || _settings.SourceHeight <= 0) return;
        _cropDragMode = CropDragMode.Create;
        _cropDragStartPoint = e.GetPosition(CropCanvas);

        var (dispX, dispY, scale) = GetVideoDisplayBounds();
        if (scale <= 0) return;

        int vidX = Math.Clamp((int)Math.Round((_cropDragStartPoint.X - dispX) / scale), 0, _settings.SourceWidth);
        int vidY = Math.Clamp((int)Math.Round((_cropDragStartPoint.Y - dispY) / scale), 0, _settings.SourceHeight);

        _startCropLeft = vidX;
        _startCropTop = vidY;
        _startCropRight = _settings.SourceWidth - vidX;
        _startCropBottom = _settings.SourceHeight - vidY;

        SetCropPixels(_startCropTop, _startCropBottom, _startCropLeft, _startCropRight);
        UpdateLiveCropOverlay();
        CropCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void CropCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_cropDragMode == CropDragMode.None || _settings.SourceWidth <= 0 || _settings.SourceHeight <= 0) return;

        var currentPoint = e.GetPosition(CropCanvas);
        var (dispX, dispY, scale) = GetVideoDisplayBounds();
        if (scale <= 0) return;

        double deltaScreenX = currentPoint.X - _cropDragStartPoint.X;
        double deltaScreenY = currentPoint.Y - _cropDragStartPoint.Y;

        int deltaVidX = (int)Math.Round(deltaScreenX / scale);
        int deltaVidY = (int)Math.Round(deltaScreenY / scale);

        int srcW = _settings.SourceWidth;
        int srcH = _settings.SourceHeight;

        int newLeft = _settings.CropLeft;
        int newTop = _settings.CropTop;
        int newRight = _settings.CropRight;
        int newBottom = _settings.CropBottom;

        switch (_cropDragMode)
        {
            case CropDragMode.Create:
                int curVidX = Math.Clamp((int)Math.Round((currentPoint.X - dispX) / scale), 0, srcW);
                int curVidY = Math.Clamp((int)Math.Round((currentPoint.Y - dispY) / scale), 0, srcH);
                int startVidX = Math.Clamp((int)Math.Round((_cropDragStartPoint.X - dispX) / scale), 0, srcW);
                int startVidY = Math.Clamp((int)Math.Round((_cropDragStartPoint.Y - dispY) / scale), 0, srcH);

                int x1 = Math.Min(startVidX, curVidX);
                int x2 = Math.Max(startVidX, curVidX);
                int y1 = Math.Min(startVidY, curVidY);
                int y2 = Math.Max(startVidY, curVidY);

                newLeft = x1;
                newRight = srcW - x2;
                newTop = y1;
                newBottom = srcH - y2;
                break;

            case CropDragMode.Move:
                int boxW = srcW - _startCropLeft - _startCropRight;
                int boxH = srcH - _startCropTop - _startCropBottom;

                newLeft = Math.Clamp(_startCropLeft + deltaVidX, 0, srcW - boxW);
                newRight = srcW - newLeft - boxW;
                newTop = Math.Clamp(_startCropTop + deltaVidY, 0, srcH - boxH);
                newBottom = srcH - newTop - boxH;
                break;

            case CropDragMode.ResizeNW:
                newLeft = Math.Clamp(_startCropLeft + deltaVidX, 0, srcW - _startCropRight - 32);
                newTop = Math.Clamp(_startCropTop + deltaVidY, 0, srcH - _startCropBottom - 32);
                break;

            case CropDragMode.ResizeN:
                newTop = Math.Clamp(_startCropTop + deltaVidY, 0, srcH - _startCropBottom - 32);
                break;

            case CropDragMode.ResizeNE:
                newRight = Math.Clamp(_startCropRight - deltaVidX, 0, srcW - _startCropLeft - 32);
                newTop = Math.Clamp(_startCropTop + deltaVidY, 0, srcH - _startCropBottom - 32);
                break;

            case CropDragMode.ResizeE:
                newRight = Math.Clamp(_startCropRight - deltaVidX, 0, srcW - _startCropLeft - 32);
                break;

            case CropDragMode.ResizeSE:
                newRight = Math.Clamp(_startCropRight - deltaVidX, 0, srcW - _startCropLeft - 32);
                newBottom = Math.Clamp(_startCropBottom - deltaVidY, 0, srcH - _startCropTop - 32);
                break;

            case CropDragMode.ResizeS:
                newBottom = Math.Clamp(_startCropBottom - deltaVidY, 0, srcH - _startCropTop - 32);
                break;

            case CropDragMode.ResizeSW:
                newLeft = Math.Clamp(_startCropLeft + deltaVidX, 0, srcW - _startCropRight - 32);
                newBottom = Math.Clamp(_startCropBottom - deltaVidY, 0, srcH - _startCropTop - 32);
                break;

            case CropDragMode.ResizeW:
                newLeft = Math.Clamp(_startCropLeft + deltaVidX, 0, srcW - _startCropRight - 32);
                break;
        }

        SetCropPixels(newTop, newBottom, newLeft, newRight);
        UpdateLiveCropOverlay();
        UpdateCropStatusDisplay();
    }

    private void CropCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropDragMode != CropDragMode.None)
        {
            _cropDragMode = CropDragMode.None;
            CropCanvas.ReleaseMouseCapture();
            ApplyCropValues();
        }
    }

    private void CropCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_cropDragMode != CropDragMode.None && e.LeftButton == MouseButtonState.Released)
        {
            _cropDragMode = CropDragMode.None;
            CropCanvas.ReleaseMouseCapture();
            ApplyCropValues();
        }
    }

    private (double dispX, double dispY, double scale) GetVideoDisplayBounds()
    {
        if (PlayerBorder == null || _settings.SourceWidth <= 0 || _settings.SourceHeight <= 0)
            return (0, 0, 0);

        double pw = PlayerBorder.ActualWidth;
        double ph = PlayerBorder.ActualHeight;
        if (pw <= 0 || ph <= 0) return (0, 0, 0);

        double scaleX = pw / _settings.SourceWidth;
        double scaleY = ph / _settings.SourceHeight;
        double scale = Math.Min(scaleX, scaleY);

        double displayedW = _settings.SourceWidth * scale;
        double displayedH = _settings.SourceHeight * scale;
        double dispX = (pw - displayedW) / 2.0;
        double dispY = (ph - displayedH) / 2.0;

        return (dispX, dispY, scale);
    }

    private void CropModeBox_Click(object s, RoutedEventArgs e)
    {
        if (CropModeBoxBtn == null) return;

        if (CropModeBoxBtn.IsChecked == true)
        {
            _isCroppedLivePreview = false;
            if (CropModePreviewBtn != null) CropModePreviewBtn.IsChecked = false;

            if (_settings.CropLeft == 0 && _settings.CropTop == 0 && _settings.CropRight == 0 && _settings.CropBottom == 0)
            {
                if (_settings.SourceWidth > 0 && _settings.SourceHeight > 0)
                {
                    int padX = (_settings.SourceWidth * 10) / 100;
                    int padY = (_settings.SourceHeight * 10) / 100;
                    SetCropPixels(padY, padY, padX, padX);
                }
            }
            ApplyLivePreview();
            SetStatus("Crop Selection Mode (Adjust handles on video)", "#388BFD");
        }
        else
        {
            _isCroppedLivePreview = false;
            if (CropCanvas != null) CropCanvas.Visibility = Visibility.Collapsed;
            ApplyLivePreview();
            SetStatus("Ready", "#8B949E");
        }
    }

    private void CropModePreview_Click(object s, RoutedEventArgs e)
    {
        if (CropModePreviewBtn == null) return;

        if (CropModePreviewBtn.IsChecked == true)
        {
            _isCroppedLivePreview = true;
            if (CropModeBoxBtn != null) CropModeBoxBtn.IsChecked = false;
            ApplyLivePreview();
            int cw = _settings.SourceWidth - _settings.CropLeft - _settings.CropRight;
            int ch = _settings.SourceHeight - _settings.CropTop - _settings.CropBottom;
            SetStatus($"Live Cropped Preview: {cw} × {ch} px", "#3FB950");
        }
        else
        {
            _isCroppedLivePreview = false;
            ApplyLivePreview();
            SetStatus("Ready", "#8B949E");
        }
    }

    private void CropBox_LostFocus(object s, RoutedEventArgs e) => ApplyCropValues();
    private void CropBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyCropValues();
    }

    private void ApplyCrop_Click(object s, RoutedEventArgs e)
    {
        ApplyCropValues();
        _isCroppedLivePreview = true;
        if (CropModeBoxBtn != null) CropModeBoxBtn.IsChecked = false;
        if (CropModePreviewBtn != null) CropModePreviewBtn.IsChecked = true;
        ApplyLivePreview();

        int cw = _settings.SourceWidth - _settings.CropLeft - _settings.CropRight;
        int ch = _settings.SourceHeight - _settings.CropTop - _settings.CropBottom;
        SetStatus($"✓ Crop Applied! Live preview showing {cw} × {ch} px", "#3FB950");
    }

    private void ApplyCropValues()
    {
        if (!_isInitialized || CropTopBox == null || CropBottomBox == null || CropLeftBox == null || CropRightBox == null) return;

        if (!int.TryParse(CropTopBox.Text, out int t)) t = 0;
        if (!int.TryParse(CropBottomBox.Text, out int b)) b = 0;
        if (!int.TryParse(CropLeftBox.Text, out int l)) l = 0;
        if (!int.TryParse(CropRightBox.Text, out int r)) r = 0;

        _settings.CropTop = Math.Max(0, t);
        _settings.CropBottom = Math.Max(0, b);
        _settings.CropLeft = Math.Max(0, l);
        _settings.CropRight = Math.Max(0, r);

        UpdateCropStatusDisplay();
        ApplyLivePreview();
    }

    private void UpdateCropStatusDisplay()
    {
        if (_settings.SourceWidth <= 0 || CropStatusText == null) return;
        int resultW = _settings.SourceWidth - _settings.CropLeft - _settings.CropRight;
        int resultH = _settings.SourceHeight - _settings.CropTop - _settings.CropBottom;
        if (_settings.CropLeft == 0 && _settings.CropTop == 0 && _settings.CropRight == 0 && _settings.CropBottom == 0)
        {
            CropStatusText.Text = "No crop applied · Full Frame";
            CropStatusText.Foreground = (Brush)FindResource("MutedBrush");
        }
        else if (resultW <= 0 || resultH <= 0)
        {
            CropStatusText.Text = "Invalid crop: dimensions <= 0";
            CropStatusText.Foreground = (Brush)FindResource("ErrorBrush");
        }
        else
        {
            CropStatusText.Text = $"Crop: {resultW} × {resultH} (from {_settings.SourceWidth} × {_settings.SourceHeight})";
            CropStatusText.Foreground = (Brush)FindResource("AccentBrush");
        }
    }

    private void ResetCrop_Click(object s, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _settings.CropTop = _settings.CropBottom = _settings.CropLeft = _settings.CropRight = 0;
        if (CropTopBox != null) CropTopBox.Text = "0";
        if (CropBottomBox != null) CropBottomBox.Text = "0";
        if (CropLeftBox != null) CropLeftBox.Text = "0";
        if (CropRightBox != null) CropRightBox.Text = "0";
        _isCroppedLivePreview = false;
        if (CropModeBoxBtn != null) CropModeBoxBtn.IsChecked = true;
        if (CropModePreviewBtn != null) CropModePreviewBtn.IsChecked = false;
        _activeCropPreset = "Freeform";
        UpdateCropPresetButtonStyles();
        UpdateCropStatusDisplay();
        ApplyLivePreview();
        SetStatus("Crop reset to full frame", "#D29922");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  RESET ALL
    // ═══════════════════════════════════════════════════════════════════════════
    private void ResetAll_Click(object s, RoutedEventArgs e)
    {
        _settings.Reset();
        if (CropTopBox != null) CropTopBox.Text = "0";
        if (CropBottomBox != null) CropBottomBox.Text = "0";
        if (CropLeftBox != null) CropLeftBox.Text = "0";
        if (CropRightBox != null) CropRightBox.Text = "0";
        if (RotationStatusText != null) RotationStatusText.Text = "Current: 0° · No flip";
        if (AudioStatusText != null) AudioStatusText.Text = "Volume: 0 dB · Stereo";
        if (AspectStatusText != null) AspectStatusText.Text = "Current: Original";
        if (CropStatusText != null)
        {
            CropStatusText.Text = "No crop applied";
            CropStatusText.Foreground = (Brush)FindResource("MutedBrush");
        }

        ApplyLivePreview();
        UpdateAllButtonStyles();
        SetStatus("All edits reset", "#D29922");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  OUTPUT SETTINGS
    // ═══════════════════════════════════════════════════════════════════════════
    private void BrowseOutput_Click(object s, RoutedEventArgs e)
    {
        var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Select output folder",
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

    // ═══════════════════════════════════════════════════════════════════════════
    //  LOG
    // ═══════════════════════════════════════════════════════════════════════════
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

    // ═══════════════════════════════════════════════════════════════════════════
    //  RENDER / QUICK SAVE
    // ═══════════════════════════════════════════════════════════════════════════
    private async void QuickSave_Click(object s, RoutedEventArgs e)
    {
            try
            {
        if (string.IsNullOrEmpty(_filePath))
        {
            MessageBox.Show("Load a video file first.", "No File", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RenderAsync(quickSave: true);
    }
            catch (Exception ex)
            {
                MessageBox.Show("An unexpected error occurred: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    private async void Render_Click(object s, RoutedEventArgs e)
    {
            try
            {
        if (string.IsNullOrEmpty(_filePath))
        {
            MessageBox.Show("Load a video file first.", "No File", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_settings.HasAnyEdits)
        {
            var result = MessageBox.Show(
                "No edits have been applied. Do you want to re-encode the video anyway?",
                "No Edits", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }

        await RenderAsync(quickSave: false);
    }
            catch (Exception ex)
            {
                MessageBox.Show("An unexpected error occurred: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    private void Cancel_Click(object s, RoutedEventArgs e) => CancelRender();

    private void CancelRender()
    {
        _cts?.Cancel();
        try { _ffmpegProcess?.Kill(); } catch { }
    }

    private async Task RenderAsync(bool quickSave)
    {
        if (_isRendering) return;
        if (!File.Exists(FFmpeg))
        {
            MessageBox.Show("FFmpeg not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _isRendering = true;
        _renderStartTime = DateTime.Now;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        // Determine output format and path
        string selFormat = OutputFormatBox.SelectedItem?.ToString()?.ToLowerInvariant() ?? "mp4";
        string ext = quickSave ? Path.GetExtension(_filePath).ToLowerInvariant() : 
                     (selFormat.Contains("mkv") ? ".mkv" : 
                      selFormat.Contains("avi") ? ".avi" : 
                      selFormat.Contains("mov") ? ".mov" : 
                      selFormat.Contains("webm") ? ".webm" : ".mp4");
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";

        string dir = (quickSave || string.IsNullOrEmpty(_customOutputFolder))
            ? (Path.GetDirectoryName(_filePath) ?? ".") : _customOutputFolder;

        string rawBaseName = Path.GetFileNameWithoutExtension(_filePath);
        string safeBaseName = SanitizeFileName(rawBaseName);
        string outputPath = GetUniqueFilePath(Path.Combine(dir, $"{safeBaseName}_edited{ext}"));
        _lastOutputFolder = dir;

        SetRenderingUI(true);
        SetStatus(quickSave ? "Quick Saving..." : "Rendering...", "#388BFD");
        Log($"\n[RENDER] Starting {(quickSave ? "Quick Save" : "Render")} for: {Path.GetFileName(_filePath)}");
        Log($"[RENDER] Output: {outputPath}");
        Log($"[RENDER] Edits: {_settings.GetEditSummary()}");

        int quality = quickSave ? 75 : (int)QualitySlider.Value;
        int crf = (int)(40 - (quality * (40 - 18) / 100.0));
        bool useGpu = (GpuCheck.IsChecked == true) && (_hasNvidia || _hasAmd || _hasIntel);

        // Try rendering with primary options
        string args = BuildRenderArgs(_filePath, outputPath, ext, crf, useGpu);
        Log($"[CMD] ffmpeg {args}");

        bool success = await RunFFmpegAsync(args, _durationSeconds, _cts.Token);

        // Automatic CPU fallback if hardware encoder failed
        if (!success && !_cts.Token.IsCancellationRequested && useGpu)
        {
            Log("[WARN] GPU hardware encoding failed. Automatically falling back to CPU encoder (libx264)...");
            SetStatus("Retrying on CPU...", "#D29922");
            args = BuildRenderArgs(_filePath, outputPath, ext, crf, useGpu: false);
            Log($"[CMD (Fallback)] ffmpeg {args}");
            success = await RunFFmpegAsync(args, _durationSeconds, _cts.Token);
        }

        _isRendering = false;
        SetRenderingUI(false);

        if (_cts.Token.IsCancellationRequested)
        {
            SetStatus("Cancelled", "#D29922");
            Log("[RENDER] Cancelled by user.");
            try { File.Delete(outputPath); } catch { }
        }
        else if (success && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
        {
            SetRenderProgress(100);
            SetStatus("Done!", "#3FB950");
            Log($"[RENDER] Success: {outputPath}");
            ShowNotification("Video Toolbox - Complete", $"Saved: {Path.GetFileName(outputPath)}");
        }
        else
        {
            SetStatus("Render failed", "#F85149");
            Log("[RENDER] Failed. Check log for details.");
        }
    }

    private string BuildRenderArgs(string input, string output, string ext, int crf, bool useGpu)
    {
        var sb = new StringBuilder();
        sb.Append("-y ");
        sb.Append($"-i \"{input}\" ");
        sb.Append("-map 0:v -map 0:a? ");

        var vf = _settings.BuildVideoFilterChain();

        if (vf != null || _settings.HasAnyEdits)
        {
            if (vf != null) sb.Append($"-vf \"{vf}\" ");

            if (ext == ".webm")
            {
                sb.Append($"-c:v libvpx-vp9 -b:v 0 -crf {crf} -pix_fmt yuv420p ");
            }
            else if (useGpu && _hasNvidia)
            {
                sb.Append($"-c:v h264_nvenc -preset fast -rc vbr -cq {crf} -b:v 0 -pix_fmt yuv420p ");
            }
            else if (useGpu && _hasAmd)
            {
                sb.Append($"-c:v h264_amf -rc cqp -qp_i {crf} -qp_p {crf} -pix_fmt yuv420p ");
            }
            else if (useGpu && _hasIntel)
            {
                sb.Append($"-c:v h264_qsv -global_quality {crf} -pix_fmt nv12 ");
            }
            else
            {
                sb.Append($"-c:v libx264 -preset fast -crf {crf} -pix_fmt yuv420p ");
            }
        }
        else
        {
            sb.Append("-c:v copy ");
        }

        // Audio
        if (_settings.MuteAudio)
        {
            sb.Append("-an ");
        }
        else
        {
            var af = _settings.BuildAudioFilterChain();
            string audioCodec = ext switch
            {
                ".webm" => "libopus -b:a 128k",
                ".avi"  => "libmp3lame -q:a 2",
                _       => "aac -b:a 192k"
            };

            if (af != null)
            {
                sb.Append($"-af \"{af}\" ");
                sb.Append($"-c:a {audioCodec} ");
            }
            else
            {
                if (vf != null || _settings.HasAnyEdits || ext == ".webm")
                    sb.Append($"-c:a {audioCodec} ");
                else
                    sb.Append("-c:a copy ");
            }
        }

        sb.Append("-map_metadata 0 ");
        AppendContainerFlags(sb, output);
        sb.Append("-progress pipe:1 ");
        sb.Append($"\"{output}\"");

        return sb.ToString();
    }

    private static string SanitizeFileName(string name)
    {
        var invalids = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (!invalids.Contains(c) && c != '|' && c != '?' && c != '*' && c != '<' && c != '>' && c != '"' && c != ':')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.ToString().Trim();
    }

    private void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var folder = string.IsNullOrEmpty(_lastOutputFolder) ? GetDefaultOutputDir() : _lastOutputFolder;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                Process.Start("explorer.exe", folder);
            else
                MessageBox.Show("Output folder not found or no video loaded yet.", "VideoFixPro", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string GetDefaultOutputDir() =>
        string.IsNullOrEmpty(_customOutputFolder)
            ? (Path.GetDirectoryName(_filePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))
            : _customOutputFolder;

    private async Task<bool> RunFFmpegAsync(string args, double totalDuration, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FFmpeg,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (_hasNvidia) GpuHelper.InjectNvCudaPath(psi);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        ProcessGuard.Watch(proc);
        _ffmpegProcess = proc;

        try
        {
            // Capture stderr for log & telemetry
            var stderrTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                {
                    if (Dispatcher.HasShutdownStarted) break;
                    string currentLine = line;
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        Log($"  {currentLine}");
                        UpdateTelemetryFromStderr(currentLine);
                    });
                }
            });

            // Parse progress from stdout (-progress pipe:1)
            await Task.Run(async () =>
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                {
                    if (ct.IsCancellationRequested || Dispatcher.HasShutdownStarted) break;

                    if (line.StartsWith("out_time_us=") &&
                        long.TryParse(line[12..], out long us) && totalDuration > 0)
                    {
                        double currentSec = us / 1_000_000.0;
                        double pct = Math.Min(currentSec / totalDuration * 100, 99.9);
                        Dispatcher.Invoke(() => SetRenderProgress(pct));
                    }
                    else if (line.StartsWith("out_time_ms=") &&
                        long.TryParse(line[12..], out long ms) && totalDuration > 0)
                    {
                        double currentSec = ms / 1_000_000.0;
                        double pct = Math.Min(currentSec / totalDuration * 100, 99.9);
                        Dispatcher.Invoke(() => SetRenderProgress(pct));
                    }
                    else if (line.StartsWith("out_time=") && totalDuration > 0)
                    {
                        string timeStr = line[9..].Trim();
                        if (TimeSpan.TryParse(timeStr, out var ts))
                        {
                            double pct = Math.Min(ts.TotalSeconds / totalDuration * 100, 99.9);
                            Dispatcher.Invoke(() => SetRenderProgress(pct));
                        }
                    }
                    else if (line.StartsWith("speed="))
                    {
                        string speed = line[6..].Trim();
                        Dispatcher.Invoke(() =>
                        {
                            if (RenderTelemetryText != null)
                                RenderTelemetryText.Text = $"⚡ {speed}";
                        });
                    }
                }
            }, ct);

            if (ct.IsCancellationRequested)
            {
                try { proc.Kill(); } catch { }
                return false;
            }

            try { await proc.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { }

            await stderrTask;
            return proc.ExitCode == 0;
        }
        finally
        {
            ProcessGuard.Unwatch(proc);
            _ffmpegProcess = null;
        }
    }

    private void UpdateTelemetryFromStderr(string line)
    {
        var match = Regex.Match(line, @"fps=\s*([\d\.]+).*speed=\s*([\d\.x]+)");
        if (match.Success && RenderTelemetryText != null)
        {
            string fps = match.Groups[1].Value;
            string speed = match.Groups[2].Value;
            var elapsed = DateTime.Now - _renderStartTime;
            RenderTelemetryText.Text = $"⚡ {speed} · 🎬 {fps} fps · ⏱️ {elapsed:mm\\:ss}";
        }

        // Dual fallback: also parse time progress directly from standard FFmpeg stderr line
        var timeMatch = Regex.Match(line, @"time=(\d{2}:\d{2}:\d{2}\.\d+)");
        if (timeMatch.Success && _durationSeconds > 0)
        {
            if (TimeSpan.TryParse(timeMatch.Groups[1].Value, out var ts))
            {
                double pct = Math.Min(ts.TotalSeconds / _durationSeconds * 100, 99.9);
                SetRenderProgress(pct);
            }
        }
    }

    private static void AppendContainerFlags(StringBuilder sb, string output)
    {
        var ext = Path.GetExtension(output).ToLowerInvariant();
        switch (ext)
        {
            case ".mp4":
            case ".m4v":
            case ".mov":
                sb.Append("-movflags +faststart+use_metadata_tags ");
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  UI HELPERS
    // ═══════════════════════════════════════════════════════════════════════════
    private void SetRenderingUI(bool running)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            if (RenderBtn != null) RenderBtn.IsEnabled = !running;
            if (QuickSaveBtn != null) QuickSaveBtn.IsEnabled = !running;
            if (CancelBtn != null) CancelBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (RenderProgressPanel != null) RenderProgressPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (TaskbarProgress != null)
            {
                TaskbarProgress.ProgressState = running
                    ? TaskbarItemProgressState.Normal
                    : TaskbarItemProgressState.None;
                if (!running) TaskbarProgress.ProgressValue = 0;
            }
            if (OpenFolderBtn != null)
                OpenFolderBtn.IsEnabled = !running && (!string.IsNullOrEmpty(_lastOutputFolder) || !string.IsNullOrEmpty(_filePath) || !string.IsNullOrEmpty(_customOutputFolder));
            if (!running && RenderProgressBar != null && RenderProgressText != null && RenderTelemetryText != null)
            {
                RenderProgressBar.Value = 0;
                RenderProgressText.Text = "0%";
                RenderTelemetryText.Text = "";
            }
        });
    }

    private void SetRenderProgress(double pct)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.Invoke(() =>
        {
            double safePct = Math.Clamp(pct, 0, 100);
            if (RenderProgressBar != null) RenderProgressBar.Value = safePct;
            if (RenderProgressText != null) RenderProgressText.Text = $"{safePct:F0}%";
            if (TaskbarProgress != null) TaskbarProgress.ProgressValue = safePct / 100.0;
            if (StatusText != null) StatusText.Text = $"{(_isRendering ? "Saving..." : "Rendering...")} {safePct:F0}%";
        });
    }

    private void Log(string msg)
    {
        if (Dispatcher.HasShutdownStarted || LogBox == null) return;
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {UiTextSanitizer.Normalize(msg)}\n");
            LogBox.ScrollToEnd();
        });
    }

    private void SetStatus(string text, string colorHex = "#8B949E")
    {
        if (Dispatcher.HasShutdownStarted || StatusText == null || StatusDot == null) return;
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = UiTextSanitizer.Normalize(text);
            StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        });
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.Hours > 0 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        int i = 1;
        while (File.Exists(path)) path = Path.Combine(dir, $"{name} ({i++}){ext}");
        return path;
    }

    private void ShowNotification(string title, string text)
    {
        try
        {
            var ni = new WinForms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                    Process.GetCurrentProcess().MainModule?.FileName ?? ""),
                Visible = true,
                BalloonTipTitle = title,
                BalloonTipText = text,
                BalloonTipIcon = WinForms.ToolTipIcon.Info
            };
            ni.ShowBalloonTip(3500);
            Task.Delay(5000).ContinueWith(_ => ni.Dispose());
        }
        catch { }
    }

    private static async Task<string> RunProcessAsync(string exe, string args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        ProcessGuard.Watch(p);

        using var registration = ct.Register(() => { try { p.Kill(); } catch { } });

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();

        await Task.WhenAll(outTask, errTask);
        try { await p.WaitForExitAsync(ct); } catch (OperationCanceledException) { }
        finally { ProcessGuard.Unwatch(p); }

        return await outTask;
    }
}
