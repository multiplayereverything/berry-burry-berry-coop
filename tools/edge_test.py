"""Relay edge cases against a running co-op host (the lab host writes its room code to lab/join.txt).

python tools/edge_test.py --relay 127.0.0.1:7799 --code ABCDE
Checks: wrong code, wrong version, a guest from another mod build, and a full room (4 players).
"""
import argparse
import socket
import struct
import time

GAME = "berrybury"
VERSION = "0.1.0/1"
TAG = "berrybury-1"


def relay_join(host, port, code, version=VERSION, name="edge"):
    s = socket.create_connection((host, port), timeout=8)
    s.sendall(("RELAY|2|JOIN|%s|%s|%s||%s\n" % (GAME, version, code, name)).encode())
    reply = b""
    while not reply.endswith(b"\n"):
        chunk = s.recv(1)
        if not chunk:
            break
        reply += chunk
    return s, reply.decode().strip()


def frame(payload):
    return struct.pack("<i", len(payload)) + payload


def net_str(text):
    data = text.encode()
    out = b""
    n = len(data)
    while True:
        b = n & 0x7F
        n >>= 7
        out += bytes([b | (0x80 if n else 0)])
        if not n:
            return out + data


def hello(tag, name):
    return frame(bytes([1]) + net_str(tag) + net_str("0.1.0") + net_str(name))


def read_frame(s, timeout=8):
    s.settimeout(timeout)
    head = b""
    while len(head) < 4:
        chunk = s.recv(4 - len(head))
        if not chunk:
            return None
        head += chunk
    n = struct.unpack("<i", head)[0]
    body = b""
    while len(body) < n:
        chunk = s.recv(n - len(body))
        if not chunk:
            return None
        body += chunk
    return body


def first_reply(s):
    """Returns ('welcome', id) or ('reject', reason), skipping pings."""
    while True:
        body = read_frame(s)
        if body is None:
            return ("closed", "")
        if body[0] == 2:
            return ("welcome", struct.unpack("<i", body[1:5])[0])
        if body[0] == 3:
            n, i, shift = 0, 1, 0
            while True:
                b = body[i]; i += 1
                n |= (b & 0x7F) << shift; shift += 7
                if not b & 0x80:
                    break
            return ("reject", body[i:i + n].decode())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--relay", default="127.0.0.1:7799")
    ap.add_argument("--code", required=True)
    args = ap.parse_args()
    host, port = args.relay.rsplit(":", 1)
    port = int(port)
    results = []

    s, r = relay_join(host, port, "ZZZZZ")
    results.append(("wrong code refused", r.startswith("E|"), r))
    s.close()

    s, r = relay_join(host, port, args.code, version="9.9.9/9")
    results.append(("wrong version refused", r.startswith("E|"), r))
    s.close()

    s, r = relay_join(host, port, args.code)
    ok = r == "OK"
    if ok:
        s.sendall(hello("berrybury-0", "oldbuild"))
        kind, why = first_reply(s)
        results.append(("other mod build rejected by host", kind == "reject", why))
    else:
        results.append(("other mod build rejected by host", False, r))
    s.close()
    time.sleep(1.5)

    # The lab guest is one player; two more fit (host + 3 guests), the next is refused.
    held = []
    for i in range(3):
        s, r = relay_join(host, port, args.code, name="fill%d" % i)
        if r != "OK":
            held.append((s, ("relay", r)))
            continue
        s.sendall(hello(TAG, "fill%d" % i))
        held.append((s, first_reply(s)))
    kinds = [k for _, k in held]
    refused = [k for k in kinds if k[0] != "welcome"]
    results.append(("full room refuses one more", len(refused) == 1 and kinds[-1][0] != "welcome", str(kinds)))
    for s, _ in held:
        s.close()

    for name, ok, detail in results:
        print("%s %s  (%s)" % ("PASS" if ok else "FAIL", name, detail))


if __name__ == "__main__":
    main()
