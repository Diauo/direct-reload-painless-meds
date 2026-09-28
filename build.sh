#!/usr/bin/env bash
# Build Direct Reload & Painless Meds (client + server).
# Requires: .NET SDK; reference DLLs placed per README ("Building from source").
set -euo pipefail

cd "$(dirname "$0")"

echo "== [1/2] Build client =="
(cd direct-reload/client && dotnet build -c Release --nologo)

echo "== [2/2] Build server =="
(cd direct-reload/server && dotnet build -c Release --nologo)

echo ""
echo "Done."
echo "  client: direct-reload/client/bin/Release/netstandard2.1/RZDirectReload.dll"
echo "  server: direct-reload/server/bin/Release/net9.0/RZDirectReload.Server.dll"
