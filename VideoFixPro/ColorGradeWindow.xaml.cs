using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoFixPro;

public partial class ColorGradeWindow : Window
{
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

    private bool _isInitialized;
    private string _filePath = string.Empty;
    private string? _lutPath;
    private double _durationSeconds;
    private int _sourceWidth;
    private int _sourceHeight;
    private double _videoRotation = 0.0;
    private readonly ColorGradeEffect _colorGradeEffect = new();

    // Color Grading Parameters
    private double _brightness = 0.0;    // -100 to +100
    private double _contrast = 100.0;    // 0 to 250%
    private double _gamma = 100.0;       // 20 to 250% (0.20 to 2.50)
    private double _sharpness = 0.0;     // 0 to 200 (0.0 to 2.0)
    private double _saturation = 100.0;  // 0 to 250%
    private double _temperature = 0.0;   // -100 (Cool) to +100 (Warm)
    private double _tint = 0.0;          // -100 (Green) to +100 (Magenta)
    private double _vignette = 0.0;      // 0 to 100%
    private string _activePreset = "Original";

    // Player state
    private readonly System.Windows.Threading.DispatcherTimer _playheadTimer = new();
    private bool _isPlayerPlaying;
    private bool _isSeeking;

    // Rendering state
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _previewRenderCts;
    private Process? _ffmpegProcess;
    private bool _isRendering;
    private bool _isComparingOriginal;
    private string _customOutputFolder = string.Empty;
    private string _lastOutputFolder = string.Empty;

    // Hardware encoder support
    private readonly bool _hasNvidia;
    private readonly bool _hasAmd;
    private readonly bool _hasIntel;

    public ColorGradeWindow(string? initialFilePath = null, bool hasNvidia = false, bool hasAmd = false, bool hasIntel = false)
    {
        InitializeComponent();

        _hasNvidia = hasNvidia;
        _hasAmd = hasAmd;
        _hasIntel = hasIntel;

        _playheadTimer.Interval = TimeSpan.FromMilliseconds(50);
        _playheadTimer.Tick += PlayheadTimer_Tick;

        if (VideoViewport != null)
            VideoViewport.Effect = _colorGradeEffect;

        _isInitialized = true;

        if (!string.IsNullOrEmpty(initialFilePath) && File.Exists(initialFilePath))
        {
            Dispatcher.BeginInvoke(new Action(async () => await LoadFileAsync(initialFilePath)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    // ── Window Chrome Controls ────────────────────────────────────────────────
    private void TitleBar_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) MaxBtn_Click(s, e);
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void MinBtn_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxBtn_Click(object s, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        if (MaxBtn != null) MaxBtn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }
    private void CloseBtn_Click(object s, RoutedEventArgs e) => Close();

    // ── Drag & Drop ───────────────────────────────────────────────────────────
    private void Window_DragEnter(object s, DragEventArgs e) => HandleDrag(e);
    private void Window_DragOver(object s, DragEventArgs e) => HandleDrag(e);
    private static void HandleDrag(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }
    private async void Window_Drop(object s, DragEventArgs e)
    {
            try
            {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files?.Length > 0)
                await LoadFileAsync(files[0]);
        }
    }
            catch (Exception ex)
            {
                MessageBox.Show("An unexpected error occurred: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    private void DropZone_Click(object s, MouseButtonEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Video for Color Grading",
            Filter = "Video Files (*.mp4;*.mkv;*.mov;*.webm;*.avi;*.flv;*.wmv)|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.flv;*.wmv|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            _ = LoadFileAsync(dlg.FileName);
    }
    private void ChangeFile_Click(object s, RoutedEventArgs e) => DropZone_Click(s, null!);

    // ── File Loading & Probing ────────────────────────────────────────────────
    private async Task LoadFileAsync(string path)
    {
            try
            {
        if (!File.Exists(path)) return;
        _filePath = path;

        // UI Reset
        TitleFileName.Text = Path.GetFileName(path);
        HeaderFileName.Text = Path.GetFileName(path);
        DropZone.Visibility = Visibility.Collapsed;
        FileHeader.Visibility = Visibility.Visible;
        PlayerBorder.Visibility = Visibility.Visible;
        SeekPanel.Visibility = Visibility.Visible;
        FilterSummaryBorder.Visibility = Visibility.Visible;

        SetStatus($"Loading {Path.GetFileName(path)}...", "#388BFD");

        Player.Source = new Uri(path);
        Player.Play();
        Player.Pause();
        _isPlayerPlaying = false;
        SeekPlayBtn.Content = "▶";
        PlayPauseBtn.Content = "▶";

        if (OpenFolderBtn != null) OpenFolderBtn.IsEnabled = true;

        await ProbeVideoAsync(path);
        UpdateFilterSummary();
        UpdateLivePreview();
        SetStatus($"Ready to color grade · {Path.GetFileName(path)}", "#3FB950");
    }
            catch (Exception ex)
            {
                MessageBox.Show("An unexpected error occurred: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    private async Task ProbeVideoAsync(string path)
    {
        if (!File.Exists(FFprobe)) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FFprobe,
                Arguments = $"-v quiet -print_format json -show_format -show_streams \"{path}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            string json = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var durMatch = Regex.Match(json, @"""duration"":\s*""([0-9.]+)""");
            if (durMatch.Success && double.TryParse(durMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
            {
                _durationSeconds = d;
                HeaderDuration.Text = TimeSpan.FromSeconds(d).ToString(@"hh\:mm\:ss");
                if (SeekTimeText != null)
                    SeekTimeText.Text = $"00:00:00 / {TimeSpan.FromSeconds(d):hh\\:mm\\:ss}";
            }

            var wMatch = Regex.Match(json, @"""width"":\s*(\d+)");
            var hMatch = Regex.Match(json, @"""height"":\s*(\d+)");
            if (wMatch.Success && hMatch.Success)
            {
                _sourceWidth = int.Parse(wMatch.Groups[1].Value);
                _sourceHeight = int.Parse(hMatch.Groups[1].Value);

                int rotation = 0;
                var sideRotMatch = Regex.Match(json, @"""rotation"":\s*(-?\d+)");
                if (sideRotMatch.Success && int.TryParse(sideRotMatch.Groups[1].Value, out int sr))
                {
                    rotation = sr;
                }
                else
                {
                    var tagRotMatch = Regex.Match(json, @"""rotate"":\s*""?(-?\d+)""?");
                    if (tagRotMatch.Success && int.TryParse(tagRotMatch.Groups[1].Value, out int tr))
                    {
                        rotation = tr;
                    }
                }

                _videoRotation = 0;
                if (rotation != 0)
                {
                    _videoRotation = ((-rotation % 360) + 360) % 360;
                    if (rotation > 0 && !sideRotMatch.Success)
                        _videoRotation = rotation % 360;
                }

                int displayW = (_videoRotation == 90 || _videoRotation == 270) ? _sourceHeight : _sourceWidth;
                int displayH = (_videoRotation == 90 || _videoRotation == 270) ? _sourceWidth : _sourceHeight;
                HeaderResolution.Text = $"{displayW}x{displayH}";

                Dispatcher.Invoke(() =>
                {
                    ApplyPlayerDimensionsAndRotation();
                });
            }

            var codecMatch = Regex.Match(json, @"""codec_name"":\s*""([^""]+)""");
            if (codecMatch.Success)
            {
                HeaderCodec.Text = codecMatch.Groups[1].Value.ToUpperInvariant();
            }
        }
        catch { }
    }

    // ── Sliders & Controls Event Handling ─────────────────────────────────────
    private void Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;

        if (s == BrightnessSlider)
        {
            _brightness = Math.Round(BrightnessSlider.Value, 0);
            if (BrightnessText != null) BrightnessText.Text = $"{(_brightness >= 0 ? "+" : "")}{_brightness:F0}%";
        }
        else if (s == ContrastSlider)
        {
            _contrast = Math.Round(ContrastSlider.Value, 0);
            if (ContrastText != null) ContrastText.Text = $"{_contrast:F0}%";
        }
        else if (s == GammaSlider)
        {
            _gamma = Math.Round(GammaSlider.Value, 0);
            if (GammaText != null) GammaText.Text = (_gamma / 100.0).ToString("F2", CultureInfo.InvariantCulture);
        }
        else if (s == SharpnessSlider)
        {
            _sharpness = Math.Round(SharpnessSlider.Value, 0);
            if (SharpnessText != null) SharpnessText.Text = (_sharpness / 100.0).ToString("F1", CultureInfo.InvariantCulture);
        }
        else if (s == SaturationSlider)
        {
            _saturation = Math.Round(SaturationSlider.Value, 0);
            if (SaturationText != null) SaturationText.Text = $"{_saturation:F0}%";
        }
        else if (s == TemperatureSlider)
        {
            _temperature = Math.Round(TemperatureSlider.Value, 0);
            if (TemperatureText != null) TemperatureText.Text = $"{(_temperature >= 0 ? "+" : "")}{_temperature:F0}";
        }
        else if (s == TintSlider)
        {
            _tint = Math.Round(TintSlider.Value, 0);
            if (TintText != null) TintText.Text = $"{(_tint >= 0 ? "+" : "")}{_tint:F0}";
        }
        else if (s == VignetteSlider)
        {
            _vignette = Math.Round(VignetteSlider.Value, 0);
            if (VignetteText != null) VignetteText.Text = $"{_vignette:F0}%";
        }

        UpdateFilterSummary();
        UpdateLivePreview();
    }

    private void QualitySlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityText != null)
            QualityText.Text = $"{(int)QualitySlider.Value}%";
    }

    // ── 1-Click Cinematic Presets ─────────────────────────────────────────────
    private void PresetOriginal_Click(object s, RoutedEventArgs e) => ApplyPreset("Original", 0, 100, 100, 0, 100, 0, 0, 0);
    private void PresetTealOrange_Click(object s, RoutedEventArgs e) => ApplyPreset("TealOrange", 2, 122, 100, 20, 125, 28, -15, 12);
    private void PresetGoldenHour_Click(object s, RoutedEventArgs e) => ApplyPreset("GoldenHour", 4, 112, 105, 0, 120, 42, 6, 10);
    private void PresetFilm35_Click(object s, RoutedEventArgs e) => ApplyPreset("Film35", 3, 106, 112, 15, 82, 14, -5, 22);
    private void PresetNoirBW_Click(object s, RoutedEventArgs e) => ApplyPreset("NoirBW", -2, 138, 96, 35, 0, 0, 0, 25);
    private void PresetVividHDR_Click(object s, RoutedEventArgs e) => ApplyPreset("VividHDR", 0, 124, 96, 75, 136, 0, 0, 0);
    private void PresetNordicCold_Click(object s, RoutedEventArgs e) => ApplyPreset("NordicCold", 0, 114, 100, 15, 75, -36, 0, 15);
    private void PresetPastel_Click(object s, RoutedEventArgs e) => ApplyPreset("Pastel", 4, 92, 118, 0, 110, 8, 12, 0);
    private void PresetEmerald_Click(object s, RoutedEventArgs e) => ApplyPreset("Emerald", 0, 116, 100, 20, 110, -10, -28, 8);
    private void PresetSepia_Click(object s, RoutedEventArgs e) => ApplyPreset("Sepia", 2, 108, 100, 0, 20, 48, -10, 25);

    private void ApplyPreset(string name, double bright, double cont, double gam, double sharp, double sat, double temp, double tint, double vig)
    {
        _activePreset = name;
        _isInitialized = false;

        BrightnessSlider.Value = bright;
        ContrastSlider.Value = cont;
        GammaSlider.Value = gam;
        SharpnessSlider.Value = sharp;
        SaturationSlider.Value = sat;
        TemperatureSlider.Value = temp;
        TintSlider.Value = tint;
        VignetteSlider.Value = vig;

        _brightness = bright;
        _contrast = cont;
        _gamma = gam;
        _sharpness = sharp;
        _saturation = sat;
        _temperature = temp;
        _tint = tint;
        _vignette = vig;

        BrightnessText.Text = $"{(_brightness >= 0 ? "+" : "")}{_brightness:F0}%";
        ContrastText.Text = $"{_contrast:F0}%";
        GammaText.Text = (_gamma / 100.0).ToString("F2", CultureInfo.InvariantCulture);
        SharpnessText.Text = (_sharpness / 100.0).ToString("F1", CultureInfo.InvariantCulture);
        SaturationText.Text = $"{_saturation:F0}%";
        TemperatureText.Text = $"{(_temperature >= 0 ? "+" : "")}{_temperature:F0}";
        TintText.Text = $"{(_tint >= 0 ? "+" : "")}{_tint:F0}";
        VignetteText.Text = $"{_vignette:F0}%";

        _isInitialized = true;

        UpdatePresetButtons();
        UpdateFilterSummary();
        UpdateLivePreview();
    }

    private void UpdatePresetButtons()
    {
        var active = (Style)FindResource("ActiveToolButton");
        var ghost = (Style)FindResource("GhostButton");

        if (PresetOriginal != null) PresetOriginal.Style = _activePreset == "Original" ? active : ghost;
        if (PresetTealOrange != null) PresetTealOrange.Style = _activePreset == "TealOrange" ? active : ghost;
        if (PresetGoldenHour != null) PresetGoldenHour.Style = _activePreset == "GoldenHour" ? active : ghost;
        if (PresetFilm35 != null) PresetFilm35.Style = _activePreset == "Film35" ? active : ghost;
        if (PresetNoirBW != null) PresetNoirBW.Style = _activePreset == "NoirBW" ? active : ghost;
        if (PresetVividHDR != null) PresetVividHDR.Style = _activePreset == "VividHDR" ? active : ghost;
        if (PresetNordicCold != null) PresetNordicCold.Style = _activePreset == "NordicCold" ? active : ghost;
        if (PresetPastel != null) PresetPastel.Style = _activePreset == "Pastel" ? active : ghost;
        if (PresetEmerald != null) PresetEmerald.Style = _activePreset == "Emerald" ? active : ghost;
        if (PresetSepia != null) PresetSepia.Style = _activePreset == "Sepia" ? active : ghost;
    }

    private void BrowseLut_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select 3D LUT",
            Filter = "3D LUT Files (*.cube)|*.cube|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            _lutPath = dlg.FileName;
            if (LutPathText != null) LutPathText.Text = System.IO.Path.GetFileName(_lutPath);
            TriggerDebouncedFrameRender();
        }
    }

    private void ClearLut_Click(object s, RoutedEventArgs e)
    {
        _lutPath = null;
        if (LutPathText != null) LutPathText.Text = "No LUT selected";
        TriggerDebouncedFrameRender();
    }

    private void ResetAll_Click(object s, RoutedEventArgs e)
    {
        PresetOriginal_Click(s, e);
    }

    private void CompareBtn_MouseDown(object s, MouseButtonEventArgs e)
    {
        _isComparingOriginal = true;
        UpdateLivePreview();
    }

    private void CompareBtn_MouseUp(object s, MouseButtonEventArgs e)
    {
        _isComparingOriginal = false;
        UpdateLivePreview();
    }

    private void UpdateLivePreview()
    {
        if (_isComparingOriginal)
        {
            if (VideoViewport != null) VideoViewport.Effect = null;
            if (GradedFramePreview != null) GradedFramePreview.Visibility = Visibility.Collapsed;
            if (PreviewBadgeText != null) PreviewBadgeText.Text = "👁️ Viewing Original (Unedited)";
            return;
        }

        if (VideoViewport != null && VideoViewport.Effect != _colorGradeEffect)
            VideoViewport.Effect = _colorGradeEffect;

        if (PreviewBadgeText != null)
            PreviewBadgeText.Text = _activePreset != "Original" ? $"✨ GPU Color Grade ({GetPresetDisplayName(_activePreset)})" : "✨ Real-Time GPU Grading";

        // Push real grading math directly to GPU PixelShader (DirectX 60+ FPS)
        _colorGradeEffect.Brightness = (_brightness / 100.0) * 0.40;
        _colorGradeEffect.Contrast = _contrast / 100.0;
        _colorGradeEffect.Gamma = _gamma / 100.0;
        _colorGradeEffect.Saturation = _saturation / 100.0;
        _colorGradeEffect.Temperature = _temperature / 100.0;
        _colorGradeEffect.Tint = _tint / 100.0;
        _colorGradeEffect.Vignette = _vignette / 100.0;

        // If a 3D LUT is selected, trigger FFmpeg snapshot for LUT preview
        if (!string.IsNullOrEmpty(_lutPath) && File.Exists(_lutPath))
        {
            TriggerDebouncedFrameRender();
        }
        else if (GradedFramePreview != null && GradedFramePreview.Visibility == Visibility.Visible)
        {
            GradedFramePreview.Visibility = Visibility.Collapsed;
        }
    }

    private void TriggerDebouncedFrameRender()
    {
        if (_isPlayerPlaying || string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath) || !File.Exists(FFmpeg))
        {
            if (GradedFramePreview != null && _isPlayerPlaying)
                GradedFramePreview.Visibility = Visibility.Collapsed;
            return;
        }

        _previewRenderCts?.Cancel();
        _previewRenderCts = new CancellationTokenSource();
        var token = _previewRenderCts.Token;

        double currentTime = Player.Position.TotalSeconds;
        if (_durationSeconds > 0 && currentTime >= _durationSeconds)
            currentTime = Math.Max(0, _durationSeconds - 0.1);

        string vf = BuildFilterChain();

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, token);
                if (token.IsCancellationRequested) return;

                string timeStr = currentTime.ToString("F3", CultureInfo.InvariantCulture);
                string filterArg = (vf != "null" && !string.IsNullOrWhiteSpace(vf)) ? $"-vf \"{vf}\"" : "";

                var psi = new ProcessStartInfo
                {
                    FileName = FFmpeg,
                    Arguments = $"-noautorotate -ss {timeStr} -i \"{_filePath}\" {filterArg} -an -sn -frames:v 1 -f image2pipe -c:v png -",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
                if (_hasNvidia) GpuHelper.InjectNvCudaPath(psi);

                using var proc = Process.Start(psi);
                if (proc == null) return;
                ProcessGuard.Watch(proc);

                try
                {
                    using var ms = new MemoryStream();
                    await proc.StandardOutput.BaseStream.CopyToAsync(ms, token);
                    await proc.WaitForExitAsync(token);

                    if (ms.Length > 100 && !token.IsCancellationRequested)
                    {
                        ms.Position = 0;
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();

                        Dispatcher.Invoke(() =>
                        {
                            if (!token.IsCancellationRequested && !_isPlayerPlaying && GradedFramePreview != null && !_isComparingOriginal)
                            {
                                GradedFramePreview.Source = bmp;
                                GradedFramePreview.Visibility = Visibility.Visible;
                            }
                        });
                    }
                }
                finally
                {
                    ProcessGuard.Unwatch(proc);
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                }
            }
            catch { }
        }, token);
    }

    private void UpdateFilterSummary()
    {
        if (ActiveFilterSummaryText == null) return;

        var parts = new List<string>();
        if (Math.Abs(_brightness) > 0.01) parts.Add($"Brightness: {(_brightness >= 0 ? "+" : "")}{_brightness:F0}%");
        if (Math.Abs(_contrast - 100) > 0.01) parts.Add($"Contrast: {_contrast:F0}%");
        if (Math.Abs(_gamma - 100) > 0.01) parts.Add($"Gamma: {(_gamma / 100.0):F2}");
        if (_sharpness > 0.01) parts.Add($"Sharpness: {(_sharpness / 100.0):F1}");
        if (Math.Abs(_saturation - 100) > 0.01) parts.Add($"Saturation: {_saturation:F0}%");
        if (Math.Abs(_temperature) > 0.01) parts.Add($"Warmth: {(_temperature >= 0 ? "+" : "")}{_temperature:F0}");
        if (Math.Abs(_tint) > 0.01) parts.Add($"Tint: {(_tint >= 0 ? "+" : "")}{_tint:F0}");
        if (_vignette > 0.01) parts.Add($"Vignette: {_vignette:F0}%");

        if (parts.Count == 0)
        {
            ActiveFilterSummaryText.Text = "Original (Flat / No adjustments)";
        }
        else
        {
            string summary = string.Join(" · ", parts);
            if (_activePreset != "Original")
                summary = $"[{GetPresetDisplayName(_activePreset)}] " + summary;
            ActiveFilterSummaryText.Text = summary;
        }
    }

    private static string GetPresetDisplayName(string key) => key switch
    {
        "TealOrange" => "Teal & Orange",
        "GoldenHour" => "Golden Hour",
        "Film35"     => "35mm Vintage",
        "NoirBW"     => "Noir B&W",
        "VividHDR"   => "Vivid HDR",
        "NordicCold" => "Cold Nordic",
        "Pastel"     => "Pastel Dream",
        "Emerald"    => "Emerald Cine",
        "Sepia"      => "Warm Sepia",
        _            => "Custom Grade"
    };

    // ── FFmpeg Filter Construction ────────────────────────────────────────────
    private string BuildFilterChain()
    {
        var filters = new List<string>();

        double b = _brightness / 100.0;
        double c = _contrast / 100.0;
        double s = _saturation / 100.0;
        double g = _gamma / 100.0;

        // 1. Basic Exposure & Color EQ
        if (Math.Abs(b) > 0.001 || Math.Abs(c - 1.0) > 0.001 || Math.Abs(s - 1.0) > 0.001 || Math.Abs(g - 1.0) > 0.001)
        {
            string bStr = b.ToString("F2", CultureInfo.InvariantCulture);
            string cStr = c.ToString("F2", CultureInfo.InvariantCulture);
            string sStr = s.ToString("F2", CultureInfo.InvariantCulture);
            string gStr = g.ToString("F2", CultureInfo.InvariantCulture);
            filters.Add($"eq=brightness={bStr}:contrast={cStr}:saturation={sStr}:gamma={gStr}");
        }

        // 2. Temperature (Warmth) and Tint (Color Balance)
        if (Math.Abs(_temperature) > 0.1 || Math.Abs(_tint) > 0.1)
        {
            double temp = _temperature / 100.0;
            double tint = _tint / 100.0;

            double rs = temp * 0.15;
            double gs = tint * 0.10;
            double bs = -temp * 0.15;

            double rm = temp * 0.25;
            double gm = tint * 0.15;
            double bm = -temp * 0.25;

            double rh = temp * 0.18;
            double gh = tint * 0.12;
            double bh = -temp * 0.18;

            string rsStr = rs.ToString("F3", CultureInfo.InvariantCulture);
            string gsStr = gs.ToString("F3", CultureInfo.InvariantCulture);
            string bsStr = bs.ToString("F3", CultureInfo.InvariantCulture);
            string rmStr = rm.ToString("F3", CultureInfo.InvariantCulture);
            string gmStr = gm.ToString("F3", CultureInfo.InvariantCulture);
            string bmStr = bm.ToString("F3", CultureInfo.InvariantCulture);
            string rhStr = rh.ToString("F3", CultureInfo.InvariantCulture);
            string ghStr = gh.ToString("F3", CultureInfo.InvariantCulture);
            string bhStr = bh.ToString("F3", CultureInfo.InvariantCulture);

            filters.Add($"colorbalance=rs={rsStr}:gs={gsStr}:bs={bsStr}:rm={rmStr}:gm={gmStr}:bm={bmStr}:rh={rhStr}:gh={ghStr}:bh={bhStr}");
        }

        // 3. Sharpness / Clarity
        if (_sharpness > 0.05)
        {
            double sh = _sharpness / 100.0;
            string shStr = sh.ToString("F2", CultureInfo.InvariantCulture);
            filters.Add($"unsharp=5:5:{shStr}:5:5:0.0");
        }

        // 4. Atmosphere / Vignette
        if (_vignette > 0.5)
        {
            double angle = (_vignette / 100.0) * (Math.PI / 3.0);
            string angleStr = angle.ToString("F3", CultureInfo.InvariantCulture);
            filters.Add($"vignette=angle={angleStr}");
        }

        if (!string.IsNullOrWhiteSpace(_lutPath))
        {
            var escapedPath = _lutPath.Replace("\\", "/").Replace(":", "\\:").Replace("'", @"'\''");
            filters.Add($"lut3d=file='{escapedPath}'");
        }

        return filters.Count > 0 ? string.Join(",", filters) : "null";
    }

    private void ApplyPlayerDimensionsAndRotation()
    {
        int rawW = _sourceWidth > 0 ? _sourceWidth : (Player?.NaturalVideoWidth > 0 ? Player.NaturalVideoWidth : 1920);
        int rawH = _sourceHeight > 0 ? _sourceHeight : (Player?.NaturalVideoHeight > 0 ? Player.NaturalVideoHeight : 1080);

        if (Player != null)
        {
            Player.Width = rawW;
            Player.Height = rawH;
        }

        if (GradedFramePreview != null)
        {
            GradedFramePreview.Width = rawW;
            GradedFramePreview.Height = rawH;
        }

        if (PlayerRotator != null)
        {
            PlayerRotator.Width = rawW;
            PlayerRotator.Height = rawH;
            if (_videoRotation != 0)
                PlayerRotator.LayoutTransform = new RotateTransform(_videoRotation);
            else
                PlayerRotator.LayoutTransform = Transform.Identity;
        }
    }

    // ── Player Controls ───────────────────────────────────────────────────────
    private void Player_MediaOpened(object s, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan && _durationSeconds <= 0)
        {
            _durationSeconds = Player.NaturalDuration.TimeSpan.TotalSeconds;
            HeaderDuration.Text = TimeSpan.FromSeconds(_durationSeconds).ToString(@"hh\:mm\:ss");
        }
        if (Player.NaturalVideoWidth > 0 && _sourceWidth <= 0)
        {
            _sourceWidth = Player.NaturalVideoWidth;
            _sourceHeight = Player.NaturalVideoHeight;
            int displayW = (_videoRotation == 90 || _videoRotation == 270) ? _sourceHeight : _sourceWidth;
            int displayH = (_videoRotation == 90 || _videoRotation == 270) ? _sourceWidth : _sourceHeight;
            HeaderResolution.Text = $"{displayW}x{displayH}";
        }

        ApplyPlayerDimensionsAndRotation();
        UpdateLivePreview();
    }

    private void Player_MediaEnded(object s, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.Zero;
        _isPlayerPlaying = false;
        SeekPlayBtn.Content = "▶";
        PlayPauseBtn.Content = "▶";
        _playheadTimer.Stop();
    }

    private void TogglePlay_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_filePath)) return;

        if (_isPlayerPlaying)
        {
            Player.Pause();
            _isPlayerPlaying = false;
            SeekPlayBtn.Content = "▶";
            PlayPauseBtn.Content = "▶";
            _playheadTimer.Stop();
        }
        else
        {
            if (GradedFramePreview != null) GradedFramePreview.Visibility = Visibility.Collapsed;
            Player.Play();
            _isPlayerPlaying = true;
            SeekPlayBtn.Content = "⏸";
            PlayPauseBtn.Content = "⏸";
            _playheadTimer.Start();
        }
    }

    private void PlayheadTimer_Tick(object? s, EventArgs e)
    {
        if (_isSeeking || _durationSeconds <= 0) return;
        double current = Player.Position.TotalSeconds;
        SeekSlider.Value = (current / _durationSeconds) * 100.0;
        SeekTimeText.Text = $"{TimeSpan.FromSeconds(current):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(_durationSeconds):hh\\:mm\\:ss}";
    }

    private void SeekSlider_MouseDown(object s, MouseButtonEventArgs e) => _isSeeking = true;
    private void SeekSlider_MouseUp(object s, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        if (_durationSeconds > 0)
        {
            double target = (SeekSlider.Value / 100.0) * _durationSeconds;
            Player.Position = TimeSpan.FromSeconds(target);
            if (!_isPlayerPlaying && !string.IsNullOrEmpty(_lutPath) && File.Exists(_lutPath))
            {
                TriggerDebouncedFrameRender();
            }
        }
    }
    private void SeekSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking && _durationSeconds > 0)
        {
            double target = (SeekSlider.Value / 100.0) * _durationSeconds;
            Player.Position = TimeSpan.FromSeconds(target);
            SeekTimeText.Text = $"{TimeSpan.FromSeconds(target):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(_durationSeconds):hh\\:mm\\:ss}";
        }
    }

    // ── Output Management ─────────────────────────────────────────────────────
    private void BrowseOutput_Click(object s, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Destination Folder for Graded Video",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _customOutputFolder = dlg.SelectedPath;
            OutputPathText.Text = _customOutputFolder;
            OutputPathText.Foreground = (Brush)FindResource("TextBrush");
        }
    }

    private void ResetOutput_Click(object s, RoutedEventArgs e)
    {
        _customOutputFolder = string.Empty;
        OutputPathText.Text = "Same as source file";
        OutputPathText.Foreground = (Brush)FindResource("MutedBrush");
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

    // ── Color Grade Execution Pipeline ────────────────────────────────────────
    private async void ApplyGrade_Click(object s, RoutedEventArgs e)
    {
            try
            {
        if (_isRendering) return;
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
        {
            MessageBox.Show("Please load a video file first.", "No Video", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ExecuteColorGradeAsync();
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

    private async Task ExecuteColorGradeAsync()
    {
        _isRendering = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        string dir = !string.IsNullOrEmpty(_customOutputFolder) ? _customOutputFolder : Path.GetDirectoryName(_filePath) ?? ".";
        string safeName = Path.GetFileNameWithoutExtension(_filePath);
        string ext = ((ComboBoxItem)OutputFormatBox.SelectedItem)?.Content?.ToString()?.Split('(')[0].Trim().ToLower() ?? "mp4";
        string outputPath = Path.Combine(dir, $"{safeName}_graded.{ext}");
        outputPath = GetUniqueFilePath(outputPath);
        _lastOutputFolder = dir;

        if (_durationSeconds <= 0)
        {
            await ProbeVideoAsync(_filePath);
        }

        SetRenderingUI(true);
        SetStatus("Rendering color graded video... 0%", "#388BFD");
        Log($"\n[COLOR GRADE] Source: {Path.GetFileName(_filePath)}");
        Log($"[COLOR GRADE] Preset: {GetPresetDisplayName(_activePreset)}");
        Log($"[COLOR GRADE] Output: {outputPath}");

        string vf = BuildFilterChain();
        string filterArg = vf != "null" ? $"-vf \"{vf}\"" : "";

        // Quality mapping
        int qualityPct = (int)QualitySlider.Value;
        int crf = (int)Math.Round(35 - (qualityPct / 100.0 * 20)); // 75% -> CRF 20

        bool useGpu = (GpuCheck.IsChecked == true) && (_hasNvidia || _hasAmd || _hasIntel);
        string vCodecArgs =                             useGpu && _hasNvidia ? $"-c:v h264_nvenc -preset fast -rc vbr -cq {crf} -b:v 0 -pix_fmt yuv420p" :
useGpu && _hasAmd ? $"-c:v h264_amf -rc 0 -qp_i {crf} -qp_p {crf} -qp_b {crf} -pix_fmt yuv420p" :
                            useGpu && _hasIntel ? $"-c:v h264_qsv -global_quality {crf} -pix_fmt nv12" :
                            $"-c:v libx264 -preset fast -crf {crf} -pix_fmt yuv420p";

        string args = $"-y -i \"{_filePath}\" {filterArg} -map 0:v -map 0:a? {vCodecArgs} -c:a aac -b:a 192k \"{outputPath}\"";
        Log($"[CMD] ffmpeg {args}");

        bool success = await RunFFmpegAsync(args, _durationSeconds, _cts.Token);

        // Auto CPU Fallback if GPU fails
        if (!success && !_cts.Token.IsCancellationRequested && useGpu)
        {
            Log("[WARN] GPU encoding failed. Retrying on CPU (libx264)...");
            SetStatus("Retrying on CPU...", "#D29922");
            args = $"-y -i \"{_filePath}\" {filterArg} -map 0:v -map 0:a? -c:v libx264 -preset fast -crf {crf} -pix_fmt yuv420p -c:a aac -b:a 192k \"{outputPath}\"";
            Log($"[CMD Fallback] ffmpeg {args}");
            success = await RunFFmpegAsync(args, _durationSeconds, _cts.Token);
        }

        _isRendering = false;
        SetRenderingUI(false);

        if (_cts.Token.IsCancellationRequested)
        {
            SetStatus("Color grading cancelled.", "#F85149");
            Log("[CANCEL] Export was cancelled by user.");
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
        }
        else if (success && File.Exists(outputPath))
        {
            var fi = new FileInfo(outputPath);
            SetStatus($"Export complete! Size: {fi.Length / 1048576.0:F1} MB", "#3FB950");
            Log($"[SUCCESS] Graded video saved to: {outputPath}");
            if (OpenFolderBtn != null) OpenFolderBtn.IsEnabled = true;
        }
        else
        {
            SetStatus("Color grading failed. Check log for details.", "#F85149");
            Log("[ERROR] FFmpeg process encountered an error.");
        }
    }

    private async Task<bool> RunFFmpegAsync(string args, double totalDuration, CancellationToken ct)
    {
        if (!File.Exists(FFmpeg))
        {
            Log("[ERROR] FFmpeg executable not found.");
            return false;
        }

        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = FFmpeg,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                if (_hasNvidia) GpuHelper.InjectNvCudaPath(psi);

                using var proc = Process.Start(psi);
                if (proc == null) return false;
                ProcessGuard.Watch(proc);
                try
                {
                    _ffmpegProcess = proc;

                    var timeRegex = new Regex(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
                    var fpsRegex = new Regex(@"fps=\s*([\d\.]+)", RegexOptions.Compiled);
                    var speedRegex = new Regex(@"speed=\s*([\d\.]+)x", RegexOptions.Compiled);

                    string? line;
                    while ((line = proc.StandardError.ReadLine()) != null)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            try { proc.Kill(); } catch { }
                            return false;
                        }

                        var match = timeRegex.Match(line);
                        if (match.Success && totalDuration > 0)
                        {
                            double h = double.Parse(match.Groups[1].Value);
                            double m = double.Parse(match.Groups[2].Value);
                            double sec = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                            double currentSeconds = (h * 3600) + (m * 60) + sec;
                            double percent = Math.Clamp((currentSeconds / totalDuration) * 100, 0, 100);

                            var fpsMatch = fpsRegex.Match(line);
                            var speedMatch = speedRegex.Match(line);
                            string speedInfo = speedMatch.Success ? $" · {speedMatch.Groups[1].Value}x" : "";
                            string fpsInfo = fpsMatch.Success ? $" ({fpsMatch.Groups[1].Value} fps)" : "";

                            Dispatcher.InvokeAsync(() =>
                            {
                                if (RenderProgressBar != null) RenderProgressBar.Value = percent;
                                if (RenderProgressText != null) RenderProgressText.Text = $"{percent:F0}%";
                                SetStatus($"Color Grading — {percent:F0}%{fpsInfo}{speedInfo}", "#388BFD");
                            });
                        }
                        else if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains("fatal", StringComparison.OrdinalIgnoreCase))
                        {
                            Dispatcher.InvokeAsync(() => Log($"[FFMPEG] {line}"));
                        }
                    }

                    proc.WaitForExit();
                    return proc.ExitCode == 0;
                }
                finally
                {
                    ProcessGuard.Unwatch(proc);
                }
            }
            catch (Exception ex)
            {
                Log($"[CRITICAL] Process error: {ex.Message}");
                return false;
            }
            finally
            {
                _ffmpegProcess?.Dispose();
                _ffmpegProcess = null;
            }
        });
    }

    private void SetRenderingUI(bool rendering)
    {
        ApplyGradeBtn.IsEnabled = !rendering;
        CancelBtn.Visibility = rendering ? Visibility.Visible : Visibility.Collapsed;
        RenderProgressBar.Visibility = rendering ? Visibility.Visible : Visibility.Collapsed;
        RenderProgressText.Visibility = rendering ? Visibility.Visible : Visibility.Collapsed;
        if (rendering)
        {
            if (RenderProgressBar != null) RenderProgressBar.Value = 0;
            if (RenderProgressText != null) RenderProgressText.Text = "0%";
        }
        if (OpenFolderBtn != null)
            OpenFolderBtn.IsEnabled = !rendering && (!string.IsNullOrEmpty(_lastOutputFolder) || !string.IsNullOrEmpty(_filePath) || !string.IsNullOrEmpty(_customOutputFolder));
    }

    private void SetStatus(string text, string colorHex)
    {
        if (StatusText != null) StatusText.Text = text;
        if (StatusDot != null)
        {
            var brush = new BrushConverter().ConvertFromString(colorHex) as SolidColorBrush;
            StatusDot.Fill = brush ?? Brushes.Gray;
        }
    }

    private void Log(string line)
    {
        if (LogBox == null) return;
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        int i = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(dir, $"{name}_{i++}{ext}");
        }
        return path;
    }
}

