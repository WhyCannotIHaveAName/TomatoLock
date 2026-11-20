using System;
using TomatoLock;
// Deliberately compiled as a GUI executable: reproduces the original pipe failure.
public static class ProbeHost {
    [STAThread] public static int Main(string[] args) {
        try {
            Program.WriteProbeOutput(args.Length>0 ? SiteProbe.LocalFolders() : "本地文件 · UTF-8 ✓");
            return 0;
        } catch(Exception e) { Program.WriteProbeOutput(e.ToString()); return 1; }
    }
}
