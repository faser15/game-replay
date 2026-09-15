# Validation — 1.3 desktop preview

Verified on Windows with NVIDIA hardware on 2026-09-15:

- Release build succeeds without warnings; self-contained executable starts.
- Pinned recording-engine download, SHA-256 verification and extraction succeed.
- Synthetic 322-second input verifies five-minute RAM/disk retention, byte limits, saving and cleanup.
- Live owned-window Windows Graphics Capture uses NVENC at 1280x720. A 15-second request exports 16 seconds of complete segments while capture continues, with no unsaved video disk writes in RAM mode.
- Full-session test exports 22.2 seconds beyond its 15-second replay setting. Both live exports fully decode.
- Editor checks pass for trim, speed, volume, captions, crop, MP4/GIF, soundtrack, watermark and audio-preserving merge. Cancellation preserves originals and removes temporary exports.
- Storage checks include recursive media accounting and rejection of oversized operations. Active operations sample storage, so brief overshoot remains possible.
- Desktop helper checks cover hotkeys, profiles and bookmarks. All settings tabs were rendered and inspected.

Microphone, webcam, voice recognition and particular game compatibility have not been validated on physical devices. The live integration test intentionally captures only its own synthetic window and disables audio. CI runs synthetic recording and editor checks; live GPU capture requires an interactive desktop.
