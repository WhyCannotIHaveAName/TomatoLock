using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TomatoLock;
public static class Smoke {
    // In-process layout rendering: no keyboard/mouse injection and no focus lock on the user's desktop.
    [STAThread] public static int Main() {
        try {
            string dir=Path.GetFullPath("test-output/smoke"); Directory.CreateDirectory(dir);
            var app=new Application(); Window window;
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml")) window=(Window)XamlReader.Load(stream);
            using(var controller=new MainController(window,new Store(Path.Combine(dir,"data")))) {
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
            }
            return 0;
        } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
