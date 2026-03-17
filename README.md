# FrameSnip

FrameSnip is a minimal Windows screenshot tool built around persistent on-screen frames.

It lets you keep one or more lightweight wireframe capture regions on screen, grab screenshots directly to the clipboard, and annotate them in place without opening a separate editor.

## Features

- Always-on-top wireframe capture frames
- Multiple frames in a single app process
- Active and inactive modes
- Click-through the frame to interact with windows behing the frame
- Thin visible frame with enlarged invisible resize hit area
- Instant framed capture to clipboard
- Conventional drag-to-select capture mode
- In-place annotation mode on the frozen screenshot
- Pen, eraser, rectangle, and redact tools
- Red, blue, green, and black annotation colors
- Undo and redo
- Copy annotated result to clipboard
- Save annotated result as PNG
- OCR captured text to clipboard
- Tray menu for creating, focusing, capturing, and closing frames

## How It Works

### Framed capture

1. Position a frame where you want it.
2. Click the camera button or press `Ctrl+Shift+S`.
3. The original screenshot is copied to the clipboard immediately.
4. The frame switches into annotation mode on that same captured image.
5. Use `Copy` or `Ctrl+C` if you want the edited version on the clipboard instead.

### Conventional capture

1. Click the top-left selection button inside a frame.
2. The frame temporarily hides.
3. Click and drag anywhere on screen to select a region.
4. Release to capture that selection.
5. The original screenshot is copied to the clipboard immediately.
6. Annotation mode opens at the selected region.

## Controls

### Global frame controls

- `Ctrl+Shift+Space`: toggle active and inactive mode
- `Ctrl+Shift+S`: capture the current frame
- `Ctrl+Shift+N`: create a new frame from the focused frame
- `Ctrl+Shift+Q`: close the current frame
- `Esc`: leave active mode or leave annotation mode

### Annotation controls

- `Ctrl+Z`: undo
- `Ctrl+Y`: redo
- `Ctrl+C`: copy annotated screenshot and return to inactive mode

Toolbar tools:

- Pen
- Eraser
- Rectangle
- Redact
- Color picker
- Undo
- Redo
- Save PNG
- OCR
- Copy
- Back

## Platform

FrameSnip is currently Windows-only.

It depends on:

- WPF
- Win32 hotkeys and window behavior
- Windows clipboard APIs
- Windows OCR APIs

## Project Layout

```text
wireframe_screenshot/
|- ScreenshotOverlay/
|  |- Assets/
|  |- Interop/
|  |- Models/
|  |- Services/
|  |- App.xaml
|  |- App.xaml.cs
|  |- OverlayWindow.xaml
|  |- OverlayWindow.xaml.cs
|  |- SelectionOverlayWindow.xaml
|  |- SelectionOverlayWindow.xaml.cs
|  `- ScreenshotOverlay.csproj
|- dotnet-sdk-8.0.419-win-x64/
|- .gitignore
`- README.md
```

## Run Locally

This repo is set up to use the portable .NET SDK folder already included locally.

From the repo root:

```powershell
.\dotnet-sdk-8.0.419-win-x64\dotnet.exe run --project .\ScreenshotOverlay\ScreenshotOverlay.csproj
```

## Publish a Standalone EXE

From the repo root:

```powershell
Stop-Process -Name FrameSnip -Force -ErrorAction SilentlyContinue
.\dotnet-sdk-8.0.419-win-x64\dotnet.exe publish .\ScreenshotOverlay\ScreenshotOverlay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish\FrameSnip
```

If the published EXE is locked by Windows, publish to a fresh folder instead:

```powershell
.\dotnet-sdk-8.0.419-win-x64\dotnet.exe publish .\ScreenshotOverlay\ScreenshotOverlay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish\FrameSnip-v2
```

