using System.Linq;
using System.Reflection;
using BerryCoop.Net;
using HarmonyLib;
using UnityEngine;

namespace BerryCoop
{
    // Effects that only happen in the host's physics (a thrown star wand upgrading a berry buddy), replayed on guests.
    public static class Effects
    {
        const byte BuddyUpgraded = 1, CannotUpgrade = 2;

        public static void HostSend(byte kind, Vector3 pos, bool flag)
        {
            if (!Session.IsHost || !Session.WorldLive || Session.Replaying > 0) return;
            Session.BroadcastReady(new Packet(Msg.Fx).Int(Session.Epoch).Byte(kind).Vec(pos).Bool(flag));
        }

        public static void GuestPlay(Reader r)
        {
            int epoch = r.Int(); byte kind = r.Byte(); Vector3 pos = r.Vec(); bool flag = r.Bool();
            if (epoch != Session.Epoch || !Session.GuestLive) return;
            Session.Replaying++;
            try
            {
                NetLog.Info("played host effect " + kind + " at " + pos);
                if (kind == BuddyUpgraded)
                {
                    AudioManager.Singleton.PlayUpgradeCultistSFX(pos, flag);
                    GameObject fx = GameManager.Singleton.prefabBank.cultistUpgradeParticles_Prefab;
                    if (fx) Object.Destroy(Object.Instantiate(fx, pos, Quaternion.identity), 6f);
                }
                else if (kind == CannotUpgrade) AudioManager.Singleton.PlayCannotUpgradeCultistSFX(pos);
            }
            finally { Session.Replaying--; }
        }

        [HarmonyPatch(typeof(AudioManager), nameof(AudioManager.PlayUpgradeCultistSFX))]
        static class UpgradeSfxPatch
        {
            static void Postfix(Vector3 _pos, bool _belladonna) { HostSend(BuddyUpgraded, _pos, _belladonna); }
        }

        [HarmonyPatch(typeof(AudioManager), nameof(AudioManager.PlayCannotUpgradeCultistSFX))]
        static class CannotUpgradeSfxPatch
        {
            static void Postfix(Vector3 _pos) { HostSend(CannotUpgrade, _pos, false); }
        }
    }

    // Open shop screens redraw when anyone's purchase changes the wallet, levels or unlocks.
    public static class ShopScreens
    {
        static float lastCheck;
        static string lastSig;
        static FieldInfo[] levels;

        public static void Tick()
        {
            if (Time.unscaledTime - lastCheck < 0.3f) return;
            lastCheck = Time.unscaledTime;
            if (!Game.InGameScene || GameManager.Singleton.gameState != GameManager.GameState.RoundOverShop) { lastSig = null; return; }
            string sig = Signature();
            if (sig == lastSig) return;
            bool first = lastSig == null;
            lastSig = sig;
            if (first) return;
            try
            {
                if (ShopPanelHelper.Singleton)
                    foreach (ShopPanel panel in Object.FindObjectsByType<ShopPanel>(FindObjectsSortMode.None))
                        ShopPanelHelper.Singleton.UpdateStorePanelInfo(panel);
                UpgradeTreeManager.Singleton.UpdateAllUpgradeTreeTiles();
            }
            catch (System.Exception e) { NetLog.Warn("shop refresh: " + e.Message); }
        }

        static string Signature()
        {
            PlayerStats s = PlayerStats.Singleton;
            int unlocked = UpgradeTreeManager.Singleton.allUpgradeTreeButtons.Count(b => b && b.isUnlocked);
            int levelSum = 0;
            var shop = ShopAndUpgradesManager.Singleton;
            if (shop)
            {
                if (levels == null)
                    levels = typeof(ShopAndUpgradesManager).GetFields(BindingFlags.Instance | BindingFlags.Public)
                        .Where(f => f.FieldType == typeof(int) && f.Name.EndsWith("_Level")).ToArray();
                foreach (FieldInfo f in levels) levelSum += (int)f.GetValue(shop);
            }
            return s.money + "/" + s.starOrbs + "/" + s.seeds + "/" + unlocked + "/" + levelSum + "/" + s.dirtPatch_NumOfPurchased + "/"
                 + s.conveyorBelt_NumOfPacksPurchased + "/" + s.trampolines_NumOfPurchased + "/" + s.blenders_NumOfPurchased;
        }
    }
}
