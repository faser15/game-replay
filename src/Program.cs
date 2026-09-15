using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GameReplay;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool testing = args.Length > 1 && (args[0] == "--smoke-test" || args[0] == "--self-test" || args[0] == "--render-ui");
        using var mutex = new Mutex(true,testing ? @"Local\GameReplay.Test." + Environment.ProcessId : @"Local\GameReplay.RollingRecorder",out bool first);
        if(!first) { MessageBox.Show("Game Replay is already running. Open it from the system tray."); return; }
        ApplicationConfiguration.Initialize();
        string root = Path.Combine(AppContext.BaseDirectory,"Data");
        if(args.Length > 1 && (args[0] == "--smoke-test" || args[0] == "--self-test" || args[0] == "--render-ui")) root = Path.GetFullPath(args[1]);
        try {
            if(args.Contains("--self-test")) { Checks.Run(root).GetAwaiter().GetResult(); return; }
            if(args.Contains("--smoke-test")) { Checks.Smoke(root).GetAwaiter().GetResult(); return; }
            using var form = new ReplayForm(root);
            if(args.Contains("--render-ui")) { form.Show(); Application.DoEvents(); using var bitmap = new Bitmap(form.Width,form.Height); form.DrawToBitmap(bitmap,form.Bounds with { X=0,Y=0 }); bitmap.Save(Path.Combine(root,"ui.png")); return; }
            Application.Run(form);
        } catch(Exception e) { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root,"error.txt"),e.ToString()); if(args.Length==0) MessageBox.Show(e.Message,"Game Replay"); Environment.ExitCode=1; }
    }
}

sealed class Settings
{
    public int Display {get;set;}
    public int Fps {get;set;} = 30;
    public bool Sound {get;set;} = true;
    public string Encoder {get;set;} = "Auto";
    public bool MemoryBuffer {get;set;} = true;
}

sealed class ReplayForm : Form
{
    readonly ReplayEngine engine;
    readonly Button start = new(), save = new();
    readonly Label state = new(), stats = new(), message = new();
    readonly ComboBox display = new(), fps = new(), encoder = new(), bufferMode = new();
    readonly CheckBox sound = new();
    readonly ProgressBar progress = new();
    readonly NotifyIcon tray = new();
    readonly System.Windows.Forms.Timer timer = new() {Interval=1000};
    bool busy, exiting, hadRunning;
    long libraryBytes;
    DateTime nextLibraryCheck;
    readonly string configPath;
    readonly Color muted = Color.FromArgb(157,169,187);
    public ReplayForm(string root)
    {
        engine = new ReplayEngine(root); configPath=Path.Combine(root,"settings.json");
        Text="Game Replay · 720p"; ClientSize=new Size(610,575); MinimumSize=MaximumSize=Size; MaximizeBox=false;
        StartPosition=FormStartPosition.CenterScreen; BackColor=Color.FromArgb(17,23,34); ForeColor=Color.White;
        Font=new Font("Segoe UI",10); Icon=SystemIcons.Application;
        AddLabel("GAME REPLAY",28,22,550,32,20,Color.White,FontStyle.Bold);
        AddLabel("720p · GPU encoding · Save your last five minutes.",30,62,550,26,10,muted);
        state.SetBounds(30,109,550,34); state.Font=new Font(Font,FontStyle.Bold); state.ForeColor=Color.FromArgb(86,222,174); state.Text="●  Ready to record"; Controls.Add(state);
        progress.SetBounds(30,149,550,8); Controls.Add(progress);
        stats.SetBounds(30,170,550,45); stats.ForeColor=muted; stats.Text="Buffer empty  ·  Temporary storage: 0 MB\nSaved clips: 0 MB / 5 GB"; Controls.Add(stats);
        AddLabel("DISPLAY INDEX",30,226,170,22,9,muted);
        AddLabel("FRAME RATE",218,226,160,22,9,muted);
        AddLabel("GPU ENCODER",402,226,178,22,9,muted);
        Configure(display,30,251,168); for(int i=0;i<Math.Max(1,Screen.AllScreens.Length);i++) display.Items.Add("Display " + i);
        Configure(fps,218,251,160); fps.Items.AddRange(["30 FPS · lighter","60 FPS · smoother"]);
        Configure(encoder,402,251,178); encoder.Items.AddRange(["Auto","h264_nvenc","h264_amf","h264_qsv"]);
        sound.Text="Game / system audio"; sound.SetBounds(30,300,260,30); sound.ForeColor=Color.White; Controls.Add(sound);
        Configure(bufferMode,316,300,264); bufferMode.Items.AddRange(["RAM buffer · no disk writes","Disk buffer · less RAM"]);
        AddLabel("Captures the selected screen, including anything over your game.\nUse borderless mode. Audio includes other apps on your default output.",30,340,550,44,9,muted);
        StyleButton(start,"Start buffer",30,400,170,false); start.Click+=async (_,_)=>await Toggle();
        StyleButton(save,"Save last 5 minutes",218,400,362,true); save.Enabled=false; save.Click+=async (_,_)=>await Save();
        AddLabel("SAVE HOTKEY   Ctrl + Shift + F8",220,452,360,23,9,muted);
        var open=new Button(); StyleButton(open,"Open clips ↗",30,488,160,false); open.Height=32; open.Click+=(_,_)=>Process.Start(new ProcessStartInfo(engine.Clips){UseShellExecute=true});
        var diag=new Button(); StyleButton(diag,"Diagnostics",208,488,135,false); diag.Height=32; diag.Click+=(_,_)=>ShowDiagnostics();
        var hide=new Button(); StyleButton(hide,"Hide to tray",361,488,219,false); hide.Height=32; hide.Click+=(_,_)=>Hide();
        message.SetBounds(30,536,550,32); message.Font=new Font("Segoe UI",8); message.ForeColor=muted; message.Text="Old footage expires automatically. Saved clips are kept until you remove them."; Controls.Add(message);
        Settings settings;
        try { settings=JsonSerializer.Deserialize<Settings>(File.ReadAllText(configPath))??new(); } catch { settings=new(); }
        display.SelectedIndex=Math.Clamp(settings.Display,0,display.Items.Count-1); fps.SelectedIndex=settings.Fps==60?1:0; sound.Checked=settings.Sound;
        encoder.SelectedIndex=Math.Max(0,encoder.Items.IndexOf(settings.Encoder));
        bufferMode.SelectedIndex=settings.MemoryBuffer?0:1;
        var menu=new ContextMenuStrip(); menu.Items.Add("Open Game Replay",null,(_,_)=>Restore());
        menu.Items.Add("Save last 5 minutes",null,async (_,_)=>await Save());
        menu.Items.Add("Start / stop buffer",null,async (_,_)=>await Toggle());
        menu.Items.Add("Open clips",null,(_,_)=>Process.Start(new ProcessStartInfo(engine.Clips){UseShellExecute=true}));
        menu.Items.Add("Exit",null,async (_,_)=>await ExitApp());
        tray.Icon=SystemIcons.Application; tray.Text="Game Replay — stopped"; tray.ContextMenuStrip=menu; tray.Visible=true; tray.DoubleClick+=(_,_)=>Restore();
        timer.Tick+=async (_,_)=>await Tick(); timer.Start();
        Shown+=(_,_)=> { if(!Native.RegisterHotKey(Handle,1,0x4000|0x0002|0x0004,(uint)Keys.F8)) message.Text="Hotkey is in use by another app. Use Save here or in the tray menu."; };
        FormClosing+=async (_,e)=> { if(exiting) return; e.Cancel=true; if(engine.Running || busy || engine.Saving) { Hide(); tray.ShowBalloonTip(2500,"Game Replay","Still running in the tray. Choose Exit there to stop.",ToolTipIcon.Info); } else await ExitApp(); };
    }
    void AddLabel(string text,int x,int y,int w,int h,int size,Color color,FontStyle style=FontStyle.Regular) { var l=new Label{Text=text,ForeColor=color,Font=new Font("Segoe UI",size,style)}; l.SetBounds(x,y,w,h); Controls.Add(l); }
    void Configure(ComboBox c,int x,int y,int w) { c.SetBounds(x,y,w,32); c.ForeColor=Color.Black; c.BackColor=Color.White; c.DropDownStyle=ComboBoxStyle.DropDownList; Controls.Add(c); }
    void StyleButton(Button b,string text,int x,int y,int w,bool accent) { b.Text=text; b.SetBounds(x,y,w,44); b.FlatStyle=FlatStyle.Flat; b.FlatAppearance.BorderSize=0; b.BackColor=accent?Color.FromArgb(71,215,161):Color.FromArgb(37,48,66); b.ForeColor=accent?Color.FromArgb(12,33,27):Color.White; b.Font=new Font(Font,FontStyle.Bold); Controls.Add(b); }
    void Restore() { Show(); WindowState=FormWindowState.Normal; Activate(); }
    void ControlsState() { start.Enabled=!busy&&!engine.Saving; save.Enabled=!busy&&!engine.Saving&&engine.BufferedSegments>0; foreach(Control c in new Control[]{display,fps,encoder,sound,bufferMode}) c.Enabled=!engine.Running&&!busy&&!engine.Saving; start.Text=engine.Running?"Stop buffer":"Start buffer"; }
    async Task Toggle()
    {
        if(busy||engine.Saving) return; busy=true; ControlsState();
        try {
            if(engine.Running) { await engine.StopAsync(false); state.Text="●  Stopped — unsaved buffer retained until restart / exit"; tray.Text="Game Replay — stopped"; }
            else {
                state.Text="●  Checking GPU and starting…";
                var s=new Settings {Display=display.SelectedIndex,Fps=fps.SelectedIndex==1?60:30,Sound=sound.Checked,Encoder=encoder.Text,MemoryBuffer=bufferMode.SelectedIndex==0};
                File.WriteAllText(configPath,JsonSerializer.Serialize(s));
                await engine.StartAsync(s.Display,s.Fps,s.Sound,s.Encoder,s.MemoryBuffer);
                state.Text="●  Recording · " + engine.Encoder; tray.Text="Game Replay — recording";
                message.Text="Save at any time. Clips finish at the next 2-second boundary.";
            }
            hadRunning=engine.Running;
        } catch(Exception e) { Error(e.Message); state.Text="●  Recording unavailable"; }
        finally { busy=false; ControlsState(); }
    }
    async Task Save()
    {
        if(busy||engine.Saving) return;
        try {
            var task=engine.SaveAsync(); ControlsState(); message.Text="Saving your clip… recording continues.";
            string path=await task; message.Text="Saved: " + Path.GetFileName(path);
            nextLibraryCheck=DateTime.MinValue;
            tray.ShowBalloonTip(3000,"Clip saved",Path.GetFileName(path),ToolTipIcon.Info);
        } catch(Exception e) { Error(e.Message); }
        finally { ControlsState(); }
    }
    async Task Tick()
    {
        if(busy) return;
        try {
            engine.Maintain();
            if(hadRunning&&!engine.Running) { hadRunning=false; await engine.StopAsync(false); state.Text="●  Capture stopped unexpectedly"; tray.Text="Game Replay — stopped"; Error("Capture stopped. Existing footage can still be saved. Check Diagnostics and restart the buffer."); }
            int seconds=Math.Min(300,engine.BufferedSegments*2);
            progress.Value=seconds*100/300;
            if(DateTime.UtcNow >= nextLibraryCheck) { libraryBytes=new DirectoryInfo(engine.Clips).GetFiles("*.mp4").Sum(f=>f.Length); nextLibraryCheck=DateTime.UtcNow.AddSeconds(15); }
            stats.Text=$"Buffer: {seconds/60:00}:{seconds%60:00} / 05:00  ·  {(engine.MemoryMode ? "RAM" : "Disk")}: {engine.BufferedBytes/1048576} MB\nSaved clips: {libraryBytes/1048576} MB / 5,120 MB";
            ControlsState();
        } catch(Exception e) { busy=true; try { await engine.StopAsync(false); hadRunning=false; state.Text="●  Stopped"; tray.Text="Game Replay — stopped"; Error(e.Message); } finally {busy=false; ControlsState();} }
    }
    void Error(string text) { message.Text=text.Split('\n')[0]; File.WriteAllText(Path.Combine(engine.Root,"diagnostics.txt"),text+"\n\n"+engine.Diagnostics); tray.ShowBalloonTip(5000,"Game Replay needs attention",text.Split('\n')[0],ToolTipIcon.Warning); if(Visible) MessageBox.Show(this,text.Length>1200?text[..1200]:text,"Game Replay",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
    void ShowDiagnostics() { string path=Path.Combine(engine.Root,"diagnostics.txt"); if(!File.Exists(path)) File.WriteAllText(path,engine.Diagnostics); Process.Start(new ProcessStartInfo("notepad.exe"){ArgumentList={path},UseShellExecute=true}); }
    async Task ExitApp() { if(busy||engine.Saving) { Restore(); message.Text="Wait for the current operation to finish before exiting."; return; } busy=true; timer.Stop(); await engine.StopAsync(true); Native.UnregisterHotKey(Handle,1); tray.Visible=false; exiting=true; Close(); }
    protected override void WndProc(ref Message m) { if(m.Msg==0x0312 && m.WParam.ToInt32()==1) _=Save(); base.WndProc(ref m); }
    protected override void Dispose(bool disposing) { if(disposing) { timer.Dispose(); tray.Dispose(); engine.Dispose(); } base.Dispose(disposing); }
}

static class Native
{
    [DllImport("user32.dll",SetLastError=true)] public static extern bool RegisterHotKey(IntPtr hWnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd,int id);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern bool CreateHardLink(string file,string existing,IntPtr security);
}

static class ProcessLifetime
{
    static readonly IntPtr job = Create();
    static IntPtr Create() {
        IntPtr h=CreateJobObject(IntPtr.Zero,null);
        var info=new ExtendedLimit {BasicLimitInformation=new BasicLimit{LimitFlags=0x2000}};
        int size=Marshal.SizeOf<ExtendedLimit>(); IntPtr ptr=Marshal.AllocHGlobal(size);
        try { Marshal.StructureToPtr(info,ptr,false); if(h==IntPtr.Zero || !SetInformationJobObject(h,9,ptr,(uint)size)) throw new System.ComponentModel.Win32Exception(); } finally {Marshal.FreeHGlobal(ptr);} return h;
    }
    public static void Attach(Process p) { if(!AssignProcessToJobObject(job,p.Handle)) {p.Kill(true); throw new System.ComponentModel.Win32Exception();} }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit {public long PerProcessUserTimeLimit,PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize,MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass,SchedulingClass;}
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit {public BasicLimit BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr security,string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job,int type,IntPtr info,uint size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
}
