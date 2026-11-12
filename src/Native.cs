using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;

namespace TomatoLock {
    public static class Native {
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LastInput info);
        [DllImport("kernel32.dll")] public static extern bool QueryUnbiasedInterruptTime(out ulong time);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string module);
        public delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SetWindowsHookEx(int type, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct LastInput { public uint Size, Time; }
        [StructLayout(LayoutKind.Sequential)] struct Keyboard { public uint Key, Scan, Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct Mouse { public Point Point; public uint Data, Flags, Time; public IntPtr Extra; }
        public static double AwakeSeconds() { ulong value; if (!QueryUnbiasedInterruptTime(out value)) throw new InvalidOperationException("无法读取系统单调时钟"); return value / 10000000.0; }
        public static double IdleSeconds() { LastInput info = new LastInput { Size = (uint)Marshal.SizeOf(typeof(LastInput)) }; return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) / 1000.0 : Double.MaxValue; }
        public static string ProcessPath(IntPtr window) {
            uint id; GetWindowThreadProcessId(window, out id);
            IntPtr process = OpenProcess(0x1000, false, id); if (process == IntPtr.Zero) return "";
            try { StringBuilder name = new StringBuilder(32768); int size = name.Capacity; return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString() : ""; }
            finally { CloseHandle(process); }
        }
        public static string ClassName(IntPtr window) { StringBuilder b=new StringBuilder(256); GetClassName(window,b,b.Capacity); return b.ToString(); }
        public static bool Browser(string path) { return new [] {"chrome", "msedge", "firefox", "brave", "vivaldi", "opera"}.Contains(Path.GetFileNameWithoutExtension(path).ToLowerInvariant()); }
        public static string Hash(string path) { using(var sha=SHA256.Create()) using(var file=File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); }

        // Hooks are installed only during a user-started session. Never log keystrokes.
        public sealed class InputGate : IDisposable {
            IntPtr keyboardHook, mouseHook;
            readonly HookProc keyboardProc, mouseProc;
            readonly UnlockChord keys = new UnlockChord();
            readonly Func<IntPtr,bool> allowed;
            readonly Action wake;
            public bool Chord { get { return keys.Complete; } }
            public void Reset() { keys.Reset(); }
            public InputGate(Func<IntPtr,bool> allowed, Action wake) {
                this.allowed=allowed; this.wake=wake; keyboardProc=KeyboardEvent; mouseProc=MouseEvent;
                // Include modifiers already held before the hook is installed.
                foreach(int key in new [] {27,46,162,163,160,161}) if((GetAsyncKeyState(key)&0x8000)!=0) keys.Update(key,true);
                keyboardHook=SetWindowsHookEx(13,keyboardProc,GetModuleHandle(null),0);
                mouseHook=SetWindowsHookEx(14,mouseProc,GetModuleHandle(null),0);
                if(keyboardHook==IntPtr.Zero || mouseHook==IntPtr.Zero) { Dispose(); throw new InvalidOperationException("无法安装专注输入拦截；本次专注没有启动。"); }
            }
            IntPtr KeyboardEvent(int code, IntPtr message, IntPtr data) {
                if(code>=0) {
                    var k=(Keyboard)Marshal.PtrToStructure(data,typeof(Keyboard)); int key=(int)k.Key;
                    bool down=message.ToInt32()==0x100 || message.ToInt32()==0x104;
                    keys.Update(key,down);
                    // Reserve Esc during focus: prevents Ctrl+Shift+Esc launching Task Manager before the chord is complete.
                    if(key==27 || Chord) return new IntPtr(1);
                    if(!allowed(GetForegroundWindow())) { wake(); return new IntPtr(1); }
                }
                return CallNextHookEx(keyboardHook,code,message,data);
            }
            IntPtr MouseEvent(int code, IntPtr message, IntPtr data) {
                if(code>=0 && message.ToInt32()!=0x200) {
                    var m=(Mouse)Marshal.PtrToStructure(data,typeof(Mouse));
                    IntPtr hit=GetAncestor(WindowFromPoint(m.Point),2);
                    if(!allowed(hit)) { wake(); return new IntPtr(1); }
                }
                return CallNextHookEx(mouseHook,code,message,data);
            }
            public void Dispose() { if(keyboardHook!=IntPtr.Zero) UnhookWindowsHookEx(keyboardHook); if(mouseHook!=IntPtr.Zero) UnhookWindowsHookEx(mouseHook); keyboardHook=mouseHook=IntPtr.Zero; keys.Reset(); }
        }
    }
    public class AppPolicy {
        readonly HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<IntPtr, bool> cache = new Dictionary<IntPtr, bool>();
        readonly HashSet<IntPtr> localFolders = new HashSet<IntPtr>();
        double foldersAt = -100;
        readonly string self = Process.GetCurrentProcess().MainModule.FileName;
        readonly string explorer=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe");
        public void Configure(IEnumerable<AllowedApp> apps) {
            paths.Clear(); cache.Clear(); paths.Add(self);
            foreach(var app in apps) {
                if(!File.Exists(app.Path)) throw new InvalidOperationException(app.Name+" 已移动或卸载，请重新选择。");
                if(Native.Hash(app.Path)!=app.Sha256) throw new InvalidOperationException(app.Name+" 已更新，请在设置中重新确认可执行文件。");
                paths.Add(app.Path);
            }
        }
        public void Refresh() { cache.Clear(); }
        public void SetLocalFolders(string result) { localFolders.Clear(); foreach(string s in result.Split(',')) { long h; if(Int64.TryParse(s,out h)) localFolders.Add(new IntPtr(h)); } foldersAt=Native.AwakeSeconds(); cache.Clear(); }
        public bool Allowed(IntPtr window) {
            if(window==IntPtr.Zero) return false;
            bool result; if(cache.TryGetValue(window,out result)) return result;
            string path=Native.ProcessPath(window);
            result=paths.Contains(path);
            if(String.Equals(path,explorer,StringComparison.OrdinalIgnoreCase)) {
                // Explorer shell and file windows are navigation surfaces. Executables they launch are separately checked.
                string cls=Native.ClassName(window);
                result=((cls=="CabinetWClass" || cls=="ExploreWClass") && Native.AwakeSeconds()-foldersAt<3 && localFolders.Contains(window)) || cls=="Progman" || cls=="WorkerW" || cls=="Shell_TrayWnd" || cls=="Shell_SecondaryTrayWnd";
            }
            cache[window]=result; return result;
        }
        public bool AllowsPath(string path) { return paths.Contains(path); }
        public bool IsFolderWindow(IntPtr window) {
            string cls=Native.ClassName(window);
            return (cls=="CabinetWClass" || cls=="ExploreWClass") && String.Equals(Native.ProcessPath(window),explorer,StringComparison.OrdinalIgnoreCase);
        }
        public static string Qualify(string path) {
            string name=Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if(name=="chatgpt") return "ChatGPT";
            if(new [] {"wps","et","wpp","wpspdf"}.Contains(name)) return "WPS · " + name;
            throw new InvalidOperationException("仅可添加 ChatGPT.exe、wps.exe、et.exe、wpp.exe 或 wpspdf.exe。其他本地文件请用内置阅读器或 WPS 打开。");
        }
        public static List<string> Discover() {
            var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var p in Process.GetProcesses()) { using(p) try { string path=p.MainModule.FileName; Qualify(path); result.Add(path); } catch {} }
            foreach(string root in new [] {Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}) {
                foreach(string suffix in new [] {"Kingsoft\\WPS Office", "Programs\\Kingsoft\\WPS Office", "Programs\\ChatGPT", "ChatGPT"}) {
                    string folder=Path.Combine(root,suffix); if(!Directory.Exists(folder)) continue;
                    try { foreach(string path in Directory.GetFiles(folder,"*.exe",SearchOption.AllDirectories)) { try { Qualify(path); result.Add(path); } catch {} } } catch {}
                }
            }
            return result.OrderBy(x=>x).ToList();
        }
    }
    public static class SiteProbe {
        public static string LocalFolders(string requested=null) {
            object shell=null, windows=null; var handles=new List<string>(); string matching=""; string matching="";
            try {
                shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                windows=shell.GetType().InvokeMember("Windows",System.Reflection.BindingFlags.InvokeMethod,null,shell,null);
                foreach(object window in (System.Collections.IEnumerable)windows) {
                    try {
                        Type t=window.GetType(); string url=(string)t.InvokeMember("LocationURL",System.Reflection.BindingFlags.GetProperty,null,window,null);
                        Uri uri; if(!Uri.TryCreate(url,UriKind.Absolute,out uri) || !uri.IsFile || uri.IsUnc) continue;
                        string root=Path.GetPathRoot(uri.LocalPath); var drive=new DriveInfo(root);
                        if(drive.DriveType!=DriveType.Fixed && drive.DriveType!=DriveType.Removable && drive.DriveType!=DriveType.CDRom) continue;
                        string handle=Convert.ToInt64(t.InvokeMember("HWND",System.Reflection.BindingFlags.GetProperty,null,window,null)).ToString();
                        handles.Add(handle);
                        if(!String.IsNullOrEmpty(requested) && String.Equals(Path.GetFullPath(uri.LocalPath).TrimEnd('\\'),Path.GetFullPath(requested).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) matching=handle;
                    } catch {} finally { if(Marshal.IsComObject(window)) Marshal.ReleaseComObject(window); }
                }
            } finally { if(windows!=null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows); if(shell!=null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
            return String.Join(",",handles)+(requested==null?"":"|"+matching);
        }
        // This runs in a short-lived child process: an unresponsive accessibility provider cannot freeze the timer or unlock keys.
        public static string Read(IntPtr handle) {
            if(Native.GetForegroundWindow()!=handle) return "";
            var root=AutomationElement.FromHandle(handle);
            var controls=root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit));
            for(int i=0;i<controls.Count;i++) {
                var e=controls[i]; string id=e.Current.AutomationId; string name=e.Current.Name;
                bool address=id=="addressEditBox" || id=="urlbar-input" || id=="omnibox" || id=="1001" ||
                    name=="Address and search bar" || name=="地址和搜索栏" || name=="地址与搜索栏" || name=="Search with Google or enter address" || name=="Search or enter address" || name=="搜索或输入地址";
                if(!address) continue;
                object pattern; if(e.TryGetCurrentPattern(ValuePattern.Pattern,out pattern)) {
                    string host=Logic.HostOnly(((ValuePattern)pattern).Current.Value);
                    if(Native.GetForegroundWindow()==handle) return host;
                }
            }
            return "";
        }
    }
}
