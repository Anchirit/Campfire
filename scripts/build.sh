#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet publish src/Campfire/Campfire.Cli.csproj -c Release -r linux-x64 \
    --self-contained true --source https://api.nuget.org/v3/index.json \
    -p:PublishTrimmed=false -p:PublishSingleFile=false -o artifacts/linux-x64 --nologo
archive=artifacts/linux-x64/easytier-linux-x86_64-v2.6.4.zip
if [[ ! -f "$archive" ]]; then
    curl -fL --connect-timeout 15 --max-time 300 \
        https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-linux-x86_64-v2.6.4.zip \
        -o "$archive"
fi
echo "61b659eaedba658fa66fe47d17e1426cdd77e5d02fa15fed447bb4357c09dfd6  $archive" | sha256sum -c -
cp LICENSE THIRD-PARTY-NOTICES.md artifacts/linux-x64/
echo 'Ready: ./campfire --help'
