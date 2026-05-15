# VideoFixPro

VideoFixPro is a Windows desktop app for repairing, trimming, and batch-processing video files with a clean WPF interface powered by `ffmpeg` and `ffprobe`.

It is designed for practical day-to-day recovery work:
- repair damaged or hard-to-play videos
- batch process multiple files
- trim videos quickly with either stream copy or full re-encode
- merge multiple trimmed segments
- preview progress, metadata, thumbnails, and output status in one place

## Features

### Video repair
- Batch queue for multiple video files
- Drag and drop support for files and folders
- Video metadata preview using `ffprobe`
- Automatic thumbnail generation for queued items
- Output filename collision protection
- Progress tracking with per-job updates and overall progress
- Optional output folder selection
- Optional source auto-delete after successful repair

### Repair modes
- `Auto`
  Tries fast stream copy first, then falls back to deep recovery if needed.
- `Stream Copy`
  Fastest option. Re-muxes without re-encoding when possible.
- `Re-encode`
  Rebuilds video as H.264 + AAC for broad compatibility.
- `Deep Recover`
  Error-tolerant recovery path for more damaged files.

### Trim tool
- Dedicated trim window with timeline-based editing
- In/Out point selection
- Keyboard shortcuts for fast trimming
- Single trim or multi-segment trim workflow
- Merge multiple segments into one final output
- Add trim jobs directly to the main repair queue
- Stream copy trim mode for fast lossless cuts
- Re-encode trim mode for frame-accurate output
- Completion notifications for trim operations

### Usability
- Built-in FFmpeg download flow if binaries are missing
- GPU encoder detection for supported systems
- Log panel with copy/save/clear actions
- Output folder shortcuts
- Desktop notifications on completion
- Safe cancellation handling during long-running operations

## Tech Stack

- `.NET 8`
- `WPF`
- `ffmpeg`
- `ffprobe`
- `Windows Forms` interop for folder dialogs and notifications

## Project Structure

```text
VideoFixPro_v3/
├─ VideoFixPro.sln
├─ README.md
└─ VideoFixPro/
   ├─ MainWindow.xaml
   ├─ MainWindow.xaml.cs
   ├─ TrimWindow.xaml
   ├─ TrimWindow.xaml.cs
   ├─ Models/
   ├─ Assets/
   └─ VideoFixPro.csproj
```

## Requirements

- Windows
- .NET 8 SDK
- `ffmpeg.exe`
- `ffprobe.exe`

## FFmpeg Setup

VideoFixPro expects these files:

```text
VideoFixPro/ffmpeg/ffmpeg.exe
VideoFixPro/ffmpeg/ffprobe.exe
```

If they are not present, the app can prompt to download FFmpeg automatically.

## Build

From the repository root:

```powershell
dotnet build .\VideoFixPro.sln -c Debug
```

For a release build:

```powershell
dotnet build .\VideoFixPro.sln -c Release
```

## Run

You can run the project from Visual Studio, or from the command line:

```powershell
dotnet run --project .\VideoFixPro\VideoFixPro.csproj
```

## How To Use

### Repair videos
1. Launch the app.
2. Drag video files or folders into the queue, or use the file picker.
3. Choose a repair mode.
4. Select an output format and optional output folder.
5. Click `Fix All Videos`.
6. Open the output folder when processing completes.

### Trim videos
1. Open the trim tool from the main window.
2. Load a video.
3. Set `In` and `Out` points on the timeline.
4. Choose:
   - `Stream Copy` for speed
   - `Re-encode` for accuracy
5. Click `Trim Video`.
6. Optionally add multiple segments and merge them.

## Trim Keyboard Shortcuts

- `I` set In point
- `O` set Out point
- `Space` preview in player
- `Left / Right` move by 1 second
- `Shift + Left / Right` move by 10 seconds
- `Home / End` jump

## Output Notes

- Re-encode mode prioritizes compatibility.
- Stream copy mode prioritizes speed and avoiding quality loss.
- Trimmed and repaired files are saved with safe unique filenames to avoid silent overwrite.

## Supported Input Formats

The app is currently configured to work with common video formats including:

- `mp4`
- `mkv`
- `avi`
- `mov`
- `wmv`
- `flv`
- `webm`
- `m4v`
- `ts`
- `m2ts`

## Why VideoFixPro

VideoFixPro is focused on being practical rather than complicated:
- fast repair flow for normal cases
- deeper recovery for damaged files
- simple trim workflow without needing a full editor
- batch processing for real-world workloads

## Known Limitations

- Stream copy depends on the original source streams being mux-compatible.
- Frame-accurate cuts are best done with re-encode mode.
- Very heavily corrupted files may still require manual FFmpeg work outside the UI.

## Development Notes

- Target framework: `net8.0-windows`
- UI framework: `WPF`
- Main app entry point: `VideoFixPro.App`

## Contributing

Suggestions, issue reports, and UI/feature improvements are welcome.

If you contribute:
- keep the UI responsive during long operations
- preserve safe cancellation behavior
- avoid silent overwrite behavior
- prefer compatibility-first defaults for end users

## License

This project is licensed under the `MIT` License.

See [LICENSE.txt](/C:/Users/vikas/Downloads/VideoFixPro_v5/VideoFixPro_v3/LICENSE.txt) for details.

## Author

Built by Vikash Choudhary.
