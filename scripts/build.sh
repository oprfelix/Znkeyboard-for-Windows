#!/usr/bin/env bash
# Publishes the framework-dependent win-x64 exe and zips it with the Chinese guide.
# Needs the .NET 10 SDK (on macOS: ~/.dotnet from dotnet-install.sh works).
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/ZnKeyboard.csproj)
rm -rf build/publish
dotnet publish src/ZnKeyboard.csproj -c Release -o build/publish
rm -f "build/ZnKeyboard-Windows-$VERSION.zip"
(cd build && mkdir -p package && cp publish/ZnKeyboard.exe package/ && cp ../scripts/使用说明.txt package/ \
  && (cd package && zip -q -X "../ZnKeyboard-Windows-$VERSION.zip" ZnKeyboard.exe 使用说明.txt) && rm -rf package)
echo "Built build/ZnKeyboard-Windows-$VERSION.zip"
