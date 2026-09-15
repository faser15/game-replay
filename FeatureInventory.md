# Desktop recorder and editor scope

This is an independently implemented Windows recorder/editor. It does not use Medal code, assets, game databases, or services. Feature parity is a checklist, not a claim that every Medal capability is complete. Cloud accounts, publishing, feeds, friends, and mobile apps are outside this first desktop release.

Baseline researched on 2026-09-15 from [Medal's Windows guide](https://support.medal.tv/support/solutions/articles/48000959661/) and [Medal's desktop overview](https://medal.tv/about). These describe capture, configurable hotkeys, audio controls, editing, and sharing. [Medal's recording modes](https://support.medal.tv/support/solutions/articles/48001157616) distinguish short retrospective clips and long sessions.

## Implementation checklist

“Implemented” below means the module and its UI path are present in source, not that compatibility with every game or device has been tested. Release checks exercise file formats and helper behavior; actual speech, microphones, webcams, and individual games need device-level checks.

| Capability | Implementation / verification state |
|---|---|
| Retrospective replay clips; configurable duration | Implemented; configurable main and shorter-clip hotkeys |
| Low-load 720p default; resolution/FPS/bitrate/encoder controls | Implemented; game/device performance must be measured locally |
| Bounded temporary buffer and saved library storage | Implemented; RAM/disk buffer and configurable library limit |
| Full session recording and bookmarks | Implemented; bookmarks saved as timestamp JSON beside the session video |
| Monitor / selected window capture | Implemented using Desktop Duplication / Windows Graphics Capture |
| Screenshots | Implemented and wired to button/hotkey; captures visible screen pixels, including overlapping windows; hidden/minimized windows rejected |
| Configurable capture, screenshot, session, and bookmark hotkeys | Implemented; parser and persistence helpers have automated checks; conflicting registrations report unavailability |
| Per-game profiles and automatic recording | Implemented local executable matching and opt-in detection; replay/session preference is stored per profile; real game lifecycle needs verification |
| Windows startup | Opt-in HKCU startup helper implemented; UI wiring inspected; no registry change from reading status |
| Voice clipping | Implemented and wired for replay saves; opt-in microphone and installed English Windows recognizer required; actual recognition reliability untested |
| System audio and microphone controls | Implemented with gain, denoise, and optional separate system/mic tracks; hardware recording requires verification |
| Webcam overlay | Implemented with selected device, corner, and size; camera compatibility untested |
| Local library, search, favorites, tags/albums, import and playback | Implemented; playback opens the default Windows media player |
| Trimming, aspect ratio, caption text, speed, volume/mute, MP4/GIF | Implemented; exports preserve original files; CPU encoding |
| Montage export | Verified concatenation in list order preserving audio and filling silent inputs; no timeline or transitions |
| Music soundtrack and static watermark | Implemented and export tested, including silent input |
| Sound effects timeline, animated stickers, multitrack visual timeline | Not implemented |
| Automatic in-game kills/wins/event detection | Not implemented; awaiting target games/events and individual integration requirements |
| Injected game capture, exclusive fullscreen guarantees | Not implemented; use the supported capture method and borderless fullscreen |
| Separate Discord/application-only audio isolation | Not implemented; system audio includes other applications |
| Cloud/social/mobile features | Deferred by requested desktop-first scope |

## Desktop module files

- `src/DesktopFeatures.cs`: window enumeration, screenshots, configurable global hotkeys, timestamp bookmarks.
- `src/GameProfiles.cs`: explicit user-created executable profiles and running-process matching; no built-in game identification database.
- `src/VoiceClipping.cs`: local Windows recognizer for “clip that” and “save clip”; speech event subscriber must marshal callbacks to the UI thread.
- `src/DesktopStartup.cs`: reads/writes this user's Windows startup entry only when explicitly enabled/disabled by the user.
- `src/DesktopFeatureChecks.cs`: hotkey validation, profile and recording-mode persistence, and bookmark timestamp readbacks; does not activate screen capture, microphone, or startup registration.

Screen/window recording cannot guarantee compatibility with protected content or every game. Test in the specific game before relying on captured footage. Screenshot capture is visible desktop capture and does not bypass other windows.

