#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "$0")/.." && pwd)"
VERSION="$(python3 "$ROOT/scripts/version.py")"
bash "$ROOT/NativeHost/build.sh"
DOTNET="${DOTNET_EXE:-/mnt/c/Program Files/dotnet/dotnet.exe}"
"$DOTNET" publish "$(wslpath -w "$ROOT/Mio.csproj")" -c Release -p:Platform=x64 \
  -o "$(wslpath -w "$ROOT/artifacts/build/V$VERSION")" --verbosity minimal
