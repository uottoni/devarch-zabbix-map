#!/usr/bin/env bash
# Gera executáveis autocontidos (não exigem .NET instalado) em dist/<plataforma>.
set -euo pipefail
cd "$(dirname "$0")"
for rid in linux-x64 win-x64 osx-arm64 osx-x64; do
  dotnet publish src/ZabbixMap.App -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "dist/$rid"
done
