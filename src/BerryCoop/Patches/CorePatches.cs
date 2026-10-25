using System;
using System.Collections.Generic;
using BerryCoop.Net;
using HarmonyLib;
using UnityEngine;

namespace BerryCoop.Patches
{
    // Entity lifecycle: the host numbers new objects, guests drop their own copies of anything the host simulates.
    [HarmonyPatch]
    static class EntityStartPatches
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PickUppable), "Start");
            yield return AccessTools.Method(typeof(SuckUppables), "Start");
            yield return AccessTools.Method(typeof(BerryCultist_AI), "Start");
            yield return AccessTools.Method(typeof(Pinata), "Start");
        }

        static bool Prefix(Component __instance)
        {
            if (!Session.IsGuest || !Session.GuestInWorld) return true;
            NetEntity ne = Entities.Of(__instance);
            if (!ne)
            {
                UnityEngine.Object.Destroy(__instance.gameObject);
                return false;
            }
            // Seed auto-deposit timers would delete the mirror on their own; the host decides.
            return !(__instance is SuckUppables);
        }

        static void Postfix(Component __instance)
        {
            if (Session.IsHost && __instance) Entities.HostTrack(__instance.gameObject);
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.LoadFromSlot))]
    static class LoadFromSlotPatch
    {
        static bool Prefix(int _slotIndex, ref string __result)
        {
            if (Session.IsGuest && Session.PendingWorldJson != null) { __result = Session.PendingWorldJson; return false; }
            if (LabDriver.SaveDir != null) { __result = LabDriver.ReadSave("slot_" + _slotIndex + ".json"); return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.SaveToSlot))]
    static class SaveToSlotPatch
    {
        static bool Prefix(SaveLoadManager __instance, int _slot, string _saveJson)
        {
            if (Session.IsGuest) return false;
            if (Session.IsHost) Session.HostSavedForNextRound(_saveJson);
            if (LabDriver.SaveDir != null)
            {
                LabDriver.WriteSave("slot_" + _slot + ".json", _saveJson);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.SaveSlotInfoToSlot))]
    static class SaveSlotInfoPatch
    {
        static bool Prefix() { return !Session.IsGuest && LabDriver.SaveDir == null; }
    }

    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.SaveGlobalSaveData))]
    static class SaveGlobalPatch
    {
        static bool Prefix() { return !Session.IsGuest && LabDriver.SaveDir == null; }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.InShop_CloseShopAndStartNextRound))]
    static class NextDayPatch
    {
        static bool Prefix()
        {
            if (!Session.IsGuest || !Session.GuestInWorld) return true;
            Session.SendToHost(new Packet(Msg.Ready).Bool(true));
            CoopMenu.Toast("Waiting for the host to start the next day...", 4f);
            return false;
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.ForceEndRound))]
    static class RoundEndPatch
    {
        static void Prefix() { if (Session.IsGuest) Session.BankContext++; }

        static void Postfix() { if (Session.IsHost) Session.HostRoundEnded(); }

        static Exception Finalizer(Exception __exception)
        {
            if (Session.IsGuest && Session.BankContext > 0) Session.BankContext--;
            return __exception;
        }
    }

    // The wallet and the coin pocket live on the host; the host banks the shared pocket when the day ends.
    [HarmonyPatch(typeof(PlayerStats), nameof(PlayerStats.IncreaseMoney_Banked))]
    static class BankPatch
    {
        static bool Prefix()
        {
            if (!Session.IsGuest || !Session.GuestInWorld) return true;
            if (Session.BankContext > 0) { try { HudManager.Singleton.PlayFeedback_MoneyBanked(); } catch { } }
            return false;
        }
    }

    // Anyone going to bed (the bed only appears at night) ends the day for everyone.
    [HarmonyPatch(typeof(WorldUsable), nameof(WorldUsable.Bed_EndRound))]
    static class BedPatch
    {
        static void Postfix()
        {
            if (Session.GuestLive) Session.SendToHost(new Packet(Msg.Slept).Int(Session.Epoch));
        }
    }

    [HarmonyPatch(typeof(PlayerStats), nameof(PlayerStats.IncreaseMoney_Held))]
    static class PocketPatch
    {
        static bool Prefix() { return !Session.IsGuest || !Session.GuestInWorld || Session.GrantContext > 0; }
    }

    [HarmonyPatch]
    static class SharedStatPatches
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            string[] names =
            {
                "SpendMoney", "IncreaseSeeds", "SpendSeeds", "IncreaseStarOrbs", "SpendStarOrbs", "AddMoneyToPiggyBank",
                "AddToCultistTotalCount", "AddSpawnedButNotDepositedStarOrb", "RemoveOrbFromSpawnedButNotDeposited",
                "IncreaseWallsBrokenStat", "UsedBerryBlitz_SetCooldownToMax", "UsedBigHole_SetCooldownToMax",
                "RoundStarted_TickAbilityCooldownsByOne",
            };
            foreach (string n in names)
            {
                var m = AccessTools.Method(typeof(PlayerStats), n);
                if (m != null) yield return m;
            }
        }

        static bool Prefix() { return !Session.IsGuest || !Session.GuestInWorld; }
    }

    [HarmonyPatch(typeof(PlayerStats), nameof(PlayerStats.RollForSeedDrop))]
    static class SeedRollPatch
    {
        static bool Prefix(ref bool __result)
        {
            if (!Session.IsGuest || !Session.GuestInWorld) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(SuckUppables), nameof(SuckUppables.OnPlayerPickedUsed))]
    static class TakePatch
    {
        static bool Prefix(SuckUppables __instance)
        {
            if (!Session.IsGuest || !Session.GuestInWorld) return true;
            NetEntity ne = Entities.Of(__instance);
            if (ne && Session.GuestLive) Entities.RequestTake(ne, __instance.pickedUpManually);
            return false;
        }
    }

    [HarmonyPatch(typeof(PickUppable), nameof(PickUppable.OnPickUp))]
    static class PickUpPatch
    {
        static void Postfix(PickUppable __instance)
        {
            if (Session.GuestLive && Session.Replaying == 0) Entities.Claim(Entities.Of(__instance), ClaimReason.Pickup);
        }
    }

    [HarmonyPatch(typeof(PlayerGrabber), "AttemptPickup")]
    static class AttemptPickupPatch
    {
        static bool Prefix(GameObject _object)
        {
            if (!Session.WorldLive) return true;
            NetEntity ne = Entities.Of(_object);
            if (!ne || ne.Owner == 0) return true;
            return Session.IsGuest && ne.Owner == Session.LocalId;
        }
    }

    [HarmonyPatch(typeof(PlayerGrabber), "DropHeldItem")]
    static class DropPatch
    {
        static void Postfix(bool _Throw, PickUppable _droppedPickUp)
        {
            if (Session.GuestLive) Entities.ReleaseSoon(Entities.Of(_droppedPickUp), _Throw ? ReleaseReason.Throw : ReleaseReason.Drop);
        }
    }

    [HarmonyPatch(typeof(PlayerGrabber), "ShootBerryFromVacuum")]
    static class VacShootPatch
    {
        static void Postfix(Rigidbody _rb)
        {
            if (Session.GuestLive && _rb) Entities.ReleaseSoon(Entities.Of(_rb), ReleaseReason.VacShoot);
        }
    }

    [HarmonyPatch(typeof(VacuumSuckTrigger), "OnTriggerEnter")]
    static class VacuumZonePatch
    {
        static void Postfix(VacuumSuckTrigger __instance)
        {
            if (!Session.WorldLive || __instance.rbsInSuckZone.Count == 0) return;
            Rigidbody rb = __instance.rbsInSuckZone[__instance.rbsInSuckZone.Count - 1];
            NetEntity ne = Entities.Of(rb);
            if (!ne) return;
            if (Session.IsHost)
            {
                if (ne.Owner != 0) __instance.rbsInSuckZone.Remove(rb);
                return;
            }
            Entities.Claim(ne, ClaimReason.Vacuum);
            if (!ne.OwnedLocally) __instance.rbsInSuckZone.Remove(rb);
        }
    }

    [HarmonyPatch(typeof(PlayerGrabber), "KickInFrontOfUs")]
    static class KickPatch
    {
        static void Prefix(ref List<NetEntity> __state)
        {
            __state = Session.GuestLive ? Entities.ClaimKickTargets() : null;
        }

        static void Postfix(List<NetEntity> __state)
        {
            if (__state == null) return;
            foreach (NetEntity ne in __state) Entities.ReleaseSoon(ne, ReleaseReason.Kick);
        }
    }

    [HarmonyPatch(typeof(PlayerGrabber), "FirePopGun")]
    static class PopGunPatch
    {
        static void Prefix(PlayerGrabber __instance)
        {
            if (!Session.GuestLive) return;
            GameManager gm = GameManager.Singleton;
            float cd = Traverse.Create(__instance).Field("popgun_FireCooldown_Curr").GetValue<float>();
            if (gm.gameState != GameManager.GameState.Playing || gm.isLookingAtPC || cd > 0f) return;
            Transform at = Game.DartSpawn; GameObject cam = Game.Camera;
            if (!at || !cam) return;
            Session.SendToHost(new Packet(Msg.PopGun).Int(Session.Epoch).Vec(at.position).Quat(at.rotation).Vec(cam.transform.forward));
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.BuildCurrentBuildable))]
    static class BuildPatch
    {
        static bool Prefix(GameManager __instance, Vector3 _pos, Quaternion _rot, bool _isGridSnapped, ref HashSet<Buildable> __state)
        {
            __state = null;
            if (Session.PlacingBuild || !Session.WorldLive) return true;
            if (Session.IsGuest)
            {
                Commands.GuestBuild(__instance.buildMode_BuildableIdentity, _pos, _rot, _isGridSnapped);
                return false;
            }
            __state = new HashSet<Buildable>(UnityEngine.Object.FindObjectsByType<Buildable>(FindObjectsSortMode.None));
            return true;
        }

        static void Postfix(GameManager __instance, Vector3 _pos, Quaternion _rot, bool _isGridSnapped, HashSet<Buildable> __state)
        {
            if (__state == null) return;
            foreach (Buildable b in UnityEngine.Object.FindObjectsByType<Buildable>(FindObjectsSortMode.None))
            {
                if (__state.Contains(b)) continue;
                GameObject root = b.transform.root.gameObject;
                Commands.HostAnnounceBuild(root, b.buildableIdentity, _pos, _rot, _isGridSnapped, 0);
                return;
            }
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnBuildableDestroyed_RunAdditionalLogic))]
    static class SellPatch
    {
        static bool Prefix(Buildable _buildable)
        {
            if (Session.SellingBuild || !Session.WorldLive || !_buildable) return true;
            BuiltObject bo = _buildable.GetComponentInParent<BuiltObject>(true);
            if (!bo) return !Session.IsGuest;
            if (Session.IsGuest)
            {
                Session.SendToHost(new Packet(Msg.Sell).Int(Session.Epoch).Int(bo.Id));
                return false;
            }
            Session.BroadcastReady(new Packet(Msg.Sell).Int(Session.Epoch).Int(bo.Id));
            return true;
        }
    }

    // Intent contexts: player input and UI clicks are the only things guests forward.
    [HarmonyPatch]
    static class IntentPatches
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerGrabber), "HandlePlayerInputs");
            yield return AccessTools.Method(typeof(UpgradeTreeButton), "OnPointerClick");
            yield return AccessTools.Method(typeof(UpgradeTreeButton), "OnPointerDown");
            yield return AccessTools.Method(typeof(UpgradeTreeButton), "HandleHeld");
            yield return AccessTools.Method(typeof(UpgradeTreeButton), "Hold_RepeatableClicks_Clicked");
            yield return AccessTools.Method(typeof(ShopAndUpgradesManager), "UpgradeBerryButtonClicked");
            yield return AccessTools.Method(typeof(GameManager), "ToggleHaltedBerryProduction_ButtonClick");
        }

        static void Prefix() { Session.IntentContext++; }

        static Exception Finalizer(Exception __exception)
        {
            if (Session.IntentContext > 0) Session.IntentContext--;
            return __exception;
        }
    }

    // Shop panels refresh by toggling HUD groups; only do that for the player actually in the shop.
    [HarmonyPatch(typeof(ShopPanelHelper), "TogglePlayingAndShopToReUpdatePanelInfo")]
    static class ShopRefreshPatch
    {
        static bool Prefix() { return !Game.InGameScene || GameManager.Singleton.gameState == GameManager.GameState.RoundOverShop; }
    }

    [HarmonyPatch(typeof(Player), "HandleMouseCursor")]
    static class CursorPatch
    {
        static bool Prefix()
        {
            if (!CoopMenu.Open) return true;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            return false;
        }
    }

    // Guests grow berries visually from the mirrored progress; the host spawns the real berry.
    [HarmonyPatch(typeof(BerryGrower), nameof(BerryGrower.HandleGrowth))]
    static class GrowthPatch
    {
        static bool Prefix(BerryGrower __instance)
        {
            if (!Session.IsGuest || !Session.GuestInWorld) return true;
            GameManager gm = GameManager.Singleton;
            if (gm.gameState != GameManager.GameState.Playing || __instance.isGrowingHalted || gm.hasTimerElapsed_IsNighttime) return false;
            if (__instance.delayBeforeGrowing > 0f) __instance.delayBeforeGrowing -= Time.deltaTime;
            else
            {
                float max = Game.GrowerMax(__instance);
                float g = Mathf.Min(max, Game.GrowerGrowth(__instance) + Time.deltaTime * 1.25f * gm.globalBerryGrowthRate);
                Game.SetGrowerGrowth(__instance, g);
            }
            if (Game.GrowerHasGrowingVisual(__instance) && Game.GrowerMax(__instance) > 0f)
                __instance.transform.localScale = Vector3.one * (Game.GrowerGrowth(__instance) / Game.GrowerMax(__instance));
            return false;
        }
    }
}
