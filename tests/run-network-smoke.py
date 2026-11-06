#!/usr/bin/env python3
"""Opt-in real EasyTier smoke test through original public nodes, using a controlled JVM.

Does not test Minecraft login or Windows interoperability. No custom nodes are used.
Usage: python3 tests/run-network-smoke.py artifacts/linux-x64/campfire
"""
import json
import os
import pathlib
import selectors
import signal
import socket
import subprocess
import sys
import tempfile
import time

root = pathlib.Path(__file__).resolve().parent.parent
cli = pathlib.Path(sys.argv[1]).resolve()


def ready(process, seconds=85):
    with selectors.DefaultSelector() as poll:
        poll.register(process.stdout, selectors.EVENT_READ)
        end = time.monotonic() + seconds
        pending = b""
        while time.monotonic() < end:
            for key, _ in poll.select(timeout=0.25):
                chunk = os.read(key.fileobj.fileno(), 65536)
                if not chunk:
                    raise RuntimeError("CLI exited before Ready: " + process.stderr.read())
                pending += chunk
                while b"\n" in pending:
                    line, pending = pending.split(b"\n", 1)
                    item = json.loads(line)
                    if item["state"] == "failed":
                        raise RuntimeError("Session failed: " + str(item["error"]))
                    if item["state"] == "ready":
                        return item
        raise TimeoutError("Session did not become Ready")


def core_pids(directory):
    found = []
    for p in pathlib.Path("/proc").iterdir():
        if not p.name.isdecimal():
            continue
        try:
            if os.readlink(p / "exe").startswith(str(directory / "EasyTier")):
                found.append(int(p.name))
        except (OSError, PermissionError):
            pass
    return found


def assert_stopped(pids):
    for _ in range(100):
        if not any(pathlib.Path(f"/proc/{pid}/exe").exists() for pid in pids):
            return
        time.sleep(0.1)
    raise RuntimeError("EasyTier process left running: " + str(pids))


with tempfile.TemporaryDirectory(prefix="campfire-network-smoke-") as temporary:
    work = pathlib.Path(temporary)
    classes = work / "classes"
    classes.mkdir()
    game = work / "game with spaces"
    game.mkdir()
    subprocess.run(["javac", "--release", "8", "-d", str(classes),
                    *map(str, (root / "tests/java-fixture/src").rglob("*.java"))], check=True)
    fixture = subprocess.Popen(["java", "-cp", str(classes), "Fixture", "--gameDir", str(game),
                                "--username", "Fixture"], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
    processes = []
    try:
        port = int(fixture.stdout.readline().strip().split("=", 1)[1])
        hostdir, guestdir = work / "host", work / "guest"

        def start(command, directory, name):
            process = subprocess.Popen([str(cli), *command, "--data-dir", str(directory), "--name", name,
                                        "--lang", "en", "--json"],
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)
            processes.append(process)
            return process

        host = start(["host", "--port", str(port), "--pid", str(fixture.pid), "--no-backup"], hostdir, "SmokeHost")
        hosted = ready(host)
        print("PASS: real EasyTier 2.6.4 host Ready; connected original nodes:", hosted["nodes"], flush=True)
        guest = start(["join", hosted["code"]], guestdir, "SmokeGuest")
        joined = ready(guest)
        print("PASS: real EasyTier guest discovered host and completed original RoomClient handshake", flush=True)
        with socket.create_connection(("127.0.0.1", joined["local_port"]), timeout=10) as connection:
            connection.sendall(bytes([254]))
            if connection.recv(1) != bytes([255]):
                raise RuntimeError("Game probe did not traverse EasyTier and host GameProxy")
        print("PASS: TCP traversed guest port-forward -> real EasyTier -> original GameProxy -> JVM fixture", flush=True)
        guestcores = core_pids(guestdir)
        guest.terminate()
        guest.wait(timeout=15)
        assert_stopped(guestcores)
        print("PASS: SIGTERM stopped guest core and released the tunnel", flush=True)
        hostcores = core_pids(hostdir)
        if not hostcores:
            raise RuntimeError("Could not identify host core for cleanup verification")
        host.kill()
        host.wait(timeout=10)
        assert_stopped(hostcores)
        if list((hostdir / "Link/runtime").glob("*.toml")):
            raise RuntimeError("Supervisor left the host's secret TOML behind")
        print("PASS: SIGKILL of CLI caused supervisor to stop its core and delete secret configuration", flush=True)
    finally:
        for process in processes:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
        fixture.terminate()
        fixture.wait(timeout=5)
