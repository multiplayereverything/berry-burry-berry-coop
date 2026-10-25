using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BerryCoop
{
    // The co-op window (F8): host online or on LAN, join by room code, pick a public room, or join an IP.
    public sealed class CoopMenu : MonoBehaviour
    {
        public static bool Open;

        struct ToastLine { public string Text; public float Until; }
        static readonly List<ToastLine> toasts = new List<ToastLine>();

        Rect window = new Rect(40, 40, 470, 0);
        string code = "";
        string ip;
        string relayField;
        string nameField;
        Vector2 roomScroll;
        string[][] rooms;
        string roomsNote = "";
        volatile bool roomsArrived;
        string[][] roomsIncoming;
        string roomsIncomingError;
        bool disabledGameInput;
        GUIStyle big, small, wrap;

        public static void Toast(string text, float seconds)
        {
            if (string.IsNullOrEmpty(text)) return;
            toasts.Add(new ToastLine { Text = text, Until = Time.unscaledTime + seconds });
            if (toasts.Count > 5) toasts.RemoveAt(0);
        }

        void Start()
        {
            ip = Plugin.CfgLastDirect.Value;
            relayField = Plugin.CfgRelayTyped.Value;
            nameField = Plugin.CfgName.Value;
        }

        void Update()
        {
            bool pressed = false;
            try { pressed = Input.GetKeyDown(Plugin.CfgMenuKey.Value); } catch { }
            if (pressed) SetOpen(!Open);
            if (roomsArrived)
            {
                roomsArrived = false;
                rooms = roomsIncoming;
                roomsNote = roomsIncomingError != null ? "Could not list rooms: " + roomsIncomingError
                          : rooms.Length == 0 ? "No public rooms right now." : "";
            }
            if (Open && Game.InGameScene && !disabledGameInput && InputManager.Singleton)
            {
                InputManager.Singleton.inputActions.PlayerActionMap.Disable();
                disabledGameInput = true;
            }
        }

        void SetOpen(bool open)
        {
            Open = open;
            if (!open && disabledGameInput)
            {
                disabledGameInput = false;
                if (InputManager.Singleton) InputManager.Singleton.inputActions.PlayerActionMap.Enable();
            }
            if (open && Plugin.RelayInfo.State == "idle") Plugin.RelayInfo.Start();
        }

        void Styles()
        {
            if (big != null) return;
            big = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            wrap = new GUIStyle(GUI.skin.label) { wordWrap = true };
        }

        void OnGUI()
        {
            Styles();
            if (Session.ShowSyncCover)
            {
                GUI.color = Color.black;
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(0, Screen.height / 2f - 30, Screen.width, 60), "Syncing with " + Session.HostName + "'s farm...", big);
            }
            DrawToasts();
            DrawHudLine();
            if (Open) window = GUILayout.Window(0x0B3EC00, window, DrawWindow, "Berry Bury Berry Co-op " + Plugin.ModVersion, GUILayout.Width(470));
        }

        void DrawToasts()
        {
            toasts.RemoveAll(t => t.Until < Time.unscaledTime);
            float y = 12;
            foreach (ToastLine t in toasts)
            {
                var rect = new Rect(Screen.width / 2f - 260, y, 520, 26);
                GUI.Box(rect, GUIContent.none);
                GUI.Label(new Rect(rect.x + 8, rect.y + 3, rect.width - 16, 22), t.Text);
                y += 30;
            }
        }

        void DrawHudLine()
        {
            string line = null;
            if (Session.IsHost)
                line = "Co-op host" + (Session.RoomCode.Length > 0 ? "  |  room " + Session.RoomCode : "") + "  |  " + Session.PlayerCount + " players";
            else if (Session.IsGuest) line = "Co-op  |  " + Session.Status;
            else if (SceneManager.GetActiveScene().name == "MainMenu") line = "Co-op: press " + Plugin.CfgMenuKey.Value;
            if (line == null || Open) return;
            GUI.Label(new Rect(10, Screen.height - 26, 700, 22), line);
        }

        void DrawWindow(int id)
        {
            GUILayout.Label(Session.Status, wrap);
            string info = Plugin.RelayInfo.StatusLine();
            if (!string.IsNullOrEmpty(info)) GUILayout.Label(info, wrap);

            if (Session.Role == Role.None) DrawIdle();
            else if (Session.IsHost) DrawHost();
            else DrawGuest();

            GUILayout.Space(6);
            if (GUILayout.Button("Close (" + Plugin.CfgMenuKey.Value + ")")) SetOpen(false);
            GUI.DragWindow();
        }

        void DrawIdle()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Your name", GUILayout.Width(80));
            string n = GUILayout.TextField(nameField ?? "", 24);
            if (n != nameField) { nameField = n; Plugin.CfgName.Value = n.Trim(); }
            GUILayout.EndHorizontal();
            GUILayout.Label("Empty uses your Steam name (" + Plugin.PlayerName + ").", small);

            GUILayout.Space(8);
            GUILayout.Label("Host", GUI.skin.box);
            GUILayout.Label("Your save is the shared farm. Friends load into it and share the wallet.", small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Host online room")) Session.StartHost(true);
            if (GUILayout.Button("Host LAN / direct IP")) Session.StartHost(false);
            GUILayout.EndHorizontal();
            bool listed = GUILayout.Toggle(Plugin.CfgListed.Value, " Show my room in the public list");
            if (listed != Plugin.CfgListed.Value) Plugin.CfgListed.Value = listed;

            GUILayout.Space(8);
            GUILayout.Label("Join", GUI.skin.box);
            GUILayout.Label("Join from the main menu: your own save stays untouched while you play on theirs.", small);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Room code", GUILayout.Width(80));
            code = GUILayout.TextField(code, 5, GUILayout.Width(90)).ToUpperInvariant();
            GUI.enabled = code.Trim().Length == 5;
            if (GUILayout.Button("Join")) Session.JoinCode(code);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh public rooms", GUILayout.Width(170))) RefreshRooms();
            GUILayout.Label(roomsNote, small);
            GUILayout.EndHorizontal();
            if (rooms != null && rooms.Length > 0)
            {
                roomScroll = GUILayout.BeginScrollView(roomScroll, GUILayout.Height(110));
                foreach (string[] r in rooms)
                {
                    if (r.Length < 5) continue;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(r[4] + "  (" + r[1] + "/" + r[2] + ")" + (r[3] == "1" ? "  locked" : ""));
                    GUI.enabled = r[1] != r[2] && r[3] != "1";
                    if (GUILayout.Button("Join " + r[0], GUILayout.Width(100))) Session.JoinCode(r[0]);
                    GUI.enabled = true;
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("IP:port", GUILayout.Width(80));
            ip = GUILayout.TextField(ip ?? "", 64);
            if (GUILayout.Button("Join IP", GUILayout.Width(80))) Session.JoinDirect(ip.Trim());
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Relay server", GUILayout.Width(80));
            string rf = GUILayout.TextField(relayField ?? "", 64);
            if (rf != relayField) { relayField = rf; Plugin.RelayAddress = rf; }
            GUILayout.EndHorizontal();
            GUILayout.Label(Plugin.CfgRelayTyped.Value.Length == 0 ? "Automatic: " + Plugin.RelayAddress + " (from mods-info)" : "Using the server you typed. Clear it to go back to automatic.", small);
        }

        void DrawHost()
        {
            if (Session.RoomCode.Length > 0)
            {
                GUILayout.Label("Room code", GUI.skin.box);
                GUILayout.Label(Session.RoomCode, big, GUILayout.Height(44));
                if (GUILayout.Button("Copy code")) GUIUtility.systemCopyBuffer = Session.RoomCode;
            }
            GUILayout.Label("LAN / direct: port " + Plugin.CfgPort.Value, small);
            if (!Game.InGameScene) GUILayout.Label("Start or load a game. Friends who joined load into it automatically.", wrap);
            GUILayout.Label("Players", GUI.skin.box);
            GUILayout.Label("  " + Plugin.PlayerName + " (host)");
            foreach (Peer p in Session.Peers.Values)
            {
                if (!p.Welcomed) continue;
                string state = !p.Ready ? "loading" : p.InShop ? "in the shop" : "playing";
                if (p.WantsNextDay) state += ", ready for the next day";
                GUILayout.Label("  " + p.Name + " - " + state);
            }
            GUILayout.Label("Traffic: " + (Session.BytesOut / 1024) + " KB out, " + (Session.BytesIn / 1024) + " KB in", small);
            if (GUILayout.Button("Stop hosting")) Session.Shutdown("Stopped hosting");
        }

        void DrawGuest()
        {
            GUILayout.Label("Players", GUI.skin.box);
            foreach (Peer p in Session.Peers.Values) GUILayout.Label("  " + p.Name + (p.Id == 0 ? " (host)" : ""));
            GUILayout.Label("  " + Plugin.PlayerName + " (you)");
            GUILayout.Label("Your saves are not touched while you play on someone else's farm.", small);
            if (GUILayout.Button("Leave")) Session.Shutdown("Left the farm");
        }

        void RefreshRooms()
        {
            roomsNote = "Asking the relay...";
            Session.RefreshPublicRooms((list, error) =>
            {
                roomsIncoming = list ?? new string[0][];
                roomsIncomingError = error;
                roomsArrived = true;
            });
        }
    }
}
