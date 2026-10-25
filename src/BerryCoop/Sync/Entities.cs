using System.Collections;
using System.Collections.Generic;
using BerryCoop.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BerryCoop
{
    // Marks one synced physics object (berry, coin, seed, star orb, tool, prop, cultist, pinata).
    public sealed class NetEntity : MonoBehaviour
    {
        public int Id;
        public bool IsStatic;
        public string Key;
        public string Prefab;
        public int Owner;
        public Rigidbody Rb;
        public PickUppable Pick;
        public SuckUppables Suck;

        public Vector3 SentPos;
        public Quaternion SentRot;
        public bool SentActive;
        public bool SentColliders;
        public bool HasSent;

        public Vector3 TargetPos;
        public Quaternion TargetRot;
        public bool HasTarget;
        public bool OwnedLocally;
        public float CoastUntil;
        public float ReleaseAt;
        public ReleaseReason PendingRelease;
        public float IdleSince;
        public bool TakeRequested;

        public Collider[] Colliders;

        public bool CollidersOn
        {
            get
            {
                foreach (Collider c in Colliders) if (c && !c.isTrigger) return c.enabled;
                return true;
            }
        }

        public void SetColliders(bool on)
        {
            foreach (Collider c in Colliders) if (c) c.enabled = on;
        }

        public void Bind()
        {
            Colliders = GetComponentsInChildren<Collider>(true);
            Rb = GetComponent<Rigidbody>();
            Pick = GetComponent<PickUppable>();
            Suck = GetComponent<SuckUppables>();
        }

        void OnDestroy() { Entities.OnDestroyed(this); }
    }

    public static class Entities
    {
        public static readonly Dictionary<int, NetEntity> ById = new Dictionary<int, NetEntity>();
        static readonly Dictionary<string, NetEntity> byKey = new Dictionary<string, NetEntity>();
        public static readonly List<NetEntity> All = new List<NetEntity>();
        static readonly List<NetEntity> spawnQueue = new List<NetEntity>();
        static readonly List<int> despawnQueue = new List<int>();
        static Dictionary<string, GameObject> prefabs;
        static readonly HashSet<string> missingWarned = new HashSet<string>();
        static int nextId = 1;
        static int snapTick;
        static float lastOwnSend;

        public static bool IsEntityRoot(GameObject go)
        {
            return go.GetComponent<PickUppable>() || go.GetComponent<SuckUppables>() || go.GetComponent<BerryCultist_AI>() || go.GetComponent<Pinata>();
        }

        public static NetEntity Of(Component c) { return c ? c.GetComponentInParent<NetEntity>(true) : null; }
        public static NetEntity Of(GameObject g) { return g ? g.GetComponentInParent<NetEntity>(true) : null; }

        public static NetEntity Get(int id)
        {
            NetEntity ne;
            return ById.TryGetValue(id, out ne) && ne ? ne : null;
        }

        public static void Reset()
        {
            ById.Clear(); byKey.Clear(); All.Clear(); spawnQueue.Clear(); despawnQueue.Clear();
            prefabs = null;
            nextId = 1;
            MapApplied = false;
        }

        // Scene objects exist on every machine, so both sides key them by hierarchy position.
        public static void RegisterStatics(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++) Walk(roots[i].transform, "R" + i + ":" + roots[i].name);
            NetLog.Info("registered " + byKey.Count + " scene entities");
        }

        static void Walk(Transform t, string path)
        {
            if (IsEntityRoot(t.gameObject))
            {
                var ne = t.gameObject.GetComponent<NetEntity>() ?? t.gameObject.AddComponent<NetEntity>();
                ne.IsStatic = true;
                ne.Key = path;
                ne.Prefab = Game.CleanName(t.name);
                ne.Bind();
                if (Session.IsHost) { ne.Id = nextId++; ById[ne.Id] = ne; }
                byKey[path] = ne;
                All.Add(ne);
                return;
            }
            for (int i = 0; i < t.childCount; i++)
            {
                Transform c = t.GetChild(i);
                Walk(c, path + "/" + i + ":" + c.name);
            }
        }

        // Hosting can start mid-day, and objects spawned while the save loads come before the world is live.
        public static void HostAdopt()
        {
            foreach (var ne in All) if (ne && ne.Id == 0) { ne.Id = nextId++; ById[ne.Id] = ne; }
            int adopted = 0;
            foreach (var t in new[] { typeof(PickUppable), typeof(SuckUppables), typeof(BerryCultist_AI), typeof(Pinata) })
                foreach (Object o in Object.FindObjectsByType(t, FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    var c = (Component)o;
                    if (Of(c)) continue;
                    HostTrack(c.gameObject);
                    adopted++;
                }
            NetLog.Info("host adopted " + adopted + " live objects");
        }

        // Called from the Start of every entity type on the host.
        public static void HostTrack(GameObject go)
        {
            if (!Session.IsHost || !Session.WorldLive) return;
            if (Of(go)) return;
            var ne = go.AddComponent<NetEntity>();
            ne.Id = nextId++;
            ne.Prefab = Game.CleanName(go.name);
            ne.Bind();
            ById[ne.Id] = ne;
            All.Add(ne);
            spawnQueue.Add(ne);
        }

        public static void OnDestroyed(NetEntity ne)
        {
            All.Remove(ne);
            if (ne.Id != 0) ById.Remove(ne.Id);
            if (ne.Key != null) byKey.Remove(ne.Key);
            if (Session.IsHost && ne.Id != 0 && ne.gameObject.scene.isLoaded && Session.WorldLive) despawnQueue.Add(ne.Id);
        }

        // Host -> a guest that just finished loading: everything it needs to match the world.
        public static void SendFull(Peer peer)
        {
            var map = new Packet(Msg.StaticMap).Int(Session.Epoch);
            var statics = new List<NetEntity>();
            foreach (var ne in All) if (ne && ne.IsStatic) statics.Add(ne);
            map.Int(statics.Count);
            foreach (var ne in statics) map.Int(ne.Id).Str(ne.Key);
            peer.Conn.Send(map);

            var dyn = new List<NetEntity>();
            foreach (var ne in All) if (ne && !ne.IsStatic && ne.Id != 0) dyn.Add(ne);
            for (int i = 0; i < dyn.Count; i += 64)
                peer.Conn.Send(SpawnPacket(dyn.GetRange(i, Mathf.Min(64, dyn.Count - i))));

            peer.Conn.Send(SnapPacket(All, true));
            foreach (var ne in All)
                if (ne && ne.Owner != 0) peer.Conn.Send(new Packet(Msg.Owner).Int(Session.Epoch).Int(ne.Id).Int(ne.Owner));
        }

        static Packet SpawnPacket(List<NetEntity> list)
        {
            var p = new Packet(Msg.Spawn).Int(Session.Epoch).Int(list.Count);
            foreach (var ne in list)
            {
                Transform t = ne.transform;
                p.Int(ne.Id).Str(ne.Prefab).Vec(t.position).Quat(t.rotation).Vec(t.localScale).Bool(ne.gameObject.activeInHierarchy);
                WriteExtra(p, ne.gameObject);
            }
            return p;
        }

        static void WriteExtra(Packet p, GameObject go)
        {
            var b = go.GetComponent<Berry>();
            p.Bool(b);
            if (b) p.Int(b.berryTier).Bool(b.isGoldenBerry).Bool(b.isNebulaBerry).Int(b.goldenBerryCoinValue);
            var c = go.GetComponent<Coin>();
            p.Bool(c);
            if (c) p.Int(c.coinTier);
            var cu = go.GetComponent<BerryCultist_AI>();
            p.Bool(cu);
            if (cu) p.Str(cu.cultistsName);
            var pk = go.GetComponent<PickUppable>();
            p.Bool(pk);
            if (pk) p.Int(pk.coinValue).Int((int)pk.GetNumOfOrbsToSpawnAtDeposited()).Bool(pk.isBonusVoidBox)
                     .Int(pk.puzzleIdentity_Index).Int(pk.disk_Index).Int(pk.cassetteIndex).Float(pk.holeJuiceValue);
        }

        static void ReadExtra(Reader r, GameObject go)
        {
            if (r.Bool())
            {
                int tier = r.Int(); bool golden = r.Bool(), nebula = r.Bool(); int gold = r.Int();
                var b = go ? go.GetComponent<Berry>() : null;
                if (b)
                {
                    b.berryTier = tier;
                    b.goldenBerryCoinValue = gold;
                    if (nebula && !b.isNebulaBerry) b.ChangeToNebulaBerry();
                    else if (golden && !b.isGoldenBerry) Game.Invoke(b, "ChangeToGoldenBerry");
                }
            }
            if (r.Bool()) { int tier = r.Int(); var c = go ? go.GetComponent<Coin>() : null; if (c) c.coinTier = tier; }
            if (r.Bool()) { string name = r.Str(); var cu = go ? go.GetComponent<BerryCultist_AI>() : null; if (cu) cu.cultistsName = name; }
            if (r.Bool())
            {
                int coin = r.Int(), orbs = r.Int(); bool bonus = r.Bool(); int puzzle = r.Int(), disk = r.Int(), cassette = r.Int(); float juice = r.Float();
                var pk = go ? go.GetComponent<PickUppable>() : null;
                if (pk)
                {
                    pk.coinValue = coin;
                    pk.SetNumOfOrbsToSpawnAtDeposited((StarOrbsToSpawnWhenDeposited)orbs);
                    pk.isBonusVoidBox = bonus;
                    pk.puzzleIdentity_Index = puzzle;
                    pk.disk_Index = disk;
                    pk.cassetteIndex = cassette;
                    pk.holeJuiceValue = juice;
                }
            }
        }

        static Packet SnapPacket(List<NetEntity> list, bool all)
        {
            var entries = new List<NetEntity>();
            snapTick++;
            foreach (var ne in list)
            {
                if (!ne || ne.Id == 0) continue;
                Transform t = ne.transform;
                bool active = ne.gameObject.activeInHierarchy;
                bool colliders = ne.CollidersOn;
                bool send = all || !ne.HasSent || active != ne.SentActive || colliders != ne.SentColliders || ((ne.Id + snapTick) % 45 == 0);
                if (!send && active)
                    send = (t.position - ne.SentPos).sqrMagnitude > 0.0001f || Quaternion.Angle(t.rotation, ne.SentRot) > 0.75f;
                if (!send) continue;
                ne.SentPos = t.position; ne.SentRot = t.rotation; ne.SentActive = active; ne.SentColliders = colliders; ne.HasSent = true;
                entries.Add(ne);
            }
            var p = new Packet(Msg.Snap).Int(Session.Epoch).Int(entries.Count);
            foreach (var ne in entries)
                p.Int(ne.Id).Byte((byte)((ne.gameObject.activeInHierarchy ? 1 : 0) | (ne.CollidersOn ? 2 : 0))).Vec(ne.transform.position).QuatSmall(ne.transform.rotation);
            return p;
        }

        // 15 Hz on the host.
        public static void HostTick()
        {
            if (spawnQueue.Count > 0)
            {
                spawnQueue.RemoveAll(ne => !ne);
                for (int i = 0; i < spawnQueue.Count; i += 64)
                    Session.BroadcastReady(SpawnPacket(spawnQueue.GetRange(i, Mathf.Min(64, spawnQueue.Count - i))));
                spawnQueue.Clear();
            }
            if (despawnQueue.Count > 0)
            {
                var p = new Packet(Msg.Despawn).Int(Session.Epoch).Int(despawnQueue.Count);
                foreach (int id in despawnQueue) p.Int(id);
                Session.BroadcastReady(p);
                despawnQueue.Clear();
            }
            var snap = SnapPacket(All, false);
            if (snap.Length > 9) Session.BroadcastReady(snap);
        }

        public static void GuestHandle(Reader r)
        {
            int epoch = r.Int();
            if (epoch != Session.Epoch || !Session.GuestLive) return;
            switch (r.Type)
            {
                case Msg.StaticMap: ApplyStaticMap(r); break;
                case Msg.Spawn: ApplySpawn(r); break;
                case Msg.Despawn:
                    {
                        int n = r.Int();
                        for (int i = 0; i < n; i++) { var ne = Get(r.Int()); if (ne) Object.Destroy(ne.gameObject); }
                        break;
                    }
                case Msg.Snap: ApplySnap(r); break;
                case Msg.Owner:
                    {
                        var ne = Get(r.Int()); int owner = r.Int();
                        if (ne && !ne.OwnedLocally) ne.Owner = owner;
                        break;
                    }
                case Msg.Deny: Denied(Get(r.Int())); break;
            }
        }

        public static bool MapApplied;

        static void ApplyStaticMap(Reader r)
        {
            MapApplied = true;
            int n = r.Int();
            var keep = new HashSet<NetEntity>();
            for (int i = 0; i < n; i++)
            {
                int id = r.Int(); string key = r.Str();
                NetEntity ne;
                if (byKey.TryGetValue(key, out ne) && ne)
                {
                    ne.Id = id;
                    ById[id] = ne;
                    keep.Add(ne);
                }
            }
            int removed = 0;
            foreach (var ne in All.ToArray())
                if (ne && ne.IsStatic && !keep.Contains(ne)) { Object.Destroy(ne.gameObject); removed++; }
            NetLog.Info("static map: " + keep.Count + " matched, " + removed + " removed");
        }

        static void ApplySpawn(Reader r)
        {
            int n = r.Int();
            for (int i = 0; i < n; i++)
            {
                int id = r.Int(); string name = r.Str(); Vector3 pos = r.Vec(); Quaternion rot = r.Quat(); Vector3 scale = r.Vec(); bool active = r.Bool();
                NetEntity ne = Get(id);
                if (!ne)
                {
                    GameObject go = CreateMirror(name, pos, rot);
                    ne = go.GetComponent<NetEntity>() ?? go.AddComponent<NetEntity>();
                    ne.Id = id; ne.IsStatic = false; ne.Key = null; ne.Prefab = name; ne.Owner = 0;
                    ne.OwnedLocally = false; ne.HasSent = false; ne.TakeRequested = false; ne.CoastUntil = 0;
                    ne.Bind();
                    ById[id] = ne;
                    All.Add(ne);
                    go.transform.localScale = scale;
                    if (ne.Rb) ne.Rb.isKinematic = true;
                }
                ReadExtra(r, ne.gameObject);
                ne.TargetPos = pos; ne.TargetRot = rot; ne.HasTarget = true;
                if (ne.gameObject.activeSelf != active) ne.gameObject.SetActive(active);
            }
        }

        static GameObject CreateMirror(string name, Vector3 pos, Quaternion rot)
        {
            GameObject src = FindPrefab(name);
            if (src)
            {
                GameObject go = Object.Instantiate(src, pos, rot);
                go.name = name;
                if (!go.activeSelf) go.SetActive(true);
                return go;
            }
            if (missingWarned.Add(name)) NetLog.Warn("no prefab for '" + name + "', showing a placeholder");
            GameObject ph = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ph.name = name;
            ph.transform.SetPositionAndRotation(pos, rot);
            Object.Destroy(ph.GetComponent<Collider>());
            var rb = ph.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            return ph;
        }

        static GameObject FindPrefab(string name)
        {
            if (prefabs == null)
            {
                prefabs = new Dictionary<string, GameObject>();
                var nested = new List<GameObject>();
                foreach (GameObject g in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (g.scene.IsValid() || !IsEntityRoot(g)) continue;
                    if (g.transform.parent != null) { nested.Add(g); continue; }
                    if (!prefabs.ContainsKey(g.name)) prefabs[g.name] = g;
                }
                // Pieces inside a prefab (wall rubble, candy in a pinata) are cloned from the asset's child.
                foreach (GameObject g in nested) if (!prefabs.ContainsKey(g.name)) prefabs[g.name] = g;
                NetLog.Info("prefab cache: " + prefabs.Count + " entity prefabs");
            }
            GameObject p;
            if (prefabs.TryGetValue(name, out p) && p) return p;
            // Objects copied from a scene instance at runtime (gnomes, props) have no prefab asset.
            foreach (var ne in All)
                if (ne && ne.Prefab == name && ne.gameObject.scene.IsValid()) return ne.gameObject;
            return null;
        }

        static void ApplySnap(Reader r)
        {
            int n = r.Int();
            for (int i = 0; i < n; i++)
            {
                int id = r.Int(); byte flags = r.Byte(); Vector3 pos = r.Vec(); Quaternion rot = r.QuatSmall();
                bool active = (flags & 1) != 0, colliders = (flags & 2) != 0;
                NetEntity ne = Get(id);
                if (!ne || ne.OwnedLocally) continue;
                if (ne.gameObject.activeSelf != active) ne.gameObject.SetActive(active);
                // Star orbs and void boxes switch collision on in physics code guests don't run; follow the host.
                if (ne.CollidersOn != colliders) ne.SetColliders(colliders);
                ne.TargetPos = pos; ne.TargetRot = rot;
                if (!ne.HasTarget) { ne.transform.SetPositionAndRotation(pos, rot); ne.HasTarget = true; }
            }
        }

        // Guests show host-owned objects as kinematic copies that glide toward the latest snapshot.
        public static void GuestLateUpdate()
        {
            float k = 1f - Mathf.Exp(-18f * Time.deltaTime);
            float now = Time.time;
            for (int i = All.Count - 1; i >= 0; i--)
            {
                NetEntity ne = All[i];
                if (!ne) continue;
                if (ne.OwnedLocally) { WatchOwned(ne, now); continue; }
                if (now < ne.CoastUntil) continue;
                if (ne.Rb && !ne.Rb.isKinematic) ne.Rb.isKinematic = true;
                if (!ne.HasTarget) continue;
                Transform t = ne.transform;
                if ((t.position - ne.TargetPos).sqrMagnitude > 16f) t.SetPositionAndRotation(ne.TargetPos, ne.TargetRot);
                else t.SetPositionAndRotation(Vector3.Lerp(t.position, ne.TargetPos, k), Quaternion.Slerp(t.rotation, ne.TargetRot, k));
            }
            if (now - lastOwnSend >= 0.05f)
            {
                lastOwnSend = now;
                SendOwnedStates();
            }
        }

        static void WatchOwned(NetEntity ne, float now)
        {
            if (ne.ReleaseAt > 0f)
            {
                if (now >= ne.ReleaseAt) Release(ne, ne.PendingRelease);
                return;
            }
            bool busy = Game.HeldBody == ne.Rb || Game.InVacuum(ne.Rb) || Game.InSuckZone(ne.Rb);
            if (busy) { ne.IdleSince = 0f; return; }
            if (ne.IdleSince == 0f) ne.IdleSince = now;
            else if (now - ne.IdleSince > 0.3f) Release(ne, ReleaseReason.Idle);
        }

        static void SendOwnedStates()
        {
            List<NetEntity> owned = null;
            foreach (var ne in All)
                if (ne && ne.OwnedLocally && ne.Id != 0) (owned ?? (owned = new List<NetEntity>())).Add(ne);
            if (owned == null) return;
            var p = new Packet(Msg.OwnState).Int(Session.Epoch).Int(owned.Count);
            foreach (var ne in owned) p.Int(ne.Id).Vec(ne.transform.position).QuatSmall(ne.transform.rotation);
            Session.SendToHost(p);
        }

        // Guest takes over simulating an object it touches, so holding and throwing feel local.
        public static void Claim(NetEntity ne, ClaimReason why)
        {
            if (!Session.GuestLive || !ne || ne.Id == 0) return;
            if (ne.OwnedLocally) { ne.ReleaseAt = 0f; ne.IdleSince = 0f; return; }
            if (ne.Owner != 0 && ne.Owner != Session.LocalId) return;
            ne.OwnedLocally = true;
            ne.Owner = Session.LocalId;
            ne.IdleSince = 0f;
            ne.ReleaseAt = 0f;
            if (ne.Rb) ne.Rb.isKinematic = false;
            Session.SendToHost(new Packet(Msg.Claim).Int(Session.Epoch).Int(ne.Id).Byte((byte)why));
        }

        // Physics impulses land on the next step, so throws hand over after a short delay.
        public static void ReleaseSoon(NetEntity ne, ReleaseReason why)
        {
            if (!ne || !ne.OwnedLocally) return;
            ne.PendingRelease = why;
            ne.ReleaseAt = Time.time + 0.06f;
        }

        public static void Release(NetEntity ne, ReleaseReason why)
        {
            if (!ne || !ne.OwnedLocally) return;
            Vector3 v = Vector3.zero, av = Vector3.zero;
            if (ne.Rb && !ne.Rb.isKinematic) { v = ne.Rb.linearVelocity; av = ne.Rb.angularVelocity; }
            ne.OwnedLocally = false;
            ne.Owner = 0;
            ne.ReleaseAt = 0f;
            ne.CoastUntil = Time.time + 0.3f;
            ne.TargetPos = ne.transform.position;
            ne.TargetRot = ne.transform.rotation;
            ne.HasTarget = true;
            Session.SendToHost(new Packet(Msg.Release).Int(Session.Epoch).Int(ne.Id).Byte((byte)why)
                .Vec(ne.transform.position).Quat(ne.transform.rotation).Vec(v).Vec(av));
        }

        static void Denied(NetEntity ne)
        {
            if (!ne || !ne.OwnedLocally) return;
            if (Game.HeldBody == ne.Rb && Game.Grabber) Game.Grabber.ClearItemInHand();
            var vac = Game.Vacuum;
            if (vac != null) vac.Remove(ne.Rb);
            ne.OwnedLocally = false;
            ne.ReleaseAt = 0f;
            if (ne.Rb) ne.Rb.isKinematic = true;
            if (ne.Pick) ne.Pick.EnableColliders_Local();
        }

        // Host side of a guest's claim / stream / release.
        public static void HostHandle(Peer peer, Reader r)
        {
            int epoch = r.Int();
            if (epoch != Session.Epoch || !peer.Ready) return;
            switch (r.Type)
            {
                case Msg.Claim:
                    {
                        int id = r.Int(); var why = (ClaimReason)r.Byte();
                        var ne = Get(id);
                        if (!ne) return;
                        bool hostBusy = (ne.Rb && (Game.HeldBody == ne.Rb || Game.InVacuum(ne.Rb)));
                        if ((ne.Owner != 0 && ne.Owner != peer.Id) || hostBusy)
                        {
                            peer.Conn.Send(new Packet(Msg.Deny).Int(Session.Epoch).Int(id));
                            return;
                        }
                        ne.Owner = peer.Id;
                        if (why == ClaimReason.Pickup && ne.Pick) ne.Pick.OnPickUp();
                        if (ne.Rb) ne.Rb.isKinematic = true;
                        var zone = Game.VacuumZone;
                        if (zone && ne.Rb) { zone.rbsInSuckZone.Remove(ne.Rb); zone.rbsInSuckZone_NonBerries.Remove(ne.Rb); }
                        Session.BroadcastReady(new Packet(Msg.Owner).Int(Session.Epoch).Int(id).Int(peer.Id));
                        break;
                    }
                case Msg.OwnState:
                    {
                        int n = r.Int();
                        for (int i = 0; i < n; i++)
                        {
                            var ne = Get(r.Int()); Vector3 pos = r.Vec(); Quaternion rot = r.QuatSmall();
                            if (!ne || ne.Owner != peer.Id) continue;
                            if (ne.Rb) { ne.Rb.MovePosition(pos); ne.Rb.MoveRotation(rot); }
                            else ne.transform.SetPositionAndRotation(pos, rot);
                        }
                        break;
                    }
                case Msg.Release:
                    {
                        var ne = Get(r.Int()); var why = (ReleaseReason)r.Byte();
                        Vector3 pos = r.Vec(); Quaternion rot = r.Quat(); Vector3 v = r.Vec(), av = r.Vec();
                        if (!ne || ne.Owner != peer.Id) return;
                        HostRelease(ne, pos, rot, v, av, why);
                        break;
                    }
                case Msg.Take: HostTake(peer, Get(r.Int()), r.Bool()); break;
            }
        }

        static void HostRelease(NetEntity ne, Vector3 pos, Quaternion rot, Vector3 v, Vector3 av, ReleaseReason why)
        {
            ne.Owner = 0;
            ne.transform.SetPositionAndRotation(pos, rot);
            bool frozen = GameManager.Singleton.hasTimerElapsed_IsNighttime && ne.Pick && !ne.Pick.doNotFreezeAtNight;
            if (ne.Rb)
            {
                ne.Rb.position = pos; ne.Rb.rotation = rot;
                ne.Rb.isKinematic = frozen;
                if (!frozen) { ne.Rb.linearVelocity = v; ne.Rb.angularVelocity = av; }
            }
            if (ne.Pick)
            {
                switch (why)
                {
                    case ReleaseReason.Drop:
                    case ReleaseReason.Throw: ne.Pick.OnDrop(); break;
                    case ReleaseReason.VacShoot:
                        ne.Pick.SetDrag_NotHeld(); ne.Pick.ResetRecentlyThrownTime(); ne.Pick.EnableColliders_Local(); break;
                    case ReleaseReason.Kick: ne.Pick.OnKick(); break;
                    default: if (ne.Pick.isHeld) ne.Pick.OnDrop(); ne.Pick.EnableColliders_Local(); break;
                }
                if (ne.Rb && !frozen) { ne.Rb.linearVelocity = v; ne.Rb.angularVelocity = av; }
            }
            Session.BroadcastReady(new Packet(Msg.Owner).Int(Session.Epoch).Int(ne.Id).Int(0));
        }

        // A guest left: drop whatever it was holding where its avatar was.
        public static void HostReleaseAll(Peer peer)
        {
            foreach (var ne in All.ToArray())
            {
                if (!ne || ne.Owner != peer.Id) continue;
                Vector3 pos = ne.transform.position;
                if (pos.y < -20f || pos.y > 500f) pos = peer.LastPos + Vector3.up * 1.5f;
                HostRelease(ne, pos, ne.transform.rotation, Vector3.zero, Vector3.zero, ReleaseReason.Idle);
            }
        }

        // Coins and seeds: the host removes the object and credits the shared pocket.
        public static void RequestTake(NetEntity ne, bool manual)
        {
            if (!ne || ne.TakeRequested || ne.Id == 0) return;
            ne.TakeRequested = true;
            Session.SendToHost(new Packet(Msg.Take).Int(Session.Epoch).Int(ne.Id).Bool(manual));
        }

        static void HostTake(Peer peer, NetEntity ne, bool manual)
        {
            if (!ne || !ne.Suck || Game.SuckDeposited(ne.Suck)) return;
            SuckUppables s = ne.Suck;
            Game.SetSuckDeposited(s, true);
            s.gameObject.tag = "Untagged";
            try { Game.Invoke(s, "OnPickUpCuteUpwardBoop"); } catch { }
            if (s.identity == ItemIdentity.Coin)
            {
                AudioManager.Singleton.PlayCoinPickUpSFX(s.transform.position);
                Coin coin = s.GetComponent<Coin>();
                coin.CalculateCoinValue();
                PlayerStats.Singleton.IncreaseMoney_Held(coin.GetCoinValue());
                peer.Conn.Send(new Packet(Msg.Grant).Byte(1).Int(coin.GetCoinValue()));
            }
            else if (s.identity == ItemIdentity.Seed)
            {
                PlayerStats.Singleton.IncreaseSeeds(1);
                peer.Conn.Send(new Packet(Msg.Grant).Byte(2).Int(1));
            }
            Plugin.Instance.StartCoroutine(DestroyLater(s.gameObject, 0.35f));
        }

        static IEnumerator DestroyLater(GameObject go, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (go) Object.Destroy(go);
        }

        public static void GuestGrant(Reader r)
        {
            byte kind = r.Byte(); int value = r.Int();
            Vector3 at = Player.Singleton ? Player.Singleton.transform.position : Vector3.zero;
            if (kind == 1)
            {
                // The coin went into the shared pocket on the host; this is only the pickup feedback.
                HudManager.Singleton.PlayFeedback_MoneyHeldPickUp();
                AudioManager.Singleton.PlayCoinPickUpSFX(at);
            }
            else if (kind == 2) HudManager.Singleton.PlayFeedback_SeedHeldPickUp();
        }

        // Guest kick: claim what the kick will touch so the game's own impulse applies, then hand back.
        public static List<NetEntity> ClaimKickTargets()
        {
            var list = new List<NetEntity>();
            var g = Game.Grabber; var cam = Game.Camera;
            if (!g || !cam) return list;
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            RaycastHit hit;
            Vector3 at = Physics.Raycast(ray, out hit, Game.KickLength, Game.KickMask, QueryTriggerInteraction.Ignore)
                ? hit.point : cam.transform.position + cam.transform.forward * Game.KickLength;
            foreach (Collider c in Physics.OverlapSphere(at, Game.KickRadius))
            {
                if (!c.gameObject.CompareTag("PickUp") && !c.gameObject.CompareTag("SuckUppable")) continue;
                var ne = Of(c);
                if (!ne || list.Contains(ne)) continue;
                if (ne.Pick && !ne.Pick.canBeKicked) continue;
                Claim(ne, ClaimReason.Kick);
                if (ne.OwnedLocally) list.Add(ne);
            }
            return list;
        }

        public static int Count { get { return All.Count; } }
    }
}
