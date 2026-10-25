using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BerryCoop
{
    // Marks a buildable placed this round (dirt patch, conveyor, trampoline, juicer); both sides share its id.
    public sealed class BuiltObject : MonoBehaviour
    {
        public int Id;
    }

    // Stable names for non-entity components so commands can point at "this wall" or "that plant bed".
    public static class NetObjects
    {
        static readonly Type[] keyed =
        {
            typeof(WorldUsable), typeof(BreakableWall), typeof(Diggable), typeof(PlantBed), typeof(BerryGrower),
            typeof(Buildable), typeof(BuildSpot), typeof(TurtleEatAoe), typeof(MilestoneGrouper), typeof(Radio),
            typeof(BerryPicker), typeof(SlotMachine_Manager),
        };

        static readonly Dictionary<Component, string> keys = new Dictionary<Component, string>();
        static readonly Dictionary<string, Component> objects = new Dictionary<string, Component>();
        static readonly Dictionary<int, BuiltObject> built = new Dictionary<int, BuiltObject>();
        static readonly Dictionary<Type, FieldInfo> singletonFields = new Dictionary<Type, FieldInfo>();
        public static int NextBuildId = 1;

        public static void Reset()
        {
            keys.Clear(); objects.Clear(); built.Clear();
            NextBuildId = 1;
        }

        public static void Register(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++) Walk(roots[i].transform, "R" + i + ":" + roots[i].name);
            // Buildables already in the scene (the starting dirt patch) get negative ids in hierarchy order.
            int sceneId = 0;
            foreach (var kv in objects)
            {
                var b = kv.Value as Buildable;
                if (!b) continue;
                GameObject root = b.transform.root.gameObject;
                if (root.GetComponent<BuiltObject>()) continue;
                var bo = root.AddComponent<BuiltObject>();
                bo.Id = --sceneId;
                built[bo.Id] = bo;
            }
        }

        static void Walk(Transform t, string path)
        {
            foreach (Type type in keyed)
            {
                Component[] cs = t.GetComponents(type);
                for (int i = 0; i < cs.Length; i++)
                {
                    string k = path + "|" + type.Name + "#" + i;
                    keys[cs[i]] = k;
                    objects[k] = cs[i];
                }
            }
            for (int i = 0; i < t.childCount; i++)
            {
                Transform c = t.GetChild(i);
                Walk(c, path + "/" + i + ":" + c.name);
            }
        }

        public static void AddBuilt(GameObject root, int id)
        {
            var b = root.GetComponent<BuiltObject>() ?? root.AddComponent<BuiltObject>();
            b.Id = id;
            built[id] = b;
            if (id >= NextBuildId) NextBuildId = id + 1;
        }

        public static GameObject Built(int id)
        {
            BuiltObject b;
            return built.TryGetValue(id, out b) && b ? b.gameObject : null;
        }

        static FieldInfo SingletonField(Type t)
        {
            FieldInfo f;
            if (!singletonFields.TryGetValue(t, out f))
            {
                f = t.GetField("Singleton", BindingFlags.Public | BindingFlags.Static);
                if (f != null && !t.IsAssignableFrom(f.FieldType)) f = null;
                singletonFields[t] = f;
            }
            return f;
        }

        public static string KeyOf(Component c)
        {
            if (!c) return null;
            FieldInfo sf = SingletonField(c.GetType());
            if (sf != null && ReferenceEquals(sf.GetValue(null), c)) return "S:" + c.GetType().Name;
            string k;
            if (keys.TryGetValue(c, out k)) return k;
            var ne = c.GetComponentInParent<NetEntity>(true);
            if (ne && ne.Id != 0) return "e:" + ne.Id + ":" + Relative(ne.transform, c);
            var bo = c.GetComponentInParent<BuiltObject>(true);
            if (bo) return "b:" + bo.Id + ":" + Relative(bo.transform, c);
            return null;
        }

        static string Relative(Transform root, Component c)
        {
            var steps = new List<int>();
            for (Transform t = c.transform; t != root && t != null; t = t.parent) steps.Add(t.GetSiblingIndex());
            steps.Reverse();
            Component[] same = c.GetComponents(c.GetType());
            return string.Join("/", steps) + "|" + c.GetType().Name + "#" + Array.IndexOf(same, c);
        }

        public static Component Resolve(string key, Type type)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (key.StartsWith("S:"))
            {
                FieldInfo sf = SingletonField(type);
                return sf != null ? sf.GetValue(null) as Component : UnityEngine.Object.FindAnyObjectByType(type) as Component;
            }
            Component c;
            if (objects.TryGetValue(key, out c)) return c ? c : null;
            if (key.StartsWith("e:") || key.StartsWith("b:"))
            {
                int colon = key.IndexOf(':', 2);
                int id = int.Parse(key.Substring(2, colon - 2));
                Transform root = null;
                if (key[0] == 'e') { var ne = Entities.Get(id); if (ne) root = ne.transform; }
                else { var go = Built(id); if (go) root = go.transform; }
                return root ? FromRelative(root, key.Substring(colon + 1), type) : null;
            }
            return null;
        }

        static Component FromRelative(Transform root, string rel, Type type)
        {
            int bar = rel.IndexOf('|');
            string steps = rel.Substring(0, bar);
            int hash = rel.LastIndexOf('#');
            int index = int.Parse(rel.Substring(hash + 1));
            Transform t = root;
            if (steps.Length > 0)
                foreach (string s in steps.Split('/'))
                {
                    int i = int.Parse(s);
                    if (i >= t.childCount) return null;
                    t = t.GetChild(i);
                }
            Component[] cs = t.GetComponents(type);
            return index < cs.Length ? cs[index] : null;
        }
    }
}
