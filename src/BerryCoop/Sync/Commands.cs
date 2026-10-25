using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BerryCoop.Net;
using HarmonyLib;
using UnityEngine;

namespace BerryCoop
{
    public enum CmdMode
    {
        // Player intent (input or UI): guests forward it, the host runs it and replays it to everyone.
        Intent,
        // Always a player action (only UI buttons call it): same as Intent without needing a context.
        Ui,
        // World events the host's physics decides; guests forward their own copy, everyone sees the host's result.
        WorldEvent,
        // Host-only event replayed to guests for its visuals; guest copies never run on their own.
        HostEvent,
        // Guest forwards, nobody replays (the result arrives through the state mirror).
        ForwardOnly,
    }

    public sealed class CmdDef
    {
        public int Id;
        public MethodInfo Method;
        public CmdMode Mode;
        public ParameterInfo[] Params;
    }

    public static class Commands
    {
        public static readonly List<CmdDef> Defs = new List<CmdDef>();
        static readonly Dictionary<MethodBase, CmdDef> byMethod = new Dictionary<MethodBase, CmdDef>();
        static int depth;
        public static int Origin = -1;

        static void Def(Type t, string name, CmdMode mode)
        {
            MethodInfo m = AccessTools.Method(t, name);
            if (m == null) { NetLog.Warn("command not found: " + t.Name + "." + name); return; }
            var d = new CmdDef { Id = Defs.Count, Method = m, Mode = mode, Params = m.GetParameters() };
            Defs.Add(d);
            byMethod[m] = d;
        }

        public static void Build()
        {
            Def(typeof(UpgradeTreeManager), "UnlockUpgrade", CmdMode.Intent);
            foreach (MethodInfo m in typeof(ShopPanelHelper).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (m.Name.StartsWith("BuyButton_")) Def(typeof(ShopPanelHelper), m.Name, CmdMode.Ui);
            Def(typeof(ShopAndUpgradesManager), "UpgradePlantBedsBerryTier", CmdMode.Intent);
            Def(typeof(PlantBed), "ToggleBerryHaltProduction", CmdMode.Intent);
            Def(typeof(GameManager), "ActivateBerryBlitz", CmdMode.Intent);
            Def(typeof(GameManager), "ActivateBigHolePowerup", CmdMode.Intent);
            Def(typeof(GameManager), "SetNewHoleDestination", CmdMode.ForwardOnly);
            Def(typeof(GameManager), "ActivateSugarRush", CmdMode.HostEvent);
            Def(typeof(BreakableWall), "BreakWall", CmdMode.WorldEvent);
            Def(typeof(Diggable), "DigUp", CmdMode.WorldEvent);
            Def(typeof(PickUppable), "BreakStarOrb", CmdMode.HostEvent);
            Def(typeof(Radio), "ToggleRadio", CmdMode.Intent);
            Def(typeof(Radio), "ChangeRadioChannel_Up", CmdMode.Intent);
            Def(typeof(Radio), "ChangeRadioChannel_Down", CmdMode.Intent);
            Def(typeof(BerryPicker), "Button_Up", CmdMode.Intent);
            Def(typeof(BerryPicker), "Button_Down", CmdMode.Intent);
            Def(typeof(WorldUsable), "PiggyBank_RetrieveMoney", CmdMode.Intent);
            Def(typeof(SlotMachine_Manager), "AttemptUse", CmdMode.Intent);
        }

        public static void Patch(Harmony h)
        {
            var prefix = new HarmonyMethod(typeof(Commands), nameof(Prefix)) { priority = Priority.First };
            var postfix = new HarmonyMethod(typeof(Commands), nameof(Postfix));
            var finalizer = new HarmonyMethod(typeof(Commands), nameof(Finalizer));
            foreach (CmdDef d in Defs) h.Patch(d.Method, prefix: prefix, postfix: postfix, finalizer: finalizer);
        }

        static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args, ref bool __state)
        {
            __state = false;
            CmdDef d;
            if (!Session.WorldLive || !byMethod.TryGetValue(__originalMethod, out d)) return true;
            if (Session.IsGuest)
            {
                if (Session.Replaying > 0) return true;
                bool intent = Session.IntentContext > 0;
                switch (d.Mode)
                {
                    case CmdMode.Intent:
                    case CmdMode.ForwardOnly:
                        if (!intent) return true;
                        break;
                    case CmdMode.HostEvent:
                        return false;
                }
                if (!GuestBefore(d, __instance, __args)) return false;
                Send(Msg.Cmd, d, __instance, __args);
                return false;
            }
            depth++;
            __state = true;
            return true;
        }

        static void Postfix(MethodBase __originalMethod, object __instance, object[] __args, bool __state)
        {
            if (!__state || depth != 1 || !Session.IsHost) return;
            CmdDef d = byMethod[__originalMethod];
            bool fromGuest = Session.HostExecuting > 0;
            bool replay;
            switch (d.Mode)
            {
                case CmdMode.Intent: replay = fromGuest || Session.IntentContext > 0; break;
                case CmdMode.ForwardOnly: replay = false; break;
                default: replay = true; break;
            }
            if (replay && HostShouldReplay(d, __instance, __args)) Send(Msg.Rpc, d, __instance, __args);
        }

        static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) depth--;
            return __exception;
        }

        static void Send(Msg type, CmdDef d, object instance, object[] args)
        {
            var p = new Packet(type).Int(Session.Epoch).Int(d.Id).Int(type == Msg.Rpc ? Origin : Session.LocalId);
            p.Str(d.Method.IsStatic ? "" : NetObjects.KeyOf((Component)instance) ?? "");
            p.Int(args.Length);
            for (int i = 0; i < args.Length; i++) WriteArg(p, d.Params[i].ParameterType, args[i]);
            if (type == Msg.Cmd) Session.SendToHost(p);
            else Session.BroadcastReady(p);
        }

        static void WriteArg(Packet p, Type t, object v)
        {
            if (typeof(UpgradeTreeButton).IsAssignableFrom(t))
                p.Int(v == null ? -1 : UpgradeTreeManager.Singleton.allUpgradeTreeButtons.IndexOf((UpgradeTreeButton)v));
            else if (typeof(BerryProfile).IsAssignableFrom(t))
                p.Int(v == null ? -1 : GameManager.Singleton.prefabBank.berryProfiles.IndexOf((BerryProfile)v));
            else if (typeof(Component).IsAssignableFrom(t))
                p.Str(NetObjects.KeyOf((Component)v) ?? "");
            else if (t == typeof(Quaternion)) p.Quat((Quaternion)v);
            else Codec.Write(p, t, v);
        }

        static object ReadArg(Reader r, Type t)
        {
            if (typeof(UpgradeTreeButton).IsAssignableFrom(t))
            {
                int i = r.Int(); var list = UpgradeTreeManager.Singleton.allUpgradeTreeButtons;
                return i >= 0 && i < list.Count ? list[i] : null;
            }
            if (typeof(BerryProfile).IsAssignableFrom(t))
            {
                int i = r.Int(); var list = GameManager.Singleton.prefabBank.berryProfiles;
                return i >= 0 && i < list.Count ? list[i] : null;
            }
            if (typeof(Component).IsAssignableFrom(t)) return NetObjects.Resolve(r.Str(), t);
            if (t == typeof(Quaternion)) return r.Quat();
            return Codec.Read(r, t);
        }

        static bool Decode(Reader r, out CmdDef d, out int origin, out object target, out object[] args)
        {
            d = null; target = null; args = null; origin = -1;
            int id = r.Int();
            origin = r.Int();
            if (id < 0 || id >= Defs.Count) return false;
            d = Defs[id];
            string key = r.Str();
            int n = r.Int();
            args = new object[n];
            for (int i = 0; i < n; i++) args[i] = ReadArg(r, d.Params[i].ParameterType);
            if (!d.Method.IsStatic)
            {
                target = NetObjects.Resolve(key, d.Method.DeclaringType);
                if (target == null) { NetLog.Debug("command target gone: " + d.Method.Name + " " + key); return false; }
            }
            for (int i = 0; i < n; i++)
                if (args[i] == null && typeof(UnityEngine.Object).IsAssignableFrom(d.Params[i].ParameterType)) return false;
            return true;
        }

        public static void HostHandleCmd(Peer peer, Reader r)
        {
            if (r.Int() != Session.Epoch || !peer.Ready) return;
            CmdDef d; int origin; object target; object[] args;
            if (!Decode(r, out d, out origin, out target, out args)) return;
            if (!HostAllow(peer, d, target, args)) return;
            GameManager gm = GameManager.Singleton;
            GameManager.GameState saved = gm.gameState;
            // Upgrade buttons only exist in the shop UI, so a guest's purchase always pays from the wallet.
            bool shop = d.Method.DeclaringType == typeof(UpgradeTreeManager);
            if (shop) gm.gameState = GameManager.GameState.RoundOverShop;
            Session.HostExecuting++;
            Origin = peer.Id;
            try { d.Method.Invoke(target, args); }
            catch (Exception e) { NetLog.Error("guest command " + d.Method.Name + ": " + (e.InnerException ?? e)); }
            finally
            {
                Session.HostExecuting--;
                Origin = -1;
                if (shop && gm.gameState == GameManager.GameState.RoundOverShop) gm.gameState = saved;
            }
        }

        public static void GuestHandleRpc(Reader r)
        {
            if (r.Int() != Session.Epoch || !Session.GuestLive) return;
            CmdDef d; int origin; object target; object[] args;
            if (!Decode(r, out d, out origin, out target, out args)) return;
            if (!GuestReplayAllowed(d, args)) return;
            Session.Replaying++;
            try { d.Method.Invoke(target, args); }
            catch (Exception e) { NetLog.Warn("replay " + d.Method.Name + ": " + (e.InnerException ?? e).Message); }
            finally { Session.Replaying--; }
            GuestAfterReplay(d, args);
        }

        static UpgradeTreeButton ButtonArg(object[] args) { return args.Length > 0 ? args[0] as UpgradeTreeButton : null; }

        // Guest-side checks before forwarding; false swallows the call.
        static bool GuestBefore(CmdDef d, object instance, object[] args)
        {
            if (d.Method.Name == "UnlockUpgrade")
            {
                var b = ButtonArg(args);
                if (b && b.upgradeIdentity == UpgradeTreeIdentity.NextRoundButton)
                {
                    Session.SendToHost(new Packet(Msg.Ready).Bool(true));
                    CoopMenu.Toast("Waiting for the host to start the next day...", 4f);
                    return false;
                }
            }
            if (d.Method.Name == "SetNewHoleDestination")
            {
                if (Time.unscaledTime - lastHoleSend < 0.1f) return false;
                lastHoleSend = Time.unscaledTime;
            }
            return true;
        }
        static float lastHoleSend;

        static bool HostAllow(Peer peer, CmdDef d, object target, object[] args)
        {
            if (d.Method.Name == "UnlockUpgrade")
            {
                var b = ButtonArg(args);
                if (!b || b.isUnlocked) return false;
                if (b.upgradeIdentity == UpgradeTreeIdentity.NextRoundButton) return false;
                if (!b.CanAfford())
                {
                    peer.Conn.Send(new Packet(Msg.CmdDenied).Str("Not enough in the shared wallet for that."));
                    return false;
                }
            }
            if (d.Method.Name == "SetNewHoleDestination") GameManager.Singleton.playerMovingHole = true;
            return true;
        }

        static bool HostShouldReplay(CmdDef d, object instance, object[] args)
        {
            if (d.Method.Name == "UnlockUpgrade")
            {
                var b = ButtonArg(args);
                if (!b) return false;
                return b.upgradeIdentity != UpgradeTreeIdentity.NextRoundButton && b.upgradeIdentity != UpgradeTreeIdentity.Rewind;
            }
            return true;
        }

        static bool GuestReplayAllowed(CmdDef d, object[] args)
        {
            if (d.Method.Name == "UnlockUpgrade")
            {
                var b = ButtonArg(args);
                return b && !b.isUnlocked;
            }
            return true;
        }

        static void GuestAfterReplay(CmdDef d, object[] args)
        {
            if (d.Method.DeclaringType == typeof(UpgradeTreeManager))
            {
                try { UpgradeTreeManager.Singleton.UpdateAllUpgradeTreeTiles(); } catch { }
            }
        }

        // Unlock found in the mirror without a replay (missed message or late join).
        public static void GuestApplyUnlock(UpgradeTreeButton b)
        {
            if (b.upgradeIdentity == UpgradeTreeIdentity.NextRoundButton || b.upgradeIdentity == UpgradeTreeIdentity.Rewind)
            {
                b.isUnlocked = true;
                return;
            }
            Session.Replaying++;
            try { UpgradeTreeManager.Singleton.UnlockUpgrade(b, false); }
            catch (Exception e) { NetLog.Warn("apply unlock: " + e.Message); b.isUnlocked = true; }
            finally { Session.Replaying--; }
        }

        // Buildables: the host instantiates and numbers them; everyone places the same object.
        public static void GuestBuild(BuildableIdentity identity, Vector3 pos, Quaternion rot, bool snapped)
        {
            Session.SendToHost(new Packet(Msg.Build).Int(Session.Epoch).Int((int)identity).Vec(pos).Quat(rot).Bool(snapped));
        }

        static BuildableInfo InfoFor(BuildableIdentity id)
        {
            PrefabBank bank = GameManager.Singleton.prefabBank;
            switch (id)
            {
                case BuildableIdentity.DirtPatch: return bank.buildable_DirtPatch;
                case BuildableIdentity.ConveyorBelt: return bank.buildable_ConveyorBelt;
                case BuildableIdentity.Trampoline: return bank.buildable_Trampoline;
                case BuildableIdentity.Blender: return bank.buildable_Blender;
            }
            return null;
        }

        // Runs GameManager.BuildCurrentBuildable for someone else's placement without touching our own build mode.
        public static GameObject PlaceBuildable(BuildableIdentity identity, Vector3 pos, Quaternion rot, bool snapped, bool exitWhenDone)
        {
            GameManager gm = GameManager.Singleton;
            BuildableInfo info = InfoFor(identity);
            if (info == null) return null;
            if (snapped)
            {
                BuildSpot spot;
                if (!gm.buildSpotsDict.TryGetValue(gm.GetGridDictFromPos(pos), out spot) || spot.isBuiltOn) return null;
            }
            var t = Traverse.Create(gm);
            object prefab = t.Field("buildMode_BuildablePrefab").GetValue();
            BuildableIdentity savedId = gm.buildMode_BuildableIdentity;
            bool savedCancel = gm.buildMode_CancelModeAfterPlacement;
            var before = new HashSet<Buildable>(UnityEngine.Object.FindObjectsByType<Buildable>(FindObjectsSortMode.None));
            t.Field("buildMode_BuildablePrefab").SetValue(info.prefabToSpawn);
            gm.buildMode_BuildableIdentity = identity;
            gm.buildMode_CancelModeAfterPlacement = exitWhenDone && savedCancel;
            Session.Replaying++;
            Session.PlacingBuild = true;
            try { gm.BuildCurrentBuildable(pos, rot, snapped); }
            finally
            {
                Session.PlacingBuild = false;
                Session.Replaying--;
                if (gm.buildModeState == PlayerBuildModeState.BuildMode)
                {
                    t.Field("buildMode_BuildablePrefab").SetValue(prefab);
                    gm.buildMode_BuildableIdentity = savedId;
                }
                gm.buildMode_CancelModeAfterPlacement = savedCancel;
            }
            foreach (Buildable b in UnityEngine.Object.FindObjectsByType<Buildable>(FindObjectsSortMode.None))
                if (!before.Contains(b)) return b.transform.root.gameObject;
            return null;
        }

        public static void HostHandleBuild(Peer peer, Reader r)
        {
            if (r.Int() != Session.Epoch || !peer.Ready) return;
            var identity = (BuildableIdentity)r.Int(); Vector3 pos = r.Vec(); Quaternion rot = r.Quat(); bool snapped = r.Bool();
            GameObject go = PlaceBuildable(identity, pos, rot, snapped, false);
            if (go) HostAnnounceBuild(go, identity, pos, rot, snapped, peer.Id);
        }

        public static void HostAnnounceBuild(GameObject go, BuildableIdentity identity, Vector3 pos, Quaternion rot, bool snapped, int origin)
        {
            int id = NetObjects.NextBuildId;
            NetObjects.AddBuilt(go, id);
            Session.BroadcastReady(new Packet(Msg.Build).Int(Session.Epoch).Int(id).Int((int)identity).Vec(pos).Quat(rot).Bool(snapped).Int(origin));
        }

        public static void GuestHandleBuild(Reader r)
        {
            if (r.Int() != Session.Epoch || !Session.GuestLive) return;
            int id = r.Int(); var identity = (BuildableIdentity)r.Int(); Vector3 pos = r.Vec(); Quaternion rot = r.Quat(); bool snapped = r.Bool(); int origin = r.Int();
            if (NetObjects.Built(id)) return;
            GameObject go = PlaceBuildable(identity, pos, rot, snapped, origin == Session.LocalId);
            if (go) NetObjects.AddBuilt(go, id);
            else NetLog.Warn("could not place buildable " + identity + " #" + id);
        }

        public static void Sell(int buildId)
        {
            GameObject go = NetObjects.Built(buildId);
            if (!go) return;
            Buildable b = go.GetComponentInChildren<Buildable>(true);
            GameManager gm = GameManager.Singleton;
            Session.Replaying++;
            Session.SellingBuild = true;
            try
            {
                if (b) gm.OnBuildableDestroyed_RunAdditionalLogic(b);
                gm.SetGridTileToAllowBuildingOn(go.transform.position);
            }
            finally { Session.SellingBuild = false; Session.Replaying--; }
            UnityEngine.Object.Destroy(go);
        }

        public static void HostHandleSell(Peer peer, Reader r)
        {
            if (r.Int() != Session.Epoch || !peer.Ready) return;
            int id = r.Int();
            if (!NetObjects.Built(id)) return;
            Sell(id);
            Session.BroadcastReady(new Packet(Msg.Sell).Int(Session.Epoch).Int(id));
        }

        public static void GuestHandleSell(Reader r)
        {
            if (r.Int() != Session.Epoch || !Session.GuestLive) return;
            Sell(r.Int());
        }

        // Pop gun darts are fired from the guest's camera on the host, where they can push things.
        public static void HostHandlePopGun(Peer peer, Reader r)
        {
            if (r.Int() != Session.Epoch || !peer.Ready) return;
            Vector3 pos = r.Vec(); Quaternion rot = r.Quat(); Vector3 fwd = r.Vec();
            GameObject prefab = Game.DartPrefab;
            if (!prefab) return;
            GameObject dart = UnityEngine.Object.Instantiate(prefab, pos, rot);
            GameManager.Singleton.popgun_SpawnedBullets.Add(dart);
            var rb = dart.GetComponent<Rigidbody>();
            if (rb) rb.AddForce(fwd * Game.DartForce, ForceMode.VelocityChange);
        }
    }
}
