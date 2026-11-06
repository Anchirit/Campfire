#!/usr/bin/env python3
"""Local protocol/JVM fixtures only; never adds EasyTier nodes."""
import pathlib
import subprocess
import sys
import tempfile

root = pathlib.Path(__file__).resolve().parent.parent
dotnet = sys.argv[1] if len(sys.argv) > 1 else "dotnet"
with tempfile.TemporaryDirectory(prefix="campfire-jvm-fixture-") as work:
    work = pathlib.Path(work)
    classes = work / "classes"
    classes.mkdir()
    game = work / "game with spaces"
    game.mkdir()
    sources = sorted((root / "tests/java-fixture/src").rglob("*.java"))
    subprocess.run(["javac", "--release", "8", "-d", str(classes), *map(str, sources)], check=True)
    fixture = subprocess.Popen(["java", "-cp", str(classes), "Fixture", "--gameDir", str(game),
                                "--username", "Fixture", "--uuid", "0" * 32, "--userType", "msa",
                                "--accessToken", "private-fixture-token"], stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True)
    try:
        line = fixture.stdout.readline().strip()
        if not line.startswith("PORT="):
            raise RuntimeError("JVM fixture did not start: " + line)
        subprocess.run([dotnet, str(root / "tests/Campfire.Tests/bin/Release/net8.0/Campfire.Tests.dll"),
                        "--java-pid", str(fixture.pid), line.split("=", 1)[1]], check=True, timeout=50)
    finally:
        fixture.terminate()
        try:
            fixture.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            fixture.kill()
            fixture.communicate()
