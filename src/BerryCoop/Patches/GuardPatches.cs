using System;
using HarmonyLib;
using UnityEngine;

namespace BerryCoop.Patches
{
    public static class CommandPatches
    {
        public static void Register(Harmony h)
        {
            Commands.Build();
            Commands.Patch(h);
        }
    }

    // On guests the host's physics decides every world interaction with a synced object, so the guest's own
    // copies of those triggers and simulations stand down.
    public static class GuardPatches
    {
        static readonly string[] worldTriggers =
        {
            "Blender.OnTriggerEnter", "ConveyorBelt_Trigger.OnTriggerEnter", "ConveyorBelt_Trigger.OnTriggerExit",
            "DiskInsertTrigger.OnTriggerEnter", "MilestoneGrouper.OnTriggerEnter", "PickUpRejectionTrigger.OnTriggerEnter",
            "PoolVacuumTrigger.OnTriggerEnter", "PoolVacuumTrigger.OnTriggerExit", "SmoothieStand.OnTriggerEnter",
            "StarDoor.OnTriggerEnter", "SuckUpVacuumTrigger.OnTriggerEnter", "SuckUpVacuumTrigger.OnTriggerExit",
            "TrampolineTrigger.OnTriggerEnter", "TurtleEatAoe.OnTriggerEnter", "TurtleEatAoe.OnTriggerExit",
        };

        static readonly string[] worldCollisions =
        {
            "FlipperPuzzle_Tile.OnCollisionEnter", "GoldenIdol_Pillar.OnCollisionEnter", "Pinata.OnCollisionEnter",
        };

        // The synced object's own physics callbacks: never on guests.
        static readonly string[] entitySelf =
        {
            "PickUppable.OnCollisionEnter", "PickUppable.OnTriggerEnter", "PickUppable.OnTriggerExit", "PickUppable.Update",
            "Berry.OnCollisionEnter", "Berry.OnTriggerEnter", "Berry.OnTriggerExit", "Berry.RollForGoldenBerry",
            "Berry.HandleNebulaBerryLifetime", "BerryCultist_AI.OnCollisionEnter", "BerryCultist_AI.Update", "BerryCultist_AI.FixedUpdate",
        };

        // Systems that spawn, destroy or level things up on their own; the host runs them for everyone.
        static readonly string[] hostSimulation =
        {
            "GameManager.HandleSoftLockingByHavingNoPlantsOrSeeds", "GameManager.CheckIfOverBerryLimit", "GameManager.CheckIfOverCoinLimit",
            "GameManager.HandleLevelingUpHole", "GameManager.HandleHolePrestige",
            "BerryGrower.HandleRoundStartBerryChoosing", "TurtleEatAoe.Update", "VoidBoxSpawnManager.Update", "NebulaBerryManager.Update",
            "PinataManager.Update", "GnomeDuplicator.FixedUpdate", "AnomalousMaterial_Manager.Update", "StarOrbGenerator.Update",
            "SuckUpVacuumTrigger.FixedUpdate", "PlantBed.CheckForLevelUp", "PlantBed.IncreaseBerriesProducedStat",
        };

        public static void Register(Harmony h)
        {
            var trigger = new HarmonyMethod(typeof(GuardPatches), nameof(TriggerGuard));
            var collision = new HarmonyMethod(typeof(GuardPatches), nameof(CollisionGuard));
            var self = new HarmonyMethod(typeof(GuardPatches), nameof(SelfGuard));
            var sim = new HarmonyMethod(typeof(GuardPatches), nameof(SimulationGuard));
            int n = 0;
            foreach (string s in worldTriggers) n += Patch(h, s, trigger);
            foreach (string s in worldCollisions) n += Patch(h, s, collision);
            foreach (string s in entitySelf) n += Patch(h, s, self);
            foreach (string s in hostSimulation) n += Patch(h, s, sim);
            n += Patch(h, "BreakableWall.OnTriggerEnter", new HarmonyMethod(typeof(GuardPatches), nameof(WallGuard)));
            n += Patch(h, "PickUppable.FixedUpdate", new HarmonyMethod(typeof(GuardPatches), nameof(OwnedOnlyGuard)));
            n += Patch(h, "BerryGrower.PickAndAssignRandomBerryTierFromCurrentCultistsInYard", new HarmonyMethod(typeof(GuardPatches), nameof(ReplayOnlyGuard)));
            NetLog.Info("guards patched: " + n);
        }

        static int Patch(Harmony h, string spec, HarmonyMethod prefix)
        {
            int dot = spec.IndexOf('.');
            Type t = AccessTools.TypeByName(spec.Substring(0, dot));
            var m = t == null ? null : AccessTools.Method(t, spec.Substring(dot + 1));
            if (m == null) { NetLog.Warn("guard target missing: " + spec); return 0; }
            h.Patch(m, prefix);
            return 1;
        }

        static bool Guest { get { return Session.IsGuest && Session.GuestInWorld && Session.Replaying == 0; } }

        static bool TriggerGuard(Collider __0)
        {
            return !Guest || !__0 || !Entities.Of(__0);
        }

        static bool CollisionGuard(Collision __0)
        {
            return !Guest || __0 == null || !Entities.Of(__0.collider);
        }

        // Walls break from a guest's own chainsaw; the break itself is forwarded as a world event.
        static bool WallGuard(Collider __0)
        {
            if (!Guest || !__0) return true;
            NetEntity ne = Entities.Of(__0);
            return !ne || ne.OwnedLocally;
        }

        static bool SelfGuard() { return !Guest; }

        static bool SimulationGuard() { return !Guest; }

        static bool ReplayOnlyGuard() { return !(Session.IsGuest && Session.GuestInWorld) || Session.Replaying > 0; }

        static bool OwnedOnlyGuard(Component __instance)
        {
            if (!Guest) return true;
            NetEntity ne = Entities.Of(__instance);
            return ne && ne.OwnedLocally;
        }
    }
}
