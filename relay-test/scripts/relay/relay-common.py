"""Shared parts of the mod relay: settings, the game list, safety limits, connections, rooms and mods-info.
Loaded by init.py, relay-flash.py and relay-unity.py."""
import argparse
import asyncio
import collections
import datetime
import json
import os
import random
import secrets
import socket
import sys
import time
import urllib.request

RELAY_VERSION = "1.1"  # 1.1: split into init.py, relay-common.py, relay-flash.py, relay-unity.py

# One entry per game. mode: "star" or "tunnel". max_players counts the host.
# framing (star only): "nul" (Flash XMLSocket / Socket + NUL) or "line" ("\n").
# versions: protocol versions a host may use ([] = any); a joiner must always
# match its host's version. idle: seconds without any traffic before a room closes.
GAMES = {
    "sh2": {"name": "Stealth Hunter 2 Online", "mode": "star", "max_players": 4, "framing": "nul",
            "versions": [], "idle": 300},
    "gm2": {"name": "Gun Mayhem 2 Online", "mode": "star", "max_players": 4, "framing": "nul",
            "versions": [], "idle": 300},
    "baldi": {"name": "Baldi Together", "mode": "tunnel", "max_players": 4, "versions": [], "idle": 300},
    "spooky": {"name": "Spooky Together", "mode": "tunnel", "max_players": 4, "versions": [], "idle": 300},
    "bic": {"name": "Bad Ice-Cream Online", "mode": "star", "max_players": 2, "framing": "nul",
            "versions": [], "idle": 300},
}
PROFILE_DEFAULTS = {"name": "", "mode": "star", "max_players": 4, "framing": "nul", "versions": [],
                    "idle": 300, "max_msg": 262144, "handoff": 0}

# Public-internet safety limits (all per connection unless noted).
LIMITS = {
    "max_conns": 2000,          # whole server
    "max_rooms": 1000,          # whole server
    "conns_per_ip": 32,
    "rooms_per_ip": 4,
    "handshake_timeout": 10.0,  # seconds to send the first line
    "handshake_max": 512,       # bytes in the first line
    "open_timeout": 10.0,       # tunnel: seconds for the host to open a friend's data connection
    "msg_rate": 600.0,          # star: messages per second (throttled, not dropped)
    "msg_burst": 2000.0,
    "byte_rate": 2_000_000.0,   # tunnel: bytes per second per direction (throttled)
    "byte_burst": 4_000_000.0,
    "max_buffer": 8_000_000,    # a receiver this far behind is dropped
    "join_fails": 20,           # wrong codes per IP per minute before refusing joins
    "control_max": 256,         # tunnel control line length
}

CODE_CHARS = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"  # no 0/O, 1/I
CODE_LEN = 5
POLICY_LINE = b"<policy-file-request/>"
POLICY = (b'<?xml version="1.0"?><cross-domain-policy>'
          b'<allow-access-from domain="*" to-ports="*"/></cross-domain-policy>\0')
QUIET = ("P|", "I|", "PL|", "p|", "q|", "r|", "WL|", "WA|", "F|", "Z|", "k|", "ks|", "HU|")

SIM_DELAY = 0.0   # testing only: one-way delay on star messages, seconds
SIM_JITTER = 0.0  # testing only: plus 0..jitter (order is kept)


def now_str():
    return datetime.datetime.now().strftime("%H:%M:%S")


def log(*a):
    print(now_str(), *a, flush=True)


def mask_ip(ip):
    """Logs show only part of an address (enough to tell players apart)."""
    if ":" in ip and "." not in ip:
        return ip.split(":")[0] + ":..."
    p = ip.split(".")
    return ".".join(p[:2] + ["x", "x"]) if len(p) == 4 else "?"


def clean(text, limit=24):
    """Names and labels: no separators or control characters."""
    s = "".join(ch for ch in text if ch.isprintable() and ch not in "|;,\0\r\n")
    return s.strip()[:limit]


class Bucket:
    """Token bucket that throttles (sleeps) instead of dropping."""

    def __init__(self, rate, burst):
        self.rate, self.burst = rate, burst
        self.tokens = burst
        self.t = time.monotonic()
        self.throttled = 0

    async def take(self, n):
        t = time.monotonic()
        self.tokens = min(self.burst, self.tokens + (t - self.t) * self.rate)
        self.t = t
        self.tokens -= n
        if self.tokens < 0:
            self.throttled += 1
            await asyncio.sleep(-self.tokens / self.rate)


class Conn:
    def __init__(self, reader, writer, ip, term):
        self.reader, self.writer, self.ip = reader, writer, ip
        self.term = term          # b"\0" or b"\n": how this client ends its lines
        self.id = -1
        self.name = "Player"
        self.role = None          # "host" | "guest" | None (legacy, not yet decided)
        self.room = None
        self.closed = False
        self.last_at = 0.0

    def send(self, text):
        self.send_bytes(text.encode("utf-8", "replace") + self.term)

    def send_bytes(self, data):
        if self.closed:
            return
        if SIM_DELAY or SIM_JITTER:
            loop = asyncio.get_running_loop()
            # strictly increasing: asyncio doesn't keep FIFO order for equal call_at times
            at = max(loop.time() + SIM_DELAY + random.uniform(0, SIM_JITTER), self.last_at + 1e-6)
            self.last_at = at
            loop.call_at(at, self._write, data)
        else:
            self._write(data)

    def _write(self, data):
        if self.closed or self.writer.is_closing():
            return
        try:
            if self.writer.transport.get_write_buffer_size() > LIMITS["max_buffer"]:
                log(f"dropping a client that stopped reading ({mask_ip(self.ip)})")
                self.close()
                return
            self.writer.write(data)
        except Exception:
            self.close()

    def error(self, reason):
        self.send("E|" + reason)
        self.close_soon()

    def close_soon(self):
        """Close once everything queued (including simulated delay) is written."""
        if SIM_DELAY or SIM_JITTER:
            loop = asyncio.get_running_loop()
            loop.call_at(max(loop.time(), self.last_at) + 0.01, self.close)
        else:
            self.close()

    def close(self):
        if not self.closed:
            self.closed = True
            try:
                self.writer.close()  # flushes what is buffered, then closes
            except Exception:
                pass


class Room:
    def __init__(self, code, game, profile, host, public, name, version):
        self.code, self.game, self.profile = code, game, profile
        self.mode = profile["mode"]
        self.host = host
        self.public, self.name, self.version = public, name, version
        self.max_players = profile["max_players"]
        self.password = ""        # v2: opaque hash; "" = open
        self.secret = ""          # v2: handoff secret; "" = the room closes when its host leaves
        self.handoff_until = 0.0  # v2: monotonic deadline while waiting for a teammate to re-host
        self.guests = {}          # star: id -> Conn
        self.next_id = 1
        self.pending = {}         # tunnel: token -> future
        self.pipes = set()        # tunnel: (friend Conn, host data Conn)
        self.last_active = time.monotonic()
        self.created = time.monotonic()
        self.closed = False

    @property
    def in_handoff(self):
        return self.host is None and self.handoff_until > 0

    @property
    def players(self):
        if self.in_handoff:
            return 0
        if self.mode == "star":
            return (1 if self.host else 0) + len(self.guests)
        return 1 + len(self.pipes) + len(self.pending)

    def touch(self):
        self.last_active = time.monotonic()

    def label(self):
        return f"room {self.code} [{self.game}]"


MODS_INFO_URL = "https://raw.githubusercontent.com/multiplayereverything/mods-info/main/"
MODS_INFO_REFRESH = 600.0  # seconds


GAME_ID_CHARS = set("abcdefghijklmnopqrstuvwxyz0123456789-")


def mods_info_profile(doc):
    """A game profile from a mods/<id>.json, or None if its relay block isn't usable."""
    relay = doc.get("relay")
    gid = doc.get("id")
    if not isinstance(relay, dict) or not isinstance(gid, str) or not gid or len(gid) > 32:
        return None
    if gid[0] == "-" or not set(gid) <= GAME_ID_CHARS:
        return None
    mode, players = relay.get("mode"), relay.get("max_players")
    if mode not in ("star", "tunnel") or not isinstance(players, int) or isinstance(players, bool) or not 2 <= players <= 16:
        return None
    name = doc.get("name") if isinstance(doc.get("name"), str) else ""
    return {"name": name[:80], "mode": mode, "max_players": players}


class ModsInfo:
    """The mods-info files, fetched in the background and kept in memory as one-line JSON (served to clients with
    INFO), and the source of the relay's game whitelist: every mod in index.json with a usable relay block. Clients
    apply the full file rules (mods-info/tools/validate.py); the relay only needs the relay blocks to be sane."""

    def __init__(self, base, relay, local):
        self.base = base if base.endswith("/") else base + "/"
        self.relay = relay
        self.local = local        # games.json entries: always win
        self.servers = None
        self.mods = {}            # game id -> one-line JSON of its mods/<id>.json

    @staticmethod
    def fetch(url):
        req = urllib.request.Request(url, headers={"User-Agent": "mod-relay/" + RELAY_VERSION})
        with urllib.request.urlopen(req, timeout=10) as r:
            data = r.read(65537)
        if len(data) > 65536:
            raise ValueError("file too large")
        doc = json.loads(data.decode("utf-8"))
        if not isinstance(doc, dict):
            raise ValueError("not a JSON object")
        return json.dumps(doc, separators=(",", ":"), ensure_ascii=True)

    async def refresh(self):
        loop = asyncio.get_running_loop()
        try:
            self.servers = await loop.run_in_executor(None, self.fetch, self.base + "servers.json")
        except Exception as ex:
            log(f"mods-info: can't fetch servers.json ({ex}); keeping {'the last copy' if self.servers else 'none'}")
            return
        try:
            index = json.loads(await loop.run_in_executor(None, self.fetch, self.base + "index.json"))
            ids = sorted({m["id"] for m in index.get("mods", []) if isinstance(m, dict) and isinstance(m.get("id"), str)
                          and m["id"] and set(m["id"]) <= GAME_ID_CHARS})
        except Exception as ex:
            log(f"mods-info: can't fetch index.json ({ex}); checking the games already known")
            ids = sorted(self.relay.games)
        results = await asyncio.gather(*[loop.run_in_executor(None, self.fetch, self.base + f"mods/{g}.json") for g in ids],
                                       return_exceptions=True)
        mods, profiles = {}, {}
        for game, result in zip(ids, results):
            if not isinstance(result, str):
                continue
            mods[game] = result
            doc = json.loads(result)
            prof = mods_info_profile(doc)
            if prof is not None and doc.get("id") == game:
                profiles[game] = prof
            else:
                log(f"mods-info: mods/{game}.json has no usable relay block; not added to the game list")
        self.mods = mods
        self.apply(profiles)
        log(f"mods-info: refreshed from {self.base} ({', '.join(sorted(mods)) or 'no mod files'})")

    def apply(self, profiles):
        """New whitelist: built-in GAMES, then mods-info profiles, then games.json. Rooms already open keep theirs."""
        games = {k: dict(v) for k, v in GAMES.items()}
        for gid, prof in profiles.items():
            games[gid] = dict(games.get(gid, {}), **prof)
        for gid, prof in self.local.items():
            games[gid] = dict(games.get(gid, {}), **prof)
        old = self.relay.games
        added, removed = sorted(set(games) - set(old)), sorted(set(old) - set(games))
        changed = sorted(g for g in set(games) & set(old) if games[g] != old[g])
        self.relay.games = games
        if added or removed or changed:
            now = ", ".join("%s (%s)" % (g, p.get("mode", "star")) for g, p in sorted(games.items()))
            log("games: " + ", ".join(["+" + g for g in added] + ["-" + g for g in removed] + ["~" + g for g in changed])
                + "  (now " + now + ")")

    async def refresh_forever(self):
        while True:
            await self.refresh()
            await asyncio.sleep(MODS_INFO_REFRESH)

    def send(self, c, game):
        mod = self.mods.get(game)
        if self.servers is None or mod is None:
            c.error(f"No mods-info for '{clean(game, 32)}' on this server yet")
            return
        c.send("INFO|servers|" + self.servers)
        c.send("INFO|mod|" + mod)


def load_local_games(path):
    """games.json next to the relay: {id: profile keys}, merged over everything else."""
    local = {}
    if path and os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            for gid, prof in json.load(f).items():
                local[gid.lower()] = dict(prof)
    return local


def load_games(path, local=None):
    games = {k: dict(v) for k, v in GAMES.items()}
    for gid, prof in (load_local_games(path) if local is None else local).items():
        games[gid] = dict(games.get(gid, {}), **prof)
    for gid, prof in games.items():
        if prof.get("mode", "star") not in ("star", "tunnel"):
            sys.exit(f"game '{gid}': mode must be star or tunnel")
    return games


def port_busy(port):
    with socket.socket() as so:
        so.settimeout(0.5)
        return so.connect_ex(("127.0.0.1", port)) == 0
