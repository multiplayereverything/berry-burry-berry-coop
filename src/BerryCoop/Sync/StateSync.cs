using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BerryCoop.Net;
using UnityEngine;

namespace BerryCoop
{
    // Host -> guest mirror of the shared game state: the wallet, every stat, upgrade levels, timers, the hole,
    // puzzles, radio, plant beds and berry growth. Guests overwrite their copies; their own changes go through commands.
    public static class StateSync
    {
        sealed class Block
        {
            public string Name;
            public Func<object> Target;
            public FieldInfo[] Fields;
            public object[] Last;
            public bool[] Sent;
        }

        static readonly List<Block> blocks = new List<Block>();
        static bool built;

        // Per-player values that must never be overwritten by the host. The coin pocket is shared like the wallet.
        static readonly HashSet<string> statsExcluded = new HashSet<string>
        {
            "fellInHoleThisRound", "debugPuzzleSolveIndex",
        };

        static readonly string[] gameManagerFields =
        {
            "goldRushIsActive", "goldRush_Time_Current", "goldRush_Cooldown_Curr", "canUseGoldRush", "goldRush_growthSpeedBoost_Curr",
            "bigHoleIsActive", "bigHole_Time_Current", "bigHole_Cooldown_Curr", "canUseBigHole", "bigHole_SizeBoost_Curr",
            "currentYardAndHoleScale", "desiredYardAndHoleScale", "tempHoleSizeJuice_Curr", "holeLevel", "holeGrowthPoints_Curr",
            "holeLevel_CurrentProgress", "playerMovingHole", "desiredHoleDestination", "holeMoveJuice_Curr", "roundTimer_Curr",
            "shouldTickDownRoundTime", "numOfGnomesRemaining", "numOfRemainingMilestones", "holePrestigeJuice_Curr",
            "holePrestigeCurrPercent", "sugarRush_AdditionalGrowthRate_Curr", "sugarRush_Duration_Curr", "juiced_Amount_Curr",
            "juiced_GrowthMultiplier_Curr", "isStarDoorOpen", "nighttime_HoleMoveSpeed_Curr", "nighttime_HoleGrowth_Curr",
            "allPlantBedsAreHalted", "milestoneObjects_MasterList_Bools", "walls_MasterList_Bools",
        };

        static readonly HashSet<string> upgradeTreeExcluded = new HashSet<string> { "disableAllInteractionsWithShopButtons" };

        static readonly Type[] publicStateTypes =
        {
            typeof(ShopAndUpgradesManager), typeof(PuzzleManager), typeof(Puzzle_GoldenIdol), typeof(Puzzle_Belladonna),
            typeof(Puzzle_Radio), typeof(Puzzle_RedString), typeof(Puzzle_Smoothie), typeof(Puzzle_Taxi), typeof(CassettesManager),
            typeof(PcManager), typeof(AnomalousMaterial_Manager), typeof(NebulaBerryManager), typeof(PinataManager),
            typeof(SlotMachine_Manager), typeof(VoidBoxSpawnManager), typeof(FlipperPuzzleManager), typeof(Radio),
            typeof(BerryPicker), typeof(StarOrbGenerator),
        };

        static float lastAux;
        static readonly List<Transform> mirroredTransforms = new List<Transform>();
        static readonly List<Vector3> transformTargets = new List<Vector3>();
        static readonly List<Quaternion> transformRotTargets = new List<Quaternion>();

        static void Build()
        {
            if (built) return;
            built = true;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags pub = BindingFlags.Instance | BindingFlags.Public;

            Add("PlayerStats", () => PlayerStats.Singleton,
                typeof(PlayerStats).GetFields(inst).Where(f => !statsExcluded.Contains(f.Name)));
            Add("GameManager", () => GameManager.Singleton,
                gameManagerFields.Select(n => typeof(GameManager).GetField(n, inst)).Where(f => f != null));
            Add("UpgradeTreeManager", () => UpgradeTreeManager.Singleton,
                typeof(UpgradeTreeManager).GetFields(pub).Where(f => !upgradeTreeExcluded.Contains(f.Name)));
            foreach (Type t in publicStateTypes)
            {
                Type type = t;
                Add(type.Name, () => Find(type), type.GetFields(pub));
            }
        }

        static void Add(string name, Func<object> target, IEnumerable<FieldInfo> fields)
        {
            FieldInfo[] fs = fields.Where(f => !f.IsLiteral && !f.IsInitOnly && Codec.Supported(f.FieldType)).ToArray();
            blocks.Add(new Block { Name = name, Target = target, Fields = fs, Last = new object[fs.Length], Sent = new bool[fs.Length] });
        }

        static readonly Dictionary<Type, UnityEngine.Object> found = new Dictionary<Type, UnityEngine.Object>();

        static object Find(Type t)
        {
            UnityEngine.Object o;
            if (found.TryGetValue(t, out o) && o) return o;
            o = UnityEngine.Object.FindAnyObjectByType(t, FindObjectsInactive.Include);
            found[t] = o;
            return o ? o : null;
        }

        public static void OnSceneLoaded()
        {
            Build();
            found.Clear();
            foreach (Block b in blocks) { Array.Clear(b.Last, 0, b.Last.Length); Array.Clear(b.Sent, 0, b.Sent.Length); }
            lastUnlocks = null;
            lastBeds.Clear();
            mirroredTransforms.Clear(); transformTargets.Clear(); transformRotTargets.Clear();
            if (!Game.InGameScene) return;
            GameObject yard = GameManager.Singleton.GetYardObject();
            if (yard) mirroredTransforms.Add(yard.transform);
            var turtles = UnityEngine.Object.FindObjectsByType<TurtleEatAoe>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(t => NetObjects.KeyOf(t) ?? "");
            var turtleParent = HarmonyLib.AccessTools.Field(typeof(TurtleEatAoe), "turtleParent");
            foreach (var t in turtles)
            {
                var go = turtleParent.GetValue(t) as GameObject;
                if (go) mirroredTransforms.Add(go.transform);
            }
            foreach (Transform t in mirroredTransforms) { transformTargets.Add(t.position); transformRotTargets.Add(t.rotation); }
        }

        // 10 Hz on the host. full=true sends every field (a guest that just loaded).
        public static Packet BuildState(bool full)
        {
            var p = new Packet(Msg.State).Int(Session.Epoch);
            var dirty = new List<KeyValuePair<int, List<int>>>();
            for (int bi = 0; bi < blocks.Count; bi++)
            {
                Block b = blocks[bi];
                object target = b.Target();
                if (target == null) continue;
                List<int> changed = null;
                for (int fi = 0; fi < b.Fields.Length; fi++)
                {
                    object v = b.Fields[fi].GetValue(target);
                    if (!full && b.Sent[fi] && Codec.Same(v, b.Last[fi])) continue;
                    if (!full) { b.Last[fi] = Codec.Copy(v); b.Sent[fi] = true; }
                    (changed ?? (changed = new List<int>())).Add(fi);
                }
                if (changed != null) dirty.Add(new KeyValuePair<int, List<int>>(bi, changed));
            }
            p.Int(dirty.Count);
            foreach (var d in dirty)
            {
                Block b = blocks[d.Key];
                object target = b.Target();
                p.Int(d.Key).Int(d.Value.Count);
                foreach (int fi in d.Value)
                {
                    p.Int(fi);
                    Codec.Write(p, b.Fields[fi].FieldType, b.Fields[fi].GetValue(target));
                }
            }
            p.Int(mirroredTransforms.Count);
            foreach (Transform t in mirroredTransforms)
            {
                p.Bool(t);
                if (t) p.Vec(t.position).Quat(t.rotation).Vec(t.localScale);
            }
            return p;
        }

        public static void GuestApplyState(Reader r)
        {
            if (r.Int() != Session.Epoch || !Session.GuestLive) return;
            if (!Session.GuestSynced && !uncovering && Entities.MapApplied) { uncovering = true; Plugin.Instance.StartCoroutine(UncoverSoon()); }
            int nb = r.Int();
            for (int i = 0; i < nb; i++)
            {
                int bi = r.Int(), nf = r.Int();
                Block b = blocks[bi];
                object target = b.Target();
                for (int j = 0; j < nf; j++)
                {
                    FieldInfo f = b.Fields[r.Int()];
                    object v = Codec.Read(r, f.FieldType);
                    if (target == null) continue;
                    try { f.SetValue(target, Codec.Assign(f.GetValue(target), v)); }
                    catch (Exception e) { NetLog.Warn("state " + b.Name + "." + f.Name + ": " + e.Message); }
                }
            }
            int nt = r.Int();
            for (int i = 0; i < nt; i++)
            {
                if (!r.Bool()) continue;
                Vector3 pos = r.Vec(); Quaternion rot = r.Quat(); Vector3 scale = r.Vec();
                if (i >= mirroredTransforms.Count || !mirroredTransforms[i]) continue;
                transformTargets[i] = pos; transformRotTargets[i] = rot;
                mirroredTransforms[i].localScale = scale;
            }
        }

        static bool uncovering;

        static System.Collections.IEnumerator UncoverSoon()
        {
            yield return new WaitForSecondsRealtime(0.4f);
            Session.GuestSynced = true;
            uncovering = false;
        }

        public static void GuestLateUpdate()
        {
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);
            for (int i = 0; i < mirroredTransforms.Count; i++)
            {
                Transform t = mirroredTransforms[i];
                if (!t) continue;
                if ((t.position - transformTargets[i]).sqrMagnitude > 100f) t.position = transformTargets[i];
                else t.position = Vector3.Lerp(t.position, transformTargets[i], k);
                t.rotation = Quaternion.Slerp(t.rotation, transformRotTargets[i], k);
            }
        }

        // Upgrade unlocks, plant beds and berry growers: 2 Hz, with visual side effects applied on the guest.
        static bool[] lastUnlocks;
        static readonly Dictionary<string, string> lastBeds = new Dictionary<string, string>();

        public static bool AuxDue()
        {
            if (Time.unscaledTime - lastAux < 0.5f) return false;
            lastAux = Time.unscaledTime;
            return true;
        }

        public static Packet BuildAux(bool full)
        {
            var p = new Packet(Msg.Growers).Int(Session.Epoch);
            var buttons = UpgradeTreeManager.Singleton.allUpgradeTreeButtons;
            bool[] unlocks = buttons.Select(b => b && b.isUnlocked).ToArray();
            bool unlocksChanged = full || lastUnlocks == null || !unlocks.SequenceEqual(lastUnlocks);
            p.Bool(unlocksChanged);
            if (unlocksChanged)
            {
                if (!full) lastUnlocks = unlocks;
                p.Int(unlocks.Length);
                foreach (bool u in unlocks) p.Bool(u);
            }

            var beds = new List<KeyValuePair<string, PlantBed>>();
            foreach (PlantBed bed in GameManager.Singleton.allPlantBeds)
            {
                string key = NetObjects.KeyOf(bed);
                if (key == null) continue;
                string sig = bed.currentBerryTier + "/" + bed.plantIdentity + "/" + bed.GetIsProductionHalted() + "/" + bed.GetXP() + "/" + bed.GetBerriesProduced();
                string last;
                if (!full && lastBeds.TryGetValue(key, out last) && last == sig) continue;
                if (!full) lastBeds[key] = sig;
                beds.Add(new KeyValuePair<string, PlantBed>(key, bed));
            }
            p.Int(beds.Count);
            foreach (var kv in beds)
            {
                PlantBed bed = kv.Value;
                p.Str(kv.Key).Int(bed.currentBerryTier).Int((int)bed.plantIdentity).Bool(bed.GetIsProductionHalted())
                 .Int(bed.GetXP()).Int(bed.GetBerriesProduced());
            }

            var growers = UnityEngine.Object.FindObjectsByType<BerryGrower>(FindObjectsSortMode.None);
            var list = new List<KeyValuePair<string, BerryGrower>>();
            foreach (BerryGrower g in growers)
            {
                string key = NetObjects.KeyOf(g);
                if (key != null) list.Add(new KeyValuePair<string, BerryGrower>(key, g));
            }
            p.Int(list.Count);
            foreach (var kv in list)
            {
                BerryGrower g = kv.Value;
                p.Str(kv.Key).Int(Game.GrowerTier(g)).Float(Game.GrowerGrowth(g)).Float(Game.GrowerMax(g)).Float(g.delayBeforeGrowing).Bool(g.isGrowingHalted);
            }
            return p;
        }

        public static void GuestApplyAux(Reader r)
        {
            if (r.Int() != Session.Epoch || !Session.GuestLive) return;
            if (r.Bool())
            {
                int n = r.Int();
                var buttons = UpgradeTreeManager.Singleton.allUpgradeTreeButtons;
                bool any = false;
                for (int i = 0; i < n; i++)
                {
                    bool u = r.Bool();
                    if (i >= buttons.Count || !buttons[i] || buttons[i].isUnlocked == u) continue;
                    any = true;
                    if (u) Commands.GuestApplyUnlock(buttons[i]);
                    else buttons[i].isUnlocked = false;
                }
                if (any) UpgradeTreeManager.Singleton.UpdateAllUpgradeTreeTiles();
            }

            int nb = r.Int();
            for (int i = 0; i < nb; i++)
            {
                string key = r.Str(); int tier = r.Int(); var ident = (PlantIdentity)r.Int(); bool halted = r.Bool(); int xp = r.Int(), produced = r.Int();
                var bed = NetObjects.Resolve(key, typeof(PlantBed)) as PlantBed;
                if (!bed) continue;
                Session.Replaying++;
                try
                {
                    if (bed.plantIdentity != ident)
                    {
                        string up = ident.ToString();
                        if (up.Contains("Tree")) bed.UpgradeToTree();
                        else if (up.Contains("Bush")) bed.UpgradeToBush();
                        else bed.UpgradeToFlower();
                    }
                    if (bed.currentBerryTier != tier && tier >= 0 && tier < GameManager.Singleton.prefabBank.berryProfiles.Count)
                    {
                        bed.currentBerryTier = tier;
                        bed.ChangeBerryProfile(GameManager.Singleton.prefabBank.berryProfiles[tier]);
                    }
                    if (bed.GetIsProductionHalted() != halted)
                    {
                        if (halted) bed.HaltBerryProduction(); else bed.EnableBerryProduction();
                    }
                    bed.SetXP(xp);
                    bed.SetBerriesProduced(produced);
                }
                catch (Exception e) { NetLog.Warn("plant bed " + key + ": " + e.Message); }
                finally { Session.Replaying--; }
            }

            int ng = r.Int();
            var profiles = GameManager.Singleton.prefabBank.berryProfiles;
            for (int i = 0; i < ng; i++)
            {
                string key = r.Str(); int tier = r.Int(); float growth = r.Float(), max = r.Float(), delay = r.Float(); bool halted = r.Bool();
                var g = NetObjects.Resolve(key, typeof(BerryGrower)) as BerryGrower;
                if (!g) continue;
                if (Game.GrowerTier(g) != tier && tier >= 0 && tier < profiles.Count)
                {
                    try { g.SelectNewBerryToGrow(profiles[tier]); } catch (Exception e) { NetLog.Warn("grower " + key + ": " + e.Message); }
                }
                Game.SetGrowerMax(g, max);
                Game.SetGrowerGrowth(g, growth);
                g.delayBeforeGrowing = delay;
                g.isGrowingHalted = halted;
            }
        }
    }
}
