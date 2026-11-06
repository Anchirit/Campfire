#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/hook" "$work/agent" "$work/attach" dist
javac -encoding UTF-8 --release 8 -cp lib/asm-9.11.1.jar -d "$work/hook" src/CampfireHook.java src/CampfireNames.java
javac -encoding UTF-8 --release 8 -cp "lib/asm-9.11.1.jar:$work/hook" -d "$work/agent" src/CampfireTransformer.java src/CampfireAgent.java
javac -encoding UTF-8 --release 9 --add-modules jdk.attach -d "$work/attach" src/CampfireAttach.java
asm="$(pwd)/lib/asm-9.11.1.jar"
(cd "$work/agent" && jar xf "$asm" && rm -f module-info.class)
printf '%s\n' 'Manifest-Version: 1.0' 'Agent-Class: campfire.CampfireAgent' 'Can-Retransform-Classes: true' 'Can-Redefine-Classes: true' '' > "$work/agent.mf"
printf '%s\n' 'Manifest-Version: 1.0' 'Main-Class: campfire.CampfireAttach' '' > "$work/attach.mf"
jar --create --file dist/CampfireAgent.jar --manifest "$work/agent.mf" -C "$work/agent" .
jar --create --file dist/CampfireHook.jar -C "$work/hook" .
jar --create --file dist/CampfireAttach.jar --manifest "$work/attach.mf" -C "$work/attach" .
