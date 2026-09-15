using System.Diagnostics;
using System.Text.Json;

namespace GameReplay;

public sealed class GameProfile
{
    public string Executable { get; set; } = "";
    public string Name { get; set; } = "";
    public bool AutoRecord { get; set; }
    public bool FullSession { get; set; }
    // Serialized RecorderOptions; detached payload keeps profile management independent of the recorder.
    public string OptionsJson { get; set; } = "{}";
}

public sealed class GameProfiles
{
    readonly string path;
    readonly List<GameProfile> profiles;
    public IReadOnlyList<GameProfile> Profiles => profiles.AsReadOnly();
    public GameProfiles(string dataDirectory)
    {
        path = Path.Combine(dataDirectory, "game-profiles.json");
        profiles = File.Exists(path) ? JsonSerializer.Deserialize<List<GameProfile>>(File.ReadAllText(path)) ?? new() : new();
    }
    public void Upsert(GameProfile profile)
    {
        string executable = NormalizeExecutable(profile.Executable);
        if (executable.Length == 0 || executable.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Choose a valid game executable.");
        using var document = JsonDocument.Parse(profile.OptionsJson);
        var saved = new GameProfile { Executable = executable, Name = string.IsNullOrWhiteSpace(profile.Name) ? executable : profile.Name.Trim(), AutoRecord = profile.AutoRecord, FullSession = profile.FullSession, OptionsJson = profile.OptionsJson };
        var next = profiles.Where(p => !string.Equals(NormalizeExecutable(p.Executable), executable, StringComparison.OrdinalIgnoreCase)).Append(saved).ToList();
        Save(next); profiles.Clear(); profiles.AddRange(next);
    }
    public void Remove(string executable)
    {
        var next = profiles.Where(p => !string.Equals(NormalizeExecutable(p.Executable), NormalizeExecutable(executable), StringComparison.OrdinalIgnoreCase)).ToList();
        Save(next); profiles.Clear(); profiles.AddRange(next);
    }
    public List<GameProfile> GetRunningProfiles()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
            using (process) { try { names.Add(process.ProcessName); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
        return profiles.Where(p => names.Contains(NormalizeExecutable(p.Executable))).ToList();
    }
    public GameProfile? DetectAutoRecordGame() => GetRunningProfiles().FirstOrDefault(p => p.AutoRecord);
    public static string NormalizeExecutable(string executable)
    {
        var name = Path.GetFileName(executable.Trim());
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
    void Save(List<GameProfile> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
