#!/usr/bin/env bash
# 遊戲更新後自動跟上：重抽編進插件的遊戲資料、版本末碼 +1、跑 release.sh 發版。
# 在自己的一份 clone 裡做($STATE/repo)，不碰平常開發的工作區。
# 每個 Steam 版本號只試一次，發了或沒發成都跳桌面通知；沒發成就把重抽的資料推到分支 game-<Steam 版本號>。
# 用法：./auto-release.sh            檢查一次(timer 每小時跑這個)
#       ./auto-release.sh --dry-run  照跑到打包完，不 push、不發版、不記版本號
#       ./auto-release.sh --install  裝 systemd 使用者層級的 timer
set -euo pipefail
export PATH="$HOME/.local/bin:$PATH"

SELF=$(readlink -f "$0")
SRC=$(dirname "$SELF")
GAME=${GAME:-$HOME/.steam/debian-installation/steamapps/common/LegendOfMortal}
MANIFEST=${GAME%/common/*}/appmanifest_1859910.acf
STATE=${XDG_STATE_HOME:-$HOME/.local/state}/known-unknown
REMOTE=${KU_REMOTE:-$(git -C "$SRC" remote get-url origin)}
BRANCH=${KU_BRANCH:-master}
UNIT=known-unknown-auto-release

DRY=
case "${1:-}" in
  "") ;;
  --dry-run) DRY=1 ;;
  --install)
    d=${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user
    mkdir -p "$d"
    printf '[Unit]\nDescription=多玩10塊錢：遊戲更新後自動發版\n\n[Service]\nType=oneshot\nExecStart=%s\n' "$SELF" > "$d/$UNIT.service"
    printf '[Unit]\nDescription=多玩10塊錢：每小時看遊戲有沒有更新\n\n[Timer]\nOnCalendar=hourly\nPersistent=true\n\n[Install]\nWantedBy=timers.target\n' > "$d/$UNIT.timer"
    systemctl --user daemon-reload
    systemctl --user enable --now "$UNIT.timer"
    exit ;;
  *) sed -n '2,7s/^# //p' "$SELF" >&2; exit 2 ;;
esac

field() { sed -n "s/^[[:space:]]*\"$1\"[[:space:]]*\"\(.*\)\"[[:space:]]*$/\1/p" "$MANIFEST"; }
BUILD=$(field buildid)
[ "$(field StateFlags)" = 4 ] || exit 0   # Steam 還在下載/安裝，下一輪再看
mkdir -p "$STATE"
[ -n "$DRY" ] || [ "$BUILD" != "$(cat "$STATE/buildid" 2>/dev/null)" ] || exit 0
exec 9>"$STATE/lock"
flock -n 9 || exit 0
exec > >(tee "$STATE/log") 2>&1

notify() { echo "$2:$3"; notify-send -a 多玩10塊錢 -u "$1" "$2" "$3" || true; }
fail() { FAIL=$1; echo "$1" >&2; exit 1; }

# 沒發成時把 clone 裡重抽好的東西推上去，之後手動接著做
rescue() {
  cd "$STATE/repo" 2>/dev/null || return 1
  [ -n "$(git status --porcelain)" ] || [ "$(git rev-parse HEAD)" != "$(git rev-parse "origin/$BRANCH")" ] || return 1
  git checkout -q -B "game-$BUILD" && git add -A &&
    { git diff --cached --quiet || git commit -qm "遊戲 $BUILD 重抽的資料(自動發版沒成功)"; } &&
    git push -q origin "game-$BUILD"
}

FAIL= RECORD=
finish() {
  rc=$?
  trap - EXIT
  if [ $rc != 0 ]; then
    msg="沒發版：${FAIL:-看紀錄}"
    if [ -z "$DRY" ] && rescue; then msg+="。重抽的資料在分支 game-$BUILD"; fi
    notify critical "遊戲更新了，自動發版失敗" "$msg。紀錄：$STATE/log"
  fi
  [ -n "$DRY" ] || [ -z "$RECORD" ] || echo "$BUILD" > "$STATE/buildid"
}
trap finish EXIT

FAIL="拿不到最新的 repo"
[ -d "$STATE/repo/.git" ] || git clone -q "$REMOTE" "$STATE/repo"
cd "$STATE/repo"
git remote set-url origin "$REMOTE"
# 公開 repo:commit 的身分跟開發用的那份一樣
git config user.name "$(git -C "$SRC" config user.name)"
git config user.email "$(git -C "$SRC" config user.email)"
git fetch -q --tags --prune origin
git checkout -q -f -B "$BRANCH" "origin/$BRANCH"
git clean -qfd
RECORD=1   # 從這裡開始，這個 Steam 版本號不管成敗都不再試

FAIL="重抽遊戲資料失敗"
uv run python known_unknown.py --game "$GAME/Mortal_Data" --system-flags plugin/SystemFlags.cs
uv run python known_unknown.py --game "$GAME/Mortal_Data" --position-tables plugin/PositionTables.txt
if git diff --quiet; then
  echo "Steam 版本號 $BUILD:編進插件的資料沒變，不用發版"
  exit 0
fi
git --no-pager diff --stat

for f in plugin/SystemFlags.cs plugin/PositionTables.txt; do
  old=$(git show "HEAD:$f" | wc -l)
  new=$(wc -l < "$f")
  [ $((new * 10)) -ge $((old * 9)) ] || fail "$f 從 $old 行變成 $new 行，重抽的資料可能壞了"
done
VER=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' plugin/KnownUnknown.csproj)
git diff --quiet "v$VER" HEAD -- plugin updater || fail "$BRANCH 上有 v$VER 之後還沒發版的插件改動，不自動發"

NEXT=${VER%.*}.$((${VER##*.} + 1))
GV=$(sed -n 's/.*GameVersion = "\(.*\)";.*/\1/p' plugin/SystemFlags.cs)
sed -i "s:<Version>$VER</Version>:<Version>$NEXT</Version>:" plugin/KnownUnknown.csproj
sed -i "s/\"多玩10塊錢\", \"$VER\"/\"多玩10塊錢\", \"$NEXT\"/" plugin/Plugin.cs
git commit -qam "跟上遊戲 $GV:重抽資料，$NEXT"

cat > "$STATE/notes.md" <<NOTES
跟上遊戲 $GV。

這一版是遊戲更新後自動發的：只重抽了編進插件的遊戲資料，還沒在遊戲裡實測過。有不對的地方請開 issue。
NOTES
FAIL="release.sh 沒跑完(編譯或測試沒過?)"
DRY_RUN=$DRY GAME=$GAME ./release.sh "$STATE/notes.md"
[ -n "$DRY" ] || notify normal "已自動發版 $NEXT" "跟上遊戲 $GV，沒在遊戲裡實測過。開發用的那份記得 git pull"
