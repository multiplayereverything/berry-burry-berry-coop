// Client side of the global mod relay's tunnel mode (mod-relay/relay.py).
// Copy this file unchanged into a mod's source folder. C# 5 / .NET 3.5+, no Unity calls,
// safe to use from background threads.
//
// Friend:  TcpClient link = RelayLink.Join(host, port, "baldi", version, code, name, 8000);
//          -> a socket whose bytes reach the host's game (throws IOException with the relay's reason).
// Host:    RelayHost room = new RelayHost(host, port, "baldi", version, listed, roomName, 8000);
//          room.Code is the room code; room.Start(onFriend, onClosed) then hands each friend's
//          data connection to onFriend(TcpClient) on a background thread.
// v2 (tunnel games): RelayLink.JoinRoom (password), RelayLink.ListRooms (locked flag),
//          new RelayHost(..., maxPlayers, passwordHash, handoffSecret, roomName, timeout) and
//          RelayHost.Resume(...) for a teammate taking over after the host left.
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ModRelay
{
    public static class RelayLink
    {
        public const int DefaultPort = 7790;

        // "host", "host:port", "[v6]:port"
        public static bool ParseAddress(string text, int defaultPort, out string host, out int port)
        {
            host = (text ?? "").Trim(); port = defaultPort;
            string portText = null;
            if (host.StartsWith("["))
            {
                int end = host.IndexOf(']');
                if (end < 0) return false;
                if (end + 1 < host.Length && host[end + 1] == ':') portText = host.Substring(end + 2);
                host = host.Substring(1, end - 1);
            }
            else if (host.IndexOf(':') > 0 && host.IndexOf(':') == host.LastIndexOf(':'))
            {
                portText = host.Substring(host.IndexOf(':') + 1);
                host = host.Substring(0, host.IndexOf(':'));
            }
            if (portText != null)
            {
                int parsed;
                if (!int.TryParse(portText, out parsed) || parsed < 1 || parsed > 65535) return false;
                port = parsed;
            }
            return host.Length > 0;
        }

        public static TcpClient Connect(string host, int port, int timeout)
        {
            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult attempt = client.BeginConnect(host, port, null, null);
                using (attempt.AsyncWaitHandle)
                {
                    if (!attempt.AsyncWaitHandle.WaitOne(timeout)) throw new IOException("Can't reach the online server " + host + ":" + port + " (timed out).");
                    client.EndConnect(attempt);
                }
                client.NoDelay = true;
                return client;
            }
            catch { client.Close(); throw; }
        }

        // Separators and line ends can't appear inside a handshake field.
        public static string Clean(string value, int max)
        {
            StringBuilder s = new StringBuilder();
            foreach (char c in value ?? "") if (!char.IsControl(c) && c != '|' && c != ';' && c != ',') s.Append(c);
            string r = s.ToString().Trim();
            return r.Length > max ? r.Substring(0, max) : r;
        }

        public static void WriteLine(Stream stream, string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(bytes, 0, bytes.Length); stream.Flush();
        }

        // Reads one "\n"-ended line a byte at a time, so nothing after it is consumed.
        public static string ReadLine(Stream stream, int max)
        {
            MemoryStream line = new MemoryStream();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) throw new EndOfStreamException("The online server closed the connection.");
                if (b == '\n' || b == 0) break;
                if (b != '\r') line.WriteByte((byte)b);
                if (line.Length > max) throw new IOException("Invalid answer from the online server.");
            }
            return Encoding.UTF8.GetString(line.ToArray());
        }

        public static string Reason(string reply)
        {
            return reply.StartsWith("E|") ? reply.Substring(2) : "Unexpected answer from the online server: " + Clean(reply, 60);
        }

        // Friend side: after this returns, the socket is a raw pipe to the host's game.
        public static TcpClient Join(string host, int port, string game, string version, string code, string name, int timeout)
        {
            TcpClient client = Connect(host, port, timeout);
            try
            {
                NetworkStream stream = client.GetStream();
                client.ReceiveTimeout = timeout + 4000; // the relay waits up to 10 s for the host
                WriteLine(stream, "RELAY|1|JOIN|" + Clean(game, 32) + "|" + Clean(version, 32) + "|" + Clean(code, 8).ToUpperInvariant() + "|" + Clean(name, 24));
                string reply = ReadLine(stream, 512);
                if (reply != "OK") throw new IOException(Reason(reply));
                client.ReceiveTimeout = 0;
                return client;
            }
            catch { client.Close(); throw; }
        }

        // v2 friend side (tunnel games): passwordHash is hex SHA-256 or "" for an open room.
        // A refusal starting "HANDOFF:" means the host left and a teammate is taking over: retry shortly.
        public static TcpClient JoinRoom(string host, int port, string game, string version, string code, string passwordHash, string name, int timeout)
        {
            TcpClient client = Connect(host, port, timeout);
            try
            {
                NetworkStream stream = client.GetStream();
                client.ReceiveTimeout = timeout + 4000;
                WriteLine(stream, "RELAY|2|JOIN|" + Clean(game, 32) + "|" + Clean(version, 48) + "|" + Clean(code, 8).ToUpperInvariant() + "|" + Clean(passwordHash, 128) + "|" + Clean(name, 24));
                string reply = ReadLine(stream, 512);
                if (reply != "OK") throw new IOException(Reason(reply));
                client.ReceiveTimeout = 0;
                return client;
            }
            catch { client.Close(); throw; }
        }

        // v2 public rooms: each entry is { code, players, max, locked ("1" or "0"), name }; locked rooms are included.
        public static string[][] ListRooms(string host, int port, string game, int timeout)
        {
            using (TcpClient client = Connect(host, port, timeout))
            {
                client.ReceiveTimeout = timeout;
                NetworkStream stream = client.GetStream();
                WriteLine(stream, "RELAY|2|LIST|" + Clean(game, 32));
                string reply = ReadLine(stream, 16384);
                if (!reply.StartsWith("LIST|")) throw new IOException(Reason(reply));
                string[] rows = reply.Substring(5).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                string[][] rooms = new string[rows.Length][];
                for (int i = 0; i < rows.Length; i++)
                {
                    string[] f = rows[i].Split(new[] { ',' }, 5);
                    rooms[i] = f.Length == 5 ? f : new[] { f[0], "?", "?", "0", "" };
                }
                return rooms;
            }
        }

        // Public rooms for a game: each entry is { code, players, max, name }.
        public static string[][] List(string host, int port, string game, int timeout)
        {
            using (TcpClient client = Connect(host, port, timeout))
            {
                client.ReceiveTimeout = timeout;
                NetworkStream stream = client.GetStream();
                WriteLine(stream, "RELAY|1|LIST|" + Clean(game, 32));
                string reply = ReadLine(stream, 16384);
                if (!reply.StartsWith("LIST|")) throw new IOException(Reason(reply));
                string[] rows = reply.Substring(5).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                string[][] rooms = new string[rows.Length][];
                for (int i = 0; i < rows.Length; i++)
                {
                    string[] f = rows[i].Split(new[] { ',' }, 4);
                    rooms[i] = f.Length == 4 ? f : new[] { f[0], "?", "?", "" };
                }
                return rooms;
            }
        }
    }

    // Host side: the control connection that keeps the room open.
    public sealed class RelayHost : IDisposable
    {
        public readonly string Code;
        public readonly int MaxPlayers;
        readonly string host, game;
        readonly int port, timeout;
        readonly TcpClient control;
        readonly NetworkStream stream;
        readonly object writeGate = new object();
        volatile bool disposed;
        long lastHeard = DateTime.UtcNow.Ticks;

        public RelayHost(string host, int port, string game, string version, bool listed, string roomName, int timeout)
            : this(host, port, game, timeout, "RELAY|1|HOST|" + RelayLink.Clean(game, 32) + "|" + RelayLink.Clean(version, 32) + "|" + (listed ? "1" : "0") + "|" + RelayLink.Clean(roomName, 24)) { }

        // v2 (tunnel games): room size, password (hex SHA-256 or ""), and a handoff secret ("" = the room closes with its host).
        public RelayHost(string host, int port, string game, string version, bool listed, int maxPlayers, string passwordHash, string handoffSecret, string roomName, int timeout)
            : this(host, port, game, timeout, "RELAY|2|HOST|" + RelayLink.Clean(game, 32) + "|" + RelayLink.Clean(version, 48) + "|" + (listed ? "1" : "0") + "|" + maxPlayers + "|" + RelayLink.Clean(passwordHash, 128) + "|" + RelayLink.Clean(handoffSecret, 128) + "|" + RelayLink.Clean(roomName, 24)) { }

        // v2: take over a room whose host left, within the game's handoff window. Friends then rejoin the same code.
        public static RelayHost Resume(string host, int port, string game, string version, string code, string handoffSecret, string name, int timeout)
        {
            return new RelayHost(host, port, game, timeout, "RELAY|2|RESUME|" + RelayLink.Clean(game, 32) + "|" + RelayLink.Clean(version, 48) + "|" + RelayLink.Clean(code, 8).ToUpperInvariant() + "|" + RelayLink.Clean(handoffSecret, 128) + "|" + RelayLink.Clean(name, 24));
        }

        RelayHost(string host, int port, string game, int timeout, string handshake)
        {
            this.host = host; this.port = port; this.game = game; this.timeout = timeout;
            control = RelayLink.Connect(host, port, timeout);
            try
            {
                stream = control.GetStream();
                control.ReceiveTimeout = timeout;
                RelayLink.WriteLine(stream, handshake);
                string reply = RelayLink.ReadLine(stream, 512);
                if (!reply.StartsWith("ROOM|")) throw new IOException(RelayLink.Reason(reply));
                string[] f = reply.Split('|');
                Code = f[1];
                int max; MaxPlayers = f.Length > 2 && int.TryParse(f[2], out max) ? max : 4;
                control.ReceiveTimeout = 0;
            }
            catch { control.Close(); throw; }
        }

        // onFriend gets each friend's data connection (already linked; just read/write it).
        // onClosed(reason) runs once when the room ends (server gone, network lost) unless Dispose was called first.
        public void Start(Action<TcpClient> onFriend, Action<string> onClosed)
        {
            Thread reader = new Thread(delegate ()
            {
                string reason = "Lost the connection to the online server.";
                try
                {
                    while (!disposed)
                    {
                        string line = RelayLink.ReadLine(stream, 256);
                        Interlocked.Exchange(ref lastHeard, DateTime.UtcNow.Ticks);
                        if (line.StartsWith("OPEN|")) OpenData(line.Substring(5), onFriend);
                        else if (line.StartsWith("E|")) { reason = "Online server: " + line.Substring(2); break; }
                    }
                }
                catch (Exception) { }
                bool wasOpen = !disposed;
                Dispose();
                if (wasOpen && onClosed != null) onClosed(reason);
            }) { IsBackground = true, Name = "relay host control" };
            reader.Start();
            Thread pinger = new Thread(delegate ()
            {
                // keeps the room alive while nobody is connected, and notices a dead link
                for (int tick = 1; !disposed; tick++)
                {
                    Thread.Sleep(1000);
                    long quiet = (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastHeard)) / TimeSpan.TicksPerSecond;
                    if (quiet > 50) { control.Close(); break; }
                    if (tick % 15 == 0)
                        try { lock (writeGate) RelayLink.WriteLine(stream, "PING"); } catch (Exception) { control.Close(); break; }
                }
            }) { IsBackground = true, Name = "relay host keepalive" };
            pinger.Start();
        }

        void OpenData(string token, Action<TcpClient> onFriend)
        {
            Thread opener = new Thread(delegate ()
            {
                TcpClient data = null;
                try
                {
                    data = RelayLink.Connect(host, port, timeout);
                    RelayLink.WriteLine(data.GetStream(), "RELAY|1|DATA|" + RelayLink.Clean(token, 64));
                    if (disposed) { data.Close(); return; }
                    onFriend(data);
                }
                catch (Exception) { if (data != null) data.Close(); }
            }) { IsBackground = true, Name = "relay host data" };
            opener.Start();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { control.Close(); } catch (Exception) { }
        }
    }
}
