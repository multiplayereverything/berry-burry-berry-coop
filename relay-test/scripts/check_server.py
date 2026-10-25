"""Check a relay (the central server, or a local one) for one game id, with fake clients. Standard library only.

    python check_server.py --game mygame                       the central server
    python check_server.py --game mygame --server 127.0.0.1:7790
    python check_server.py --game mygame --version mygame-1    the version string your mod sends
    python check_server.py --game mygame --mode tunnel --server 127.0.0.1:7790   before mods-info has the game

Prints one line per check and exits 1 if any fails:
  known game   LIST is answered (an unknown id gets "Unknown game": the server doesn't serve it yet)
  mods-info    INFO returns the game's mods-info files (only once mods/<id>.json is merged in mods-info)
  room         a host opens a room and a friend joins; star: messages both ways; tunnel: 200 kB both ways
"""
import argparse
import json
import os
import socket
import sys

CENTRAL = "195.60.166.57:25569"


def connect(addr):
    host, port = addr.rsplit(":", 1)
    s = socket.create_connection((host, int(port)), 8)
    s.settimeout(8)
    return s


def read_lines(s, n, term):
    buf, out = b"", []
    try:
        while len(out) < n:
            d = s.recv(65536)
            if not d:
                break
            buf += d
            while term in buf:
                line, buf = buf.split(term, 1)
                out.append(line.decode("utf-8", "replace"))
    except socket.timeout:
        pass
    return out


def ask(addr, line, n=1, term=b"\n"):
    s = connect(addr)
    s.sendall(line.encode() + term)
    return s, read_lines(s, n, term)


def recv_exact(s, n):
    data = b""
    s.settimeout(20)
    while len(data) < n:
        d = s.recv(min(65536, n - len(data)))
        if not d:
            raise ConnectionError("closed after %d bytes" % len(data))
        data += d
    return data


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", required=True, help="the relay game id, e.g. bic")
    ap.add_argument("--server", default=CENTRAL)
    ap.add_argument("--version", default="check-1", help="version string to send (host and friend send the same)")
    ap.add_argument("--mode", choices=("star", "tunnel"), help="the game's mode, if mods-info doesn't have it yet")
    a = ap.parse_args()
    ok = True

    def report(name, passed, detail):
        nonlocal ok
        ok = ok and passed
        print("%s %-11s %s" % ("PASS" if passed else "FAIL", name, detail))

    s, r = ask(a.server, "RELAY|1|LIST|" + a.game)
    s.close()
    known = bool(r) and r[0].startswith("LIST|")
    report("known game", known, r[0] if r else "no answer")
    if not known:
        print("The server doesn't serve '%s' yet: get mods/%s.json merged into mods-info (it is picked up within"
              " 10 minutes), or test against a local relay with a games.json." % (a.game, a.game))
        sys.exit(1)

    s, r = ask(a.server, "RELAY|1|INFO|" + a.game, 2)
    s.close()
    has_info = len(r) == 2 and r[1].startswith("INFO|mod|")
    if has_info or a.mode is None:
        report("mods-info", has_info, r[-1][:90] if r else "no answer")
    else:
        # testing before mods/<id>.json is merged (or on a local relay): not a failure
        print("SKIP mods-info   %s" % (r[-1][:90] if r else "no answer"))
    mode = a.mode
    if mode is None and has_info:
        try:
            mode = json.loads(r[1].split("|", 2)[2])["relay"]["mode"]
        except (ValueError, KeyError, TypeError):
            pass
    if mode not in ("star", "tunnel"):
        print("Can't tell the game's mode from mods-info: pass --mode star or --mode tunnel.")
        sys.exit(1)
    star = mode == "star"

    # star rooms speak NUL-ended lines (Flash); tunnel rooms newline-ended control lines, then raw bytes
    term = b"\0" if star else b"\n"
    s, r = ask(a.server, "RELAY|1|HOST|%s|%s|0|check" % (a.game, a.version), 1, term)
    if not r or not r[0].startswith("ROOM|"):
        s.close()
        report("room", False, r[0] if r else "no answer")
        sys.exit(1)
    code = r[0].split("|")[1]
    if star:
        read_lines(s, 1, b"\0")  # W|0
    try:
        if star:
            g, r = ask(a.server, "RELAY|1|JOIN|%s|%s|%s|friend" % (a.game, a.version, code), 1, b"\0")
            joined = read_lines(s, 1, b"\0")
            g.sendall(b"hello\0")
            got = read_lines(s, 1, b"\0")
            s.sendall(b"hi\0")
            back = read_lines(g, 1, b"\0")
            g.close()
            report("room", r == ["W|1"] and got == ["G|1|hello"] and back == ["hi"],
                   "star room %s: friend %s, host got %s, friend got %s (host saw %s)" % (code, r, got, back, joined))
        else:
            f = connect(a.server)
            f.sendall(("RELAY|1|JOIN|%s|%s|%s|friend\n" % (a.game, a.version, code)).encode())
            op = read_lines(s, 1, b"\n")
            d = connect(a.server)
            d.sendall(("RELAY|1|DATA|%s\n" % op[0].split("|")[1]).encode())
            ack = read_lines(f, 1, b"\n")
            payload = os.urandom(200000)
            f.sendall(payload)
            got = recv_exact(d, len(payload))
            d.sendall(got)
            back = recv_exact(f, len(payload))
            f.close()
            d.close()
            report("room", ack == ["OK"] and got == payload and back == payload,
                   "tunnel room %s: friend %s, 200 kB both ways %s" % (code, ack, "intact" if back == payload else "BROKEN"))
    finally:
        s.close()
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
