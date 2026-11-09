#!/usr/bin/env bash
# 建置插件、打包成玩家直接解壓到遊戲資料夾的 zip，發 GitHub Release(tag = v<csproj 的 Version>)。
# 用法：./release.sh [說明檔]   沒給說明檔就用 GitHub 自動產生的
set -euo pipefail
cd "$(dirname "$0")"

GAME=${GAME:-$HOME/.steam/debian-installation/steamapps/common/LegendOfMortal}
VER=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' plugin/KnownUnknown.csproj)
grep -q "\"known-unknown\", \"多玩10塊錢\", \"$VER\"" plugin/Plugin.cs || { echo "Plugin.cs 的版本不是 $VER" >&2; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "工作區有沒 commit 的改動" >&2; exit 1; }
# 編進插件的遊戲資料照目前安裝的遊戲重新產生；有變 = 遊戲更新過，先 commit 再發版
uv run python known_unknown.py --game "$GAME/Mortal_Data" --system-flags plugin/SystemFlags.cs
uv run python known_unknown.py --game "$GAME/Mortal_Data" --position-tables plugin/PositionTables.txt
[ -z "$(git status --porcelain)" ] || { git status --short; echo "遊戲資料跟 repo 裡的不同(遊戲更新了?),看過、在遊戲裡測過再 commit" >&2; exit 1; }
GAME="$GAME" tests/run.sh

for p in plugin updater; do
  docker run --rm -v "$PWD/$p":/src -v "$GAME":/game:ro -w /src -e HOME=/tmp \
    mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release -p:GamePath=/game -o /src/bin/out
done

OUT=$PWD/plugin/bin/release
ZIP=$OUT/KnownUnknown-$VER.zip
rm -rf "$OUT"
mkdir -p "$OUT/stage/BepInEx/plugins/KnownUnknown" "$OUT/stage/BepInEx/patchers/KnownUnknown"
cp plugin/bin/out/KnownUnknown.dll LICENSE "$OUT/stage/BepInEx/plugins/KnownUnknown/"
# 自動更新：下次啟動在插件載入前換上下載好的新版
cp updater/bin/out/KnownUnknown.Updater.dll "$OUT/stage/BepInEx/patchers/KnownUnknown/"
# 附上 BepInEx 6，玩家解壓到遊戲資料夾就能用。換版本要一起改 sha256
BEPINEX=BepInEx-Unity.Mono-win-x64-6.0.0-be.789+6cea286.zip
BEPINEX_SHA256=e7ab96aa80f77119335fca0e99e978460d422f4a76befb4deb9c2d36380614d0
CACHE=$PWD/plugin/bin/cache/$BEPINEX
[ -f "$CACHE" ] || { mkdir -p "$(dirname "$CACHE")"; curl -fsSLo "$CACHE" "https://builds.bepinex.dev/projects/bepinex_be/789/${BEPINEX//+/%2B}"; }
echo "$BEPINEX_SHA256  $CACHE" | sha256sum -c --quiet
unzip -q "$CACHE" -x changelog.txt -d "$OUT/stage"
cat > "$OUT/stage/BepInEx/plugins/KnownUnknown/THIRD-PARTY-NOTICES.txt" <<'EOF'
BepInEx 6.0.0-be.789 (BepInEx/core, winhttp.dll, doorstop_config.ini)
https://github.com/BepInEx/BepInEx
Licensed under LGPL-2.1: https://github.com/BepInEx/BepInEx/blob/master/LICENSE
EOF
(cd "$OUT/stage" && zip -qr "$ZIP" .)
# 固定檔名的複本給 releases/latest/download/KnownUnknown.zip 用(巴哈等處的連結)。不是 KnownUnknown- 開頭，自動更新不會挑到它
LATEST=$OUT/KnownUnknown.zip
cp "$ZIP" "$LATEST"

git push origin HEAD
if [ $# -gt 0 ]; then
  gh release create "v$VER" "$ZIP" "$LATEST" --target "$(git rev-parse HEAD)" --title "$VER" --notes-file "$1"
else
  gh release create "v$VER" "$ZIP" "$LATEST" --target "$(git rev-parse HEAD)" --title "$VER" --generate-notes
fi
