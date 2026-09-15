namespace GameReplay;

public static class DesktopFeatureChecks
{
    public static Task RunAsync(string root)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Desktop feature check failed: " + message); }
        Require(HotkeyBinding.TryParse("Ctrl+Shift+F8", out var hotkey) && hotkey.Modifiers == 6 && hotkey.Key == Keys.F8, "default hotkey parsing");
        Require(HotkeyBinding.TryParse(hotkey.ToString(), out var roundtrip) && hotkey == roundtrip, "hotkey display roundtrip");
        foreach (var invalid in new[] { "", "Ctrl+Shift", "Ctrl+F8+F9", "Ctrl+NotAKey", "Ctrl+ControlKey", "Ctrl++F8" })
            Require(!HotkeyBinding.TryParse(invalid, out _), "invalid hotkey accepted: " + invalid);
        var directory = Path.Combine(root, "desktop-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var profiles = new GameProfiles(directory);
        Require(!new GameProfile().AutoRecord && !new GameProfile().FullSession, "new profiles default to manual replay mode");
        profiles.Upsert(new GameProfile { Executable = @"C:\Games\game.client.exe", Name = "Test game", FullSession = true, OptionsJson = "{\"Fps\":30}" });
        var loaded = new GameProfiles(directory);
        Require(loaded.Profiles.Count == 1 && loaded.Profiles[0].Executable == "game.client" && loaded.Profiles[0].OptionsJson == "{\"Fps\":30}", "profile persistence");
        Require(loaded.Profiles[0].FullSession, "full session profile persistence");
        loaded.Upsert(new GameProfile { Executable = "GAME.CLIENT.EXE", Name = "Updated", AutoRecord = false });
        Require(loaded.Profiles.Count == 1 && loaded.Profiles[0].Name == "Updated", "profile case-insensitive replacement");
        loaded.Remove("game.client");
        Require(new GameProfiles(directory).Profiles.Count == 0, "profile deletion persistence");
        var bookmarks = new BookmarkStore(Path.Combine(directory, "session.mp4"));
        bookmarks.Add(TimeSpan.FromSeconds(12.5), "First highlight");
        var readback = new BookmarkStore(Path.Combine(directory, "session.mp4"));
        Require(readback.Entries.Count == 1 && readback.Entries[0].Seconds == 12.5 && readback.Entries[0].Label == "First highlight", "bookmark timestamp and label persistence");
        bool rejected = false;
        try { bookmarks.Add(TimeSpan.FromSeconds(-1)); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Require(rejected && bookmarks.Entries.Count == 1, "negative bookmark timestamp rejected");
        File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: hotkeys, profiles, bookmark persistence. Screen capture and microphone were not invoked.");
        return Task.CompletedTask;
    }
}
