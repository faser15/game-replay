using System.Diagnostics;
using System.Globalization;
namespace GameReplay;

public sealed class MediaExportOptions
{
    public double StartSeconds { get; set; }
    public double? DurationSeconds { get; set; }
    public string Crop { get; set; } = "Original";
    public int Height { get; set; } = 720;
    public double Speed { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public bool Mute { get; set; }
    public bool Gif { get; set; }
    public string Title { get; set; } = "";
    public string MusicPath { get; set; } = "";
    public double MusicVolume { get; set; } = .5;
    public string WatermarkPath { get; set; } = "";
}

public static class MediaTools
{
    public static string UniquePath(string directory, string stem, string extension)
    {
        Directory.CreateDirectory(directory);
        foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        string path = Path.Combine(directory, stem + extension);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(directory, stem + "-" + i + extension);
        return path;
    }
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token, Action<string>? progress = null, string? workingDirectory = null, bool allowNonzeroExit = false)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        if (workingDirectory != null) info.WorkingDirectory = workingDirectory;
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        token.ThrowIfCancellationRequested(); process.Start(); ProcessLifetime.Attach(process);
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var tail = new Queue<string>();
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            tail.Enqueue(line); if (tail.Count > 60) tail.Dequeue();
            if (line.Contains("time=")) progress?.Invoke(line.Trim());
        }
        await process.WaitForExitAsync(); await stdout; token.ThrowIfCancellationRequested();
        string output = string.Join(Environment.NewLine, tail);
        if (process.ExitCode != 0 && !allowNonzeroExit) throw new InvalidOperationException("FFmpeg could not complete the operation:\n" + output);
        return output;
    }
    public static async Task<string> ExportAsync(string input, string ffmpeg, string directory, MediaExportOptions o, CancellationToken token, Action<string>? progress = null)
    {
        if (!double.IsFinite(o.StartSeconds) || !double.IsFinite(o.Speed) || !double.IsFinite(o.Volume) || !double.IsFinite(o.MusicVolume) || (o.DurationSeconds is { } finite && !double.IsFinite(finite)) || o.StartSeconds < 0 || o.DurationSeconds <= 0 || o.Speed < .25 || o.Speed > 4 || o.Height < 144 || o.Height > 2160 || o.Volume < 0 || o.Volume > 2 || o.MusicVolume < 0 || o.MusicVolume > 2) throw new ArgumentException("Invalid export settings.");
        var source = await ProbeAsync(input, ffmpeg, token);
        double remaining = source.Duration - o.StartSeconds;
        if (remaining <= 0) throw new ArgumentException("The start time must be before the end of the clip.");
        double outputDuration = Math.Min(o.DurationSeconds ?? remaining, remaining) / o.Speed;
        string work = Path.Combine(Path.GetTempPath(), "GameReplay-edit-" + Guid.NewGuid().ToString("N"));
        string target = UniquePath(directory, Path.GetFileNameWithoutExtension(input) + "-edited", o.Gif ? ".gif" : ".mp4");
        string temporary = Path.Combine(directory, ".export-" + Guid.NewGuid().ToString("N") + (o.Gif ? ".gif" : ".mp4"));
        await using var budget = new MediaBudgetGuard(directory, token, temporary, work);
        try
        {
            Directory.CreateDirectory(work);
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-ss", N(o.StartSeconds), "-i", input };
            int nextInput = 1, watermarkInput = -1, musicInput = -1;
            if (!string.IsNullOrWhiteSpace(o.WatermarkPath)) { watermarkInput = nextInput++; args.AddRange(new[] { "-i", o.WatermarkPath }); }
            if (!o.Gif && !string.IsNullOrWhiteSpace(o.MusicPath)) { musicInput = nextInput++; args.AddRange(new[] { "-stream_loop", "-1", "-i", o.MusicPath }); }
            var filters = new List<string>();
            if (o.DurationSeconds is { } duration) filters.Add("trim=duration=" + N(duration));
            filters.Add("setpts=(PTS-STARTPTS)/" + N(o.Speed));
            if (o.Crop == "Square") filters.Add("crop='min(iw,ih)':'min(iw,ih)'");
            else if (o.Crop == "Portrait") filters.Add("crop='min(iw,ih*9/16)':'min(ih,iw*16/9)'");
            else if (o.Crop == "Landscape") filters.Add("crop='min(iw,ih*16/9)':'min(ih,iw*9/16)'");
            filters.Add("scale=-2:" + o.Height);
            if (!string.IsNullOrWhiteSpace(o.Title))
            {
                await File.WriteAllTextAsync(Path.Combine(work, "title.txt"), o.Title, token);
                string font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
                File.Copy(font, Path.Combine(work, "caption.ttf"));
                filters.Add("drawtext=fontfile=caption.ttf:textfile=title.txt:expansion=none:fontcolor=white:fontsize=32:box=1:boxcolor=black@0.6:boxborderw=10:x=(w-text_w)/2:y=h-text_h-30");
            }
            var graph = new List<string> { "[0:v]" + string.Join(",", filters) + "[base]" };
            string videoLabel = "base";
            if (watermarkInput >= 0)
            {
                graph.Add("[" + watermarkInput + ":v]scale=" + Math.Max(32, o.Height / 5) + ":-1[watermark]");
                graph.Add("[base][watermark]overlay=W-w-12:12:eof_action=repeat[marked]"); videoLabel = "marked";
            }
            if (o.Gif)
            {
                graph.Add("[" + videoLabel + "]fps=15,split[a][b];[a]palettegen[p];[b][p]paletteuse[out]");
                args.AddRange(new[] { "-map", "[out]", "-an", "-loop", "0" });
            }
            else
            {
                args.AddRange(new[] { "-map", "[" + videoLabel + "]", "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p" });
                bool originalAudio = !o.Mute && source.HasAudio;
                if (originalAudio)
                {
                    var audio = new List<string>();
                    if (o.DurationSeconds is { } d) audio.Add("atrim=duration=" + N(d));
                    audio.Add("asetpts=PTS-STARTPTS");
                    double speed = o.Speed;
                    while (speed > 2) { audio.Add("atempo=2"); speed /= 2; }
                    while (speed < .5) { audio.Add("atempo=0.5"); speed /= .5; }
                    audio.Add("atempo=" + N(speed)); audio.Add("volume=" + N(o.Volume));
                    audio.Add("aformat=sample_rates=48000:channel_layouts=stereo"); audio.Add("apad"); audio.Add("atrim=duration=" + N(outputDuration));
                    graph.Add("[0:a:0]" + string.Join(",", audio) + "[originalAudio]");
                }
                if (musicInput >= 0) graph.Add("[" + musicInput + ":a:0]asetpts=PTS-STARTPTS,volume=" + N(o.MusicVolume) + ",aformat=sample_rates=48000:channel_layouts=stereo,apad,atrim=duration=" + N(outputDuration) + "[music]");
                string? audioLabel = originalAudio ? "originalAudio" : null;
                if (musicInput >= 0 && originalAudio) { graph.Add("[originalAudio][music]amix=inputs=2:duration=first:normalize=0,alimiter=limit=0.95[audio]"); audioLabel = "audio"; }
                else if (musicInput >= 0) audioLabel = "music";
                if (audioLabel != null) args.AddRange(new[] { "-map", "[" + audioLabel + "]", "-c:a", "aac", "-b:a", "128k" }); else args.Add("-an");
                args.AddRange(new[] { "-movflags", "+faststart" });
            }
            args.AddRange(new[] { "-filter_complex", string.Join(";", graph), "-t", N(outputDuration) });
            args.Add(temporary); await RunAsync(ffmpeg, args, budget.Token, progress, work);
            StorageBudget.RequireCapacity(directory, StorageBudget.GetLimit(directory), new FileInfo(temporary).Length);
            File.Move(temporary, target); return target;
        }
        catch (OperationCanceledException) when (budget.Failure != null) { throw new IOException(budget.Failure); }
        finally { try { File.Delete(temporary); Directory.Delete(work, true); } catch { } }
    }
    public static async Task<string> MergeAsync(IReadOnlyList<string> inputs, string ffmpeg, string directory, CancellationToken token, Action<string>? progress = null)
    {
        if (inputs.Count < 2) throw new ArgumentException("Select at least two clips.");
        string work = Path.Combine(Path.GetTempPath(), "GameReplay-merge-" + Guid.NewGuid().ToString("N"));
        string target = UniquePath(directory, "Merged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), ".mp4");
        string temp = Path.Combine(directory, ".merge-" + Guid.NewGuid().ToString("N") + ".mp4");
        await using var budget = new MediaBudgetGuard(directory, token, temp, work);
        try
        {
            Directory.CreateDirectory(work);
            for (int i = 0; i < inputs.Count; i++)
            {
                var source = await ProbeAsync(inputs[i], ffmpeg, budget.Token);
                var normalize = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", inputs[i] };
                if (!source.HasAudio) normalize.AddRange(new[] { "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo" });
                normalize.AddRange(new[] { "-map", "0:v:0", "-map", source.HasAudio ? "0:a:0" : "1:a:0", "-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,setpts=PTS-STARTPTS", "-af", "aresample=48000,aformat=channel_layouts=stereo,asetpts=PTS-STARTPTS,apad", "-t", N(source.Duration), "-shortest", "-c:a", "aac", "-b:a", "128k", "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", Path.Combine(work, i + ".mp4") });
                await RunAsync(ffmpeg, normalize, budget.Token, progress);
            }
            await File.WriteAllLinesAsync(Path.Combine(work, "list.txt"), Enumerable.Range(0, inputs.Count).Select(i => "file '" + i + ".mp4'"), token);
            await RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-y", "-f", "concat", "-safe", "1", "-i", Path.Combine(work, "list.txt"), "-c", "copy", "-movflags", "+faststart", temp }, budget.Token, progress);
            StorageBudget.RequireCapacity(directory, StorageBudget.GetLimit(directory), new FileInfo(temp).Length);
            File.Move(temp, target); return target;
        }
        catch (OperationCanceledException) when (budget.Failure != null) { throw new IOException(budget.Failure); }
        finally { try { File.Delete(temp); Directory.Delete(work, true); } catch { } }
    }
    private static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    public static async Task<(bool HasAudio, double Duration)> ProbeAsync(string input, string ffmpeg, CancellationToken token)
    {
        string output = await RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-i", input }, token, allowNonzeroExit: true);
        var m = System.Text.RegularExpressions.Regex.Match(output, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!m.Success) throw new InvalidOperationException("Could not determine clip duration. Use a finalized local video file.");
        double seconds = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return (output.Contains("Audio:"), seconds);
    }
    public static async Task ImportAsync(string source, string destination, CancellationToken token)
    {
        string directory = Path.GetDirectoryName(destination)!;
        StorageBudget.RequireCapacity(directory, StorageBudget.GetLimit(directory), new FileInfo(source).Length);
        string partial = Path.Combine(directory, ".import-" + Guid.NewGuid().ToString("N"));
        await using var budget = new MediaBudgetGuard(directory, token, partial);
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, true))
                await input.CopyToAsync(output, budget.Token);
            StorageBudget.RequireCapacity(directory, StorageBudget.GetLimit(directory), new FileInfo(partial).Length);
            File.Move(partial, destination);
        }
        catch (OperationCanceledException) when (budget.Failure != null) { throw new IOException(budget.Failure); }
        finally { try { File.Delete(partial); } catch { } }
    }
}

// A sampled guard, not an instantaneous disk quota. Final publication also checks exact size.
internal sealed class MediaBudgetGuard : IAsyncDisposable
{
    readonly CancellationTokenSource processCancellation, stop = new();
    readonly Task watcher;
    public CancellationToken Token => processCancellation.Token;
    public string? Failure { get; private set; }
    public MediaBudgetGuard(string directory, CancellationToken token, params string[] temporaryPaths)
    {
        StorageBudget.RequireCapacity(directory, StorageBudget.GetLimit(directory), 1024 * 1024);
        var drives = temporaryPaths.Append(directory).Select(Path.GetPathRoot).Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => new DriveInfo(p!)).ToArray();
        if (drives.Any(d => d.AvailableFreeSpace < 64L * 1024 * 1024)) throw new IOException("At least 64 MB of free disk space is required to start this operation.");
        processCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        watcher = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            try
            {
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    long temporaryBytes = 0;
                    foreach (string path in temporaryPaths)
                        if (File.Exists(path)) temporaryBytes += new FileInfo(path).Length;
                        else if (Directory.Exists(path)) temporaryBytes += new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    if (StorageBudget.UsedBytes(directory) + temporaryBytes > StorageBudget.GetLimit(directory)) Failure = "Storage limit reached. The unfinished output was removed; delete or move clips before retrying.";
                    else if (drives.Any(d => d.AvailableFreeSpace < 64L * 1024 * 1024)) Failure = "Disk space is low. The unfinished output was removed.";
                    if (Failure != null) { processCancellation.Cancel(); break; }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { Failure = "Could not verify remaining storage: " + ex.Message; processCancellation.Cancel(); }
        });
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); await watcher; processCancellation.Dispose(); stop.Dispose();
    }
}
