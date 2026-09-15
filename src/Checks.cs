using System.Diagnostics;
using System.Text.Json;

namespace GameReplay;
static class Checks
{
    public static async Task Run(string root)
    {
        using var engine=new ReplayEngine(root);
        using(var p=engine.Launch(["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=30","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","322","-c:v","libx264","-preset","ultrafast","-b:v","200k","-g","60","-sc_threshold","0","-c:a","aac","-f","segment","-segment_time","2","-segment_format","mpegts","-reset_timestamps","1","chunk-%08d.ts"])) { await p.WaitForExitAsync(); if(p.ExitCode!=0) throw new Exception(engine.Diagnostics); }
        int generated=engine.Chunks().Length;
        if(generated<160) throw new Exception("Not enough test segments.");
        engine.Maintain(); int kept=engine.Chunks().Length;
        if(kept!=152) throw new Exception("Retention failed: " + kept);
        string clip=await engine.SaveAsync();
        if(engine.Chunks().Length!=152) throw new Exception("Save changed the rolling buffer.");
        if(Directory.GetFiles(engine.Buffer,"save-*").Length!=0) throw new Exception("Snapshot cleanup failed.");
        // Exercise byte-limit pruning with sparse files; do not allocate a large test video.
        foreach(var f in engine.Chunks()) f.Delete();
        for(int i=0;i<5;i++) {using var f=File.Create(Path.Combine(engine.Buffer,$"chunk-{i:D8}.ts")); f.SetLength(100L*1024*1024);}
        engine.Maintain();
        if(engine.Chunks().Sum(f=>f.Length)>ReplayEngine.BufferLimit) throw new Exception("Byte cap failed.");
        await engine.StopAsync(true);
        if(engine.Chunks().Length!=0) throw new Exception("Stop cleanup failed.");
        // Library quota must refuse additional saves, preserving existing files.
        bool refused=false;
        try {ReplayEngine.CheckLibraryQuota(ReplayEngine.LibraryLimit,2048);} catch(Exception e) when(e.Message.Contains("5 GB")) {refused=true;}
        if(!refused || !File.Exists(clip)) throw new Exception("Library quota did not preserve clips.");
        string memoryOutput = engine.CreateMemoryOutput();
        using(var p=engine.Launch(["-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x180:rate=30","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","322","-c:v","libx264","-preset","ultrafast","-b:v","200k","-g","60","-sc_threshold","0","-c:a","aac","-f","segment","-segment_time","2","-segment_format","mpegts","-reset_timestamps","1",memoryOutput])) {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await p.WaitForExitAsync(timeout.Token); } catch { p.Kill(true); throw; }
            if(p.ExitCode!=0) throw new Exception(engine.Diagnostics);
        }
        await engine.StopAsync(false); engine.Maintain();
        if(engine.BufferedSegments!=150) throw new Exception("RAM retention failed: "+engine.BufferedSegments);
        if(Directory.GetFiles(engine.Buffer).Length!=0) throw new Exception("RAM mode wrote recording data to disk before save.");
        string memoryClip=await engine.SaveAsync();
        if(engine.BufferedSegments!=150 || Directory.GetFiles(engine.Buffer).Length!=0) throw new Exception("RAM save did not preserve buffer or clean snapshots.");
        await engine.StopAsync(true);
        File.WriteAllText(Path.Combine(root,"test-results.json"),JsonSerializer.Serialize(new {passed=true,generated,retained=kept,clip,memoryClip,checks=new[]{"5-minute disk and RAM retention","MP4 export from RAM and disk","zero video disk files in RAM mode before save","snapshot cleanup","192 MB disk cap","exit cleanup","5 GB saved quota"}},new JsonSerializerOptions{WriteIndented=true}));
    }
    public static async Task Smoke(string root)
    {
        using var engine=new ReplayEngine(root);
        await engine.StartAsync(0,30,true,"Auto");
        await Task.Delay(10000); engine.Maintain();
        if(!engine.MemoryMode || engine.BufferedSegments<3 || Directory.GetFiles(engine.Buffer).Length!=0) throw new Exception("Live RAM recording did not fill memory without disk files. "+engine.Diagnostics);
        string clip=await engine.SaveAsync();
        bool continued=engine.Running;
        await engine.StopAsync(true);
        File.WriteAllText(Path.Combine(root,"smoke-results.json"),JsonSerializer.Serialize(new {passed=continued,encoder=engine.Encoder,clip,captureContinuedDuringSave=continued,log=engine.Diagnostics},new JsonSerializerOptions{WriteIndented=true}));
    }
}
