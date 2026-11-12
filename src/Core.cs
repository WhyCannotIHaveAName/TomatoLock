using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace TomatoLock {
    // A brief focus handoff is not permission to use an unverified Explorer window.
    public sealed class FolderHandoff {
        public IntPtr Window { get; private set; }
        double deadline;
        public void Observe(IntPtr window,double now) { if(window!=Window) { Window=window; deadline=now+4; } }
        public bool Waiting(IntPtr window,double now) { return window!=IntPtr.Zero && window==Window && now<deadline; }
        public void Reset() { Window=IntPtr.Zero; deadline=0; }
    }
    public sealed class UnlockChord {
        readonly HashSet<int> keys=new HashSet<int>();
        public void Update(int key,bool down) { if(down)keys.Add(key); else keys.Remove(key); }
        public void Reset() { keys.Clear(); }
        public bool Complete { get { return keys.Contains(27) && keys.Contains(46) && (keys.Contains(162)||keys.Contains(163)||keys.Contains(17)) && (keys.Contains(160)||keys.Contains(161)||keys.Contains(16)); } }
    }
    public class Settings {
        public bool TrackApps { get; set; }
        public bool TrackSites { get; set; }
        public bool Startup { get; set; }
        public bool Muted { get; set; }
        public int IdleSeconds { get; set; }
        public List<AllowedApp> Apps { get; set; }
        public Settings() { IdleSeconds = 180; Apps = new List<AllowedApp>(); }
    }
    public class AllowedApp {
        public string Path { get; set; }
        public string Name { get; set; }
        public string Sha256 { get; set; }
    }
    public class Session {
        public string Id { get; set; }
        public DateTimeOffset Started { get; set; }
        public DateTimeOffset Ended { get; set; }
        public double TargetSeconds { get; set; }
        public double ElapsedSeconds { get; set; }
        public string Status { get; set; }
    }
    public class Usage {
        public string Date { get; set; }
        public string Kind { get; set; }
        public string Name { get; set; }
        public double Seconds { get; set; }
    }
    public class State {
        public int Schema { get; set; }
        public Settings Settings { get; set; }
        public List<Session> Sessions { get; set; }
        public List<Usage> Usage { get; set; }
        public Session Active { get; set; }
        public List<string> Awards { get; set; }
        public State() { Awards = new List<string>(); Schema = 1; Settings = new Settings(); Sessions = new List<Session>(); Usage = new List<Usage>(); }
    }
    public class Store {
        public readonly string Root;
        public State Data;
        public string Notice = "";
        public bool Healthy = true;
        public Store(string root) { Root = root; Directory.CreateDirectory(root); Load(); }
        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }; }
        static State Read(string path) {
            State value = Serializer().Deserialize<State>(File.ReadAllText(path, Encoding.UTF8));
            if (value == null || value.Schema != 1 || value.Settings == null || value.Settings.Apps == null || value.Sessions == null || value.Usage == null)
                throw new InvalidDataException("数据格式不受支持");
            if(value.Awards==null) value.Awards=new List<string>();
            return value;
        }
        void Load() {
            string path = Path.Combine(Root, "state.json");
            if (!File.Exists(path)) {
                if (File.Exists(path + ".bak")) { Data = Read(path + ".bak"); Notice = "已从备份恢复记录。"; }
                else Data = new State();
            } else {
                try { Data = Read(path); }
                catch {
                    File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.Ticks, false);
                    if (File.Exists(path + ".bak")) {
                        try { Data = Read(path + ".bak"); Notice = "记录文件损坏，已恢复上次备份；损坏文件保留。"; File.Delete(path); }
                        catch { Data = new State(); Healthy = false; Notice = "记录及备份均损坏。原文件已保留，请修复后再开始。"; }
                    } else { Data = new State(); Healthy = false; Notice = "记录文件损坏，已保留原件。请修复后再开始。"; }
                }
            }
            if (Data.Active != null) {
                Data.Active.Status = "interrupted";
                Data.Active.Ended = DateTimeOffset.Now;
                Data.Sessions.Add(Data.Active); Data.Active = null;
                Notice += " 上次未完成的专注已记为中断，不授予完成成就。";
                if (Healthy) Save();
            }
        }
        public void Save() {
            if (!Healthy) throw new IOException(Notice);
            AtomicWrite(Path.Combine(Root, "state.json"), Serializer().Serialize(Data));
        }
        public static void AtomicWrite(string path, string text) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true);
            else File.Move(temp, path);
        }
    }
    public static class Logic {
        public static double SafeDelta(double previous, double now) {
            double delta = now - previous;
            return delta > 0 && delta <= 2.5 ? delta : 0;
        }
        public static void Advance(Session session, double seconds) {
            if (session == null || seconds < 0 || Double.IsNaN(seconds) || Double.IsInfinity(seconds)) return;
            session.ElapsedSeconds = Math.Min(session.TargetSeconds, session.ElapsedSeconds + seconds);
        }
        public static string HostOnly(string raw) {
            if (String.IsNullOrWhiteSpace(raw)) return "";
            raw = raw.Trim();
            if (raw.Any(Char.IsWhiteSpace)) return "";
            Uri uri;
            if (!raw.Contains("://")) raw = "https://" + raw;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out uri)) return "";
            if (uri.IsFile) return "本地文件";
            if (uri.Scheme != "http" && uri.Scheme != "https") return "";
            if (uri.HostNameType == UriHostNameType.Unknown || String.IsNullOrEmpty(uri.Host)) return "";
            // A single word in an address bar may be a search query, not a domain.
            if (!uri.Host.Contains(".") && uri.Host != "localhost" && uri.HostNameType != UriHostNameType.IPv6) return "";
            return uri.IdnHost.ToLowerInvariant();
        }
        public static void AddUsage(State state, DateTimeOffset end, double seconds, string kind, string name) {
            if (seconds <= 0 || seconds > 2.5 || String.IsNullOrEmpty(name)) return;
            DateTimeOffset cursor = end.AddSeconds(-seconds);
            while (cursor < end) {
                DateTimeOffset midnight = new DateTimeOffset(cursor.Date.AddDays(1), cursor.Offset);
                DateTimeOffset stop = midnight < end ? midnight : end;
                string date = cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                Usage row = state.Usage.FirstOrDefault(x => x.Date == date && x.Kind == kind && x.Name == name);
                if (row == null) { row = new Usage { Date = date, Kind = kind, Name = name }; state.Usage.Add(row); }
                row.Seconds += (stop - cursor).TotalSeconds; cursor = stop;
            }
        }
        public static string Duration(double seconds) {
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        }
        public static int LongestStreak(IEnumerable<Session> sessions) {
            var days = sessions.Where(x => x.Status == "completed").Select(x => x.Ended.Date).Distinct().OrderBy(x => x).ToList();
            int best = 0, run = 0; DateTime last = DateTime.MinValue;
            foreach (var day in days) { run = day == last.AddDays(1) ? run + 1 : 1; best = Math.Max(best, run); last = day; }
            return best;
        }
        public static List<Achievement> Achievements(State state) {
            var done = state.Sessions.Where(x => x.Status == "completed").ToList();
            double total = done.Sum(x => x.ElapsedSeconds);
            double chat=state.Usage.Where(x=>x.Kind=="app" && String.Equals(x.Name,"ChatGPT.exe",StringComparison.OrdinalIgnoreCase)).Sum(x=>x.Seconds)/3600;
            double wps=state.Usage.Where(x=>x.Kind=="app" && new [] {"wps.exe","et.exe","wpp.exe","wpspdf.exe"}.Contains((x.Name??"").ToLowerInvariant())).Sum(x=>x.Seconds)/3600;
            var result=new List<Achievement> {
                new Achievement("first",0,"第一颗番茄","完整完成一次专注","恭喜，你已经不是一颗生番茄了。",done.Count,1,"次"),
                new Achievement("sprout",1,"小番茄出土","完成一次至少 10 分钟的专注","今天先长两片叶子。",done.Count(x=>x.TargetSeconds>=600),1,"次"),
                new Achievement("scholar",2,"知识番子","完成一次至少 45 分钟的专注","知识进脑，番茄熟了。",done.Count(x=>x.TargetSeconds>=2700),1,"次"),
                new Achievement("rocket",3,"百分钟远航","完成一次至少 100 分钟的专注","地球太吵，本番先飞一步。",done.Count(x=>x.TargetSeconds>=6000),1,"次"),
                new Achievement("basket",4,"番茄批发商","累计完成 10 次专注","散装的努力，现在可以整箱发货。",done.Count,10,"次"),
                new Achievement("wizard",5,"时间煮术师","累计完成 10 小时专注","再熬一会儿，就能变成番茄浓汤。",total/3600,10,"小时"),
                new Achievement("trio",6,"番茄三连","连续 3 个自然日完成专注","昨天的你给今天点了个赞。",LongestStreak(done),3,"天"),
                new Achievement("week",7,"七日熟成","连续 7 个自然日完成专注","一周的阳光，都收进肚子里。",LongestStreak(done),7,"天"),
                new Achievement("king",8,"番茄大帝","累计完成 50 小时专注","朕打下的江山，全是待办清单。",total/3600,50,"小时"),
                new Achievement("silicon",9,"硅基生物","累计活跃使用 ChatGPT 10 小时","碳基外壳，硅基代餐。",chat,10,"小时"),
                new Achievement("rotten",10,"烂番茄","累计主动提前解锁 3 次","熟过头了？下次再种一颗。",state.Sessions.Count(x=>x.Status=="unlocked"),3,"次"),
                new Achievement("office",11,"表格炼金术士","累计活跃使用 WPS 5 小时","把单元格里的日子，炼成金子。",wps,5,"小时")
            };
            foreach(var item in result) item.Earned=state.Awards!=null && state.Awards.Contains(item.Id);
            return result;
        }
        public static List<Achievement> CaptureAwards(State state) {
            if(state.Awards==null) state.Awards=new List<string>();
            var fresh=Achievements(state).Where(x=>x.Value>=x.Target && !state.Awards.Contains(x.Id)).ToList();
            foreach(var item in fresh) state.Awards.Add(item.Id);
            return fresh;
        }

        public static string StatusName(string status) { return status == "completed" ? "已完成" : status == "unlocked" ? "四键解锁" : "意外中断"; }
    }
    public class Achievement {
        public string Id, Name, Description, Quip, Unit;
        public int Image;
        public double Value, Target;
        public bool Earned;
        public bool Unlocked { get { return Earned || Value>=Target; } }
        public Achievement(string id,int image,string name,string description,string quip,double value,double target,string unit) {
            Id=id; Image=image; Name=name; Description=description; Quip=quip; Value=value; Target=target; Unit=unit;
        }
    }

    public static class Reports {
        static string Cell(string text) { return text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " "); }
        static string Csv(string value) { return "\"" + value.Replace("\"", "\"\"") + "\""; }
        public static void Generate(Store store, string date) {
            var sessions = store.Data.Sessions.Where(x => x.Ended.ToString("yyyy-MM-dd") == date).ToList();
            var rows = store.Data.Usage.Where(x => x.Date == date).OrderByDescending(x => x.Seconds).ToList();
            var done = sessions.Where(x => x.Status == "completed").ToList();
            StringBuilder md = new StringBuilder("# TomatoLock · " + date + "\n\n");
            md.AppendFormat("完成 **{0}** 次，专注 **{1}**；中断 **{2}** 次。\n\n", done.Count, Logic.Duration(done.Sum(x=>x.ElapsedSeconds)), sessions.Count-done.Count);
            md.Append("专注按结束日归档；应用/网站按使用发生日归档。应用统计为前台活跃时间，连续无输入超过设置阈值后不再计时；网站是浏览器时间的明细，不能与应用时间相加。未开启统计或无法读取的时间不会被猜测补齐。\n\n");
            md.Append("## 专注记录\n\n| 开始 | 结束 | 目标 | 实际 | 结果 |\n|---|---|---|---|---|\n");
            foreach(var s in sessions) md.AppendFormat("| {0:HH:mm:ss} | {1:HH:mm:ss} | {2} | {3} | {4} |\n", s.Started, s.Ended, Logic.Duration(s.TargetSeconds), Logic.Duration(s.ElapsedSeconds), Logic.StatusName(s.Status));
            foreach(string kind in new [] {"app", "site"}) {
                md.Append("\n## " + (kind == "app" ? "应用" : "网站（仅域名）") + "\n\n| 名称 | 活跃时长 |\n|---|---|\n");
                foreach(var r in rows.Where(x=>x.Kind==kind)) md.Append("| " + Cell(r.Name) + " | " + Logic.Duration(r.Seconds) + " |\n");
            }
            string dir = Path.Combine(store.Root, "reports");
            Store.AtomicWrite(Path.Combine(dir, date + ".md"), md.ToString());
            StringBuilder csv = new StringBuilder("date,kind,name,seconds\r\n");
            foreach(var r in rows) csv.Append(Csv(date)+","+Csv(r.Kind)+","+Csv(r.Name)+","+Math.Round(r.Seconds,2).ToString(CultureInfo.InvariantCulture)+"\r\n");
            Store.AtomicWrite(Path.Combine(dir, date + ".csv"), "\uFEFF" + csv);
        }
        public static void CatchUp(Store store) {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            var dates = store.Data.Usage.Select(x=>x.Date).Concat(store.Data.Sessions.Select(x=>x.Ended.ToString("yyyy-MM-dd"))).Distinct();
            foreach(string date in dates) if(String.CompareOrdinal(date, today)<0 && !File.Exists(Path.Combine(store.Root,"reports",date+".md"))) Generate(store,date);
        }
    }
}
