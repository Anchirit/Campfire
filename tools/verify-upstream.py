#!/usr/bin/env python3
"""Verify the retained upstream protocol, node list, configuration and Java artifacts."""
import hashlib
import json
import pathlib

root = pathlib.Path(__file__).resolve().parent.parent
manifest = json.loads((root / "upstream-manifest.json").read_text())
immutable = {
    "src/Campfire/Link/RoomCode.cs", "src/Campfire/Link/PublicNodes.cs",
    "src/Campfire/Link/Runtime/EasyTierConfig.cs", "src/Campfire/Link/Runtime/EasyTierPeer.cs",
    "src/Campfire/Link/Minecraft/GameProxy.cs", "src/Campfire/Link/Minecraft/GameProtocol.cs",
    "src/Campfire/Link/Minecraft/GameOverlay.cs", "src/Campfire/Link/Minecraft/MinecraftProbe.cs",
    "src/Campfire/Link/Minecraft/LanOpener.cs", "src/Campfire/Link/Minecraft/LanWorldAnnouncer.cs",
}
changed = []
for name, expected in manifest["files"].items():
    actual = hashlib.sha256((root / name).read_bytes()).hexdigest()
    protected = (name in immutable or name.startswith("src/Campfire/Link/Room/")
                 or name.startswith("src/java-agent/") or "/Mappings/" in name)
    if actual != expected:
        if protected:
            raise SystemExit("Original protocol/node/agent file changed: " + name)
        changed.append(name)
print("PASS: original room protocol, invitation, nodes, network configuration and Java artifacts match", manifest["commit"])
print("Adapted upstream files:")
for name in changed:
    print("  " + name)
