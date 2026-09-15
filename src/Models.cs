namespace GameReplay;

public sealed class RecorderOptions
{
    public int DisplayIndex { get; set; }
    public long WindowHandle { get; set; }
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;
    public int BitrateKbps { get; set; } = 3000;
    public string Encoder { get; set; } = "Auto";
    public int ClipSeconds { get; set; } = 300;
    public bool MemoryBuffer { get; set; } = true;
    public bool SystemAudio { get; set; } = true;
    public bool Microphone { get; set; }
    public int MicrophoneDevice { get; set; } = -1;
    public float MicGain { get; set; } = 1;
    public bool NoiseSuppression { get; set; }
    public bool SeparateAudioTracks { get; set; }
    public bool CaptureCursor { get; set; } = true;
    public string WebcamDevice { get; set; } = "";
    public string WebcamCorner { get; set; } = "BottomRight";
    public int WebcamSize { get; set; } = 240;
}

public sealed class AppSettings
{
    public RecorderOptions Recording { get; set; } = new();
    public string ClipHotkey { get; set; } = "Ctrl+Shift+F8";
    public string ShortClipHotkey { get; set; } = "Ctrl+Shift+F6";
    public int ShortClipSeconds { get; set; } = 30;
    public string SessionHotkey { get; set; } = "Ctrl+Shift+F10";
    public string ScreenshotHotkey { get; set; } = "Ctrl+Shift+F9";
    public string BookmarkHotkey { get; set; } = "Ctrl+Shift+F7";
    public bool SoundAlerts { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool RecordOnLaunch { get; set; }
    public bool VoiceClipping { get; set; }
    public bool AutoDetectGames { get; set; }
    public string SaveFolder { get; set; } = "";
    public int StorageLimitGiB { get; set; } = 5;
}
