# Backlog

Items retain the order requested.

## SHIPPED

1. Five-minute game replay with bounded storage: src/ReplayEngine.cs, src/MemoryReplayBuffer.cs.
2. Lightweight 720p defaults: src/Models.cs, src/MainForm.cs.
3. Independently implement GPU encoding and RAM buffering informed by Medal behavior: src/ReplayEngine.cs, src/MemoryReplayBuffer.cs.

5. Public source: https://github.com/faser15/game-replay . Files: .github/workflows/build.yml and source.

6. Public Windows executable: https://github.com/faser15/game-replay/releases/tag/v1.3.0 . Files: build.ps1, RELEASE-NOTES.md and release assets.

7. Fix readable settings during recording: src/MainForm.cs; ApplySettings still guards active recording.
8. Add logo, executable/window/tray icon and dark navigation: src/MainForm.cs, src/BrandIcon.cs, src/Assets/GameReplay.ico, src/GameReplay.csproj. Released in 1.3.1.

## ANSWERED

4. Scope clarified: Windows recorder and editor first; cloud/social/mobile deferred. See FeatureInventory.md.

## OPEN

4. Complete desktop feature parity. Recorder, library and basic editor delivered in this preview. Remaining: game-specific event integrations (target games needed), per-application audio, native game hooks, multitrack timeline, animated stickers and transitions. Files: src/ReplayEngine.cs, src/AudioPipe.cs, src/EditorForm.cs, src/MediaTools.cs, FeatureInventory.md.




