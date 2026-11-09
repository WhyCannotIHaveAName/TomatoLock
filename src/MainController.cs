using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms=System.Windows.Forms;

namespace TomatoLock {
    public sealed class MainController : IDisposable {
        readonly Window window;
        readonly Store store;
        readonly AppPolicy policy=new AppPolicy();
        readonly DispatcherTimer timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };
        readonly Forms.NotifyIcon tray;
        Native.InputGate gate;
        double last, savedAt, renderedAt, probeAt, folderAt, chordAt=-1;
        string lastDate=DateTime.Now.ToString("yyyy-MM-dd");
        string site=""; IntPtr siteWindow; double siteAt;
        bool probeBusy, folderBusy, locked, disposed, exitRequested, updating;
        int consentGeneration;
        IntPtr handle;
        T C<T>(string name) where T:class { return window.FindName(name) as T; }
        void Click(string name, Action action) { C<Button>(name).Click+=(s,e)=>Run(action); }
        void Run(Action action) { try { action(); } catch(Exception e) { MessageBox.Show(window,e.Message,"TomatoLock",MessageBoxButton.OK,MessageBoxImage.Information); } }
        void Status(string text) { C<TextBlock>("Status").Text=text; }
        bool Active { get { return store.Data.Active!=null; } }
        public MainController(Window window, Store store) {
            this.window=window; this.store=store;
            window.SourceInitialized+=(s,e)=>{ handle=new WindowInteropHelper(window).Handle; };
            window.Closing+=(s,e)=>{
                if(Active) { e.Cancel=true; window.Hide(); tray.ShowBalloonTip(3000,"专注仍在继续","从托盘打开；提前结束请同时按住 Esc + Ctrl + Delete + Shift 1 秒。",Forms.ToolTipIcon.Info); }
                else if(!exitRequested && store.Data.Settings.TrackApps) { e.Cancel=true; window.Hide(); tray.ShowBalloonTip(2000,"后台统计中","右键托盘选择退出可停止统计。",Forms.ToolTipIcon.Info); }
            };
            var menu=new Forms.ContextMenuStrip(); menu.Items.Add("打开 TomatoLock",null,(s,e)=>Show());
            menu.Items.Add("退出",null,(s,e)=>{ if(Active) { Show(); Status("专注中：请使用四键组合提前结束。"); } else { exitRequested=true; window.Close(); } });
            tray=new Forms.NotifyIcon { Text="TomatoLock · 就绪", Icon=System.Drawing.Icon.ExtractAssociatedIcon(Program.Exe), Visible=true, ContextMenuStrip=menu };
            tray.DoubleClick+=(s,e)=>Show();
            Click("NavFocus",()=>Page("Focus")); Click("NavHistory",()=>Page("History")); Click("NavAchievements",()=>Page("Achievements")); Click("NavSettings",()=>Page("Settings"));
            foreach(Button b in C<WrapPanel>("Presets").Children) b.Click+=(s,e)=>{ if(!Active) C<TextBox>("CustomMinutes").Text=((Button)s).Tag.ToString(); Render(); };
            C<TextBox>("CustomMinutes").TextChanged+=(s,e)=>{ if(!Active) Render(); };
            Click("StartButton",Start);
            Click("OpenChat",()=>LaunchApp("ChatGPT")); Click("OpenWps",()=>LaunchApp("WPS"));
            Click("OpenFile",OpenFile); Click("OpenFolder",()=>OpenDirectory(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
            Click("DiscoverApps",Discover); Click("AddApp",AddApp); Click("SaveSettings",SaveSettings);
            Click("DataFolder",()=>OpenDirectory(store.Root)); Click("ReportsFolder",()=>OpenDirectory(Path.Combine(store.Root,"reports")));
            Click("MakeReport",()=>{ DateTime day=C<DatePicker>("ReportDate").SelectedDate ?? DateTime.Now; Reports.Generate(store,day.ToString("yyyy-MM-dd")); Status("已生成 "+day.ToString("yyyy-MM-dd")+" 的 Markdown 与 CSV 日报。"); History(); });
            Click("ClearUsage",()=>{
                if(Active) throw new InvalidOperationException("请在本次专注结束后修改数据。");
                if(MessageBox.Show(window,"删除全部应用 / 网站统计及生成的日报？专注记录与成就保留。","删除统计",MessageBoxButton.YesNo)!=MessageBoxResult.Yes) return;
                consentGeneration++; site=""; store.Data.Usage.Clear(); store.Save(); store.Save();
                string dir=Path.Combine(store.Root,"reports"); if(Directory.Exists(dir)) foreach(string f in Directory.GetFiles(dir)) {
                    string n=Path.GetFileName(f); DateTime d; if(n.Length>=10 && DateTime.TryParseExact(n.Substring(0,10),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out d) && (n.EndsWith(".md")||n.EndsWith(".csv")||n.EndsWith(".bak"))) File.Delete(f);
                }
                Status("应用 / 网站统计及日报已删除，专注记录保留。");
            });
            C<CheckBox>("TrackApps").Click+=(s,e)=>{ C<CheckBox>("TrackSites").IsEnabled=C<CheckBox>("TrackApps").IsChecked==true; if(C<CheckBox>("TrackApps").IsChecked!=true) C<CheckBox>("TrackSites").IsChecked=false; };
            C<DatePicker>("ReportDate").SelectedDate=DateTime.Now.Date;
            C<DatePicker>("ReportDate").SelectedDateChanged+=(s,e)=>History();
            SystemEvents.SessionSwitch+=SessionSwitch;
            last=savedAt=renderedAt=Native.AwakeSeconds();
            LoadSettings(); Render(); History(); Achievements();
            Run(()=>Reports.CatchUp(store));
            if(!String.IsNullOrEmpty(store.Notice)) Status(store.Notice);
            timer.Tick+=(s,e)=>Tick(); timer.Start();
        }
        void Show() { window.Show(); window.WindowState=WindowState.Normal; window.Activate(); }
        void Wake() { if(!Active) return; if(!window.IsVisible) window.Show(); if(window.WindowState==WindowState.Minimized) window.WindowState=WindowState.Normal; Native.SetForegroundWindow(handle); }
        void Page(string page) {
            foreach(string p in new [] {"Focus","History","Achievements","Settings"}) {
                C<ScrollViewer>(p+"Page").Visibility=p==page ? Visibility.Visible : Visibility.Collapsed;
                C<Button>("Nav"+p).Background=new SolidColorBrush(p==page ? Color.FromRgb(220,233,205) : Color.FromRgb(40,82,69));
                C<Button>("Nav"+p).Foreground=p==page ? new SolidColorBrush(Color.FromRgb(35,60,53)) : Brushes.White;
            }
            C<TextBlock>("PageTitle").Text=page=="Focus" ? "专注，从这一刻开始" : page=="History" ? "时间有迹可循" : page=="Achievements" ? "每一步，都算数" : "让专注适合你";
            C<TextBlock>("PageSubtitle").Text=page=="Focus" ? "选一个时长，把眼前的事情慢慢做好。" : page=="History" ? "在这里，看看时间去了哪里。" : page=="Achievements" ? "只和自己的昨天相比。" : "所有统计与开机自启，都由你决定。";
            if(page=="History") History(); if(page=="Achievements") Achievements();
        }
        int Minutes() { int n; return Int32.TryParse(C<TextBox>("CustomMinutes").Text,out n) && n>=1 && n<=720 ? n : 0; }
        void Start() {
            if(Active) return;
            int minutes=Minutes(); if(minutes==0) throw new InvalidOperationException("请输入 1–720 之间的整数分钟。");
            if(!store.Healthy) throw new IOException(store.Notice);
            policy.Configure(store.Data.Settings.Apps);
            if(MessageBox.Show(window,"即将专注 "+minutes+" 分钟。\n\n仅允许已确认的 ChatGPT、WPS、本地文件浏览和内置阅读器。其他前台应用会被拦截，未保存的内容不会被关闭。\n\n提前结束：同时按住 Esc + Ctrl + Delete + Shift 1 秒。专注期间 Esc 不会传给其他应用。睡眠 / 锁屏暂停计时。\n\n这是应用级限制，无法封锁 Windows 安全界面或强制结束进程。开始吗？","开始专注",MessageBoxButton.OKCancel,MessageBoxImage.Information)!=MessageBoxResult.OK) return;
            store.Data.Active=new Session { Id=Guid.NewGuid().ToString("N"), Started=DateTimeOffset.Now, TargetSeconds=minutes*60, Status="active" };
            try { store.Save(); gate=new Native.InputGate(policy.Allowed,()=>window.Dispatcher.BeginInvoke(new Action(Wake))); }
            catch { store.Data.Active=null; store.Save(); throw; }
            last=Native.AwakeSeconds(); chordAt=-1; Page("Focus"); Render(); SetEditable(false); Status("正在专注 · Esc + Ctrl + Delete + Shift 同时按住 1 秒可提前结束。");
        }
        void Finish(string status) {
            if(!Active) return;
            if(gate!=null) { gate.Dispose(); gate=null; }
            Session session=store.Data.Active; session.Status=status; session.Ended=DateTimeOffset.Now;
            store.Data.Active=null; store.Data.Sessions.Add(session); chordAt=-1;
            SetEditable(true); store.Save(); Reports.Generate(store,session.Ended.ToString("yyyy-MM-dd")); Render();
            if(status=="completed") {
                System.Media.SystemSounds.Asterisk.Play(); Show();
                Status("这一段专注完成了："+Logic.Duration(session.ElapsedSeconds)+"。起来活动一下吧。");
                tray.ShowBalloonTip(5000,"专注完成", "已记录 "+Logic.Duration(session.ElapsedSeconds)+"，辛苦了。", Forms.ToolTipIcon.Info);
            } else Status("已解锁；本次实际专注 "+Logic.Duration(session.ElapsedSeconds)+" 已保留，不计入完成成就。");
        }
        void SetEditable(bool value) {
            foreach(string name in new [] {"DiscoverApps","AddApp","SaveSettings","ClearUsage"}) C<Button>(name).IsEnabled=value;
            C<StackPanel>("AllowedRows").IsEnabled=value;
            foreach(string name in new [] {"TrackApps","TrackSites","Startup"}) C<CheckBox>(name).IsEnabled=value;
            C<TextBox>("IdleLimit").IsEnabled=value;
            if(value) C<CheckBox>("TrackSites").IsEnabled=C<CheckBox>("TrackApps").IsChecked==true;
        }
        void Tick() {
            double now=Native.AwakeSeconds(); double delta=Logic.SafeDelta(last,now); last=now;
            if(Active) {
                policy.Refresh();
                if(gate!=null && gate.Chord) {
                    if(chordAt<0) chordAt=now;
                    if(now-chordAt>=1) { Finish("unlocked"); return; }
                    Status("四键解锁中…请保持按住。");
                } else chordAt=-1;
                if(!locked) { Logic.Advance(store.Data.Active,delta); if(store.Data.Active.ElapsedSeconds>=store.Data.Active.TargetSeconds) { Finish("completed"); return; } }
                if(!locked) {
                    IntPtr foreground=Native.GetForegroundWindow();
                    if(!policy.Allowed(foreground)) { Wake(); }
                }
                if(now-folderAt>=1 && !folderBusy) { folderAt=now; Folders(); }
            }
            Track(now,delta);
            if(now-renderedAt>=0.5) { renderedAt=now; Render(); }
            if(now-savedAt>=5) { savedAt=now; if(store.Healthy && (Active || store.Data.Settings.TrackApps)) store.Save(); }
            string date=DateTime.Now.ToString("yyyy-MM-dd");
            if(date!=lastDate) { Reports.Generate(store,lastDate); Reports.CatchUp(store); lastDate=date; }
        }
        void SessionSwitch(object sender,SessionSwitchEventArgs e) { window.Dispatcher.BeginInvoke(new Action(()=>{
            if(gate!=null)gate.Reset(); chordAt=-1;
            if(e.Reason==SessionSwitchReason.SessionLock || e.Reason==SessionSwitchReason.SessionLogoff || e.Reason==SessionSwitchReason.RemoteDisconnect || e.Reason==SessionSwitchReason.ConsoleDisconnect) locked=true;
            if(e.Reason==SessionSwitchReason.SessionUnlock || e.Reason==SessionSwitchReason.SessionLogon || e.Reason==SessionSwitchReason.RemoteConnect || e.Reason==SessionSwitchReason.ConsoleConnect) locked=false;
            last=Native.AwakeSeconds(); if(store.Healthy) store.Save();
        })); }
        void Track(double now,double delta) {
            if(!store.Data.Settings.TrackApps || locked || Native.IdleSeconds()>=store.Data.Settings.IdleSeconds) { site=""; return; }
            IntPtr foreground=Native.GetForegroundWindow(); string path=Native.ProcessPath(foreground); if(String.IsNullOrEmpty(path)) return;
            string name=Path.GetFileName(path);
            Logic.AddUsage(store.Data,DateTimeOffset.Now,delta,"app",name);
            if(!store.Data.Settings.TrackSites || !Native.Browser(path)) { site=""; return; }
            string host=siteWindow==foreground && now-siteAt<3 && !String.IsNullOrEmpty(site) ? site : "未识别网站（"+name+"）";
            Logic.AddUsage(store.Data,DateTimeOffset.Now,delta,"site",host);
            if(now-probeAt>=1.5 && !probeBusy) {
                probeAt=now; probeBusy=true; int gen=consentGeneration;
                Probe("--probe "+foreground.ToInt64(),result=>{ probeBusy=false; if(disposed || gen!=consentGeneration || !store.Data.Settings.TrackSites) return; site=result; siteWindow=foreground; siteAt=Native.AwakeSeconds(); });
            }
        }
        void Folders() { folderBusy=true; Probe("--folders",result=>{ folderBusy=false; if(!disposed) policy.SetLocalFolders(result); }); }
        void Probe(string args,Action<string> done) {
            Task.Factory.StartNew(()=>{
                try { using(var p=new Process { StartInfo=new ProcessStartInfo(Program.Exe,args) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, StandardOutputEncoding=System.Text.Encoding.UTF8 } }) {
                    p.Start(); if(!p.WaitForExit(1300)) { p.Kill(); return ""; } return p.StandardOutput.ReadToEnd().Trim();
                } } catch { return ""; }
            }).ContinueWith(t=>{ if(!disposed) window.Dispatcher.BeginInvoke(new Action(()=>done(t.Result))); });
        }
        void Render() {
            if(disposed) return;
            C<TextBlock>("LocalClock").Text=DateTime.Now.ToString("yyyy / MM / dd\nHH:mm:ss");
            double seconds=Active ? Math.Ceiling(store.Data.Active.TargetSeconds-store.Data.Active.ElapsedSeconds) : Minutes()*60;
            TimeSpan remaining=TimeSpan.FromSeconds(Math.Max(0,seconds));
            C<TextBlock>("Countdown").Text=((int)remaining.TotalMinutes).ToString("00")+":"+remaining.Seconds.ToString("00");
            C<TextBlock>("SessionLabel").Text=Active ? locked ? "系统已锁屏 · 计时暂停" : "专注进行中，慢慢来" : "给专注留一块安静的地方";
            C<TextBlock>("EndEstimate").Text=Active ? "预计 "+DateTime.Now.AddSeconds(seconds).ToString("HH:mm")+" 完成 · 睡眠与锁屏顺延" : "准备好了，就开始吧";
            C<ProgressBar>("TimerProgress").Value=Active ? store.Data.Active.ElapsedSeconds/store.Data.Active.TargetSeconds*100 : 0;
            C<Button>("StartButton").IsEnabled=!Active && store.Healthy; C<Button>("StartButton").Content=Active ? "正在专注" : "开始专注";
            C<TextBox>("CustomMinutes").IsEnabled=!Active; C<WrapPanel>("Presets").IsEnabled=!Active;
            var done=store.Data.Sessions.Where(x=>x.Status=="completed" && x.Ended.Date==DateTime.Now.Date).ToList();
            C<TextBlock>("TodaySummary").Text="今天已完成 "+done.Count+" 次专注 · 共 "+Logic.Duration(done.Sum(x=>x.ElapsedSeconds));
            tray.Text=Active ? "TomatoLock · 剩余 "+C<TextBlock>("Countdown").Text : "TomatoLock · "+(store.Data.Settings.TrackApps ? "后台统计中" : "就绪");
        }
        TextBlock Text(string text,int size=14) { return new TextBlock { Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,7) }; }
        Border Card(UIElement child) { return new Border { Child=child, Background=Brushes.White,CornerRadius=new CornerRadius(14),Padding=new Thickness(22),Margin=new Thickness(0,0,0,12) }; }
        void History() {
            var done=store.Data.Sessions.Where(x=>x.Status=="completed").ToList();
            C<TextBlock>("Totals").Text="累计专注 "+Logic.Duration(done.Sum(x=>x.ElapsedSeconds))+"   /   "+done.Count+" 次完成";
            var rows=C<StackPanel>("HistoryRows"); rows.Children.Clear();
            foreach(var s in store.Data.Sessions.OrderByDescending(x=>x.Started).Take(100)) { var panel=new StackPanel(); panel.Children.Add(Text(s.Started.ToString("MM月dd日 HH:mm")+"     "+Logic.StatusName(s.Status),16)); panel.Children.Add(Text("实际 "+Logic.Duration(s.ElapsedSeconds)+"   /   目标 "+Logic.Duration(s.TargetSeconds))); rows.Children.Add(Card(panel)); }
            if(store.Data.Sessions.Count==0) rows.Children.Add(Text("还没有记录。第一段专注，从今天开始。"));
            if(store.Data.Sessions.Count>100) rows.Children.Add(Text("显示最近 100 条，全部记录仍保存在本机，可按日期导出日报。"));
            var usage=C<StackPanel>("UsageRows"); usage.Children.Clear();
            string date=(C<DatePicker>("ReportDate").SelectedDate??DateTime.Now).ToString("yyyy-MM-dd");
            foreach(string kind in new [] {"app","site"}) {
                var list=store.Data.Usage.Where(x=>x.Date==date && x.Kind==kind).OrderByDescending(x=>x.Seconds).Take(8).ToList();
                if(list.Count==0) continue; usage.Children.Add(Text(kind=="app"?"应用活跃时长 · 前 8 项":"网站活跃时长 · 前 8 项",16));
                foreach(var r in list) { usage.Children.Add(Text(r.Name+"   "+Logic.Duration(r.Seconds),12)); usage.Children.Add(new ProgressBar { Value=r.Seconds/list[0].Seconds*100,Height=4,Margin=new Thickness(0,0,0,12),Foreground=new SolidColorBrush(Color.FromRgb(100,141,104)) }); }
            }
            if(usage.Children.Count==0) usage.Children.Add(Text("所选日期暂无应用统计。可在偏好设置中自愿开启。",12));
        }
        void Achievements() {
            var rows=C<StackPanel>("AchievementRows"); rows.Children.Clear();
            foreach(var a in Logic.Achievements(store.Data)) {
                var panel=new StackPanel(); panel.Children.Add(Text((a.Unlocked ? "★  " : "☆  ")+a.Name,19)); panel.Children.Add(Text(a.Description+"  ·  "+(a.Unlocked ? "已达成" : Math.Min(a.Value,a.Target).ToString("0.#")+" / "+a.Target),13));
                panel.Children.Add(new ProgressBar { Value=Math.Min(100,a.Value/a.Target*100),Height=5,Foreground=new SolidColorBrush(Color.FromRgb(100,141,104)) }); rows.Children.Add(Card(panel));
            }
        }
        void LoadSettings() {
            updating=true; var s=store.Data.Settings;
            C<CheckBox>("TrackApps").IsChecked=s.TrackApps; C<CheckBox>("TrackSites").IsChecked=s.TrackSites; C<CheckBox>("TrackSites").IsEnabled=s.TrackApps;
            C<CheckBox>("Startup").IsChecked=s.Startup; C<TextBox>("IdleLimit").Text=s.IdleSeconds.ToString(); updating=false; AllowedRows();
        }
        void SaveSettings() {
            if(Active || updating) return;
            int idle; if(!Int32.TryParse(C<TextBox>("IdleLimit").Text,out idle) || idle<30 || idle>3600) throw new InvalidOperationException("空闲阈值应为 30–3600 秒。");
            bool apps=C<CheckBox>("TrackApps").IsChecked==true, sites=apps && C<CheckBox>("TrackSites").IsChecked==true, startup=C<CheckBox>("Startup").IsChecked==true;
            Program.SetStartup(startup);
            var s=store.Data.Settings; s.TrackApps=apps; s.TrackSites=sites; s.Startup=startup; s.IdleSeconds=idle;
            consentGeneration++; site=""; store.Save(); Status("偏好已保存 · "+(apps ? "应用统计已开启" : "使用统计已关闭")+(sites ? " · 域名统计已开启" : ""));
        }
        void AllowedRows() {
            var rows=C<StackPanel>("AllowedRows"); rows.Children.Clear();
            foreach(var a in store.Data.Settings.Apps.ToList()) {
                var panel=new StackPanel(); panel.Children.Add(Text(a.Name,14)); panel.Children.Add(Text(a.Path,11));
                var button=new Button { Content="移除",HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(10,5,10,5) };
                button.Click+=(s,e)=>Run(()=>{ if(Active)return; store.Data.Settings.Apps.Remove(a); store.Save(); AllowedRows(); }); panel.Children.Add(button); rows.Children.Add(panel);
            }
            if(rows.Children.Count==0) rows.Children.Add(Text("尚未确认应用路径；仍可使用本地文本 / 图片阅读器。",12));
        }
        void Register(string path) {
            string name=AppPolicy.Qualify(path);
            store.Data.Settings.Apps.RemoveAll(x=>String.Equals(x.Path,path,StringComparison.OrdinalIgnoreCase));
            store.Data.Settings.Apps.Add(new AllowedApp { Name=name,Path=path,Sha256=Native.Hash(path) });
        }
        void Discover() {
            if(Active)return;
            var paths=AppPolicy.Discover();
            if(paths.Count==0) throw new InvalidOperationException("没有找到应用。请先打开 ChatGPT / WPS，再试一次；也可手动选择 exe 文件。");
            if(MessageBox.Show(window,"是否确认以下是你信任的 ChatGPT / WPS 程序？\n\n"+String.Join("\n",paths),"确认允许的应用",MessageBoxButton.OKCancel)!=MessageBoxResult.OK) return;
            foreach(string p in paths) Register(p); store.Save(); AllowedRows(); Status("已确认 "+paths.Count+" 个程序。应用更新后可再次识别。");
        }
        void AddApp() {
            if(Active)return; var d=new OpenFileDialog { Title="选择已安装的 ChatGPT / WPS 程序",Filter="应用程序|*.exe" };
            if(d.ShowDialog(window)==true) { AppPolicy.Qualify(d.FileName); if(MessageBox.Show(window,"确认信任这个程序？\n"+d.FileName,"确认程序",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return; Register(d.FileName); store.Save(); AllowedRows(); }
        }
        void LaunchApp(string prefix) {
            var app=store.Data.Settings.Apps.FirstOrDefault(x=>x.Name.StartsWith(prefix));
            if(app==null) throw new InvalidOperationException("请先在偏好设置中识别或选择 "+prefix+" 的安装路径。");
            if(Native.Hash(app.Path)!=app.Sha256) throw new InvalidOperationException("应用已更新，请在专注结束后重新确认路径。");
            Process.Start(new ProcessStartInfo(app.Path) { UseShellExecute=true });
        }
        void OpenDirectory(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe","\""+path+"\"") { UseShellExecute=true }); if(Active) Folders(); }
        void OpenFile() {
            var d=new OpenFileDialog { Title="阅读本地文件",Filter="支持的文件|*.txt;*.md;*.csv;*.json;*.log;*.cs;*.cpp;*.h;*.py;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx|所有文件|*.*" };
            if(d.ShowDialog(window)!=true)return;
            Uri uri=new Uri(d.FileName); var drive=new DriveInfo(Path.GetPathRoot(d.FileName));
            if(uri.IsUnc || drive.DriveType==DriveType.Network) throw new InvalidOperationException("专注阅读器只打开本地磁盘文件。");
            string ext=Path.GetExtension(d.FileName).ToLowerInvariant();
            if(new [] {".pdf",".doc",".docx",".xls",".xlsx",".ppt",".pptx"}.Contains(ext)) {
                string preferred=ext==".pdf"?"wpspdf":ext.StartsWith(".xls")?"et":ext.StartsWith(".ppt")?"wpp":"wps";
                var app=store.Data.Settings.Apps.FirstOrDefault(x=>Path.GetFileNameWithoutExtension(x.Path).Equals(preferred,StringComparison.OrdinalIgnoreCase)) ?? store.Data.Settings.Apps.FirstOrDefault(x=>x.Name.StartsWith("WPS"));
                if(app==null) throw new InvalidOperationException("这类文档请先配置 WPS，然后再打开。");
                if(Native.Hash(app.Path)!=app.Sha256) throw new InvalidOperationException("WPS 已更新，请重新确认。");
                Process.Start(new ProcessStartInfo(app.Path,"\""+d.FileName+"\"") { UseShellExecute=true }); return;
            }
            var reader=new Window { Title=Path.GetFileName(d.FileName)+" · 本地阅读",Width=900,Height=700,Owner=window,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brushes.White };
            if(new [] {".png",".jpg",".jpeg",".bmp",".gif"}.Contains(ext)) {
                if(new FileInfo(d.FileName).Length>25*1024*1024) throw new InvalidOperationException("图片超过 25 MB，请使用 WPS 查看。");
                var bitmap=new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption=BitmapCacheOption.OnLoad; bitmap.UriSource=uri; bitmap.DecodePixelWidth=2400; bitmap.EndInit(); bitmap.Freeze();
                reader.Content=new ScrollViewer { Content=new Image { Source=bitmap,Stretch=Stretch.Uniform },VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
            } else {
                if(!new [] {".txt",".md",".csv",".json",".log",".cs",".cpp",".h",".py"}.Contains(ext)) throw new InvalidOperationException("该格式尚不支持内置阅读。文本、图片可直接读，Office / PDF 请通过 WPS 打开。");
                if(new FileInfo(d.FileName).Length>5*1024*1024) throw new InvalidOperationException("文本超过 5 MB，请用 WPS 打开。");
                reader.Content=new TextBox { Text=File.ReadAllText(d.FileName),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(24),FontSize=16,BorderThickness=new Thickness(0) };
            }
            reader.Show();
        }
        public void FailSafe(Exception error) {
            if(gate!=null) { gate.Dispose(); gate=null; }
            timer.Stop();
            if(Active) { var s=store.Data.Active; s.Status="interrupted"; s.Ended=DateTimeOffset.Now; store.Data.Sessions.Add(s); store.Data.Active=null; }
            try { store.Save(); } catch {}
            SetEditable(false); C<Button>("StartButton").IsEnabled=false;
            Show(); Status("出现错误，已解除输入限制。请退出后重开。记录路径："+store.Root);
            MessageBox.Show(window,"已解除限制以避免锁死。\n"+error.Message,"TomatoLock",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        public void Dispose() {
            if(disposed)return; disposed=true; timer.Stop(); if(gate!=null)gate.Dispose();
            SystemEvents.SessionSwitch-=SessionSwitch; tray.Visible=false; tray.Dispose();
            try { if(store.Healthy) { store.Save(); Reports.Generate(store,DateTime.Now.ToString("yyyy-MM-dd")); } } catch {}
        }
    }
}
