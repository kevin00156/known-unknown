#!/usr/bin/env bash
# 建置插件、打包成玩家直接解壓到遊戲資料夾的 zip，發 GitHub Release(tag = v<csproj 的 Version>)。
# 用法：./release.sh [說明檔]   沒給說明檔就用 GitHub 自動產生的
set -euo pipefail
cd "$(dirname "$0")"

GAME=${GAME:-$HOME/.steam/debian-installation/steamapps/common/LegendOfMortal}
VER=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' plugin/KnownUnknown.csproj)
grep -q "\"known-unknown\", \"多玩10塊錢\", \"$VER\"" plugin/Plugin.cs || { echo "Plugin.cs 的版本不是 $VER" >&2; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "工作區有沒 commit 的改動" >&2; exit 1; }

docker run --rm -v "$PWD/plugin":/src -v "$GAME":/game:ro -w /src -e HOME=/tmp \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release -p:GamePath=/game -o /src/bin/out

OUT=$PWD/plugin/bin/release
ZIP=$OUT/KnownUnknown-$VER.zip
rm -rf "$OUT"
mkdir -p "$OUT/stage/BepInEx/plugins/KnownUnknown"
cp plugin/bin/out/KnownUnknown.dll LICENSE "$OUT/stage/BepInEx/plugins/KnownUnknown/"
(cd "$OUT/stage" && zip -qr "$ZIP" BepInEx)

git push origin HEAD
if [ $# -gt 0 ]; then
  gh release create "v$VER" "$ZIP" --target "$(git rev-parse HEAD)" --title "$VER" --notes-file "$1"
else
  gh release create "v$VER" "$ZIP" --target "$(git rev-parse HEAD)" --title "$VER" --generate-notes
fi
