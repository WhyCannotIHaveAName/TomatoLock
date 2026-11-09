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
                if(Active) { e.Cancel=true; window.Hide(); tray.ShowBalloonTip(3000,"רע���ڼ���","�����̴򿪣���ǰ������ͬʱ��ס Esc + Ctrl + Delete + Shift 1 �롣",Forms.ToolTipIcon.Info); }
                else if(!exitRequested && store.Data.Settings.TrackApps) { e.Cancel=true; window.Hide(); tray.ShowBalloonTip(2000,"��̨ͳ����","�Ҽ�����ѡ���˳���ֹͣͳ�ơ�",Forms.ToolTipIcon.Info); }
            };
            var menu=new Forms.ContextMenuStrip(); menu.Items.Add("�� TomatoLock",null,(s,e)=>Show());
            menu.Items.Add("�˳�",null,(s,e)=>{ if(Active) { Show(); Status("רע�У���ʹ���ļ������ǰ������"); } else { exitRequested=true; window.Close(); } });
            tray=new Forms.NotifyIcon { Text="TomatoLock �� ����", Icon=System.Drawing.Icon.ExtractAssociatedIcon(Program.Exe), Visible=true, ContextMenuStrip=menu };
            tray.DoubleClick+=(s,e)=>Show();
            Click("NavFocus",()=>Page("Focus")); Click("NavHistory",()=>Page("History")); Click("NavAchievements",()=>Page("Achievements")); Click("NavSettings",()=>Page("Settings"));
            foreach(Button b in C<WrapPanel>("Presets").Children) b.Click+=(s,e)=>{ if(!Active) C<TextBox>("CustomMinutes").Text=((Button)s).Tag.ToString(); Render(); };
            C<TextBox>("CustomMinutes").TextChanged+=(s,e)=>{ if(!Active) Render(); };
            Click("StartButton",Start);
            Click("OpenChat",()=>LaunchApp("ChatGPT")); Click("OpenWps",()=>LaunchApp("WPS"));
            Click("OpenFile",OpenFile); Click("OpenFolder",()=>OpenDirectory(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
            Click("DiscoverApps",Discover); Click("AddApp",AddApp); Click("SaveSettings",SaveSettings);
            Click("DataFolder",()=>OpenDirectory(store.Root)); Click("ReportsFolder",()=>OpenDirectory(Path.Combine(store.Root,"reports")));
            Click("MakeReport",()=>{ DateTime day=C<DatePicker>("ReportDate").SelectedDate ?? DateTime.Now; Reports.Generate(store,day.ToString("yyyy-MM-dd")); Status("������ "+day.ToString("yyyy-MM-dd")+" �� Markdown �� CSV �ձ���"); History(); });
            Click("ClearUsage",()=>{
                if(Active) throw new InvalidOperationException("���ڱ���רע�������޸����ݡ�");
                if(MessageBox.Show(window,"ɾ��ȫ��Ӧ�� / ��վͳ�Ƽ����ɵ��ձ���רע��¼��ɾͱ�����","ɾ��ͳ��",MessageBoxButton.YesNo)!=MessageBoxResult.Yes) return;
                consentGeneration++; site=""; store.Data.Usage.Clear(); store.Save(); store.Save();
                string dir=Path.Combine(store.Root,"reports"); if(Directory.Exists(dir)) foreach(string f in Directory.GetFiles(dir)) {
                    string n=Path.GetFileName(f); DateTime d; if(n.Length>=10 && DateTime.TryParseExact(n.Substring(0,10),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out d) && (n.EndsWith(".md")||n.EndsWith(".csv")||n.EndsWith(".bak"))) File.Delete(f);
                }
                Status("Ӧ�� / ��վͳ�Ƽ��ձ���ɾ����רע��¼������");
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
            C<TextBlock>("PageTitle").Text=page=="Focus" ? "רע������һ�̿�ʼ" : page=="History" ? "ʱ���м���ѭ" : page=="Achievements" ? "ÿһ����������" : "��רע�ʺ���";
            C<TextBlock>("PageSubtitle").Text=page=="Focus" ? "ѡһ��ʱ��������ǰ�������������á�" : page=="History" ? "���������ʱ��ȥ�����" : page=="Achievements" ? "ֻ���Լ���������ȡ�" : "����ͳ���뿪�������������������";
            if(page=="History") History(); if(page=="Achievements") Achievements();
        }
        int Minutes() { int n; return Int32.TryParse(C<TextBox>("CustomMinutes").Text,out n) && n>=1 && n<=720 ? n : 0; }
        void Start() {
            if(Active) return;
            int minutes=Minutes(); if(minutes==0) throw new InvalidOperationException("������ 1�C720 ֮����������ӡ�");
            if(!store.Healthy) throw new IOException(store.Notice);
            policy.Configure(store.Data.Settings.Apps);
            if(MessageBox.Show(window,"����רע "+minutes+" ���ӡ�\n\n��������ȷ�ϵ� ChatGPT��WPS�������ļ�����������Ķ���������ǰ̨Ӧ�ûᱻ���أ�δ��������ݲ��ᱻ�رա�\n\n��ǰ������ͬʱ��ס Esc + Ctrl + Delete + Shift 1 �롣רע�ڼ� Esc ���ᴫ������Ӧ�á�˯�� / ������ͣ��ʱ��\n\n����Ӧ�ü����ƣ��޷����� Windows ��ȫ�����ǿ�ƽ������̡���ʼ��","��ʼרע",MessageBoxButton.OKCancel,MessageBoxImage.Information)!=MessageBoxResult.OK) return;
            store.Data.Active=new Session { Id=Guid.NewGuid().ToString("N"), Started=DateTimeOffset.Now, TargetSeconds=minutes*60, Status="active" };
            try { store.Save(); gate=new Native.InputGate(policy.Allowed,()=>window.Dispatcher.BeginInvoke(new Action(Wake))); }
            catch { store.Data.Active=null; store.Save(); throw; }
            last=Native.AwakeSeconds(); chordAt=-1; Page("Focus"); Render(); SetEditable(false); Status("����רע �� Esc + Ctrl + Delete + Shift ͬʱ��ס 1 �����ǰ������");
        }
        void Finish(string status) {
            if(!Active) return;
            if(gate!=null) { gate.Dispose(); gate=null; }
            Session session=store.Data.Active; session.Status=status; session.Ended=DateTimeOffset.Now;
            store.Data.Active=null; store.Data.Sessions.Add(session); chordAt=-1;
            SetEditable(true); store.Save(); Reports.Generate(store,session.Ended.ToString("yyyy-MM-dd")); Render();
            if(status=="completed") {
                System.Media.SystemSounds.Asterisk.Play(); Show();
                Status("��һ��רע����ˣ�"+Logic.Duration(session.ElapsedSeconds)+"�������һ�°ɡ�");
                tray.ShowBalloonTip(5000,"רע���", "�Ѽ�¼ "+Logic.Duration(session.ElapsedSeconds)+"�������ˡ�", Forms.ToolTipIcon.Info);
            } else Status("�ѽ���������ʵ��רע "+Logic.Duration(session.ElapsedSeconds)+" �ѱ�������������ɳɾ͡�");
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
                    Status("�ļ������С��뱣�ְ�ס��");
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
            string host=siteWindow==foreground && now-siteAt<3 && !String.IsNullOrEmpty(site) ? site : "δʶ����վ��"+name+"��";
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
            C<TextBlock>("SessionLabel").Text=Active ? locked ? "ϵͳ������ �� ��ʱ��ͣ" : "רע�����У�������" : "��רע��һ�鰲���ĵط�";
            C<TextBlock>("EndEstimate").Text=Active ? "Ԥ�� "+DateTime.Now.AddSeconds(seconds).ToString("HH:mm")+" ��� �� ˯��������˳��" : "׼�����ˣ��Ϳ�ʼ��";
            C<ProgressBar>("TimerProgress").Value=Active ? store.Data.Active.ElapsedSeconds/store.Data.Active.TargetSeconds*100 : 0;
            C<Button>("StartButton").IsEnabled=!Active && store.Healthy; C<Button>("StartButton").Content=Active ? "����רע" : "��ʼרע";
            C<TextBox>("CustomMinutes").IsEnabled=!Active; C<WrapPanel>("Presets").IsEnabled=!Active;
            var done=store.Data.Sessions.Where(x=>x.Status=="completed" && x.Ended.Date==DateTime.Now.Date).ToList();
            C<TextBlock>("TodaySummary").Text="��������� "+done.Count+" ��רע �� �� "+Logic.Duration(done.Sum(x=>x.ElapsedSeconds));
            tray.Text=Active ? "TomatoLock �� ʣ�� "+C<TextBlock>("Countdown").Text : "TomatoLock �� "+(store.Data.Settings.TrackApps ? "��̨ͳ����" : "����");
        }
        TextBlock Text(string text,int size=14) { return new TextBlock { Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,7) }; }
        Border Card(UIElement child) { return new Border { Child=child, Background=Brushes.White,CornerRadius=new CornerRadius(14),Padding=new Thickness(22),Margin=new Thickness(0,0,0,12) }; }
        void History() {
            var done=store.Data.Sessions.Where(x=>x.Status=="completed").ToList();
            C<TextBlock>("Totals").Text="�ۼ�רע "+Logic.Duration(done.Sum(x=>x.ElapsedSeconds))+"   /   "+done.Count+" �����";
            var rows=C<StackPanel>("HistoryRows"); rows.Children.Clear();
            foreach(var s in store.Data.Sessions.OrderByDescending(x=>x.Started).Take(100)) { var panel=new StackPanel(); panel.Children.Add(Text(s.Started.ToString("MM��dd�� HH:mm")+"     "+Logic.StatusName(s.Status),16)); panel.Children.Add(Text("ʵ�� "+Logic.Duration(s.ElapsedSeconds)+"   /   Ŀ�� "+Logic.Duration(s.TargetSeconds))); rows.Children.Add(Card(panel)); }
            if(store.Data.Sessions.Count==0) rows.Children.Add(Text("��û�м�¼����һ��רע���ӽ��쿪ʼ��"));
            if(store.Data.Sessions.Count>100) rows.Children.Add(Text("��ʾ��� 100 ����ȫ����¼�Ա����ڱ������ɰ����ڵ����ձ���"));
            var usage=C<StackPanel>("UsageRows"); usage.Children.Clear();
            string date=(C<DatePicker>("ReportDate").SelectedDate??DateTime.Now).ToString("yyyy-MM-dd");
            foreach(string kind in new [] {"app","site"}) {
                var list=store.Data.Usage.Where(x=>x.Date==date && x.Kind==kind).OrderByDescending(x=>x.Seconds).Take(8).ToList();
                if(list.Count==0) continue; usage.Children.Add(Text(kind=="app"?"Ӧ�û�Ծʱ�� �� ǰ 8 ��":"��վ��Ծʱ�� �� ǰ 8 ��",16));
                foreach(var r in list) { usage.Children.Add(Text(r.Name+"   "+Logic.Duration(r.Seconds),12)); usage.Children.Add(new ProgressBar { Value=r.Seconds/list[0].Seconds*100,Height=4,Margin=new Thickness(0,0,0,12),Foreground=new SolidColorBrush(Color.FromRgb(100,141,104)) }); }
            }
            if(usage.Children.Count==0) usage.Children.Add(Text("��ѡ��������Ӧ��ͳ�ơ�����ƫ����������Ը������",12));
        }
        void Achievements() {
            var rows=C<StackPanel>("AchievementRows"); rows.Children.Clear();
            foreach(var a in Logic.Achievements(store.Data)) {
                var panel=new StackPanel(); panel.Children.Add(Text((a.Unlocked ? "��  " : "��  ")+a.Name,19)); panel.Children.Add(Text(a.Description+"  ��  "+(a.Unlocked ? "�Ѵ��" : Math.Min(a.Value,a.Target).ToString("0.#")+" / "+a.Target),13));
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
            int idle; if(!Int32.TryParse(C<TextBox>("IdleLimit").Text,out idle) || idle<30 || idle>3600) throw new InvalidOperationException("������ֵӦΪ 30�C3600 �롣");
            bool apps=C<CheckBox>("TrackApps").IsChecked==true, sites=apps && C<CheckBox>("TrackSites").IsChecked==true, startup=C<CheckBox>("Startup").IsChecked==true;
            Program.SetStartup(startup);
            var s=store.Data.Settings; s.TrackApps=apps; s.TrackSites=sites; s.Startup=startup; s.IdleSeconds=idle;
            consentGeneration++; site=""; store.Save(); Status("ƫ���ѱ��� �� "+(apps ? "Ӧ��ͳ���ѿ���" : "ʹ��ͳ���ѹر�")+(sites ? " �� ����ͳ���ѿ���" : ""));
        }
        void AllowedRows() {
            var rows=C<StackPanel>("AllowedRows"); rows.Children.Clear();
            foreach(var a in store.Data.Settings.Apps.ToList()) {
                var panel=new StackPanel(); panel.Children.Add(Text(a.Name,14)); panel.Children.Add(Text(a.Path,11));
                var button=new Button { Content="�Ƴ�",HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(10,5,10,5) };
                button.Click+=(s,e)=>Run(()=>{ if(Active)return; store.Data.Settings.Apps.Remove(a); store.Save(); AllowedRows(); }); panel.Children.Add(button); rows.Children.Add(panel);
            }
            if(rows.Children.Count==0) rows.Children.Add(Text("��δȷ��Ӧ��·�����Կ�ʹ�ñ����ı� / ͼƬ�Ķ�����",12));
        }
        void Register(string path) {
            string name=AppPolicy.Qualify(path);
            store.Data.Settings.Apps.RemoveAll(x=>String.Equals(x.Path,path,StringComparison.OrdinalIgnoreCase));
            store.Data.Settings.Apps.Add(new AllowedApp { Name=name,Path=path,Sha256=Native.Hash(path) });
        }
        void Discover() {
            if(Active)return;
            var paths=AppPolicy.Discover();
            if(paths.Count==0) throw new InvalidOperationException("û���ҵ�Ӧ�á����ȴ� ChatGPT / WPS������һ�Σ�Ҳ���ֶ�ѡ�� exe �ļ���");
            if(MessageBox.Show(window,"�Ƿ�ȷ�������������ε� ChatGPT / WPS ����\n\n"+String.Join("\n",paths),"ȷ��������Ӧ��",MessageBoxButton.OKCancel)!=MessageBoxResult.OK) return;
            foreach(string p in paths) Register(p); store.Save(); AllowedRows(); Status("��ȷ�� "+paths.Count+" ������Ӧ�ø��º���ٴ�ʶ��");
        }
        void AddApp() {
            if(Active)return; var d=new OpenFileDialog { Title="ѡ���Ѱ�װ�� ChatGPT / WPS ����",Filter="Ӧ�ó���|*.exe" };
            if(d.ShowDialog(window)==true) { AppPolicy.Qualify(d.FileName); if(MessageBox.Show(window,"ȷ�������������\n"+d.FileName,"ȷ�ϳ���",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return; Register(d.FileName); store.Save(); AllowedRows(); }
        }
        void LaunchApp(string prefix) {
            var app=store.Data.Settings.Apps.FirstOrDefault(x=>x.Name.StartsWith(prefix));
            if(app==null) throw new InvalidOperationException("������ƫ��������ʶ���ѡ�� "+prefix+" �İ�װ·����");
            if(Native.Hash(app.Path)!=app.Sha256) throw new InvalidOperationException("Ӧ���Ѹ��£�����רע����������ȷ��·����");
            Process.Start(new ProcessStartInfo(app.Path) { UseShellExecute=true });
        }
        void OpenDirectory(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe","\""+path+"\"") { UseShellExecute=true }); if(Active) Folders(); }
        void OpenFile() {
            var d=new OpenFileDialog { Title="�Ķ������ļ�",Filter="֧�ֵ��ļ�|*.txt;*.md;*.csv;*.json;*.log;*.cs;*.cpp;*.h;*.py;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx|�����ļ�|*.*" };
            if(d.ShowDialog(window)!=true)return;
            Uri uri=new Uri(d.FileName); var drive=new DriveInfo(Path.GetPathRoot(d.FileName));
            if(uri.IsUnc || drive.DriveType==DriveType.Network) throw new InvalidOperationException("רע�Ķ���ֻ�򿪱��ش����ļ���");
            string ext=Path.GetExtension(d.FileName).ToLowerInvariant();
            if(new [] {".pdf",".doc",".docx",".xls",".xlsx",".ppt",".pptx"}.Contains(ext)) {
                string preferred=ext==".pdf"?"wpspdf":ext.StartsWith(".xls")?"et":ext.StartsWith(".ppt")?"wpp":"wps";
                var app=store.Data.Settings.Apps.FirstOrDefault(x=>Path.GetFileNameWithoutExtension(x.Path).Equals(preferred,StringComparison.OrdinalIgnoreCase)) ?? store.Data.Settings.Apps.FirstOrDefault(x=>x.Name.StartsWith("WPS"));
                if(app==null) throw new InvalidOperationException("�����ĵ��������� WPS��Ȼ���ٴ򿪡�");
                if(Native.Hash(app.Path)!=app.Sha256) throw new InvalidOperationException("WPS �Ѹ��£�������ȷ�ϡ�");
                Process.Start(new ProcessStartInfo(app.Path,"\""+d.FileName+"\"") { UseShellExecute=true }); return;
            }
            var reader=new Window { Title=Path.GetFileName(d.FileName)+" �� �����Ķ�",Width=900,Height=700,Owner=window,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brushes.White };
            if(new [] {".png",".jpg",".jpeg",".bmp",".gif"}.Contains(ext)) {
                if(new FileInfo(d.FileName).Length>25*1024*1024) throw new InvalidOperationException("ͼƬ���� 25 MB����ʹ�� WPS �鿴��");
                var bitmap=new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption=BitmapCacheOption.OnLoad; bitmap.UriSource=uri; bitmap.DecodePixelWidth=2400; bitmap.EndInit(); bitmap.Freeze();
                reader.Content=new ScrollViewer { Content=new Image { Source=bitmap,Stretch=Stretch.Uniform },VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
            } else {
                if(!new [] {".txt",".md",".csv",".json",".log",".cs",".cpp",".h",".py"}.Contains(ext)) throw new InvalidOperationException("�ø�ʽ�в�֧�������Ķ����ı���ͼƬ��ֱ�Ӷ���Office / PDF ��ͨ�� WPS �򿪡�");
                if(new FileInfo(d.FileName).Length>5*1024*1024) throw new InvalidOperationException("�ı����� 5 MB������ WPS �򿪡�");
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
            Show(); Status("���ִ����ѽ���������ơ����˳����ؿ�����¼·����"+store.Root);
            MessageBox.Show(window,"�ѽ�������Ա���������\n"+error.Message,"TomatoLock",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        public void Dispose() {
            if(disposed)return; disposed=true; timer.Stop(); if(gate!=null)gate.Dispose();
            SystemEvents.SessionSwitch-=SessionSwitch; tray.Visible=false; tray.Dispose();
            try { if(store.Healthy) { store.Save(); Reports.Generate(store,DateTime.Now.ToString("yyyy-MM-dd")); } } catch {}
        }
    }
}
