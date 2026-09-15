using System.Diagnostics;
namespace GameReplay;

public sealed class EditorForm : Form
{
    readonly string input, ffmpeg, directory;
    readonly NumericUpDown start = Number(0, 86400, 0, 2), duration = Number(0, 86400, 0, 2), speed = Number(.25m, 4, 1, 2), volume = Number(0, 200, 100, 0);
    readonly ComboBox crop = Combo("Original", "Landscape", "Square", "Portrait"), size = Combo("720", "480", "1080"), format = Combo("MP4", "GIF");
    readonly CheckBox mute = new() { Text = "Mute audio", AutoSize = true };
    readonly TextBox title = new() { Width = 320, MaxLength = 300 };
    readonly TextBox music = new() { Width = 210, ReadOnly = true }, watermark = new() { Width = 210, ReadOnly = true };
    readonly NumericUpDown musicVolume = Number(0, 200, 50, 0);
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(570, 130), Text = "Original file is preserved. Exports use CPU encoding." };
    readonly Button export = new() { Text = "Export copy", AutoSize = true }, cancel = new() { Text = "Cancel export", Enabled = false, AutoSize = true };
    CancellationTokenSource? cancellation;
    public EditorForm(string inputPath, string ffmpegPath, string outputDirectory)
    {
        input = inputPath; ffmpeg = ffmpegPath; directory = outputDirectory;
        Text = "GameReplay • Edit " + Path.GetFileName(input); Width = 680; Height = 760; MinimumSize = new Size(620, 580); StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(24, 27, 34); ForeColor = Color.WhiteSmoke; Font = new Font("Segoe UI", 10);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20), ColumnCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(panel);
        mute.Text = "Mute original audio";
        Add("Start (seconds)", start); Add("Length (0 = to end)", duration); Add("Center crop", crop); Add("Output height", size); Add("Speed multiplier", speed); Add("Original volume (%)", volume); Add("", mute); Add("Text caption", title);
        Add("Soundtrack (loops)", FilePicker(music, "Audio files|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac")); Add("Music volume (%)", musicVolume);
        Add("Watermark (top right)", FilePicker(watermark, "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.webp")); Add("Format", format);
        var buttons = new FlowLayoutPanel { AutoSize = true, Width = 390 }; var play = new Button { Text = "Play original", AutoSize = true };
        play.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(input) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        buttons.Controls.AddRange(new Control[] { play, export, cancel }); Add("", buttons); panel.Controls.Add(status); panel.SetColumnSpan(status, 2);
        export.Click += async (_, _) => await Export(); cancel.Click += (_, _) => cancellation?.Cancel();
        FormClosing += (_, e) => { if (cancellation != null) { cancellation.Cancel(); e.Cancel = true; status.Text = "Cancelling export; close once it stops."; } };
        void Add(string label, Control control) { panel.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 0, 9) }); panel.Controls.Add(control); }
    }
    async Task Export()
    {
        cancellation = new CancellationTokenSource(); export.Enabled = false; cancel.Enabled = true;
        try
        {
            var options = new MediaExportOptions { StartSeconds = (double)start.Value, DurationSeconds = duration.Value == 0 ? null : (double)duration.Value, Crop = crop.Text, Height = int.Parse(size.Text), Speed = (double)speed.Value, Volume = (double)volume.Value / 100, Mute = mute.Checked, Gif = format.Text == "GIF", Title = title.Text, MusicPath = music.Text, MusicVolume = (double)musicVolume.Value / 100, WatermarkPath = watermark.Text };
            var progress = new Progress<string>(s => status.Text = s);
            status.Text = "Exporting…";
            string path = await MediaTools.ExportAsync(input, ffmpeg, directory, options, cancellation.Token, s => ((IProgress<string>)progress).Report(s));
            status.Text = "Saved " + Path.GetFileName(path);
        }
        catch (OperationCanceledException) { status.Text = "Export cancelled."; }
        catch (Exception ex) { status.Text = "Export failed."; MessageBox.Show(this, ex.Message, "Export failed"); }
        finally { cancellation.Dispose(); cancellation = null; export.Enabled = true; cancel.Enabled = false; }
    }
    static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals) => new() { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Width = 130, Increment = decimals > 0 ? .25m : 1 };
    Control FilePicker(TextBox field, string filter)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Width = 420 };
        var browse = new Button { Text = "Browse", AutoSize = true }; var clear = new Button { Text = "Clear", AutoSize = true };
        browse.Click += (_, _) => { using var dialog = new OpenFileDialog { Filter = filter }; if (dialog.ShowDialog(this) == DialogResult.OK) field.Text = dialog.FileName; };
        clear.Click += (_, _) => field.Clear(); row.Controls.AddRange(new Control[] { field, browse, clear }); return row;
    }
    static ComboBox Combo(params string[] values) { var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 }; box.Items.AddRange(values); box.SelectedIndex = 0; return box; }
}
