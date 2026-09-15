# Security and privacy

GameReplay is a local recorder. Capture starts only through a user command or an explicitly enabled startup/game-profile setting. Microphone, webcam, and voice recognition are opt-in. The app does not upload recordings or send telemetry.

The recording engine is downloaded only during explicit setup from the fixed URL in `EngineInstaller.cs`; its SHA-256 is checked before installation. Releases do not contain Medal code or services.

Please report reproducible security issues through the repository's GitHub security reporting feature if available. Avoid attaching private recordings, access tokens, or personal diagnostics to public issues. Ordinary bugs can be filed as GitHub issues with redacted reproduction steps.

The public Windows build is unsigned. Check the release SHA256SUMS file to verify the downloaded executable.
