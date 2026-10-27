using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using BerryCoop.Net;
using ModRelay;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BerryCoop
{
    public enum Role { None, Host, Guest }

    public sealed class Peer
    {
        public int Id;
        public string Name;
        public Connection Conn;
        public bool Welcomed;
        public int SceneEpoch = -1;
        public int WorldEpochSent = -1;
        public bool Ready;
        public bool InShop;
        public bool WantsNextDay;
        public Vector3 LastPos;
        public float LastHeard;
    }

    public static class Session
    {
        public const string GameScene = "Game";

        public static Role Role;
        public static bool IsHost { get { return Role == Role.Host; } }
        public static bool IsGuest { get { return Role == Role.Guest; } }
        public static int LocalId;
        public static string Status = "";
        public static string RoomCode = "";
        public static bool RoomListed;
        public static readonly Dictionary<int, Peer> Peers = new Dictionary<int, Peer>();

        public static int Epoch;
        public static bool HostWorldReady;
        static bool guestReadySent;
        static bool guestSceneLoaded;
        public static string PendingWorldJson;
        static int pendingEpoch = -1;
        static int nextPeerId = 1;

        // Context counters read by the patches.
        public static int Replaying;
        public static int IntentContext;
        public static int GrantContext;
        public static int BankContext;
        public static int HostExecuting;
        public static bool PlacingBuild;
        public static bool SellingBuild;

        static Connection hostLink;
        static Listener listener;
        static RelayHost room;
        static readonly Queue<TcpClient> pendingSockets = new Queue<TcpClient>();
        static volatile string relayLost;
        static volatile string connectError;
        static volatile TcpClient connected;
        static volatile string newRoomCode;
        static float lastTick, lastStateTick, lastPoseTick, lastPing, lastFullSync;

        // Host: the scene is the game and the save is loaded; guests: our copy is loaded for the host's epoch.
        public static bool WorldLive { get { return IsHost ? HostWorldReady : GuestLive; } }
        public static bool GuestLive { get { return IsGuest && guestReadySent && Game.InGameScene; } }
        public static bool GuestInWorld { get { return IsGuest && guestSceneLoaded && Game.InGameScene; } }
        // Guests see a "syncing" screen until the host's full state has landed, instead of their own copy of the farm.
        public static bool GuestSynced;
        static float guestSceneSince;
        static bool guestSeenPlaying;
        public static bool ShowSyncCover
        {
            get
            {
                if (!GuestInWorld || Time.unscaledTime - guestSceneSince > 30f) return false;
                if (!guestSeenPlaying && GuestSynced && GameManager.Singleton.gameState != GameManager.GameState.Intro
                    && GameManager.Singleton.gameState != GameManager.GameState.PreRoundSetup)
                {
                    guestSeenPlaying = true;
                    NetLog.Info("sync cover lifted (" + (Time.unscaledTime - guestSceneSince).ToString("F1") + " s after load)");
                }
                return !guestSeenPlaying;
            }
        }
        public static int PlayerCount { get { return Peers.Count + (Role == Role.None ? 0 : (IsHost ? 1 : 0)); } }

        public static string ProtocolTag { get { return Plugin.GameId + "-" + Plugin.WireVersion; } }

        public static void StartHost(bool online)
        {
            if (Role != Role.None) return;
            Role = Role.Host;
            LocalId = 0;
            Epoch = 1;
            HostWorldReady = false;
            nextPeerId = 1;
            RoomCode = "";
            try
            {
                listener = new Listener(Plugin.CfgPort.Value, pendingSockets);
                Status = "Hosting on LAN port " + Plugin.CfgPort.Value;
            }
            catch (Exception e)
            {
                listener = null;
                Status = "LAN port " + Plugin.CfgPort.Value + " is busy (" + e.Message + ")";
            }
            if (online) OpenRoom();
            if (Game.InGameScene) BeginHostWorld();
            NetLog.Info("hosting (online=" + online + ")");
        }

        static void OpenRoom()
        {
            if (Plugin.CfgRelayTyped.Value.Length == 0 && Plugin.RelayInfo.Current.TooOld)
            {
                Status = "Online needs an update: " + Plugin.RelayInfo.StatusLine();
                return;
            }
            string address = Plugin.RelayAddress;
            string host; int port;
            if (!RelayLink.ParseAddress(address, 25569, out host, out port)) { Status = "Bad relay address " + address; return; }
            int max = Mathf.Clamp(Plugin.CfgMaxPlayers.Value, 2, 4);
            bool listed = Plugin.CfgListed.Value;
            string roomName = Plugin.PlayerName + "'s farm";
            Status = "Opening an online room...";
            new Thread(() =>
            {
                try
                {
                    var r = new RelayHost(host, port, Plugin.GameId, Plugin.RelayVersion, listed, max, null, null, roomName, 8000);
                    r.Start(c => { lock (pendingSockets) pendingSockets.Enqueue(c); }, reason => relayLost = reason);
                    room = r;
                    newRoomCode = r.Code;
                }
                catch (Exception e) { relayLost = e.Message; }
            }) { IsBackground = true, Name = "coop-room" }.Start();
            RoomListed = listed;
        }

        public static void JoinDirect(string address)
        {
            if (Role != Role.None) return;
            string host; int port;
            if (!RelayLink.ParseAddress(address, Plugin.CfgPort.Value, out host, out port)) { Status = "Bad address " + address; return; }
            Plugin.CfgLastDirect.Value = address;
            BeginGuest("Connecting to " + host + ":" + port + "...");
            new Thread(() =>
            {
                try { connected = RelayLink.Connect(host, port, 8000); }
                catch (Exception e) { connectError = e.Message; }
            }) { IsBackground = true, Name = "coop-join" }.Start();
        }

        public static void JoinCode(string code)
        {
            if (Role != Role.None) return;
            if (Plugin.CfgRelayTyped.Value.Length == 0 && Plugin.RelayInfo.Current.TooOld)
            {
                Status = "Online needs an update: " + Plugin.RelayInfo.StatusLine();
                return;
            }
            string host; int port;
            if (!RelayLink.ParseAddress(Plugin.RelayAddress, 25569, out host, out port)) { Status = "Bad relay address"; return; }
            string name = Plugin.PlayerName;
            BeginGuest("Joining room " + code.ToUpperInvariant() + "...");
            new Thread(() =>
            {
                try { connected = RelayLink.JoinRoom(host, port, Plugin.GameId, Plugin.RelayVersion, code.Trim(), null, name, 8000); }
                catch (Exception e) { connectError = e.Message; }
            }) { IsBackground = true, Name = "coop-join" }.Start();
        }

        static void BeginGuest(string status)
        {
            Role = Role.Guest;
            LocalId = -1;
            Status = status;
            Epoch = 0;
            pendingEpoch = -1;
            guestReadySent = false;
            PendingWorldJson = null;
        }

        public static void Shutdown(string reason)
        {
            if (Role == Role.None) return;
            Role was = Role;
            NetLog.Info("session closed: " + reason);
            foreach (Peer p in Peers.Values) if (p.Conn != null) p.Conn.Close(reason);
            Peers.Clear();
            if (hostLink != null) hostLink.Close(reason);
            hostLink = null;
            if (listener != null) listener.Stop();
            listener = null;
            if (room != null) { try { room.Dispose(); } catch { } }
            room = null;
            lock (pendingSockets) { while (pendingSockets.Count > 0) { try { pendingSockets.Dequeue().Close(); } catch { } } }
            Role = Role.None;
            RoomCode = "";
            HostWorldReady = false;
            guestReadySent = false;
            PendingWorldJson = null;
            Avatars.Clear();
            foreach (var ne in Entities.All.ToArray())
                if (ne && ne.Rb && was == Role.Guest && Game.InGameScene) ne.Rb.isKinematic = false;
            Status = reason;
            CoopMenu.Toast(reason, 5f);
            if (was == Role.Guest && Game.InGameScene)
            {
                // The world on screen was the host's; leave it rather than let it be saved as ours.
                SceneManager.LoadScene("MainMenu");
            }
        }

        public static void OnSceneLoaded(Scene scene)
        {
            Game.ResetCaches();
            bool game = scene.name == GameScene;
            Entities.Reset();
            NetObjects.Reset();
            Avatars.OnSceneLoaded();
            if (game)
            {
                Entities.RegisterStatics(scene);
                NetObjects.Register(scene);
                StateSync.OnSceneLoaded();
            }
            if (IsHost)
            {
                Epoch++;
                HostWorldReady = false;
                foreach (Peer p in Peers.Values) p.Ready = false;
                if (!game)
                {
                    // Endings, credits and the menu are not shared; guests wait in their menu.
                    var p = new Packet(Msg.ToMenu).Str(scene.name);
                    foreach (Peer peer in Peers.Values) if (peer.Welcomed) peer.Conn.Send(p);
                }
            }
            else if (IsGuest)
            {
                guestReadySent = false;
                GuestSynced = false;
                guestSeenPlaying = false;
                guestSceneSince = Time.unscaledTime;
                guestSceneLoaded = game && pendingEpoch == Epoch && PendingWorldJson != null;
            }
        }

        static void BeginHostWorld()
        {
            HostWorldReady = true;
            Entities.HostAdopt();
            NetLog.Info("host world ready, epoch " + Epoch);
            foreach (Peer p in Peers.Values)
            {
                if (!p.Welcomed) continue;
                if (p.WorldEpochSent != Epoch) SendWorld(p, Epoch);
                if (p.SceneEpoch == Epoch) MakeReady(p);
            }
        }

        static string WorldJson() { return JsonUtility.ToJson(SaveLoadManager.Singleton.CreateNewPlayerData()); }

        static string NgModsJson()
        {
            var mods = MenuToGameBridger.Singleton ? MenuToGameBridger.Singleton.activeSaveGame_NGPlusModifiers : null;
            if (!mods) return "";
            var parts = new List<string>();
            foreach (FieldInfo f in typeof(NGPlusModifiers).GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (f.FieldType == typeof(bool)) parts.Add(f.Name + "=" + ((bool)f.GetValue(mods) ? "1" : "0"));
            return string.Join(";", parts.ToArray());
        }

        static void ApplyNgMods(string text)
        {
            var mods = MenuToGameBridger.Singleton ? MenuToGameBridger.Singleton.activeSaveGame_NGPlusModifiers : null;
            if (!mods || string.IsNullOrEmpty(text)) return;
            foreach (string part in text.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                FieldInfo f = typeof(NGPlusModifiers).GetField(part.Substring(0, eq));
                if (f != null && f.FieldType == typeof(bool)) f.SetValue(mods, part.Substring(eq + 1) == "1");
            }
        }

        static void SendWorld(Peer p, int epoch) { SendWorld(p, epoch, WorldJson()); }

        static void SendWorld(Peer p, int epoch, string json)
        {
            p.WorldEpochSent = epoch;
            p.Ready = false;
            p.Conn.Send(new Packet(Msg.World).Int(epoch).Str(json).Str(NgModsJson()));
            NetLog.Info("sent world epoch " + epoch + " to " + p.Name + " (" + json.Length + " chars)");
        }

        // Next day: the host just saved; guests load the same save while the host reloads.
        static int savedForEpoch = -1;

        public static void HostSavedForNextRound(string json)
        {
            if (!IsHost || savedForEpoch == Epoch) return;
            savedForEpoch = Epoch;
            int next = Epoch + 1;
            foreach (Peer p in Peers.Values)
            {
                p.WantsNextDay = false;
                p.InShop = false;
                if (p.Welcomed) SendWorld(p, next, json);
            }
        }

        public static void HostRoundEnded(bool fromBed)
        {
            if (!IsHost || !HostWorldReady) return;
            BroadcastReady(new Packet(Msg.RoundEnd).Int(Epoch).Bool(fromBed));
        }

        static void MakeReady(Peer p)
        {
            p.Ready = true;
            Entities.SendFull(p);
            p.Conn.Send(StateSync.BuildState(true));
            p.Conn.Send(StateSync.BuildAux(true));
            NetLog.Info(p.Name + " is in the world (epoch " + Epoch + ")");
        }

        public static void Update()
        {
            if (Role == Role.None) return;
            float now = Time.unscaledTime;
            if (IsHost) HostUpdate(now); else GuestUpdate(now);
            if (Role != Role.None) ShopScreens.Tick();
        }

        static void HostUpdate(float now)
        {
            if (newRoomCode != null) { RoomCode = newRoomCode; newRoomCode = null; Status = "Room " + RoomCode + " is open"; NetLog.Info("room code " + RoomCode); }
            if (relayLost != null)
            {
                string why = relayLost; relayLost = null;
                Status = "Online room closed: " + why;
                CoopMenu.Toast(Status, 5f);
                RoomCode = "";
                if (room != null) { try { room.Dispose(); } catch { } room = null; }
            }
            while (true)
            {
                TcpClient c;
                lock (pendingSockets) { if (pendingSockets.Count == 0) break; c = pendingSockets.Dequeue(); }
                var peer = new Peer { Id = nextPeerId++, Name = "?", Conn = new Connection(c, "peer"), LastHeard = now };
                Peers[peer.Id] = peer;
            }
            if (!HostWorldReady && Game.InGameScene && GameManager.Singleton.gameState != GameManager.GameState.PreRoundSetup)
                BeginHostWorld();

            foreach (Peer p in new List<Peer>(Peers.Values))
            {
                byte[] data;
                int budget = 400;
                while (budget-- > 0 && p.Conn.TryReceive(out data))
                {
                    p.LastHeard = now;
                    try { HostDispatch(p, new Reader(data)); }
                    catch (Exception e) { NetLog.Error("from " + p.Name + ": " + e); }
                }
                if (p.Conn.Closed || (!p.Welcomed && now - p.LastHeard > 15f) || now - p.LastHeard > 30f)
                    DropPeer(p, p.Conn.Closed ? p.Conn.CloseReason : "timed out");
            }

            if (now - lastPing > 2f)
            {
                lastPing = now;
                var ping = new Packet(Msg.Ping);
                foreach (Peer p in Peers.Values) if (p.Welcomed) p.Conn.Send(ping);
            }
            if (!HostWorldReady) return;
            if (now - lastTick >= 1f / 15f) { lastTick = now; Entities.HostTick(); }
            if (now - lastStateTick >= 0.1f)
            {
                lastStateTick = now;
                // Every 5 s the host resends everything, so anything that drifted on a guest is corrected.
                bool full = now - lastFullSync >= 5f;
                if (full) lastFullSync = now;
                BroadcastReady(StateSync.BuildState(full));
                if (full) BroadcastReady(StateSync.BuildAux(true));
                else if (StateSync.AuxDue()) BroadcastReady(StateSync.BuildAux(false));
            }
            if (now - lastPoseTick >= 0.05f) { lastPoseTick = now; Avatars.SendLocalPose(); }
        }

        static void HostDispatch(Peer p, Reader r)
        {
            if (!p.Welcomed)
            {
                if (r.Type != Msg.Hello) { DropPeer(p, "bad handshake"); return; }
                string proto = r.Str(), version = r.Str(), name = r.Str();
                if (proto != ProtocolTag) { Reject(p, "Different co-op mod build (host " + Plugin.ModVersion + " / " + ProtocolTag + ", you " + version + " / " + proto + "). Use the same mod build."); return; }
                if (Peers.Count > Mathf.Clamp(Plugin.CfgMaxPlayers.Value, 2, 4) - 1) { Reject(p, "The farm is full"); return; }
                p.Name = RelayLink.Clean(name, 24);
                p.Welcomed = true;
                var w = new Packet(Msg.Welcome).Int(p.Id).Str(Plugin.PlayerName).Int(Peers.Count - 1);
                foreach (Peer o in Peers.Values) if (o != p && o.Welcomed) w.Int(o.Id).Str(o.Name);
                p.Conn.Send(w);
                var join = new Packet(Msg.PlayerJoin).Int(p.Id).Str(p.Name);
                foreach (Peer o in Peers.Values) if (o != p && o.Welcomed) o.Conn.Send(join);
                CoopMenu.Toast(p.Name + " joined the farm", 4f);
                NetLog.Info("welcomed " + p.Name + " as #" + p.Id);
                if (HostWorldReady) SendWorld(p, Epoch);
                return;
            }
            switch (r.Type)
            {
                case Msg.SceneReady:
                    p.SceneEpoch = r.Int();
                    if (HostWorldReady && p.SceneEpoch == Epoch && p.WorldEpochSent == Epoch) MakeReady(p);
                    break;
                case Msg.Pong: break;
                case Msg.Claim: case Msg.OwnState: case Msg.Release: case Msg.Take:
                    Entities.HostHandle(p, r); break;
                case Msg.Cmd: Commands.HostHandleCmd(p, r); break;
                case Msg.Build: Commands.HostHandleBuild(p, r); break;
                case Msg.Sell: Commands.HostHandleSell(p, r); break;
                case Msg.PopGun: Commands.HostHandlePopGun(p, r); break;
                case Msg.Player: Avatars.HostReceivePose(p, r); break;
                case Msg.Ready:
                    p.WantsNextDay = r.Bool();
                    if (p.WantsNextDay) CoopMenu.Toast(p.Name + " is ready for the next day", 4f);
                    break;
                case Msg.Slept:
                    if (r.Int() == Epoch && p.Ready && Game.InGameScene && GameManager.Singleton.gameState == GameManager.GameState.Playing)
                    {
                        CoopMenu.Toast(p.Name + " went to bed", 4f);
                        VcrIntroManager.Singleton.PlayTapeWindDownSFX();
                        GameManager.Singleton.ForceEndRound(true);
                    }
                    break;
                case Msg.Fell:
                    if (r.Int() == Epoch && p.Ready && Game.InGameScene && GameManager.Singleton.gameState == GameManager.GameState.Playing)
                    {
                        CoopMenu.Toast(p.Name + " fell into the hole", 4f);
                        GameManager.Singleton.ForceEndRound(false);
                    }
                    break;
                case Msg.Bank:
                    {
                        if (r.Int() != Epoch || !p.Ready) break;
                        int amount = r.Int();
                        if (amount > 0 && Game.InGameScene)
                        {
                            PlayerStats.Singleton.IncreaseMoney_Banked(amount);
                            NetLog.Info(p.Name + " banked $" + amount);
                        }
                        break;
                    }
            }
        }

        static void Reject(Peer p, string reason)
        {
            p.Conn.Send(new Packet(Msg.Reject).Str(reason));
            NetLog.Info("rejected a player: " + reason);
            Plugin.Instance.StartCoroutine(CloseSoon(p.Conn, reason));
            Peers.Remove(p.Id);
        }

        static System.Collections.IEnumerator CloseSoon(Connection c, string reason)
        {
            yield return new WaitForSecondsRealtime(1f);
            c.Close(reason);
        }

        static void DropPeer(Peer p, string reason)
        {
            Peers.Remove(p.Id);
            p.Conn.Close(reason);
            if (!p.Welcomed) return;
            if (Game.InGameScene) Entities.HostReleaseAll(p);
            Avatars.Remove(p.Id);
            var leave = new Packet(Msg.PlayerLeave).Int(p.Id);
            foreach (Peer o in Peers.Values) if (o.Welcomed) o.Conn.Send(leave);
            CoopMenu.Toast(p.Name + " left (" + reason + ")", 4f);
            NetLog.Info(p.Name + " left: " + reason);
        }

        static void GuestUpdate(float now)
        {
            if (connectError != null)
            {
                string e = connectError; connectError = null;
                Shutdown("Could not join: " + e);
                return;
            }
            if (connected != null)
            {
                TcpClient c = connected; connected = null;
                hostLink = new Connection(c, "host");
                hostLink.Send(new Packet(Msg.Hello).Str(ProtocolTag).Str(Plugin.ModVersion).Str(Plugin.PlayerName));
                Status = "Connected, waiting for the host...";
            }
            if (hostLink == null) return;
            byte[] data;
            int budget = 2000;
            while (budget-- > 0 && hostLink != null && hostLink.TryReceive(out data))
            {
                try { GuestDispatch(new Reader(data)); }
                catch (Exception e) { NetLog.Error("from host: " + e); }
            }
            if (hostLink == null) return;
            if (hostLink.Closed) { Shutdown("Disconnected: " + hostLink.CloseReason); return; }

            if (guestSceneLoaded && !guestReadySent && Game.InGameScene && GameManager.Singleton.gameState != GameManager.GameState.PreRoundSetup)
            {
                guestReadySent = true;
                hostLink.Send(new Packet(Msg.SceneReady).Int(Epoch));
                Status = "Playing on " + HostName + "'s farm";
                NetLog.Info("guest world loaded, epoch " + Epoch);
            }
            if (GuestLive && now - lastPoseTick >= 0.05f) { lastPoseTick = now; Avatars.SendLocalPose(); }
        }

        public static string HostName = "";

        static void GuestDispatch(Reader r)
        {
            switch (r.Type)
            {
                case Msg.Welcome:
                    {
                        LocalId = r.Int();
                        HostName = r.Str();
                        Peers.Clear();
                        Peers[0] = new Peer { Id = 0, Name = HostName, Welcomed = true };
                        int n = r.Int();
                        for (int i = 0; i < n; i++) { int id = r.Int(); Peers[id] = new Peer { Id = id, Name = r.Str(), Welcomed = true }; }
                        Status = "Joined " + HostName + ". Waiting for the host's game...";
                        CoopMenu.Toast("Joined " + HostName + "'s farm", 4f);
                        NetLog.Info("welcome as #" + LocalId);
                        break;
                    }
                case Msg.Reject: Shutdown("Refused: " + r.Str()); break;
                case Msg.PlayerJoin: { int id = r.Int(); string name = r.Str(); Peers[id] = new Peer { Id = id, Name = name, Welcomed = true }; CoopMenu.Toast(name + " joined", 3f); break; }
                case Msg.PlayerLeave: { int id = r.Int(); Peer p; if (Peers.TryGetValue(id, out p)) CoopMenu.Toast(p.Name + " left", 3f); Peers.Remove(id); Avatars.Remove(id); break; }
                case Msg.Ping: hostLink.Send(new Packet(Msg.Pong)); break;
                case Msg.World:
                    {
                        int epoch = r.Int(); string json = r.Str(); string mods = r.Str();
                        GuestLoadWorld(epoch, json, mods);
                        break;
                    }
                case Msg.RoundEnd:
                    {
                        int epoch = r.Int(); bool fromBed = r.Bool();
                        if (epoch == Epoch && GuestLive && GameManager.Singleton.gameState == GameManager.GameState.Playing)
                            GameManager.Singleton.ForceEndRound(fromBed);
                        break;
                    }
                case Msg.ToMenu:
                    {
                        string scene = r.Str();
                        pendingEpoch = -1;
                        PendingWorldJson = null;
                        guestReadySent = false;
                        Status = "The host is in " + scene + ". You will rejoin when they start playing.";
                        if (Game.InGameScene) SceneManager.LoadScene("MainMenu");
                        break;
                    }
                case Msg.StaticMap: case Msg.Spawn: case Msg.Despawn: case Msg.Snap: case Msg.Owner: case Msg.Deny:
                    Entities.GuestHandle(r); break;
                case Msg.Grant: Entities.GuestGrant(r); break;
                case Msg.State: StateSync.GuestApplyState(r); break;
                case Msg.Growers: StateSync.GuestApplyAux(r); break;
                case Msg.Rpc: Commands.GuestHandleRpc(r); break;
                case Msg.Build: Commands.GuestHandleBuild(r); break;
                case Msg.Sell: Commands.GuestHandleSell(r); break;
                case Msg.CmdDenied: CoopMenu.Toast(r.Str(), 3f); break;
                case Msg.Player: Avatars.GuestReceivePose(r); break;
                case Msg.Toast: CoopMenu.Toast(r.Str(), 4f); break;
                case Msg.Fx: Effects.GuestPlay(r); break;
            }
        }

        static void GuestLoadWorld(int epoch, string json, string mods)
        {
            if (epoch == pendingEpoch && PendingWorldJson != null) return;
            Epoch = epoch;
            pendingEpoch = epoch;
            PendingWorldJson = json;
            guestReadySent = false;
            guestSceneLoaded = false;
            if (!MenuToGameBridger.Singleton) { NetLog.Error("no MenuToGameBridger; cannot load the host's world"); return; }
            ApplyNgMods(mods);
            // Guests skip the game's own wake-up step (the host starts the day), which is what stops the shop music.
            try { if (AudioManager.Singleton) AudioManager.Singleton.StopShopMusic(); } catch { }
            MenuToGameBridger.Singleton.loadDataOnNextGameSceneLoad = true;
            MenuToGameBridger.Singleton.comingBackToMainMenuFromTrueEnding = false;
            Status = "Loading " + HostName + "'s farm...";
            NetLog.Info("loading host world epoch " + epoch);
            SceneManager.LoadScene(GameScene);
        }

        public static void LateUpdate()
        {
            if (!GuestLive) { if (IsHost && HostWorldReady) Avatars.LateUpdate(); return; }
            Entities.GuestLateUpdate();
            StateSync.GuestLateUpdate();
            Avatars.LateUpdate();
        }

        public static void SendToHost(Packet p) { if (hostLink != null) hostLink.Send(p); }

        public static void BroadcastReady(Packet p)
        {
            byte[] data = p.ToArray();
            foreach (Peer peer in Peers.Values) if (peer.Ready && peer.Conn != null) peer.Conn.Send(data);
        }

        public static void BroadcastReadyExcept(Packet p, int except)
        {
            byte[] data = p.ToArray();
            foreach (Peer peer in Peers.Values) if (peer.Ready && peer.Conn != null && peer.Id != except) peer.Conn.Send(data);
        }

        public static void GuestBank(int amount)
        {
            if (amount > 0) SendToHost(new Packet(Msg.Bank).Int(Epoch).Int(amount));
        }

        public static string NameOf(int id)
        {
            if (id == LocalId) return Plugin.PlayerName;
            Peer p;
            return Peers.TryGetValue(id, out p) ? p.Name : "#" + id;
        }

        public static void RefreshPublicRooms(Action<string[][], string> done)
        {
            string host; int port;
            if (!RelayLink.ParseAddress(Plugin.RelayAddress, 25569, out host, out port)) { done(null, "Bad relay address"); return; }
            new Thread(() =>
            {
                try { done(RelayLink.ListRooms(host, port, Plugin.GameId, 5000), null); }
                catch (Exception e) { done(null, e.Message); }
            }) { IsBackground = true, Name = "coop-list" }.Start();
        }

        public static long BytesIn
        {
            get
            {
                long n = hostLink != null ? hostLink.BytesIn : 0;
                foreach (Peer p in Peers.Values) if (p.Conn != null) n += p.Conn.BytesIn;
                return n;
            }
        }

        public static long BytesOut
        {
            get
            {
                long n = hostLink != null ? hostLink.BytesOut : 0;
                foreach (Peer p in Peers.Values) if (p.Conn != null) n += p.Conn.BytesOut;
                return n;
            }
        }
    }
}
