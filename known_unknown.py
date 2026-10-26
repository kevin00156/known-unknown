"""多玩10塊錢：活俠傳白字(未讀劇情)盤點。

全部台詞 key − Save_universe 的 ReadStoryData = 白字。
可達性：劇本與資料表建成引用圖，從遊戲資料的入口做 BFS,圖外的劇本視為現版本拿不到。
條件裡用到「整個遊戲沒有任何地方會寫入」的旗標時，以旗標預設值求值；求出來永遠不成立的入口不算數。
"""
import argparse
import collections
import glob
import json
import os
import re
import struct
import sys

import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

STEAM = os.path.expanduser("~/.steam/debian-installation/steamapps")
GAME = f"{STEAM}/common/LegendOfMortal/Mortal_Data"
SAVE_GLOB = (f"{STEAM}/compatdata/1859910/pfx/drive_c/users/steamuser/AppData/"
             "LocalLow/Obb Studio/Mortal/*/Save_universe.json")
SCRIPT_DIR = "story/chinesetraditional/"   # LuaManager 只載這個；chinesesimplified 是舊草稿
NEW_GAME_SCRIPT = "ch1_1"                  # SaveSystem.NewGameData()

# 遊戲 enum(反編譯 Mortal.Core)
POSITION = ["", "正心堂", "校場", "煉丹房", "鍛冶場", "後山", "弟子房", "大門", "講經堂", "伙房", "女弟子房", "神秘房子"]
STAGE = {1: "上旬", 2: "中旬", 3: "下旬"}
COMPARE = ["=", "!=", "<", "<=", ">", ">="]
CHECK_CONST, CHECK_FLAG = 0, 3            # StatCheckType.常數 / 旗標
# C# 直接寫死名字的旗標(SaveSystem / MissionManagerData)
CODE_WRITTEN_FLAGS = {"demo", "newgameplus", "alpha_001"}
# 指到 FlagData 但只讀不寫的類別(看過反編譯確認)。不在這裡的類別一律當成會寫。
FLAG_READERS = {"StatGroupVariable", "FlagData",
                "FlagCollectionData",           # 總表；依名字寫入的路徑已由 Lua SetFlag/AddFlag 涵蓋
                "TestFlagStatPanel",            # 除錯面板
                "CombatEventConditionPlayerStat", "CombatEventConditionRoundFlag",
                "DiceResultData", "FlagDataRateConvertData"}

KEY_RE = re.compile(r'GetStoryText\("([^"]+)"\)')
STR_RE = re.compile(r'"([A-Za-z0-9_]+)"')
FLAG_WRITE_RE = re.compile(r'statmodifymanager\.(?:SetFlag|AddFlag)\("([^"]+)"')


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


class Assets:
    """所有序列化檔一次載入(跨檔 PPtr 才解得開),MonoBehaviour 依類別索引。"""

    def __init__(self, game):
        files = [f"{game}/{f}" for f in sorted(os.listdir(game))
                 if f.endswith(".assets") or re.fullmatch(r"level\d+|globalgamemanagers", f)]
        self.env = UnityPy.load(*files)
        self.cls = {}                              # (檔名, path_id) -> 類別
        self.objs = {}                             # (檔名, path_id) -> 物件
        self.by_cls = collections.defaultdict(list)
        for obj in self.env.objects:
            if obj.type.name == "MonoBehaviour":
                k = (obj.assets_file.name, obj.path_id)
                self.cls[k] = obj.read(check_read=False).m_Script.read().m_ClassName
                self.objs[k] = obj
                self.by_cls[self.cls[k]].append(obj)
        # 型別樹要在讀完類別之後才掛，掛了之後 m_Script 會解不開
        gen = TypeTreeGenerator("2020.3.49f1")
        gen.load_local_game(os.path.dirname(game))
        self.env.typetree_generator = gen
        self._tt = {}

    def tt(self, obj):
        k = (obj.assets_file.name, obj.path_id)
        if k not in self._tt:
            self._tt[k] = obj.read_typetree()
        return self._tt[k]

    def key(self, src, ptr):
        fid = ptr["m_FileID"]
        f = src.assets_file.name if fid == 0 else os.path.basename(src.assets_file.externals[fid - 1].path)
        return f, ptr["m_PathID"]

    def deref(self, src, ptr):
        return self.objs.get(self.key(src, ptr)) if ptr["m_PathID"] else None


def written_flags(assets, scripts):
    """會被寫入的旗標名(小寫)。寧可多算：多算只會讓「不可能」判得比較少，不會誤殺。"""
    out = set(CODE_WRITTEN_FLAGS)
    for t in scripts.values():
        out |= {s.lower() for s in FLAG_WRITE_RE.findall(t)}
    flags = {(o.assets_file.name, o.path_id): assets.tt(o)["m_Name"].lower() for o in assets.by_cls["FlagData"]}
    # 不在 FLAG_READERS 的物件，只要用 PPtr 直接指到 FlagData,就當它會寫
    # (FlagModifyStat、CombatResultFlag、FacilityModifyItem、ProgressLevelData、FateBonusFlag ...)
    for k, obj in assets.objs.items():
        if assets.cls[k] in FLAG_READERS:
            continue
        raw = obj.get_raw_data()
        ext = [os.path.basename(e.path) for e in obj.assets_file.externals]
        for i in range(0, len(raw) - 11, 4):
            fid, pid = struct.unpack_from("<iq", raw, i)
            if 0 <= fid <= len(ext) and pid:
                f = obj.assets_file.name if fid == 0 else ext[fid - 1]
                if (f, pid) in flags:
                    out.add(flags[(f, pid)])
    return out


class Conditions:
    """ConditionResultItem → (人看得懂的文字, 有沒有可能成立)。"""

    def __init__(self, assets, written):
        self.a, self.written = assets, written

    def _sgv(self, src, ptr):
        obj = self.a.deref(src, ptr)
        return (obj, self.a.tt(obj)) if obj else (None, None)

    def value(self, src, ptr):
        """靜態可知的值；不可知回 None。"""
        obj, t = self._sgv(src, ptr)
        if not t or len(t["_value"]) != 1:
            return None
        v = t["_value"][0]
        sign = -1 if v["_negative"] else 1
        if v["_checkType"] == CHECK_CONST:
            return sign * v["_constant"]
        if v["_checkType"] == CHECK_FLAG:
            flag = self.a.deref(obj, v["_flag"])
            ft = flag and self.a.tt(flag)
            if ft and ft["m_Name"].lower() not in self.written:
                return sign * ft["_default"]
        return None

    def text(self, src, ptr):
        obj, t = self._sgv(src, ptr)
        if not t:
            return "?"
        name = t["m_Name"]
        if name.startswith("常數_"):
            return name[3:]
        if len(t["_value"]) == 1 and t["_value"][0]["_checkType"] == CHECK_FLAG:
            flag = self.a.deref(obj, t["_value"][0]["_flag"])
            note = flag and self.a.tt(flag)["_devNote"]
            if note:
                return f"{name}({' '.join(note.split())})"
        return name

    def item(self, src, it):
        op = it["_compareOp"]
        text = f'{self.text(src, it["_value1"])}{COMPARE[op]}{self.text(src, it["_value2"])}'
        a, b = self.value(src, it["_value1"]), self.value(src, it["_value2"])
        if a is None or b is None:
            return text, True
        return text, [a == b, a != b, a < b, a <= b, a > b, a >= b][op]

    def group(self, src, grp):
        """GameStatUtils.LogicOp 語意：空清單 = false,AND/OR 逐項。"""
        res = [self.item(src, it) for it in grp["_items"]]
        if not res:
            return "", False
        ok = [p for _, p in res]
        possible = all(ok) if grp["_logicOp"] == 0 else any(ok)
        return (" 且 " if grp["_logicOp"] == 0 else " 或 ").join(t for t, _ in res), possible


def game_time(t):
    s = f'第{t["Year"]}年{t["Month"]}月{STAGE.get(t["Stage"], "")}'
    return s + ("晚上" if t.get("DayEnvironment") == 2 else "")


def mission_entries(a, cond):
    for obj in a.by_cls["MissionCheckData"]:
        t = a.tt(obj)
        script = t["_startScript"].lower()
        if not script:
            continue
        texts, possible = [], bool(t["_active"])
        for g in t["_conditions"]:
            txt, ok = cond.group(obj, g)
            possible &= ok
            if txt:
                texts.append(txt)
        when = ""
        if t["_dayCheck"]:
            when = {1: game_time(t["_specificTime"]),
                    2: f'{game_time(t["_minTime"])}~{game_time(t["_maxTime"])}',
                    3: f'每月{STAGE.get(t["_periodStage"], "")}'}.get(t["_timeCheckType"], "")
        yield script, {"kind": "任務", "id": t["_key"], "where": POSITION[t["_position"]],
                       "when": when, "cond": " 且 ".join(texts), "possible": possible,
                       **({} if t["_active"] else {"why": "任務停用(_active=0)"})}


def position_entries(a):
    for obj in a.by_cls["FreePositionData"]:
        t = a.tt(obj)
        if t["_defaultScript"]:
            yield t["_defaultScript"].lower(), {"kind": "地點", "id": t["m_Name"],
                                                "where": t["m_Name"].split("_")[-1], "possible": True}
    for obj in a.by_cls["PositionController"]:
        t = a.tt(obj)
        if t["_scriptName"]:
            yield t["_scriptName"].lower(), {"kind": "地點", "id": t["_scriptName"],
                                             "where": POSITION[t["_position"]], "possible": True}


def position_results(a, cond):
    """PositionResultData 名 -> [(劇本, 入口)]。劇本名是執行時拼的：_scriptPrefix + "_" + StoryMappingItem.Value。"""
    out = collections.defaultdict(list)
    for obj in a.by_cls["PositionResultData"]:
        t = a.tt(obj)
        for p in t["_list"]:
            ev = a.deref(obj, p)
            if not ev:
                continue
            e = a.tt(ev)
            mapping = a.deref(ev, e["_config"])
            if not mapping:
                continue
            txt, possible = cond.group(ev, e["_activeCondition"]) if e["_toggleCondition"] else ("", True)
            out[t["m_Name"].lower()].append((
                f'{t["_scriptPrefix"]}_{a.tt(mapping)["Value"]}'.lower(),
                {"kind": "地點事件", "id": e["m_Name"], "rate": e["_defaultRate"],
                 "cond": txt, "possible": possible}))
    return out


def build(game):
    scripts = load_scripts(game)
    names = set(scripts)   # Resources 路徑全小寫,Resources.Load 不分大小寫，比對一律小寫
    a = Assets(game)
    cond = Conditions(a, written_flags(a, scripts))

    entries = collections.defaultdict(list)   # 劇本 -> 入口(遊戲資料直接觸發)
    entries[NEW_GAME_SCRIPT].append({"kind": "開局", "id": "新遊戲", "possible": True})
    for script, e in [*mission_entries(a, cond), *position_entries(a)]:
        if script in names:
            entries[script].append(e)

    linked = position_results(a, cond)
    edges = {}                                  # 劇本 -> [(下一個劇本, 經由的入口或 None)]
    for n, t in scripts.items():
        out = {}
        for s in map(str.lower, STR_RE.findall(t)):
            if s in names and s != n:
                out.setdefault(s, None)
            for target, e in linked.get(s, []):
                if target in names and target != n:
                    out.setdefault(target, e)
        edges[n] = sorted(out.items())
    keys = {n: list(dict.fromkeys(KEY_RE.findall(t))) for n, t in scripts.items()}
    return {"keys": keys, "edges": edges, "entries": entries}


def reach(db):
    """BFS,只走「可能成立」的入口與邊。回傳 劇本 -> (上一個劇本 或 None, 入口)。"""
    seen = {}
    for n, es in db["entries"].items():
        ok = [e for e in es if e["possible"]]
        if ok:
            seen[n] = (None, ok[0])
    queue = collections.deque(seen)
    while queue:
        n = queue.popleft()
        for m, e in db["edges"].get(n, []):
            if m not in seen and (e is None or e["possible"]):
                seen[m] = (n, e)
                queue.append(m)
    return seen


def describe(e):
    parts = [e["kind"], e.get("id", "")]
    if e.get("where"):
        parts.append("@" + e["where"])
    if e.get("when"):
        parts.append(e["when"])
    if e.get("rate"):
        parts.append(f'基礎機率{e["rate"]}%')
    s = " ".join(p for p in parts if p)
    if e.get("cond"):
        s += f' [{e["cond"]}]'
    return s


def origin(seen, n):
    """往回追到資料入口，回傳 (入口說明, 中間經過幾份劇本)。"""
    hops, cur = 0, n
    while seen[cur][0] is not None:
        if seen[cur][1] is not None:          # 中途的地點事件也值得講
            return describe(seen[cur][1]) + f"(由 {seen[cur][0]})", hops
        cur, hops = seen[cur][0], hops + 1
    return describe(seen[cur][1]), hops


def category(n):
    m = re.match(r"ch(\d+)_", n)
    if m:
        return f"主線 第{m.group(1)}章"
    if re.match(r"s\d{4}", n):
        return "支線事件 S" + n[1:3]
    if n.startswith("section_"):
        return "自由行動"
    if n.startswith("talk_"):
        return "談話 " + n.split("_")[1]
    if n.startswith("travel"):
        return "下山遊歷"
    return "養成/其他"


def find_save():
    saves = sorted(glob.glob(SAVE_GLOB), key=os.path.getmtime)
    if not saves:
        sys.exit("找不到 Save_universe.json,用 --save 指定")
    return saves[-1]


def report(db, read, top, show_unreachable, only):
    seen = reach(db)
    rows, dead = [], []
    stat = collections.Counter()
    cat = collections.defaultdict(lambda: [0, 0])
    for n, ks in db["keys"].items():
        if not ks:
            continue
        unread = sum(k not in read for k in ks)
        bucket = "reach" if n in seen else "dead"
        stat[bucket + "_total"] += len(ks)
        stat[bucket + "_unread"] += unread
        if bucket == "dead":
            dead.append((n, len(ks), len(ks) - unread))
            continue
        c = cat[category(n)]
        c[0] += unread
        c[1] += len(ks)
        if unread and (not only or only in category(n)):
            rows.append((n, unread, len(ks)))

    print(f"可達：已讀 {stat['reach_total'] - stat['reach_unread']}/{stat['reach_total']},"
          f"白字 {stat['reach_unread']}")
    print(f"不可達：{len(dead)} 份共 {stat['dead_total']} 句,"
          f"其中已讀 {stat['dead_total'] - stat['dead_unread']} 句")
    for n, t, r in dead:
        if r:   # 舊版遊戲、改檔/強制觸發事件的 mod 看過，或分析真的漏了——值得人工看一眼
            print(f"  ⚠ {n} 已讀 {r}/{t} 但判為不可達(舊版或 mod 強制觸發？細節見 --unreachable)")
    known = {k for ks in db["keys"].values() for k in ks}
    print(f"存檔裡 {len(read - known)} 句已讀 key 不在現版劇本中(舊版改掉的台詞，忽略)\n")

    print(f"{'分類':<16}{'白字':>7}{'總句':>7}")
    for c, (u, t) in sorted(cat.items(), key=lambda x: -x[1][0]):
        if u:
            print(f"{c:<16}{u:>7}{t:>7}")
    print()

    rows.sort(key=lambda r: -r[1])
    print(f"{'劇本':<28}{'白字':>5}{'總句':>5}  入口")
    for n, u, t in rows[:top]:
        src, hops = origin(seen, n)
        print(f"{n:<28}{u:>5}{t:>5}  {src}" + (f" →{hops}層" if hops else ""))
    if len(rows) > top:
        print(f"... 還有 {len(rows) - top} 份(--top 0 全列)")

    if show_unreachable:
        print("\n不可達:")
        for n, t, r in sorted(dead, key=lambda d: (-d[2], -d[1])):
            why = "; ".join(f'{describe(e)} → {e.get("why", "條件不可能成立")}'
                            for e in db["entries"].get(n, []))
            print(f"  {n:<28}{t:>5} 句，已讀 {r}" + (f"  {why}" if why else "  (沒有入口)"))


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--game", default=GAME)
    ap.add_argument("--save", help="Save_universe.json(預設自動找最新)")
    ap.add_argument("--cache", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "ku-db.json"))
    ap.add_argument("--rebuild", action="store_true", help="遊戲更新後重建劇本快取")
    ap.add_argument("--top", type=int, default=40)
    ap.add_argument("--only", help="只列某分類，例如「第8章」「支線」「談話」")
    ap.add_argument("--unreachable", action="store_true", help="列出判定為不可達的劇本與原因")
    a = ap.parse_args()

    if a.rebuild or not os.path.exists(a.cache):
        db = build(a.game)
        with open(a.cache, "w", encoding="utf-8") as f:
            json.dump(db, f, ensure_ascii=False)
    with open(a.cache, encoding="utf-8") as f:
        db = json.load(f)
    with open(a.save or find_save(), encoding="utf-8-sig") as f:
        read = set(json.load(f)["ReadStoryData"])
    report(db, read, a.top if a.top > 0 else 10**9, a.unreachable, a.only)


if __name__ == "__main__":
    main()
