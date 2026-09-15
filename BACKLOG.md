# Backlog

Items retain the order requested.

## SHIPPED

1. Five-minute game replay with bounded storage: src/ReplayEngine.cs, src/MemoryReplayBuffer.cs.
2. Lightweight 720p defaults: src/Models.cs, src/MainForm.cs.
3. Independently implement GPU encoding and RAM buffering informed by Medal behavior: src/ReplayEngine.cs, src/MemoryReplayBuffer.cs.

## ANSWERED

4. Scope clarified: Windows recorder and editor first; cloud/social/mobile deferred. See FeatureInventory.md.

## OPEN

4. Complete desktop feature parity. Recorder, library and basic editor delivered in this preview. Remaining: game-specific event integrations (target games needed), per-application audio, native game hooks, multitrack timeline, animated stickers and transitions. Files: src/ReplayEngine.cs, src/AudioPipe.cs, src/EditorForm.cs, src/MediaTools.cs, FeatureInventory.md.
5. Publish public GitHub source: repository and .github/workflows/build.yml.
6. Publish executable: build.ps1, RELEASE-NOTES.md and release assets.
