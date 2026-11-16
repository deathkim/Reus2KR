// 범용 한글화 설치기 (Generic Unity localization installer)
// - Windows 기본 .NET Framework 4.x 로 동작하는 단일 exe. 추가 프로그램 설치 불필요.
// - 번역/플러그인/패치 파일은 exe 안(payload.zip 리소스)에 들어 있으며, package.json 매니페스트가 설치 방식을 정의한다.
// - 방식 1: 플러그인(BepInEx) — 파일 복사. 방식 2: 원본 파일 직접 패치 — 해시 검증 + 델타(RDP1) 적용 + 백업/복원.
// - C# 5 문법만 사용한다(Framework 4 csc 로 빌드).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LocInstaller
{
    internal static class Json
    {
        private static readonly JavaScriptSerializer Ser = MakeSer();
        private static JavaScriptSerializer MakeSer()
        {
            JavaScriptSerializer s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            return s;
        }
        public static Dictionary<string, object> ParseObject(string text)
        {
            return (Dictionary<string, object>)Ser.DeserializeObject(text);
        }
        public static string Write(object o) { return Ser.Serialize(o); }
        public static Dictionary<string, object> D(object o, string key)
        {
            if (o == null) return null;
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }
        public static string S(object o, string key, string def)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            return Convert.ToString(v);
        }
        public static bool B(object o, string key, bool def)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            return def;
        }
        public static List<object> L(object o, string key)
        {
            List<object> result = new List<object>();
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d == null) return result;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return result;
            IEnumerable e = v as IEnumerable;
            if (e == null || v is string) return result;
            foreach (object item in e) result.Add(item);
            return result;
        }
    }

    internal sealed class Entry
    {
        public string Path;
        public bool Backup;
        public string Group;
    }

    internal sealed class Engine
    {
        public Dictionary<string, object> M;
        public ZipArchive Zip;
        public Action<string> Log;
        public string Id;
        public string Name;
        public string Version;

        public Engine(Stream payload, Action<string> log)
        {
            Log = log;
            Zip = new ZipArchive(payload, ZipArchiveMode.Read);
            byte[] bytes = ReadEntry("package.json");
            if (bytes == null) throw new InvalidOperationException("payload 에 package.json 이 없습니다.");
            M = Json.ParseObject(Encoding.UTF8.GetString(bytes));
            Id = Json.S(M, "id", "project");
            Name = Json.S(M, "name", Id);
            Version = Json.S(M, "version", "");
        }

        public byte[] ReadEntry(string name)
        {
            ZipArchiveEntry e = Zip.GetEntry(name.Replace('\\', '/'));
            if (e == null) return null;
            using (Stream s = e.Open())
            using (MemoryStream ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private Stream OpenEntry(string name)
        {
            ZipArchiveEntry e = Zip.GetEntry(name.Replace('\\', '/'));
            if (e == null) throw new FileNotFoundException("payload 항목을 찾을 수 없습니다: " + name);
            return e.Open();
        }

        // ---------- 게임 폴더 ----------
        public bool IsGameDir(string dir)
        {
            if (String.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            List<object> detect = Json.L(Json.D(M, "game"), "detectFiles");
            if (detect.Count == 0) return true;
            foreach (object f in detect)
                if (!File.Exists(System.IO.Path.Combine(dir, Convert.ToString(f).Replace('/', '\\')))) return false;
            return true;
        }

        public List<string> FindGameDirs(string exeDir)
        {
            List<string> found = new List<string>();
            List<string> candidates = new List<string>();
            if (!String.IsNullOrEmpty(exeDir))
            {
                candidates.Add(exeDir);
                try { candidates.Add(Directory.GetParent(exeDir).FullName); } catch (Exception) { }
            }
            Dictionary<string, object> game = Json.D(M, "game");
            foreach (object p in Json.L(game, "searchPaths")) candidates.Add(Convert.ToString(p));
            string steam = Json.S(game, "steamFolder", null);
            if (!String.IsNullOrEmpty(steam))
            {
                string root = SteamRoot();
                if (root != null)
                {
                    candidates.Add(System.IO.Path.Combine(root, "steamapps\\common\\" + steam));
                    try
                    {
                        string vdf = System.IO.Path.Combine(root, "steamapps\\libraryfolders.vdf");
                        if (File.Exists(vdf))
                        {
                            foreach (string line in File.ReadAllLines(vdf))
                            {
                                int i = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
                                if (i < 0) continue;
                                string rest = line.Substring(i + 6).Trim().Trim('"').Replace("\\\\", "\\");
                                candidates.Add(System.IO.Path.Combine(rest, "steamapps\\common\\" + steam));
                            }
                        }
                    }
                    catch (Exception) { }
                }
            }
            string gog = Json.S(game, "gogId", null);
            if (!String.IsNullOrEmpty(gog))
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\GOG.com\\Games\\" + gog))
                    {
                        if (k != null && k.GetValue("path") != null) candidates.Add(Convert.ToString(k.GetValue("path")));
                    }
                }
                catch (Exception) { }
            }
            foreach (string c in candidates)
            {
                try
                {
                    string full = System.IO.Path.GetFullPath(c);
                    if (IsGameDir(full) && !found.Contains(full)) found.Add(full);
                }
                catch (Exception) { }
            }
            return found;
        }

        private static string SteamRoot()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam"))
                {
                    if (k != null && k.GetValue("SteamPath") != null)
                        return Convert.ToString(k.GetValue("SteamPath")).Replace('/', '\\');
                }
            }
            catch (Exception) { }
            return null;
        }

        public bool GameRunning(string dir)
        {
            string exe = Json.S(Json.D(M, "game"), "exe", null);
            if (String.IsNullOrEmpty(exe)) return false;
            string name = System.IO.Path.GetFileNameWithoutExtension(exe);
            try
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        string path = p.MainModule.FileName;
                        if (path != null && path.StartsWith(System.IO.Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch (Exception) { return true; }
                }
            }
            catch (Exception) { }
            return false;
        }

        // ---------- 상태 ----------
        private string StateDir(string game) { return System.IO.Path.Combine(game, ".locpatch\\" + Id); }
        private string StateFile(string game) { return System.IO.Path.Combine(StateDir(game), "install.json"); }
        private string BackupRoot(string game) { return System.IO.Path.Combine(StateDir(game), "backup"); }

        public Dictionary<string, object> ReadState(string game)
        {
            try
            {
                string f = StateFile(game);
                if (!File.Exists(f)) return null;
                return Json.ParseObject(File.ReadAllText(f, Encoding.UTF8));
            }
            catch (Exception) { return null; }
        }

        public string StatusText(string game)
        {
            Dictionary<string, object> st = ReadState(game);
            if (st == null) return "설치되지 않음";
            return "설치됨 — " + Json.S(st, "modeLabel", Json.S(st, "mode", "")) + " (버전 " + Json.S(st, "version", "?") + ")";
        }

        // ---------- 공통 유틸 ----------
        private static string Rel(string rel)
        {
            string r = rel.Replace('/', '\\').TrimStart('\\');
            foreach (string seg in r.Split('\\'))
                if (seg == "..") throw new InvalidOperationException("허용되지 않는 경로: " + rel);
            return r;
        }

        private static string Full(string game, string rel)
        {
            string g = System.IO.Path.GetFullPath(game).TrimEnd('\\') + "\\";
            string p = System.IO.Path.GetFullPath(System.IO.Path.Combine(game, Rel(rel)));
            if (!p.StartsWith(g, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("게임 폴더 밖의 경로입니다: " + rel);
            return p;
        }

        public static string Sha256(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (SHA256 sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(fs));
            }
        }

        private static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private void EnsureDir(string file)
        {
            string d = System.IO.Path.GetDirectoryName(file);
            if (!Directory.Exists(d)) Directory.CreateDirectory(d);
        }

        // 설치 대상 파일 하나를 기록하며 복사한다.
        private void PlaceFile(string game, string rel, string payloadSrc, string expectSha, string group, List<Entry> entries)
        {
            string dest = Full(game, rel);
            Entry en = new Entry();
            en.Path = Rel(rel);
            en.Group = group;
            if (File.Exists(dest))
            {
                BackupFile(game, rel);
                en.Backup = true;
            }
            EnsureDir(dest);
            string tmp = dest + ".loctmp";
            using (Stream s = OpenEntry(payloadSrc))
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                s.CopyTo(fs);
            if (!String.IsNullOrEmpty(expectSha) && !String.Equals(Sha256(tmp), expectSha, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new InvalidDataException("배포본 파일이 손상되었습니다: " + rel);
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
            entries.Add(en);
        }

        private void BackupFile(string game, string rel)
        {
            string src = Full(game, rel);
            string bak = System.IO.Path.Combine(BackupRoot(game), Rel(rel));
            EnsureDir(bak);
            if (!File.Exists(bak)) File.Copy(src, bak, false);
        }

        private void PlaceFileList(string game, IEnumerable list, string group, List<Entry> entries)
        {
            foreach (object o in list)
            {
                string src = Json.S(o, "src", null);
                string dst = Json.S(o, "dst", null);
                if (src == null || dst == null) continue;
                PlaceFile(game, dst, src, Json.S(o, "sha256", null), group, entries);
                Log("  복사: " + dst);
            }
        }

        // ---------- 검증 ----------
        public List<string> Verify(string game, out bool blocked)
        {
            List<string> messages = new List<string>();
            blocked = false;
            foreach (object v in Json.L(M, "verify"))
            {
                string file = Json.S(v, "file", null);
                if (file == null) continue;
                string path = Full(game, file);
                if (!File.Exists(path)) continue;
                string actual = Sha256(path);
                bool ok = false;
                string single = Json.S(v, "sha256", null);
                if (single != null && String.Equals(single, actual, StringComparison.OrdinalIgnoreCase)) ok = true;
                foreach (object h in Json.L(v, "sha256List"))
                    if (String.Equals(Convert.ToString(h), actual, StringComparison.OrdinalIgnoreCase)) ok = true;
                if (!ok)
                {
                    string msg = Json.S(v, "message", "지원하지 않는 게임 버전일 수 있습니다: " + file);
                    messages.Add(msg);
                    if (Json.S(v, "onMismatch", "warn") == "block") blocked = true;
                }
            }
            return messages;
        }

        // ---------- 설치 ----------
        public List<string> ModeIds()
        {
            List<string> ids = new List<string>();
            Dictionary<string, object> modes = Json.D(M, "modes");
            if (modes != null) foreach (string k in modes.Keys) ids.Add(k);
            return ids;
        }

        public string ModeLabel(string mode)
        {
            return Json.S(Json.D(Json.D(M, "modes"), mode), "label", mode);
        }

        public string ModeDescription(string mode)
        {
            return Json.S(Json.D(Json.D(M, "modes"), mode), "description", "");
        }

        public void Install(string game, string mode)
        {
            if (!IsGameDir(game)) throw new InvalidOperationException("게임 폴더가 올바르지 않습니다.");
            Dictionary<string, object> modeDef = Json.D(Json.D(M, "modes"), mode);
            if (modeDef == null) throw new InvalidOperationException("알 수 없는 설치 방식: " + mode);
            if (GameRunning(game)) throw new InvalidOperationException("게임이 실행 중입니다. 게임을 종료한 뒤 다시 시도하세요.");

            bool blocked;
            List<string> warns = Verify(game, out blocked);
            foreach (string w in warns) Log("경고: " + w);
            if (blocked) throw new InvalidOperationException("지원하지 않는 게임 버전입니다: " + String.Join(" / ", warns.ToArray()));

            if (ReadState(game) != null)
            {
                Log("기존 설치를 먼저 제거합니다.");
                Uninstall(game);
            }

            List<Entry> entries = new List<Entry>();
            bool bepinexByUs = false;
            try
            {
                if (Json.B(modeDef, "requiresBepInEx", false))
                {
                    Dictionary<string, object> bep = Json.D(M, "bepinex");
                    string detect = Json.S(bep, "detect", "BepInEx/core/BepInEx.dll");
                    if (File.Exists(Full(game, detect)))
                    {
                        Log("BepInEx 가 이미 설치되어 있어 그대로 사용합니다.");
                    }
                    else
                    {
                        Log("BepInEx 를 설치합니다.");
                        PlaceFileList(game, Json.L(bep, "files"), "bepinex", entries);
                        bepinexByUs = true;
                    }
                }

                foreach (object p in Json.L(modeDef, "patches"))
                    ApplyPatch(game, p, entries);

                PlaceFileList(game, Json.L(modeDef, "files"), "mode", entries);
            }
            catch (Exception ex)
            {
                Log("오류: " + ex.Message + " — 변경 사항을 되돌립니다.");
                Rollback(game, entries);
                throw;
            }

            Dictionary<string, object> state = new Dictionary<string, object>();
            state["id"] = Id;
            state["version"] = Version;
            state["mode"] = mode;
            state["modeLabel"] = ModeLabel(mode);
            state["time"] = DateTime.Now.ToString("s");
            state["bepinexInstalledByUs"] = bepinexByUs;
            List<object> list = new List<object>();
            foreach (Entry e in entries)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["path"] = e.Path;
                d["backup"] = e.Backup;
                d["group"] = e.Group;
                list.Add(d);
            }
            state["entries"] = list;
            Directory.CreateDirectory(StateDir(game));
            File.WriteAllText(StateFile(game), Json.Write(state), new UTF8Encoding(false));
            Log("설치가 완료되었습니다. (" + ModeLabel(mode) + ")");
        }

        private void ApplyPatch(string game, object patch, List<Entry> entries)
        {
            string target = Json.S(patch, "target", null);
            string deltaName = Json.S(patch, "delta", null);
            string srcSha = Json.S(patch, "sourceSha256", null);
            string resSha = Json.S(patch, "resultSha256", null);
            if (target == null || deltaName == null) throw new InvalidDataException("패치 정의가 올바르지 않습니다.");
            string path = Full(game, target);
            if (!File.Exists(path)) throw new FileNotFoundException("패치 대상 파일이 없습니다: " + target);
            Log("  패치: " + target + " (해시 확인 중)");
            string cur = Sha256(path);
            if (resSha != null && String.Equals(cur, resSha, StringComparison.OrdinalIgnoreCase))
            {
                Log("  이미 패치된 파일입니다. 건너뜁니다.");
                return;
            }
            if (srcSha != null && !String.Equals(cur, srcSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("원본 파일이 예상과 다릅니다(게임 버전 불일치 또는 이미 변경됨): " + target);

            BackupFile(game, target);
            Entry en = new Entry();
            en.Path = Rel(target);
            en.Backup = true;
            en.Group = "patch";
            entries.Add(en);

            string tmp = path + ".loctmp";
            try
            {
                using (Stream delta = OpenEntry(deltaName))
                    Delta.Apply(path, delta, tmp);
                if (resSha != null && !String.Equals(Sha256(tmp), resSha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("패치 결과 검증에 실패했습니다: " + target);
                File.Delete(path);
                File.Move(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        // ---------- 제거 ----------
        public void Uninstall(string game)
        {
            Dictionary<string, object> st = ReadState(game);
            if (st == null) { Log("설치된 항목이 없습니다."); return; }
            if (GameRunning(game)) throw new InvalidOperationException("게임이 실행 중입니다. 게임을 종료한 뒤 다시 시도하세요.");
            List<Entry> entries = new List<Entry>();
            foreach (object o in Json.L(st, "entries"))
            {
                Entry e = new Entry();
                e.Path = Json.S(o, "path", null);
                e.Backup = Json.B(o, "backup", false);
                e.Group = Json.S(o, "group", "mode");
                if (e.Path != null) entries.Add(e);
            }
            Rollback(game, entries);
            try { Directory.Delete(StateDir(game), true); } catch (Exception) { }
            try
            {
                string parent = System.IO.Path.Combine(game, ".locpatch");
                if (Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0) Directory.Delete(parent);
            }
            catch (Exception) { }
            Log("제거가 완료되었습니다.");
        }

        private void Rollback(string game, List<Entry> entries)
        {
            bool keepBepInEx = false;
            // 1) bepinex 그룹을 제외한 항목을 먼저 되돌린다.
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry e = entries[i];
                if (e.Group == "bepinex") continue;
                RestoreOne(game, e);
            }
            // 2) 다른 플러그인이 남아 있으면 BepInEx 는 유지한다.
            bool hasBep = false;
            foreach (Entry e in entries) if (e.Group == "bepinex") hasBep = true;
            if (hasBep)
            {
                string plugins = System.IO.Path.Combine(game, "BepInEx\\plugins");
                if (Directory.Exists(plugins) && Directory.GetFiles(plugins, "*", SearchOption.AllDirectories).Length > 0)
                    keepBepInEx = true;
                if (keepBepInEx)
                {
                    Log("  다른 플러그인이 있어 BepInEx 는 남겨 둡니다.");
                }
                else
                {
                    for (int i = entries.Count - 1; i >= 0; i--)
                        if (entries[i].Group == "bepinex") RestoreOne(game, entries[i]);
                    foreach (string junk in new string[] { "BepInEx\\cache", "BepInEx\\LogOutput.log" })
                    {
                        try
                        {
                            string p = System.IO.Path.Combine(game, junk);
                            if (Directory.Exists(p)) Directory.Delete(p, true);
                            else if (File.Exists(p)) File.Delete(p);
                        }
                        catch (Exception) { }
                    }
                    TryRemoveEmpty(System.IO.Path.Combine(game, "BepInEx\\plugins"), game);
                    TryRemoveEmpty(System.IO.Path.Combine(game, "BepInEx\\patchers"), game);
                    TryRemoveEmpty(System.IO.Path.Combine(game, "BepInEx\\core"), game);
                }
            }
        }

        private void RestoreOne(string game, Entry e)
        {
            string dest = Full(game, e.Path);
            try
            {
                if (e.Backup)
                {
                    string bak = System.IO.Path.Combine(BackupRoot(game), Rel(e.Path));
                    if (File.Exists(bak))
                    {
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Copy(bak, dest, true);
                        Log("  복원: " + e.Path);
                    }
                    else Log("  경고: 백업이 없어 복원하지 못했습니다: " + e.Path);
                }
                else if (File.Exists(dest))
                {
                    File.Delete(dest);
                    Log("  삭제: " + e.Path);
                }
            }
            catch (Exception ex)
            {
                Log("  경고: " + e.Path + " 처리 실패 — " + ex.Message);
            }
            TryRemoveEmptyChain(System.IO.Path.GetDirectoryName(dest), game);
        }

        private void TryRemoveEmpty(string dir, string stop)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
            }
            catch (Exception) { }
            TryRemoveEmptyChain(System.IO.Path.GetDirectoryName(dir), stop);
        }

        private void TryRemoveEmptyChain(string dir, string stop)
        {
            string root = System.IO.Path.GetFullPath(stop).TrimEnd('\\');
            try
            {
                while (!String.IsNullOrEmpty(dir))
                {
                    string d = System.IO.Path.GetFullPath(dir).TrimEnd('\\');
                    if (String.Equals(d, root, StringComparison.OrdinalIgnoreCase) || d.Length <= root.Length) break;
                    if (!Directory.Exists(d) || Directory.GetFileSystemEntries(d).Length > 0) break;
                    Directory.Delete(d);
                    dir = System.IO.Path.GetDirectoryName(d);
                }
            }
            catch (Exception) { }
        }
    }

    // RDP1 델타 형식: "RDP1" | int64 목표크기 | ops...  (0=COPY off,len / 1=ADD len,data / 2=END)
    internal static class Delta
    {
        public static void Apply(string sourcePath, Stream delta, string outPath)
        {
            using (BinaryReader r = new BinaryReader(delta))
            using (FileStream src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream dst = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] magic = r.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != 'R' || magic[1] != 'D' || magic[2] != 'P' || magic[3] != '1')
                    throw new InvalidDataException("지원하지 않는 패치 형식입니다.");
                long target = r.ReadInt64();
                byte[] buf = new byte[1 << 20];
                while (true)
                {
                    int op = r.ReadByte();
                    if (op == 2) break;
                    if (op == 0)
                    {
                        long off = r.ReadInt64();
                        long len = r.ReadInt64();
                        src.Seek(off, SeekOrigin.Begin);
                        Pump(src, dst, len, buf);
                    }
                    else if (op == 1)
                    {
                        long len = r.ReadInt64();
                        Pump(r.BaseStream, dst, len, buf);
                    }
                    else throw new InvalidDataException("손상된 패치 파일입니다.");
                }
                if (dst.Length != target) throw new InvalidDataException("패치 결과 크기가 맞지 않습니다.");
            }
        }

        private static void Pump(Stream from, Stream to, long len, byte[] buf)
        {
            while (len > 0)
            {
                int n = from.Read(buf, 0, (int)Math.Min(buf.Length, len));
                if (n <= 0) throw new EndOfStreamException("패치 데이터가 부족합니다.");
                to.Write(buf, 0, n);
                len -= n;
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Engine engine;
        private TextBox pathBox;
        private Label detectLabel;
        private Label stateLabel;
        private Label modeDesc;
        private TextBox logBox;
        private Button installBtn;
        private Button uninstallBtn;
        private Button browseBtn;
        private Button noticeBtn;
        private readonly List<RadioButton> modeButtons = new List<RadioButton>();
        private readonly string exeDir;

        public MainForm(Engine eng, string exeDirectory)
        {
            engine = eng;
            exeDir = exeDirectory;
            Text = engine.Name + " 설치 프로그램";
            Font = new Font("맑은 고딕", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 560);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            Label title = new Label();
            title.Text = engine.Name + (engine.Version.Length > 0 ? "  v" + engine.Version : "");
            title.Font = new Font("맑은 고딕", 13f, FontStyle.Bold);
            title.SetBounds(16, 12, 608, 30);
            Controls.Add(title);

            string sub = Json.S(engine.M, "description", "");
            Label subtitle = new Label();
            subtitle.Text = sub;
            subtitle.ForeColor = Color.DimGray;
            subtitle.SetBounds(16, 42, 608, 34);
            Controls.Add(subtitle);

            GroupBox gPath = new GroupBox();
            gPath.Text = "1. 게임 폴더";
            gPath.SetBounds(16, 80, 608, 84);
            pathBox = new TextBox();
            pathBox.SetBounds(12, 24, 480, 24);
            pathBox.TextChanged += delegate { RefreshState(); };
            browseBtn = new Button();
            browseBtn.Text = "찾아보기...";
            browseBtn.SetBounds(500, 22, 96, 27);
            browseBtn.Click += OnBrowse;
            detectLabel = new Label();
            detectLabel.SetBounds(12, 54, 584, 22);
            gPath.Controls.Add(pathBox);
            gPath.Controls.Add(browseBtn);
            gPath.Controls.Add(detectLabel);
            Controls.Add(gPath);

            GroupBox gMode = new GroupBox();
            gMode.Text = "2. 설치 방식";
            int count = engine.ModeIds().Count;
            int modeHeight = 30 + count * 26 + 52;
            gMode.SetBounds(16, 172, 608, modeHeight);
            int y = 24;
            modeDesc = new Label();
            foreach (string id in engine.ModeIds())
            {
                RadioButton rb = new RadioButton();
                rb.Text = engine.ModeLabel(id);
                rb.Tag = id;
                rb.AutoSize = true;
                rb.Location = new Point(14, y);
                rb.CheckedChanged += delegate { UpdateModeDesc(); };
                modeButtons.Add(rb);
                gMode.Controls.Add(rb);
                y += 26;
            }
            modeDesc.ForeColor = Color.DimGray;
            modeDesc.SetBounds(14, y + 2, 580, 44);
            gMode.Controls.Add(modeDesc);
            Controls.Add(gMode);
            if (modeButtons.Count > 0) modeButtons[0].Checked = true;

            int top = 172 + modeHeight + 8;
            stateLabel = new Label();
            stateLabel.Font = new Font("맑은 고딕", 9f, FontStyle.Bold);
            stateLabel.SetBounds(16, top, 608, 22);
            Controls.Add(stateLabel);

            installBtn = new Button();
            installBtn.Text = "설치";
            installBtn.SetBounds(16, top + 28, 110, 32);
            installBtn.Click += OnInstall;
            uninstallBtn = new Button();
            uninstallBtn.Text = "제거(원상 복구)";
            uninstallBtn.SetBounds(132, top + 28, 130, 32);
            uninstallBtn.Click += OnUninstall;
            noticeBtn = new Button();
            noticeBtn.Text = "안내";
            noticeBtn.SetBounds(268, top + 28, 80, 32);
            noticeBtn.Click += OnNotice;
            Button close = new Button();
            close.Text = "닫기";
            close.SetBounds(514, top + 28, 110, 32);
            close.Click += delegate { Close(); };
            Controls.Add(installBtn);
            Controls.Add(uninstallBtn);
            Controls.Add(noticeBtn);
            Controls.Add(close);

            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Color.White;
            logBox.SetBounds(16, top + 68, 608, ClientSize.Height - (top + 68) - 14);
            Controls.Add(logBox);

            engine.Log = AppendLog;

            List<string> dirs = engine.FindGameDirs(exeDir);
            if (dirs.Count > 0) pathBox.Text = dirs[0];
            else RefreshState();
            UpdateModeDesc();
        }

        private void UpdateModeDesc()
        {
            foreach (RadioButton rb in modeButtons)
                if (rb.Checked) { modeDesc.Text = engine.ModeDescription((string)rb.Tag); return; }
        }

        private string SelectedMode()
        {
            foreach (RadioButton rb in modeButtons) if (rb.Checked) return (string)rb.Tag;
            return null;
        }

        private void RefreshState()
        {
            string dir = pathBox.Text.Trim();
            bool ok = engine.IsGameDir(dir);
            detectLabel.Text = ok ? "게임이 확인되었습니다." : "게임 폴더를 선택하세요. (" + Json.S(Json.D(engine.M, "game"), "exe", "") + " 이 있는 폴더)";
            detectLabel.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
            stateLabel.Text = ok ? "현재 상태: " + engine.StatusText(dir) : "";
            installBtn.Enabled = ok;
            uninstallBtn.Enabled = ok && engine.ReadState(dir) != null;
        }

        private void AppendLog(string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), text); return; }
            logBox.AppendText(text + "\r\n");
        }

        private void OnBrowse(object s, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "게임이 설치된 폴더를 선택하세요.";
                if (dlg.ShowDialog(this) == DialogResult.OK) pathBox.Text = dlg.SelectedPath;
            }
        }

        private void OnNotice(object s, EventArgs e)
        {
            string text = Json.S(engine.M, "notice", "안내 문구가 없습니다.");
            MessageBox.Show(this, text.Replace("\\n", "\r\n"), "안내", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SetBusy(bool busy)
        {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(SetBusy), busy); return; }
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            installBtn.Enabled = !busy;
            uninstallBtn.Enabled = !busy;
            browseBtn.Enabled = !busy;
            pathBox.Enabled = !busy;
            if (!busy) RefreshState();
        }

        private void RunTask(string name, Action work)
        {
            SetBusy(true);
            logBox.AppendText("== " + name + " ==\r\n");
            Thread t = new Thread(delegate ()
            {
                try { work(); }
                catch (Exception ex) { AppendLog("실패: " + ex.Message); }
                SetBusy(false);
            });
            t.IsBackground = true;
            t.Start();
        }

        private void OnInstall(object s, EventArgs e)
        {
            string dir = pathBox.Text.Trim();
            string mode = SelectedMode();
            if (mode == null) return;
            string msg = engine.ModeLabel(mode) + " 방식으로 설치합니다.\n\n게임 폴더:\n" + dir + "\n\n계속하시겠습니까?";
            if (MessageBox.Show(this, msg, "설치 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            RunTask("설치", delegate { engine.Install(dir, mode); });
        }

        private void OnUninstall(object s, EventArgs e)
        {
            string dir = pathBox.Text.Trim();
            if (MessageBox.Show(this, "설치한 한글화를 제거하고 원본 파일을 복원합니다.\n계속하시겠습니까?", "제거 확인",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            RunTask("제거", delegate { engine.Uninstall(dir); });
        }
    }

    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int pid);

        private static Stream OpenPayload(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--payload") return File.OpenRead(args[i + 1]);
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
            if (s != null)
            {
                MemoryStream ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;
                return ms;
            }
            string side = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "payload.zip");
            if (File.Exists(side)) return File.OpenRead(side);
            throw new InvalidOperationException("배포 파일(payload)을 찾을 수 없습니다.");
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return null;
        }

        private static bool Has(string[] args, string name)
        {
            foreach (string a in args) if (a == name) return true;
            return false;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (Has(args, "--silent") || Has(args, "--status") || Has(args, "--info"))
            {
                AttachConsole(-1);
                try { if (Console.IsOutputRedirected) Console.OutputEncoding = new UTF8Encoding(false); } catch (Exception) { }
                try
                {
                    Engine eng = new Engine(OpenPayload(args), delegate (string t) { Console.WriteLine(t); });
                    if (Has(args, "--info"))
                    {
                        Console.WriteLine(eng.Name + " " + eng.Version);
                        foreach (string m in eng.ModeIds()) Console.WriteLine("mode: " + m + " — " + eng.ModeLabel(m));
                        return 0;
                    }
                    string game = Arg(args, "--game");
                    if (game == null)
                    {
                        List<string> found = eng.FindGameDirs(exeDir);
                        if (found.Count == 0) { Console.WriteLine("게임 폴더를 찾지 못했습니다. --game <폴더> 를 지정하세요."); return 2; }
                        game = found[0];
                    }
                    if (Has(args, "--status")) { Console.WriteLine(eng.StatusText(game)); return 0; }
                    if (Has(args, "--uninstall")) { eng.Uninstall(game); return 0; }
                    string mode = Arg(args, "--mode");
                    if (mode == null) { List<string> ids = eng.ModeIds(); if (ids.Count > 0) mode = ids[0]; }
                    eng.Install(game, mode);
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("실패: " + ex.Message);
                    return 1;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Engine engine = new Engine(OpenPayload(args), delegate (string t) { });
                MainForm form = new MainForm(engine, exeDir);
                string snap = Arg(args, "--snapshot");
                if (snap != null)
                {
                    // 개발용: 창을 그려서 PNG 로 저장하고 종료한다.
                    form.Show();
                    Application.DoEvents();
                    using (Bitmap bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        bmp.Save(snap, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Close();
                    return 0;
                }
                Application.Run(form);
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "locinstaller_error.txt"), ex.ToString()); } catch (Exception) { }
                MessageBox.Show("설치 프로그램을 시작하지 못했습니다.\n" + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
