// Live mod information from github.com/multiplayereverything/mods-info: the central server's address,
// the latest and oldest allowed versions, and a notice, so a server move or an update needs no rebuild.
// Copy this file unchanged into a mod (src/ModInfo.cs). C# 5, .NET 3.5+ / Unity Mono, no Unity calls.
//
//   var info = new ModRelay.ModInfo("baldi", ModVersion, Path.Combine(Root, "UserData/BaldiTogether.modinfo.json"));
//   info.Start();                                   // background download, about 5 s timeout, never blocks
//   string server = info.Pick(typedByPlayer, DefaultRelay);   // typed > downloaded > cached > built-in
//   string line = info.StatusLine();                // "Update available: ..." or the notice, or ""
//   if (info.Current.TooOld) ...                    // online play needs an update
//
// The checks match mods-info/tools/validate.py; anything invalid is ignored and the last good copy (the
// cache file) or the built-in default is used. Nothing is ever downloaded or run besides the two JSON files.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ModRelay
{
    public sealed class ModInfo
    {
        public const string DefaultBaseUrl = "https://raw.githubusercontent.com/multiplayereverything/mods-info/main/";
        const int MaxBytes = 65536;

        // One validated download (or cache read). Replaced as a whole, so the main thread can read it any time.
        public sealed class Snapshot
        {
            public string Source = "none";     // "none", "cache" or "download"
            public string Address = "";        // host:port, or "" when the files don't name one
            public string ServerStatus = "";   // online, offline, maintenance
            public string ServerNotice = "";
            public string LatestVersion = "", MinVersion = "", DownloadUrl = "", Notice = "", Status = "";
            public bool UpdateAvailable, TooOld;
        }

        public readonly string GameId, ModVersion, CachePath, BaseUrl;
        volatile Snapshot current = new Snapshot();
        volatile string state = "idle";        // idle, loading, ok, failed
        volatile string error = "";
        int running;
        DateTime lastStart = DateTime.MinValue;

        public ModInfo(string gameId, string modVersion, string cachePath) : this(gameId, modVersion, cachePath, null) { }

        public ModInfo(string gameId, string modVersion, string cachePath, string baseUrl)
        {
            GameId = gameId; ModVersion = modVersion; CachePath = cachePath;
            BaseUrl = string.IsNullOrEmpty(baseUrl) ? DefaultBaseUrl : (baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
            LoadCache();
        }

        public Snapshot Current { get { return current; } }
        public string State { get { return state; } }
        public string Error { get { return error; } }
        // Bumped on every change, so a menu can rebuild when the download finishes.
        public int Revision { get; private set; }

        // The downloaded (or cached) server address, else the mod's built-in one.
        public string ServerAddress(string builtIn)
        { return current.Address.Length > 0 ? current.Address : builtIn; }

        // The address to connect to: the player's own (typed in settings) wins, then the downloaded or cached one,
        // then the built-in one.
        public string Pick(string typed, string builtIn)
        { return !string.IsNullOrEmpty(typed) && typed.Trim().Length > 0 ? typed.Trim() : ServerAddress(builtIn); }

        // One line for the online menu: the update message first, then the mod's notice, then the server's.
        public string StatusLine()
        {
            Snapshot s = current;
            if (s.TooOld) return "Online play needs version " + s.MinVersion + " or newer (you have " + ModVersion + ")." + (s.DownloadUrl.Length > 0 ? " Get it at " + s.DownloadUrl : "");
            if (s.UpdateAvailable) return "Update available: " + s.LatestVersion + " (you have " + ModVersion + ")." + (s.DownloadUrl.Length > 0 ? " " + s.DownloadUrl : "");
            if (s.Notice.Length > 0) return s.Notice;
            if (s.ServerStatus == "maintenance") return s.ServerNotice.Length > 0 ? s.ServerNotice : "The server is down for maintenance.";
            return s.ServerNotice;
        }

        // Starts a background download unless one is running or one started less than minSeconds ago.
        public void Start() { Start(5000, 0); }

        public void Start(int timeoutMs, int minSeconds)
        {
            if ((DateTime.UtcNow - lastStart).TotalSeconds < minSeconds) return;
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
            lastStart = DateTime.UtcNow;
            state = "loading";
            var thread = new Thread(delegate ()
            {
                try { Refresh(timeoutMs); }
                catch (Exception ex) { state = "failed"; error = ex.Message; }
                finally { Interlocked.Exchange(ref running, 0); }
            });
            thread.IsBackground = true; thread.Name = "ModInfo " + GameId;
            thread.Start();
        }

        // Synchronous: download both files, check them, keep and cache them. False: the old snapshot stays.
        public bool Refresh(int timeoutMs)
        {
            string servers = null, mod = null;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    servers = Download(BaseUrl + "servers.json", timeoutMs);
                    mod = Download(BaseUrl + "mods/" + GameId + ".json", timeoutMs);
                    break;
                }
                catch (WebException ex)
                {
                    // one retry for network trouble (not for a 404, which won't change in a second)
                    var http = ex.Response as HttpWebResponse;
                    if (attempt >= 2 || (http != null && http.StatusCode == HttpStatusCode.NotFound)) { state = "failed"; error = "can't download: " + ex.Message; return false; }
                    Thread.Sleep(1000);
                }
                catch (Exception ex) { state = "failed"; error = "can't download: " + ex.Message; return false; }
            }
            string why;
            Snapshot s = Build(servers, mod, "download", out why);
            if (s == null) { state = "failed"; error = why; return false; }
            current = s; Revision++;
            state = "ok"; error = "";
            try
            {
                if (!string.IsNullOrEmpty(CachePath))
                    File.WriteAllText(CachePath, "{\"servers\":" + servers.Trim() + ",\"mod\":" + mod.Trim() + "}", new UTF8Encoding(false));
            }
            catch (Exception) { }
            return true;
        }

        void LoadCache()
        {
            try
            {
                if (string.IsNullOrEmpty(CachePath) || !File.Exists(CachePath)) return;
                var both = Json.Parse(File.ReadAllText(CachePath)) as Dictionary<string, object>;
                if (both == null || !both.ContainsKey("servers") || !both.ContainsKey("mod")) return;
                string why;
                Snapshot s = Check(both["servers"], both["mod"], "cache", out why);
                if (s != null) { current = s; Revision++; }
            }
            catch (Exception) { }
        }

        static string Download(string url, int timeoutMs)
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch (Exception) { }  // TLS 1.2 on old Mono
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = timeoutMs; request.ReadWriteTimeout = timeoutMs;
            request.UserAgent = "ModRelay-ModInfo/1";
            request.AllowAutoRedirect = false;
            request.KeepAlive = false;  // Unity's Mono can reuse a connection the server already closed, then hang
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            {
                if (response.StatusCode != HttpStatusCode.OK) throw new IOException("HTTP " + (int)response.StatusCode);
                var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int n;
                while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, n);
                    if (buffer.Length > MaxBytes) throw new IOException("file too large");
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        // ---- checks (the same rules as tools/validate.py) ----

        static readonly Regex IdRule = new Regex("^[a-z0-9][a-z0-9-]{0,31}$");
        static readonly Regex VersionRule = new Regex("^[0-9]+(\\.[0-9]+){0,3}$");
        static readonly Regex AddressRule = new Regex("^[A-Za-z0-9.-]{1,253}:[0-9]{1,5}$");

        public Snapshot Build(string serversText, string modText, string source, out string why)
        {
            object servers, mod;
            try { servers = Json.Parse(serversText); mod = Json.Parse(modText); }
            catch (Exception ex) { why = "not valid JSON: " + ex.Message; return null; }
            return Check(servers, mod, source, out why);
        }

        Snapshot Check(object serversDoc, object modDoc, string source, out string why)
        {
            var sv = serversDoc as Dictionary<string, object>;
            var md = modDoc as Dictionary<string, object>;
            why = "";
            if (sv == null || Number(sv, "schema") != 1) { why = "servers.json: schema must be 1"; return null; }
            if (md == null || Number(md, "schema") != 1) { why = "mod file: schema must be 1"; return null; }
            var list = Get(sv, "servers") as List<object>;
            if (list == null || list.Count == 0) { why = "servers.json: no servers"; return null; }
            var byId = new Dictionary<string, Dictionary<string, object>>();
            foreach (object o in list)
            {
                var server = o as Dictionary<string, object>;
                if (server == null) { why = "servers.json: a server isn't an object"; return null; }
                string id = Text(server, "id"), address = Text(server, "address"), status = Text(server, "status");
                if (!IdRule.IsMatch(id) || byId.ContainsKey(id)) { why = "servers.json: bad or duplicate id"; return null; }
                if (!ValidAddress(address)) { why = "servers.json: bad address for " + id; return null; }
                if (status != "online" && status != "offline" && status != "maintenance") { why = "servers.json: bad status for " + id; return null; }
                if (status == "online" && address.Length == 0) { why = "servers.json: online server " + id + " has no address"; return null; }
                if (!OneLine(Text(server, "name")) || !OneLine(Text(server, "notice"))) { why = "servers.json: bad text for " + id; return null; }
                byId[id] = server;
            }
            foreach (string key in new[] { "id", "name", "game", "engine", "status", "latest_version", "min_version", "protocol", "download_url", "changelog_url", "relay", "notice", "updated" })
                if (!md.ContainsKey(key)) { why = "mod file: missing " + key; return null; }
            if (Text(md, "id") != GameId) { why = "mod file is for '" + Text(md, "id") + "', not '" + GameId + "'"; return null; }
            string latest = Text(md, "latest_version"), minimum = Text(md, "min_version");
            if (!VersionRule.IsMatch(latest) || !VersionRule.IsMatch(minimum)) { why = "mod file: bad version"; return null; }
            if (Compare(minimum, latest) > 0) { why = "mod file: min_version is above latest_version"; return null; }
            string download = Text(md, "download_url"), changelog = Text(md, "changelog_url");
            if (!ValidUrl(download) || !ValidUrl(changelog)) { why = "mod file: links must be https://"; return null; }
            foreach (string key in new[] { "name", "game", "engine", "protocol", "notice" })
                if (!OneLine(Text(md, key))) { why = "mod file: bad " + key; return null; }
            var relay = Get(md, "relay") as Dictionary<string, object>;
            if (relay == null) { why = "mod file: relay must be an object"; return null; }
            string mode = Text(relay, "mode"), serverId = Text(relay, "server");
            double players = Number(relay, "max_players");
            if (mode != "star" && mode != "tunnel") { why = "mod file: bad relay.mode"; return null; }
            if (players < 2 || players > 16 || players != Math.Floor(players)) { why = "mod file: bad relay.max_players"; return null; }
            if (!byId.ContainsKey(serverId)) { why = "mod file: relay.server isn't in servers.json"; return null; }
            string modAddress = Text(relay, "address");
            if (!ValidAddress(modAddress)) { why = "mod file: bad relay.address"; return null; }
            var chosen = byId[serverId];
            var s = new Snapshot();
            s.Source = source;
            s.Address = modAddress.Length > 0 ? modAddress : Text(chosen, "address");
            s.ServerStatus = Text(chosen, "status");
            s.ServerNotice = Text(chosen, "notice");
            s.LatestVersion = latest; s.MinVersion = minimum;
            s.DownloadUrl = download; s.Notice = Text(md, "notice"); s.Status = Text(md, "status");
            if (VersionRule.IsMatch(ModVersion ?? ""))
            {
                s.UpdateAvailable = Compare(ModVersion, latest) < 0;
                s.TooOld = Compare(ModVersion, minimum) < 0;
            }
            return s;
        }

        // Numeric dotted compare: "0.10" > "0.9", "1" == "1.0".
        public static int Compare(string a, string b)
        {
            string[] x = a.Split('.'), y = b.Split('.');
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                long p = i < x.Length ? long.Parse(x[i], CultureInfo.InvariantCulture) : 0;
                long q = i < y.Length ? long.Parse(y[i], CultureInfo.InvariantCulture) : 0;
                if (p != q) return p < q ? -1 : 1;
            }
            return 0;
        }

        static bool ValidAddress(string a)
        {
            if (a.Length == 0) return true;
            if (!AddressRule.IsMatch(a)) return false;
            int port = int.Parse(a.Substring(a.LastIndexOf(':') + 1), CultureInfo.InvariantCulture);
            return port >= 1 && port <= 65535;
        }

        static bool ValidUrl(string u) { return u.Length == 0 || (u.StartsWith("https://") && u.Length <= 500 && OneLine(u)); }
        static bool OneLine(string t) { return t.Length <= 200 && t.IndexOf('\n') < 0 && t.IndexOf('\r') < 0; }
        static object Get(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) ? v : null; }
        static string Text(Dictionary<string, object> d, string key) { return Get(d, key) as string ?? ""; }
        static double Number(Dictionary<string, object> d, string key) { object v = Get(d, key); return v is double ? (double)v : double.NaN; }

        // Minimal JSON reader: objects -> Dictionary<string, object>, arrays -> List<object>, numbers -> double.
        public static class Json
        {
            public static object Parse(string text)
            {
                int i = 0;
                object value = Value(text, ref i);
                Space(text, ref i);
                if (i != text.Length) throw new FormatException("extra text at " + i);
                return value;
            }

            static void Space(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

            static object Value(string s, ref int i)
            {
                Space(s, ref i);
                if (i >= s.Length) throw new FormatException("unexpected end");
                char c = s[i];
                if (c == '{')
                {
                    var d = new Dictionary<string, object>();
                    i++; Space(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    while (true)
                    {
                        Space(s, ref i);
                        if (i >= s.Length || s[i] != '"') throw new FormatException("expected a key at " + i);
                        string key = Str(s, ref i);
                        Space(s, ref i);
                        if (i >= s.Length || s[i] != ':') throw new FormatException("expected : at " + i);
                        i++;
                        d[key] = Value(s, ref i);
                        Space(s, ref i);
                        if (i < s.Length && s[i] == ',') { i++; continue; }
                        if (i < s.Length && s[i] == '}') { i++; return d; }
                        throw new FormatException("expected , or } at " + i);
                    }
                }
                if (c == '[')
                {
                    var l = new List<object>();
                    i++; Space(s, ref i);
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    while (true)
                    {
                        l.Add(Value(s, ref i));
                        Space(s, ref i);
                        if (i < s.Length && s[i] == ',') { i++; continue; }
                        if (i < s.Length && s[i] == ']') { i++; return l; }
                        throw new FormatException("expected , or ] at " + i);
                    }
                }
                if (c == '"') return Str(s, ref i);
                if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
                if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
                if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
                int start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                double number;
                if (i == start || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    throw new FormatException("bad value at " + start);
                return number;
            }

            static string Str(string s, ref int i)
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) break;
                    char e = s[i++];
                    if (e == 'n') sb.Append('\n');
                    else if (e == 'r') sb.Append('\r');
                    else if (e == 't') sb.Append('\t');
                    else if (e == 'b') sb.Append('\b');
                    else if (e == 'f') sb.Append('\f');
                    else if (e == 'u' && i + 4 <= s.Length) { sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; }
                    else sb.Append(e);
                }
                throw new FormatException("unterminated string");
            }
        }
    }
}
