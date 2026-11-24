#!/bin/bash
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
output="$here/bin/Release/Desk Madeline.app"
dotnet_command="${DOTNET:-dotnet}"
arch="${DESKMADELINE_ARCH:-x64}"
case "$arch" in
    x64) clang_arch=x86_64 ;;
    arm64) clang_arch=arm64 ;;
    *) echo "DESKMADELINE_ARCH must be x64 or arm64" >&2; exit 2 ;;
esac

stage="$(mktemp -d "$here/.build.XXXXXXXX")"
trap 'rm -rf "$stage"' EXIT
app="$stage/Desk Madeline.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$here/Native/Info.plist" "$app/Contents/Info.plist"
cp "$here/Native/DeskMadeline.icns" "$app/Contents/Resources/"
cp "$here/../LICENSE" "$app/Contents/Resources/LICENSE-desk-madeline.txt"
cp "$here/README.md" "$app/Contents/Resources/README.md"

if [[ ${DESKMADELINE_BUNDLE_ART:-1} == 1 ]]; then
    celeste="${CELESTE_PATH:-}"
    if [[ -z "$celeste" ]]; then
        for candidate in "$HOME/Library/Application Support/Steam/steamapps/common/Celeste/Celeste.app" \
            "$HOME/Applications/Celeste.app" "/Applications/Celeste.app"; do
            if [[ -f "$candidate/Contents/Resources/Content/Graphics/Atlases/Gameplay.meta" ]]; then
                celeste="$candidate"
                break
            fi
        done
    fi
    if [[ -z "$celeste" ]]; then
        printf 'Set CELESTE_PATH to your Celeste.app or its Resources folder to bundle the game artwork.\n' >&2
        exit 2
    fi
    if [[ -d "$celeste/Celeste.app" ]]; then celeste="$celeste/Celeste.app"; fi
    if [[ -d "$celeste/Contents/Resources" ]]; then celeste="$celeste/Contents/Resources"; fi
    graphics="$celeste/Content/Graphics"
    atlases="$graphics/Atlases"
    destination="$app/Contents/Resources/Content/Graphics"
    mkdir -p "$destination/Atlases/Portraits/madeline" "$destination/Atlases/Overworld"
    cp "$graphics/Sprites.xml" "$graphics/Portraits.xml" "$destination/"
    cp "$atlases/Gameplay.meta" "$atlases"/Gameplay*.data "$atlases/Gui.meta" "$atlases"/Gui*.data \
        "$atlases/Portraits.meta" "$destination/Atlases/"
    cp "$atlases"/Portraits/madeline/*.data "$destination/Atlases/Portraits/madeline/"
    cp "$atlases/Overworld/snow.data" "$destination/Atlases/Overworld/"
    find "$destination" -type f -exec chmod 644 {} +
fi

"$dotnet_command" publish "$here/DeskMadeline.Mac.csproj" -c Release -r "osx-$arch" \
    --self-contained true -p:DebugType=none -p:PublishSingleFile=true \
    -p:PublishReadyToRun=true -p:EnableCompressionInSingleFile=true -o "$app/Contents/MacOS"
mv "$app/Contents/MacOS/assets" "$app/Contents/MacOS/licenses" "$app/Contents/Resources/"
xcrun clang -arch "$clang_arch" -mmacosx-version-min=15.0 -fobjc-arc -O2 \
    -Wall -Wextra -Werror -dynamiclib -install_name @rpath/libDeskMadelineMac.dylib \
    -framework AppKit -framework ApplicationServices -framework QuartzCore -framework GameController \
    "$here/Native/MacHost.m" -o "$app/Contents/MacOS/libDeskMadelineMac.dylib"

while IFS= read -r -d '' library; do
    codesign --force --sign - --timestamp=none "$library"
done < <(find "$app/Contents/MacOS" -name '*.dylib' -print0)
codesign --force --sign - --timestamp=none "$app"
codesign --verify --deep --strict "$app"

mkdir -p "$(dirname "$output")"
rm -rf "$output"
mv "$app" "$output"
printf '%s\n' "$output"
