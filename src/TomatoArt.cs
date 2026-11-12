using System;
using System.IO;
using System.Media;
using System.Reflection;
using System.Windows.Media.Imaging;
using System.Windows;

namespace TomatoLock {
    public static class TomatoArt {
        static BitmapSource atlas;
        static readonly BitmapSource[] badges=new BitmapSource[12];
        static SoundPlayer player;
        static MemoryStream audio;
        public static BitmapSource Badge(int index) {
            if(atlas==null) using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("tomato-badges.png")) {
                var image=new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad; image.StreamSource=stream; image.EndInit(); image.Freeze(); atlas=image;
            }
            if(badges[index]==null) {
                int w=atlas.PixelWidth/4,h=atlas.PixelHeight/3;
                var image=new CroppedBitmap(atlas,new Int32Rect(index%4*w,index/4*h,w,h)); image.Freeze(); badges[index]=image;
            }
            return badges[index];
        }
        public static void Play(bool rotten) {
            try {
                if(player!=null) { player.Stop(); player.Dispose(); audio.Dispose(); }
                // Original synthesized, offline jingle. No audio files or downloads required.
                double[] notes=rotten ? new [] {392.0,349.23,293.66,196.0} : new [] {523.25,659.25,783.99,1046.50};
                const int rate=22050; int perNote=(int)(rate*0.19), count=perNote*notes.Length;
                audio=new MemoryStream(); var writer=new BinaryWriter(audio);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36+count*2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate*2); writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(count*2);
                foreach(double hz in notes) for(int i=0;i<perNote;i++) {
                    double t=(double)i/rate, envelope=Math.Min(1,i/220.0)*Math.Pow(1-(double)i/perNote,2);
                    writer.Write((short)(6000*envelope*(Math.Sin(2*Math.PI*hz*t)+0.2*Math.Sin(4*Math.PI*hz*t))));
                }
                writer.Flush(); audio.Position=0; player=new SoundPlayer(audio); player.Play();
            } catch { /* Audio device failure must never interrupt a focus session. */ }
        }
        public static void Stop() { if(player!=null) { player.Stop(); player.Dispose(); player=null; audio.Dispose(); audio=null; } }
    }
}

