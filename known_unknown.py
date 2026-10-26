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
# PositionType 的 [StringValue];畫面上的名字在在地化表 Position/Name/<值>(enum 名「校場」畫面上是「練功場」)
POSITION_KEYS = ["", "Mall", "Center", "Alchemy", "Forge", "BackMountain", "Room1", "Door", "Study", "Kitchen", "Room2", "Secret"]
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
CAST_RE = re.compile(r'characters\.Get\("([^"]+)"\)')
NOT_CAST = re.compile(r"player|unknown|pigeon")   # 主角自己、？？？、信鴿不算登場人物
# 只有這些呼叫的參數才是劇本名；ShowPicture/SetFlag/Switch 的參數常剛好和劇本同名，不能算
SCRIPT_RE = re.compile(r'luamanager\.(?:SetNextScript|SetCurrentStoryScript|SetCurrentTravelScript)\("([A-Za-z0-9_]+)"'
                       r'|luamanager\.SetTempScript\(\d+, "([A-Za-z0-9_]+)"')
POSITION_RE = re.compile(r'checkpointmanager\.Position\("([A-Za-z0-9_]+)"')
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
        self.script = {}                           # (檔名, path_id) -> (組件, 完整類別名)
        self.objs = {}                             # (檔名, path_id) -> 物件
        self.by_cls = collections.defaultdict(list)
        for obj in self.env.objects:
            if obj.type.name == "MonoBehaviour":
                k = (obj.assets_file.name, obj.path_id)
                sc = obj.read(check_read=False).m_Script.read()
                self.cls[k] = sc.m_ClassName
                self.script[k] = (sc.m_AssemblyName, f"{sc.m_Namespace}.{sc.m_ClassName}".lstrip("."))
                self.objs[k] = obj
                self.by_cls[self.cls[k]].append(obj)
        # 型別樹要在讀完類別之後才掛，掛了之後 m_Script 會解不開
        gen = TypeTreeGenerator("2020.3.49f1")
        gen.load_local_game(os.path.dirname(game))
        self.env.typetree_generator = gen
        self.gen = gen
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

    def texts(self, lang="ChineseTraditional"):
        """LeanLanguageCSV 的在地化表：Story/<台詞key>、Character/<角色>、Position/Name/<地點> ..."""
        out = {}
        for obj in self.by_cls["LeanLanguageCSV"]:
            t = self.tt(obj)
            if t["Language"] == lang:
                out.update((e["Name"], e["Text"]) for e in t["entries"])
        return out


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


def mission_entries(a, cond, pos):
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
        yield script, {"kind": "任務", "id": t["_key"], "where": pos[t["_position"]],
                       "when": when, "cond": " 且 ".join(texts), "possible": possible,
                       **({} if t["_active"] else {"why": "任務停用(_active=0)"})}


def position_entries(a, pos):
    for obj in a.by_cls["FreePositionData"]:
        t = a.tt(obj)
        if t["_defaultScript"]:
            yield t["_defaultScript"].lower(), {"kind": "地點", "id": t["m_Name"],
                                                "where": t["m_Name"].split("_")[-1], "possible": True}
    for obj in a.by_cls["PositionController"]:
        t = a.tt(obj)
        if t["_scriptName"]:
            yield t["_scriptName"].lower(), {"kind": "地點", "id": t["_scriptName"],
                                             "where": pos[t["_position"]], "possible": True}


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
    tw = a.texts()
    pos = [tw.get(f"Position/Name/{k}", k) for k in POSITION_KEYS]

    entries = collections.defaultdict(list)   # 劇本 -> 入口(遊戲資料直接觸發)
    entries[NEW_GAME_SCRIPT].append({"kind": "開局", "id": "新遊戲", "possible": True})
    for script, e in [*mission_entries(a, cond, pos), *position_entries(a, pos)]:
        if script in names:
            entries[script].append(e)

    linked = position_results(a, cond)
    edges = {}                                  # 劇本 -> [(下一個劇本, 經由的入口或 None)]
    for n, t in scripts.items():
        out = {}
        for s in (a or b for a, b in SCRIPT_RE.findall(t)):
            if s.lower() in names and s.lower() != n:
                out.setdefault(s.lower(), None)
        for s in map(str.lower, POSITION_RE.findall(t)):
            for target, e in linked.get(s, []):
                if target in names and target != n:
                    out.setdefault(target, e)
        edges[n] = sorted(out.items())
    keys = {n: list(dict.fromkeys(KEY_RE.findall(t))) for n, t in scripts.items()}
    info = {n: scene_info(t, keys[n], tw) for n, t in scripts.items() if keys[n]}
    return {"keys": keys, "edges": edges, "entries": entries, "info": info}


def scene_info(text, keys, tw):
    """玩家認得出來的描述：登場人物(出場次數多的在前)與開頭第一句。"""
    cast = collections.Counter(c for c in CAST_RE.findall(text) if not NOT_CAST.search(c))
    names = list(dict.fromkeys(tw.get(f"Character/{c}", c) for c, _ in cast.most_common()))
    first = " ".join(tw.get(f"Story/{keys[0]}", "").split())
    return {"cast": names[:4], "first": first[:40]}


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


def report(db, read, top, show_unreachable, only, spoil):
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
            print(f"  ⚠ {'、'.join(db['info'][n]['cast'])} [{n}] 已讀 {r}/{t} 但判為不可達"
                  "(舊版或 mod 強制觸發？細節見 --unreachable)")
    known = {k for ks in db["keys"].values() for k in ks}
    print(f"存檔裡 {len(read - known)} 句已讀 key 不在現版劇本中(舊版改掉的台詞，忽略)\n")

    print(f"{'分類':<16}{'白字':>7}{'總句':>7}")
    for c, (u, t) in sorted(cat.items(), key=lambda x: -x[1][0]):
        if u:
            print(f"{c:<16}{u:>7}{t:>7}")
    print()

    rows.sort(key=lambda r: -r[1])
    for n, u, t in rows[:top]:
        src, hops = origin(seen, n)
        inf = db["info"][n]
        cast = "、".join(inf["cast"]) or "(無其他角色)"
        print(f"■ {cast}  白字 {u}/{t}  [{n}]")
        if spoil and inf["first"]:
            print(f"    開頭：「{inf['first']}」")
        print(f"    觸發：{src}" + (f",之後再過 {hops} 段劇情" if hops else ""))
    if len(rows) > top:
        print(f"... 還有 {len(rows) - top} 份(--top 0 全列)")

    if show_unreachable:
        print("\n不可達:")
        for n, t, r in sorted(dead, key=lambda d: (-d[2], -d[1])):
            why = "; ".join(f'{describe(e)} → {e.get("why", "條件不可能成立")}'
                            for e in db["entries"].get(n, []))
            cast = "、".join(db["info"][n]["cast"])
            print(f"  {cast} [{n}] {t} 句，已讀 {r}" + (f"\n    {why}" if why else "  (沒有入口)"))


def write_system_flags(game, path):
    """插件執行時只看得到 Lua 的 SetFlag/AddFlag;Lua 以外(戰鬥、商店、設施…)寫入的旗標由這裡產生清單。
    兩邊都沒有的旗標 = 現版本拿不到，插件不提示。遊戲更新後重跑。"""
    assets = Assets(game)
    scripts = load_scripts(game)
    lua = set()
    for t in scripts.values():
        lua |= {s.lower() for s in FLAG_WRITE_RE.findall(t)}
    names = sorted({assets.tt(o)["m_Name"] for o in assets.by_cls["FlagData"]})
    written = written_flags(assets, scripts)
    system = [n for n in names if n.lower() in written and n.lower() not in lua]
    with open(path, "w", encoding="utf-8") as f:
        f.write("// 由 known_unknown.py --system-flags 產生，別手改。遊戲更新後重跑。\n")
        f.write("namespace KnownUnknown\n{\n    internal static class SystemFlags\n    {\n")
        f.write("        // Lua 以外(戰鬥、商店、設施、任務…)會寫入的旗標\n")
        f.write("        public static readonly string[] Names =\n        {\n")
        for n in system:
            f.write(f'            "{n}",\n')
        f.write("        };\n    }\n}\n")
    print(f"{len(system)} 個旗標 → {path}")


# 地點事件表在劇情場景(Story)的資產裡，地圖上沒載入。抽出事件表和它引用的同檔物件，插件在地圖上照原樣重建；
# 引用到常駐資產(globalgamemanagers.assets:屬性、旗標、書…)的，用 類別+名字 接回遊戲裡的活物件。
POSITION_FILE = "sharedassets2.assets"
RESIDENT_FILE = "globalgamemanagers.assets"
PRELOAD_ROOTS = ["PositionResultData", "SwitchResultData", "ConditionResultData"]
SKIP_FIELDS = {"m_GameObject", "m_Enabled", "m_Script", "m_Name", "m_EditorHideFlags", "m_EditorClassIdentifier"}


def write_position_tables(game, path):
    a = Assets(game)
    resident = collections.Counter((a.cls[k], o.peek_name()) for k, o in a.objs.items() if k[0] == RESIDENT_FILE)
    ids, objects, externals, ext_ids = {}, [], [], {}
    # 地點事件表，加上劇本分支用的 Switch / Condition 表(地圖上缺了它們，分支會全當成可能成立)
    queue = [o for c in PRELOAD_ROOTS for o in a.by_cls[c] if o.assets_file.name == POSITION_FILE]

    def ref(src, ptr):
        if not ptr["m_PathID"]:
            return None
        k = a.key(src, ptr)
        if k[0] == RESIDENT_FILE:
            cn = (a.cls[k], a.objs[k].peek_name())
            if resident[cn] != 1:
                sys.exit(f"常駐物件 {cn} 名字不唯一，無法對應")
            if cn not in ext_ids:
                ext_ids[cn] = len(externals)
                externals.append({"type": cn[0], "name": cn[1]})
            return f"e{ext_ids[cn]}"
        if k[0] != POSITION_FILE or k not in a.objs:
            sys.exit(f"引用到不支援的檔案/物件 {k}")
        if k not in ids:
            ids[k] = len(ids)
            queue.append(a.objs[k])
        return f"i{ids[k]}"

    # JsonUtility 執行期不解 instanceID:引用另外記成「欄位路徑=目標」，插件用反射設
    def plain(node, val, src, path, refs):
        # 型別樹產生器把 bool 寫成 UInt8;這些類別沒有 byte 欄位(看過反編譯)。JsonUtility 的 bool 要 true/false
        if node.m_Type in ("bool", "UInt8"):
            return bool(val)
        if node.m_Type.startswith("PPtr<"):
            target = ref(src, val)
            if target:
                refs.append(f"{path}={target}")
            return {"instanceID": 0}
        if isinstance(val, list):
            data = node.m_Children[0].m_Children[1]
            return [plain(data, v, src, f"{path}[{i}]", refs) for i, v in enumerate(val)]
        if isinstance(val, dict):
            return {c.m_Name: plain(c, val[c.m_Name], src, f"{path}.{c.m_Name}".lstrip("."), refs)
                    for c in node.m_Children if c.m_Name in val and c.m_Name not in SKIP_FIELDS}
        return val

    for o in queue:
        ids.setdefault((o.assets_file.name, o.path_id), len(ids))
    i = 0
    while i < len(queue):
        o = queue[i]
        k = (o.assets_file.name, o.path_id)
        asm, full = a.script[k]
        node = a.gen.get_nodes_up(asm, full)
        t = a.tt(o)
        refs = []
        body = json.dumps(plain(node, t, o, "", refs), ensure_ascii=False, separators=(",", ":"))
        objects.append({"id": ids[k], "type": full, "name": t["m_Name"], "json": body, "refs": "|".join(refs)})
        i += 1
    objects.sort(key=lambda x: x["id"])
    # 外層不用 JSON:插件的 JsonUtility 只認遊戲組件裡的型別。一行一筆、tab 分隔(json.dumps 會跳脫 tab/換行)
    with open(path, "w", encoding="utf-8") as f:
        f.write(f"{os.path.getsize(f'{game}/{POSITION_FILE}')}\n")
        for e in externals:
            f.write(f"E\t{e['type']}\t{e['name']}\n")
        for o in objects:
            f.write(f"O\t{o['type']}\t{o['name']}\t{o['json']}\t{o['refs']}\n")
    print(f"{len(objects)} 個物件、{len(externals)} 個常駐引用 → {path}")


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--game", default=GAME)
    ap.add_argument("--save", help="Save_universe.json(預設自動找最新)")
    ap.add_argument("--cache", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "ku-db.json"))
    ap.add_argument("--rebuild", action="store_true", help="遊戲更新後重建劇本快取")
    ap.add_argument("--top", type=int, default=40)
    ap.add_argument("--only", help="只列某分類，例如「第8章」「支線」「談話」")
    ap.add_argument("--unreachable", action="store_true", help="列出判定為不可達的劇本與原因")
    ap.add_argument("--spoil", action="store_true", help="顯示每段白字的開頭第一句(輕微劇透)")
    ap.add_argument("--dump-scripts", metavar="DIR", help="把劇本(story/chinesetraditional)匯出成 DIR/<名>.lua 後結束")
    ap.add_argument("--system-flags", metavar="CS", help="產生插件用的「只由遊戲系統寫入的旗標」清單(C#)後結束")
    ap.add_argument("--position-tables", metavar="JSON", help="產生插件用的地點事件表資料(地圖上預載，plugin/PositionTables.txt)後結束")
    a = ap.parse_args()

    if a.position_tables:
        write_position_tables(a.game, a.position_tables)
        return

    if a.system_flags:
        write_system_flags(a.game, a.system_flags)
        return

    if a.dump_scripts:
        os.makedirs(a.dump_scripts, exist_ok=True)
        for n, t in load_scripts(a.game).items():
            with open(os.path.join(a.dump_scripts, n + ".lua"), "w", encoding="utf-8") as f:
                f.write(t)
        return

    if a.rebuild or not os.path.exists(a.cache):
        db = build(a.game)
        with open(a.cache, "w", encoding="utf-8") as f:
            json.dump(db, f, ensure_ascii=False)
    with open(a.cache, encoding="utf-8") as f:
        db = json.load(f)
    with open(a.save or find_save(), encoding="utf-8-sig") as f:
        read = set(json.load(f)["ReadStoryData"])
    report(db, read, a.top if a.top > 0 else 10**9, a.unreachable, a.only, a.spoil)


if __name__ == "__main__":
    main()
