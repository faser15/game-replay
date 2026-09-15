using System.Diagnostics;
using Microsoft.VisualBasic.FileIO;
namespace GameReplay;

public sealed class ClipLibraryForm : Form
{
    readonly string directory, ffmpeg;
    readonly ClipMetadataStore metadata;
    readonly TextBox search = new() { Width = 250, PlaceholderText = "Search name, tags, album" };
    readonly ComboBox sort = new() { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox favorites = new() { Text = "Favorites only", AutoSize = true };
    readonly ListView clips = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false };
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 45, Text = "Select a clip to play or edit. Merge order follows the list." };
    readonly FlowLayoutPanel actions = new() { Dock = DockStyle.Bottom, Height = 82, Padding = new Padding(6) };
    readonly Button cancel = new() { Text = "Cancel merge", Enabled = false, AutoSize = true };
    CancellationTokenSource? cancellation;
    bool importing;
    public ClipLibraryForm(string clipsPath, string ffmpegPath)
    {
        directory = clipsPath; ffmpeg = ffmpegPath; Directory.CreateDirectory(directory); metadata = new(directory);
        Text = "GameReplay • Clip library"; Width = 1000; Height = 670; MinimumSize = new Size(760, 500); StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(24, 27, 34); ForeColor = Color.WhiteSmoke; Font = new Font("Segoe UI", 10);
        clips.BackColor = Color.FromArgb(31, 35, 44); clips.ForeColor = ForeColor;
        clips.Columns.Add("Clip", 350); clips.Columns.Add("Created", 165); clips.Columns.Add("Size", 95); clips.Columns.Add("Favorite", 75); clips.Columns.Add("Tags", 145); clips.Columns.Add("Album", 120);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8) };
        sort.Items.AddRange(new[] { "Newest first", "Oldest first", "Name", "Largest first" }); sort.SelectedIndex = 0;
        top.Controls.AddRange(new Control[] { search, sort, favorites });
        Controls.Add(clips); Controls.Add(actions); Controls.Add(status); Controls.Add(top);
        Button("Play", Play); Button("Edit", () => { if (SelectedOne() is { } path) { using var form = new EditorForm(path, ffmpeg, directory); form.ShowDialog(this); RefreshClips(); } });
        Button("Favorite", () => ChangeMetadata(m => m.Favorite = !m.Favorite));
        Button("Tags", () => SetTextMetadata(false)); Button("Album", () => SetTextMetadata(true));
        Button("Rename", Rename); Button("Import", Import); Button("Recycle", Recycle);
        Button("Open folder", () => Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }));
        Button("Refresh", RefreshClips);
        var merge = new Button { Text = "Merge clips", AutoSize = true }; merge.Click += async (_, _) => await Merge(); actions.Controls.Add(merge); actions.Controls.Add(cancel);
        cancel.Click += (_, _) => cancellation?.Cancel();
        search.TextChanged += (_, _) => RefreshClips(); sort.SelectedIndexChanged += (_, _) => RefreshClips(); favorites.CheckedChanged += (_, _) => RefreshClips(); clips.DoubleClick += (_, _) => Safe(Play);
        FormClosing += (_, e) => { if (importing) { e.Cancel = true; status.Text = "Wait for the current import to finish before closing."; } else if (cancellation != null) { cancellation.Cancel(); e.Cancel = true; status.Text = "Cancelling merge; close once it stops."; } };
        RefreshClips();
    }
    void Button(string text, Action action) { var b = new Button { Text = text, AutoSize = true }; b.Click += (_, _) => Safe(action); actions.Controls.Add(b); }
    void Safe(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Clip library"); } }
    string[] Selected() => clips.SelectedItems.Cast<ListViewItem>().Select(x => (string)x.Tag!).ToArray();
    string? SelectedOne() { var paths = Selected(); if (paths.Length != 1) { status.Text = "Select exactly one clip."; return null; } return paths[0]; }
    void Play() { if (SelectedOne() is { } p) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); }
    void RefreshClips()
    {
        var selected = Selected().ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<FileInfo> files = new DirectoryInfo(directory).EnumerateFiles().Where(f => !f.Name.StartsWith('.') && new[] { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".gif" }.Contains(f.Extension.ToLowerInvariant()));
        files = files.Where(f => { var m = metadata.Get(f.Name); return (!favorites.Checked || m.Favorite) && (f.Name + " " + m.Tags + " " + m.Album).Contains(search.Text, StringComparison.OrdinalIgnoreCase); });
        files = sort.SelectedIndex switch { 1 => files.OrderBy(f => f.CreationTimeUtc), 2 => files.OrderBy(f => f.Name), 3 => files.OrderByDescending(f => f.Length), _ => files.OrderByDescending(f => f.CreationTimeUtc) };
        clips.BeginUpdate(); clips.Items.Clear();
        foreach (var f in files)
        {
            var m = metadata.Get(f.Name); var item = new ListViewItem(new[] { f.Name, f.CreationTime.ToString("g"), (f.Length / 1048576d).ToString("0.0") + " MB", m.Favorite ? "★" : "", m.Tags, m.Album }) { Tag = f.FullName, Selected = selected.Contains(f.FullName) }; clips.Items.Add(item);
        }
        clips.EndUpdate();
    }
    void ChangeMetadata(Action<ClipMetadata> change) { foreach (var path in Selected()) change(metadata.Get(Path.GetFileName(path))); metadata.Save(); RefreshClips(); }
    void SetTextMetadata(bool album)
    {
        if (SelectedOne() is not { } path) return;
        var m = metadata.Get(Path.GetFileName(path)); var text = Prompt(album ? "Album" : "Tags (comma separated)", album ? m.Album : m.Tags);
        if (text == null) return; if (album) m.Album = text; else m.Tags = text; metadata.Save(); RefreshClips();
    }
    void Rename()
    {
        if (SelectedOne() is not { } path) return;
        string? stem = Prompt("New clip name (without extension)", Path.GetFileNameWithoutExtension(path));
        if (stem == null) return;
        if (string.IsNullOrWhiteSpace(stem) || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || stem.EndsWith('.') || stem.EndsWith(' ')) throw new ArgumentException("Enter a valid file name.");
        string destination = Path.Combine(directory, stem + Path.GetExtension(path)); if (destination.Equals(path, StringComparison.OrdinalIgnoreCase)) return;
        File.Move(path, destination); metadata.Items[Path.GetFileName(destination)] = metadata.Get(Path.GetFileName(path)); metadata.Items.Remove(Path.GetFileName(path)); metadata.Save(); RefreshClips();
    }
    async void Import()
    {
        using var dialog = new OpenFileDialog { Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.gif", Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            importing = true; actions.Enabled = false; status.Text = "Importing…";
            long importBytes = dialog.FileNames.Sum(path => new FileInfo(path).Length);
            StorageBudget.RequireCapacity(directory, StorageBudget.GetLimit(directory), importBytes);
            foreach (string source in dialog.FileNames)
            {
                string destination = MediaTools.UniquePath(directory, Path.GetFileNameWithoutExtension(source), Path.GetExtension(source));
                await MediaTools.ImportAsync(source, destination, CancellationToken.None);
            }
            status.Text = "Import complete."; RefreshClips();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import failed"); }
        finally { importing = false; if (!IsDisposed) actions.Enabled = true; }
    }
    void Recycle()
    {
        var paths = Selected(); if (paths.Length == 0) return;
        if (MessageBox.Show(this, $"Move {paths.Length} clip(s) to Windows Recycle Bin? Restore them there if needed.", "Recycle clips", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        foreach (string path in paths) FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        RefreshClips();
    }
    async Task Merge()
    {
        var paths = Selected(); if (paths.Length < 2) { status.Text = "Select at least two clips. Merge follows list order and keeps audio."; return; }
        if (MessageBox.Show(this, "Merge selected clips in list order at 720p with audio? Originals are preserved.", "Merge clips", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        cancellation = new(); foreach (Control c in actions.Controls) c.Enabled = c == cancel; cancel.Enabled = true;
        try
        {
            var progress = new Progress<string>(s => status.Text = s);
            string path = await MediaTools.MergeAsync(paths, ffmpeg, directory, cancellation.Token, s => ((IProgress<string>)progress).Report(s)); status.Text = "Saved " + Path.GetFileName(path); RefreshClips();
        }
        catch (OperationCanceledException) { status.Text = "Merge cancelled."; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Merge failed"); }
        finally { cancellation.Dispose(); cancellation = null; foreach (Control c in actions.Controls) c.Enabled = true; cancel.Enabled = false; }
    }
    string? Prompt(string label, string value)
    {
        using var form = new Form { Text = label, Width = 440, Height = 160, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var field = new TextBox { Text = value, Left = 15, Top = 15, Width = 390, MaxLength = 250 }; var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Left = 245, Top = 55 }; var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 330, Top = 55 };
        form.Controls.AddRange(new Control[] { field, ok, cancelButton }); form.AcceptButton = ok; form.CancelButton = cancelButton;
        return form.ShowDialog(this) == DialogResult.OK ? field.Text.Trim() : null;
    }
}
