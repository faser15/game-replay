using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GameReplay;

/// <summary>Real Windows Graphics Capture test against an animated window owned by this process.</summary>
public static class CaptureChecks
{
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr hwnd);
    public static void Run(string root)
    {
        Directory.CreateDirectory(root);
        Exception? failure=null;
        using var form=new Form { Text="GameReplay automated capture test — synthetic content", ClientSize=new Size(960,540), StartPosition=FormStartPosition.CenterScreen, BackColor=Color.Navy };
        using var timer=new System.Windows.Forms.Timer { Interval=33 };
        int frame=0;
        var firstPaint=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        form.Paint+=(_,e)=> {
            firstPaint.TrySetResult();
            e.Graphics.Clear(Color.FromArgb(16,24,48));
            using var brush=new SolidBrush(Color.FromArgb(40+(frame%160),180,230));
            e.Graphics.FillRectangle(brush,(frame*9)%800,180,120,120);
            e.Graphics.DrawString("SYNTHETIC WINDOW CAPTURE TEST\n"+frame+" frames",SystemFonts.DefaultFont,Brushes.White,30,30);
        };
        timer.Tick+=(_,_)=> { frame++; form.Invalidate(); };
        form.Shown+=async (_,_)=> {
            timer.Start();
            try {
                // The runner starts hidden. Explicitly reveal only this synthetic window
                // without activating it before passing its HWND to Graphics Capture.
                ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
                form.Invalidate(); form.Update();
                await firstPaint.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(750);
                if(!IsWindowVisible(form.Handle)) throw new Exception("Synthetic capture window remained hidden after SW_SHOWNOACTIVATE.");
                await RunAsync(root,form.Handle.ToInt64());
            }
            catch(Exception error) { failure=error; File.WriteAllText(Path.Combine(root,"capture-results.json"),JsonSerializer.Serialize(new {passed=false,error=error.ToString()},new JsonSerializerOptions{WriteIndented=true})); }
            finally { timer.Stop(); form.Close(); }
        };
        Application.Run(form);
        if(failure!=null) throw new InvalidOperationException("Capture integration test failed; see capture-results.json.",failure);
    }
    static async Task RunAsync(string root,long hwnd)
    {
        using var engine=new ReplayEngine(root);
        var options=new RecorderOptions { WindowHandle=hwnd, Width=1280,Height=720,Fps=30,ClipSeconds=15,MemoryBuffer=true,SystemAudio=false,Microphone=false };
        await engine.StartAsync(options);
        if(engine.CaptureBackend!="Windows Graphics Capture" || !engine.MemoryMode) throw new Exception("Selected-window RAM capture was not used.");
        await WaitCaptureAsync(engine,18000);
        if(engine.BufferedSegments!=8 || Directory.GetFiles(engine.Buffer).Length!=0) throw new Exception("15-second RAM retention or no-disk-write contract failed: "+engine.BufferedSegments);
        string replay=await engine.SaveAsync(15);
        if(!engine.Running || !engine.MemoryMode || Directory.GetFiles(engine.Buffer).Length!=0) throw new Exception("Save interrupted recording or left temporary files.");
        var replayInfo=await VerifyClipAsync(engine,replay);
        if(replayInfo.Duration<15.8 || replayInfo.Duration>16.3) throw new Exception("15-second replay must retain its newest complete 2-second segment: "+replayInfo.Duration);
        await engine.StopAsync(true);
        await engine.StartSessionAsync(options);
        await WaitCaptureAsync(engine,19000);
        if(engine.MemoryMode || !engine.SessionRunning || engine.BufferedSegments<=8) throw new Exception("Full session was pruned to replay duration.");
        string session=await engine.StopSessionAsync();
        if(engine.Running || engine.SessionRunning || Directory.GetFiles(engine.Buffer).Length!=0) throw new Exception("Full-session completion did not clean recording state.");
        var sessionInfo=await VerifyClipAsync(engine,session);
        if(sessionInfo.Duration<18) throw new Exception("Full-session export lost footage beyond the replay limit.");
        File.WriteAllText(Path.Combine(root,"capture-results.json"),JsonSerializer.Serialize(new {
            passed=true, captureBackend=engine.CaptureBackend,encoder=engine.Encoder,replay,session,replayInfo,sessionInfo,
            checks=new[]{"Explicit owned-window GPU capture at 1280x720", "15-second RAM replay retains 16 seconds of complete GOPs", "RAM has zero unsaved video disk writes", "Saving continues capture", "Full session retains beyond replay duration", "All exported video decodes", "System audio and microphone remain disabled"}
        },new JsonSerializerOptions{WriteIndented=true}));
    }
    static async Task WaitCaptureAsync(ReplayEngine engine,int milliseconds)
    {
        for(int elapsed=0;elapsed<milliseconds;elapsed+=500) {
            await Task.Delay(500); engine.Maintain();
            if(!engine.Running) throw new Exception("Capture stopped unexpectedly. "+engine.Diagnostics);
        }
    }
    public sealed record ClipInfo(int Width,int Height,double Duration,int AudioStreams);
    static async Task<ClipInfo> VerifyClipAsync(ReplayEngine engine,string path)
    {
        string probe=Path.Combine(Path.GetDirectoryName(engine.Ffmpeg)!,"ffprobe.exe");
        var start=new ProcessStartInfo(probe) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        foreach(string arg in new[]{"-v","error","-show_streams","-show_format","-of","json",path}) start.ArgumentList.Add(arg);
        using var process=Process.Start(start) ?? throw new IOException("Could not launch ffprobe.");
        ProcessLifetime.Attach(process);
        Task<string> stdout=process.StandardOutput.ReadToEndAsync(),stderr=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
        string json=await stdout,errors=await stderr;
        if(process.ExitCode!=0) throw new IOException("ffprobe failed: "+errors);
        using var data=JsonDocument.Parse(json);
        var streams=data.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video=streams.Single(s=>s.GetProperty("codec_type").GetString()=="video");
        var info=new ClipInfo(video.GetProperty("width").GetInt32(),video.GetProperty("height").GetInt32(),double.Parse(data.RootElement.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture),streams.Count(s=>s.GetProperty("codec_type").GetString()=="audio"));
        if(info.Width!=1280 || info.Height!=720 || info.AudioStreams!=0) throw new Exception("Incorrect dimensions or unexpected audio capture.");
        using var decode=engine.Launch(["-hide_banner","-loglevel","error","-xerror","-i",path,"-map","0:v:0","-f","null","-"]);
        using var decodeTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await decode.WaitForExitAsync(decodeTimeout.Token); } catch { decode.Kill(true); throw; }
        if(decode.ExitCode!=0) throw new IOException("Exported video failed full decode: "+engine.Diagnostics);
        return info;
    }
}

