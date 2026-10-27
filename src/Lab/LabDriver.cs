using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BerryCoop
{
    // Self-driving test runs (-berrycoop-lab host|guest -berrycoop-labdir <dir>). They never touch the player's
    // saves, drive the game through its own methods and write LAB lines plus screenshots for judging.
    public sealed class LabDriver : MonoBehaviour
    {
        public static string SaveDir;
        string dir, role;
        float started;

        public static string ReadSave(string name)
        {
            string p = Path.Combine(SaveDir, name);
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }

        public static void WriteSave(string name, string text)
        {
            Directory.CreateDirectory(SaveDir);
            File.WriteAllText(Path.Combine(SaveDir, name), text);
        }

        void Awake()
        {
            role = CommandLine.Value("-berrycoop-lab") ?? "host";
            dir = CommandLine.Value("-berrycoop-labdir") ?? Path.Combine(Plugin.Root, "lab");
            Directory.CreateDirectory(dir);
            SaveDir = Path.Combine(dir, "saves-" + role);
            started = Time.realtimeSinceStartup;
        }

        void Start() { StartCoroutine(role == "guest" ? Guest() : Host()); }

        static void Log(string s) { NetLog.Info("LAB " + s); }

        void Shot(string name)
        {
            string p = Path.Combine(dir, role + "-" + name + ".png");
            ScreenCapture.CaptureScreenshot(p);
            Log("screenshot " + p);
        }

        // Renders a frame from a temporary camera aimed at a point, independent of where the player looks.
        void AimedShot(string name, Vector3 target) { AimedShot(name, target, false); }

        // eyeLevel: camera at a player's eye height looking dead level, so the avatar's eyes should sit on the centre line.
        void AimedShot(string name, Vector3 target, bool eyeLevel)
        {
            var go = new GameObject("LabCamera");
            var cam = go.AddComponent<Camera>();
            Camera main = Camera.main;
            if (main) cam.CopyFrom(main);
            if (eyeLevel)
            {
                float eyes = Avatars.CurrentEyeHeight;
                go.transform.position = target + new Vector3(2.5f, eyes, 2.5f);
                go.transform.LookAt(target + Vector3.up * eyes);
                Log("eye-level shot at " + eyes.ToString("F2") + " m");
            }
            else
            {
                // Pick an angle with nothing between the camera and the avatar (the pergola kept blocking a fixed one).
                Vector3 look = target + Vector3.up * 1.6f;
                Vector3 best = target + new Vector3(3.5f, 2.2f, 3.5f);
                for (int i = 0; i < 12; i++)
                {
                    Vector3 from = look + Quaternion.Euler(0f, i * 30f, 0f) * new Vector3(0f, 0.8f, 3.2f);
                    bool blocked = Physics.RaycastAll(from, look - from, Vector3.Distance(from, look), ~0, QueryTriggerInteraction.Ignore)
                        .Any(h => !h.transform.root.name.StartsWith("CoopPlayer_"));
                    if (!blocked && !Physics.CheckSphere(from, 0.3f, ~0, QueryTriggerInteraction.Ignore)) { best = from; break; }
                }
                go.transform.position = best;
                go.transform.LookAt(look);
            }
            var rt = new RenderTexture(800, 450, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(800, 450, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 800, 450), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            string p = Path.Combine(dir, role + "-" + name + ".png");
            File.WriteAllBytes(p, tex.EncodeToPNG());
            cam.targetTexture = null;
            Destroy(rt); Destroy(tex); Destroy(go);
            Log("aimed screenshot " + p + " at " + target);
        }

        static IEnumerator WaitFor(Func<bool> cond, float timeout, string what)
        {
            float until = Time.realtimeSinceStartup + timeout;
            while (!cond())
            {
                if (Time.realtimeSinceStartup > until) { Log("TIMEOUT waiting for " + what); yield break; }
                yield return null;
            }
        }

        static bool InMenu { get { return SceneManager.GetActiveScene().name == "MainMenu" && MenuToGameBridger.Singleton; } }
        static bool Playing { get { return Game.InGameScene && GameManager.Singleton.gameState == GameManager.GameState.Playing; } }

        static string StateLine()
        {
            if (!Game.InGameScene) return "scene=" + SceneManager.GetActiveScene().name;
            PlayerStats s = PlayerStats.Singleton;
            int unlocked = UpgradeTreeManager.Singleton.allUpgradeTreeButtons.Count(b => b && b.isUnlocked);
            int built = UnityEngine.Object.FindObjectsByType<Buildable>(FindObjectsSortMode.None).Length;
            return "epoch=" + Session.Epoch + " state=" + GameManager.Singleton.gameState + " money=" + s.money + " held=" + s.money_Held
                 + " seeds=" + s.seeds + " orbs=" + s.starOrbs + " dirtBought=" + s.dirtPatch_NumOfPurchased + " dirtPlaced=" + s.dirtPatch_NumPlaced
                 + " unlocked=" + unlocked + " entities=" + Entities.Count + " buildables=" + built
                 + " timer=" + GameManager.Singleton.roundTimer_Curr.ToString("F1") + " rounds=" + s.totalRounds;
        }

        static float maxAvatarY = float.MinValue;

        IEnumerator AvatarHeights()
        {
            while (true)
            {
                foreach (Peer p in Session.Peers.Values)
                {
                    Vector3? at = Avatars.PositionOf(p.Id);
                    if (at.HasValue && at.Value.y > maxAvatarY) maxAvatarY = at.Value.y;
                }
                yield return null;
            }
        }

        IEnumerator Ticker()
        {
            while (true)
            {
                Log("STATE " + role + " " + StateLine());
                if (Session.IsHost)
                {
                    foreach (Peer p in Session.Peers.Values)
                    {
                        Vector3? av = Avatars.PositionOf(p.Id);
                        Log("PEER " + p.Name + " ready=" + p.Ready + " shop=" + p.InShop + " pos=" + p.LastPos + " avatar=" + (av.HasValue ? av.Value.ToString() : "none") + " maxY=" + maxAvatarY.ToString("F2"));
                    }
                    foreach (NetEntity ne in Entities.All)
                        if (ne && ne.Owner != 0) Log("OWNED #" + ne.Id + " " + ne.Prefab + " by " + ne.Owner + " at " + ne.transform.position);
                }
                yield return new WaitForSecondsRealtime(2f);
            }
        }

        void Update()
        {
            if (PlayerStats.Singleton && Session.Role != Role.Guest) PlayerStats.Singleton.disableTutorials = true;
        }

        IEnumerator Host()
        {
            yield return WaitFor(() => InMenu, 90f, "main menu");
            yield return new WaitForSecondsRealtime(2f);
            bool online = CommandLine.Has("-berrycoop-online");
            string codeFile = Path.Combine(dir, "join.txt");
            if (File.Exists(codeFile)) File.Delete(codeFile);
            Session.StartHost(online);
            if (online)
            {
                yield return WaitFor(() => Session.RoomCode.Length == 5 || Session.Role == Role.None, 30f, "room code");
                File.WriteAllText(codeFile, Session.RoomCode);
                Log("room code " + Session.RoomCode);
            }
            else File.WriteAllText(codeFile, "127.0.0.1:" + Plugin.CfgPort.Value);
            StartCoroutine(Ticker());

            MenuToGameBridger.Singleton.loadDataOnNextGameSceneLoad = false;
            MenuToGameBridger.Singleton.activeSaveGameSlot = 0;
            SceneManager.LoadScene("Game");
            yield return WaitFor(() => Playing, 90f, "host playing");
            PlayerStats.Singleton.IncreaseMoney_Banked(5000);
            PlayerStats.Singleton.IncreaseStarOrbs(50);
            PlayerStats.Singleton.seeds += 3;
            PlayerStats.Singleton.dirtPatch_NumOfPurchased += 2;
            PlayerStats.Singleton.vacuum_Unlocked = true;
            StartCoroutine(AvatarHeights());
            Log("host gave itself $5000, 50 orbs, 3 seeds, 2 dirt patches");
            Shot("day1-start");

            yield return WaitFor(() => Session.Peers.Values.Any(p => p.Ready), 180f, "guest ready");
            Log("guest is in the world");
            yield return new WaitForSecondsRealtime(1f);
            foreach (TurtleEatAoe t in FindObjectsByType<TurtleEatAoe>(FindObjectsSortMode.None)) t.SpawnACertainValueOfCoins(60);
            Log("host spawned coins for the guest to collect");
            Vector3? guestFeet = Avatars.PositionOf(Session.Peers.Values.First(p => p.Ready).Id);
            if (guestFeet.HasValue && AnomalousMaterial_Manager.Singleton)
            {
                AnomalousMaterial_Manager.Singleton.SpawnAnomalousMaterial(StarOrbsToSpawnWhenDeposited.One, false, guestFeet.Value + Vector3.up * 2.5f + Vector3.forward * 2f, 0f, 0f, false);
                Log("host spawned a star orb next to the guest");
            }
            yield return new WaitForSecondsRealtime(19f);
            Shot("day1-with-guest");
            if (Player.Singleton) AudioManager.Singleton.PlayUpgradeCultistSFX(Player.Singleton.transform.position, false);
            Log("host played the star wand upgrade effect");
            Vector3? guestAt = Avatars.PositionOf(Session.Peers.Values.First(p => p.Ready).Id);
            if (guestAt.HasValue) { AimedShot("guest-avatar", guestAt.Value); AimedShot("guest-avatar-eyes", guestAt.Value, true); }
            int guestId = Session.Peers.Values.First(p => p.Ready).Id;
            StandInFrontOf(guestId);
            yield return new WaitForSecondsRealtime(4f);
            Vector3? vacAt = Avatars.PositionOf(guestId);
            if (vacAt.HasValue) { AimedShot("guest-avatar-vacuum", vacAt.Value); Log("guest avatar vacuum shown=" + Avatars.VacuumShown(guestId)); }
            yield return WaitFor(() => GameManager.Singleton.gameState == GameManager.GameState.RoundOverShop, 40f, "the guest falling in");
            if (GameManager.Singleton.gameState == GameManager.GameState.RoundOverShop) Log("day ended by the guest: fellInHole=" + PlayerStats.Singleton.fellInHoleThisRound + " " + StateLine());
            else { Log("host ends the day"); GameManager.Singleton.ForceEndRound(true); }
            yield return new WaitForSecondsRealtime(18f);
            Log("host starts the next day");
            GameManager.Singleton.InShop_CloseShopAndStartNextRound();
            yield return WaitFor(() => Playing && Session.Epoch >= 3, 90f, "day 2");
            yield return WaitFor(() => Session.Peers.Values.Any(p => p.Ready), 90f, "guest back for day 2");
            Log("day 2 with the guest");
            yield return new WaitForSecondsRealtime(10f);
            Shot("day2");
            Log("DONE host");
        }

        IEnumerator Guest()
        {
            yield return WaitFor(() => InMenu, 90f, "main menu");
            string codeFile = Path.Combine(dir, "join.txt");
            yield return WaitFor(() => File.Exists(codeFile), 120f, "join file");
            yield return new WaitForSecondsRealtime(1f);
            string target = File.ReadAllText(codeFile).Trim();
            Log("joining " + target);
            if (target.Contains(":")) Session.JoinDirect(target); else Session.JoinCode(target);
            StartCoroutine(Ticker());

            yield return WaitFor(() => Session.GuestLive && Playing, 180f, "guest playing");
            yield return new WaitForSecondsRealtime(3f);
            Log("guest sees: " + StateLine());
            Shot("day1-joined");

            yield return WaitFor(() => Entities.All.Any(e => e && e.Suck && e.Id != 0 && e.gameObject.activeInHierarchy), 10f, "a coin");
            NetEntity coin = Entities.All.Where(e => e && e.Suck && e.Id != 0 && e.gameObject.activeInHierarchy).OrderBy(e => Dist(e)).FirstOrDefault();
            if (coin)
            {
                int held = PlayerStats.Singleton.money_Held;
                Log("taking " + coin.Prefab + " #" + coin.Id);
                coin.Suck.pickedUpManually = true;
                coin.Suck.OnPlayerPickedUsed();
                yield return new WaitForSecondsRealtime(2f);
                Log("pocket " + held + " -> " + PlayerStats.Singleton.money_Held + ", seeds " + PlayerStats.Singleton.seeds + ", coin alive=" + (bool)coin);
            }
            else Log("no coin or seed to take");

            yield return WaitFor(() => Entities.All.Any(e => e && e.Pick && e.Pick.GetItemIdentity() == ItemIdentity.AnomalousMaterial && e.CollidersOn), 12f, "a grabbable star orb");
            NetEntity orb = Entities.All.FirstOrDefault(e => e && e.Pick && e.Pick.GetItemIdentity() == ItemIdentity.AnomalousMaterial && e.CollidersOn);
            if (orb)
            {
                Traverse.Create(Game.Grabber).Method("AttemptPickup", orb.gameObject).GetValue();
                yield return new WaitForSecondsRealtime(1f);
                Log("star orb #" + orb.Id + " colliders=" + orb.CollidersOn + " holding=" + (Game.HeldBody == orb.Rb) + " owned=" + orb.OwnedLocally);
                Traverse.Create(Game.Grabber).Method("DropHeldItem", false, orb.Pick).GetValue();
                yield return new WaitForSecondsRealtime(1f);
            }
            else Log("no grabbable star orb (colliders never came on)");

            NetEntity berry = Entities.All.Where(e => e && e.Pick && e.Pick.canPickUp && e.gameObject.activeInHierarchy && e.Rb && e.GetComponent<Berry>())
                .OrderBy(e => Dist(e)).FirstOrDefault()
                ?? Entities.All.Where(e => e && e.Pick && e.Pick.canPickUp && e.gameObject.activeInHierarchy && e.Rb).OrderBy(e => Dist(e)).FirstOrDefault();
            if (berry)
            {
                Vector3 from = berry.transform.position;
                Log("grabbing " + berry.Prefab + " #" + berry.Id + " at " + from);
                Traverse.Create(Game.Grabber).Method("AttemptPickup", berry.gameObject).GetValue();
                yield return new WaitForSecondsRealtime(1.5f);
                Log("holding=" + (Game.HeldBody == berry.Rb) + " owned=" + berry.OwnedLocally + " at " + berry.transform.position);
                Traverse.Create(Game.Grabber).Method("DropHeldItem", true, berry.Pick).GetValue();
                yield return new WaitForSecondsRealtime(2.5f);
                Log("thrown " + berry.Prefab + " #" + berry.Id + " now at " + (berry ? berry.transform.position.ToString() : "gone") + " owned=" + (berry && berry.OwnedLocally));
            }
            else Log("no berry to grab");

            yield return new WaitForSecondsRealtime(1f);
            GameManager gm = GameManager.Singleton;
            Buildable patch = FindObjectsByType<Buildable>(FindObjectsSortMode.None).FirstOrDefault(b => b.buildableIdentity == BuildableIdentity.DirtPatch);
            if (patch)
            {
                Vector3 spotPos = patch.transform.root.position;
                Log("selling the dirt patch at " + spotPos + "; " + StateLine());
                gm.OnBuildableDestroyed_RunAdditionalLogic(patch);
                gm.SetGridTileToAllowBuildingOn(spotPos);
                Destroy(patch.transform.root.gameObject);
                yield return new WaitForSecondsRealtime(2f);
                Log("after selling: " + StateLine());
                BuildSpot spot = gm.buildSpotsDict.Values.FirstOrDefault(s => s && !s.isBuiltOn);
                if (spot)
                {
                    gm.EnterBuildMode(gm.prefabBank.buildable_DirtPatch, true, BuildModeRotationMode.SnapRotation_90, true, BuildableIdentity.DirtPatch);
                    Vector3 at = spot.transform.position + Vector3.up * gm.buildMode_YOffset;
                    Log("placing a dirt patch at " + at);
                    gm.BuildCurrentBuildable(at, Quaternion.identity, true);
                    yield return new WaitForSecondsRealtime(2f);
                    Log("after placing: " + StateLine());
                }
                else Log("no free build spot after selling");
            }
            else Log("no dirt patch to sell");

            Rigidbody me = Player.Singleton.GetComponent<Rigidbody>();
            if (me) { me.linearVelocity = Vector3.up * 9f; Log("guest jumps from y=" + Player.Singleton.transform.position.y.ToString("F2")); }
            yield return new WaitForSecondsRealtime(2f);

            StandInFrontOf(0);
            Log("guest stands in front of the host at " + Player.Singleton.transform.position);
            NetEntity vacBerry = Entities.All.Where(e => e && e.Rb && e.GetComponent<Berry>() && !e.OwnedLocally && e.gameObject.activeInHierarchy).OrderBy(e => Dist(e)).FirstOrDefault();
            if (vacBerry)
            {
                Entities.Claim(vacBerry, Net.ClaimReason.Vacuum);
                Traverse.Create(Game.Grabber).Method("AddBerryToVacuum", vacBerry.Rb).GetValue();
                Log("berry #" + vacBerry.Id + " in the guest's vacuum; vacuum model shown=" + (Game.VacuumArt && Game.VacuumArt.activeInHierarchy));
            }
            yield return new WaitForSecondsRealtime(9f);
            Shot("day1-host-avatar");
            Vector3? hostAt = Avatars.PositionOf(0);
            if (hostAt.HasValue) { AimedShot("host-avatar", hostAt.Value); AimedShot("host-avatar-eyes", hostAt.Value, true); }

            yield return new WaitForSecondsRealtime(3f);
            Vector3 hole = GameManager.Singleton.GetYardObject().transform.position + Vector3.down * 12f;
            Log("guest drops into the hole; pocket " + PlayerStats.Singleton.money_Held);
            if (me) { me.linearVelocity = Vector3.zero; me.position = hole; }
            Player.Singleton.transform.position = hole;
            yield return WaitFor(() => Game.InGameScene && GameManager.Singleton.gameState == GameManager.GameState.RoundOverShop, 90f, "shop");
            Log("guest followed the host into the shop: " + StateLine());
            yield return new WaitForSecondsRealtime(2f);
            Shot("shop");
            UpgradeTreeButton pick = UpgradeTreeManager.Singleton.allUpgradeTreeButtons
                .Where(b => b && !b.isUnlocked && !b.DISABLED_DO_NOT_INCLUDE_IN_TREE && b.calculatedPrice > 0 && b.preReqUpgrades.All(p => p && p.isUnlocked)
                         && b.upgradeCostType == UpgradeTreeCostType.Money && b.upgradeIdentity != UpgradeTreeIdentity.NextRoundButton
                         && b.upgradeIdentity != UpgradeTreeIdentity.Rewind && b.CanAfford())
                .OrderBy(b => b.calculatedPrice).FirstOrDefault();
            if (pick)
            {
                int index = UpgradeTreeManager.Singleton.allUpgradeTreeButtons.IndexOf(pick);
                Log("buying upgrade #" + index + " " + pick.upgradeIdentity + " for $" + pick.calculatedPrice + "; money before " + PlayerStats.Singleton.money);
                Session.IntentContext++;
                try { UpgradeTreeManager.Singleton.UnlockUpgrade(pick); } finally { Session.IntentContext--; }
                yield return new WaitForSecondsRealtime(2f);
                Log("upgrade #" + index + " unlocked=" + pick.isUnlocked + " money after " + PlayerStats.Singleton.money);
            }
            else Log("no affordable upgrade");
            int bought = PlayerStats.Singleton.dirtPatch_NumOfPurchased;
            ShopPanelHelper.Singleton.BuyButton_DirtPatch();
            yield return new WaitForSecondsRealtime(2f);
            Log("dirt patches bought " + bought + " -> " + PlayerStats.Singleton.dirtPatch_NumOfPurchased + ", money " + PlayerStats.Singleton.money);
            GameManager.Singleton.InShop_CloseShopAndStartNextRound();
            int epoch = Session.Epoch;
            yield return WaitFor(() => Session.Epoch > epoch && Session.GuestLive && Playing, 120f, "day 2");
            yield return new WaitForSecondsRealtime(3f);
            Log("day 2: " + StateLine());
            Shot("day2");
            Log("DONE guest");
        }

        // Park our player 4 m in front of another player's camera so screenshots show the avatars.
        static void StandInFrontOf(int id)
        {
            Vector3? at = Avatars.PositionOf(id);
            if (!at.HasValue || !Player.Singleton) { Log("no avatar for #" + id); return; }
            Vector3 pos = at.Value + Quaternion.Euler(0f, Avatars.YawOf(id), 0f) * Vector3.forward * 4f;
            Rigidbody body = Player.Singleton.GetComponent<Rigidbody>();
            if (body) { body.linearVelocity = Vector3.zero; body.position = pos; }
            Player.Singleton.transform.position = pos;
        }

        static float Dist(NetEntity e)
        {
            return Player.Singleton ? (e.transform.position - Player.Singleton.transform.position).sqrMagnitude : 0f;
        }
    }
}
