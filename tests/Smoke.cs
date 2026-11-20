using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TomatoLock;
public static class Smoke {
    static object StartupValue() {
        using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) return key==null ? null : key.GetValue("TomatoLock");
    }
    // In-process layout rendering: no keyboard/mouse injection and no focus lock on the user's desktop.
    [STAThread] public static int Main() {
        try {
            using(var probe=new Process { StartInfo=new ProcessStartInfo(Path.GetFullPath("build/TomatoLock.ProbeHost.exe")) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,StandardOutputEncoding=System.Text.Encoding.UTF8} }) {
                probe.Start(); if(!probe.WaitForExit(4000)) { probe.Kill(); throw new Exception("GUI pipe probe timed out"); }
                if(probe.ExitCode!=0 || probe.StandardOutput.ReadToEnd()!="本地文件 · UTF-8 ✓") throw new Exception("GUI UTF-8 pipe failed");
                Console.WriteLine("PASS: GUI child writes UTF-8 to redirected pipe without a console");
            }
            string dir=Path.GetFullPath("test-output/smoke"); Directory.CreateDirectory(dir);
            typeof(Program).GetField("customDataRoot",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,Path.Combine(dir,"data"));
            var app=new Application(); Window window;
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml")) window=(Window)XamlReader.Load(stream);
            var smokeStore=new Store(Path.Combine(dir,"data")); smokeStore.Data.Settings.Muted=true;
            using(var controller=new MainController(window,smokeStore)) {
                if(((CheckBox)window.FindName("Startup")).IsEnabled) throw new Exception("Isolated instance exposes system startup setting");
                object startupBefore=StartupValue();
                Program.SetStartup(true);
                if(!Object.Equals(startupBefore,StartupValue())) throw new Exception("Isolated instance enabled system startup");
                Program.SetStartup(false);
                if(!Object.Equals(startupBefore,StartupValue())) throw new Exception("Isolated instance removed system startup");
                typeof(MainController).GetMethod("SetEditable",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(controller,new object[]{true});
                if(((CheckBox)window.FindName("Startup")).IsEnabled) throw new Exception("Finishing focus exposes isolated startup setting");
                ((Button)window.FindName("SaveSettings")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(!Object.Equals(startupBefore,StartupValue())) throw new Exception("Saving isolated preferences changed system startup");
                Console.WriteLine("PASS: isolated data directory leaves system startup unchanged");
                foreach(string page in new [] {"Focus","History","Achievements","Settings"}) {
                    ((Button)window.FindName("Nav"+page)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var root=(FrameworkElement)window.Content; root.Measure(new Size(1080,740)); root.Arrange(new Rect(0,0,1080,740)); root.UpdateLayout();
                    var bitmap=new RenderTargetBitmap(1080,740,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
                    var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using(var file=File.Create(Path.Combine(dir,page+".png"))) encoder.Save(file);
                    Console.WriteLine("PASS: XAML, controller, and "+page+" layout");
                }
                ((TextBox)window.FindName("CustomMinutes")).Text="100";
                if(((TextBlock)window.FindName("Countdown")).Text!="100:00") throw new Exception("Custom 100-minute countdown failed");
                Console.WriteLine("PASS: custom duration updates countdown");
                ((Button)window.FindName("NavAchievements")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var content=(FrameworkElement)window.Content;
                content.Measure(new Size(900,640)); content.Arrange(new Rect(0,0,900,640)); content.UpdateLayout();
                ((ScrollViewer)window.FindName("AchievementsPage")).ScrollToBottom(); content.UpdateLayout();
                var small=new RenderTargetBitmap(900,640,96,96,PixelFormats.Pbgra32); small.Render(content);
                var smallEncoder=new PngBitmapEncoder(); smallEncoder.Frames.Add(BitmapFrame.Create(small));
                using(var file=File.Create(Path.Combine(dir,"Achievements-bottom.png"))) smallEncoder.Save(file);
                Console.WriteLine("PASS: minimum-size collection scrolls to final badges");
                typeof(MainController).GetMethod("Celebrate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(controller,new object[]{Logic.Achievements(new State())[10],0.0});
                content.UpdateLayout(); var toast=new RenderTargetBitmap(900,640,96,96,PixelFormats.Pbgra32); toast.Render(content);
                var toastEncoder=new PngBitmapEncoder(); toastEncoder.Frames.Add(BitmapFrame.Create(toast));
                using(var file=File.Create(Path.Combine(dir,"Achievement-toast.png"))) toastEncoder.Save(file);
                Console.WriteLine("PASS: achievement toast renders");
            }
            return 0;
        } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
