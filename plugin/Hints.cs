using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Mortal.Core;
using Mortal.Free;
using Mortal.Story;
using UnityEngine;

namespace KnownUnknown
{
    internal enum CountUnit
    {
        Events,
        Lines,
        Both,
    }

    // 沒看過的內容有多少：事件 = 還有白字的入口(地點結果、任務、跳轉劇本),句 = 白字台詞
    internal struct Tally
    {
        public int Events;
        public int Lines;

        public static CountUnit Unit = CountUnit.Both;

        public Tally(int events, int lines)
        {
            Events = events;
            Lines = lines;
        }

        public static Tally operator +(Tally a, Tally b) => new Tally(a.Events + b.Events, a.Lines + b.Lines);

        public bool Any => Lines > 0;

        public override string ToString() =>
            Unit == CountUnit.Events ? $"{Events} 段"
            : Unit == CountUnit.Lines ? $"{Lines} 句"
            : $"{Events} 段・{Lines} 句";
    }

    // 條件不足 / 現版本未開放用顏色分，不另外寫字(IMGUI rich text;日誌用 Plain 去掉)
    internal static class Paint
    {
        public const string Lacking = "#B48C5A";   // 條件不足：暗一點的土黃，不搶眼
        public const string Closed = "#8C8C8C";    // 現版本未開放：灰

        public static string Color(string color, string s) => $"<color={color}>{s}</color>";

        public static string Plain(string s) => Regex.Replace(s, "</?color[^>]*>", "");
    }

    // 一個入口的白字：現在走得到的，和被條件擋住的(原因 → 白字)
    internal class Outlook
    {
        public readonly HashSet<string> Open = new HashSet<string>();
        public readonly Dictionary<string, HashSet<string>> Locked = new Dictionary<string, HashSet<string>>();
        public readonly HashSet<string> Closed = new HashSet<string>();   // Locked 裡現版本拿不到的原因

        public Tally Count => new Tally(Open.Count > 0 ? 1 : 0, Open.Count);

        // 「1 段・3 句：小師妹好感 ≥ 20(目前 14)」,橘 = 條件不足、灰 = 現版本未開放;what 放最前面(「抽到「其他」」)
        public IEnumerable<string> LockedLines(string what = "") =>
            Locked.Select(x => Paint.Color(Closed.Contains(x.Key) ? Paint.Closed : Paint.Lacking, $"{what}{new Tally(1, x.Value.Count)}:{x.Key}"));
    }

    internal class Hint
    {
        public string Where;   // 去哪裡、選哪個行動
        public string Chance;  // 這一趟看到白字的機率
        public string Detail;  // 沒看過的結果 / 登場人物
        public int Rank;       // 排序用：越大越前面
        public Tally Count;
        public List<string> Locked = new List<string>();   // 條件不足的白字(現版本未開放的要 ShowLocked)
        public bool Flag;      // 「本旬拿得到、之後解鎖白字」的旗標提示
        public List<Outcome> Outcomes = new List<Outcome>();   // 地點：各結果；旗標：取得處
    }

    // 選單上一個選項的提示
    internal class OptionHint
    {
        public string Text;                                // 「白字 1 段・2 句」「18% 抽到白字 …」「已看完」
        public List<string> Locked = new List<string>();
        public string Detail = "";                         // 日誌用：算進去的白字 key
    }

    // 地點事件表抽一次的結果
    internal struct Roll
    {
        public int Chance;              // 抽到還有白字的結果的機率(%)
        public Tally Count;             // 每個還有白字的結果算一段
        public List<string> Unseen;     // 「失敗 25%(唐中翎)」
        public List<string> Locked;     // 「抽到「其他」1 段・3 句:…」
        public List<Outcome> Outcomes;  // 還有白字(含未開放)的結果，F2 清單逐一列出、可以勾選指定
    }

    // 地點事件表的一個結果：勾選指定的單位
    internal class Outcome
    {
        public PositionResultData Table;
        public string Type;                  // 結果代碼(ResultType)
        public string Label;                 // 「抽到「其他」18%」或旗標的取得處
        public Tally Count;                  // 現在看得到的白字
        public List<string> Locked = new List<string>();
    }

    // 地圖上的一個入口：去哪個地點、在選單選哪一項，會抽到哪張事件表
    internal class Place
    {
        public string Where;              // 「去伙房 →「掌廚」」
        public FreePositionData Data;
        public string Action;             // 通往事件表的選單選項(劇本原文);沒有選單 = null
        public PositionResultData Table;
    }

    // 依當下遊戲狀態算提示。
    internal class Hints
    {
        private readonly StoryIndex _index;
        private readonly Cond _cond = new Cond();
        private readonly Dictionary<string, PositionResultData> _results = new Dictionary<string, PositionResultData>();
        private readonly HashSet<string> _systemFlags = new HashSet<string>(SystemFlags.Names);
        // 劇本 → 玩家看得懂的入口說明(「伙房「掌廚」抽到「其他」」),說旗標在哪裡拿用
        private Dictionary<string, string> _labels;

        // 也列出現版本拿不到的白字(條件永遠不會成立；劇透)
        public static bool ShowLocked;

        // PositionType 的 [StringValue];畫面上的名字在 Position/Name/<值>(enum 名「校場」畫面上是「練功場」)
        private static readonly string[] PlaceKeys =
        {
            "", "Mall", "Center", "Alchemy", "Forge", "BackMountain", "Room1", "Door", "Study", "Kitchen", "Room2", "Secret",
        };

        private static readonly HashSet<string> NoFlags = new HashSet<string>();

        public Hints(StoryIndex index)
        {
            _index = index;
            Capture();
        }

        // 地點事件表、Switch/Condition 表在劇情場景裡，只有跑劇情時才在記憶體。抓到就留著引用，回到地圖還能用。
        // 還沒進過劇情時用預載的複本(Preload)
        public bool HasResults => _results.Count > 0;

        public void Capture()
        {
            _cond.Capture();
            Preload.Merge(_results, p => p.name.ToLowerInvariant(), (a, b) => Preload.Mismatches(a, b, Rate), "地點事件表");
        }

        // 有自己入口的劇本(任務起點)。算「後續劇情」時走到這裡就停，免得主線一路串到結局。
        // 地點預設劇本不用列：沒有劇本會跳回去(離線工具確認過)。
        public static HashSet<string> StopSet() =>
            new HashSet<string>(Resources.FindObjectsOfTypeAll<MissionCheckData>().Select(m => m.StartScript.ToLowerInvariant()));

        // ---- 一個入口看得到 / 看不到的白字 ----

        // paths:入口走得到的每一項和沿途條件。同一段劇情裡自己會寫入的旗標(暫存旗標、前面選項設的)
        // 現在還沒設，但演到那裡時可能已經設了，所以只用到這些旗標的條件當成可能成立。
        private Outlook Look(IEnumerable<KeyValuePair<Item, Guard[]>> paths)
        {
            List<KeyValuePair<Item, Guard[]>> all = paths.ToList();
            HashSet<string> inStory = new HashSet<string>(all.Where(x => x.Key.Kind == ItemKind.Flag).Select(x => x.Key.Value));
            Outlook o = new Outlook();
            foreach (KeyValuePair<Item, Guard[]> x in all)
            {
                if (x.Key.Kind != ItemKind.Key || ReadStorySystem.Instance.ExistKey(x.Key.Value) || o.Open.Contains(x.Key.Value))
                    continue;
                List<Guard> blockers = x.Value.Where(g => Blocks(g, inStory)).Distinct().ToList();
                if (blockers.Count == 0)
                {
                    o.Open.Add(x.Key.Value);
                    continue;
                }
                // 現版本拿不到的條件(旗標沒有任何地方寫入)：沒勾就當它不存在
                bool closed = blockers.Any(g => _cond.Impossible(g, Obtainable));
                if (closed && !ShowLocked)
                    continue;
                string why = string.Join(";還要 ", blockers.Select(g => _cond.Need(g, FlagSource)).Where(w => w != null).Distinct());
                if (!o.Locked.TryGetValue(why, out HashSet<string> keys))
                    o.Locked[why] = keys = new HashSet<string>();
                if (closed)
                    o.Closed.Add(why);
                keys.Add(x.Key.Value);
            }
            foreach (HashSet<string> keys in o.Locked.Values)
                keys.ExceptWith(o.Open);
            foreach (string why in o.Locked.Where(x => x.Value.Count == 0).Select(x => x.Key).ToList())
                o.Locked.Remove(why);
            return o;
        }

        private Outlook Look(string script) => Look(_index.Paths(script));

        private bool Blocks(Guard g, HashSet<string> inStory)
        {
            if (_cond.Holds(g) != false)
                return false;
            List<FlagData> flags = _cond.Flags(g).ToList();
            return flags.Count == 0 || !flags.All(f => inStory.Contains(f.name));
        }

        // 這個旗標拿得到嗎：Lua 有寫入，或遊戲系統會寫入
        private bool Obtainable(FlagData f) => _index.FlagWriters(f.name).Any() || _systemFlags.Contains(f.name);

        // 旗標去哪裡拿
        private string FlagSource(FlagData f)
        {
            List<string> where = _index.FlagWriters(f.name)
                .Select(s => _labels != null && _labels.TryGetValue(s, out string l) ? l : null)
                .Where(l => l != null).Distinct().Take(2).ToList();
            if (where.Count > 0)
                return "取得：" + string.Join("、", where);
            if (_systemFlags.Contains(f.name))
                return "由戰鬥或遊戲系統設定";
            return _index.FlagWriters(f.name).Any() ? "劇情中取得" : null;
        }

        // ---- 地點事件表 ----

        // 完全照 PositionResultData.GetResult / PositionEvent:依清單順序取機率 > 0 的事件，
        // 同一個結果代碼只算第一筆，在 1..總和 之間均勻抽。
        public Roll RollOf(PositionResultData pr)
        {
            List<KeyValuePair<PositionEventData, int>> events = Events(pr);
            int sum = events.Sum(x => x.Value);
            Roll roll = new Roll { Unseen = new List<string>(), Locked = new List<string>(), Outcomes = new List<Outcome>() };
            int hit = 0;
            foreach (KeyValuePair<PositionEventData, int> x in events)
            {
                string script = ScriptOf(pr, x.Key);
                Outlook o = Look(script);
                roll.Locked.AddRange(o.LockedLines($"抽到「{Label(x.Key)}」"));
                string cast0 = _index.Cast(script, 2);
                if (o.Open.Count > 0 || o.Locked.Count > 0)
                    roll.Outcomes.Add(new Outcome
                    {
                        Table = pr,
                        Type = x.Key.ResultType,
                        Label = $"抽到「{Label(x.Key)}」{100 * x.Value / sum}%" + (cast0.Length > 0 ? $"({cast0})" : ""),
                        Count = o.Count,
                        Locked = o.LockedLines().ToList(),
                    });
                if (o.Open.Count == 0)
                    continue;
                hit += x.Value;
                roll.Count += o.Count;
                string cast = _index.Cast(script, 2);
                roll.Unseen.Add($"{Label(x.Key)} {100 * x.Value / sum}%" + (cast.Length > 0 ? $"({cast})" : ""));
            }
            roll.Chance = sum == 0 ? 0 : 100 * hit / sum;
            roll.Locked = roll.Locked.Distinct().ToList();
            return roll;
        }

        private List<KeyValuePair<PositionEventData, int>> Events(PositionResultData pr)
        {
            HashSet<string> types = new HashSet<string>();
            List<KeyValuePair<PositionEventData, int>> events = new List<KeyValuePair<PositionEventData, int>>();
            foreach (PositionEventData e in pr.List)
            {
                if (e == null)
                    continue;
                int rate = Rate(e);
                if (rate > 0 && types.Add(e.ResultType))
                    events.Add(new KeyValuePair<PositionEventData, int>(e, rate));
            }
            return events;
        }

        // 同 PositionEventData.GetRate(),但不呼叫它:DiceMaster 在 GetRate 上掛 patch,
        // 碰到玩家指定的事件會回 99999 並清掉指定，插件一呼叫就把玩家的選擇吃掉
        private int Rate(PositionEventData e)
        {
            Traverse t = Traverse.Create(e);
            if (t.Field("_toggleCondition").GetValue<bool>() && !_cond.Eval(t.Field("_activeCondition").GetValue<ConditionResultItem>()))
                return 0;
            int rate = t.Field("_defaultRate").GetValue<int>();
            foreach (PositionEventRateItem r in t.Field("_eventRateItem").GetValue<PositionEventRateItem[]>())
            {
                if (_cond.Eval(r.Condition))
                    rate += r.Rate;
            }
            return Mathf.Max(rate, 0);
        }

        // ---- 選單 ----

        // 選單上某個選項：選了會看到多少白字。null = 這個選單不認得
        public OptionHint ForOption(string script, string option)
        {
            _cond.Forget();
            EnsureLabels();
            Script s = _index.Get(script);
            if (s == null || !s.Options.TryGetValue(option.TrimStart('~'), out KeyValuePair<string, int> pick))
                return null;
            OptionHint hint = new OptionHint();
            List<string> parts = new List<string>();
            // 只看這個選項的分支：這個劇本裡直接出現的台詞算一段，每個跳轉出去的劇本各算一段
            List<KeyValuePair<Item, Guard[]>> inline = new List<KeyValuePair<Item, Guard[]>>();
            List<Outlook> looks = new List<Outlook>();
            foreach (Item i in s.Items.Where(i => i.Guards.Any(g => g.Kind == GuardKind.Choice && g.Name == pick.Key && g.Value == pick.Value)))
            {
                if (i.Kind == ItemKind.Key || i.Kind == ItemKind.Flag)
                    inline.Add(new KeyValuePair<Item, Guard[]>(i, i.Guards));
                else if (i.Kind == ItemKind.Script)
                    looks.Add(Look(_index.Paths(i.Value).Select(x =>
                        new KeyValuePair<Item, Guard[]>(x.Key, i.Guards.Concat(x.Value).ToArray()))));
                else if (_results.TryGetValue(i.Value, out PositionResultData pr) && !i.Guards.Any(g => Blocks(g, NoFlags)))
                {
                    Roll r = RollOf(pr);
                    if (r.Count.Any)
                        parts.Add($"{r.Chance}% 抽到白字 {r.Count}");
                    hint.Locked.AddRange(r.Locked);
                    hint.Detail += $"[{i.Value}: {string.Join("、", r.Unseen)}]";
                }
            }
            looks.Insert(0, Look(inline));
            Tally t = looks.Aggregate(new Tally(), (a, o) => a + o.Count);
            if (t.Any)
                parts.Add($"白字 {t}");
            hint.Locked.AddRange(looks.SelectMany(o => o.LockedLines()));
            hint.Detail += string.Join(",", looks.SelectMany(o => o.Open).Distinct());
            hint.Text = parts.Count == 0 ? "已看完" : string.Join(",", parts);
            return hint;
        }

        // ---- F2 清單 / 地圖提示：這一旬去哪裡能看到白字 ----

        public List<Hint> ForTurn()
        {
            _cond.Forget();
            EnsureLabels();
            List<Hint> hints = new List<Hint>();
            AddMissions(hints);
            AddPlaces(hints);
            if (HasResults)
                AddFlags(hints);
            hints.Sort((a, b) => b.Rank != a.Rank ? b.Rank.CompareTo(a.Rank) : b.Count.Lines.CompareTo(a.Count.Lines));
            return hints;
        }

        private static List<MissionCheckData> ActiveMissions()
        {
            MissionManagerData mm = MissionManagerData.Instance;
            List<MissionCheckData> active = new List<MissionCheckData>();
            if (mm.ActiveSubMissions != null)
                active.AddRange(mm.ActiveSubMissions.Values);
            if (mm.MainMissionStart && mm.MainCheckData != null)
                active.Add(mm.MainCheckData);
            return active;
        }

        private void AddMissions(List<Hint> hints)
        {
            foreach (MissionCheckData m in ActiveMissions())
            {
                string start = m.StartScript.ToLowerInvariant();
                Outlook o = Look(start);
                if (o.Open.Count == 0 && o.Locked.Count == 0)
                    continue;
                // 不用指定地點的任務，這一旬本來就會自己演，提示只是讓你知道
                bool auto = m.Position == PositionType.無;
                string cast = _index.Cast(start);
                hints.Add(new Hint
                {
                    Where = auto ? "(本旬自動發生)" : "去" + PlaceName(m.Position),
                    Chance = o.Open.Count == 0 || auto ? "" : "必定",
                    Detail = "劇情" + (cast.Length > 0 ? ",登場：" + cast : ""),
                    Rank = o.Open.Count == 0 ? -2 : auto ? -1 : 1000,
                    Count = o.Count,
                    Locked = o.LockedLines().ToList(),
                });
            }
        }

        private void AddPlaces(List<Hint> hints)
        {
            foreach (Place x in Places())
            {
                Roll roll = RollOf(x.Table);
                if (!roll.Count.Any && roll.Locked.Count == 0)
                    continue;
                hints.Add(new Hint
                {
                    Where = x.Where,
                    Chance = roll.Count.Any ? roll.Chance + "%" : "",
                    Detail = roll.Count.Any ? "沒看過的結果：" + string.Join("、", roll.Unseen) : "目前條件不足",
                    Rank = roll.Count.Any ? roll.Chance : -2,
                    Count = roll.Count,
                    Locked = roll.Locked,
                    Outcomes = roll.Outcomes,
                });
            }
        }

        // 本旬地圖上點得到的地點事件表：「去伙房 →「掌廚」」→ 事件表。地點資料跟著地圖場景，只在地圖上查得到。
        private IEnumerable<Place> Places()
        {
            foreach (FreePositionData place in Resources.FindObjectsOfTypeAll<FreePositionData>())
            {
                string start = DefaultScript(place);
                if (!_index.Has(start) || Disabled(place))
                    continue;
                string where = "去" + place.name.Split('_').Last();
                HashSet<string> seen = new HashSet<string>();
                foreach (Item i in _index.Walk(start, gs => !gs.Any(g => Blocks(g, NoFlags))))
                {
                    if (i.Kind != ItemKind.Position || !seen.Add(i.Value) || !_results.TryGetValue(i.Value, out PositionResultData pr))
                        continue;
                    string action = _index.ActionOf(i.Value);
                    yield return new Place
                    {
                        Where = where + (action == null ? "" : $" →「{Text.OptionLabel(action)}」"),
                        Data = place,
                        Action = action,
                        Table = pr,
                    };
                }
            }
        }

        // 事件表在地圖上的入口；本旬去不了 = null
        public Place PlaceOf(PositionResultData table) => Places().FirstOrDefault(p => p.Table == table);

        // ---- 本旬拿得到、之後會解鎖白字的旗標 ----

        // 被旗標條件擋住的白字(所有入口；靜態，第一次用時建)
        private class Gated
        {
            public string Key;
            public Guard[] Guards;
            public string Root;
            public HashSet<string> InStory;   // 這個入口自己會寫入的旗標
            public HashSet<string> Flags;     // 沿途條件牽涉到的旗標
        }

        private List<Gated> _gated;
        private Dictionary<string, FlagData> _flags;

        // 本旬可以去的入口：(說明, 劇本)。地點事件表每個機率 > 0 的結果、本旬的任務
        // 本旬可以去的入口：(說明與可指定的結果, 劇本)。地點事件表每個機率 > 0 的結果、本旬的任務
        private IEnumerable<KeyValuePair<Outcome, string>> TurnEntries()
        {
            foreach (Place t in Places())
            {
                List<KeyValuePair<PositionEventData, int>> events = Events(t.Table);
                int sum = events.Sum(x => x.Value);
                foreach (KeyValuePair<PositionEventData, int> e in events)
                    yield return new KeyValuePair<Outcome, string>(new Outcome
                    {
                        Table = t.Table,
                        Type = e.Key.ResultType,
                        Label = $"{t.Where}抽到「{Label(e.Key)}」({100 * e.Value / sum}%)",
                    }, ScriptOf(t.Table, e.Key));
            }
            foreach (MissionCheckData m in ActiveMissions())
                yield return new KeyValuePair<Outcome, string>(new Outcome
                {
                    Label = m.Position == PositionType.無 ? "本旬的劇情" : $"去{PlaceName(m.Position)}的劇情",
                }, m.StartScript.ToLowerInvariant());
        }

        // 每條任務線目前在等的那一步(條件還沒成立的)。已過期的(指定的旬、區間已過)不算
        private List<KeyValuePair<string, MissionCheckData>> PendingMissions()
        {
            MissionManagerData mm = MissionManagerData.Instance;
            List<KeyValuePair<string, MissionCheckData>> pending = new List<KeyValuePair<string, MissionCheckData>>();
            List<MissionData> lines = new List<MissionData> { Traverse.Create(mm).Field("_main").GetValue<MissionData>() };
            lines.AddRange(Traverse.Create(mm).Field("_subMissions").GetValue<SubMissionsData>().List);
            HashSet<MissionCheckData> active = new HashSet<MissionCheckData>(ActiveMissions());
            foreach (MissionData line in lines.Where(l => l != null && l.CurrentKey != "clear"))
            {
                MissionCheckData m = Traverse.Create(line).Field("_missions").GetValue<MissionCheckData[]>()
                    .FirstOrDefault(x => x != null && x.Key == line.CurrentKey);
                if (m == null || active.Contains(m) || Expired(m))
                    continue;
                // 任務線多半沒有描述(名字是 S0030 這種代碼),改用登場人物
                string desc = Traverse.Create(line).Field("_desc").GetValue<string>();
                string cast = _index.Cast(m.StartScript.ToLowerInvariant(), 2);
                pending.Add(new KeyValuePair<string, MissionCheckData>(
                    !string.IsNullOrEmpty(desc) ? $"任務「{desc}」" : cast.Length > 0 ? $"{cast}的任務" : "一段任務", m));
            }
            return pending;
        }

        private static bool Expired(MissionCheckData m)
        {
            Traverse t = Traverse.Create(m);
            if (!t.Field("_active").GetValue<bool>())
                return true;
            if (!t.Field("_dayCheck").GetValue<bool>())
                return false;
            GameTime now = PlayerStatManagerData.Instance.GameTime;
            TimeCheckType type = t.Field("_timeCheckType").GetValue<TimeCheckType>();
            if (type == TimeCheckType.特定)
                return t.Field("_specificTime").GetValue<GameTime>() < now;
            if (type == TimeCheckType.區間)
                return t.Field("_maxTime").GetValue<GameTime>() < now;
            return false;
        }

        private bool MissionReady(MissionCheckData m) =>
            Traverse.Create(m).Field("_conditions").GetValue<ConditionResultItem[]>().All(_cond.Eval);

        private void BuildGated()
        {
            _flags = new Dictionary<string, FlagData>();
            foreach (FlagData f in Resources.FindObjectsOfTypeAll<FlagData>())
                _flags[f.name] = f;
            HashSet<string> roots = new HashSet<string>(Resources.FindObjectsOfTypeAll<MissionCheckData>()
                .Select(m => m.StartScript.ToLowerInvariant()));
            foreach (PositionResultData pr in _results.Values)
                roots.UnionWith(pr.List.Where(e => e != null).Select(e => ScriptOf(pr, e)));
            _gated = new List<Gated>();
            // 同一個分支條件出現在很多條路徑上；每次重查會一直碰 Unity 物件(== null、name),很慢
            Dictionary<Guard, string[]> names = new Dictionary<Guard, string[]>();
            string[] Names(Guard g)
            {
                if (!names.TryGetValue(g, out string[] n))
                    names[g] = n = _cond.Flags(g).Select(f => f.name).ToArray();
                return n;
            }
            foreach (string root in roots.Where(_index.Has))
            {
                List<KeyValuePair<Item, Guard[]>> all = _index.Paths(root).ToList();
                HashSet<string> inStory = new HashSet<string>(all.Where(x => x.Key.Kind == ItemKind.Flag).Select(x => x.Key.Value));
                foreach (KeyValuePair<Item, Guard[]> x in all.Where(x => x.Key.Kind == ItemKind.Key))
                {
                    HashSet<string> flags = new HashSet<string>(x.Value.SelectMany(Names));
                    if (flags.Count > 0)
                        _gated.Add(new Gated { Key = x.Key.Value, Guards = x.Value, Root = root, InStory = inStory, Flags = flags });
                }
            }
        }

        // 暫存旗標(同一段劇情裡設、讀，用完就清)不算
        private static bool Temporary(FlagData f) => (f.DevNote ?? "").Contains("暫存");

        private void AddFlags(List<Hint> hints)
        {
            if (_gated == null)
                BuildGated();
            // 本旬走得到的分支裡會寫入、而且會改變值的旗標 → 在哪裡拿
            Dictionary<FlagData, Dictionary<int, List<Outcome>>> writes = new Dictionary<FlagData, Dictionary<int, List<Outcome>>>();
            foreach (KeyValuePair<Outcome, string> e in TurnEntries())
            {
                List<KeyValuePair<Item, Guard[]>> all = _index.Paths(e.Value).ToList();
                HashSet<string> inStory = new HashSet<string>(all.Where(x => x.Key.Kind == ItemKind.Flag).Select(x => x.Key.Value));
                foreach (KeyValuePair<Item, Guard[]> x in all.Where(x => x.Key.Kind == ItemKind.Flag))
                {
                    if (!_flags.TryGetValue(x.Key.Value, out FlagData f) || Temporary(f) || x.Value.Any(g => Blocks(g, inStory)))
                        continue;
                    int v = x.Key.Add ? f.State + x.Key.Number : x.Key.Number;
                    if (v == f.State)
                        continue;
                    if (!writes.TryGetValue(f, out Dictionary<int, List<Outcome>> byValue))
                        writes[f] = byValue = new Dictionary<int, List<Outcome>>();
                    if (!byValue.TryGetValue(v, out List<Outcome> where))
                        byValue[v] = where = new List<Outcome>();
                    if (!where.Any(o => o.Label == e.Key.Label))
                        where.Add(e.Key);
                }
            }

            // 現在被擋住、跟這些旗標有關的白字
            List<Gated> blocked = _gated.Where(g => g.Flags.Overlaps(writes.Keys.Select(f => f.name))
                && !ReadStorySystem.Instance.ExistKey(g.Key) && g.Guards.Any(x => Blocks(x, g.InStory))).ToList();
            List<KeyValuePair<string, MissionCheckData>> pending = PendingMissions().Where(m => !MissionReady(m.Value)).ToList();

            foreach (KeyValuePair<FlagData, Dictionary<int, List<Outcome>>> w in writes)
            {
                foreach (KeyValuePair<int, List<Outcome>> byValue in w.Value)
                {
                    // 假設拿到這個旗標，重新判斷；只算光靠它就解鎖的
                    _cond.Assume(new Dictionary<FlagData, int> { { w.Key, byValue.Key } });
                    Dictionary<string, HashSet<string>> unlocked = new Dictionary<string, HashSet<string>>();
                    foreach (Gated g in blocked.Where(g => g.Flags.Contains(w.Key.name) && !g.Guards.Any(x => Blocks(x, g.InStory))))
                    {
                        if (!unlocked.TryGetValue(g.Root, out HashSet<string> keys))
                            unlocked[g.Root] = keys = new HashSet<string>();
                        keys.Add(g.Key);
                    }
                    List<string> what = new List<string>();
                    foreach (KeyValuePair<string, MissionCheckData> m in pending.Where(m => MissionReady(m.Value)))
                    {
                        string start = m.Value.StartScript.ToLowerInvariant();
                        HashSet<string> keys = new HashSet<string>(_index.Keys(start, _ => true).Where(k => !ReadStorySystem.Instance.ExistKey(k)));
                        if (keys.Count == 0)
                            continue;
                        unlocked[start] = keys;
                        what.Add(m.Key);
                    }
                    what.AddRange(unlocked.Keys.Where(r => !pending.Any(m => m.Value.StartScript.ToLowerInvariant() == r))
                        .Select(r => _labels != null && _labels.TryGetValue(r, out string l) ? l
                            : _index.Cast(r, 2).Length > 0 ? _index.Cast(r, 2) + "的劇情" : "其他劇情"));
                    _cond.Assume(null);
                    if (unlocked.Count == 0)
                        continue;
                    Tally count = new Tally(unlocked.Count, unlocked.Values.Sum(k => k.Count));
                    hints.Add(new Hint
                    {
                        Where = $"拿{Cond.FlagName(w.Key)}",
                        Chance = "",
                        Detail = $"在：{string.Join("、", byValue.Value.Take(2).Select(o => o.Label))};之後解鎖：{string.Join("、", what.Distinct().Take(3))}",
                        Outcomes = byValue.Value.Where(o => o.Table != null).ToList(),
                        Rank = -3,
                        Count = count,
                        Flag = true,
                    });
                }
            }
        }

        // 要有地點事件表(劇情場景)和地點資料(地圖場景，跑劇情時也還在)才建得出來
        private void EnsureLabels()
        {
            if (_labels == null && HasResults && Resources.FindObjectsOfTypeAll<FreePositionData>().Length > 0)
                _labels = Labels();
        }

        // 劇本 → 入口說明。地點(含所有事件結果)和任務的劇本，連同跳轉下去的劇本都標上入口。
        // 不看條件：只是用來說「這個旗標在哪裡拿」。
        private Dictionary<string, string> Labels()
        {
            Dictionary<string, string> labels = new Dictionary<string, string>();
            void Mark(string script, string label)
            {
                if (!labels.ContainsKey(script))
                    labels[script] = label;
                foreach (KeyValuePair<Item, Guard[]> x in _index.Paths(script))
                {
                    if (x.Key.Kind == ItemKind.Script && !labels.ContainsKey(x.Key.Value))
                        labels[x.Key.Value] = label;
                }
            }
            foreach (FreePositionData place in Resources.FindObjectsOfTypeAll<FreePositionData>())
            {
                string start = DefaultScript(place);
                string name = place.name.Split('_').Last();
                foreach (Item i in _index.Walk(start, _ => true).Where(i => i.Kind == ItemKind.Position))
                {
                    if (!_results.TryGetValue(i.Value, out PositionResultData pr))
                        continue;
                    string action = _index.ActionOf(i.Value);
                    string where = name + (action == null ? "" : $"「{Text.OptionLabel(action)}」");
                    foreach (PositionEventData e in pr.List.Where(e => e != null))
                        Mark(ScriptOf(pr, e), $"{where}抽到「{Label(e)}」");
                }
                Mark(start, name);
            }
            foreach (MissionCheckData m in Resources.FindObjectsOfTypeAll<MissionCheckData>())
            {
                string desc = Traverse.Create(m).Field("_desc").GetValue<string>();
                string start = m.StartScript.ToLowerInvariant();
                string cast = _index.Cast(start, 2);
                Mark(start, !string.IsNullOrEmpty(desc) ? $"劇情「{desc}」" : cast.Length > 0 ? $"{cast}的劇情" : "劇情");
            }
            return labels;
        }

        private static string PlaceName(PositionType p)
        {
            int i = (int)p;
            return i > 0 && i < PlaceKeys.Length ? Text.Get("Position/Name/" + PlaceKeys[i], p.ToString()) : p.ToString();
        }

        // 同 FreePositionData.GetActiveConditionResult() 的反面，但不呼叫會讓計數 +1 的 ConditionResultData.Execute()
        private bool Disabled(FreePositionData p)
        {
            object cond = Traverse.Create(p).Field("_disableCondition").GetValue();
            if (cond == null)
                return false;
            List<ConditionResultItem> items = Traverse.Create(cond).Field("_items").GetValue<List<ConditionResultItem>>();
            return items != null && items.Any(_cond.Eval);
        }

        public static string DefaultScript(FreePositionData p) =>
            (Traverse.Create(p).Field("_defaultScript").GetValue<string>() ?? "").ToLowerInvariant();

        // 劇本名是執行時拼的：_scriptPrefix + "_" + 結果代碼
        private static string ScriptOf(PositionResultData p, PositionEventData e) =>
            (Traverse.Create(p).Field("_scriptPrefix").GetValue<string>() + "_" + e.ResultType).ToLowerInvariant();

        // 結果的中文名:StoryMappingItem.Key 是「大失敗_001」這種，去掉尾碼
        private static string Label(PositionEventData e)
        {
            string key = Traverse.Create(e).Field("_config").Field("Key").GetValue<string>() ?? e.name;
            int i = key.LastIndexOf('_');
            return i > 0 ? key.Substring(0, i) : key;
        }
    }
}
