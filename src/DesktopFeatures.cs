using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GameReplay;

public sealed record CaptureWindow(long Handle, string Title, string ProcessName)
{
    public override string ToString() => $"{Title} ({ProcessName})";
}

public static class DesktopFeatures
{
    public static List<CaptureWindow> GetWindows()
    {
        var windows = new List<CaptureWindow>();
        EnumWindows((hwnd, _) => {
            if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return true;
            var title = new StringBuilder(1024); GetWindowText(hwnd, title, title.Capacity);
            try { using var process = Process.GetProcessById((int)pid); windows.Add(new(hwnd.ToInt64(), title.ToString(), process.ProcessName)); }
            catch (ArgumentException) { } catch (System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        return windows.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Captures the pixels presently visible on the screen. Occluding windows appear in a window screenshot.
    public static string SaveScreenshot(string path, int displayIndex = 0, long windowHandle = 0)
    {
        Rectangle region;
        if (windowHandle != 0)
        {
            var hwnd = new IntPtr(windowHandle);
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || !GetWindowRect(hwnd, out var rect))
                throw new InvalidOperationException("The selected window is closed, hidden, or minimized. Restore it before taking a screenshot.");
            region = Rectangle.Intersect(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom), SystemInformation.VirtualScreen);
        }
        else
        {
            var screens = Screen.AllScreens;
            if (displayIndex < 0 || displayIndex >= screens.Length) throw new ArgumentOutOfRangeException(nameof(displayIndex));
            region = screens[displayIndex].Bounds;
        }
        if (region.Width <= 0 || region.Height <= 0) throw new InvalidOperationException("The selected window is outside the visible desktop.");
        using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(region.Location, Point.Empty, region.Size, CopyPixelOperation.SourceCopy);
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Refuse overwrite so a repeated hotkey cannot destroy an earlier screenshot.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        bitmap.Save(stream, ImageFormat.Png);
        return path;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
}

public readonly record struct HotkeyBinding(uint Modifiers, Keys Key)
{
    public bool Register(IntPtr window, int id) => RegisterHotKey(window, id, Modifiers | 0x4000, (uint)Key);
    public static bool Unregister(IntPtr window, int id) => UnregisterHotKey(window, id);
    public override string ToString() => ((Modifiers & 2) != 0 ? "Ctrl+" : "") + ((Modifiers & 1) != 0 ? "Alt+" : "") + ((Modifiers & 4) != 0 ? "Shift+" : "") + ((Modifiers & 8) != 0 ? "Win+" : "") + Key;
    public static bool TryParse(string? value, out HotkeyBinding binding)
    {
        binding = default; if (string.IsNullOrWhiteSpace(value)) return false;
        uint modifiers = 0; Keys key = Keys.None;
        foreach (var part in value.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": modifiers |= 2; break;
                case "ALT": modifiers |= 1; break;
                case "SHIFT": modifiers |= 4; break;
                case "WIN": modifiers |= 8; break;
                default:
                    if (key != Keys.None || !Enum.TryParse(part, true, out key) || !Enum.IsDefined(key) || key == Keys.None || (uint)key > 255 || key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return false;
                    break;
            }
        }
        if (key == Keys.None) return false;
        binding = new(modifiers, key); return true;
    }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr window, int id);
}

public sealed record SessionBookmark(double Seconds, string Label, DateTimeOffset CreatedUtc);
public sealed class BookmarkStore
{
    readonly string path;
    readonly List<SessionBookmark> entries = new();
    public IReadOnlyList<SessionBookmark> Entries => entries.AsReadOnly();
    public BookmarkStore(string sessionVideoPath)
    {
        path = sessionVideoPath + ".bookmarks.json";
        if (File.Exists(path)) entries.AddRange(JsonSerializer.Deserialize<List<SessionBookmark>>(File.ReadAllText(path)) ?? new());
    }
    public SessionBookmark Add(TimeSpan elapsed, string label = "Highlight")
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        var entry = new SessionBookmark(elapsed.TotalSeconds, label, DateTimeOffset.UtcNow);
        var updated = entries.Append(entry).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true); entries.Add(entry); return entry;
    }
}
