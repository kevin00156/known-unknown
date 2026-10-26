"""活俠傳未讀劇情(白字)盤點。

全部台詞 key − Save_universe 的 ReadStoryData = 白字。
可達性：劇本與資料表建成引用圖，從遊戲資料的入口做 BFS,圖外的劇本視為現版本拿不到。
"""
import argparse
import collections
import glob
import json
import os
import re
import sys

import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

STEAM = os.path.expanduser("~/.steam/debian-installation/steamapps")
GAME = f"{STEAM}/common/LegendOfMortal/Mortal_Data"
SAVE_GLOB = (f"{STEAM}/compatdata/1859910/pfx/drive_c/users/steamuser/AppData/"
             "LocalLow/Obb Studio/Mortal/*/Save_universe.json")
SCRIPT_DIR = "story/chinesetraditional/"   # LuaManager 只載這個；chinesesimplified 是舊草稿

# 這些資料表本身就是遊戲入口(任務、時間事件、自由行動地點、開局)
ROOT_CLASSES = {"MissionData", "MissionCheckData", "GameTimeData", "FreePositionData",
                "PositionController", "PlayerStatManagerData", "FlagData"}
# 只有被劇本點名(checkpointmanager.Position/Switch/Condition)才會走到
LINKED_CLASSES = {"PositionResultData", "SwitchResultData", "ConditionResultData"}

KEY_RE = re.compile(r'GetStoryText\("([^"]+)"\)')
STR_RE = re.compile(r'"([A-Za-z0-9_]+)"')
TOK_RE = re.compile(rb"[A-Za-z0-9_]{4,}")


def load_scripts(game):
    # Resources 路徑表在 globalgamemanagers 的 ResourceManager;UnityPy 的 env.container 拿不到
    env = UnityPy.load(f"{game}/globalgamemanagers", f"{game}/resources.assets")
    objs = {o.path_id: o for o in env.objects if o.assets_file.name == "resources.assets"}
    rm = next(o for o in env.objects if o.type.name == "ResourceManager").read_typetree()
    scripts = {}
    for path, ptr in rm["m_Container"]:
        obj = objs.get(ptr["m_PathID"])
        if path.startswith(SCRIPT_DIR) and obj and obj.type.name == "TextAsset":
            scripts[path[len(SCRIPT_DIR):]] = obj.read().m_Script
    return scripts


def load_data(game, names):
    """回傳 [(類別, 物件名, {引用到的劇本})],只留真的引用劇本的物件。

    多數資料表直接寫劇本名，掃 raw bytes 就抓得到。
    PositionResultData 例外：劇本名在執行時才拼成 _scriptPrefix + "_" + StoryMappingItem.Value,
    要用型別樹把 _list → PositionEventData._config → StoryMappingItem 一路解開。
    """
    files = [f"{game}/{f}" for f in sorted(os.listdir(game))
             if f.endswith(".assets") or re.fullmatch(r"level\d+|globalgamemanagers", f)]
    env = UnityPy.load(*files)   # 一次全載，跨檔 PPtr 才解得開
    mbs = {}                     # (檔名, path_id) -> (類別, 物件)
    for obj in env.objects:
        if obj.type.name == "MonoBehaviour":
            mbs[(obj.assets_file.name, obj.path_id)] = (
                obj.read(check_read=False).m_Script.read().m_ClassName, obj)

    out = []
    for cls, obj in mbs.values():
        hits = {t.decode().lower() for t in TOK_RE.findall(obj.get_raw_data())} & names
        if hits:
            out.append((cls, obj.read(check_read=False).m_Name.lower(), hits))

    # 型別樹要在讀完類別之後才掛，掛了之後 m_Script 會解不開
    gen = TypeTreeGenerator("2020.3.49f1")
    gen.load_local_game(os.path.dirname(game))
    env.typetree_generator = gen

    def deref(src, ptr):
        fid = ptr["m_FileID"]
        f = src.assets_file.name if fid == 0 else os.path.basename(src.assets_file.externals[fid - 1].path)
        return mbs.get((f, ptr["m_PathID"]), (None, None))[1]

    for cls, obj in mbs.values():
        if cls != "PositionResultData":
            continue
        t = obj.read_typetree()
        hits = set()
        for p in t["_list"]:
            ev = deref(obj, p)
            mapping = ev and deref(ev, ev.read_typetree()["_config"])
            if mapping:
                hits.add(f'{t["_scriptPrefix"]}_{mapping.read_typetree()["Value"]}'.lower())
        if hits & names:
            out.append((cls, t["m_Name"].lower(), hits & names))
    return out


def build(game):
    scripts = load_scripts(game)
    names = set(scripts)   # Resources 路徑全小寫,Resources.Load 不分大小寫，比對一律小寫
    data = load_data(game, names)
    keys = {n: list(dict.fromkeys(KEY_RE.findall(t))) for n, t in scripts.items()}

    linked = collections.defaultdict(set)   # 資料物件名 -> 劇本
    roots = {}                               # 劇本 -> 入口說明
    for cls, obj, hits in data:
        if cls in LINKED_CLASSES:
            linked[obj] |= hits
        elif cls in ROOT_CLASSES:
            for h in hits:
                roots.setdefault(h, f"{cls}:{obj}")

    edges = {}
    for n, t in scripts.items():
        out = set()
        for s in map(str.lower, STR_RE.findall(t)):
            if s in names:
                out.add(s)
            out |= linked.get(s, set())
        out.discard(n)
        edges[n] = sorted(out)
    return {"keys": keys, "edges": edges, "roots": roots}


def reach(db):
    """BFS,回傳 劇本 -> 入口說明(沿路繼承最近的入口)。"""
    seen = dict(db["roots"])
    queue = collections.deque(seen)
    while queue:
        n = queue.popleft()
        for m in db["edges"].get(n, []):
            if m not in seen:
                seen[m] = seen[n]
                queue.append(m)
    return seen


def find_save():
    saves = sorted(glob.glob(SAVE_GLOB), key=os.path.getmtime)
    if not saves:
        sys.exit("找不到 Save_universe.json,用 --save 指定")
    return saves[-1]


def report(db, read, top, show_unreachable):
    reachable = reach(db)
    rows, dead = [], []
    stat = collections.Counter()
    for n, ks in db["keys"].items():
        if not ks:
            continue
        unread = [k for k in ks if k not in read]
        bucket = "reach" if n in reachable else "dead"
        stat[bucket + "_total"] += len(ks)
        stat[bucket + "_unread"] += len(unread)
        stat[bucket + "_read"] += len(ks) - len(unread)
        if bucket == "dead":
            dead.append((n, len(ks), len(ks) - len(unread)))
        elif unread:
            rows.append((n, len(unread), len(ks), reachable[n]))

    print(f"可達劇本：已讀 {stat['reach_read']}/{stat['reach_total']},白字 {stat['reach_unread']}")
    print(f"不可達劇本：{len(dead)} 份共 {stat['dead_total']} 句"
          f"(其中 {stat['dead_read']} 句已讀 → 這些是可達性分析漏判的證據)")
    known = {k for ks in db["keys"].values() for k in ks}
    print(f"存檔裡 {len(read - known)} 句已讀 key 不在現版劇本中(舊版改掉的台詞，忽略)")
    print()
    rows.sort(key=lambda r: -r[1])
    print(f"{'劇本':<32}{'白字':>6}{'總句':>6}  入口")
    for n, u, t, src in rows[:top]:
        print(f"{n:<32}{u:>6}{t:>6}  {src}")
    if len(rows) > top:
        print(f"... 還有 {len(rows) - top} 份(--top 0 全列)")
    if show_unreachable:
        print("\n不可達(已讀數 > 0 的排前面):")
        for n, t, r in sorted(dead, key=lambda d: (-d[2], -d[1])):
            print(f"  {n:<32}{t:>6} 句，已讀 {r}")


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--game", default=GAME)
    ap.add_argument("--save", help="Save_universe.json(預設自動找最新)")
    ap.add_argument("--cache", default=os.path.join(os.path.dirname(__file__), "lom-db.json"))
    ap.add_argument("--rebuild", action="store_true", help="遊戲更新後重建劇本快取")
    ap.add_argument("--top", type=int, default=40)
    ap.add_argument("--unreachable", action="store_true", help="列出判定為不可達的劇本")
    a = ap.parse_args()

    if a.rebuild or not os.path.exists(a.cache):
        db = build(a.game)
        with open(a.cache, "w", encoding="utf-8") as f:
            json.dump(db, f, ensure_ascii=False)
    with open(a.cache, encoding="utf-8") as f:
        db = json.load(f)
    with open(a.save or find_save(), encoding="utf-8-sig") as f:
        read = set(json.load(f)["ReadStoryData"])
    report(db, read, a.top if a.top > 0 else 10**9, a.unreachable)


if __name__ == "__main__":
    main()
