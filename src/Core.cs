using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace TomatoLock {
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
        public State() { Schema = 1; Settings = new Settings(); Sessions = new List<Session>(); Usage = new List<Usage>(); }
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
                throw new InvalidDataException("���ݸ�ʽ����֧��");
            return value;
        }
        void Load() {
            string path = Path.Combine(Root, "state.json");
            if (!File.Exists(path)) {
                if (File.Exists(path + ".bak")) { Data = Read(path + ".bak"); Notice = "�Ѵӱ��ݻָ���¼��"; }
                else Data = new State();
            } else {
                try { Data = Read(path); }
                catch {
                    File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.Ticks, false);
                    if (File.Exists(path + ".bak")) {
                        try { Data = Read(path + ".bak"); Notice = "��¼�ļ��𻵣��ѻָ��ϴα��ݣ����ļ�������"; File.Delete(path); }
                        catch { Data = new State(); Healthy = false; Notice = "��¼�����ݾ��𻵡�ԭ�ļ��ѱ��������޸����ٿ�ʼ��"; }
                    } else { Data = new State(); Healthy = false; Notice = "��¼�ļ��𻵣��ѱ���ԭ�������޸����ٿ�ʼ��"; }
                }
            }
            if (Data.Active != null) {
                Data.Active.Status = "interrupted";
                Data.Active.Ended = DateTimeOffset.Now;
                Data.Sessions.Add(Data.Active); Data.Active = null;
                Notice += " �ϴ�δ��ɵ�רע�Ѽ�Ϊ�жϣ���������ɳɾ͡�";
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
            if (uri.IsFile) return "�����ļ�";
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
            return new List<Achievement> {
                new Achievement("��һ�ŷ���", "�������һ��רע", done.Count, 1),
                new Achievement("������", "���һ������ 10 ���ӵ�רע", done.Count(x=>x.TargetSeconds >= 600), 1),
                new Achievement("��������", "���һ������ 45 ���ӵ�רע", done.Count(x=>x.TargetSeconds >= 2700), 1),
                new Achievement("�ٷ���Զ��", "���һ������ 100 ���ӵ�רע", done.Count(x=>x.TargetSeconds >= 6000), 1),
                new Achievement("���ٳɶ�", "�ۼ���� 10 ��רע", done.Count, 10),
                new Achievement("ʱ���ղؼ�", "�ۼ���� 10 Сʱ", total / 3600, 10),
                new Achievement("�Ȳ�ǰ��", "���� 3 ����Ȼ�����רע", LongestStreak(done), 3),
                new Achievement("һ�ܵ�Լ��", "���� 7 ����Ȼ�����רע", LongestStreak(done), 7),
                new Achievement("СС��̱�", "�ۼ���� 50 Сʱ", total / 3600, 50)
            };
        }
        public static string StatusName(string status) { return status == "completed" ? "�����" : status == "unlocked" ? "�ļ�����" : "�����ж�"; }
    }
    public class Achievement {
        public string Name, Description; public double Value, Target;
        public bool Unlocked { get { return Value >= Target; } }
        public Achievement(string name, string description, double value, double target) { Name = name; Description = description; Value = value; Target = target; }
    }
    public static class Reports {
        static string Cell(string text) { return text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " "); }
        static string Csv(string value) { return "\"" + value.Replace("\"", "\"\"") + "\""; }
        public static void Generate(Store store, string date) {
            var sessions = store.Data.Sessions.Where(x => x.Ended.ToString("yyyy-MM-dd") == date).ToList();
            var rows = store.Data.Usage.Where(x => x.Date == date).OrderByDescending(x => x.Seconds).ToList();
            var done = sessions.Where(x => x.Status == "completed").ToList();
            StringBuilder md = new StringBuilder("# TomatoLock �� " + date + "\n\n");
            md.AppendFormat("��� **{0}** �Σ�רע **{1}**���ж� **{2}** �Ρ�\n\n", done.Count, Logic.Duration(done.Sum(x=>x.ElapsedSeconds)), sessions.Count-done.Count);
            md.Append("רע�������չ鵵��Ӧ��/��վ��ʹ�÷����չ鵵��Ӧ��ͳ��Ϊǰ̨��Ծʱ�䣬���������볬��������ֵ���ټ�ʱ����վ�������ʱ�����ϸ��������Ӧ��ʱ����ӡ�δ����ͳ�ƻ��޷���ȡ��ʱ�䲻�ᱻ�²ⲹ�롣\n\n");
            md.Append("## רע��¼\n\n| ��ʼ | ���� | Ŀ�� | ʵ�� | ��� |\n|---|---|---|---|---|\n");
            foreach(var s in sessions) md.AppendFormat("| {0:HH:mm:ss} | {1:HH:mm:ss} | {2} | {3} | {4} |\n", s.Started, s.Ended, Logic.Duration(s.TargetSeconds), Logic.Duration(s.ElapsedSeconds), Logic.StatusName(s.Status));
            foreach(string kind in new [] {"app", "site"}) {
                md.Append("\n## " + (kind == "app" ? "Ӧ��" : "��վ����������") + "\n\n| ���� | ��Ծʱ�� |\n|---|---|\n");
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
