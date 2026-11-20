using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using TomatoLock;
public static class Tests {
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern uint GetShortPathName(string path,StringBuilder result,uint length);
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
            Assert(Logic.HostOnly("file:///C:/Documents/secret.txt")=="本地文件","file names not stored");
            Logic.AddUsage(recovered.Data,start.AddSeconds(2),2,"app","test.exe");
            Assert(recovered.Data.Usage.Count==2 && recovered.Data.Usage.All(x=>Math.Abs(x.Seconds-1)<0.001),"usage split exactly at local midnight");
            Logic.AddUsage(recovered.Data,start,3600,"app","invalid.exe"); Assert(recovered.Data.Usage.Count==2,"large usage gap ignored");
            for(int i=0;i<3;i++) recovered.Data.Sessions.Add(new Session { Id="done"+i, Started=start.AddDays(i).AddMinutes(-100),Ended=start.AddDays(i),ElapsedSeconds=6000,TargetSeconds=6000,Status="completed" });
            Assert(Logic.LongestStreak(recovered.Data.Sessions)==3,"three-day streak");
            Assert(Logic.Achievements(recovered.Data).First(x=>x.Name=="百分钟远航").Unlocked,"100-minute achievement");
            recovered.Save(); Reports.Generate(recovered,"2026-11-09");
            Assert(File.ReadAllText(Path.Combine(root,"reports","2026-11-09.md")).Contains("test.exe"),"daily Markdown report");
            Assert(File.ReadAllText(Path.Combine(root,"reports","2026-11-09.csv")).Contains("\"app\""),"daily CSV report");
            recovered.Save(); File.WriteAllText(Path.Combine(root,"state.json"),"broken"); var restored=new Store(root);
            Assert(restored.Healthy && restored.Data.Sessions.Count==4,"corrupt state restored from backup");
            Assert(Directory.GetFiles(root,"*.corrupt-*").Length==1,"corrupt file preserved");
            string bad=Path.Combine(root,"bad"); Directory.CreateDirectory(bad); File.WriteAllText(Path.Combine(bad,"state.json"),"bad"); var blocked=new Store(bad);
            Assert(!blocked.Healthy,"unrecoverable data refuses a fresh session");
            bool refused=false; try { AppPolicy.Qualify("notepad.exe"); } catch { refused=true; } Assert(refused,"arbitrary executable not allowlisted");
            bool refusedBackup=false; try { AppPolicy.Qualify("wps.exe_bk"); } catch(InvalidOperationException) { refusedBackup=true; }
            Assert(refusedBackup,"WPS updater backup is not an executable candidate");
            string oldChat=Path.Combine(root,"old application","ChatGPT.exe"), newChat=Path.Combine(root,"new application","ChatGPT.exe"), wps=Path.Combine(root,"office","wps.exe");
            foreach(string p in new [] {oldChat,newChat,wps}) { Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p,p); }
            var approved=new List<AllowedApp>();
            AppPolicy.RegisterApproved(approved,new [] {oldChat,wps});
            AppPolicy.RegisterApproved(approved,new [] {newChat});
            Assert(approved.Count==2 && AppPolicy.SelectApproved(approved,"ChatGPT").Path==Native.CanonicalPath(newChat),"rediscovery replaces the earlier ChatGPT version rather than appending it");
            Assert(approved.Any(a=>a.Name=="WPS · wps"),"confirming ChatGPT preserves other approved modules");
            approved.Insert(0,new AllowedApp {Name="ChatGPT",Path=Path.Combine(root,"removed package","ChatGPT.exe"),Sha256="old"});
            Assert(AppPolicy.SelectApproved(approved,"ChatGPT").Path==Native.CanonicalPath(newChat),"missing old ChatGPT cannot mask the confirmed current version");
            string backupWps=Path.Combine(root,"office","wps.exe_bk"); File.WriteAllText(backupWps,"backup");
            approved.Add(new AllowedApp {Name="WPS · wps",Path=backupWps,Sha256="backup"});
            approved.Add(new AllowedApp {Name="ChatGPT",Path=newChat,Sha256=Native.Hash(newChat)});
            Assert(AppPolicy.PruneApproved(approved)==3 && approved.Count==2,"startup cleanup removes missing paths backups and duplicate entries");
            var policy=new AppPolicy(); policy.Configure(approved);
            Assert(policy.AllowsPath(Native.CanonicalPath(newChat)),"cleaned approved app remains allowed for focus");
            var shortPath=new StringBuilder(32768); uint shortLength=GetShortPathName(wps,shortPath,(uint)shortPath.Capacity);
            Assert(shortLength>0 && Native.CanonicalPath(shortPath.ToString())==Native.CanonicalPath(wps),"short and long Windows file paths share one canonical identity");
            approved.Add(new AllowedApp {Name="WPS · wps",Path=shortPath.ToString(),Sha256=Native.Hash(wps)});
            AppPolicy.PruneApproved(approved);
            Assert(approved.Count(a=>a.Name=="WPS · wps")==1,"short path alias does not duplicate the WPS installation");
            string confirmedFingerprint=AppPolicy.SelectApproved(approved,"ChatGPT").Sha256;
            File.WriteAllText(newChat,"updated without confirmation"); AppPolicy.PruneApproved(approved);
            Assert(AppPolicy.SelectApproved(approved,"ChatGPT").Sha256==confirmedFingerprint,"cleanup never approves a changed executable fingerprint");
            bool refusedUpdated=false; try { policy.Configure(approved); } catch(InvalidOperationException) {refusedUpdated=true;}
            Assert(refusedUpdated,"updated executable still requires explicit reconfirmation");
            bool registrationFailed=false; try { AppPolicy.RegisterApproved(approved,new [] {wps,Path.Combine(root,"not installed","ChatGPT.exe")}); } catch(IOException) {registrationFailed=true;}
            Assert(registrationFailed && approved.Count==2 && AppPolicy.SelectApproved(approved,"ChatGPT").Sha256==confirmedFingerprint,"failed rediscovery leaves existing approvals unchanged");
            var chord=new UnlockChord(); chord.Update(27,true); chord.Update(162,true); chord.Update(160,true);
            Assert(!chord.Complete,"three keys cannot unlock"); chord.Update(46,true); Assert(chord.Complete,"exact four-key chord detected");
            chord.Update(27,false); Assert(!chord.Complete,"releasing a required key cancels chord");
            chord.Reset(); foreach(int key in new [] {46,161,163,27})chord.Update(key,true); Assert(chord.Complete,"right-side modifiers and different key order accepted");
            chord.Reset(); Assert(!chord.Complete,"lock-screen reset clears stale keys");
            foreach(int key in new [] {27,46,162,160}) chord.Update(key,true,10);
            Assert(!chord.HeldFor(10.999,1) && chord.HeldFor(11,1),"unlock requires a full second from the last required key");
            chord.Update(160,false,10.95); chord.Update(160,true,10.99);
            Assert(!chord.HeldFor(11,1) && chord.HeldFor(11.99,1),"brief modifier release between timer ticks restarts the hold");
            chord.Update(27,true,11.5);
            Assert(chord.HeldFor(11.99,1),"key repeat does not restart a continuous hold");
            chord.Reset(); Assert(!chord.HeldFor(99,1),"session reset clears the hold deadline");
            chord.Update(27,true,20); chord.Update(46,true,20); chord.Update(163,true,20); chord.Update(161,true,20.5);
            Assert(!chord.HeldFor(21,1) && chord.HeldFor(21.5,1),"right modifiers measure overlap rather than first key time");
            var handoff=new FolderHandoff(); var folder=new IntPtr(17);
            handoff.Observe(folder,10); Assert(handoff.Waiting(folder,13.9),"new Explorer retains focus while verification runs");
            handoff.Observe(folder,13.9); Assert(!handoff.Waiting(folder,14),"repeated input cannot extend unverified folder grace");
            Assert(!handoff.Waiting(new IntPtr(18),11),"folder grace is scoped to one window");
            handoff.Reset(); Assert(!handoff.Waiting(folder,11),"new focus session clears handoff");
            var awards=new State();
            awards.Usage.Add(new Usage {Kind="app",Name="CHATGPT.EXE",Seconds=35999});
            awards.Usage.Add(new Usage {Kind="site",Name="chatgpt.com",Seconds=72000});
            Assert(!Logic.Achievements(awards).First(x=>x.Id=="silicon").Unlocked,"silicon counts app time only and respects threshold");
            awards.Usage[0].Seconds++;
            Assert(Logic.CaptureAwards(awards).Single().Id=="silicon","silicon unlocks at ten active hours");
            Assert(Logic.CaptureAwards(awards).Count==0,"achievement notifications are not repeated");
            awards.Usage.Clear(); Assert(Logic.Achievements(awards).First(x=>x.Id=="silicon").Unlocked,"clearing usage retains earned badges");
            for(int i=0;i<3;i++) awards.Sessions.Add(new Session {Status="interrupted"});
            Assert(!Logic.Achievements(awards).First(x=>x.Id=="rotten").Unlocked,"crashes do not count as rotten tomatoes");
            for(int i=0;i<2;i++) awards.Sessions.Add(new Session {Status="unlocked"});
            Assert(!Logic.Achievements(awards).First(x=>x.Id=="rotten").Unlocked,"two early unlocks stay below threshold");
            awards.Sessions.Add(new Session {Status="unlocked"});
            Assert(Logic.CaptureAwards(awards).Single().Id=="rotten","third deliberate unlock earns rotten tomato");
            awards.Usage.Add(new Usage {Kind="app",Name="et.exe",Seconds=9000}); awards.Usage.Add(new Usage {Kind="app",Name="WPS.exe",Seconds=9000});
            Assert(Logic.CaptureAwards(awards).Single().Id=="office","WPS modules combine for office achievement");
            var awardStore=new Store(Path.Combine(root,"awards")); awardStore.Data=awards; awardStore.Data.Settings.Muted=true; awardStore.Save();
            var reloadAwards=new Store(awardStore.Root);
            Assert(reloadAwards.Data.Awards.Count==3 && reloadAwards.Data.Settings.Muted,"awards and mute survive restart");
            string legacy=Path.Combine(root,"legacy"); Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy,"state.json"),"{\"Schema\":1,\"Settings\":{\"Apps\":[],\"IdleSeconds\":180},\"Sessions\":[],\"Usage\":[]}");
            var migrated=new Store(legacy); Assert(migrated.Healthy && migrated.Data.Awards!=null && !migrated.Data.Settings.Muted,"v1.0 settings load without reset");
            string package=Path.Combine(root,"Test.Package_1.0.0.0_x64__publisher"), exe=Path.Combine(package,"app","fixture.exe"); Directory.CreateDirectory(Path.GetDirectoryName(exe)); File.WriteAllText(exe,"fixture");
            File.WriteAllText(Path.Combine(package,"AppxManifest.xml"),"<Package><Identity Name=\"Test.Package\"/><Applications><Application Id=\"Main\" Executable=\"app\\fixture.exe\"/></Applications></Package>");
            Assert(AppLauncher.ResolveId(exe)=="Test.Package_publisher!Main","cold MSIX launch resolves matching manifest application");
            Assert(AppLauncher.ResolveId(Path.Combine(package,"other.exe"))=="","manifest must match exact approved executable");
            bool refusedLaunch=false; try { AppLauncher.Launch(new AllowedApp {Path=exe,Sha256="changed"}); } catch(InvalidOperationException) {refusedLaunch=true;}
            Assert(refusedLaunch,"changed executable is rejected before activation");
            Console.WriteLine("All "+count+" checks passed."); return 0;
        } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
