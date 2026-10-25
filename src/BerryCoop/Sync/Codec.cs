using System;
using System.Collections;
using System.Collections.Generic;
using BerryCoop.Net;
using UnityEngine;

namespace BerryCoop
{
    // Field values on the wire. Only plain data travels; object references are never mirrored.
    public static class Codec
    {
        public static bool Supported(Type t)
        {
            if (Scalar(t)) return true;
            if (t.IsArray) return t.GetArrayRank() == 1 && Scalar(t.GetElementType());
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return Scalar(t.GetGenericArguments()[0]);
            return false;
        }

        static bool Scalar(Type t)
        {
            return t == typeof(int) || t == typeof(float) || t == typeof(bool) || t == typeof(long) || t == typeof(double)
                || t == typeof(string) || t == typeof(byte) || t == typeof(Vector3) || t.IsEnum;
        }

        static Type ElementOf(Type t) { return t.IsArray ? t.GetElementType() : t.GetGenericArguments()[0]; }

        public static void Write(Packet p, Type t, object v)
        {
            if (Scalar(t)) { WriteScalar(p, t, v); return; }
            var list = v as IList;
            if (list == null) { p.Int(-1); return; }
            Type e = ElementOf(t);
            p.Int(list.Count);
            foreach (object o in list) WriteScalar(p, e, o);
        }

        static void WriteScalar(Packet p, Type t, object v)
        {
            if (t == typeof(int)) p.Int((int)v);
            else if (t == typeof(float)) p.Float((float)v);
            else if (t == typeof(bool)) p.Bool((bool)v);
            else if (t == typeof(long)) p.W.Write((long)v);
            else if (t == typeof(double)) p.W.Write((double)v);
            else if (t == typeof(string)) p.Str((string)v);
            else if (t == typeof(byte)) p.Byte((byte)v);
            else if (t == typeof(Vector3)) p.Vec((Vector3)v);
            else if (t.IsEnum) p.Int(Convert.ToInt32(v));
        }

        public static object Read(Reader r, Type t)
        {
            if (Scalar(t)) return ReadScalar(r, t);
            int n = r.Int();
            if (n < 0) return null;
            Type e = ElementOf(t);
            if (t.IsArray)
            {
                Array a = Array.CreateInstance(e, n);
                for (int i = 0; i < n; i++) a.SetValue(ReadScalar(r, e), i);
                return a;
            }
            var list = (IList)Activator.CreateInstance(t);
            for (int i = 0; i < n; i++) list.Add(ReadScalar(r, e));
            return list;
        }

        static object ReadScalar(Reader r, Type t)
        {
            if (t == typeof(int)) return r.Int();
            if (t == typeof(float)) return r.Float();
            if (t == typeof(bool)) return r.Bool();
            if (t == typeof(long)) return r.R.ReadInt64();
            if (t == typeof(double)) return r.R.ReadDouble();
            if (t == typeof(string)) return r.Str();
            if (t == typeof(byte)) return r.Byte();
            if (t == typeof(Vector3)) return r.Vec();
            if (t.IsEnum) return Enum.ToObject(t, r.Int());
            throw new InvalidOperationException("unsupported " + t);
        }

        public static bool Same(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is float) return Mathf.Abs((float)a - (float)b) < 0.0005f;
            if (a is Vector3) return ((Vector3)a - (Vector3)b).sqrMagnitude < 0.000001f;
            var la = a as IList;
            if (la != null)
            {
                var lb = (IList)b;
                if (la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++) if (!Same(la[i], lb[i])) return false;
                return true;
            }
            return a.Equals(b);
        }

        // Snapshot copy so later in-place list edits are noticed.
        public static object Copy(object v)
        {
            var l = v as IList;
            if (l == null) return v;
            if (v is Array) return ((Array)v).Clone();
            var copy = (IList)Activator.CreateInstance(v.GetType());
            foreach (object o in l) copy.Add(o);
            return copy;
        }

        // Lists are refilled in place because game code keeps references to them.
        public static object Assign(object current, object incoming)
        {
            var cur = current as IList;
            var inc = incoming as IList;
            if (cur == null || inc == null || cur.IsFixedSize != inc.IsFixedSize) return incoming;
            if (cur is Array)
            {
                if (cur.Count != inc.Count) return incoming;
                for (int i = 0; i < inc.Count; i++) cur[i] = inc[i];
                return current;
            }
            cur.Clear();
            foreach (object o in inc) cur.Add(o);
            return current;
        }
    }
}
