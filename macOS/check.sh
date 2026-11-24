#!/bin/bash
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
dotnet_command="${DOTNET:-dotnet}"
arch="${DESKMADELINE_ARCH:-x64}"
stage="$(mktemp -d "$here/.check.XXXXXXXX")"
trap 'rm -rf "$stage"' EXIT

"$dotnet_command" publish "$here/DeskMadeline.Mac.csproj" -c Release -r "osx-$arch" \
    --self-contained true -p:IncludeMacChecks=true -o "$stage"
"$stage/DeskMadeline" --check "$@"
