"""Unity and other TCP games (tunnel mode): the host keeps a control connection; for each friend the relay asks it
to open a data connection and then pipes raw bytes both ways (like playit.gg). Also RELAY|2 (tunnel only):
room password, host-chosen size and host handoff."""

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


class UnityRooms:
    """Tunnel rooms and RELAY|2; mixed into init.Relay."""

    async def command_v2(self, c, parts, rest):
        verb = parts[2]
        game = parts[3].strip().lower()
        prof = self.profile(game)
        if prof is None:
            c.error(f"Unknown game '{clean(game, 32)}' on this server")
            return
        if prof["mode"] != "tunnel":
            c.error("RELAY|2 is for tunnel games")
            return
        if verb == "LIST":
            self.send_list(c, game, v2=True)
            c.close_soon()
            return
        minimum = {"HOST": 10, "JOIN": 8, "RESUME": 8}.get(verb)
        if minimum is None or len(parts) < minimum:
            c.error("Bad handshake")
            return
        version = clean(parts[4], 48)
        if prof["versions"] and version not in prof["versions"]:
            c.error(f"This server doesn't support version {version} of {prof['name'] or game}; update the mod")
            return
        if verb == "HOST":
            try:
                size = int(parts[6])
            except ValueError:
                c.error("Bad handshake")
                return
            c.name = clean("|".join(parts[9:])) or "Host"
            room = self.create_room(c, game, prof, parts[5].strip() == "1", c.name, version,
                                    max_players=max(2, min(prof["max_players"], size)))
            if room is None:
                return
            room.password = parts[7].strip()[:128]
            room.secret = parts[8].strip()[:128] if prof["handoff"] > 0 else ""
            await self.tunnel_host(c, room, rest)
            return
        code = parts[5].strip().upper().replace("0", "O").replace("1", "I")
        c.name = clean("|".join(parts[7:])) or "Player"
        if verb == "RESUME":
            room = self.rooms.get(code)
            if room is None or room.closed or room.game != game or not room.in_handoff:
                self.fail_join(c, f"Room {clean(code, 8)} isn't waiting for a new host")
                return
            if not room.secret or parts[6].strip() != room.secret or version != room.version:
                self.fail_join(c, "Not allowed to take over this room")
                return
            room.host, room.handoff_until = c, 0.0
            c.role, c.room, c.id = "host", room, 0
            room.touch()
            log(f"{room.label()} taken over by '{c.name}' ({mask_ip(c.ip)})")
            c.send(f"ROOM|{room.code}|{room.max_players}")
            await self.tunnel_host(c, room, rest)
            return
        room = self.find_room(c, game, code, version, password=parts[6].strip())
        if room is None:
            return
        await self.tunnel_join(c, room)

    async def tunnel_host(self, c, room, rest):
        buf = rest
        try:
            while not c.closed and not room.closed:
                while b"\n" in buf or b"\0" in buf:
                    cut = min(i for i in (buf.find(b"\n"), buf.find(b"\0")) if i >= 0)
                    line, buf = buf[:cut].strip(b"\r"), buf[cut + 1:]
                    room.touch()
                    if line == b"PING":
                        c.send("PONG")
                if len(buf) > LIMITS["control_max"]:
                    c.error("Control line too long")
                    return
                data = await c.reader.read(4096)
                if not data:
                    break
                buf += data
        finally:
            if room.secret and not room.closed and room.host is c:
                self.start_handoff(room, c)
            else:
                self.close_room(room, f"host '{c.name}' left")

    def start_handoff(self, room, old_host):
        """v2: keep the code reserved so a teammate can RESUME; every current link ends (the host is gone)."""
        room.host = None
        room.handoff_until = time.monotonic() + room.profile["handoff"]
        for token, fut in list(room.pending.items()):
            self.tokens.pop(token, None)
            if not fut.done():
                fut.cancel()
        room.pending.clear()
        for a, b in list(room.pipes):
            a.close()
            release(b)
        room.pipes.clear()
        old_host.close_soon()
        log(f"{room.label()} host '{old_host.name}' left; waiting {room.profile['handoff']} s for a teammate to take over")

    async def tunnel_join(self, c, room):
        token = secrets.token_hex(12)
        fut = asyncio.get_running_loop().create_future()
        room.pending[token] = fut
        self.tokens[token] = room
        room.touch()
        room.host.send(f"OPEN|{token}")
        try:
            data_conn, data_rest = await asyncio.wait_for(fut, LIMITS["open_timeout"])
        except (asyncio.TimeoutError, asyncio.CancelledError):
            if fut.done() and not fut.cancelled():
                release(fut.result()[0])  # arrived just as the wait ran out
            if not room.closed:
                c.error("The host didn't answer (their game may be busy or closed)")
            else:
                c.error("The host closed the room")
            return
        finally:
            room.pending.pop(token, None)
            self.tokens.pop(token, None)
        if room.closed:
            release(data_conn)
            c.error("The host closed the room")
            return
        pair = (c, data_conn)
        room.pipes.add(pair)
        c.role, c.room = "guest", room
        log(f"{room.label()} '{c.name}' connected ({mask_ip(c.ip)})  [{room.players}/{room.profile['max_players']}]")
        c.send("OK")
        try:
            up = asyncio.ensure_future(self.pump(c.reader, data_conn, room, b""))
            down = asyncio.ensure_future(self.pump(data_conn.reader, c, room, data_rest))
            done, pending = await asyncio.wait({up, down}, return_when=asyncio.FIRST_COMPLETED)
            for t in pending:
                t.cancel()
        finally:
            room.pipes.discard(pair)
            c.close()
            release(data_conn)
            if not room.closed:
                log(f"{room.label()} '{c.name}' left  [{room.players}/{room.profile['max_players']}]")

    async def pump(self, reader, dst, room, first):
        bucket = Bucket(LIMITS["byte_rate"], LIMITS["byte_burst"])
        try:
            if first:
                dst.writer.write(first)
            while not dst.closed:
                data = await reader.read(65536)
                if not data:
                    return
                room.touch()
                await bucket.take(len(data))
                dst.writer.write(data)
                await dst.writer.drain()  # backpressure: a slow side slows the other
        except (ConnectionError, OSError, asyncio.IncompleteReadError):
            return

    async def tunnel_data(self, c, token, rest):
        # the token (96 random bits, sent only to the room's host) is the proof
        room = self.tokens.get(token.strip())
        fut = room.pending.get(token.strip()) if room else None
        if fut is None or fut.done():
            c.error("Unknown or expired data token")
            return
        c.role, c.room = "hostdata", room
        c.done = asyncio.get_running_loop().create_future()
        fut.set_result((c, rest))
        await c.done  # the joining friend's task pipes this connection until it ends


def release(data_conn):
    """End a tunnel host data connection and let its handler return."""
    data_conn.close()
    if not data_conn.done.done():
        data_conn.done.set_result(None)
