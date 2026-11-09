using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Win32;

[assembly: AssemblyTitle("TomatoLock")]
[assembly: AssemblyProduct("TomatoLock")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace TomatoLock {
    public static class Program {
        static string customDataRoot;
        public static string DataRoot { get { return customDataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TomatoLock"); } }
        public static string Exe { get { return Assembly.GetExecutingAssembly().Location; } }
        [STAThread] public static int Main(string[] args) {
            if(args.Length>=2 && args[0]=="--probe") { try { Console.OutputEncoding=System.Text.Encoding.UTF8; Console.Write(SiteProbe.Read(new IntPtr(Int64.Parse(args[1])))); return 0; } catch { return 1; } }
            if(args.Length==1 && args[0]=="--folders") { try { Console.OutputEncoding=System.Text.Encoding.UTF8; Console.Write(SiteProbe.LocalFolders()); return 0; } catch { return 1; } }
            if(args.Length==2 && args[0]=="--data-dir") customDataRoot=Path.GetFullPath(args[1]);
            bool created;
            string identity;
            using(var sha=System.Security.Cryptography.SHA256.Create()) identity=BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(DataRoot.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()))).Replace("-","");
            using(var mutex=new Mutex(true,"Local\\TomatoLock-"+identity,out created)) {
                if(!created) { if(!args.Contains("--startup")) MessageBox.Show("TomatoLock 已在运行，请从系统托盘打开。","TomatoLock"); return 0; }
                try {
                    var app=new Application();
                    Window window;
                    using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml")) window=(Window)XamlReader.Load(stream);
                    var controller=new MainController(window,new Store(DataRoot));
                    if(customDataRoot!=null) window.Loaded+=(s,e)=>File.WriteAllText(Path.Combine(DataRoot,"startup-ready.txt"),"Window loaded successfully at "+DateTimeOffset.Now);
                    app.DispatcherUnhandledException+=(s,e)=>{ controller.FailSafe(e.Exception); e.Handled=true; };
                    app.Exit+=(s,e)=>controller.Dispose();
                    if(args.Contains("--startup")) window.Loaded+=(s,e)=>window.Hide();
                    app.Run(window); return 0;
                } catch(Exception e) {
                    try { Directory.CreateDirectory(DataRoot); File.AppendAllText(Path.Combine(DataRoot,"startup-error.log"),DateTimeOffset.Now+"\n"+e+"\n"); } catch {}
                    MessageBox.Show("TomatoLock 无法启动：\n"+e.Message+"\n\n错误详情："+Path.Combine(DataRoot,"startup-error.log"),"TomatoLock"); return 1;
                }
            }
        }
        public static void SetStartup(bool enabled) {
            using(var key=Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) {
                if(enabled) key.SetValue("TomatoLock","\""+Exe+"\" --startup"); else key.DeleteValue("TomatoLock",false);
            }
        }
    }
}
