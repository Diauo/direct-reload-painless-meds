#!/usr/bin/env bash
# Build Direct Reload & Painless Meds (client + server) for both SPT lines.
# Requires: .NET SDK; reference DLLs placed per README ("Building from source").
set -euo pipefail

cd "$(dirname "$0")"

echo "== [1/4] Build client (SPT 4.0) =="
(cd direct-reload/client && dotnet build -c Release --nologo)

echo "== [2/4] Build server (SPT 4.0) =="
(cd direct-reload/server && dotnet build -c Release --nologo)

echo "== [3/4] Build client (SPT 4.1) =="
(cd direct-reload-41/client && dotnet build -c Release --nologo)

echo "== [4/4] Build server (SPT 4.1) =="
(cd direct-reload-41/server && dotnet build -c Release --nologo)

echo ""
echo "Done."
echo "  4.0 client: direct-reload/client/bin/Release/netstandard2.1/RZDirectReload.dll"
echo "  4.0 server: direct-reload/server/bin/Release/net9.0/RZDirectReload.Server.dll"
echo "  4.1 client: direct-reload-41/client/bin/Release/netstandard2.1/RZDirectReload.dll"
echo "  4.1 server: direct-reload-41/server/bin/Release/RZDirectReload.Server/RZDirectReload.Server.dll"
