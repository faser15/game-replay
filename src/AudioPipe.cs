using System.Diagnostics;
using System.IO.Pipes;
using NAudio.Wave;
namespace GameReplay;

/// <summary>A paced PCM pipe. Silence is emitted when a WASAPI loopback device is idle.</summary>
sealed class AudioPipe : IDisposable
{
    readonly IWaveIn capture;
    readonly BufferedWaveProvider buffer;
    readonly NamedPipeServerStream pipe;
    readonly CancellationTokenSource cancel = new();
    public string? Failure;
    public string[] InputArguments { get; }
    public AudioPipe(bool microphone, int device = -1)
    {
        capture = microphone ? new WaveInEvent { DeviceNumber = device, WaveFormat = new WaveFormat(48000,16,1), BufferMilliseconds=20 } : new WasapiLoopbackCapture();
        var format = capture.WaveFormat;
        bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat || (format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        string raw = floating ? "f32le" : "s" + format.BitsPerSample + "le";
        buffer = new BufferedWaveProvider(format) { BufferDuration=TimeSpan.FromSeconds(1), DiscardOnBufferOverflow=true, ReadFully=true };
        string name = "GameReplay-Audio-" + Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream(name,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        InputArguments = ["-thread_queue_size","64","-probesize","32","-analyzeduration","0","-f",raw,"-ar",format.SampleRate.ToString(),"-ac",format.Channels.ToString(),"-i",@"\\.\pipe\"+name];
        capture.DataAvailable += (_,e) => buffer.AddSamples(e.Buffer,0,e.BytesRecorded);
        capture.RecordingStopped += (_,e) => { if(!cancel.IsCancellationRequested) Failure = e.Exception?.Message ?? "The audio device stopped."; };
        // Open now so a denied microphone/device fails before the recorder reports success.
        try { capture.StartRecording(); } catch { capture.Dispose(); pipe.Dispose(); cancel.Dispose(); throw; }
        _ = Task.Run(async()=> {
            try {
                await pipe.WaitForConnectionAsync(cancel.Token);
                buffer.ClearBuffer();
                var clock = Stopwatch.StartNew(); long written = 0;
                byte[] block = new byte[format.SampleRate / 100 * format.BlockAlign];
                while(!cancel.IsCancellationRequested) {
                    var due = TimeSpan.FromSeconds((double)written/format.AverageBytesPerSecond) - clock.Elapsed;
                    if(due > TimeSpan.Zero) await Task.Delay(due,cancel.Token);
                    buffer.Read(block,0,block.Length);
                    await pipe.WriteAsync(block,cancel.Token); written += block.Length;
                }
            } catch(Exception e) { if(!cancel.IsCancellationRequested) Failure=e.Message; }
        });
    }
    public void Dispose() { cancel.Cancel(); try { capture.StopRecording(); } catch {} capture.Dispose(); pipe.Dispose(); }
}


