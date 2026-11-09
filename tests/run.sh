#!/usr/bin/env bash
# 自動化測試：拿目前安裝的遊戲劇本跑 StoryIndex(劇本解析、選項會算到哪些台詞)
set -euo pipefail
cd "$(dirname "$0")/.."

GAME=${GAME:-$HOME/.steam/debian-installation/steamapps/common/LegendOfMortal}
uv run python known_unknown.py --game "$GAME/Mortal_Data" --dump-scripts tests/bin/scripts
docker run --rm -v "$PWD":/src -v "$GAME":/game:ro -w /src/tests -e HOME=/tmp -e KU_SCRIPTS=/src/tests/bin/scripts \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet test -p:GamePath=/game --logger "console;verbosity=normal"
