#!/bin/bash
# Runs the MapGenerator over the client Patch folder (read only) and writes .nfmap files to data/map-gen (all worlds, a few hours at worst).
# Afterwards copy the new files into data/map (existing files are kept).
set -euo pipefail
cd "$(dirname "$0")/.."
PATCH=${PATCH:-$PWD/client/Patch}
docker run --rm -i -v "$PWD/Source:/src:ro" -v "$PATCH:/patch:ro" -v "$PWD/data/map-gen:/out" mcr.microsoft.com/dotnet/sdk:5.0 sh -c '
  cp -r /src /build && cd /build/NexusForever.MapGenerator && dotnet publish -c Release -o /app >/dev/null &&
  cd /out && mkdir -p /out/work && cd /out/work && dotnet /app/NexusForever.MapGenerator.dll -i /patch -g </dev/null' 
