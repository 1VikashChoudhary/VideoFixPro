using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace VideoFixPro
{
    public partial class AudioVisualizerWindow : Window
    {
        private TimeSpan _totalDuration = TimeSpan.Zero;
        private Process? _ffmpegProcess;
        private bool _isCancelled = false;
        private readonly bool _hasNvidia;
        private readonly bool _hasAmd;
        private readonly bool _hasIntel;

        public AudioVisualizerWindow(bool hasNvidia, bool hasAmd, bool hasIntel)
        {
            InitializeComponent();
            _hasNvidia = hasNvidia;
            _hasAmd = hasAmd;
            _hasIntel = hasIntel;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
                if (files.Length > 0)
                {
                    string ext = Path.GetExtension(files[0]).ToLower();
                    if (ext == ".mp3" || ext == ".wav" || ext == ".m4a" || ext == ".flac")
                    {
                        InputAudioBox.Text = files[0];
                    }
                    else
                    {
                        MessageBox.Show("Please drop an audio file (mp3, wav, m4a, flac).", "Invalid file", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
        }

        private void BrowseBg_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp";
            if (dlg.ShowDialog() == true)
            {
                BackgroundImageBox.Text = dlg.FileName;
            }
        }

        private void ClearBg_Click(object sender, RoutedEventArgs e)
        {
            BackgroundImageBox.Text = string.Empty;
        }

        private async void Generate_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(InputAudioBox.Text) || !File.Exists(InputAudioBox.Text))
            {
                MessageBox.Show("Please provide a valid input audio file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string inputAudio = InputAudioBox.Text;
            string bgImage = BackgroundImageBox.Text;
            string outPath = Path.Combine(Path.GetDirectoryName(inputAudio)!, Path.GetFileNameWithoutExtension(inputAudio) + "_visualizer.mp4");

            int count = 1;
            while (File.Exists(outPath))
            {
                outPath = Path.Combine(Path.GetDirectoryName(inputAudio)!, Path.GetFileNameWithoutExtension(inputAudio) + $"_visualizer_{count}.mp4");
                count++;
            }

            LogBox.Clear();
            ProcessProgressBar.Value = 0;
            ProcessProgressText.Text = "0%";
            _totalDuration = await GetAudioDuration(inputAudio);
            _isCancelled = false;

            bool hasBg = !string.IsNullOrWhiteSpace(bgImage) && File.Exists(bgImage);
            string args = "";
            
            bool isWaveform = StyleBox.SelectedIndex == 0;
            string filterCore = isWaveform ? "showwaves=s=1280x720:mode=cline:colors=white" : "showfreqs=s=1280x720:mode=bar:colors=white";

            if (hasBg)
            {
                args = $"-i \"{inputAudio}\" -loop 1 -framerate 30 -i \"{bgImage}\" -filter_complex \"[0:a]{filterCore}[wave];[1:v]scale=1280:720[bg];[bg][wave]overlay=format=auto:shortest=1[outv]\" -map \"[outv]\" -map 0:a -c:v libx264 -preset fast -pix_fmt yuv420p \"{outPath}\" -y";
            }
            else
            {
                args = $"-i \"{inputAudio}\" -filter_complex \"[0:a]{filterCore}[v]\" -map \"[v]\" -map 0:a -c:v libx264 -preset fast -pix_fmt yuv420p \"{outPath}\" -y";
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                if (_hasNvidia) InjectNvCudaPath(psi);
                
                _ffmpegProcess = new Process { StartInfo = psi };

                ProcessGuard.Watch(_ffmpegProcess);
                
                _ffmpegProcess.ErrorDataReceived += (s, ev) =>
                {
                    if (!string.IsNullOrEmpty(ev.Data))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            LogBox.AppendText(ev.Data + Environment.NewLine);
                            LogBox.ScrollToEnd();
                            UpdateProgress(ev.Data);
                        });
                    }
                };
                
                _ffmpegProcess.OutputDataReceived += (s, ev) =>
                {
                    if (!string.IsNullOrEmpty(ev.Data))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            LogBox.AppendText(ev.Data + Environment.NewLine);
                            LogBox.ScrollToEnd();
                        });
                    }
                };

                _ffmpegProcess.Start();
                _ffmpegProcess.BeginErrorReadLine();
                _ffmpegProcess.BeginOutputReadLine();

                await Task.Run(() => _ffmpegProcess.WaitForExit());

                if (_ffmpegProcess.ExitCode == 0 && !_isCancelled)
                {
                    ProcessProgressBar.Value = 100;
                    ProcessProgressText.Text = "100%";
                    MessageBox.Show("Visualizer video generated successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (_isCancelled)
                {
                    LogBox.AppendText("\nProcess cancelled.\n");
                }
                else
                {
                    MessageBox.Show("FFmpeg exited with error.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                if (!_isCancelled)
                    MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (_ffmpegProcess != null)
                {
                    ProcessGuard.Unwatch(_ffmpegProcess);
                    _ffmpegProcess = null;
                }
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _isCancelled = true;
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                try
                {
                    _ffmpegProcess.Kill();
                }
                catch { }
            }
            LogBox.AppendText("\nCancellation requested...\n");
        }
        
        private async Task<TimeSpan> GetAudioDuration(string file)
        {
            try
            {
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffprobe.exe"),
                        Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{file}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    }
                };
                proc.Start();
                string output = await proc.StandardOutput.ReadToEndAsync();
                await Task.Run(() => proc.WaitForExit());
                if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double secs))
                {
                    return TimeSpan.FromSeconds(secs);
                }
            }
            catch {}
            return TimeSpan.Zero;
        }

        private void UpdateProgress(string data)
        {
            if (_totalDuration.TotalSeconds <= 0) return;

            var match = Regex.Match(data, @"time=(\d{2}):(\d{2}):(\d{2}\.\d{2})");
            if (match.Success)
            {
                if (TimeSpan.TryParse(match.Value.Substring(5), out TimeSpan current))
                {
                    double percent = (current.TotalSeconds / _totalDuration.TotalSeconds) * 100.0;
                    if (percent > 100) percent = 100;
                    if (percent < 0) percent = 0;
                    
                    ProcessProgressBar.Value = percent;
                    ProcessProgressText.Text = $"{percent:F1}%";
                }
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
                
                var driverStore = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                            "System32", "DriverStore", "FileRepository");
                if (Directory.Exists(driverStore))
                {
                    foreach (var pattern in new[] { "nv_disp*", "nvdsp*", "nvlt*", "nvmi*" })
                        foreach (var dir in Directory.GetDirectories(driverStore, pattern, SearchOption.TopDirectoryOnly))
                            foreach (var name in new[] { "nvcuda64.dll", "nvcuda.dll" })
                                if (File.Exists(Path.Combine(dir, name))) { _nvCudaDir = dir; return _nvCudaDir; }
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

    }
}