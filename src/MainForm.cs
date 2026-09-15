using System.Diagnostics;
using System.Media;
using System.Text.Json;
using NAudio.Wave;

namespace GameReplay;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool testing=args.Length>1 && args[0].StartsWith("--");
        using var mutex=new Mutex(true,testing?@"Local\GameReplay.Test."+Environment.ProcessId:@"Local\GameReplay.RollingRecorder",out bool first);
        if(!first){MessageBox.Show("GameReplay is already running. Exit the previous version from its tray menu before opening this version.");return;}
        ApplicationConfiguration.Initialize();
        string root=testing?Path.GetFullPath(args[1]):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GameReplay");
        try {
            Directory.CreateDirectory(root);
            if(args.FirstOrDefault()=="--self-test"){Checks.Run(root).GetAwaiter().GetResult();DesktopFeatureChecks.RunAsync(root).GetAwaiter().GetResult();return;}
            if(args.FirstOrDefault()=="--smoke-test"){Checks.Smoke(root).GetAwaiter().GetResult();return;}
            if(args.FirstOrDefault()=="--setup-engine"){EngineInstaller.InstallAsync(root).GetAwaiter().GetResult();return;}
            if(args.FirstOrDefault()=="--media-test"){ReleaseChecks.RunAsync(root).GetAwaiter().GetResult();return;}
            if(args.FirstOrDefault()=="--capture-test"){CaptureChecks.Run(root);return;}
            using var form=new MainForm(root,args.FirstOrDefault()=="--render-ui");
            if(args.FirstOrDefault()=="--render-ui"){form.Show();Application.DoEvents();form.RenderViews(root);return;}
            Application.Run(form);
        } catch(Exception e){File.WriteAllText(Path.Combine(root,"error.txt"),e.ToString());if(!testing)MessageBox.Show(e.Message,"GameReplay");Environment.ExitCode=1;}
    }
}

sealed record CaptureChoice(string Label,long Handle,int Display)
{
    public override string ToString()=>Label;
}

sealed class MainForm:Form
{
    readonly string root,settingsPath;
    readonly ReplayEngine engine;
    readonly GameProfiles profiles;
    AppSettings settings;
    readonly TabControl tabs=new(){Dock=DockStyle.Fill};
    readonly Label status=new(),details=new(),notice=new();
    readonly ProgressBar bufferProgress=new();
    readonly Button start=new(),save=new(),session=new(),install=new();
    readonly ComboBox target=new(),resolution=new(),fps=new(),encoder=new(),bufferMode=new(),microphone=new(),webcamCorner=new();
    readonly NumericUpDown bitrate=new(),duration=new(),gain=new(),storage=new(),shortDuration=new();
    readonly CheckBox systemAudio=new(),micEnabled=new(),denoise=new(),separateTracks=new(),cursor=new(),alerts=new(),minimized=new(),recordOnLaunch=new(),voiceEnabled=new(),autoGames=new(),loginStartup=new(),gameFullSession=new();
    readonly TextBox webcam=new(),saveFolder=new(),clipHotkey=new(),shortHotkey=new(),screenshotHotkey=new(),sessionHotkey=new(),bookmarkHotkey=new();
    readonly ListBox games=new();
    readonly NotifyIcon tray=new();
    readonly System.Windows.Forms.Timer timer=new(){Interval=1000};
    readonly List<(TimeSpan Time,string Label)> bookmarks=new();
    VoiceClipping? voice;
    bool busy,exiting,ticking,autoOwned,autoSuppressed,expectedRunning,recoveryBlocked;
    string? activeAutoExecutable;
    RecorderOptions? activeOptions;
    long libraryBytes;
    DateTime nextLibraryCheck,nextGameCheck;
    readonly Color bg=Color.FromArgb(17,23,34),panel=Color.FromArgb(26,35,49),muted=Color.FromArgb(157,169,187),accent=Color.FromArgb(71,215,161);

    public MainForm(string dataRoot,bool preview=false)
    {
        root=dataRoot;settingsPath=Path.Combine(root,"settings.json");
        try{settings=JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath))??new();}catch{settings=new();}
        engine=new ReplayEngine(root);profiles=new GameProfiles(root);
        Text="GameReplay";ClientSize=new Size(900,700);MinimumSize=new Size(820,660);StartPosition=FormStartPosition.CenterScreen;
        BackColor=bg;ForeColor=Color.White;Font=new Font("Segoe UI",10);Icon=SystemIcons.Application;
        var top=new Panel{Dock=DockStyle.Top,Height=82,BackColor=bg};
        top.Controls.Add(new Label{Text="GAME REPLAY",Font=new Font("Segoe UI",22,FontStyle.Bold),ForeColor=Color.White,Bounds=new Rectangle(24,12,430,38)});
        top.Controls.Add(new Label{Text="Record. Keep the moment. Make it yours.",ForeColor=muted,Bounds=new Rectangle(26,51,720,25)});
        notice.Dock=DockStyle.Bottom;notice.Height=40;notice.Padding=new Padding(18,8,10,0);notice.ForeColor=muted;notice.Text="720p defaults keep recording light. Clips stay on this PC.";
        Controls.Add(tabs);Controls.Add(notice);Controls.Add(top);
        BuildRecordTab();BuildSettingsTab();BuildHotkeysTab();BuildGamesTab();BuildHelpTab();LoadSettings();RefreshTargets();RefreshGames();
        var menu=new ContextMenuStrip();menu.Items.Add("Open GameReplay",null,(_,_)=>Restore());menu.Items.Add("Save clip",null,async(_,_)=>await SaveClip());menu.Items.Add("Start / stop buffer",null,async(_,_)=>await ToggleBuffer());menu.Items.Add("Library",null,(_,_)=>OpenLibrary());menu.Items.Add("Exit",null,async(_,_)=>await ExitApp());
        tray.Icon=SystemIcons.Application;tray.Text="GameReplay — stopped";tray.ContextMenuStrip=menu;tray.Visible=!preview;tray.DoubleClick+=(_,_)=>Restore();
        timer.Tick+=async(_,_)=>await Tick();if(!preview)timer.Start();
        Shown+=async(_,_)=>{if(preview)return;try{ConfigureStorage();RegisterHotkeys();if(settings.StartMinimized)Hide();if(settings.RecordOnLaunch&&File.Exists(engine.Ffmpeg)&&settings.Recording.WindowHandle==0)await ToggleBuffer();}catch(Exception e){Error(e.Message);}};
        FormClosing+=async(_,e)=>{if(exiting)return;e.Cancel=true;if(engine.Running||busy||engine.Saving){Hide();tray.ShowBalloonTip(2500,"GameReplay","Recording continues in the tray. Choose Exit there to stop.",ToolTipIcon.Info);}else await ExitApp();};
        UpdateControls();
    }
    TabPage Page(string title){var page=new TabPage(title){BackColor=bg,ForeColor=Color.White,AutoScroll=true,Padding=new Padding(20)};tabs.TabPages.Add(page);return page;}
    Button Button(string text,EventHandler action,bool primary=false){var b=new Button{Text=text,AutoSize=true,MinimumSize=new Size(140,40),FlatStyle=FlatStyle.Flat,BackColor=primary?accent:panel,ForeColor=primary?Color.FromArgb(12,33,27):Color.White,Margin=new Padding(0,0,12,12)};b.FlatAppearance.BorderSize=0;b.Click+=action;return b;}
    void StyleButton(Button b,string text,EventHandler action,bool primary=false){b.Text=text;b.MinimumSize=new Size(165,46);b.AutoSize=true;b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderSize=0;b.BackColor=primary?accent:panel;b.ForeColor=primary?Color.FromArgb(12,33,27):Color.White;b.Margin=new Padding(0,0,12,12);b.Click+=action;}
    FlowLayoutPanel Flow()=>new(){AutoSize=true,Dock=DockStyle.Top,WrapContents=true,Padding=new Padding(0,6,0,2)};
    TableLayoutPanel FormGrid()=>new(){AutoSize=true,Dock=DockStyle.Top,ColumnCount=2,ColumnStyles={new ColumnStyle(SizeType.Absolute,235),new ColumnStyle(SizeType.Percent,100)},Padding=new Padding(0,8,0,0)};
    void Row(TableLayoutPanel grid,string label,Control control){int row=grid.RowCount++;grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));grid.Controls.Add(new Label{Text=label,AutoSize=true,ForeColor=muted,Margin=new Padding(0,8,14,12)},0,row);control.Margin=new Padding(0,4,0,8);control.Width=420;control.Anchor=AnchorStyles.Left|AnchorStyles.Right;if(control is TextBoxBase or NumericUpDown){control.ForeColor=Color.Black;control.BackColor=Color.White;}grid.Controls.Add(control,1,row);}
    void Combo(ComboBox c,params object[] values){c.DropDownStyle=ComboBoxStyle.DropDownList;c.ForeColor=Color.Black;c.BackColor=Color.White;c.Items.AddRange(values);if(values.Length>0)c.SelectedIndex=0;}
    void Number(NumericUpDown n,int min,int max,int value){n.Minimum=min;n.Maximum=max;n.Value=Math.Clamp(value,min,max);}
    void Check(CheckBox c,string text){c.Text=text;c.AutoSize=true;c.ForeColor=Color.White;}
    void BuildRecordTab()
    {
        var page=Page("Record");var layout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1};page.Controls.Add(layout);
        status.Text="Ready to record";status.Font=new Font(Font.FontFamily,17,FontStyle.Bold);status.ForeColor=accent;status.AutoSize=true;status.Margin=new Padding(0,8,0,12);layout.Controls.Add(status);
        details.Text="Buffer empty";details.ForeColor=muted;details.AutoSize=true;details.Margin=new Padding(0,0,0,12);layout.Controls.Add(details);
        bufferProgress.Width=790;bufferProgress.Height=10;bufferProgress.Anchor=AnchorStyles.Left|AnchorStyles.Right;layout.Controls.Add(bufferProgress);
        var targetRow=Flow();Combo(target);target.Width=600;targetRow.Controls.Add(target);targetRow.Controls.Add(Button("Refresh windows",(_,_)=>RefreshTargets()));layout.Controls.Add(targetRow);
        layout.Controls.Add(new Label{Text="Choose a screen or a game's window. Borderless/windowed mode is recommended.",AutoSize=true,ForeColor=muted,Margin=new Padding(0,0,0,20)});
        var actions=Flow();StyleButton(start,"Start buffer",async(_,_)=>await ToggleBuffer());StyleButton(save,"Save last 5 minutes",async(_,_)=>await SaveClip(),true);StyleButton(session,"Start full session",async(_,_)=>await ToggleSession());actions.Controls.AddRange([start,save,session]);layout.Controls.Add(actions);
        var more=Flow();more.Controls.Add(Button("Screenshot",(_,_)=>Screenshot()));more.Controls.Add(Button("Bookmark session",(_,_)=>Bookmark()));more.Controls.Add(Button("Clip library / editor",(_,_)=>OpenLibrary()));more.Controls.Add(Button("Hide to tray",(_,_)=>Hide()));layout.Controls.Add(more);
        layout.Controls.Add(new Label{Text="Replay mode keeps only your recent footage. Full sessions keep recording until stopped or the storage limit is reached.\nMicrophone and webcam recording are off unless enabled in Settings.",AutoSize=true,MaximumSize=new Size(800,0),ForeColor=muted,Margin=new Padding(0,14,0,0)});
        StyleButton(install,"Install recording engine",async(_,_)=>await InstallEngine(),true);layout.Controls.Add(install);
    }
    void BuildSettingsTab()
    {
        var page=Page("Recording settings");var grid=FormGrid();page.Controls.Add(grid);
        Combo(resolution,"360p · smallest","720p · light","1080p","1440p","2160p / 4K");Combo(fps,24,30,60,120);Combo(encoder,"Auto","h264_nvenc","h264_amf","h264_qsv","libx264");Combo(bufferMode,"RAM · write footage on save","Disk · lower RAM use");
        Number(bitrate,500,30000,3000);bitrate.Increment=500;Number(duration,15,600,300);duration.Increment=15;Number(gain,0,500,100);Number(storage,1,1000,5);
        Check(systemAudio,"Record system audio");Check(micEnabled,"Record microphone");Check(denoise,"Reduce microphone noise");Check(separateTracks,"Keep system and microphone as separate tracks");Check(cursor,"Include cursor");
        Combo(microphone,"Default microphone");for(int i=0;i<WaveIn.DeviceCount;i++)microphone.Items.Add(WaveIn.GetCapabilities(i).ProductName);
        Combo(webcamCorner,"BottomRight","BottomLeft","TopRight","TopLeft");webcam.PlaceholderText="Optional DirectShow camera name; blank disables webcam";
        saveFolder.PlaceholderText="Default: this app's local Clips folder";saveFolder.ForeColor=Color.Black;saveFolder.BackColor=Color.White;
        Row(grid,"Resolution",resolution);Row(grid,"Frames per second",fps);Row(grid,"Video bitrate (kbps)",bitrate);Row(grid,"Replay length (seconds)",duration);Row(grid,"Encoder",encoder);Row(grid,"Buffer location",bufferMode);
        Row(grid,"Desktop audio",systemAudio);Row(grid,"Microphone",micEnabled);Row(grid,"Microphone device",microphone);Row(grid,"Microphone gain (%)",gain);Row(grid,"Noise suppression",denoise);Row(grid,"Audio tracks",separateTracks);Row(grid,"Mouse cursor",cursor);
        Row(grid,"Webcam device",webcam);Row(grid,"Webcam position",webcamCorner);
        var folderRow=Flow();folderRow.Controls.Add(saveFolder);saveFolder.Width=280;folderRow.Controls.Add(Button("Browse",(_,_)=>{using var d=new FolderBrowserDialog();if(d.ShowDialog(this)==DialogResult.OK)saveFolder.Text=d.SelectedPath;}));Row(grid,"Clip folder",folderRow);Row(grid,"Storage limit (GiB)",storage);Row(grid,"",Button("Apply settings",(_,_)=>ApplySettings(),true));
    }
    void BuildHotkeysTab()
    {
        var page=Page("Hotkeys & startup");var grid=FormGrid();page.Controls.Add(grid);
        Number(shortDuration,5,600,30);Row(grid,"Save configured replay",clipHotkey);Row(grid,"Save shorter clip",shortHotkey);Row(grid,"Short clip length (seconds)",shortDuration);Row(grid,"Start / stop session",sessionHotkey);Row(grid,"Screenshot",screenshotHotkey);Row(grid,"Session bookmark",bookmarkHotkey);
        Check(alerts,"Play a sound when saved");Check(minimized,"Start in the tray");Check(recordOnLaunch,"Start recording when the app opens");Check(voiceEnabled,"Voice clipping: say 'clip that' (uses microphone)");Check(autoGames,"Automatically record enabled game profiles");
        Check(loginStartup,"Launch when I sign in to Windows");Row(grid,"Feedback",alerts);Row(grid,"Window",minimized);Row(grid,"Windows startup",loginStartup);Row(grid,"Recording on launch",recordOnLaunch);Row(grid,"Voice",voiceEnabled);Row(grid,"Game detection",autoGames);Row(grid,"",Button("Apply hotkeys & startup",(_,_)=>ApplySettings(),true));Row(grid,"Hotkey format",new Label{Text="Examples: Ctrl+Shift+F8, Alt+F9. Voice needs an English Windows speech recognizer.",AutoSize=true,MaximumSize=new Size(450,0),ForeColor=muted});
    }
    void BuildGamesTab()
    {
        var page=Page("Game profiles");var layout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1};page.Controls.Add(layout);
        layout.Controls.Add(new Label{Text="Select a running game in Record, choose your settings, then add its profile.\nProfiles recognize executable names and capture that game's visible window. They do not inject into games.",AutoSize=true,ForeColor=muted,Margin=new Padding(0,4,0,20)});
        games.Width=790;games.Height=270;games.ForeColor=Color.Black;layout.Controls.Add(games);
        Check(gameFullSession,"New/updated profile records the full session instead of a replay buffer");layout.Controls.Add(gameFullSession);
        var row=Flow();row.Controls.Add(Button("Add selected game",(_,_)=>AddGame()));row.Controls.Add(Button("Choose game EXE",(_,_)=>AddGameFile()));row.Controls.Add(Button("Load profile",(_,_)=>LoadGame()));row.Controls.Add(Button("Remove profile",(_,_)=>RemoveGame()));layout.Controls.Add(row);
        layout.Controls.Add(new Label{Text="Automatic kill/win detection requires game-specific integrations. It is not active in this release.\nYou can always save with a hotkey, voice command, or the Save button.",AutoSize=true,ForeColor=muted,Margin=new Padding(0,16,0,0)});
    }
    void BuildHelpTab()
    {
        var page=Page("About & help");var flow=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};page.Controls.Add(flow);
        flow.Controls.Add(new Label{Text="GameReplay 1.3\nAn independent, local-first Windows recorder and clip editor.\n\nDefaults: 720p, 30 FPS, GPU H.264, five-minute RAM replay.\nThe first setup downloads a checksum-verified FFmpeg engine (~106 MiB).\nRecordings are local. This app does not upload clips or require an account.\n\nUse the library to organize, trim, crop, add text, and export MP4/GIF.\nFull sessions stop at your storage limit. Save a test clip before important gameplay.\n\nHDR, exclusive fullscreen, and anti-cheat behavior vary by game.\nVoice recognition, webcam, and microphone depend on devices installed on this PC.",AutoSize=true,MaximumSize=new Size(790,0),ForeColor=muted,Margin=new Padding(0,8,0,24)});
        flow.Controls.Add(Button("Project & releases",(_,_)=>Open("https://github.com/faser15/game-replay")));flow.Controls.Add(Button("Open diagnostics",(_,_)=>Diagnostics()));flow.Controls.Add(Button("Open clips folder",(_,_)=>Open(engine.Clips)));flow.Controls.Add(Button("Recording engine setup",async(_,_)=>await InstallEngine()));
    }
    void LoadSettings()
    {
        var o=settings.Recording;resolution.SelectedIndex=o.Height>=2160?4:o.Height>=1440?3:o.Height>=1080?2:o.Height>=720?1:0;
        fps.SelectedItem=fps.Items.Cast<int>().Contains(o.Fps)?o.Fps:30;encoder.SelectedItem=encoder.Items.Contains(o.Encoder)?o.Encoder:"Auto";
        bitrate.Value=Math.Clamp(o.BitrateKbps,500,30000);duration.Value=Math.Clamp(o.ClipSeconds,15,600);bufferMode.SelectedIndex=o.MemoryBuffer?0:1;systemAudio.Checked=o.SystemAudio;micEnabled.Checked=o.Microphone;gain.Value=(decimal)Math.Clamp(o.MicGain*100,0,500);microphone.SelectedIndex=Math.Clamp(o.MicrophoneDevice+1,0,microphone.Items.Count-1);denoise.Checked=o.NoiseSuppression;separateTracks.Checked=o.SeparateAudioTracks;cursor.Checked=o.CaptureCursor;webcam.Text=o.WebcamDevice;webcamCorner.SelectedItem=webcamCorner.Items.Contains(o.WebcamCorner)?o.WebcamCorner:"BottomRight";
        clipHotkey.Text=settings.ClipHotkey;shortHotkey.Text=settings.ShortClipHotkey;sessionHotkey.Text=settings.SessionHotkey;screenshotHotkey.Text=settings.ScreenshotHotkey;bookmarkHotkey.Text=settings.BookmarkHotkey;shortDuration.Value=Math.Clamp(settings.ShortClipSeconds,5,600);alerts.Checked=settings.SoundAlerts;minimized.Checked=settings.StartMinimized;recordOnLaunch.Checked=settings.RecordOnLaunch;voiceEnabled.Checked=settings.VoiceClipping;autoGames.Checked=settings.AutoDetectGames;saveFolder.Text=settings.SaveFolder;storage.Value=Math.Clamp(settings.StorageLimitGiB,1,1000);
        loginStartup.Checked=DesktopStartup.Enabled;
    }
    RecorderOptions ReadOptions()
    {
        var dimensions=new[]{(640,360),(1280,720),(1920,1080),(2560,1440),(3840,2160)}[Math.Max(0,resolution.SelectedIndex)];var selected=target.SelectedItem as CaptureChoice;
        return new(){DisplayIndex=selected?.Display??0,WindowHandle=selected?.Handle??0,Width=dimensions.Item1,Height=dimensions.Item2,Fps=(int)(fps.SelectedItem??30),BitrateKbps=(int)bitrate.Value,Encoder=encoder.Text,ClipSeconds=(int)duration.Value,MemoryBuffer=bufferMode.SelectedIndex==0,SystemAudio=systemAudio.Checked,Microphone=micEnabled.Checked,MicrophoneDevice=microphone.SelectedIndex-1,MicGain=(float)gain.Value/100,NoiseSuppression=denoise.Checked,SeparateAudioTracks=separateTracks.Checked,CaptureCursor=cursor.Checked,WebcamDevice=webcam.Text.Trim(),WebcamCorner=webcamCorner.Text};
    }
    void ApplySettings()
    {
        if(busy||engine.Running||engine.Saving){Error("Stop recording before changing settings.");return;}
        string previousFolder=engine.Clips;long previousLimit=engine.StorageLimitBytes;bool storageChanged=false,startupChanged=false,previousStartup=false;
        string temporary=settingsPath+".tmp";
        try{
            var keys=new[]{clipHotkey.Text,shortHotkey.Text,sessionHotkey.Text,screenshotHotkey.Text,bookmarkHotkey.Text};var parsed=new List<HotkeyBinding>();foreach(string key in keys){if(!HotkeyBinding.TryParse(key,out var binding))throw new Exception("Invalid hotkey: "+key);if(parsed.Contains(binding))throw new Exception("Each action needs a different hotkey.");parsed.Add(binding);}
            var candidate=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            candidate.Recording=ReadOptions();candidate.ClipHotkey=clipHotkey.Text;candidate.ShortClipHotkey=shortHotkey.Text;candidate.SessionHotkey=sessionHotkey.Text;candidate.ScreenshotHotkey=screenshotHotkey.Text;candidate.BookmarkHotkey=bookmarkHotkey.Text;candidate.ShortClipSeconds=(int)shortDuration.Value;candidate.SoundAlerts=alerts.Checked;candidate.StartMinimized=minimized.Checked;candidate.RecordOnLaunch=recordOnLaunch.Checked;candidate.VoiceClipping=voiceEnabled.Checked;candidate.AutoDetectGames=autoGames.Checked;candidate.SaveFolder=saveFolder.Text.Trim();candidate.StorageLimitGiB=(int)storage.Value;
            File.WriteAllText(temporary,JsonSerializer.Serialize(candidate,new JsonSerializerOptions{WriteIndented=true}));
            engine.ConfigureStorage(string.IsNullOrWhiteSpace(candidate.SaveFolder)?Path.Combine(root,"Clips"):candidate.SaveFolder,candidate.StorageLimitGiB*1024L*1024*1024);storageChanged=true;
            previousStartup=DesktopStartup.Enabled;
            if(previousStartup!=loginStartup.Checked){DesktopStartup.SetEnabled(loginStartup.Checked);startupChanged=true;}
            File.Move(temporary,settingsPath,true);settings=candidate;nextLibraryCheck=DateTime.MinValue;
            if(RegisterHotkeys())notice.Text="Settings saved.";
        }catch(Exception e){
            var errors=new List<string>{e.Message};
            if(startupChanged)try{DesktopStartup.SetEnabled(previousStartup);}catch(Exception rollback){errors.Add("Startup rollback failed: "+rollback.Message);}
            if(storageChanged)try{engine.ConfigureStorage(previousFolder,previousLimit);}catch(Exception rollback){errors.Add("Storage rollback failed: "+rollback.Message);}
            Error(string.Join("\n",errors));
        }finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch{}}
    }
    void ConfigureStorage()=>engine.ConfigureStorage(string.IsNullOrWhiteSpace(settings.SaveFolder)?Path.Combine(root,"Clips"):settings.SaveFolder,settings.StorageLimitGiB*1024L*1024*1024);
    bool RegisterHotkeys(){var failed=new List<string>();string[] keys=[settings.ClipHotkey,settings.ShortClipHotkey,settings.SessionHotkey,settings.ScreenshotHotkey,settings.BookmarkHotkey];for(int i=1;i<=keys.Length;i++)HotkeyBinding.Unregister(Handle,i);for(int i=0;i<keys.Length;i++)if(!HotkeyBinding.TryParse(keys[i],out var binding)||!binding.Register(Handle,i+1))failed.Add(keys[i]);if(failed.Count>0)notice.Text="Hotkeys unavailable: "+string.Join(", ",failed)+". Buttons still work.";return failed.Count==0;}
    void RefreshTargets()
    {
        if(engine.Running||busy)return;
        var previous=target.SelectedItem as CaptureChoice;
        int display=previous?.Display??settings.Recording.DisplayIndex;
        target.Items.Clear();for(int i=0;i<Screen.AllScreens.Length;i++)target.Items.Add(new CaptureChoice("Screen "+(i+1)+" · "+Screen.AllScreens[i].Bounds.Width+"×"+Screen.AllScreens[i].Bounds.Height,0,i));
        foreach(var w in DesktopFeatures.GetWindows())target.Items.Add(new CaptureChoice(w.Title+" ("+w.ProcessName+")",w.Handle,0));
        if(target.Items.Count>0)target.SelectedIndex=Math.Clamp(display,0,Math.Max(0,Screen.AllScreens.Length-1));
        // An HWND persisted by a previous process may have been recycled: never restore it at launch.
        if(previous?.Handle!=0&&previous!=null)for(int i=0;i<target.Items.Count;i++)if(target.Items[i] is CaptureChoice choice&&choice.Handle==previous.Handle&&choice.Label==previous.Label)target.SelectedIndex=i;
        if(previous==null&&settings.Recording.WindowHandle!=0)notice.Text="Select your game's current window before recording; saved window handles are not reused.";
    }
    void UpdateControls(){bool ready=File.Exists(engine.Ffmpeg);start.Enabled=session.Enabled=ready&&!busy&&!engine.Saving;save.Enabled=ready&&!busy&&!engine.Saving&&!engine.SessionRunning&&engine.BufferedSegments>0;target.Enabled=!busy&&!engine.Running;start.Text=engine.Running&&!engine.SessionRunning?"Stop buffer":"Start buffer";session.Text=engine.SessionRunning?"Stop & save session":"Start full session";save.Text="Save last "+(settings.Recording.ClipSeconds>=60?settings.Recording.ClipSeconds/60+" min":settings.Recording.ClipSeconds+" sec");install.Visible=!ready;install.Enabled=!busy;tabs.TabPages[1].Enabled=tabs.TabPages[2].Enabled=!busy&&!engine.Running&&!engine.Saving;tray.Text=engine.Running?"GameReplay — recording":"GameReplay — stopped";}
    async Task ToggleBuffer()
    {
        if(busy||engine.Saving)return;if(engine.SessionRunning){Error("Stop and save the full session first.");return;}
        busy=true;UpdateControls();
        try{
            if(engine.Running){expectedRunning=false;await engine.StopAsync(false);StopVoice();autoSuppressed=autoOwned;autoOwned=false;activeAutoExecutable=null;status.Text="Stopped — buffer available to save";}
            else{var options=ReadOptions();status.Text="Starting capture…";await engine.StartAsync(options);activeOptions=options;settings.Recording=options;expectedRunning=true;recoveryBlocked=false;autoOwned=false;activeAutoExecutable=null;StartVoice();status.Text="Recording replay · "+engine.CaptureBackend;}
        }catch(Exception e){expectedRunning=false;StopVoice();recoveryBlocked=true;Error(e.Message);status.Text="Capture unavailable — retained footage can be saved";}
        finally{busy=false;UpdateControls();}
    }
    async Task CompleteSession()
    {
        expectedRunning=false;StopVoice();
        string path=await engine.StopSessionAsync();
        var store=new BookmarkStore(path);foreach(var b in bookmarks)store.Add(b.Time,b.Label);
        bookmarks.Clear();nextLibraryCheck=DateTime.MinValue;Notify("Session saved",Path.GetFileName(path));status.Text="Session saved";
    }
    async Task ToggleSession()
    {
        if(busy||engine.Saving)return;busy=true;UpdateControls();
        try{
            if(engine.SessionRunning){autoSuppressed=autoOwned;autoOwned=false;activeAutoExecutable=null;await CompleteSession();}
            else{expectedRunning=false;if(engine.Running)await engine.StopAsync(false);StopVoice();var options=ReadOptions();bookmarks.Clear();status.Text="Starting full session…";await engine.StartSessionAsync(options);activeOptions=options;settings.Recording=options;expectedRunning=true;recoveryBlocked=false;autoOwned=false;activeAutoExecutable=null;StartVoice();status.Text="Recording full session · "+engine.CaptureBackend;}
        }catch(Exception e){expectedRunning=false;StopVoice();recoveryBlocked=true;Error(e.Message);}
        finally{busy=false;UpdateControls();}
    }
    async Task SaveClip(int? seconds=null){if(busy||engine.Saving)return;if(engine.SessionRunning){if(engine.Running)Bookmark();else await ToggleSession();return;}try{var task=engine.SaveAsync(seconds);UpdateControls();notice.Text="Saving clip…";string path=await task;nextLibraryCheck=DateTime.MinValue;Notify("Clip saved",Path.GetFileName(path));}catch(Exception e){Error(e.Message);}finally{UpdateControls();}}
    void Screenshot(){try{var o=engine.Running&&activeOptions!=null?activeOptions:ReadOptions();StorageBudget.RequireCapacity(engine.Clips,engine.StorageLimitBytes,32L*1024*1024);string folder=Path.Combine(engine.Clips,"Screenshots");string file=DesktopFeatures.SaveScreenshot(Path.Combine(folder,$"Screenshot-{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png"),o.DisplayIndex,o.WindowHandle);nextLibraryCheck=DateTime.MinValue;Notify("Screenshot saved",Path.GetFileName(file));}catch(Exception e){Error(e.Message);}}
    void Bookmark(){if(!engine.SessionRunning||!engine.Running){Error("Start a full session before adding bookmarks.");return;}bookmarks.Add((DateTime.UtcNow-engine.Started,"Highlight "+(bookmarks.Count+1)));Notify("Bookmark added",bookmarks.Count+" session bookmarks");}
    void OpenLibrary(){if(!File.Exists(engine.Ffmpeg)){Error("Install the recording engine first.");return;}new ClipLibraryForm(engine.Clips,engine.Ffmpeg).Show(this);}
    void StartVoice(){if(!settings.VoiceClipping)return;try{if(voice==null){voice=new VoiceClipping();voice.RecognitionError+=text=>{if(!IsDisposed&&IsHandleCreated)BeginInvoke(()=>notice.Text="Voice clipping stopped: "+text);};}voice.ActionClipRequested-=VoiceRequested;voice.ActionClipRequested+=VoiceRequested;voice.Start();}catch(Exception e){notice.Text="Recording is active; voice clipping unavailable: "+e.Message;}}
    void VoiceRequested(){if(!IsDisposed&&IsHandleCreated)BeginInvoke(async()=>{if(engine.SessionRunning)Bookmark();else await SaveClip();});}
    void StopVoice(){voice?.Stop();}
    async Task InstallEngine(){if(busy||engine.Running)return;busy=true;UpdateControls();try{await EngineInstaller.InstallAsync(root,new Progress<string>(s=>notice.Text=s));notice.Text="Engine ready. Click Start buffer.";}catch(Exception e){Error(e.Message);}finally{busy=false;UpdateControls();}}
    async Task Tick()
    {
        if(busy||ticking)return;ticking=true;
        try{
            if(expectedRunning&&!engine.Running&&autoOwned&&!engine.Saving)await CheckGames();
            if(expectedRunning&&!engine.Running){expectedRunning=false;recoveryBlocked=true;autoSuppressed=true;autoOwned=false;activeAutoExecutable=null;StopVoice();await engine.StopAsync(false);status.Text="Recording stopped unexpectedly — footage retained";Error("The recorder exited unexpectedly. Save the retained replay or stop/save the pending session before starting again.\n"+engine.Diagnostics);return;}
            engine.Maintain();int seconds=Math.Min(settings.Recording.ClipSeconds,engine.BufferedSegments*2);bufferProgress.Value=Math.Clamp(seconds*100/Math.Max(15,settings.Recording.ClipSeconds),0,100);
            if(DateTime.UtcNow>=nextLibraryCheck){libraryBytes=StorageBudget.UsedBytes(engine.Clips);nextLibraryCheck=DateTime.UtcNow.AddSeconds(15);}
            details.Text=$"{(engine.SessionRunning?"Session":"Replay")}: {engine.BufferedSegments*2/60:00}:{engine.BufferedSegments*2%60:00}  ·  {(engine.MemoryMode?"RAM":"Disk")} {engine.BufferedBytes/1048576} MB  ·  Library {libraryBytes/1048576} / {settings.StorageLimitGiB*1024} MB";
            if(settings.AutoDetectGames&&DateTime.UtcNow>=nextGameCheck&&!engine.Saving){nextGameCheck=DateTime.UtcNow.AddSeconds(5);await CheckGames();}UpdateControls();
        }catch(Exception e){busy=true;try{expectedRunning=false;recoveryBlocked=true;autoSuppressed=true;autoOwned=false;activeAutoExecutable=null;await engine.StopAsync(false);StopVoice();status.Text="Recording stopped — buffer retained";Error(e.Message);}finally{busy=false;UpdateControls();}}finally{ticking=false;UpdateControls();}
    }
    async Task CheckGames()
    {
        if(recoveryBlocked||engine.Saving)return;
        var running=profiles.GetRunningProfiles();
        if(autoOwned&&activeAutoExecutable!=null&&!running.Any(p=>GameProfiles.NormalizeExecutable(p.Executable).Equals(activeAutoExecutable,StringComparison.OrdinalIgnoreCase)))
        {
            busy=true;
            try{
                expectedRunning=false;StopVoice();autoOwned=false;activeAutoExecutable=null;
                if(engine.SessionRunning)await CompleteSession();
                else{await engine.StopAsync(false);if(engine.BufferedSegments>0){string path=await engine.SaveAsync();Notify("Game closed — replay saved",Path.GetFileName(path));}status.Text="Game closed — recording stopped";}
            }catch{recoveryBlocked=true;autoSuppressed=true;throw;}finally{busy=false;}
        }
        var match=running.FirstOrDefault(p=>p.AutoRecord);
        if(match==null){autoSuppressed=false;return;}
        if(engine.Running||engine.SessionRunning||autoSuppressed)return;
        var window=DesktopFeatures.GetWindows().FirstOrDefault(w=>w.ProcessName.Equals(GameProfiles.NormalizeExecutable(match.Executable),StringComparison.OrdinalIgnoreCase));if(window==null)return;
        var options=JsonSerializer.Deserialize<RecorderOptions>(match.OptionsJson)??settings.Recording;options.WindowHandle=window.Handle;
        busy=true;try{status.Text="Detected "+match.Name+" — starting…";if(match.FullSession){bookmarks.Clear();await engine.StartSessionAsync(options);}else await engine.StartAsync(options);activeOptions=options;settings.Recording=options;expectedRunning=true;autoOwned=true;activeAutoExecutable=GameProfiles.NormalizeExecutable(match.Executable);StartVoice();status.Text="Recording "+match.Name+(match.FullSession?" · full session":" · replay");}catch(Exception e){expectedRunning=false;StopVoice();autoSuppressed=true;recoveryBlocked=true;Error(e.Message);}finally{busy=false;}
    }
    void RefreshGames(){games.Items.Clear();foreach(var p in profiles.Profiles)games.Items.Add(p.Name+" ["+p.Executable+"] · "+(p.FullSession?"Full session":"Replay"));}
    void AddGame(){try{var c=target.SelectedItem as CaptureChoice;if(c==null||c.Handle==0)throw new Exception("Select a game's window in Record first.");var w=DesktopFeatures.GetWindows().FirstOrDefault(w=>w.Handle==c.Handle)??throw new Exception("That game window closed. Refresh windows.");profiles.Upsert(new(){Executable=w.ProcessName,Name=w.Title,AutoRecord=true,FullSession=gameFullSession.Checked,OptionsJson=JsonSerializer.Serialize(ReadOptions())});RefreshGames();notice.Text="Game profile added. Enable game detection in Hotkeys & startup to use it.";}catch(Exception e){Error(e.Message);}}
    void AddGameFile(){using var d=new OpenFileDialog{Filter="Game executable|*.exe"};if(d.ShowDialog(this)!=DialogResult.OK)return;try{var o=ReadOptions();o.WindowHandle=0;profiles.Upsert(new(){Executable=d.FileName,Name=Path.GetFileNameWithoutExtension(d.FileName),AutoRecord=true,FullSession=gameFullSession.Checked,OptionsJson=JsonSerializer.Serialize(o)});RefreshGames();}catch(Exception e){Error(e.Message);}}
    void LoadGame(){if(busy||engine.Running||engine.Saving||games.SelectedIndex<0)return;try{var profile=profiles.Profiles[games.SelectedIndex];var original=settings;settings=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;settings.Recording=JsonSerializer.Deserialize<RecorderOptions>(profile.OptionsJson)??new();try{LoadSettings();gameFullSession.Checked=profile.FullSession;RefreshTargets();var window=DesktopFeatures.GetWindows().FirstOrDefault(w=>w.ProcessName.Equals(GameProfiles.NormalizeExecutable(profile.Executable),StringComparison.OrdinalIgnoreCase));if(window!=null)for(int i=0;i<target.Items.Count;i++)if(target.Items[i] is CaptureChoice c&&c.Handle==window.Handle)target.SelectedIndex=i;}finally{settings=original;}tabs.SelectedIndex=1;notice.Text="Profile loaded. Apply settings to save it as your default.";}catch(Exception e){Error(e.Message);}}
    void RemoveGame(){if(games.SelectedIndex<0)return;profiles.Remove(profiles.Profiles[games.SelectedIndex].Executable);RefreshGames();}
    void Notify(string title,string text){notice.Text=title+": "+text;tray.ShowBalloonTip(2500,title,text,ToolTipIcon.Info);if(settings.SoundAlerts)SystemSounds.Asterisk.Play();}
    void Error(string text){notice.Text=text.Split('\n')[0];File.WriteAllText(Path.Combine(root,"diagnostics.txt"),text+"\n\n"+engine.Diagnostics);tray.ShowBalloonTip(5000,"GameReplay needs attention",notice.Text,ToolTipIcon.Warning);if(Visible)MessageBox.Show(this,text.Length>1600?text[..1600]:text,"GameReplay",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    void Diagnostics(){string file=Path.Combine(root,"diagnostics.txt");if(!File.Exists(file))File.WriteAllText(file,engine.Diagnostics);Open(file);}
    static void Open(string value)=>Process.Start(new ProcessStartInfo(value){UseShellExecute=true});
    void Restore(){Show();WindowState=FormWindowState.Normal;Activate();}
    async Task ExitApp(){if(busy||engine.Saving){Restore();notice.Text="Wait for the current save or setup to finish.";return;}busy=true;try{expectedRunning=false;if(engine.SessionRunning)await CompleteSession();await engine.StopAsync(true);StopVoice();timer.Stop();for(int i=1;i<=5;i++)HotkeyBinding.Unregister(Handle,i);tray.Visible=false;exiting=true;Close();}catch(Exception e){busy=false;Restore();Error("Exit paused to preserve unsaved footage: "+e.Message);}}
    public void RenderViews(string directory)
    {
        Directory.CreateDirectory(directory);
        for(int i=0;i<tabs.TabCount;i++){
            tabs.SelectedIndex=i;tabs.TabPages[i].AutoScrollPosition=Point.Empty;Application.DoEvents();
            using var bitmap=new Bitmap(Width,Height);DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));bitmap.Save(Path.Combine(directory,i==0?"ui.png":"ui-tab-"+i+".png"));
            var page=tabs.TabPages[i];if(page.VerticalScroll.Visible){page.AutoScrollPosition=new Point(0,page.VerticalScroll.Maximum);Application.DoEvents();using var bottom=new Bitmap(Width,Height);DrawToBitmap(bottom,new Rectangle(0,0,Width,Height));bottom.Save(Path.Combine(directory,"ui-tab-"+i+"-bottom.png"));}
        }
        tabs.SelectedIndex=0;
    }
    protected override void WndProc(ref Message m){if(m.Msg==0x0312){switch(m.WParam.ToInt32()){case 1:_=SaveClip();break;case 2:_=SaveClip(settings.ShortClipSeconds);break;case 3:_=ToggleSession();break;case 4:Screenshot();break;case 5:Bookmark();break;}}base.WndProc(ref m);}
    protected override void Dispose(bool disposing){if(disposing){timer.Dispose();tray.Dispose();voice?.Dispose();engine.Dispose();}base.Dispose(disposing);}
}
