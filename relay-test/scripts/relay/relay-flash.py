"""Flash games (star mode): one host, guests send through the relay. The old relay protocol inside a room:
guest -> host as G|<id>|<msg>, host -> every guest unchanged, plus W|id, J|id|name, L|id, X, E|reason.
Also the legacy default room for old clients that start with H|name / J|name."""

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


_use(_load("relay_common", "relay-common.py"))


class FlashRooms:
    """Star rooms; mixed into init.Relay."""

    async def star_host(self, c, room, rest):
        c.send("W|0")
        await self.star_loop(c, room, rest)

    async def star_guest(self, c, room, rest):
        c.role, c.room, c.id = "guest", room, room.next_id
        room.next_id += 1
        room.guests[c.id] = c
        room.touch()
        log(f"{room.label()} '{c.name}' joined as id {c.id} ({mask_ip(c.ip)})  [{room.players}/{room.profile['max_players']}]")
        c.send(f"W|{c.id}")
        if room.host:
            room.host.send(f"J|{c.id}|{c.name}")
        await self.star_loop(c, room, rest)

    async def star_loop(self, c, room, rest):
        prof = room.profile
        sep = b"\0" if prof["framing"] == "nul" else b"\n"
        bucket = Bucket(LIMITS["msg_rate"], LIMITS["msg_burst"])
        buf = rest
        try:
            while not c.closed:
                while True:
                    i = buf.find(sep)
                    if i < 0:
                        break
                    raw, buf = buf[:i], buf[i + 1:]
                    raw = raw.strip(b"\r\n")
                    if raw:
                        await bucket.take(1)
                        self.star_msg(c, c.room if c.room is not None else room, raw.decode("utf-8", "replace"))
                        if c.closed:
                            return
                if len(buf) > prof["max_msg"]:
                    c.error(f"Message too large (limit {prof['max_msg']} bytes)")
                    return
                data = await c.reader.read(65536)
                if not data:
                    break
                buf += data
        finally:
            if bucket.throttled and self.log_traffic:
                log(f"{room.label()} {c.role}{c.id} was throttled {bucket.throttled} times")
            self.star_drop(c)

    def star_msg(self, c, room, msg):
        if msg.startswith("D|"):  # debug line from a game
            if self.log_traffic:
                # "<code> [host0] text": the same "[host0]" tag as the old relays (GM2's net_test parses it)
                log(f"{room.code} [{c.role or '?'}{c.id}] {msg[2:]}")
            return
        if c.role is None:  # legacy default room: the first H|/J| decides
            self.legacy_role(c, room, msg)
            return
        room.touch()
        if self.log_traffic and not msg.startswith(QUIET):
            log(f"{room.code} [{c.role}{c.id}] {msg[:200]}")
        if c.role == "host":
            for g in list(room.guests.values()):
                g.send(msg)
        elif room.host is not None:
            room.host.send(f"G|{c.id}|{msg}")

    def star_drop(self, c):
        room = c.room
        if room is None:
            return
        c.room = None
        if room is self.legacy:
            self.legacy_drop(c, room)
            return
        if c.role == "host" and room.host is c:
            self.close_room(room, f"host '{c.name}' left")
        elif c.role == "guest" and room.guests.get(c.id) is c:
            del room.guests[c.id]
            log(f"{room.label()} '{c.name}' (id {c.id}) left  [{room.players}/{room.profile['max_players']}]")
            if room.host is not None:
                room.host.send(f"L|{c.id}")

    async def legacy_session(self, c, first, rest):
        if self.legacy is None:
            prof = dict(PROFILE_DEFAULTS, name="legacy room", mode="star", max_players=16, idle=10 ** 9)
            self.legacy = Room("LEGACY", "legacy", prof, None, False, "legacy", "")
        room = self.legacy
        c.room = room
        await self.star_loop(c, room, first.encode("utf-8") + c.term + rest)

    def legacy_role(self, c, room, msg):
        kind, _, name = msg.partition("|")
        c.name = clean(name) or "Player"
        if kind == "H":
            if room.host is not None:
                c.send("E|A host is already connected to this server")
                return
            c.role, c.id = "host", 0
            room.host = c
            log(f"legacy room: host '{c.name}' connected ({mask_ip(c.ip)})")
            c.send("W|0")
            for g in room.guests.values():
                c.send(f"J|{g.id}|{g.name}")
        elif kind == "J":
            if len(room.guests) + 1 >= room.profile["max_players"]:
                c.error("The server is full")
                return
            c.role, c.id = "guest", room.next_id
            room.next_id += 1
            room.guests[c.id] = c
            log(f"legacy room: friend '{c.name}' (id {c.id}) connected ({mask_ip(c.ip)})")
            c.send(f"W|{c.id}")
            if room.host:
                room.host.send(f"J|{c.id}|{c.name}")

    def legacy_drop(self, c, room):
        # as the old relay: guests stay connected after "X"; they leave by themselves
        if c.role == "host" and room.host is c:
            room.host = None
            log(f"legacy room: host '{c.name}' left")
            for g in room.guests.values():
                g.send("X")
        elif c.role == "guest" and room.guests.get(c.id) is c:
            del room.guests[c.id]
            log(f"legacy room: friend '{c.name}' left")
            if room.host:
                room.host.send(f"L|{c.id}")
