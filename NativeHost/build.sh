#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "$0")/.." && pwd)"
TOOLCHAIN="${LLVM_MINGW:?Set LLVM_MINGW to the extracted llvm-mingw toolchain directory}"
python3 "$ROOT/scripts/version.py"
mkdir -p "$ROOT/NativeHost/build"
"$TOOLCHAIN/bin/x86_64-w64-mingw32-windres" -I "$ROOT/NativeHost" -i "$ROOT/NativeHost/host.rc" -o "$ROOT/NativeHost/build/host.res"
"$TOOLCHAIN/bin/x86_64-w64-mingw32-clang" -std=c17 -O2 -DNDEBUG -DSQLITE_THREADSAFE=1 -DSQLITE_DEFAULT_MEMSTATUS=0 -c \
  "$ROOT/NativeHost/vendor/sqlite/sqlite3.c" -o "$ROOT/NativeHost/build/sqlite3.o"
"$TOOLCHAIN/bin/x86_64-w64-mingw32-clang++" -std=c++20 -O2 -DNDEBUG -DUNICODE -D_UNICODE -D_WIN32_WINNT=0x0A00 \
  -Wall -Wextra -Werror -static -municode -mwindows -fuse-ld=lld \
  "$ROOT/NativeHost/main.cpp" "$ROOT/NativeHost/NativeLogger.cpp" "$ROOT/NativeHost/SqliteStore.cpp" "$ROOT/NativeHost/build/sqlite3.o" "$ROOT/NativeHost/build/host.res" \
  -o "$ROOT/NativeHost/build/Mio.Host.exe" \
  -lws2_32 -liphlpapi -ladvapi32 -lole32 -loleaut32 -luuid -lshell32 -lbcrypt
