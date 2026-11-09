using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("TomatoLock Setup")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
public static class Setup {
    static readonly string InstallDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","TomatoLock");
    static readonly string Shortcut=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"TomatoLock.lnk");
    const string UninstallKey="Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\TomatoLock";
    [STAThread] public static int Main(string[] args) {
        Application.EnableVisualStyles();
        try {
            if(args.Length>0 && args[0]=="--verify") { using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("TomatoLock.exe")) return s!=null && s.Length>10000 ? 0 : 1; }
            if(args.Length>0 && args[0]=="--uninstall") { Uninstall(); return 0; }
            var form=new Form { Text="��װ TomatoLock 1.0.0",ClientSize=new Size(540,365),StartPosition=FormStartPosition.CenterScreen,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,BackColor=Color.FromArgb(246,247,243),Font=new Font("Microsoft YaHei UI",10) };
            form.Controls.Add(new Label { Text="TomatoLock",Font=new Font("Segoe UI",28,FontStyle.Bold),ForeColor=Color.FromArgb(24,63,53),Location=new Point(30,24),AutoSize=true });
            form.Controls.Add(new Label { Text="���߷����� �� רע���� �� ���ؼ�¼��ɾ�",Location=new Point(33,90),AutoSize=true });
            form.Controls.Add(new Label { Text="��װ����ǰ�û����������ԱȨ�ޡ�\nʹ��ͳ�ƺͿ�������Ĭ�Ϲرգ����Ժ���������ѡ��\n\n��װλ�ã�\n"+InstallDir,Location=new Point(33,135),Size=new Size(475,105) });
            var launch=new CheckBox { Text="��װ��ɺ�� TomatoLock",Checked=true,Location=new Point(33,250),AutoSize=true }; form.Controls.Add(launch);
            var button=new Button { Text="��װ",BackColor=Color.FromArgb(33,78,61),ForeColor=Color.White,FlatStyle=FlatStyle.Flat,Location=new Point(350,300),Size=new Size(155,40) }; form.Controls.Add(button);
            button.Click+=(s,e)=>{
                try { Install(); MessageBox.Show("��װ��ɡ��ɴӿ�ʼ�˵��� TomatoLock��","TomatoLock"); if(launch.Checked) Process.Start(Path.Combine(InstallDir,"TomatoLock.exe")); form.Close(); }
                catch(Exception error) { MessageBox.Show(error.Message,"��װδ���",MessageBoxButtons.OK,MessageBoxIcon.Error); }
            };
            Application.Run(form); return 0;
        } catch(Exception e) { MessageBox.Show(e.Message,"TomatoLock",MessageBoxButtons.OK,MessageBoxIcon.Error); return 1; }
    }
    static void RequireClosed() {
        foreach(var process in Process.GetProcessesByName("TomatoLock")) using(process) {
            try { if(String.Equals(process.MainModule.FileName,Path.Combine(InstallDir,"TomatoLock.exe"),StringComparison.OrdinalIgnoreCase)) throw new IOException("���ȴ������˳��������е� TomatoLock���ٽ��а�װ��ж�ء�"); }
            catch(System.ComponentModel.Win32Exception) { throw new IOException("�޷�ȷ�� TomatoLock �Ƿ����˳�����رպ����ԡ�"); }
        }
    }
    static void Install() {
        RequireClosed(); Directory.CreateDirectory(InstallDir);
        Extract("TomatoLock.exe"); Extract("TomatoLock.exe.config");
        string self=Assembly.GetExecutingAssembly().Location;
        if(!String.Equals(self,Path.Combine(InstallDir,"Uninstall.exe"),StringComparison.OrdinalIgnoreCase)) File.Copy(self,Path.Combine(InstallDir,"Uninstall.exe"),true);
        Type shellType=Type.GetTypeFromProgID("WScript.Shell"); object shell=Activator.CreateInstance(shellType);
        object shortcut=shellType.InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{Shortcut});
        Type type=shortcut.GetType(); type.InvokeMember("TargetPath",BindingFlags.SetProperty,null,shortcut,new object[]{Path.Combine(InstallDir,"TomatoLock.exe")});
        type.InvokeMember("WorkingDirectory",BindingFlags.SetProperty,null,shortcut,new object[]{InstallDir}); type.InvokeMember("Save",BindingFlags.InvokeMethod,null,shortcut,null);
        System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut); System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        using(var key=Registry.CurrentUser.CreateSubKey(UninstallKey)) {
            key.SetValue("DisplayName","TomatoLock"); key.SetValue("DisplayVersion","1.0.0"); key.SetValue("Publisher","TomatoLock contributors");
            key.SetValue("InstallLocation",InstallDir); key.SetValue("DisplayIcon",Path.Combine(InstallDir,"TomatoLock.exe"));
            key.SetValue("UninstallString","\""+Path.Combine(InstallDir,"Uninstall.exe")+"\" --uninstall"); key.SetValue("NoModify",1); key.SetValue("NoRepair",1);
        }
    }
    static void Extract(string name) { using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) using(var output=File.Create(Path.Combine(InstallDir,name))) input.CopyTo(output); }
    static void Uninstall() {
        RequireClosed();
        if(MessageBox.Show("ж�� TomatoLock��\nרע��¼��ͳ�����ݽ������ڱ�����","ж��",MessageBoxButtons.YesNo)!=DialogResult.Yes) return;
        using(var key=Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run",true)) if(key!=null)key.DeleteValue("TomatoLock",false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey,false); if(File.Exists(Shortcut))File.Delete(Shortcut);
        // Fixed per-user install directory; validate it before a single-shell deferred recursive deletion.
        string script="$ErrorActionPreference = 'Stop'\nWait-Process -Id "+Process.GetCurrentProcess().Id+" -ErrorAction SilentlyContinue\n"+
            "$base = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))\n"+
            "$target = [IO.Path]::GetFullPath((Join-Path $base 'TomatoLock'))\n"+
            "if ([IO.Path]::GetDirectoryName($target) -ne $base -or [IO.Path]::GetFileName($target) -ne 'TomatoLock') { throw 'Invalid uninstall path' }\n"+
            "if (Test-Path -LiteralPath $target) { if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing reparse point' }; Remove-Item -LiteralPath $target -Recurse -Force }";
        string command=Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe"),"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand "+command) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden });
        MessageBox.Show("ж����Ϣ���Ƴ����رմ˴��ں�ɾ�������ļ�����ļ�¼���� %LocalAppData%\\TomatoLock��","TomatoLock");
    }
}
