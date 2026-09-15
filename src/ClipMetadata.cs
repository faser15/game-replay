using System.Text.Json;
namespace GameReplay;
public sealed class ClipMetadata
{
    public bool Favorite { get; set; }
    public string Tags { get; set; } = "";
    public string Album { get; set; } = "";
}
internal sealed class ClipMetadataStore
{
    readonly string path;
    internal Dictionary<string, ClipMetadata> Items { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    internal ClipMetadataStore(string directory)
    {
        path = Path.Combine(directory, ".gamereplay-library.json");
        if (File.Exists(path))
        {
            // Preserve corrupt metadata for recovery rather than silently overwriting it.
            try { Items = new Dictionary<string, ClipMetadata>(JsonSerializer.Deserialize<Dictionary<string, ClipMetadata>>(File.ReadAllText(path)) ?? new(), StringComparer.OrdinalIgnoreCase); }
            catch { throw new InvalidOperationException("The library metadata could not be read. Back up and repair .gamereplay-library.json before opening this library."); }
        }
    }
    internal ClipMetadata Get(string name) { if (!Items.TryGetValue(name, out var value)) Items[name] = value = new(); return value; }
    internal void Save()
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
