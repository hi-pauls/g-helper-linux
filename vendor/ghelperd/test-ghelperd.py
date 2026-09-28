#!/usr/bin/env python3
"""Protocol checks for ghelperd against a running instance.

Run the daemon as an unprivileged user in the client group, then:
    ghelperd --socket /tmp/g.sock &  python3 test-ghelperd.py /tmp/g.sock
The refusals must hold for any account; the positive write/hidraw cases are
skipped when the machine has no suitable node."""
import array
import glob
import os
import socket
import sys

sock_path = sys.argv[1]
failures = 0

def request(*fields: str, fds: tuple[int, ...] = ()) -> list[bytes]:
    s = socket.socket(socket.AF_UNIX, socket.SOCK_SEQPACKET)
    s.connect(sock_path)
    payload = b"\0".join(f.encode() for f in fields)
    if fds:
        s.sendmsg([payload], [(socket.SOL_SOCKET, socket.SCM_RIGHTS, array.array("i", fds))])
    else:
        s.send(payload)
    reply = s.recv(8192).split(b"\0")
    s.close()
    return reply

def check(name: str, ok: bool, detail: object = "") -> None:
    global failures
    print(f"{'PASS' if ok else 'FAIL'}  {name}  {detail}")
    failures += 0 if ok else 1

reply = request("ping")
check("ping answers version", reply[0] == b"ok", reply)

for path in ("/etc/passwd", "/sys/../etc/passwd", "/sys/class/../../etc/shadow", "/proc/sys/kernel/sysrq"):
    reply = request("write", path, "1")
    check(f"write refused outside /sys: {path}", reply[0] == b"err", reply)

reply = request("read", "/sys/class/power_supply/AC0/online")
check("read inside /sys", reply[0] == b"ok", reply)

reply = request("write", "/sys/kernel/mm/transparent_hugepage/enabled", "always")
check("write denied by file permissions", reply[0] == b"err" and reply[1] == b"13", reply)

reply = request("hidraw", "/dev/input/event0")
check("hidraw refuses non-hidraw path", reply[0] == b"err", reply)

for node in sorted(glob.glob("/dev/hidraw*")):
    vendor = open(f"/sys/class/hidraw/{os.path.basename(node)}/device/uevent").read()
    if "00000B05" not in vendor.upper() and "0000046D" not in vendor.upper() and "0000048D" not in vendor.upper():
        reply = request("hidraw", node)
        check(f"hidraw refuses foreign vendor {node}", reply[0] == b"err", reply)
        break

reply = request("exec", "/usr/bin/bash", "-c", "id", fds=(0, 1, 2))
check("exec refuses non-helper binary", reply[0] == b"err", reply)

reply = request("exec", "/opt/ghelper/gpu-helper", "list")
check("exec refuses without stdio descriptors", reply[0] == b"err", reply)

reply = request("bogus")
check("unknown request refused", reply[0] == b"err", reply)

sys.exit(1 if failures else 0)
