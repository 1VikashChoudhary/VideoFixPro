# VideoFixPro

<p align="center">
  <img width="1178" height="881" alt="VideoFixPro Main Window" src="https://github.com/user-attachments/assets/45fb1a35-1e3e-4313-8206-a24f47c25b73" />
</p>

<p align="center">
  <img width="1134" height="775" alt="VideoFixPro Trim Window" src="https://github.com/user-attachments/assets/71023bdd-ba53-4beb-9253-837cfc594b29" />
</p>

<p align="center">
  <img width="1366" height="893" alt="VideoFixPro Video Toolbox" src="https://github.com/user-attachments/assets/dd1a9011-0bed-48d7-85e1-926338ab95b2" />
</p>

<p align="center">
  <img width="1186" height="793" alt="VideoFixPro Color Grade Studio" src="https://github.com/user-attachments/assets/dc6f9fee-197e-448c-9b32-b68760afff09" />
</p>

<p align="center">
  <strong>Professional Commercial-Grade Video Repair, Recovery & Studio Suite for Windows</strong><br>
  <em>Powered by .NET 8, WPF, and high-performance FFmpeg / FFprobe media pipelines.</em>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue?logo=windows" alt="Platform" />
  <img src="https://img.shields.io/badge/.NET-8.0%20WPF-purple?logo=dotnet" alt=".NET 8" />
  <img src="https://img.shields.io/badge/Engine-FFmpeg%20%26%20FFprobe-green?logo=ffmpeg" alt="FFmpeg" />
  <img src="https://img.shields.io/badge/GPU%20Accel-NVENC%20%7C%20AMF%20%7C%20QSV-red" alt="GPU Acceleration" />
  <img src="https://img.shields.io/badge/License-MIT-brightgreen" alt="License" />
</p>

---

## Overview

**VideoFixPro** is an all-in-one desktop utility engineered for video recovery, batch processing, lossless trimming, and creative studio enhancements. Designed with a sleek GitHub Dark Dimmed interface, robust process crash-guarding, and hardware-accelerated transcoding, it handles everything from unplayable or truncated videos to advanced color grading, watermarking, and metadata sanitization.

---

## Key Feature Matrix

### 1. Core Video Repair & Recovery Engine
* **Intelligent Auto Repair**: Automatically tests ultra-fast stream copy re-muxing first, seamlessly falling back to error-tolerant deep recovery if container structures are damaged.
* **Deep Container Reconstruction**: Fixes missing `moov` atoms, truncated headers, corrupted index tables, and non-monotonic timestamps (`+genpts`, `+discardcorrupt`).
* **Batch Processing Queue**: Drag-and-drop individual files or entire folders. Supports multi-job queuing with real-time ETA, frame rate, progress percentage, and taskbar integration.
* **Explorer-Friendly Output**: Automatically injects MP4 faststart (`+faststart+use_metadata_tags`), proper stream mapping (`-map 0:v:0 -map 0:a?`), and Apple/Windows-compatible codec tags (`hvc1` for HEVC, `avc1` for H.264).
* **Data Safety Guards**: Verified non-empty output validation (`>= 1024` bytes) before any optional source file cleanup to prevent accidental data loss.

### 2. Studio Toolset & Specialized Modules

| Studio / Tool | Description | Capabilities |
| :--- | :--- | :--- |
| ✂ **Video Trimmer** | Timeline-based multi-segment video editor | Lossless stream-copy or frame-accurate re-encode cuts, live filmstrip thumbnails, In/Out cue markers, multi-segment concat merger, and direct queue export. |
| ⚡ **Video Toolbox** | 8-in-1 quick media converter & processor | Format conversion, audio extraction (MP3/AAC/WAV/FLAC), quick compression, speed adjustments, audio removal, video looping, reverse video, and crop/resize. |
| 🎨 **Color Grade & Visual FX** | Real-time color correction & filters | Brightness, contrast, gamma, saturation, sharpness, vignette, hue, warmth, color balance, invert, grayscale, and sepia with live preview rendering. |
| 🗜 **Smart Compressor** | Target size & quality-based compressor | Target file size (MB) calculator, CRF & custom bitrate control, resolution downscaling, and multi-threaded GPU encoding. |
| ⏱ **Speed Studio** | High-precision speed controller | 0.25x slow-motion up to 10x fast-motion with automatic audio pitch preservation (`atempo`). |
| 🎞 **GIF & WebP Animator** | High-quality animated image generator | Two-pass `palettegen` & `paletteuse` color quantization, FPS & dimension scaling, bounce/reverse loops, and custom range selection. |
| 💧 **Watermark & Logo Studio** | Visual branding & text/image overlay | Text & PNG/JPG watermark overlays, 9-point anchor alignment, margin offsets, opacity sliders, and real-time positioning preview. |
| 🛡 **Metadata Cleaner & GPS Stripper** | Privacy & metadata sanitization | Full container/stream metadata inspection, automatic detection of sensitive ISO6709 GPS location coordinates, and one-click total metadata stripping. |
| 🔗 **Video Merger & Joiner** | Multi-clip concatenation | Lossless stream copy re-muxing or uniform resolution/codec re-encoding with drag-and-drop order rearrangement. |

---

## Hardware GPU Acceleration

VideoFixPro automatically detects available GPU hardware upon launch and leverages native vendor-specific hardware acceleration:

| Vendor | Video Encoder | Pixel Format |
| :--- | :--- | :--- |
| **NVIDIA** | `h264_nvenc`, `hevc_nvenc` | `yuv420p` |
| **AMD** | `h264_amf`, `hevc_amf` | `yuv420p` |
| **Intel** | `h264_qsv`, `hevc_qsv` | `nv12` / `yuv420p` |
| **CPU Fallback** | `libx264`, `libx265` | `yuv420p` |

---

## Supported Input & Output Formats

Works seamlessly across all modern video and audio containers:
* **Video**: `.mp4`, `.mkv`, `.avi`, `.mov`, `.wmv`, `.flv`, `.webm`, `.m4v`, `.ts`, `.m2ts`
* **Audio**: `.mp3`, `.wav`, `.aac`, `.m4a`, `.flac`, `.ogg`, `.wma`
* **Animated**: `.gif`, `.webp`

---

## Tech Stack & Architecture

* **Framework**: `.NET 8.0` (C# 12)
* **UI Technology**: Windows Presentation Foundation (`WPF`) with Custom WindowChrome
* **Media Engine**: `FFmpeg` (Transcoding & Filtering) and `FFprobe` (Metadata & Stream Analysis)
* **Process Lifetime Management**: Custom `ProcessGuard` with Win32 Job Object interop for zero orphaned background processes
* **UI Threading**: Asynchronous worker pipelines (`Task.Run`) coupled with non-blocking `Dispatcher.BeginInvoke` telemetry streaming

---

## Project Structure

```text
VideoFixPro/
├── VideoFixPro.sln                      # Visual Studio Solution
├── README.md                            # Documentation
├── LICENSE.txt                          # MIT License
└── VideoFixPro/
    ├── App.xaml / App.xaml.cs           # Application lifecycle & global theme resources
    ├── MainWindow.xaml (.cs)            # Main dashboard & batch repair queue
    ├── TrimWindow.xaml (.cs)            # Video Trimmer & timeline editor
    ├── VideoToolboxWindow.xaml (.cs)    # 8-in-1 Quick Tools utility suite
    ├── ColorGradeWindow.xaml (.cs)      # Color Grading & Visual FX studio
    ├── CompressorWindow.xaml (.cs)      # Smart Video Compressor
    ├── SpeedStudioWindow.xaml (.cs)     # Speed & Tempo Studio
    ├── GifMakerWindow.xaml (.cs)        # GIF & WebP Animation Maker
    ├── WatermarkWindow.xaml (.cs)       # Watermark & Logo Studio
    ├── MetadataCleanerWindow.xaml (.cs) # Metadata & GPS Stripper
    ├── VideoMergerWindow.xaml (.cs)     # Multi-video merger & joiner
    ├── ProcessGuard.cs                  # Child process supervisor & crash resilience
    ├── UiTextSanitizer.cs               # UI text normalization & glyph safety
    ├── StatusColorConverter.cs          # WPF UI status binding converters
    ├── Models/                          # Data structures (VideoJob, TrimSegment, etc.)
    ├── Assets/                          # App icons, vectors, and branding
    └── ffmpeg/                          # FFmpeg & FFprobe portable binaries
```

---

## Getting Started

### Prerequisites
* Windows 10 (Build 19041+) or Windows 11
* [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or [.NET 8.0 SDK](https://dotnet.microsoft.com/download)

### FFmpeg Setup
VideoFixPro looks for `ffmpeg.exe` and `ffprobe.exe` in:
1. `VideoFixPro\ffmpeg\ffmpeg.exe` (Application directory)
2. `%LOCALAPPDATA%\VideoFixPro\ffmpeg\ffmpeg.exe`

> **Note:** If the binaries are not found on initial launch, the application provides a one-click automated download flow to fetch and configure official static builds.

---

## Building from Source

### 1. Clone the repository
```powershell
git clone https://github.com/1VikashChoudhary/VideoFixPro.git
cd VideoFixPro
```

### 2. Build Debug Configuration
```powershell
dotnet build .\VideoFixPro.sln -c Debug
```

### 3. Build Optimized Release
```powershell
dotnet build .\VideoFixPro.sln -c Release
```

### 4. Run the Application
```powershell
dotnet run --project .\VideoFixPro\VideoFixPro.csproj
```

---

## Trimmer Keyboard Shortcuts

| Key | Action |
| :--- | :--- |
| `I` | Set In point to current playhead |
| `O` | Set Out point to current playhead |
| `Space` | Play / Pause preview |
| `←` / `→` | Seek backward / forward by 1 second |
| `Shift + ←` / `Shift + →` | Seek backward / forward by 10 seconds |
| `Home` / `End` | Jump to start / end of media |

---

## License

This project is licensed under the [MIT License](LICENSE.txt).

---

## Author & Attribution

**Video Fix Pro** is built with ❤️ by **[Vikash Choudhary](https://github.com/1VikashChoudhary)**.
