using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace BerryCoop.Net
{
    public enum Msg : byte
    {
        Hello = 1, Welcome, Reject, PlayerJoin, PlayerLeave, World, SceneReady, RoundEnd, ToMenu, Ready, Toast, Ping, Pong, Bank, Slept,
        StaticMap = 20, Spawn, Despawn, Snap, Claim, Release, OwnState, Owner, Deny, Take, Grant,
        State = 40, Growers,
        Cmd = 50, Rpc, Build, Sell, Kick, PopGun, CmdDenied,
        Player = 60,
    }

    public enum ClaimReason : byte { Pickup = 1, Vacuum, Kick }
    public enum ReleaseReason : byte { Idle = 1, Drop, Throw, VacShoot, Kick }

    public sealed class Packet
    {
        readonly MemoryStream ms = new MemoryStream(64);
        public readonly BinaryWriter W;

        public Packet(Msg type)
        {
            W = new BinaryWriter(ms, Encoding.UTF8);
            W.Write((byte)type);
        }

        public byte[] ToArray() { W.Flush(); return ms.ToArray(); }
        public int Length { get { W.Flush(); return (int)ms.Length; } }

        public Packet Int(int v) { W.Write(v); return this; }
        public Packet Byte(byte v) { W.Write(v); return this; }
        public Packet Bool(bool v) { W.Write(v); return this; }
        public Packet Float(float v) { W.Write(v); return this; }
        public Packet Str(string v) { W.Write(v ?? ""); return this; }
        public Packet Vec(Vector3 v) { W.Write(v.x); W.Write(v.y); W.Write(v.z); return this; }
        public Packet Quat(Quaternion q) { W.Write(q.x); W.Write(q.y); W.Write(q.z); W.Write(q.w); return this; }

        // Rotation as four signed shorts; plenty for visuals at a quarter of the size.
        public Packet QuatSmall(Quaternion q)
        {
            if (q.w < 0) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
            W.Write((short)(q.x * 32767f)); W.Write((short)(q.y * 32767f));
            W.Write((short)(q.z * 32767f)); W.Write((short)(q.w * 32767f));
            return this;
        }
    }

    public sealed class Reader
    {
        public readonly Msg Type;
        public readonly BinaryReader R;

        public Reader(byte[] data)
        {
            R = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            Type = (Msg)R.ReadByte();
        }

        public int Int() { return R.ReadInt32(); }
        public byte Byte() { return R.ReadByte(); }
        public bool Bool() { return R.ReadBoolean(); }
        public float Float() { return R.ReadSingle(); }
        public string Str() { return R.ReadString(); }
        public Vector3 Vec() { return new Vector3(R.ReadSingle(), R.ReadSingle(), R.ReadSingle()); }
        public Quaternion Quat() { return new Quaternion(R.ReadSingle(), R.ReadSingle(), R.ReadSingle(), R.ReadSingle()); }

        public Quaternion QuatSmall()
        {
            var q = new Quaternion(R.ReadInt16() / 32767f, R.ReadInt16() / 32767f, R.ReadInt16() / 32767f, R.ReadInt16() / 32767f);
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m < 0.0001f) return Quaternion.identity;
            return new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }

        public bool AtEnd { get { return R.BaseStream.Position >= R.BaseStream.Length; } }
    }

    // One framed TCP link (4-byte little-endian length + payload). Reads and writes run on their own threads;
    // the main thread only touches Send and Incoming.
    public sealed class Connection
    {
        const int MaxFrame = 8 * 1024 * 1024;

        readonly TcpClient client;
        readonly NetworkStream stream;
        readonly Queue<byte[]> outgoing = new Queue<byte[]>();
        readonly Queue<byte[]> incoming = new Queue<byte[]>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool closed;
        string closeReason = "";

        public readonly string Label;
        public long BytesOut, BytesIn;

        public Connection(TcpClient client, string label)
        {
            this.client = client;
            Label = label;
            client.NoDelay = true;
            client.ReceiveTimeout = 0;
            client.SendTimeout = 15000;
            stream = client.GetStream();
            new Thread(ReadLoop) { IsBackground = true, Name = "coop-read " + label }.Start();
            new Thread(WriteLoop) { IsBackground = true, Name = "coop-write " + label }.Start();
        }

        public bool Closed { get { return closed; } }
        public string CloseReason { get { lock (outgoing) return closeReason; } }

        public void Send(Packet p) { Send(p.ToArray()); }

        public void Send(byte[] payload)
        {
            if (closed) return;
            lock (outgoing) outgoing.Enqueue(payload);
            wake.Set();
        }

        public bool TryReceive(out byte[] payload)
        {
            lock (incoming)
            {
                if (incoming.Count > 0) { payload = incoming.Dequeue(); return true; }
            }
            payload = null;
            return false;
        }

        public void Close(string reason)
        {
            lock (outgoing) { if (!closed) closeReason = reason; }
            closed = true;
            wake.Set();
            try { client.Close(); } catch { }
        }

        void ReadLoop()
        {
            byte[] head = new byte[4];
            try
            {
                while (!closed)
                {
                    ReadExact(head, 4);
                    int len = BitConverter.ToInt32(head, 0);
                    if (len <= 0 || len > MaxFrame) throw new IOException("bad frame length " + len);
                    byte[] body = new byte[len];
                    ReadExact(body, len);
                    Interlocked.Add(ref BytesIn, len + 4);
                    lock (incoming) incoming.Enqueue(body);
                }
            }
            catch (Exception e) { if (!closed) Close("Connection lost (" + e.Message + ")"); }
        }

        void ReadExact(byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = stream.Read(buf, got, count - got);
                if (n <= 0) throw new IOException("closed by the other side");
                got += n;
            }
        }

        void WriteLoop()
        {
            var batch = new List<byte[]>();
            try
            {
                while (!closed)
                {
                    wake.WaitOne(250);
                    batch.Clear();
                    lock (outgoing) { while (outgoing.Count > 0) batch.Add(outgoing.Dequeue()); }
                    if (batch.Count == 0) continue;
                    using (var ms = new MemoryStream())
                    {
                        foreach (byte[] p in batch)
                        {
                            ms.Write(BitConverter.GetBytes(p.Length), 0, 4);
                            ms.Write(p, 0, p.Length);
                        }
                        stream.Write(ms.GetBuffer(), 0, (int)ms.Length);
                        Interlocked.Add(ref BytesOut, ms.Length);
                    }
                }
            }
            catch (Exception e) { if (!closed) Close("Connection lost (" + e.Message + ")"); }
        }
    }

    // LAN / direct-IP listener. Accepted sockets are handed to the main thread through Pending.
    public sealed class Listener
    {
        readonly TcpListener listener;
        readonly Queue<TcpClient> pending;
        volatile bool stopped;

        public Listener(int port, Queue<TcpClient> pending)
        {
            this.pending = pending;
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "coop-accept" }.Start();
        }

        void AcceptLoop()
        {
            while (!stopped)
            {
                try
                {
                    TcpClient c = listener.AcceptTcpClient();
                    lock (pending) pending.Enqueue(c);
                }
                catch { if (stopped) return; Thread.Sleep(100); }
            }
        }

        public void Stop()
        {
            stopped = true;
            try { listener.Stop(); } catch { }
        }
    }
}
