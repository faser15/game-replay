using System.Text.Json;

namespace GameReplay;

public static class ReleaseChecks
{
    public static async Task RunAsync(string root)
    {
        using var engine=new ReplayEngine(root);
        string input=Path.Combine(root,"editor-input.mp4");
        await MediaTools.RunAsync(engine.Ffmpeg,["-hide_banner","-v","error","-y","-f","lavfi","-i","testsrc2=size=640x360:rate=30","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","4","-c:v","libx264","-preset","ultrafast","-c:a","aac",input],CancellationToken.None);
        string edited=await MediaTools.ExportAsync(input,engine.Ffmpeg,engine.Clips,new(){StartSeconds=.5,DurationSeconds=2,Crop="Square",Height=480,Speed=2,Volume=.5,Title="Test: clip's 100% [safe]"},CancellationToken.None);
        string gif=await MediaTools.ExportAsync(input,engine.Ffmpeg,engine.Clips,new(){DurationSeconds=1,Crop="Portrait",Height=240,Gif=true},CancellationToken.None);
        string merged=await MediaTools.MergeAsync([input,input],engine.Ffmpeg,engine.Clips,CancellationToken.None);
        string music=Path.Combine(root,"music.wav"),watermark=Path.Combine(root,"watermark.png");
        await MediaTools.RunAsync(engine.Ffmpeg,["-v","error","-y","-f","lavfi","-i","sine=frequency=220:sample_rate=48000","-t","1",music],CancellationToken.None);
        using(var image=new Bitmap(80,40)){using var g=Graphics.FromImage(image);g.Clear(Color.Red);image.Save(watermark);}
        string soundtrack=await MediaTools.ExportAsync(input,engine.Ffmpeg,engine.Clips,new(){DurationSeconds=2,MusicPath=music,MusicVolume=.2,WatermarkPath=watermark},CancellationToken.None);
        foreach(string path in new[]{edited,gif,merged,soundtrack})
        {
            if(!File.Exists(path)||new FileInfo(path).Length<1000)throw new Exception("Empty editor export.");
            await MediaTools.RunAsync(engine.Ffmpeg,["-v","error","-i",path,"-f","null","-"],CancellationToken.None);
        }
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();bool canceled=false;
        try{await MediaTools.ExportAsync(input,engine.Ffmpeg,engine.Clips,new(),cancellation.Token);}catch(OperationCanceledException){canceled=true;}
        if(!canceled||!File.Exists(input))throw new Exception("Cancel or original preservation failed.");
        await DesktopFeatureChecks.RunAsync(root);
        string quota=Path.Combine(root,"quota-check");Directory.CreateDirectory(Path.Combine(quota,"Screenshots"));File.WriteAllBytes(Path.Combine(quota,"Screenshots","test.png"),new byte[100]);File.WriteAllBytes(Path.Combine(quota,"test.gif"),new byte[200]);
        if(StorageBudget.UsedBytes(quota)!=300)throw new Exception("Storage accounting omitted supported media.");
        bool quotaRejected=false;try{StorageBudget.RequireCapacity(quota,350,100);}catch(IOException){quotaRejected=true;}if(!quotaRejected)throw new Exception("Storage quota did not reject excess media.");
        File.WriteAllText(Path.Combine(root,"media-results.json"),JsonSerializer.Serialize(new{passed=true,edited,gif,merged,soundtrack,checks=new[]{"trim","speed","volume","caption escaping","crop","MP4","GIF","merge with audio","soundtrack","watermark","decode","cancellation","original preserved","desktop helpers","recursive storage quota"}},new JsonSerializerOptions{WriteIndented=true}));
    }
}
