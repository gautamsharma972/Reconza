#!/usr/bin/env bash
# Builds a release package for upload to a server.
#   scripts/publish.sh portable   -> dist/reconza-portable.zip  framework-dependent: 32- AND 64-bit app pools,
#                                    needs the .NET 10 Hosting Bundle on the server
#   scripts/publish.sh            -> dist/reconza-win-x64.zip   self-contained (no .NET needed), 64-bit app pool
#   scripts/publish.sh win-x86    -> self-contained for 32-bit application pools
#   scripts/publish.sh linux-x64  -> self-contained for Linux servers with a reverse proxy
set -euo pipefail
RID="${1:-win-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist/reconza-$RID"
ZIP="$ROOT/dist/reconza-$RID.zip"

echo "==> Running tests"
dotnet test "$ROOT/tests/SettleFlow.Tests/SettleFlow.Tests.csproj" -c Release --nologo -v q

rm -rf "$OUT" "$ZIP"
if [ "$RID" = "portable" ]; then
  echo "==> Publishing portable framework-dependent build (any CPU: 32- and 64-bit; requires .NET 10 runtime)"
  dotnet publish "$ROOT/src/SettleFlow.Web/SettleFlow.Web.csproj" -c Release --self-contained false \
    -p:DebugType=None -p:DebugSymbols=false -p:GenerateDocumentationFile=false -o "$OUT" --nologo -v q
  # IIS starts the shared .NET runtime ("dotnet", 32- or 64-bit to match the app pool) with the app dll.
  sed -i.bak -E 's#processPath="[^"]*" arguments="[^"]*"#processPath="dotnet" arguments=".\\Reconza.Web.dll"#' "$OUT/web.config" && rm -f "$OUT/web.config.bak"
  rm -f "$OUT/Reconza.Web.exe"  # apphost is x64-only; IIS uses dotnet.exe instead
else
  echo "==> Publishing $RID (self-contained, no debug symbols)"
  dotnet publish "$ROOT/src/SettleFlow.Web/SettleFlow.Web.csproj" -c Release -r "$RID" --self-contained true \
    -p:DebugType=None -p:DebugSymbols=false -p:GenerateDocumentationFile=false -o "$OUT" --nologo -v q
fi

cp "$ROOT/DEPLOY-PLESK.md" "$OUT/"

echo "==> Generating database script for MySQL/MariaDB (for phpMyAdmin import)"
dotnet ef migrations script --context MySqlAppDbContext --project "$ROOT/src/SettleFlow.Web" --no-build --configuration Release -o "$OUT/reconza-database.raw.sql" >/dev/null
find "$ROOT" -name design.db -delete
python3 - "$OUT" <<'PY'
import sys, os
out = sys.argv[1]
raw = os.path.join(out, "reconza-database.raw.sql")
sql = open(raw, encoding="utf-8-sig").read()
header = ("-- Reconza database schema (MySQL 8 / MariaDB 10.6+). Run ONCE on an EMPTY database "
          "(Plesk > Databases > phpMyAdmin > Import), then set Database:AutoMigrate=false.\n"
          "SET NAMES utf8mb4;\nALTER DATABASE CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;\n\n")
open(os.path.join(out, "reconza-database.sql"), "w", encoding="utf-8").write(header + sql)
os.remove(raw)
PY

echo "==> Checking the package for development leakage"
fail=0
for f in appsettings.Development.json appsettings.Server.json; do
  if [ -e "$OUT/$f" ]; then echo "  !! $f must not be shipped"; fail=1; fi
done
if [ -n "$(find "$OUT" \( -name '*.db' -o -name '*.db-*' -o -name '*.pdb' -o -name '*.eml' -o -path '*App_Data*' -o -name 'dev-test-account.txt' \) -print -quit)" ]; then
  echo "  !! local data, debug symbols or App_Data found in package"; fail=1
fi
if grep -rIl --exclude='*.md' -e "$HOME" -e "localhost:5187" "$OUT" >/dev/null 2>&1; then
  echo "  !! local machine paths or dev URLs found:"; grep -rIl --exclude='*.md' -e "$HOME" -e "localhost:5187" "$OUT" | head; fail=1
fi
[ "$fail" -eq 0 ] || { echo "Package rejected."; exit 1; }

(cd "$OUT" && zip -qr "$ZIP" .)
echo "==> $ZIP ($(du -h "$ZIP" | cut -f1))"
