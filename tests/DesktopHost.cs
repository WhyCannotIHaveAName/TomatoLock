using System;
using System.IO;
using TomatoLock;

// Launch from the desktop test tool without touching the user's normal data directory.
public static class DesktopHost {
    [STAThread] public static int Main(string[] args) {
        if(args.Length>0) return Program.Main(args);
        string root=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Program.Exe),"..","test-output","desktop"));
        return Program.Main(new [] {"--data-dir",root});
    }
}
