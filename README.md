# GameReplay

A local Windows game recorder and clip editor with a lightweight 720p default.

[Download the Windows executable](https://github.com/faser15/game-replay/releases/latest) · [Feature checklist](FeatureInventory.md)

## Start

1. Download `GameReplay.exe` from Releases and keep it in a permanent folder.
2. Open it and choose **Install recording engine** once. This downloads a pinned, SHA-256-verified FFmpeg distribution (~106 MiB). The app itself includes its .NET runtime.
3. Choose a screen or game window and click **Start buffer**.
4. Press **Ctrl+Shift+F8** to save the replay. Open **Clip library / editor** to browse and edit clips.

Exit an earlier GameReplay version from its tray menu before opening this one. Avoid recording the same game with several apps at once.

## Recorder

- Screen and selected-window capture. On supported GPUs, Windows Graphics Capture resizes directly on the GPU; desktop-only fallback uses Desktop Duplication.
- Configurable 15–600 second replay, RAM/disk buffer, 360p–4K, 24/30/60/120 FPS, bitrate, and NVIDIA/AMD/Intel or explicit CPU encoding.
- Full-session recording and timestamp bookmarks.
- System audio, optional microphone, gain, spectral denoise, mixed or separate audio tracks, optional webcam overlay.
- Configurable replay, short-clip, session, screenshot, and bookmark hotkeys.
- Opt-in voice saving through installed Windows English speech recognition.
- User-created game profiles and automatic capture for matching executable/window names.
- Custom clip folder and storage quota; microphone, webcam, voice, auto-recording, and Windows startup default off.

Defaults remain **720p / 30 FPS / 3 Mbps**, with about **120 MB** per five-minute clip. RAM buffering trades compressed-video RAM for continuous disk writes. Quality and duration increases also increase resource use. Replay saves use whole two-second segments; an odd duration can round up by up to two seconds.

## Library and editor

Search and sort local clips; favorites, tags, albums, import, rename, recycle, and playback in the Windows default player. Edit copies with trim, aspect crop, resize, speed, volume/mute, caption, soundtrack, watermark, MP4/GIF export, and audio-preserving merge. Originals are retained. Exporting uses CPU encoding and can temporarily use extra disk space.

## Compatibility and current limits

Windows 10/11 x64. Borderless/windowed gameplay is recommended. Hardware behavior varies; protected content and every exclusive-fullscreen game are not guaranteed. Selected-window capture never silently falls back to recording the whole desktop.

This is **not complete Medal feature parity**. Per-game kill/win detection needs individual game integrations. Injected game hooks, per-application audio isolation, a full multitrack timeline, AI highlights, and cloud/social/mobile services are not included in this release. See the checklist for precise scope and validation.

The release is unsigned. App data and clips default to `%LOCALAPPDATA%\GameReplay`; change the clip folder in Settings. No accounts, telemetry, or automatic uploads are built in. Unsaved RAM video disappears on exit. Save full sessions before exiting; crash recovery for unfinished sessions is not implemented.

## Build

Install the .NET 8 SDK on Windows, then:

```powershell
./build.ps1
# Includes engine setup, synthetic recorder tests, desktop helper checks, and editor tests:
./build.ps1 -Test
```

Output: `artifacts/win-x64/GameReplay.exe`. Live capture validation needs an interactive Windows desktop and compatible GPU:

```powershell
./artifacts/win-x64/GameReplay.exe --capture-test ./artifacts/tests
```

CI builds Windows binaries and runs synthetic verification. A real microphone, webcam, Windows speech recognizer, and specific games require separate device tests.

## Source

- `src/MainForm.cs`: active desktop UI; `src/Program.cs` retains the earlier UI source and is excluded from builds.
- `src/ReplayEngine.cs`, `src/MemoryReplayBuffer.cs`, `src/AudioPipe.cs`: recording.
- `src/ClipLibraryForm.cs`, `src/EditorForm.cs`, `src/MediaTools.cs`: library/editing.
- `src/DesktopFeatures.cs`, `src/GameProfiles.cs`, `src/VoiceClipping.cs`: desktop controls.
- `src/EngineInstaller.cs`: pinned engine setup.

MIT licensed. [Dependency notices](THIRD-PARTY-NOTICES.md). Independently implemented; no Medal code, art, game databases, or services are included.
