using System;
using System.IO;
using System.Linq;
using TomatoLock;
public static class Tests {
    static int count;
    static void Assert(bool condition,string name) { if(!condition) throw new Exception("FAIL: "+name); count++; Console.WriteLine("PASS: "+name); }
    public static int Main() {
        string root=Path.Combine(Path.GetTempPath(),"TomatoLock-tests-"+Guid.NewGuid().ToString("N"));
        try {
            var store=new Store(root);
            Assert(!store.Data.Settings.TrackApps && !store.Data.Settings.TrackSites && !store.Data.Settings.Startup,"privacy defaults off");
            var start=new DateTimeOffset(2026,11,9,23,59,59,TimeSpan.FromHours(8));
            store.Data.Active=new Session { Id="test",Started=start,TargetSeconds=600,ElapsedSeconds=37,Status="active" }; store.Save();
            var recovered=new Store(root);
            Assert(recovered.Data.Active==null && recovered.Data.Sessions.Count==1 && recovered.Data.Sessions[0].Status=="interrupted","crash recovery interrupts rather than completes");
            Assert(recovered.Data.Sessions[0].ElapsedSeconds==37 && recovered.Data.Sessions[0].Started==start,"timestamps and progress round trip");
            Assert(!Logic.Achievements(recovered.Data).Any(x=>x.Unlocked),"interruption grants no achievements");
            var session=new Session { TargetSeconds=60,ElapsedSeconds=59 };
            Logic.Advance(session,10); Assert(session.ElapsedSeconds==60,"timer caps at target");
            Logic.Advance(session,-1); Logic.Advance(session,Double.NaN); Assert(session.ElapsedSeconds==60,"invalid deltas rejected");
            Assert(Logic.SafeDelta(100,100.5)==0.5 && Logic.SafeDelta(100,1000)==0 && Logic.SafeDelta(100,90)==0,"large stalls and backwards deltas excluded");
            Assert(Logic.HostOnly("https://www.example.com/private?q=secret#token")=="www.example.com","only domain persisted");
            Assert(Logic.HostOnly("example.com/a")=="example.com","scheme-less browser URL");
            Assert(Logic.HostOnly("search words")=="" && Logic.HostOnly("secret")=="" && Logic.HostOnly("about:blank")=="","search terms and internal URLs not stored");
            Assert(Logic.HostOnly("file:///C:/Documents/secret.txt")=="�����ļ�","file names not stored");
            Logic.AddUsage(recovered.Data,start.AddSeconds(2),2,"app","test.exe");
            Assert(recovered.Data.Usage.Count==2 && recovered.Data.Usage.All(x=>Math.Abs(x.Seconds-1)<0.001),"usage split exactly at local midnight");
            Logic.AddUsage(recovered.Data,start,3600,"app","invalid.exe"); Assert(recovered.Data.Usage.Count==2,"large usage gap ignored");
            for(int i=0;i<3;i++) recovered.Data.Sessions.Add(new Session { Id="done"+i, Started=start.AddDays(i).AddMinutes(-100),Ended=start.AddDays(i),ElapsedSeconds=6000,TargetSeconds=6000,Status="completed" });
            Assert(Logic.LongestStreak(recovered.Data.Sessions)==3,"three-day streak");
            Assert(Logic.Achievements(recovered.Data).First(x=>x.Name=="�ٷ���Զ��").Unlocked,"100-minute achievement");
            recovered.Save(); Reports.Generate(recovered,"2026-11-09");
            Assert(File.ReadAllText(Path.Combine(root,"reports","2026-11-09.md")).Contains("test.exe"),"daily Markdown report");
            Assert(File.ReadAllText(Path.Combine(root,"reports","2026-11-09.csv")).Contains("\"app\""),"daily CSV report");
            recovered.Save(); File.WriteAllText(Path.Combine(root,"state.json"),"broken"); var restored=new Store(root);
            Assert(restored.Healthy && restored.Data.Sessions.Count==4,"corrupt state restored from backup");
            Assert(Directory.GetFiles(root,"*.corrupt-*").Length==1,"corrupt file preserved");
            string bad=Path.Combine(root,"bad"); Directory.CreateDirectory(bad); File.WriteAllText(Path.Combine(bad,"state.json"),"bad"); var blocked=new Store(bad);
            Assert(!blocked.Healthy,"unrecoverable data refuses a fresh session");
            bool refused=false; try { AppPolicy.Qualify("notepad.exe"); } catch { refused=true; } Assert(refused,"arbitrary executable not allowlisted");
            var chord=new UnlockChord(); chord.Update(27,true); chord.Update(162,true); chord.Update(160,true);
            Assert(!chord.Complete,"three keys cannot unlock"); chord.Update(46,true); Assert(chord.Complete,"exact four-key chord detected");
            chord.Update(27,false); Assert(!chord.Complete,"releasing a required key cancels chord");
            chord.Reset(); foreach(int key in new [] {46,161,163,27})chord.Update(key,true); Assert(chord.Complete,"right-side modifiers and different key order accepted");
            chord.Reset(); Assert(!chord.Complete,"lock-screen reset clears stale keys");
            Console.WriteLine("All "+count+" checks passed."); return 0;
        } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
