using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BerryCoop
{
    // Typed access to the game's private members the sync code needs.
    public static class Game
    {
        static readonly AccessTools.FieldRef<PlayerGrabber, Rigidbody> heldRb = AccessTools.FieldRefAccess<PlayerGrabber, Rigidbody>("heldRigidBody");
        static readonly AccessTools.FieldRef<PlayerGrabber, List<Rigidbody>> vacList = AccessTools.FieldRefAccess<PlayerGrabber, List<Rigidbody>>("rbsInVacuum");
        static readonly AccessTools.FieldRef<PlayerGrabber, VacuumSuckTrigger> vacZone = AccessTools.FieldRefAccess<PlayerGrabber, VacuumSuckTrigger>("vacuumSuckVolume");
        static readonly AccessTools.FieldRef<PlayerGrabber, GameObject> cam = AccessTools.FieldRefAccess<PlayerGrabber, GameObject>("playerCam");
        static readonly AccessTools.FieldRef<PlayerGrabber, LayerMask> kickMask = AccessTools.FieldRefAccess<PlayerGrabber, LayerMask>("kickRayMask");
        static readonly AccessTools.FieldRef<PlayerGrabber, float> kickLen = AccessTools.FieldRefAccess<PlayerGrabber, float>("kickRay_Length");
        static readonly AccessTools.FieldRef<PlayerGrabber, float> kickRad = AccessTools.FieldRefAccess<PlayerGrabber, float>("kickRadius");
        static readonly AccessTools.FieldRef<PlayerGrabber, GameObject> dartPrefab = AccessTools.FieldRefAccess<PlayerGrabber, GameObject>("popgun_BulletPrefab");
        static readonly AccessTools.FieldRef<PlayerGrabber, float> dartForce = AccessTools.FieldRefAccess<PlayerGrabber, float>("popgun_BulletForce");
        static readonly AccessTools.FieldRef<PlayerGrabber, Transform> dartSpawn = AccessTools.FieldRefAccess<PlayerGrabber, Transform>("popgun_BulletSpawnPoint");
        static readonly AccessTools.FieldRef<SuckUppables, bool> suckDeposited = AccessTools.FieldRefAccess<SuckUppables, bool>("hasDepositied");
        static readonly AccessTools.FieldRef<BerryGrower, int> growerTier = AccessTools.FieldRefAccess<BerryGrower, int>("currBerryTier");
        static readonly AccessTools.FieldRef<BerryGrower, float> growerGrowth = AccessTools.FieldRefAccess<BerryGrower, float>("currentGrowth");
        static readonly AccessTools.FieldRef<BerryGrower, float> growerMax = AccessTools.FieldRefAccess<BerryGrower, float>("growTimeMax");
        static readonly AccessTools.FieldRef<BerryGrower, GameObject> growerGrowingPrefab = AccessTools.FieldRefAccess<BerryGrower, GameObject>("growingBerryPrefab");

        static readonly AccessTools.FieldRef<PlayerGrabber, GameObject> vacArt = AccessTools.FieldRefAccess<PlayerGrabber, GameObject>("vacuumArt");

        public static GameObject VacuumArt { get { var g = Grabber; return g ? vacArt(g) : null; } }

        static PlayerGrabber grabber;

        public static PlayerGrabber Grabber
        {
            get
            {
                if (!grabber) grabber = Object.FindAnyObjectByType<PlayerGrabber>();
                return grabber;
            }
        }

        public static Rigidbody HeldBody { get { var g = Grabber; return g ? heldRb(g) : null; } }
        public static List<Rigidbody> Vacuum { get { var g = Grabber; return g ? vacList(g) : null; } }
        public static VacuumSuckTrigger VacuumZone { get { var g = Grabber; return g ? vacZone(g) : null; } }
        public static GameObject Camera { get { var g = Grabber; return g ? cam(g) : null; } }
        public static LayerMask KickMask { get { return kickMask(Grabber); } }
        public static float KickLength { get { return kickLen(Grabber); } }
        public static float KickRadius { get { return kickRad(Grabber); } }
        public static GameObject DartPrefab { get { return dartPrefab(Grabber); } }
        public static float DartForce { get { return dartForce(Grabber); } }
        public static Transform DartSpawn { get { return dartSpawn(Grabber); } }

        public static bool SuckDeposited(SuckUppables s) { return suckDeposited(s); }
        public static void SetSuckDeposited(SuckUppables s, bool v) { suckDeposited(s) = v; }

        public static int GrowerTier(BerryGrower g) { return growerTier(g); }
        public static float GrowerGrowth(BerryGrower g) { return growerGrowth(g); }
        public static void SetGrowerGrowth(BerryGrower g, float v) { growerGrowth(g) = v; }
        public static float GrowerMax(BerryGrower g) { return growerMax(g); }
        public static void SetGrowerMax(BerryGrower g, float v) { growerMax(g) = v; }
        public static bool GrowerHasGrowingVisual(BerryGrower g) { return growerGrowingPrefab(g) != null; }

        public static bool InVacuum(Rigidbody rb)
        {
            var v = Vacuum;
            return rb && v != null && v.Contains(rb);
        }

        public static bool InSuckZone(Rigidbody rb)
        {
            var z = VacuumZone;
            var g = Grabber;
            return rb && z && g && g.vacuumisOn && (z.rbsInSuckZone.Contains(rb) || z.rbsInSuckZone_NonBerries.Contains(rb));
        }

        public static void Invoke(object target, string method, params object[] args)
        {
            AccessTools.Method(target.GetType(), method).Invoke(target, args);
        }

        public static bool InGameScene
        {
            get { return GameManager.Singleton && SaveLoadManager.Singleton && PlayerStats.Singleton; }
        }

        public static string CleanName(string name)
        {
            int i;
            while ((i = name.IndexOf("(Clone)")) >= 0) name = name.Remove(i, 7);
            return name.Trim();
        }

        public static void ResetCaches() { grabber = null; }
    }
}
