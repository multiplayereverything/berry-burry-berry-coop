using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BerryCoop
{
    [BepInPlugin(Guid, "Berry Bury Berry Co-op", ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "berrybury.coop";
        public const string ModVersion = "0.1.0";
        // Wire protocol revision; bump whenever a message layout changes.
        public const int WireVersion = 1;
        public const string GameId = "berrybury";
        public const string DefaultRelay = "195.60.166.57:25569";
        public const int DefaultPort = 27777;

        public static string RelayVersion { get { return ModVersion + "/" + WireVersion; } }

        public static Plugin Instance;
        public static ManualLogSource Log;
        public static string Root;

        public static ConfigEntry<string> CfgName;
        public static ConfigEntry<string> CfgRelayTyped;
        public static ConfigEntry<int> CfgPort;
        public static ConfigEntry<int> CfgMaxPlayers;
        public static ConfigEntry<bool> CfgListed;
        public static ConfigEntry<string> CfgLastDirect;
        public static ConfigEntry<KeyCode> CfgMenuKey;

        public static ModRelay.ModInfo RelayInfo;
        public static LabDriver Lab;

        void Awake()
        {
            Instance = this;
            Log = Logger;
            Root = Path.Combine(Paths.ConfigPath, "BerryCoop");
            Directory.CreateDirectory(Root);

            CfgName = Config.Bind("Player", "Name", "", "Name shown to other players. Empty uses your Steam name.");
            CfgRelayTyped = Config.Bind("Online", "Relay", "", "Relay server you typed yourself (host:port). Empty follows mods-info, then the built-in server.");
            CfgPort = Config.Bind("LAN", "Port", DefaultPort, "TCP port the host listens on for LAN / direct IP players.");
            CfgMaxPlayers = Config.Bind("Online", "MaxPlayers", 4, "Players per room, host included (2-4).");
            CfgListed = Config.Bind("Online", "PublicRoom", true, "List hosted rooms in the public room list.");
            CfgLastDirect = Config.Bind("LAN", "LastAddress", "127.0.0.1", "Last direct IP you joined.");
            CfgMenuKey = Config.Bind("Interface", "MenuKey", KeyCode.F8, "Opens the co-op window.");

            string infoUrl = CommandLine.Value("-berrycoop-modinfo");
            bool lab = CommandLine.Has("-berrycoop-lab");
            RelayInfo = new ModRelay.ModInfo(GameId, ModVersion, Path.Combine(Root, "modinfo-cache.json"), infoUrl);
            if (!lab || infoUrl != null) RelayInfo.Start();

            Application.runInBackground = true;
            NetLog.Open(Path.Combine(Root, lab ? "lab-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log" : "coop.log"));

            var harmony = new Harmony(Guid);
            int ok = 0, failed = 0;
            foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try { harmony.CreateClassProcessor(t).Patch(); ok++; }
                catch (Exception e) { failed++; NetLog.Error("patch " + t.Name + " failed: " + e.Message); }
            }
            try { Patches.CommandPatches.Register(new Harmony(Guid + ".commands")); }
            catch (Exception e) { failed++; NetLog.Error("command patches failed: " + e); }
            try { Patches.GuardPatches.Register(new Harmony(Guid + ".guards")); }
            catch (Exception e) { failed++; NetLog.Error("guard patches failed: " + e); }
            NetLog.Info("patch classes: " + ok + " ok, " + failed + " failed");

            SceneManager.sceneLoaded += OnSceneLoaded;
            gameObject.AddComponent<CoopMenu>();
            if (lab) Lab = gameObject.AddComponent<LabDriver>();
            Log.LogInfo("Berry Bury Berry Co-op " + ModVersion + " loaded (wire " + WireVersion + ")");
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try { Session.OnSceneLoaded(scene); }
            catch (Exception e) { NetLog.Error("sceneLoaded: " + e); }
        }

        void Update()
        {
            try { Session.Update(); }
            catch (Exception e) { NetLog.Error("update: " + e); }
        }

        void LateUpdate()
        {
            try { Session.LateUpdate(); }
            catch (Exception e) { NetLog.Error("lateUpdate: " + e); }
        }

        void OnApplicationQuit()
        {
            Session.Shutdown("Game closed");
            NetLog.Close();
        }

        public static string PlayerName
        {
            get
            {
                string n = CfgName.Value;
                if (string.IsNullOrEmpty(n))
                {
                    try { n = Steamworks.SteamClient.Name; } catch { n = null; }
                }
                if (string.IsNullOrEmpty(n)) n = "Player";
                string lab = CommandLine.Value("-berrycoop-name");
                if (lab != null) n = lab;
                return ModRelay.RelayLink.Clean(n, 24);
            }
        }

        // Typed > mods-info > cache > built in. Only the typed value is ever saved.
        public static string RelayAddress
        {
            get
            {
                string lab = CommandLine.Value("-berrycoop-relay");
                if (lab != null) return lab;
                return RelayInfo.Pick(CfgRelayTyped.Value, DefaultRelay);
            }
            set
            {
                string v = (value ?? "").Trim();
                CfgRelayTyped.Value = v.Length == 0 || v == RelayInfo.ServerAddress(DefaultRelay) ? "" : v;
            }
        }
    }

    public static class CommandLine
    {
        static string[] args;
        static string[] Args { get { return args ?? (args = Environment.GetCommandLineArgs()); } }

        public static bool Has(string flag)
        {
            foreach (string a in Args) if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string Value(string flag)
        {
            string[] a = Args;
            for (int i = 0; i < a.Length - 1; i++)
                if (string.Equals(a[i], flag, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
            return null;
        }
    }

    public static class NetLog
    {
        static StreamWriter file;
        static readonly object gate = new object();

        public static void Open(string path)
        {
            try { file = new StreamWriter(path, false) { AutoFlush = true }; }
            catch { file = null; }
        }

        public static void Close()
        {
            lock (gate) { if (file != null) file.Dispose(); file = null; }
        }

        public static void Info(string msg) { Write("INFO", msg); Plugin.Log.LogInfo(msg); }
        public static void Warn(string msg) { Write("WARN", msg); Plugin.Log.LogWarning(msg); }
        public static void Error(string msg) { Write("ERROR", msg); Plugin.Log.LogError(msg); }
        public static void Debug(string msg) { Write("DEBUG", msg); }

        static void Write(string level, string msg)
        {
            lock (gate)
            {
                if (file == null) return;
                try { file.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + msg); } catch { }
            }
        }
    }
}
