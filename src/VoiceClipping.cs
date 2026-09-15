using System.Speech.Recognition;

namespace GameReplay;

// Start is explicitly opt-in. Windows recognition runs locally on the default microphone.
public sealed class VoiceClipping : IDisposable
{
    SpeechRecognitionEngine? recognizer;
    DateTime lastRequestUtc;
    public event Action? ActionClipRequested;
    public event Action<string>? RecognitionError;
    public bool Running => recognizer != null;
    public static bool IsAvailable(out string reason)
    {
        try
        {
            bool installed = SpeechRecognitionEngine.InstalledRecognizers().Any(r => r.Culture.TwoLetterISOLanguageName == "en");
            reason = installed ? "Windows English speech recognition is available." : "Install an English Windows speech recognition language in Windows Settings to enable voice clipping.";
            return installed;
        }
        catch (Exception e) { reason = "Windows speech recognition is unavailable: " + e.Message; return false; }
    }
    public void Start()
    {
        if (Running) return;
        var info = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
            ?? throw new InvalidOperationException("No English Windows speech recognizer is installed.");
        var engine = new SpeechRecognitionEngine(info);
        try
        {
            var grammar = new GrammarBuilder { Culture = info.Culture };
            grammar.Append(new Choices("clip that", "save clip"));
            engine.LoadGrammar(new Grammar(grammar));
            engine.SpeechRecognized += OnRecognized;
            engine.RecognizeCompleted += OnCompleted;
            engine.SetInputToDefaultAudioDevice();
            recognizer = engine;
            engine.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch { recognizer = null; engine.Dispose(); throw; }
    }
    void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (e.Result.Confidence < 0.75f || DateTime.UtcNow - lastRequestUtc < TimeSpan.FromSeconds(5)) return;
        lastRequestUtc = DateTime.UtcNow;
        ActionClipRequested?.Invoke(); // Caller marshals to UI before manipulating controls.
    }
    void OnCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (e.Error != null) RecognitionError?.Invoke(e.Error.Message);
    }
    public void Stop()
    {
        var engine = recognizer; recognizer = null;
        if (engine == null) return;
        engine.SpeechRecognized -= OnRecognized; engine.RecognizeCompleted -= OnCompleted;
        try { engine.RecognizeAsyncCancel(); } finally { engine.Dispose(); }
    }
    public void Dispose() => Stop();
}
