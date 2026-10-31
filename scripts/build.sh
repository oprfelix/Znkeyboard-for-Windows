#!/usr/bin/env bash
# Publishes the framework-dependent win-x64 exe as build/ZnKeyboard-Windows-<version>.exe. Run, it installs itself
# (src/Installer.cs), so the release ships the bare exe; scripts/README-ZnKeyboard.txt is the guide to share.
# Needs the .NET 10 SDK (on macOS: ~/.dotnet from dotnet-install.sh works).
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/ZnKeyboard.csproj)
rm -rf build/publish
dotnet publish src/ZnKeyboard.csproj -c Release -o build/publish
cp build/publish/ZnKeyboard.exe "build/ZnKeyboard-Windows-$VERSION.exe"
echo "Built build/ZnKeyboard-Windows-$VERSION.exe"
