using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace TomatoLock {
    public static class AppLauncher {
        delegate bool EnumWindow(IntPtr window, IntPtr state);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr state);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);
        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] class ActivationManager {}
        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IActivationManager {
            [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint process);
            [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint process);
            [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr items, out uint process);
        }
        public static bool FocusExisting(string path) {
            IntPtr found=IntPtr.Zero;
            EnumWindows((h,s)=>{ if(IsWindowVisible(h) && String.Equals(Native.ProcessPath(h),path,StringComparison.OrdinalIgnoreCase)) { found=h; return false; } return true; },IntPtr.Zero);
            if(found==IntPtr.Zero) return false;
            if(Native.IsIconic(found)) Native.ShowWindowAsync(found,9);
            return Native.SetForegroundWindow(found);
        }
        public static string ResolveId(string path) {
            foreach(var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path))) using(p) {
                try {
                    if(!String.Equals(p.MainModule.FileName,path,StringComparison.OrdinalIgnoreCase)) continue;
                    uint length=0; GetApplicationUserModelId(p.Handle,ref length,null);
                    if(length==0 || length>4096) continue;
                    var id=new StringBuilder((int)length);
                    if(GetApplicationUserModelId(p.Handle,ref length,id)==0) return id.ToString();
                } catch {}
            }
            // Cold launch: resolve only the manifest containing the already-approved executable.
            var folder=new DirectoryInfo(Path.GetDirectoryName(path));
            for(int depth=0; folder!=null && depth<5; depth++,folder=folder.Parent) {
                string manifest=Path.Combine(folder.FullName,"AppxManifest.xml");
                if(!File.Exists(manifest)) continue;
                try {
                    var xml=new XmlDocument { XmlResolver=null }; xml.Load(manifest);
                    var identity=xml.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']");
                    string[] pieces=folder.Name.Split('_');
                    if(identity==null || pieces.Length!=5 || pieces[0]!=identity.Attributes["Name"].Value) return "";
                    foreach(XmlNode app in xml.SelectNodes("//*[local-name()='Applications']/*[local-name()='Application']")) {
                        var exe=app.Attributes["Executable"]; var id=app.Attributes["Id"];
                        if(exe!=null && id!=null && String.Equals(Path.GetFullPath(Path.Combine(folder.FullName,exe.Value)),Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase))
                            return pieces[0]+"_"+pieces[4]+"!"+id.Value;
                    }
                } catch {}
                return "";
            }
            return "";
        }
        public static void Launch(AllowedApp app) {
            if(!File.Exists(app.Path) || Native.Hash(app.Path)!=app.Sha256) throw new InvalidOperationException("应用已更新或移动，请在专注结束后重新识别并确认路径。");
            if(FocusExisting(app.Path)) return;
            string id=ResolveId(app.Path);
            if(!String.IsNullOrEmpty(id)) {
                var manager=(IActivationManager)new ActivationManager();
                try { uint process; Marshal.ThrowExceptionForHR(manager.ActivateApplication(id,"",2,out process)); }
                finally { Marshal.ReleaseComObject(manager); }
                return;
            }
            try { Process.Start(new ProcessStartInfo(app.Path) { UseShellExecute=true, WorkingDirectory=Path.GetDirectoryName(app.Path) }); }
            catch(System.ComponentModel.Win32Exception e) {
                throw new InvalidOperationException("Windows 未能启动该应用。请先从开始菜单打开，再点“自动识别应用”确认。\n"+e.Message,e);
            }
        }
    }
}

