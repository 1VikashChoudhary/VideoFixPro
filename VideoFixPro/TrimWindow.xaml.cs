using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VideoFixPro.Models;
using WinForms = System.Windows.Forms;

namespace VideoFixPro;

// â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
//  TrimWindow  â€”  Video Trim Tool
//  Handles: timeline canvas, filmstrip thumbnails, multi-segment editing,
//           stream-copy and re-encode trim, multi-segment concat.
// â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
public partial class TrimWindow : Window
{
    private readonly record struct TrimOperationResult(int Done, int Failed, bool Cancelled, bool OpenFolderEnabled);

    // â”€â”€ State â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private string _filePath        = string.Empty;
    private double _durationSeconds = 0;

    // trim points (seconds)
    private double _inPoint  = 0;
    private double _outPoint = 0;

    // playhead (seconds) â€“ where the cursor sits on the timeline
    private double _playhead = 0;

    // timeline drag state
    private enum DragTarget { None, Playhead, InHandle, OutHandle }
    private DragTarget _dragging = DragTarget.None;

    private readonly System.Windows.Threading.DispatcherTimer _playheadTimer = new();
    private bool _isSeeking = false;
    private bool _isPlayerPlaying = false;

    // segments list (multi-trim)
    private readonly ObservableCollection<TrimSegment> _segments = new();

    // cancel
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _thumbCts;
    private Process? _ffmpegProcess;
    private bool _isTrimming;

    // output
    private string _customOutputFolder = string.Empty;
    private string _lastOutputFolder   = string.Empty;
    private string _videoCodec         = "-";

    // GPU support (passed from main)
    private bool _hasNvidia;
    private bool _hasAmd;
    private int  _qualityPercent = 70;

    // filmstrip thumbnails (one per bucket)
    private const int FilmstripBuckets = 12;
    private readonly BitmapImage?[] _filmImages = new BitmapImage?[FilmstripBuckets];

    // colours
    private static readonly Color ColIn        = Color.FromRgb(0x3F, 0xB9, 0x50); // green
    private static readonly Color ColOut       = Color.FromRgb(0xF8, 0x51, 0x49); // red
    private static readonly Color ColPlayhead  = Color.FromRgb(0x38, 0x8B, 0xFD); // blue
    private static readonly Color ColSelected  = Color.FromArgb(0x33, 0x38, 0x8B, 0xFD);
    private static readonly Color ColWaveform  = Color.FromArgb(0x55, 0x38, 0x8B, 0xFD);

    // timeline geometry constants
    private const double HandleHalfW = 5;
    private const double TimelineH   = 84;

    // â”€â”€ FFmpeg paths (mirrors MainWindow) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static string AppDir   => AppDomain.CurrentDomain.BaseDirectory;
    private static string FFmpeg   => GetBinPath("ffmpeg.exe");
    private static string FFprobe  => GetBinPath("ffprobe.exe");
    private static bool   IsFolderWritable(string p)
    {
        try { var t = System.IO.Path.Combine(p, "__write_test__"); File.WriteAllText(t, ""); File.Delete(t); return true; }
        catch { return false; }
    }
    private static string GetBinPath(string name)
    {
        var appBin = System.IO.Path.Combine(AppDir, "ffmpeg", name);
        if (File.Exists(appBin)) return appBin;
        var localBin = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoFixPro", "ffmpeg", name);
        return File.Exists(localBin) ? localBin : appBin;
    }
    private static readonly HashSet<string> VideoExts =
        new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".m2ts" };

    // â”€â”€ Constructor â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public TrimWindow(string? preloadPath = null, bool hasNvidia = false, bool hasAmd = false, int quality = 70)
    {
        InitializeComponent();
        Loaded += (_, _) => UiTextSanitizer.Apply(this);
        _hasNvidia = hasNvidia;
        _hasAmd    = hasAmd;
        _qualityPercent = quality;

        SegmentList.ItemsSource = _segments;
        _segments.CollectionChanged += (_, _) => UpdateSegmentCount();

        TrimMode_Changed(null, null!);   // init mode hint

        if (!string.IsNullOrEmpty(preloadPath) && File.Exists(preloadPath))
            _ = LoadFileAsync(preloadPath);

        _playheadTimer.Interval = TimeSpan.FromMilliseconds(40);
        _playheadTimer.Tick += (s, e) => { if (!_isSeeking) UpdatePlayheadFromPlayer(); };
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  TITLE BAR
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
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
        CancelTrimOp();
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _cts?.Dispose();
        _playheadTimer?.Stop();
        
        try { TrimPlayer.Source = null; } catch { }

        if (Owner is MainWindow main) main.CleanupTempThumbs();

        base.OnClosing(e);
    }
    private void MaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        TrimMaxBtn.Content = WindowState == WindowState.Maximized ? "â" : "â–¡";
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  DROP ZONE
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void TrimDrop_DragEnter(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        TrimDropZone.BorderBrush = (Brush)FindResource("AccentBrush");
        e.Handled = true;
    }
    private void TrimDrop_DragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void TrimDrop_DragLeave(object s, DragEventArgs e) => ResetDropZone();
    private void TrimDrop_Drop(object s, DragEventArgs e)
    {
        ResetDropZone();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var video = files.FirstOrDefault(f => VideoExts.Contains(System.IO.Path.GetExtension(f)));
        if (video != null) _ = LoadFileAsync(video);
    }
    private void TrimDrop_Click(object s, MouseButtonEventArgs e) => BrowseForFile();
    private void ChangeFile_Click(object s, RoutedEventArgs e) => BrowseForFile();
    private void ResetDropZone() => TrimDropZone.BorderBrush = (Brush)FindResource("MutedBrush");
    private void BrowseForFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Video File to Trim",
            Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.ts;*.m2ts|All Files|*.*"
        };
        if (dlg.ShowDialog() == true) _ = LoadFileAsync(dlg.FileName);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  FILE LOADING
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private async Task LoadFileAsync(string path)
    {
        _filePath = path;
        SetTrimStatus("Probing fileâ€¦", "#388BFD");
        TrimBtn.IsEnabled = false;
        AddToQueueBtn.IsEnabled = false;

        // Cancel previous thumbnail generation if any
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = new CancellationTokenSource();
        _playheadTimer.Start();

        // Clear previous filmstrip
        for (int i = 0; i < FilmstripBuckets; i++) _filmImages[i] = null;
        Dispatcher.Invoke(DrawFilmstrip);

        // Switch UI zones
        TrimDropZone.Visibility  = Visibility.Collapsed;
        TrimFileHeader.Visibility = Visibility.Visible;
        HeaderFileName.Text = System.IO.Path.GetFileName(path);
        TrimFileName.Text   = System.IO.Path.GetFileName(path);

        // Probe
        var info = await ProbeFileAsync(path);
        _durationSeconds = info.Duration;

        // Update header badges
        HeaderDuration.Text   = TrimSegment.FormatTime(info.Duration);
        HeaderCodec.Text      = info.VideoCodec;
        HeaderResolution.Text = info.Resolution;
        _videoCodec = info.VideoCodec;

        // Init trim points to full range
        _inPoint  = 0;
        _outPoint = _durationSeconds;
        _playhead = 0;

        // Show timeline
        FilmstripBorder.Visibility    = Visibility.Visible;
        TimelineBorder.Visibility     = Visibility.Visible;
        TimeLabelsGrid.Visibility     = Visibility.Visible;
        QuickButtonsGrid.Visibility   = Visibility.Visible;
        SegmentsBorder.Visibility     = Visibility.Visible;
        PlayerBorder.Visibility       = Visibility.Visible;
        PlayPauseBtn.Visibility       = Visibility.Visible;

        // Load into player
        TrimPlayer.Source = new Uri(path);
        TrimPlayer.Play(); // Start playing to get frame then pause
        _isPlayerPlaying = true;
        UpdatePlayPauseUI();

        UpdateTimeBoxes();
        UpdateSelectionDisplay();
        DrawTimeline();

        TrimBtn.IsEnabled = true;
        AddToQueueBtn.IsEnabled = true;
        SetTrimStatus($"Loaded â€” {TrimSegment.FormatTime(_durationSeconds)}  â€¢  Press I/O to set trim points", "#3FB950");

        // Generate filmstrip in background
        _ = GenerateFilmstripAsync(path, info.Duration, _thumbCts.Token);
    }

    // â”€â”€ Probe result record â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private record ProbeInfo(double Duration, string VideoCodec, string AudioCodec, string Resolution, string Fps);

    private async Task<ProbeInfo> ProbeFileAsync(string path)
    {
        double duration = 0;
        string vc = "-", ac = "-", res = "-", fps = "-";
        if (!File.Exists(FFprobe)) return new(0, vc, ac, res, fps);
        try
        {
            var json = await RunProcessAsync(FFprobe,
                $"-v quiet -print_format json -show_streams -show_format \"{path}\"");
            var root = JsonNode.Parse(json);
            var streams = root?["streams"]?.AsArray();
            var format = root?["format"];
            if (double.TryParse(format?["duration"]?.GetValue<string>(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                duration = d;
            if (streams != null)
            {
                foreach (var stream in streams)
                {
                    var type = stream?["codec_type"]?.GetValue<string>();
                    if (type == "video" && vc == "-")
                    {
                        vc = (stream?["codec_name"]?.GetValue<string>() ?? "-").ToUpperInvariant();
                        var width = stream?["width"]?.GetValue<int>() ?? 0;
                        var height = stream?["height"]?.GetValue<int>() ?? 0;
                        if (width > 0 && height > 0) res = $"{width}x{height}";
                        var frameRate = stream?["r_frame_rate"]?.GetValue<string>() ?? string.Empty;
                        if (frameRate.Contains('/'))
                        {
                            var parts = frameRate.Split('/');
                            if (double.TryParse(parts[0], out double numerator) &&
                                double.TryParse(parts[1], out double denominator) && denominator != 0)
                            {
                                fps = $"{numerator / denominator:F2}";
                            }
                        }
                    }
                    else if (type == "audio" && ac == "-")
                    {
                        ac = (stream?["codec_name"]?.GetValue<string>() ?? "-").ToUpperInvariant();
                    }
                }
            }
        }
        catch
        {
            // Probing is best-effort; the rest of the trim tool can still work.
        }
        return new(duration, vc, ac, res, fps);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  FILMSTRIP GENERATION  (ffmpeg thumbnail frames)
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private async Task GenerateFilmstripAsync(string path, double duration, CancellationToken ct)
    {
        if (!File.Exists(FFmpeg) || duration <= 0) return;

        // writable temp dir
        var thumbBase = IsFolderWritable(AppDir)
            ? AppDir
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoFixPro");
        var dir = System.IO.Path.Combine(thumbBase, "trim_thumbs");
        try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { return; }

        for (int i = 0; i < FilmstripBuckets; i++)
        {
            if (ct.IsCancellationRequested) break;

            double t   = duration * i / FilmstripBuckets;
            var outImg  = System.IO.Path.Combine(dir, $"fs_{GetHashCode()}_{i}.jpg");
            var args    = $"-y -ss {t.ToString(CultureInfo.InvariantCulture)} -i \"{path}\" -frames:v 1 -vf scale=120:-1 -q:v 4 \"{outImg}\"";

            try
            {
                await RunProcessAsync(FFmpeg, args, ct);
                if (ct.IsCancellationRequested) break;

                if (File.Exists(outImg))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource     = new Uri(outImg);
                    bmp.CacheOption   = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.EndInit();
                    bmp.Freeze();
                    _filmImages[i] = bmp;
                    
                    // Delay deletion slightly or use OnLoad to ensure it's safe
                    try { File.Delete(outImg); } catch { }
                    
                    Dispatcher.Invoke(DrawFilmstrip);
                }
            }
            catch { /* silently skip frame */ }
        }
    }

    private void DrawFilmstrip()
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
                Source  = img,
                Width   = cellW,
                Height  = h,
                Stretch = Stretch.UniformToFill,
                ClipToBounds = true
            };
            Canvas.SetLeft(ib, i * cellW);
            Canvas.SetTop(ib, 0);
            FilmstripCanvas.Children.Add(ib);
        }

        // Update playhead line on filmstrip
        double px = _durationSeconds > 0
            ? (_playhead / _durationSeconds) * w : 0;
        Canvas.SetLeft(FilmstripPlayhead, px - 1);
        FilmstripPlayhead.Height = h;
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  TIMELINE CANVAS
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void Timeline_Loaded(object s, RoutedEventArgs e)   => DrawTimeline();
    private void Timeline_SizeChanged(object s, SizeChangedEventArgs e) => DrawTimeline();

    private void DrawTimeline()
    {
        TimelineCanvas.Children.Clear();
        double w = TimelineCanvas.ActualWidth;
        double h = TimelineH;
        if (w <= 0 || _durationSeconds <= 0) return;

        double ToX(double sec) => sec / _durationSeconds * w;
        double inX  = ToX(_inPoint);
        double outX = ToX(_outPoint);
        double phX  = ToX(_playhead);

        // â”€â”€ Background track â”€â”€
        var bg = MakeRect(0, h * 0.35, w, h * 0.3,
            Color.FromRgb(0x21, 0x26, 0x2D), 0);
        TimelineCanvas.Children.Add(bg);

        // â”€â”€ Selected region â”€â”€
        var sel = MakeRect(inX, 0, outX - inX, h,
            ColSelected, 0);
        TimelineCanvas.Children.Add(sel);

        // â”€â”€ Tick marks â”€â”€
        DrawTicks(w, h);

        // â”€â”€ Waveform hint bars (decorative; real waveform needs ffmpeg pipe) â”€â”€
        DrawWaveformHint(w, h, inX, outX);

        // â”€â”€ IN handle â”€â”€
        DrawHandle(inX, h, ColIn, "â–¶");

        // â”€â”€ OUT handle â”€â”€
        DrawHandle(outX, h, ColOut, "â—€");

        // â”€â”€ Playhead â”€â”€
        var phLine = new Line
        {
            X1 = phX, Y1 = 0, X2 = phX, Y2 = h,
            Stroke = new SolidColorBrush(ColPlayhead),
            StrokeThickness = 2,
            SnapsToDevicePixels = true
        };
        TimelineCanvas.Children.Add(phLine);

        // Playhead diamond head
        var diamond = new Polygon
        {
            Points = new PointCollection
            {
                new(phX - 5, 0),
                new(phX + 5, 0),
                new(phX,     8)
            },
            Fill = new SolidColorBrush(ColPlayhead)
        };
        TimelineCanvas.Children.Add(diamond);

        // Update filmstrip playhead overlay
        if (FilmstripBorder.Visibility == Visibility.Visible)
        {
            double fw = FilmstripCanvas.ActualWidth;
            if (fw > 0)
            {
                Canvas.SetLeft(FilmstripPlayhead, (_playhead / _durationSeconds) * fw - 1);
                FilmstripPlayhead.Height = FilmstripCanvas.ActualHeight;
            }
        }

        // Time labels
        LabelTimeStart.Text   = TrimSegment.FormatTime(_inPoint);
        LabelTimeCurrent.Text = TrimSegment.FormatTime(_playhead);
        LabelTimeEnd.Text     = TrimSegment.FormatTime(_outPoint);
        PlayheadTimeText.Text  = TrimSegment.FormatTime(_playhead);
    }

    private void DrawTicks(double w, double h)
    {
        // Choose tick interval: aim for ~10 ticks
        double rawInterval = _durationSeconds / 10.0;
        double[] niceIntervals = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
        double interval = niceIntervals.FirstOrDefault(v => v >= rawInterval, niceIntervals[^1]);

        for (double t = 0; t <= _durationSeconds; t += interval)
        {
            double x = t / _durationSeconds * w;
            bool major = (t % (interval * 5) < 0.001);

            var tick = new Line
            {
                X1 = x, Y1 = h, X2 = x, Y2 = major ? h * 0.6 : h * 0.8,
                Stroke = new SolidColorBrush(Color.FromRgb(0x48, 0x4F, 0x58)),
                StrokeThickness = major ? 1.5 : 1,
                SnapsToDevicePixels = true
            };
            TimelineCanvas.Children.Add(tick);

            if (major && x > 20 && x < w - 30)
            {
                var label = new TextBlock
                {
                    Text       = TrimSegment.FormatTime(t),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x48, 0x4F, 0x58)),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize   = 9
                };
                Canvas.SetLeft(label, x - 14);
                Canvas.SetTop(label, h * 0.58);
                TimelineCanvas.Children.Add(label);
            }
        }
    }

    private void DrawWaveformHint(double w, double h, double inX, double outX)
    {
        // Draw simple pseudo-waveform bars (random-seeded by filename) inside selected region
        var rng = new Random(_filePath.GetHashCode());
        int bars = (int)Math.Min(200, (outX - inX) / 3);
        if (bars < 2) return;
        double barW = (outX - inX) / bars;
        double midY = h / 2;

        for (int i = 0; i < bars; i++)
        {
            double amp   = rng.NextDouble() * midY * 0.65 + midY * 0.05;
            double barX  = inX + i * barW;
            var bar = new Rectangle
            {
                Width  = Math.Max(1, barW - 1),
                Height = amp * 2,
                Fill   = new SolidColorBrush(ColWaveform),
                RadiusX = 1, RadiusY = 1
            };
            Canvas.SetLeft(bar, barX);
            Canvas.SetTop(bar, midY - amp);
            TimelineCanvas.Children.Add(bar);
        }
    }

    private void DrawHandle(double x, double h, Color col, string arrow)
    {
        // Vertical line
        var line = new Line
        {
            X1 = x, Y1 = 0, X2 = x, Y2 = h,
            Stroke = new SolidColorBrush(col),
            StrokeThickness = 3,
            SnapsToDevicePixels = true
        };
        TimelineCanvas.Children.Add(line);

        // Grip tab
        var grip = new Border
        {
            Width      = 14,
            Height     = 22,
            Background = new SolidColorBrush(col),
            CornerRadius = new CornerRadius(3)
        };
        var gripLabel = new TextBlock
        {
            Text       = arrow,
            FontSize   = 8,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };
        grip.Child = gripLabel;
        Canvas.SetLeft(grip, x - 7);
        Canvas.SetTop(grip, h / 2 - 11);
        TimelineCanvas.Children.Add(grip);
    }

    private static Rectangle MakeRect(double x, double y, double w, double h, Color fill, double radius)
    {
        var r = new Rectangle
        {
            Width   = Math.Max(0, w),
            Height  = Math.Max(0, h),
            Fill    = new SolidColorBrush(fill),
            RadiusX = radius, RadiusY = radius
        };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        return r;
    }

    // â”€â”€ Mouse interaction â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private void Timeline_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (_durationSeconds <= 0) return;
        double x = e.GetPosition(TimelineCanvas).X;
        double w = TimelineCanvas.ActualWidth;
        double inX  = _inPoint  / _durationSeconds * w;
        double outX = _outPoint / _durationSeconds * w;

        // Hit-test: in-handle, out-handle, else playhead
        if (Math.Abs(x - inX) <= 10)
            _dragging = DragTarget.InHandle;
        else if (Math.Abs(x - outX) <= 10)
            _dragging = DragTarget.OutHandle;
        else
            _dragging = DragTarget.Playhead;

        TimelineCanvas.CaptureMouse();
        ApplyDrag(x);
    }

    private void Timeline_MouseMove(object s, MouseEventArgs e)
    {
        if (_dragging == DragTarget.None || _durationSeconds <= 0) return;
        ApplyDrag(e.GetPosition(TimelineCanvas).X);
    }

    private void Timeline_MouseUp(object s, MouseButtonEventArgs e)
    {
        _dragging = DragTarget.None;
        TimelineCanvas.ReleaseMouseCapture();
    }

    private void Timeline_MouseLeave(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragging = DragTarget.None;
            TimelineCanvas.ReleaseMouseCapture();
        }
    }

    private void ApplyDrag(double mouseX)
    {
        double w   = TimelineCanvas.ActualWidth;
        double sec = Math.Clamp(mouseX / w * _durationSeconds, 0, _durationSeconds);

        switch (_dragging)
        {
            case DragTarget.InHandle:
                _inPoint  = Math.Min(sec, _outPoint - 0.1);
                _playhead = _inPoint;
                UpdateTimeBoxes();
                break;
            case DragTarget.OutHandle:
                _outPoint = Math.Max(sec, _inPoint + 0.1);
                _playhead = _outPoint;
                UpdateTimeBoxes();
                break;
            case DragTarget.Playhead:
                _playhead = sec;
                break;
        }

        UpdateSelectionDisplay();
        DrawTimeline();

        if (_durationSeconds > 0)
        {
            _isSeeking = true;
            TrimPlayer.Position = TimeSpan.FromSeconds(_playhead);
            _isSeeking = false;
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  QUICK BUTTONS  /  KEYBOARD SHORTCUTS
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void SetIn_Click(object s, RoutedEventArgs e)  => SetInToPlayhead();
    private void SetOut_Click(object s, RoutedEventArgs e) => SetOutToPlayhead();
    private void ResetPoints_Click(object s, RoutedEventArgs e)
    {
        _inPoint  = 0;
        _outPoint = _durationSeconds;
        _playhead = 0;
        UpdateTimeBoxes();
        UpdateSelectionDisplay();
        DrawTimeline();
    }

    private void SetInToPlayhead()
    {
        if (_durationSeconds <= 0) return;
        _inPoint = Math.Min(_playhead, _outPoint - 0.1);
        UpdateTimeBoxes();
        UpdateSelectionDisplay();
        DrawTimeline();
    }
    private void SetOutToPlayhead()
    {
        if (_durationSeconds <= 0) return;
        _outPoint = Math.Max(_playhead, _inPoint + 0.1);
        UpdateTimeBoxes();
        UpdateSelectionDisplay();
        DrawTimeline();
    }

    private void Window_KeyDown(object s, KeyEventArgs e)
    {
        if (_durationSeconds <= 0) return;
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        double step = shift ? 10.0 : 1.0;

        switch (e.Key)
        {
            case Key.I:     SetInToPlayhead();  break;
            case Key.O:     SetOutToPlayhead(); break;
            case Key.Space: OpenInSystemPlayer(); break;
            case Key.Left:
                _playhead = Math.Max(0, _playhead - step);
                UpdateSelectionDisplay(); DrawTimeline(); break;
            case Key.Right:
                _playhead = Math.Min(_durationSeconds, _playhead + step);
                UpdateSelectionDisplay(); DrawTimeline(); break;
            case Key.Home:
                _playhead = 0;
                UpdateSelectionDisplay(); DrawTimeline(); break;
            case Key.End:
                _playhead = _durationSeconds;
                UpdateSelectionDisplay(); DrawTimeline(); break;
        }
        e.Handled = e.Key is Key.Left or Key.Right or Key.Home or Key.End
                                      or Key.I or Key.O or Key.Space;
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  TIME INPUT BOXES
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void TimeBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (s == InTimeBox)  ApplyInFromBox();
        else                 ApplyOutFromBox();
        e.Handled = true;
    }
    private void InTimeBox_LostFocus(object s, RoutedEventArgs e)  => ApplyInFromBox();
    private void OutTimeBox_LostFocus(object s, RoutedEventArgs e) => ApplyOutFromBox();
    private void SetInFromBox_Click(object s, RoutedEventArgs e)   => ApplyInFromBox();
    private void SetOutFromBox_Click(object s, RoutedEventArgs e)  => ApplyOutFromBox();

    private void ApplyInFromBox()
    {
        double t = TrimSegment.ParseTime(InTimeBox.Text);
        if (t < 0 || t >= _durationSeconds) { FlashBox(InTimeBox, true); return; }
        _inPoint  = Math.Min(t, _outPoint - 0.1);
        _playhead = _inPoint;
        UpdateTimeBoxes();
        UpdateSelectionDisplay();
        DrawTimeline();
    }
    private void ApplyOutFromBox()
    {
        double t = TrimSegment.ParseTime(OutTimeBox.Text);
        if (t <= 0 || t > _durationSeconds) { FlashBox(OutTimeBox, true); return; }
        _outPoint = Math.Max(t, _inPoint + 0.1);
        _playhead = _outPoint;
        UpdateTimeBoxes();
        UpdateSelectionDisplay();
        DrawTimeline();
    }
    private void UpdateTimeBoxes()
    {
        InTimeBox.Text  = TrimSegment.FormatTime(_inPoint);
        OutTimeBox.Text = TrimSegment.FormatTime(_outPoint);
    }
    private static void FlashBox(TextBox box, bool error)
    {
        var orig = box.BorderBrush;
        box.BorderBrush = error
            ? new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49))
            : new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
        var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) => { box.BorderBrush = orig; timer.Stop(); };
        timer.Start();
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  SEGMENTS  (multi-trim)
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void AddSegment_Click(object s, RoutedEventArgs e)
    {
        if (_durationSeconds <= 0) return;
        if (_outPoint - _inPoint < 0.1)
        {
            SetTrimStatus("Selection too short to add as segment.", "#D29922");
            return;
        }
        var seg = new TrimSegment
        {
            Index        = _segments.Count + 1,
            StartSeconds = _inPoint,
            EndSeconds   = _outPoint
        };
        _segments.Add(seg);
        SegmentList.SelectedItem = seg;
        SetTrimStatus($"Segment {seg.Index} added  ({seg.StartFormatted} â†’ {seg.EndFormatted})", "#3FB950");
    }

    private void RemoveSegment_Click(object s, RoutedEventArgs e)
    {
        if (SegmentList.SelectedItem is TrimSegment seg)
        {
            _segments.Remove(seg);
            // Re-index
            for (int i = 0; i < _segments.Count; i++)
                _segments[i].Index = i + 1;
        }
    }

    private void SegmentList_SelectionChanged(object s, SelectionChangedEventArgs e) { /* reserved */ }

    private void LoadSegmentIntoEditor_Click(object s, RoutedEventArgs e)
    {
        if (s is Button { Tag: TrimSegment seg })
        {
            _inPoint  = seg.StartSeconds;
            _outPoint = seg.EndSeconds;
            _playhead = seg.StartSeconds;
            UpdateTimeBoxes();
            UpdateSelectionDisplay();
            DrawTimeline();
        }
    }

    private void UpdateSegmentCount()
        => SegmentCountText.Text = _segments.Count.ToString();

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  OUTPUT SETTINGS
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void TrimMode_Changed(object? s, RoutedEventArgs e)
    {
        if (TrimModeHint == null) return;
        TrimModeHint.Text = TrimModeStreamCopy.IsChecked == true
            ? "Fast lossless cut. Output starts/ends on the nearest keyframe (may be Â±0.5 s)."
            : "Frame-accurate cut via full H.264 + AAC re-encode. Slower but precise to the frame.";
    }

    private void BrowseTrimOutput_Click(object s, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description         = "Select output folder for trimmed video",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _customOutputFolder           = dlg.SelectedPath;
            TrimOutputFolderText.Text     = _customOutputFolder;
            TrimOutputFolderText.Foreground = (Brush)FindResource("TextBrush");
        }
    }
    private void ResetTrimOutput_Click(object s, RoutedEventArgs e)
    {
        _customOutputFolder             = string.Empty;
        TrimOutputFolderText.Text       = "Same as source";
        TrimOutputFolderText.Foreground = (Brush)FindResource("MutedBrush");
    }

    private string GetOutputDir()
    {
        if (!string.IsNullOrEmpty(_customOutputFolder)) return _customOutputFolder;
        return System.IO.Path.GetDirectoryName(_filePath) ?? ".";
    }

    private string BuildOutputPath(int segIndex = 0, string? suffix = null)
    {
        var ext  = ((ComboBoxItem)TrimFormatBox.SelectedItem)?.Content?.ToString()?.ToLower() ?? "mp4";
        var dir  = GetOutputDir();
        var name = System.IO.Path.GetFileNameWithoutExtension(_filePath);
        var tag  = suffix ?? (segIndex == 0 ? "_trimmed" : $"_seg{segIndex:D2}");
        var path = System.IO.Path.Combine(dir, name + tag + "." + ext);
        return GetUniqueFilePath(path);
    }

    private string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir  = System.IO.Path.GetDirectoryName(path) ?? "";
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        string ext  = System.IO.Path.GetExtension(path);
        int i = 1;
        while (File.Exists(path)) { path = System.IO.Path.Combine(dir, $"{name} ({i++}){ext}"); }
        return path;
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  TRIM EXECUTION
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private async void TrimBtn_Click(object s, RoutedEventArgs e)
    {
        if (_isTrimming || string.IsNullOrEmpty(_filePath)) return;
        if (!File.Exists(FFmpeg))
        {
            MessageBox.Show("ffmpeg.exe not found.", "FFmpeg Missing",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Decide: use segments list or single in/out
        List<TrimSegment> jobs;
        if (_segments.Count > 0)
            jobs = _segments.ToList();
        else
        {
            // Single segment from current in/out
            jobs = new List<TrimSegment>
            {
                new() { Index = 1, StartSeconds = _inPoint, EndSeconds = _outPoint }
            };
        }

        bool concat  = _segments.Count > 1 && ConcatCheck.IsChecked == true;
        bool reEncode = TrimModeReEncode.IsChecked == true;

        _cts       = new CancellationTokenSource();
        _isTrimming = true;
        SetTrimRunningUI(true);

        try
        {
            var result = concat
                ? await TrimAndConcatAsync(jobs, reEncode, _cts.Token)
                : await TrimMultipleAsync(jobs, reEncode, _cts.Token);

            ShowTrimCompletionNotification(result, concat);
        }
        finally
        {
            _isTrimming = false;
            SetTrimRunningUI(false);
        }
    }

    // â”€â”€ Single or multiple independent segments â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private async Task<TrimOperationResult> TrimMultipleAsync(List<TrimSegment> jobs, bool reEncode, CancellationToken ct)
    {
        int done = 0, failed = 0;
        for (int i = 0; i < jobs.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var seg  = jobs[i];
            var outP = BuildOutputPath(jobs.Count > 1 ? seg.Index : 0);

            SetTrimStatus($"Trimming segment {seg.Index} / {jobs.Count}â€¦", "#388BFD");
            SetTrimProgress(0);

            bool ok = await RunTrimAsync(_filePath, outP, seg.StartSeconds, seg.EndSeconds,
                reEncode, seg.DurationSeconds, ct, progressOffset: i * 100.0 / jobs.Count, progressScale: 1.0 / jobs.Count);

            if (ok)
            {
                done++;
                _lastOutputFolder = GetOutputDir();
                SetTrimStatus($"âœ” Segment {seg.Index} saved â†’ {System.IO.Path.GetFileName(outP)}", "#3FB950");
            }
            else { failed++; }

            SetTrimProgress((i + 1) * 100.0 / jobs.Count);
        }

        if (!ct.IsCancellationRequested)
        {
            if (failed == 0) SetTrimStatus($"All {done} segment(s) trimmed successfully âœ”", "#3FB950");
            else SetTrimStatus($"{done} done Â· {failed} failed", "#F85149");
            OpenTrimFolderBtn.IsEnabled = done > 0;
        }
        else SetTrimStatus("Cancelled.", "#D29922");

        return new TrimOperationResult(done, failed, ct.IsCancellationRequested, done > 0);
    }

    // â”€â”€ Multi-segment concat  (trim each â†’ concat list â†’ final output) â”€â”€â”€â”€â”€â”€â”€â”€
    private async Task<TrimOperationResult> TrimAndConcatAsync(List<TrimSegment> jobs, bool reEncode, CancellationToken ct)
    {
        int done = 0;
        int failed = 0;
        bool concatOk = false;

        // Step 1: trim each segment to a temp file
        var tmpDir  = System.IO.Path.Combine(
            IsFolderWritable(AppDir) ? AppDir
                : System.IO.Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "VideoFixPro"),
            "trim_concat_tmp");
        Directory.CreateDirectory(tmpDir);

        var tmpFiles   = new List<string>();
        string concatList = string.Empty;   // hoisted: CS0165 fix â€” goto can bypass later assignment
        SetTrimStatus($"Trimming {jobs.Count} segmentsâ€¦", "#388BFD");

        for (int i = 0; i < jobs.Count; i++)
        {
            if (ct.IsCancellationRequested) goto cleanup;
            var seg  = jobs[i];
            var tmp  = System.IO.Path.Combine(tmpDir, $"seg_{GetHashCode()}_{i:D3}.mkv");

            bool ok = await RunTrimAsync(_filePath, tmp, seg.StartSeconds, seg.EndSeconds,
                reEncode, seg.DurationSeconds, ct,
                progressOffset: i * 100.0 / (jobs.Count + 1),
                progressScale:  1.0 / (jobs.Count + 1));

            if (!ok)
            {
                failed++;
                SetTrimStatus($"Segment {i + 1} failed â€” aborting.", "#F85149");
                goto cleanup;
            }

            done++;
            tmpFiles.Add(tmp);
        }

        if (ct.IsCancellationRequested) goto cleanup;

        // Step 2: write concat list file
        SetTrimStatus("Concatenating segmentsâ€¦", "#388BFD");
        SetTrimProgress(jobs.Count * 100.0 / (jobs.Count + 1));

        concatList = System.IO.Path.Combine(tmpDir, $"concat_{GetHashCode()}.txt");
        var sb = new StringBuilder();
        foreach (var f in tmpFiles)
            sb.AppendLine($"file '{f.Replace("'", "'\\''")}'");
        File.WriteAllText(concatList, sb.ToString());

        // Step 3: concat
        var finalOut = BuildOutputPath(suffix: "_merged");
        var concatBuilder = new StringBuilder();
        concatBuilder.Append($"-y -fflags +genpts -f concat -safe 0 -i \"{concatList}\" ");
        concatBuilder.Append("-c copy -map_metadata 0 -map_chapters 0 ");
        AppendExplorerFriendlyStreamMap(concatBuilder, finalOut);
        AppendExplorerFriendlyContainerFlags(concatBuilder, finalOut, _videoCodec);
        concatBuilder.Append($"\"{finalOut}\"");
        var concatArgs = concatBuilder.ToString();

        concatOk = await RunFFmpegRawAsync(concatArgs, totalDuration: jobs.Sum(j => j.DurationSeconds), ct,
            progressOffset: jobs.Count * 100.0 / (jobs.Count + 1),
            progressScale: 1.0 / (jobs.Count + 1));

        if (concatOk)
        {
            _lastOutputFolder = GetOutputDir();
            done = Math.Max(done, 1);
            SetTrimStatus($"âœ” Merged â†’ {System.IO.Path.GetFileName(finalOut)}", "#3FB950");
            OpenTrimFolderBtn.IsEnabled = true;
        }
        else
        {
            failed++;
            SetTrimStatus("Concat failed.", "#F85149");
        }

        SetTrimProgress(100);

        cleanup:
        // Clean up temp files
        foreach (var f in tmpFiles)
            try { File.Delete(f); } catch { }
        try { File.Delete(concatList); } catch { }
        try { Directory.Delete(tmpDir); } catch { }

        if (ct.IsCancellationRequested)
            SetTrimStatus("Cancelled.", "#D29922");

        return new TrimOperationResult(done, failed, ct.IsCancellationRequested, concatOk && !ct.IsCancellationRequested);
    }

    private void ShowTrimCompletionNotification(TrimOperationResult result, bool concat)
    {
        if (result.Cancelled)
        {
            return;
        }

        try
        {
            var ni = new WinForms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule?.FileName ?? ""),
                Visible = true,
                BalloonTipTitle = concat ? "Video Trim - Merge Complete" : "Video Trim - Complete",
                BalloonTipText = result.Failed == 0
                    ? (concat
                        ? $"Merged output saved successfully.\n{result.Done} step(s) completed."
                        : $"{result.Done} trim job(s) saved successfully.")
                    : $"{result.Done} done, {result.Failed} failed.",
                BalloonTipIcon = result.Failed == 0 ? WinForms.ToolTipIcon.Info : WinForms.ToolTipIcon.Warning
            };

            ni.ShowBalloonTip(3500);
            Task.Delay(5000).ContinueWith(_ => ni.Dispose());
        }
        catch
        {
        }
    }

    private async Task<bool> RunTrimAsync(string input, string output,
        double startSec, double endSec, bool reEncode, double segDuration,
        CancellationToken ct, double progressOffset = 0, double progressScale = 1.0)
    {
        var ic  = CultureInfo.InvariantCulture;
        var ss  = startSec.ToString("F3", ic);
        var dur = (endSec - startSec).ToString("F3", ic);
        
        var sb = new StringBuilder();
        sb.Append("-y ");

        // Common robust flags
        sb.Append("-err_detect ignore_err ");

        if (!reEncode)
        {
            // Stream Copy logic (matches MainWindow)
            sb.Append($"-ss {ss} -i \"{input}\" -t {dur} ");
            sb.Append("-fflags +genpts -c copy -map_metadata 0 -map_chapters 0 -dn -ignore_unknown -copyinkf -avoid_negative_ts make_zero ");
            AppendExplorerFriendlyStreamMap(sb, output);
            AppendExplorerFriendlyContainerFlags(sb, output, _videoCodec);
        }
        else
        {
            // Re-encode logic (matches MainWindow)
            sb.Append($"-i \"{input}\" -ss {ss} -t {dur} ");
            
            // Map quality percent to CRF (18-40)
            int crf = (int)(40 - (_qualityPercent * (40 - 18) / 100.0));

            if (_hasNvidia)
                sb.Append($"-c:v h264_nvenc -preset fast -rc vbr -cq {crf} -b:v 0 -pix_fmt yuv420p ");
            else if (_hasAmd)
                sb.Append($"-c:v h264_amf -rc 0 -qp_i {crf} -qp_p {crf} -qp_b {crf} -pix_fmt yuv420p ");
            else
                sb.Append($"-c:v libx264 -preset fast -crf {crf} -pix_fmt yuv420p ");

            sb.Append("-c:a aac -b:a 192k ");
        }

        sb.Append("-progress pipe:1 ");
        sb.Append($"\"{output}\"");

        return await RunFFmpegRawAsync(sb.ToString(), segDuration, ct, progressOffset, progressScale);
    }

    private static void AppendExplorerFriendlyContainerFlags(StringBuilder sb, string output, string? videoCodec)
    {
        var ext = System.IO.Path.GetExtension(output).ToLowerInvariant();
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
        var ext = System.IO.Path.GetExtension(output).ToLowerInvariant();
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



    // â”€â”€ Raw ffmpeg runner with progress parsing â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private async Task<bool> RunFFmpegRawAsync(string args, double totalDuration,
        CancellationToken ct, double progressOffset = 0, double progressScale = 1.0)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = FFmpeg,
            Arguments              = args,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        ProcessGuard.Watch(proc);
        _ffmpegProcess = proc;

        // Read stderr silently (errors go there)
        _ = proc.StandardError.ReadToEndAsync();

        // Parse progress from stdout
        await Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (ct.IsCancellationRequested) break;
                if (line.StartsWith("out_time_ms=") &&
                    long.TryParse(line[12..], out long ms) && totalDuration > 0)
                {
                    double pct = Math.Min(ms / 1_000_000.0 / totalDuration * 100, 99.9);
                    double scaled = progressOffset + pct * progressScale;
                    Dispatcher.Invoke(() => SetTrimProgress(scaled));
                }
            }
        }, ct);

        if (ct.IsCancellationRequested)
        {
            try { proc.Kill(); } catch { }
            _ffmpegProcess = null;
            return false;
        }

        try 
        { 
            await proc.WaitForExitAsync(ct); 
        } 
        catch (OperationCanceledException) { /* Canceled */ }

        _ffmpegProcess = null;
        return proc.ExitCode == 0;
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  ADD TO MAIN QUEUE
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void AddToQueue_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_filePath))
        {
            MessageBox.Show("Load a video file first.", "No File", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (Owner is MainWindow main)
        {
            var mode = TrimModeReEncode.IsChecked == true ? RepairMode.ReEncode : RepairMode.StreamCopy;
            main.AddTrimJobToQueue(_filePath, _inPoint, _outPoint, mode);
            SetTrimStatus($"Added {mode} trim job to main queue  ({TrimSegment.FormatTime(_inPoint)} â†’ {TrimSegment.FormatTime(_outPoint)})", "#3FB950");
        }
        else
            MessageBox.Show("Open the Trim Tool from the main window to use this feature.",
                "No Main Window", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  CANCEL / OPEN FOLDER
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void CancelTrim_Click(object s, RoutedEventArgs e) => CancelTrimOp();
    private void CancelTrimOp()
    {
        _cts?.Cancel();
        try { _ffmpegProcess?.Kill(); } catch { }
    }

    private void OpenTrimFolder_Click(object s, RoutedEventArgs e)
    {
        var folder = string.IsNullOrEmpty(_lastOutputFolder) ? GetOutputDir() : _lastOutputFolder;
        if (Directory.Exists(folder))
            Process.Start("explorer.exe", folder);
    }

    private void Preview_Click(object s, RoutedEventArgs e)
    {
        if (_isPlayerPlaying)
        {
            TrimPlayer.Pause();
            _isPlayerPlaying = false;
            _playheadTimer.Stop();
        }
        else
        {
            if (TrimPlayer.Position.TotalSeconds >= _durationSeconds - 0.1)
                TrimPlayer.Position = TimeSpan.FromSeconds(_inPoint);

            TrimPlayer.Play();
            _isPlayerPlaying = true;
            _playheadTimer.Start();
        }
        UpdatePlayPauseUI();
    }

    private void TogglePlay_Click(object s, RoutedEventArgs e) => Preview_Click(s, e);

    private void UpdatePlayheadFromPlayer()
    {
        if (_isSeeking || _durationSeconds <= 0) return;
        double pos = TrimPlayer.Position.TotalSeconds;
        if (pos != _playhead)
        {
            _playhead = Math.Clamp(pos, 0, _durationSeconds);
            Dispatcher.Invoke(() =>
            {
                LabelTimeCurrent.Text = TrimSegment.FormatTime(_playhead);
                DrawTimeline();
            });
        }
    }

    private void UpdatePlayPauseUI()
    {
        PlayPauseBtn.Content = _isPlayerPlaying ? "||" : ">";
        PlayPauseBtn.Opacity = _isPlayerPlaying ? 0.2 : 0.6;
    }

    private void TrimPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (TrimPlayer.NaturalDuration.HasTimeSpan)
        {
            // Successfully opened
            TrimPlayer.Pause();
            _isPlayerPlaying = false;
            UpdatePlayPauseUI();
        }
    }

    private void TrimPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        TrimPlayer.Pause();
        _isPlayerPlaying = false;
        _playheadTimer.Stop();
        UpdatePlayPauseUI();
    }

    private void PlayPauseBtn_MouseEnter(object s, MouseEventArgs e) => PlayPauseBtn.Opacity = 0.9;
    private void PlayPauseBtn_MouseLeave(object s, MouseEventArgs e) => PlayPauseBtn.Opacity = _isPlayerPlaying ? 0.2 : 0.6;

    private void OpenInSystemPlayer()
    {
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) return;
        try { Process.Start(new ProcessStartInfo(_filePath) { UseShellExecute = true }); }
        catch { }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  UI HELPERS
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    private void UpdateSelectionDisplay()
    {
        var dur = TrimSegment.FormatTime(_outPoint - _inPoint);
        SelectionDurationText.Text  = dur;
        SelectionDurationBadge.Text = $"Selection: {dur}";
    }

    private void SetTrimStatus(string text, string hexColour = "#8B949E")
    {
        Dispatcher.Invoke(() =>
        {
            TrimStatusText.Text = UiTextSanitizer.Normalize(text);
            TrimStatusDot.Fill = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hexColour));
        });
    }

    private void SetTrimProgress(double pct)
    {
        Dispatcher.Invoke(() =>
        {
            TrimProgressBar.Value   = Math.Clamp(pct, 0, 100);
            TrimProgressText.Text   = $"{pct:F0}%";
        });
    }

    private void SetTrimRunningUI(bool running)
    {
        Dispatcher.Invoke(() =>
        {
            TrimBtn.IsEnabled           = !running;
            AddToQueueBtn.IsEnabled     = !running;
            CancelTrimBtn.IsEnabled     = running;
            TrimProgressBar.Visibility  = running ? Visibility.Visible : Visibility.Collapsed;
            TrimProgressText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (!running)
            {
                TrimProgressBar.Value = 0;
                OpenTrimFolderBtn.IsEnabled = !string.IsNullOrEmpty(_lastOutputFolder);
            }
        });
    }

    private void OutTimeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (OutTimeBox == null)
            return;
        var text = OutTimeBox.Text.Trim();
        var isValid = string.IsNullOrEmpty(text) || TrimSegment.ParseTime(text) >= 0;
        OutTimeBox.BorderBrush = isValid
            ? (Brush)FindResource("MutedBrush")
            : Brushes.IndianRed;
    }

    // â”€â”€ Static ffmpeg runner (probe / filmstrip) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private static async Task<string> RunProcessAsync(string exe, string args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        
        using var registration = ct.Register(() => { try { p.Kill(); } catch { } });
        
        // Fix P5: Read both streams to prevent hang
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        
        await Task.WhenAll(outTask, errTask);
        try { await p.WaitForExitAsync(ct); } catch (OperationCanceledException) { }

        return await outTask;
    }
}



