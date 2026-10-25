using System.Collections.Generic;
using BerryCoop.Net;
using UnityEngine;

namespace BerryCoop
{
    // Other players as simple berry-coloured figures with a name tag. On the host they also have a kinematic
    // body, so a guest walking into berries pushes them in the simulation everyone sees.
    public static class Avatars
    {
        const byte FlagInShop = 1;

        sealed class Puppet
        {
            public int Id;
            public GameObject Root;
            public Transform Head;
            public TextMesh Label;
            public Rigidbody Body;
            public Vector3 Target;
            public float Yaw, Pitch;
            public bool HasTarget;
        }

        static readonly Dictionary<int, Puppet> puppets = new Dictionary<int, Puppet>();
        static readonly Color[] colors =
        {
            new Color(0.95f, 0.25f, 0.35f), new Color(0.3f, 0.45f, 0.95f), new Color(0.45f, 0.85f, 0.35f), new Color(0.95f, 0.75f, 0.2f),
        };
        static Material baseMaterial;

        public static void OnSceneLoaded()
        {
            puppets.Clear();
            baseMaterial = null;
        }

        public static void Clear()
        {
            foreach (Puppet p in puppets.Values) if (p.Root) Object.Destroy(p.Root);
            puppets.Clear();
        }

        public static void Remove(int id)
        {
            Puppet p;
            if (!puppets.TryGetValue(id, out p)) return;
            if (p.Root) Object.Destroy(p.Root);
            puppets.Remove(id);
        }

        static Packet PosePacket(int id)
        {
            Transform t = Player.Singleton.transform;
            GameObject cam = Game.Camera;
            float yaw = cam ? cam.transform.eulerAngles.y : t.eulerAngles.y;
            float pitch = cam ? cam.transform.eulerAngles.x : 0f;
            byte flags = 0;
            if (GameManager.Singleton.gameState == GameManager.GameState.RoundOverShop) flags |= FlagInShop;
            return new Packet(Msg.Player).Int(Session.Epoch).Int(id).Vec(t.position).Float(yaw).Float(pitch).Byte(flags);
        }

        public static void SendLocalPose()
        {
            if (!Player.Singleton || !Game.InGameScene) return;
            if (Session.IsHost) Session.BroadcastReady(PosePacket(0));
            else Session.SendToHost(PosePacket(Session.LocalId));
        }

        public static void HostReceivePose(Peer peer, Reader r)
        {
            int epoch = r.Int();
            r.Int();
            Vector3 pos = r.Vec(); float yaw = r.Float(), pitch = r.Float(); byte flags = r.Byte();
            if (epoch != Session.Epoch || !peer.Ready) return;
            peer.LastPos = pos;
            peer.InShop = (flags & FlagInShop) != 0;
            Apply(peer.Id, pos, yaw, pitch, flags);
            Session.BroadcastReadyExcept(new Packet(Msg.Player).Int(Session.Epoch).Int(peer.Id).Vec(pos).Float(yaw).Float(pitch).Byte(flags), peer.Id);
        }

        public static void GuestReceivePose(Reader r)
        {
            int epoch = r.Int(), id = r.Int();
            Vector3 pos = r.Vec(); float yaw = r.Float(), pitch = r.Float(); byte flags = r.Byte();
            if (epoch != Session.Epoch || !Session.GuestLive || id == Session.LocalId) return;
            Apply(id, pos, yaw, pitch, flags);
        }

        static void Apply(int id, Vector3 pos, float yaw, float pitch, byte flags)
        {
            Puppet p;
            if (!puppets.TryGetValue(id, out p) || !p.Root) puppets[id] = p = Create(id);
            bool show = (flags & FlagInShop) == 0;
            if (p.Root.activeSelf != show) p.Root.SetActive(show);
            p.Target = pos; p.Yaw = yaw; p.Pitch = pitch;
            if (!p.HasTarget) { p.Root.transform.position = pos; p.HasTarget = true; }
        }

        public static float YawOf(int id)
        {
            Puppet p;
            return puppets.TryGetValue(id, out p) ? p.Yaw : 0f;
        }

        public static Vector3? PositionOf(int id)
        {
            Puppet p;
            return puppets.TryGetValue(id, out p) && p.Root ? p.Root.transform.position : (Vector3?)null;
        }

        public static void LateUpdate()
        {
            if (puppets.Count == 0) return;
            float k = 1f - Mathf.Exp(-14f * Time.deltaTime);
            Camera cam = Camera.main;
            foreach (Puppet p in puppets.Values)
            {
                if (!p.Root || !p.HasTarget) continue;
                Transform t = p.Root.transform;
                Vector3 next = (t.position - p.Target).sqrMagnitude > 64f ? p.Target : Vector3.Lerp(t.position, p.Target, k);
                Quaternion rot = Quaternion.Slerp(t.rotation, Quaternion.Euler(0f, p.Yaw, 0f), k);
                if (p.Body) { p.Body.MovePosition(next); p.Body.MoveRotation(rot); }
                else t.SetPositionAndRotation(next, rot);
                if (p.Head) p.Head.localRotation = Quaternion.Euler(Mathf.DeltaAngle(0f, p.Pitch) * 0.6f, 0f, 0f);
                if (p.Label && cam) p.Label.transform.rotation = Quaternion.LookRotation(p.Label.transform.position - cam.transform.position);
            }
        }

        static Material MakeMaterial(Color c)
        {
            if (!baseMaterial) baseMaterial = FindGameMaterial();
            var m = new Material(baseMaterial);
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", null);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", null);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            m.color = c;
            return m;
        }

        // A material the game already draws with, so its shader variants are in the build (fresh ones render nothing).
        static Material FindGameMaterial()
        {
            Material fallback = null;
            foreach (MeshRenderer r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                Material mat = r.sharedMaterial;
                if (!mat || !mat.shader) continue;
                string s = mat.shader.name;
                if (s.Contains("Universal Render Pipeline/Lit") || s.Contains("Simple Lit")) return mat;
                if (!fallback && mat.HasProperty("_BaseColor")) fallback = mat;
            }
            if (fallback) return fallback;
            return new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default"));
        }

        static Puppet Create(int id)
        {
            Color c = colors[Mathf.Abs(id) % colors.Length];
            var root = new GameObject("CoopPlayer_" + id);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.95f, 0f);
            body.transform.localScale = new Vector3(0.8f, 0.85f, 0.8f);
            body.GetComponent<Renderer>().material = MakeMaterial(c);

            var head = new GameObject("Head").transform;
            head.SetParent(root.transform, false);
            head.localPosition = new Vector3(0f, 1.75f, 0f);
            GameObject skull = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(skull.GetComponent<Collider>());
            skull.transform.SetParent(head, false);
            skull.transform.localScale = Vector3.one * 0.7f;
            skull.GetComponent<Renderer>().material = MakeMaterial(Color.Lerp(c, Color.white, 0.25f));
            Material eyeMat = MakeMaterial(new Color(0.08f, 0.08f, 0.1f));
            foreach (float x in new[] { -0.13f, 0.13f })
            {
                GameObject eye = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.Destroy(eye.GetComponent<Collider>());
                eye.transform.SetParent(head, false);
                eye.transform.localPosition = new Vector3(x, 0.06f, 0.3f);
                eye.transform.localScale = Vector3.one * 0.12f;
                eye.GetComponent<Renderer>().material = eyeMat;
            }
            GameObject leaf = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(leaf.GetComponent<Collider>());
            leaf.transform.SetParent(head, false);
            leaf.transform.localPosition = new Vector3(0f, 0.42f, 0f);
            leaf.transform.localRotation = Quaternion.Euler(0f, 45f, 20f);
            leaf.transform.localScale = new Vector3(0.28f, 0.06f, 0.16f);
            leaf.GetComponent<Renderer>().material = MakeMaterial(new Color(0.25f, 0.7f, 0.25f));

            var labelGo = new GameObject("Name");
            labelGo.transform.SetParent(root.transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 2.45f, 0f);
            var label = labelGo.AddComponent<TextMesh>();
            label.text = Session.NameOf(id);
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.characterSize = 0.08f;
            label.fontSize = 48;
            label.color = Color.white;
            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f) { label.font = f; labelGo.GetComponent<MeshRenderer>().material = f.material; }

            var cap = root.AddComponent<CapsuleCollider>();
            cap.center = new Vector3(0f, 0.95f, 0f);
            cap.height = 1.9f;
            cap.radius = 0.4f;
            var rb = root.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            NetLog.Info("avatar for " + label.text + " created (shader " + body.GetComponent<Renderer>().sharedMaterial.shader.name + ")");
            return new Puppet { Id = id, Root = root, Head = head, Label = label, Body = rb };
        }
    }
}
