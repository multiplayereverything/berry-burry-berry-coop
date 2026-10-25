#!/usr/bin/env python3
"""Global mod relay: online play for every multiplayer mod on one TCP port.

A host creates a room for a game and gets a short room code; friends join with
the game id and that code. Rooms are isolated, so many groups and games share
one server. Python 3.7+, standard library only.

Two room modes:
  star    the old SH2/GM2 relay inside a room. Messages are NUL-terminated
          text. Guest -> host is wrapped as "G|<id>|<msg>", host -> guests is
          forwarded unchanged. The relay sends "W|<id>" (welcome),
          "J|<id>|<name>" and "L|<id>" (to the host), "X" (host left) and
          "E|<reason>" (error, then the connection closes).
  tunnel  raw TCP for games with their own protocol (Baldi, Spooky). The host
          keeps a control connection; for each friend the relay asks it to
          open a data connection, then pipes bytes both ways untouched.

Handshake: the first line a client sends, ended by "\\n" or "\\0" (replies use
the same ending). Version 1:
  RELAY|1|HOST|<game>|<version>|<public 0/1>|<room name>
      star:   -> ROOM|<code>|<max players>  then W|0, then the star protocol
      tunnel: -> ROOM|<code>|<max players>  then OPEN|<token> per friend;
                 the host may send PING (-> PONG) to keep the room alive
  RELAY|1|JOIN|<game>|<version>|<code>|<player name>
      star:   -> W|<id>, then the star protocol
      tunnel: -> OK, then raw bytes to and from the host
  RELAY|1|DATA|<token>        tunnel host's data connection for one friend
  RELAY|1|LIST|<game>         -> LIST|<code>,<players>,<max>,<name>;...  (public rooms)
  RELAY|1|INFO|<game>         -> INFO|servers|<servers.json> and INFO|mod|<mods/<game>.json>, one line each:
                                 the game's mods-info (github.com/multiplayereverything/mods-info), for clients
                                 that can't fetch it over HTTP themselves (Flash Player). The relay refreshes it
                                 every 10 minutes; E|reason when it has none.
  Any refusal is "E|<reason>" and the connection closes.
Version 2 (tunnel games; v1 keeps working) adds a password, a host-chosen size and host handoff:
  RELAY|2|HOST|<game>|<version>|<public>|<max players>|<password hash or empty>|<handoff secret or empty>|<room name>
                 -> ROOM|<code>|<max players>, then the tunnel host protocol
  RELAY|2|JOIN|<game>|<version>|<code>|<password hash or empty>|<player name>
                 -> OK, or E|<reason>. While a room changes host the reason starts "HANDOFF:" (retry shortly).
  RELAY|2|RESUME|<game>|<version>|<code>|<handoff secret>|<player name>
                 a teammate re-hosts the same room within the game's "handoff" seconds after the host left
                 -> ROOM|<code>|<max players>, then the tunnel host protocol
  RELAY|2|LIST|<game>  -> LIST|<code>,<players>,<max>,<locked 0/1>,<name>;...
The password hash and the secret are opaque to the relay (the mods send hex SHA-256) and compared exactly.
Old clients that start with "H|name" / "J|name" (no handshake) share one
default star room, exactly like server/sh2_relay.py and gm2_relay.py (--no-legacy
turns that off). Flash's "<policy-file-request/>" is answered on the same port.

Usage:
  python init.py                      port from $SERVER_PORT (Kinetic/Pterodactyl), else 7790
  python init.py --port 7790 --log    local run, with traffic and game debug lines
Which games the relay serves (the whitelist), each with its settings, comes from three places, later ones winning:
  1. the GAMES table below (built in, so the known games work even without internet);
  2. mods-info: every mod listed in index.json whose mods/<id>.json has a "relay" block (mode, max_players), refreshed
     every 10 minutes, so adding a mod is a push to mods-info with no change or restart here;
  3. games.json next to this file (same keys as GAMES), for local overrides and settings mods-info doesn't carry.
See ADDING_A_MOD.md.
"""

import importlib.util
import os
import sys


def _load(name, filename):
    """Load a sibling relay file (the hyphenated names can't be imported the normal way), once."""
    if name not in sys.modules:
        spec = importlib.util.spec_from_file_location(name, os.path.join(os.path.dirname(os.path.abspath(__file__)), filename))
        module = importlib.util.module_from_spec(spec)
        sys.modules[name] = module
        spec.loader.exec_module(module)
    return sys.modules[name]


def _use(module):
    """Make the module's names usable here unqualified (constants, classes, helpers, its imports)."""
    globals().update({k: v for k, v in vars(module).items() if not k.startswith("__") and k not in ("_load", "_use")})


common = _load("relay_common", "relay-common.py")
flash = _load("relay_flash", "relay-flash.py")
unity = _load("relay_unity", "relay-unity.py")
for _m in (common, flash, unity):
    _use(_m)


class Relay(flash.FlashRooms, unity.UnityRooms):
    """The relay: one port for every game. Reads each connection's first line and hands it to the Flash
    (star) or Unity (tunnel) rooms by the game's mode; owns room codes, the room list and clean-up."""

    def __init__(self, games, log_traffic=False, legacy=True, idle_override=None):
        self.games = games
        self.log_traffic = log_traffic
        self.legacy_enabled = legacy
        self.idle_override = idle_override
        self.info = None          # ModsInfo, unless --no-mods-info
        self.rooms = {}           # code -> Room
        self.tokens = {}          # tunnel token -> Room
        self.legacy = None        # the default star room for old H|/J| clients
        self.conns_by_ip = collections.Counter()
        self.total_conns = 0
        self.join_fails = collections.defaultdict(collections.deque)

    async def handle(self, reader, writer):
        peer = writer.get_extra_info("peername") or ("?", 0)
        ip = str(peer[0])
        if self.total_conns >= LIMITS["max_conns"] or self.conns_by_ip[ip] >= LIMITS["conns_per_ip"]:
            writer.close()
            return
        self.total_conns += 1
        self.conns_by_ip[ip] += 1
        sock = writer.get_extra_info("socket")
        if sock is not None:
            try:
                # per-frame messages are tiny; don't let Nagle batch them
                sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            except OSError:
                pass
        c = None
        try:
            try:
                line, term, rest = await asyncio.wait_for(self.first_line(reader), LIMITS["handshake_timeout"])
            except (asyncio.TimeoutError, ValueError, ConnectionError, OSError, asyncio.IncompleteReadError):
                return
            if line == POLICY_LINE:
                writer.write(POLICY)
                await writer.drain()
                return
            c = Conn(reader, writer, ip, term)
            text = line.decode("utf-8", "replace").strip("\r")
            parts = text.split("|")
            if parts[0] == "RELAY":
                await self.command(c, parts, rest)
            elif parts[0] in ("H", "J", "D") and self.legacy_enabled:
                await self.legacy_session(c, text, rest)
            else:
                c.error("Unknown handshake: update the mod, or check the server address")
        except (ConnectionError, OSError, asyncio.IncompleteReadError):
            pass
        except Exception as ex:  # one bad client must never take the server down
            log(f"error with a client ({mask_ip(ip)}): {ex!r}")
        finally:
            self.total_conns -= 1
            self.conns_by_ip[ip] -= 1
            if self.conns_by_ip[ip] <= 0:
                del self.conns_by_ip[ip]
            if c is not None:
                c.close_soon()  # replies the lag simulator still holds (LIST, E|) go out first
            else:
                writer.close()

    async def first_line(self, reader):
        buf = b""
        while True:
            cut = [i for i in (buf.find(b"\0"), buf.find(b"\n")) if i >= 0]
            if cut:
                i = min(cut)
                return buf[:i], buf[i:i + 1], buf[i + 1:]
            if len(buf) > LIMITS["handshake_max"]:
                raise ValueError("handshake too long")
            data = await reader.read(1024)
            if not data:
                raise ConnectionError("closed during handshake")
            buf += data

    def profile(self, game):
        p = self.games.get(game)
        if p is None:
            return None
        prof = dict(PROFILE_DEFAULTS, **p)
        if self.idle_override is not None:
            prof["idle"] = self.idle_override
        return prof

    async def command(self, c, parts, rest):
        if len(parts) < 4 or parts[1] not in ("1", "2"):
            c.error("Unsupported relay handshake version (this server speaks RELAY|1 and RELAY|2)")
            return
        if parts[1] == "2":
            await self.command_v2(c, parts, rest)
            return
        verb = parts[2]
        if verb == "DATA":
            await self.tunnel_data(c, parts[3], rest)
            return
        game = parts[3].strip().lower()
        prof = self.profile(game)
        if prof is None:
            c.error(f"Unknown game '{clean(game, 32)}' on this server")
            return
        if verb == "LIST":
            self.send_list(c, game)
            c.close_soon()
            return
        if verb == "INFO":
            if self.info is None:
                c.error("This server doesn't serve mods-info")
            else:
                self.info.send(c, game)
            c.close_soon()
            return
        if verb not in ("HOST", "JOIN") or len(parts) < 7:
            c.error("Bad handshake")
            return
        version = clean(parts[4], 32)
        if verb == "HOST":
            if prof["versions"] and version not in prof["versions"]:
                c.error(f"This server doesn't support version {version} of {prof['name'] or game}; update the mod")
                return
            c.name = clean("|".join(parts[6:])) or "Host"
            room = self.create_room(c, game, prof, parts[5].strip() == "1", c.name, version)
            if room is None:
                return
            if room.mode == "star":
                await self.star_host(c, room, rest)
            else:
                await self.tunnel_host(c, room, rest)
        else:
            code = parts[5].strip().upper().replace("0", "O").replace("1", "I")
            c.name = clean("|".join(parts[6:])) or "Player"
            room = self.find_room(c, game, code, version)
            if room is None:
                return
            if room.mode == "star":
                await self.star_guest(c, room, rest)
            else:
                await self.tunnel_join(c, room)

    def new_code(self):
        for _ in range(1000):
            code = "".join(secrets.choice(CODE_CHARS) for _ in range(CODE_LEN))
            if code not in self.rooms:
                return code
        raise RuntimeError("no free room code")

    def create_room(self, c, game, prof, public, name, version, max_players=None):
        if len(self.rooms) >= LIMITS["max_rooms"]:
            c.error("The server is full right now, try again later")
            return None
        mine = sum(1 for r in self.rooms.values() if r.host is not None and r.host.ip == c.ip)
        if mine >= LIMITS["rooms_per_ip"]:
            c.error(f"Too many rooms from your address (limit {LIMITS['rooms_per_ip']})")
            return None
        room = Room(self.new_code(), game, prof, c, public, name, version)
        if max_players is not None:
            room.max_players = max_players
        self.rooms[room.code] = room
        c.role, c.room, c.id = "host", room, 0
        log(f"{room.label()} opened by '{c.name}' ({mask_ip(c.ip)}), {'public' if public else 'private'}, "
            f"version '{version}', mode {room.mode}  [rooms: {len(self.rooms)}]")
        c.send(f"ROOM|{room.code}|{room.max_players}")
        return room

    def fail_join(self, c, reason):
        q = self.join_fails[c.ip]
        q.append(time.monotonic())
        c.error(reason)

    def find_room(self, c, game, code, version, password=None):
        q = self.join_fails[c.ip]
        t = time.monotonic()
        while q and t - q[0] > 60:
            q.popleft()
        if not q:
            self.join_fails.pop(c.ip, None)
        elif len(q) >= LIMITS["join_fails"]:
            c.error("Too many wrong room codes; wait a minute and try again")
            return None
        room = self.rooms.get(code)
        if room is None or room.closed:
            self.fail_join(c, f"No room with code {clean(code, 8)} (check the code, or the host closed it)")
            return None
        if room.game != game:
            self.fail_join(c, f"Room {code} is for another game")
            return None
        if room.version and version != room.version:
            c.error(f"Version mismatch: the host runs '{room.version}', you run '{version}'. Use the same mod build.")
            return None
        if room.password and password != room.password:
            # v1 clients send no password, so they can't enter a locked room either
            self.fail_join(c, "Wrong password" if password else "This room needs a password; update the mod")
            return None
        if room.in_handoff:
            c.error("HANDOFF: the host left and a teammate is taking over; retry in a moment")
            return None
        if room.players >= room.max_players:
            c.error(f"Room {code} is full ({room.max_players} players)")
            return None
        return room

    def close_room(self, room, why):
        if room.closed:
            return
        room.closed = True
        if self.rooms.get(room.code) is room:
            del self.rooms[room.code]
        if room.mode == "star":
            for g in list(room.guests.values()):
                g.send("X")
                g.close_soon()
            room.guests.clear()
        else:
            for token, fut in list(room.pending.items()):
                self.tokens.pop(token, None)
                if not fut.done():
                    fut.cancel()
            room.pending.clear()
            for a, b in list(room.pipes):
                a.close()
                release(b)
            room.pipes.clear()
        if room.host is not None:
            room.host.close_soon()
        log(f"{room.label()} closed: {why}  [rooms: {len(self.rooms)}]")

    def send_list(self, c, game, v2=False):
        rows = []
        for r in sorted(self.rooms.values(), key=lambda r: r.created):
            if r.game == game and r.public and not r.closed and not r.in_handoff:
                if v2:
                    rows.append(f"{r.code},{r.players},{r.max_players},{1 if r.password else 0},{clean(r.name)}")
                elif not r.password:  # v1 clients can't enter locked rooms, so they don't see them
                    rows.append(f"{r.code},{r.players},{r.max_players},{clean(r.name)}")
        c.send("LIST|" + ";".join(rows[:50]))

    async def sweep(self):
        """Close rooms that went quiet (dead host, abandoned lobby)."""
        last_stats = time.monotonic()
        while True:
            await asyncio.sleep(2)
            t = time.monotonic()
            for room in list(self.rooms.values()):
                if room.in_handoff:
                    if t > room.handoff_until:
                        self.close_room(room, "nobody took over after the host left")
                elif t - room.last_active > room.profile["idle"]:
                    self.close_room(room, f"idle for {int(room.profile['idle'])} s")
            if t - last_stats > 600:
                last_stats = t
                log(f"status: {len(self.rooms)} rooms, {self.total_conns} connections")


async def main_async(args):
    # testing knobs live in relay-common.py, where the code that reads them is
    common.SIM_DELAY, common.SIM_JITTER = args.sim_delay / 1000, args.sim_jitter / 1000
    if args.mods_info_refresh:
        common.MODS_INFO_REFRESH = args.mods_info_refresh
    for key in ("msg_rate", "msg_burst", "byte_rate", "byte_burst", "rooms_per_ip", "join_fails",
                "handshake_timeout", "open_timeout"):
        val = getattr(args, key)
        if val is not None:
            LIMITS[key] = val
    local = load_local_games(args.games)
    games = load_games(args.games, local)
    relay = Relay(games, log_traffic=args.log, legacy=not args.no_legacy, idle_override=args.idle)
    if port_busy(args.port):
        sys.exit(f"port {args.port} is already in use (another relay or game?): not starting")
    server = await asyncio.start_server(relay.handle, args.bind, args.port, reuse_address=(os.name != "nt"))
    public = os.environ.get("SERVER_IP", "")
    log(f"mod relay {RELAY_VERSION} listening on {args.bind}:{args.port}"
        + (f"  (public address {public}:{args.port})" if public else ""))
    log("games: " + ", ".join(f"{g} ({p.get('mode', 'star')})" for g, p in sorted(games.items()))
        + ("; legacy H|/J| room on" if not args.no_legacy else ""))
    sweeper = asyncio.ensure_future(relay.sweep())
    info_task = None
    if not args.no_mods_info:
        relay.info = ModsInfo(args.mods_info, relay, local)
        info_task = asyncio.ensure_future(relay.info.refresh_forever())
    async with server:
        try:
            await server.serve_forever()
        finally:
            sweeper.cancel()
            if info_task:
                info_task.cancel()


def main():
    try:
        sys.stdout.reconfigure(errors="replace")
    except Exception:
        pass
    env_port = os.environ.get("SERVER_PORT", "").strip()
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, default=int(env_port) if env_port.isdigit() else 7790,
                    help="TCP port (default: $SERVER_PORT, else 7790)")
    ap.add_argument("--bind", default="0.0.0.0", help="address to listen on (default all)")
    ap.add_argument("--log", action="store_true", help="print relayed messages and games' debug (D|) lines")
    ap.add_argument("--games", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "games.json"),
                    help="extra game profiles (default games.json next to relay.py, if present)")
    ap.add_argument("--no-legacy", action="store_true", help="refuse old clients that send H|/J| without a room")
    ap.add_argument("--mods-info", default=MODS_INFO_URL, help="base URL of mods-info, served to clients with RELAY|1|INFO")
    ap.add_argument("--no-mods-info", action="store_true", help="don't fetch or serve mods-info")
    ap.add_argument("--mods-info-refresh", type=float, help="testing: seconds between mods-info refreshes (default 600)")
    ap.add_argument("--idle", type=float, help="testing: idle timeout in seconds for every game")
    ap.add_argument("--sim-delay", type=float, default=0, help="testing: add N ms one-way delay (star)")
    ap.add_argument("--sim-jitter", type=float, default=0, help="testing: add 0..N ms random delay (star)")
    ap.add_argument("--msg-rate", type=float, help="star messages per second per connection")
    ap.add_argument("--msg-burst", type=float)
    ap.add_argument("--byte-rate", type=float, help="tunnel bytes per second per direction")
    ap.add_argument("--byte-burst", type=float)
    ap.add_argument("--rooms-per-ip", type=int)
    ap.add_argument("--join-fails", type=int, help="wrong codes per IP per minute")
    ap.add_argument("--handshake-timeout", type=float, help="testing: seconds allowed for the first line")
    ap.add_argument("--open-timeout", type=float, help="testing: seconds a tunnel host has to open a data connection")
    args = ap.parse_args()
    try:
        asyncio.run(main_async(args))
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
