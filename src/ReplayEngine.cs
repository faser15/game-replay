using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GameReplay;

public sealed class ReplayEngine : IDisposable
{
    public const long BufferLimit = 192L * 1024 * 1024;
    public const long LibraryLimit = 5L * 1024 * 1024 * 1024;
    public readonly string Root;
    string clipsPath;
    public string Clips => clipsPath;
    long storageLimitBytes = LibraryLimit;
    public long StorageLimitBytes { get => storageLimitBytes; set { if(value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); storageLimitBytes=value; } }
    public void ConfigureStorage(string directory, long limit)
    {
        if(Running || Saving) throw new InvalidOperationException("Stop the capture before changing storage.");
        if(limit < 512L*1024*1024 || limit > 1024L*1024*1024*1024) throw new ArgumentOutOfRangeException(nameof(limit));
        string path=Path.GetFullPath(directory); Directory.CreateDirectory(path); StorageBudget.WriteLimit(path,limit); clipsPath=path; StorageLimitBytes=limit;
    }
    void CheckStorageQuota(long stored, long needed) { if(needed > StorageLimitBytes - stored) throw new IOException($"Saved clips have reached the {StorageLimitBytes/(1024.0*1024*1024):0.##} GB storage limit. Move or delete clips or increase the limit in Settings."); }
    public string Buffer => Path.Combine(Root, "Buffer");
    public string Ffmpeg => File.Exists(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")) ? Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") : Path.Combine(Root, "Engine", "ffmpeg.exe");
    public string Encoder { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public bool Running => process is { HasExited: false };
    public bool Saving { get; private set; }
    public DateTime Started { get; private set; }
    public int RecordingFps { get; private set; } = 30;
    Process? process;
    AudioPipe? audio;
    AudioPipe? microphone;
    RecorderOptions options = new();
    public bool SessionRunning { get; private set; }
    bool sessionPending;
    public string CaptureBackend { get; private set; } = "";
    long ActiveBufferLimit => Math.Max(BufferLimit, (long)options.BitrateKbps * 1000 / 8 * options.ClipSeconds * 14 / 10);
    MemoryReplayBuffer? memory;
    public bool MemoryMode => memory != null;
    public int BufferedSegments => memory?.Count ?? Math.Max(0,Chunks().Length-(Running?1:0));
    public long BufferedBytes => memory?.Bytes ?? Chunks().Sum(f=>f.Length);
    internal string CreateMemoryOutput() { memory?.Dispose(); memory = new MemoryReplayBuffer(options.ClipSeconds, ActiveBufferLimit); return memory.OutputPattern; }
    readonly object gate = new();
    readonly Queue<string> log = new();
    public ReplayEngine(string root, string? clipsDirectory = null)
    {
        Root = root;
        clipsPath=Path.GetFullPath(clipsDirectory ?? Path.Combine(root,"Clips"));
        Directory.CreateDirectory(Buffer);
        Directory.CreateDirectory(Clips);
        foreach (var f in Directory.GetFiles(Buffer)) File.Delete(f);
        // Partial exports are retained for manual recovery; never sweep the chosen clips folder.
    }
    public Process Launch(IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = Buffer };
        foreach (string arg in arguments) psi.ArgumentList.Add(arg);
        var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) return; lock (log) { log.Enqueue(e.Data); while(log.Count > 100) log.Dequeue(); } };
        p.Start(); p.BeginErrorReadLine();
        ProcessLifetime.Attach(p);
        return p;
    }
    public string Diagnostics { get { lock(log) return string.Join(Environment.NewLine,log); } }
    static string[] Codec(string encoder, RecorderOptions o)
    {
        var args = new List<string> { "-c:v", encoder };
        args.AddRange(encoder switch {
            "h264_nvenc" => ["-preset","p1","-tune","ll","-rc","cbr"],
            "h264_amf" => ["-quality","speed","-rc","cbr"],
            "h264_qsv" => ["-preset","veryfast"],
            "libx264" => ["-preset","ultrafast","-tune","zerolatency","-threads","2"],
            _ => throw new ArgumentException("Unknown encoder.")
        });
        args.AddRange(["-b:v",o.BitrateKbps+"k","-maxrate",o.BitrateKbps+"k","-bufsize",(o.BitrateKbps*2)+"k","-g",(o.Fps*2).ToString(),"-bf","0"]);
        return args.ToArray();
    }
    static string CaptureSource(RecorderOptions o, bool gfx) => gfx
        ? $"gfxcapture={(o.WindowHandle != 0 ? "hwnd="+o.WindowHandle : "monitor_idx="+o.DisplayIndex)}:width={o.Width}:height={o.Height}:resize_mode=scale_aspect:max_framerate={o.Fps}:capture_cursor={(o.CaptureCursor?1:0)}"
        : $"ddagrab=output_idx={o.DisplayIndex}:framerate={o.Fps}:draw_mouse={(o.CaptureCursor?1:0)}";
    static string CpuFilter(RecorderOptions o, bool gfx) => "hwdownload,format=bgra," + (gfx ? "" : $"scale={o.Width}:{o.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=fast_bilinear,") + "format=nv12";
    public Task StartAsync(int display, int fps, bool sound, string preferred, bool memoryBuffer = true) => StartAsync(new RecorderOptions { DisplayIndex=display, Fps=fps, SystemAudio=sound, Encoder=preferred, MemoryBuffer=memoryBuffer });
    public Task StartAsync(RecorderOptions settings) => StartCoreAsync(settings,false);
    public Task StartSessionAsync(RecorderOptions settings) => StartCoreAsync(settings,true);
    async Task StartCoreAsync(RecorderOptions o, bool session)
    {
        if(Running) throw new InvalidOperationException("Stop the current capture first.");
        if(Saving) throw new InvalidOperationException("Wait for the current save.");
        if(sessionPending) throw new InvalidOperationException("Save the pending session before starting another capture.");
        if(o.Width%2!=0 || o.Height%2!=0 || o.Width<320 || o.Width>3840 || o.Height<180 || o.Height>2160 || o.Fps<15 || o.Fps>120 || o.BitrateKbps<500 || o.BitrateKbps>30000 || o.ClipSeconds<15 || o.ClipSeconds>600 || o.MicGain<0 || o.MicGain>5) throw new ArgumentException("Recording settings are outside supported bounds.");
        options=o; RecordingFps=o.Fps; LastError="";
        memory?.Dispose(); memory=null;
        if(!File.Exists(Ffmpeg)) throw new Exception("Recording engine is missing. Install FFmpeg in Settings.");
        if(new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace < 1024L*1024*1024) throw new Exception("At least 1 GB free space is required to start.");
        CheckStorageQuota(StorageBudget.UsedBytes(Clips),16L*1024*1024);
        foreach(var f in Directory.GetFiles(Buffer)) File.Delete(f);
        var candidates = o.Encoder == "Auto" ? new[]{"h264_nvenc","h264_amf","h264_qsv"} : new[]{o.Encoder};
        bool? chosenGfx=null;
        foreach(bool gfx in o.WindowHandle != 0 ? new[]{true} : new[]{true,false}) {
            foreach(string codec in candidates) {
                var probeArgs = new List<string> {"-hide_banner","-loglevel","warning","-f","lavfi","-i",CaptureSource(o,gfx)};
                if(!gfx || codec!="h264_nvenc" || o.WebcamDevice.Length>0) probeArgs.AddRange(["-vf",CpuFilter(o,gfx)]);
                probeArgs.AddRange(Codec(codec,o)); probeArgs.AddRange(["-frames:v","2","-f","null","-"]);
                using var probe=Launch(probeArgs);
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await probe.WaitForExitAsync(timeout.Token); } catch(OperationCanceledException) { probe.Kill(true); await probe.WaitForExitAsync(); }
                if(probe.ExitCode!=0) continue;
                Encoder=codec; chosenGfx=gfx; break;
            }
            if(chosenGfx.HasValue) break;
        }
        if(chosenGfx==null) throw new Exception(o.WindowHandle!=0 ? "Could not capture the selected game window. Use borderless/windowed mode and select its current window. Desktop capture was not substituted.\n"+Diagnostics : "No capture/encoder combination worked. Try another display or choose the CPU encoder explicitly.\n"+Diagnostics);
        bool useGfx=chosenGfx.Value;
        CaptureBackend=useGfx ? "Windows Graphics Capture" : "Desktop Duplication";
        var args=new List<string>{"-hide_banner","-loglevel","warning","-y","-filter_threads","2","-filter_complex_threads","2","-thread_queue_size","8","-f","lavfi","-i",CaptureSource(o,useGfx)};
        try {
            int next=1, systemIndex=-1, micIndex=-1, webcamIndex=-1;
            if(o.SystemAudio) { audio=new AudioPipe(false); args.AddRange(audio.InputArguments); systemIndex=next++; }
            if(o.Microphone) { microphone=new AudioPipe(true,o.MicrophoneDevice); args.AddRange(microphone.InputArguments); micIndex=next++; }
            if(o.WebcamDevice.Length>0) { webcamIndex=next++; args.AddRange(["-thread_queue_size","8","-f","dshow","-i","video="+o.WebcamDevice]); }
            var filters=new List<string>();
            if(webcamIndex>=0) {
                int size=Math.Clamp(o.WebcamSize,80,o.Width/2);
                string x=o.WebcamCorner.Contains("Left") ? "16" : "main_w-overlay_w-16";
                string y=o.WebcamCorner.Contains("Top") ? "16" : "main_h-overlay_h-16";
                filters.Add($"[0:v]{CpuFilter(o,useGfx)}[screen]");
                filters.Add($"[{webcamIndex}:v]scale={size}:-2[cam]");
                filters.Add($"[screen][cam]overlay=x={x}:y={y}:eof_action=pass,format=nv12[video]");
                args.AddRange(["-map","[video]"]);
            } else {
                args.AddRange(["-map","0:v:0"]);
                if(!useGfx || Encoder!="h264_nvenc") args.AddRange(["-vf",CpuFilter(o,useGfx)]);
            }
            if(micIndex>=0) filters.Add($"[{micIndex}:a]volume={o.MicGain.ToString(CultureInfo.InvariantCulture)}"+(o.NoiseSuppression?",afftdn=nf=-25":"")+"[mic]");
            if(systemIndex>=0 && micIndex>=0 && !o.SeparateAudioTracks) {
                filters.Add($"[{systemIndex}:a][mic]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95[mixed]"); args.AddRange(["-map","[mixed]"]);
            } else {
                if(systemIndex>=0) args.AddRange(["-map",systemIndex+":a:0","-metadata:s:a:0","title=System audio"]);
                if(micIndex>=0) args.AddRange(["-map","[mic]","-metadata:s:a:"+(systemIndex>=0?1:0),"title=Microphone"]);
            }
            if(filters.Count>0) args.AddRange(["-filter_complex",string.Join(";",filters)]);
            if(systemIndex>=0 || micIndex>=0) args.AddRange(["-c:a","aac","-b:a","128k","-ac","2"]);
            args.AddRange(["-r",o.Fps.ToString(),"-fps_mode","cfr"]); args.AddRange(Codec(Encoder,o));
            string output=!session && o.MemoryBuffer ? CreateMemoryOutput() : "chunk-%08d.ts";
            args.AddRange(["-force_key_frames","expr:gte(t,n_forced*2)","-f","segment","-segment_time","2","-segment_format","mpegts","-reset_timestamps","1",output]);
            process=Launch(args); Started=DateTime.UtcNow;
            await Task.Delay(3500);
            if(!Running) throw new Exception("Capture could not start. "+Diagnostics);
            SessionRunning=session; sessionPending=session; Maintain();
        } catch { await StopAsync(false); throw; }
    }
    public async Task<string> StopSessionAsync()
    {
        if(!sessionPending) throw new InvalidOperationException("No session is pending.");
        await StopAsync(false);
        string result=await SaveAsync();
        sessionPending=false; SessionRunning=false;
        await StopAsync(true);
        return result;
    }
    public FileInfo[] Chunks() => new DirectoryInfo(Buffer).GetFiles("chunk-*.ts").OrderBy(f=>f.Name,StringComparer.Ordinal).ToArray();
    public static void CheckLibraryQuota(long stored,long needed) { if(stored + needed > LibraryLimit) throw new Exception("Your saved clips have reached the 5 GB limit. Move or delete some clips in Open Clips, then save again."); }
    public static FileInfo[] ToPrune(FileInfo[] chunks) => ToPrune(chunks, 152, BufferLimit);
    static FileInfo[] ToPrune(FileInfo[] chunks, int count, long limit)
    {
        var remove = new List<FileInfo>(); long total = chunks.Sum(f=>f.Length);
        for(int i=0;i<chunks.Length-1 && (chunks.Length-i > count || total > limit);i++) { remove.Add(chunks[i]); total -= chunks[i].Length; }
        return remove.ToArray();
    }
    public void Maintain()
    {
        lock(gate) {
            if(!Saving && !sessionPending) foreach(var f in ToPrune(Chunks(), (options.ClipSeconds+1)/2+2, ActiveBufferLimit)) f.Delete();
        }
        if(microphone?.Failure != null) throw new IOException("Microphone stopped: " + microphone.Failure);
        if(sessionPending && Running) CheckStorageQuota(StorageBudget.UsedBytes(Clips), BufferedBytes + 32L*1024*1024);
        if(audio?.Failure != null) throw new IOException("Audio capture stopped: " + audio.Failure);
        if(memory?.Failure != null) throw new IOException("Memory buffer stopped: " + memory.Failure);
        if(Running && new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace < 512L*1024*1024) throw new IOException("Recording stopped because less than 512 MB is free.");
    }
    public async Task<string> SaveAsync(int? seconds = null)
    {
        if(Saving) throw new InvalidOperationException("A clip is already being saved.");
        if(SessionRunning && Running) throw new InvalidOperationException("Stop and save the full session first.");
        int duration = sessionPending ? int.MaxValue : Math.Clamp(seconds ?? options.ClipSeconds, 1, options.ClipSeconds);
        // Retain whole two-second GOPs so odd requested lengths never discard the newest moment.
        int exportDuration = sessionPending ? int.MaxValue : ((duration+1)/2)*2;
        Saving = true;
        string? partial = null;
        try {
            // Wait for the segment containing the hotkey moment to close. Capture keeps running.
            var before = Chunks().LastOrDefault()?.Name;
            if(Running && memory != null) {
                long sequence = memory.CompletedSequence;
                var until = DateTime.UtcNow.AddSeconds(8);
                while(Running && memory.CompletedSequence == sequence && DateTime.UtcNow < until) await Task.Delay(100);
                if(memory.CompletedSequence == sequence) throw new Exception("Capture is stalled. Stop and restart the buffer.");
            } else if(Running && before != null) {
                var until = DateTime.UtcNow.AddSeconds(8);
                while(Running && Chunks().LastOrDefault()?.Name == before && DateTime.UtcNow < until) await Task.Delay(100);
                if(Running && Chunks().LastOrDefault()?.Name == before) throw new Exception("Capture is stalled. Stop and restart the buffer.");
            }
            var files = Chunks();
            if(Running) files = files.SkipLast(1).ToArray();
            if(!sessionPending) files = files.TakeLast((duration+1)/2).ToArray();
            byte[][]? inMemory = memory?.Snapshot((duration+1)/2);
            if((inMemory?.Length ?? files.Length) == 0) throw new Exception("No complete footage yet. Let the buffer record for a few seconds.");
            long needed = (inMemory?.Sum(b=>(long)b.Length) ?? files.Sum(f=>f.Length)) + 8L*1024*1024;
            long stored = StorageBudget.UsedBytes(Clips);
            CheckStorageQuota(stored,needed);
            if(new DriveInfo(Path.GetPathRoot(Clips)!).AvailableFreeSpace < needed + 512L*1024*1024 || new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace < needed * (inMemory != null ? 2 : 1) + 512L*1024*1024) throw new Exception("Not enough free space to save safely.");
            // Same-volume hard links freeze the snapshot without duplicating the buffer.
            var snapshots = new List<string>();
            try {
                if(inMemory != null) {
                    for(int i=0;i<inMemory.Length;i++) {
                        string name = $"save-{i:D4}.ts";
                        snapshots.Add(name);
                        await File.WriteAllBytesAsync(Path.Combine(Buffer,name),inMemory[i]);
                    }
                } else lock(gate) {
                    for(int i=0;i<files.Length;i++) {
                        string name = $"save-{i:D4}.ts";
                        if(!Native.CreateHardLink(Path.Combine(Buffer,name),files[i].FullName,IntPtr.Zero)) throw new IOException("Could not snapshot clip: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                        snapshots.Add(name);
                    }
                }
                File.WriteAllLines(Path.Combine(Buffer,"save-list.txt"),snapshots.SelectMany((n,i)=> i < snapshots.Count-1 ? new[]{ $"file '{n}'", "duration 2.0" } : new[]{ $"file '{n}'" }),new UTF8Encoding(false));
                string final = Path.Combine(Clips,$"Replay-{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.mp4");
                partial = final.Replace(".mp4",".partial.mp4");
                using var export = Launch(["-hide_banner","-loglevel","warning","-y","-f","concat","-safe","1","-i","save-list.txt","-map","0","-c","copy","-bsf:v",$"setts=pts=N/({RecordingFps}*TB):dts=N/({RecordingFps}*TB):duration=1/({RecordingFps}*TB)","-t",exportDuration.ToString(CultureInfo.InvariantCulture),"-movflags","+faststart",partial]);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(sessionPending ? 300 : 60));
                try { await export.WaitForExitAsync(timeout.Token); } catch(OperationCanceledException) { export.Kill(true); await export.WaitForExitAsync(); throw new Exception("Saving timed out. The buffer is still available."); }
                if(export.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length < 1024) throw new Exception("Could not create the clip. " + Diagnostics);
                File.Move(partial,final); partial = null; return final;
            } finally { foreach(var name in snapshots) File.Delete(Path.Combine(Buffer,name)); File.Delete(Path.Combine(Buffer,"save-list.txt")); }
        } finally { if(partial != null && File.Exists(partial)) File.Delete(partial); Saving = false; if(!sessionPending) Maintain(); }
    }
    public async Task StopAsync(bool clear)
    {
        if(process != null) {
            if(!process.HasExited) {
                try { await process.StandardInput.WriteLineAsync("q"); await process.StandardInput.FlushAsync(); } catch { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { await process.WaitForExitAsync(timeout.Token); } catch(OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); }
            }
            process.Dispose(); process = null;
        }
        audio?.Dispose(); audio = null; microphone?.Dispose(); microphone=null;
        if(memory != null) { await memory.StopAsync(); if(clear) { memory.Dispose(); memory=null; } }
        if(clear) { sessionPending=false; SessionRunning=false; foreach(var f in Directory.GetFiles(Buffer)) File.Delete(f); }
    }
    public void Dispose() { if(process is {HasExited:false}) process.Kill(true); process?.Dispose(); audio?.Dispose(); microphone?.Dispose(); memory?.Dispose(); }
}









