using System.Text.Json;

namespace GameReplay;

public static class StorageBudget
{
    static readonly HashSet<string> MediaExtensions=new(StringComparer.OrdinalIgnoreCase){".mp4",".mkv",".mov",".webm",".avi",".gif",".png",".jpg",".jpeg",".wav",".mp3"};
    public static long UsedBytes(string directory)
    {
        if(!Directory.Exists(directory))return 0;
        long size=0;
        foreach(string path in Directory.EnumerateFiles(directory,"*",new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=false,AttributesToSkip=FileAttributes.ReparsePoint}))
        {
            string name=Path.GetFileName(path);
            if(name.StartsWith('.')||name.EndsWith(".partial.mp4",StringComparison.OrdinalIgnoreCase)||!MediaExtensions.Contains(Path.GetExtension(path)))continue;
            try{size+=new FileInfo(path).Length;}catch(FileNotFoundException){}
        }
        return size;
    }
    public static void RequireCapacity(string directory,long limit,long needed)
    {
        if(needed<0||limit<=0)throw new ArgumentOutOfRangeException(nameof(needed));
        if(UsedBytes(directory)>limit-needed)throw new IOException("The clip library storage limit would be exceeded. Move or recycle clips, or increase the limit in Settings.");
        string full=Path.GetFullPath(directory);
        if(new DriveInfo(Path.GetPathRoot(full)!).AvailableFreeSpace<needed+256L*1024*1024)throw new IOException("Not enough free disk space to save safely.");
    }
    public static long GetLimit(string directory)
    {
        string path=Path.Combine(directory,".gamereplay-storage.json");
        if(!File.Exists(path))return 5L*1024*1024*1024;
        using var doc=JsonDocument.Parse(File.ReadAllText(path));
        return Math.Clamp(doc.RootElement.GetProperty("LimitBytes").GetInt64(),512L*1024*1024,1024L*1024*1024*1024);
    }
    public static void WriteLimit(string directory,long limit)
    {
        Directory.CreateDirectory(directory);string path=Path.Combine(directory,".gamereplay-storage.json");
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new{LimitBytes=limit}));File.Move(path+".tmp",path,true);
    }
}
