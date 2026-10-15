#!/usr/bin/env bash
# Build Direct Reload & Painless Meds (client + server) — SPT 4.1 line.
# The SPT 4.0 line is frozen at v1.2.0 and no longer built.
# Requires: .NET SDK; reference DLLs placed per README ("Building from source").
set -euo pipefail

cd "$(dirname "$0")"

echo "== [1/2] Build client (SPT 4.1) =="
(cd direct-reload-41/client && dotnet build -c Release --nologo)

echo "== [2/2] Build server (SPT 4.1) =="
(cd direct-reload-41/server && dotnet build -c Release --nologo)

echo ""
echo "Done."
echo "  4.1 client: direct-reload-41/client/bin/Release/netstandard2.1/RZDirectReload.dll"
echo "  4.1 server: direct-reload-41/server/bin/Release/RZDirectReload.Server/RZDirectReload.Server.dll"
