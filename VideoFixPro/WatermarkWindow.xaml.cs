using System;
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
using System.Windows.Shell;
using WinForms = System.Windows.Forms;

namespace VideoFixPro;

public partial class WatermarkWindow : Window
{
    private bool _isInitialized;
    private string _filePath = string.Empty;
    private string _logoPath = string.Empty;
    private double _durationSeconds;
    private int _sourceWidth;
    private int _sourceHeight;

    // Watermark state
    private string _currentPosition = "BR"; // TL, TC, TR, CL, CC, CR, BL, BC, BR
    private bool _isFreeMode = false;
    private double _freePosX = 85.0;         // 0% - 100%
    private double _freePosY = 85.0;         // 0% - 100%
    private double _rotationAngle = 0.0;     // -180° to +180°
    private double _scalePercent = 15.0;     // 3% - 80%
    private double _opacityPercent = 85.0;   // 10% - 100%
    private int _marginPx = 24;              // 0 - 100

    // Dragging state
    private bool _isDraggingWatermark;
    private Point _dragStartMouse;
    private double _dragStartX;
    private double _dragStartY;

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

    public WatermarkWindow(string? preloadPath = null, bool hasNvidia = false, bool hasAmd = false, bool hasIntel = false)
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
            {
                string ext = Path.GetExtension(files[0]).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")
                    LoadLogo(files[0]);
                else
                    _ = LoadFileAsync(files[0]);
            }
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
            Title = "Select Video to Watermark",
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
            Player.Play();
            Player.Pause();
            _isPlayerPlaying = false;
            if (SeekPlayBtn != null) SeekPlayBtn.Content = "▶";
            if (PlayPauseBtn != null) PlayPauseBtn.Content = "▶";
        }
        catch { }

        await ProbeVideoAsync(path);
        UpdateLiveOverlay();
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

    // ── Logo Management ───────────────────────────────────────────────────────
    private void SelectLogo_Click(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var dlg = new WinForms.OpenFileDialog
        {
            Title = "Select Logo Watermark Image",
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All Files|*.*"
        };
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            LoadLogo(dlg.FileName);
    }

    private void LoadLogo(string path)
    {
        if (!File.Exists(path)) return;
        _logoPath = path;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();

            if (LogoThumbnail != null) LogoThumbnail.Source = bmp;
            if (LiveWatermarkImage != null) LiveWatermarkImage.Source = bmp;
            if (LogoFileNameText != null) LogoFileNameText.Text = Path.GetFileName(path);
            if (ClearLogoBtn != null) ClearLogoBtn.Visibility = Visibility.Visible;

            UpdateLiveOverlay();
            SetStatus($"Logo loaded: {Path.GetFileName(path)}", "#3FB950");
        }
        catch { }
    }

    private void ClearLogo_Click(object s, RoutedEventArgs e)
    {
        _logoPath = string.Empty;
        if (LogoThumbnail != null) LogoThumbnail.Source = null;
        if (LiveWatermarkImage != null)
        {
            LiveWatermarkImage.Source = null;
            LiveWatermarkImage.Visibility = Visibility.Collapsed;
        }
        if (LogoFileNameText != null) LogoFileNameText.Text = "Click to select logo (PNG, JPG)";
        if (ClearLogoBtn != null) ClearLogoBtn.Visibility = Visibility.Collapsed;
    }

    // ── Mode Switcher (9 Anchors vs Free Drag) ──────────────────────────────
    private void ModeAnchor_Click(object s, RoutedEventArgs e)
    {
        _isFreeMode = false;
        if (ModeAnchorBtn != null) ModeAnchorBtn.Style = (Style)FindResource("ActiveToolButton");
        if (ModeFreeBtn != null) ModeFreeBtn.Style = (Style)FindResource("GhostButton");
        if (AnchorPanel != null) AnchorPanel.Visibility = Visibility.Visible;
        if (FreeTransformPanel != null) FreeTransformPanel.Visibility = Visibility.Collapsed;
        if (MarginRow != null) MarginRow.Visibility = Visibility.Visible;
        UpdateLiveOverlay();
    }

    private void ModeFree_Click(object s, RoutedEventArgs e)
    {
        _isFreeMode = true;
        if (ModeAnchorBtn != null) ModeAnchorBtn.Style = (Style)FindResource("GhostButton");
        if (ModeFreeBtn != null) ModeFreeBtn.Style = (Style)FindResource("ActiveToolButton");
        if (AnchorPanel != null) AnchorPanel.Visibility = Visibility.Collapsed;
        if (FreeTransformPanel != null) FreeTransformPanel.Visibility = Visibility.Visible;
        if (MarginRow != null) MarginRow.Visibility = Visibility.Collapsed;
        UpdateLiveOverlay();
    }

    // ── Position & Scaling Controls ───────────────────────────────────────────
    private void PosTL_Click(object s, RoutedEventArgs e) => SetPosition("TL");
    private void PosTC_Click(object s, RoutedEventArgs e) => SetPosition("TC");
    private void PosTR_Click(object s, RoutedEventArgs e) => SetPosition("TR");
    private void PosCL_Click(object s, RoutedEventArgs e) => SetPosition("CL");
    private void PosCC_Click(object s, RoutedEventArgs e) => SetPosition("CC");
    private void PosCR_Click(object s, RoutedEventArgs e) => SetPosition("CR");
    private void PosBL_Click(object s, RoutedEventArgs e) => SetPosition("BL");
    private void PosBC_Click(object s, RoutedEventArgs e) => SetPosition("BC");
    private void PosBR_Click(object s, RoutedEventArgs e) => SetPosition("BR");

    private void SetPosition(string pos)
    {
        _currentPosition = pos;
        _isFreeMode = false;
        if (ModeAnchorBtn != null) ModeAnchorBtn.Style = (Style)FindResource("ActiveToolButton");
        if (ModeFreeBtn != null) ModeFreeBtn.Style = (Style)FindResource("GhostButton");
        if (AnchorPanel != null) AnchorPanel.Visibility = Visibility.Visible;
        if (FreeTransformPanel != null) FreeTransformPanel.Visibility = Visibility.Collapsed;
        if (MarginRow != null) MarginRow.Visibility = Visibility.Visible;

        UpdatePositionButtons();
        UpdateLiveOverlay();
    }

    private void UpdatePositionButtons()
    {
        var active = (Style)FindResource("ActiveToolButton");
        var ghost = (Style)FindResource("GhostButton");

        if (PosTL != null) PosTL.Style = _currentPosition == "TL" ? active : ghost;
        if (PosTC != null) PosTC.Style = _currentPosition == "TC" ? active : ghost;
        if (PosTR != null) PosTR.Style = _currentPosition == "TR" ? active : ghost;
        if (PosCL != null) PosCL.Style = _currentPosition == "CL" ? active : ghost;
        if (PosCC != null) PosCC.Style = _currentPosition == "CC" ? active : ghost;
        if (PosCR != null) PosCR.Style = _currentPosition == "CR" ? active : ghost;
        if (PosBL != null) PosBL.Style = _currentPosition == "BL" ? active : ghost;
        if (PosBC != null) PosBC.Style = _currentPosition == "BC" ? active : ghost;
        if (PosBR != null) PosBR.Style = _currentPosition == "BR" ? active : ghost;
    }

    private void PosXSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized || _isDraggingWatermark) return;
        _freePosX = Math.Round(PosXSlider.Value, 1);
        if (PosXText != null) PosXText.Text = $"{_freePosX:F0}%";
        if (!_isFreeMode) ModeFree_Click(s, e);
        UpdateLiveOverlay();
    }

    private void PosYSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized || _isDraggingWatermark) return;
        _freePosY = Math.Round(PosYSlider.Value, 1);
        if (PosYText != null) PosYText.Text = $"{_freePosY:F0}%";
        if (!_isFreeMode) ModeFree_Click(s, e);
        UpdateLiveOverlay();
    }

    // ── Rotation & Angle Controls ─────────────────────────────────────────────
    private void RotateSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        _rotationAngle = Math.Round(RotateSlider.Value, 0);
        if (RotateText != null) RotateText.Text = $"{_rotationAngle:F0}°";
        if (WatermarkRotateTransform != null) WatermarkRotateTransform.Angle = _rotationAngle;
    }

    private void Rotate0_Click(object s, RoutedEventArgs e) => SetRotation(0);
    private void Rotate45_Click(object s, RoutedEventArgs e) => SetRotation(45);
    private void Rotate90_Click(object s, RoutedEventArgs e) => SetRotation(90);
    private void RotateMinus45_Click(object s, RoutedEventArgs e) => SetRotation(-45);
    private void RotateMinus90_Click(object s, RoutedEventArgs e) => SetRotation(-90);

    private void SetRotation(double deg)
    {
        _rotationAngle = deg;
        if (RotateSlider != null) RotateSlider.Value = deg;
        if (RotateText != null) RotateText.Text = $"{deg:F0}°";
        if (WatermarkRotateTransform != null) WatermarkRotateTransform.Angle = deg;
    }

    // ── Interactive Drag & Drop on Video Player ────────────────────────────────
    private void Watermark_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && VideoFrameOverlay != null && WatermarkDraggableHost != null)
        {
            _isDraggingWatermark = true;
            _dragStartMouse = e.GetPosition(VideoFrameOverlay);
            _dragStartX = _freePosX;
            _dragStartY = _freePosY;
            WatermarkDraggableHost.CaptureMouse();
            if (!_isFreeMode) ModeFree_Click(s, e);
            e.Handled = true;
        }
    }

    private void Watermark_MouseMove(object s, MouseEventArgs e)
    {
        if (_isDraggingWatermark && VideoFrameOverlay != null && VideoFrameOverlay.ActualWidth > 0 && VideoFrameOverlay.ActualHeight > 0)
        {
            Point cur = e.GetPosition(VideoFrameOverlay);
            double deltaX = cur.X - _dragStartMouse.X;
            double deltaY = cur.Y - _dragStartMouse.Y;

            double pctX = (deltaX / VideoFrameOverlay.ActualWidth) * 100.0;
            double pctY = (deltaY / VideoFrameOverlay.ActualHeight) * 100.0;

            _freePosX = Math.Clamp(_dragStartX + pctX, 0, 100);
            _freePosY = Math.Clamp(_dragStartY + pctY, 0, 100);

            if (PosXSlider != null) PosXSlider.Value = _freePosX;
            if (PosYSlider != null) PosYSlider.Value = _freePosY;
            if (PosXText != null) PosXText.Text = $"{_freePosX:F0}%";
            if (PosYText != null) PosYText.Text = $"{_freePosY:F0}%";

            UpdateLiveOverlay();
            e.Handled = true;
        }
    }

    private void Watermark_MouseUp(object s, MouseButtonEventArgs e)
    {
        if (_isDraggingWatermark)
        {
            _isDraggingWatermark = false;
            WatermarkDraggableHost?.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void Canvas_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && VideoFrameOverlay != null && VideoFrameOverlay.ActualWidth > 0 && VideoFrameOverlay.ActualHeight > 0)
        {
            Point pt = e.GetPosition(VideoFrameOverlay);
            if (!_isFreeMode) ModeFree_Click(s, e);

            _freePosX = Math.Clamp((pt.X / VideoFrameOverlay.ActualWidth) * 100.0, 0, 100);
            _freePosY = Math.Clamp((pt.Y / VideoFrameOverlay.ActualHeight) * 100.0, 0, 100);

            if (PosXSlider != null) PosXSlider.Value = _freePosX;
            if (PosYSlider != null) PosYSlider.Value = _freePosY;
            if (PosXText != null) PosXText.Text = $"{_freePosX:F0}%";
            if (PosYText != null) PosYText.Text = $"{_freePosY:F0}%";

            UpdateLiveOverlay();
        }
    }
    private void Canvas_MouseMove(object s, MouseEventArgs e) { }
    private void Canvas_MouseUp(object s, MouseButtonEventArgs e) { }

    private void ScaleSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        _scalePercent = Math.Round(ScaleSlider.Value, 0);
        if (ScaleText != null) ScaleText.Text = $"{_scalePercent:F0}%";
        UpdateLiveOverlay();
    }

    private void OpacitySlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        _opacityPercent = Math.Round(OpacitySlider.Value, 0);
        if (OpacityText != null) OpacityText.Text = $"{_opacityPercent:F0}%";
        UpdateLiveOverlay();
    }

    private void MarginSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        _marginPx = (int)Math.Round(MarginSlider.Value, 0);
        if (MarginText != null) MarginText.Text = $"{_marginPx}px";
        UpdateLiveOverlay();
    }

    private void PlayerBorder_SizeChanged(object s, SizeChangedEventArgs e) => UpdateLiveOverlay();

    private void UpdateLiveOverlay()
    {
        if (LiveWatermarkImage == null || VideoFrameOverlay == null || WatermarkDraggableHost == null) return;
        if (string.IsNullOrEmpty(_logoPath) || !File.Exists(_logoPath))
        {
            LiveWatermarkImage.Visibility = Visibility.Collapsed;
            WatermarkDraggableHost.Visibility = Visibility.Collapsed;
            return;
        }

        // Get source video dimensions
        int vidW = _sourceWidth > 0 ? _sourceWidth : (Player?.NaturalVideoWidth > 0 ? Player.NaturalVideoWidth : 1920);
        int vidH = _sourceHeight > 0 ? _sourceHeight : (Player?.NaturalVideoHeight > 0 ? Player.NaturalVideoHeight : 1080);

        double containerW = PlayerBorder?.ActualWidth ?? 600;
        double containerH = PlayerBorder?.ActualHeight ?? 400;

        if (containerW < 50 || containerH < 50) return;

        // Calculate exact rendered video bounds inside Uniform Stretch
        double scale = Math.Min(containerW / vidW, containerH / vidH);
        double renderedW = vidW * scale;
        double renderedH = vidH * scale;

        VideoFrameOverlay.Width = renderedW;
        VideoFrameOverlay.Height = renderedH;

        LiveWatermarkImage.Visibility = Visibility.Visible;
        WatermarkDraggableHost.Visibility = Visibility.Visible;
        LiveWatermarkImage.Opacity = _opacityPercent / 100.0;

        // Scaled watermark dimensions matching FFmpeg output
        double logoW = Math.Max(12, renderedW * (_scalePercent / 100.0));
        LiveWatermarkImage.Width = logoW;
        WatermarkDraggableHost.Width = logoW;

        double logoH = logoW;
        if (LiveWatermarkImage.Source is BitmapSource bs && bs.PixelWidth > 0)
        {
            logoH = logoW * ((double)bs.PixelHeight / bs.PixelWidth);
        }
        LiveWatermarkImage.Height = logoH;
        WatermarkDraggableHost.Height = logoH;

        if (WatermarkRotateTransform != null)
            WatermarkRotateTransform.Angle = _rotationAngle;

        double left = 0;
        double top = 0;

        if (!_isFreeMode)
        {
            double m = Math.Max(0, _marginPx * scale);
            switch (_currentPosition)
            {
                case "TL": left = m; top = m; break;
                case "TC": left = (renderedW - logoW) / 2.0; top = m; break;
                case "TR": left = Math.Max(0, renderedW - logoW - m); top = m; break;
                case "CL": left = m; top = (renderedH - logoH) / 2.0; break;
                case "CC": left = (renderedW - logoW) / 2.0; top = (renderedH - logoH) / 2.0; break;
                case "CR": left = Math.Max(0, renderedW - logoW - m); top = (renderedH - logoH) / 2.0; break;
                case "BL": left = m; top = Math.Max(0, renderedH - logoH - m); break;
                case "BC": left = (renderedW - logoW) / 2.0; top = Math.Max(0, renderedH - logoH - m); break;
                case "BR":
                default:   left = Math.Max(0, renderedW - logoW - m); top = Math.Max(0, renderedH - logoH - m); break;
            }
        }
        else
        {
            left = Math.Clamp((renderedW - logoW) * (_freePosX / 100.0), 0, Math.Max(0, renderedW - logoW));
            top = Math.Clamp((renderedH - logoH) * (_freePosY / 100.0), 0, Math.Max(0, renderedH - logoH));
        }

        Canvas.SetLeft(WatermarkDraggableHost, left);
        Canvas.SetTop(WatermarkDraggableHost, top);
    }

    // ── Player Controls ───────────────────────────────────────────────────────
    private void Player_MediaOpened(object s, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan && _durationSeconds <= 0)
            _durationSeconds = Player.NaturalDuration.TimeSpan.TotalSeconds;

        if (Player.NaturalVideoWidth > 0 && _sourceWidth <= 0)
        {
            _sourceWidth = Player.NaturalVideoWidth;
            _sourceHeight = Player.NaturalVideoHeight;
        }

        UpdateLiveOverlay();
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

    // ── Output Management ─────────────────────────────────────────────────────
    private void BrowseOutput_Click(object s, RoutedEventArgs e)
    {
        var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Select Output Folder for Watermarked Video",
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

    // ── Watermark Application Execution ───────────────────────────────────────
    private async void ApplyWatermark_Click(object s, RoutedEventArgs e)
    {
        if (_isRendering) return;
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
        {
            MessageBox.Show("Please load a video file first.", "No Video", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrEmpty(_logoPath) || !File.Exists(_logoPath))
        {
            MessageBox.Show("Please select a logo watermark image first.", "No Logo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ExecuteWatermarkAsync();
    }

    private void Cancel_Click(object s, RoutedEventArgs e)
    {
        _cts?.Cancel();
        try { _ffmpegProcess?.Kill(); } catch { }
    }

    private async Task ExecuteWatermarkAsync()
    {
        _isRendering = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        string dir = !string.IsNullOrEmpty(_customOutputFolder) ? _customOutputFolder : Path.GetDirectoryName(_filePath) ?? ".";
        string safeName = Path.GetFileNameWithoutExtension(_filePath);
        string ext = Path.GetExtension(_filePath);
        string outputPath = Path.Combine(dir, $"{safeName}_watermarked{ext}");
        outputPath = GetUniqueFilePath(outputPath);
        _lastOutputFolder = dir;

        SetRenderingUI(true);
        SetStatus("Applying watermark overlay...", "#388BFD");
        Log($"\n[WATERMARK] Source: {Path.GetFileName(_filePath)}");
        Log($"[WATERMARK] Logo: {Path.GetFileName(_logoPath)}");
        Log($"[WATERMARK] Anchor: {_currentPosition}, Scale: {_scalePercent}%, Opacity: {_opacityPercent}%");
        Log($"[WATERMARK] Output: {outputPath}");

        if (_sourceWidth <= 0 || _durationSeconds <= 0)
        {
            await ProbeVideoAsync(_filePath);
            if (_durationSeconds <= 0) _durationSeconds = 1.0;
        }

        double scaleFactor = _scalePercent / 100.0;
        double alphaFactor = _opacityPercent / 100.0;
        string alphaStr = alphaFactor.ToString("F2", CultureInfo.InvariantCulture);

        // Ensure target logo width is even and proportional to source video width
        int targetLogoWidth = _sourceWidth > 0 ? (int)(_sourceWidth * scaleFactor) : 240;
        targetLogoWidth = Math.Max(16, targetLogoWidth - (targetLogoWidth % 2));

        // Calculate overlay position expression for FFmpeg
        string overlayPos;
        if (!_isFreeMode)
        {
            string m = _marginPx.ToString();
            overlayPos = _currentPosition switch
            {
                "TL" => $"{m}:{m}",
                "TC" => $"(main_w-overlay_w)/2:{m}",
                "TR" => $"main_w-overlay_w-{m}:{m}",
                "CL" => $"{m}:(main_h-overlay_h)/2",
                "CC" => "(main_w-overlay_w)/2:(main_h-overlay_h)/2",
                "CR" => $"main_w-overlay_w-{m}:(main_h-overlay_h)/2",
                "BL" => $"{m}:main_h-overlay_h-{m}",
                "BC" => $"(main_w-overlay_w)/2:main_h-overlay_h-{m}",
                "BR" => $"main_w-overlay_w-{m}:main_h-overlay_h-{m}",
                _    => $"main_w-overlay_w-{m}:main_h-overlay_h-{m}"
            };
        }
        else
        {
            double posX = _freePosX / 100.0;
            double posY = _freePosY / 100.0;
            string posXStr = posX.ToString("F4", CultureInfo.InvariantCulture);
            string posYStr = posY.ToString("F4", CultureInfo.InvariantCulture);
            overlayPos = $"(main_w-overlay_w)*{posXStr}:(main_h-overlay_h)*{posYStr}";
        }

        // Apply rotation if requested (c=none preserves transparent background)
        string rotFilter = "";
        if (Math.Abs(_rotationAngle) > 0.01)
        {
            double rad = _rotationAngle * (Math.PI / 180.0);
            string radStr = rad.ToString("F4", CultureInfo.InvariantCulture);
            rotFilter = $",rotate={radStr}:ow=rotw({radStr}):oh=roth({radStr}):c=none";
        }

        string filterComplex = $"[1:v]format=rgba,colorchannelmixer=aa={alphaStr},scale={targetLogoWidth}:-2{rotFilter}[wm];[0:v][wm]overlay={overlayPos}:format=auto[outv]";

        bool useGpu = (GpuCheck.IsChecked == true) && (_hasNvidia || _hasAmd || _hasIntel);
        string vCodecArgs =                             useGpu && _hasNvidia ? "-c:v h264_nvenc -pix_fmt yuv420p" :
useGpu && _hasAmd ? "-c:v h264_amf -pix_fmt yuv420p" :
                            useGpu && _hasIntel ? "-c:v h264_qsv -pix_fmt nv12" :
                            "-c:v libx264 -preset fast -crf 19 -pix_fmt yuv420p";

        string args = $"-y -i \"{_filePath}\" -i \"{_logoPath}\" -filter_complex \"{filterComplex}\" -map \"[outv]\" -map 0:a? {vCodecArgs} -c:a aac -b:a 192k \"{outputPath}\"";
        Log($"[CMD] ffmpeg {args}");

        bool success = await RunFFmpegAsync(args, _durationSeconds, _cts.Token);

        // Auto CPU Fallback if GPU fails
        if (!success && !_cts.Token.IsCancellationRequested && useGpu)
        {
            Log("[WARN] GPU encoding failed. Retrying on CPU (libx264)...");
            SetStatus("Retrying on CPU...", "#D29922");
            args = $"-y -i \"{_filePath}\" -i \"{_logoPath}\" -filter_complex \"{filterComplex}\" -map \"[outv]\" -map 0:a? -c:v libx264 -preset fast -crf 19 -pix_fmt yuv420p -c:a aac -b:a 192k \"{outputPath}\"";
            Log($"[CMD Fallback] ffmpeg {args}");
            success = await RunFFmpegAsync(args, _durationSeconds, _cts.Token);
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
            Log($"[SUCCESS] Watermark applied ({outputPath})");
            ShowNotification("Watermark Complete", $"Saved: {Path.GetFileName(outputPath)}");
        }
        else
        {
            SetStatus("Watermark failed", "#F85149");
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
            var sys32 = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcuda.dll");
            if (System.IO.File.Exists(sys32)) { _nvCudaDir = System.IO.Path.GetDirectoryName(sys32); return _nvCudaDir; }

            var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrEmpty(cudaPath))
            {
                var cudaBin = System.IO.Path.Combine(cudaPath, "bin", "nvcuda.dll");
                if (System.IO.File.Exists(cudaBin)) { _nvCudaDir = System.IO.Path.GetDirectoryName(cudaBin); return _nvCudaDir; }
            }

            var driverStore = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                           "System32", "DriverStore", "FileRepository");
            if (System.IO.Directory.Exists(driverStore))
            {
                foreach (var pattern in new[] { "nv_disp*", "nvdsp*", "nvlt*", "nvmi*" })
                    foreach (var dir in System.IO.Directory.GetDirectories(driverStore, pattern, System.IO.SearchOption.TopDirectoryOnly))
                        foreach (var name in new[] { "nvcuda64.dll", "nvcuda.dll" })
                            if (System.IO.File.Exists(System.IO.Path.Combine(dir, name))) { _nvCudaDir = dir; return _nvCudaDir; }
            }

            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                       Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                var nvDir = System.IO.Path.Combine(pf, "NVIDIA Corporation");
                if (System.IO.Directory.Exists(nvDir))
                    try { foreach (var f in System.IO.Directory.GetFiles(nvDir, "nvcuda*.dll", System.IO.SearchOption.AllDirectories))
                        { _nvCudaDir = System.IO.Path.GetDirectoryName(f); return _nvCudaDir; } } catch { }
            }
        }
        catch { }
        return null;
    }

    private static void InjectNvCudaPath(System.Diagnostics.ProcessStartInfo psi)
    {
        var nvDir = FindNvCudaDir();
        if (nvDir != null)
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = nvDir + ";" + currentPath;
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
        if (ApplyBtn != null) ApplyBtn.IsEnabled = !rendering;
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
