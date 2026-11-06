using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Mortal.Core;
using UnityEngine;

namespace KnownUnknown
{
    internal enum GuardKind
    {
        Switch,  // local r = checkpointmanager.Switch("S") … if r == N:遊戲狀態決定，可即時判斷
        Choice,  // local c = choose(arr) … if c == N:玩家選第 N 個選項；擲骰(ExecuteRoll(arr…) 後 local c = dicemenudialog.ResultSelection)也是
        Random,  // 骰子、math.random:一律當成可能
    }

    // 所在分支的一個條件：變數 == Value
    // 會拿來當字典鍵、Distinct:struct 預設的 Equals/GetHashCode 走反射，很慢
    internal struct Guard : IEquatable<Guard>
    {
        public GuardKind Kind;
        public string Name;   // Switch 名或 choice 變數名
        public int Value;
        public string Gate;   // Choice:這個選項要 checkpointmanager.Condition(Gate) 成立才會出現在選單上

        public bool Equals(Guard o) => Kind == o.Kind && Value == o.Value && Name == o.Name && Gate == o.Gate;

        public override bool Equals(object o) => o is Guard g && Equals(g);

        public override int GetHashCode() => ((int)Kind * 397 ^ Value) * 397 ^ (Name?.GetHashCode() ?? 0) ^ (Gate?.GetHashCode() ?? 0);
    }

    internal enum ItemKind
    {
        Key,       // say(luamanager.GetStoryText("K"))
        Script,    // SetNextScript / SetCurrentStoryScript / SetCurrentTravelScript / SetTempScript
        Position,  // checkpointmanager.Position("X"):地點事件表，執行時才抽出結果劇本
        Flag,      // statmodifymanager.SetFlag("F", n) / AddFlag("F", n)
        Library,   // 講經堂：endgamepanel.Open("20001") = 汗青書「end:20001」;ChangeScene("GameOver", "10023") = 生死簿「dead:10023」
    }

    internal struct Item
    {
        public ItemKind Kind;
        public string Value;
        public Guard[] Guards;
        public int Number;    // Flag:設成/加上多少
        public bool Add;      // Flag:AddFlag,或設成非常數(SetFlag("F", var1)):值不固定
    }

    // 一份劇本解析後的樣子
    internal class Script
    {
        public Item[] Items;
        // 選項字串 -> (choice 變數, 第幾個)
        public Dictionary<string, KeyValuePair<string, int>> Options = new Dictionary<string, KeyValuePair<string, int>>();
        public string[] Cast;
        // 角色代號(girl5、special1)-> 這份劇本最多加多少好感(每個加好感的呼叫都算一次，不管在哪個分支)
        public Dictionary<string, int> Gains = new Dictionary<string, int>();
    }

    // 劇本解析與靜態關係。遊戲啟動後建一次，內容不會變。
    // 劇本共 65MB,Unity 的 Mono 正規表示式很慢：讀檔(Load)在主執行緒，解析(建構子)丟背景執行緒，
    // 逐行字串處理不用正規表示式。
    internal class StoryIndex
    {
        // LuaManager 從 Resources 的 Story/ChineseTraditional/<劇本> 載入；Resources 路徑不分大小寫，一律小寫
        private const string ScriptFolder = "Story/ChineseTraditional";
        private static readonly Regex CastRe = new Regex("characters\\.Get\\(\"([^\"]+)\"\\)");
        // 主角自己、？？？、信鴿不算登場人物
        private static readonly Regex NotCast = new Regex("player|unknown|pigeon");
        // 只有這些呼叫的參數才是劇本名。ShowPicture/SetFlag/Switch 的參數常剛好和劇本同名，不能算。
        private static readonly string[] Transitions =
        {
            "luamanager.SetNextScript(", "luamanager.SetCurrentStoryScript(", "luamanager.SetCurrentTravelScript(",
            "luamanager.SetTempScript(",
        };

        private readonly Dictionary<string, Script> _scripts = new Dictionary<string, Script>();
        // 有自己入口的劇本(任務起點):沿劇本跳轉走到這裡就停
        private readonly HashSet<string> _stop;
        // 地點事件表名 -> 從選單哪個選項進去(選項字串)
        private readonly Dictionary<string, string> _actions = new Dictionary<string, string>();

        // 旗標 -> 會寫入它的劇本(Lua SetFlag/AddFlag) -> 寫進去的值；null = 什麼值都可能(AddFlag、非常數)
        private readonly Dictionary<string, Dictionary<string, HashSet<int>>> _flagWriters = new Dictionary<string, Dictionary<string, HashSet<int>>>();
        // 旗標 -> Lua 寫進去過的值；null = 什麼值都可能(AddFlag、非常數)
        private readonly Dictionary<string, HashSet<int>> _flagValues = new Dictionary<string, HashSet<int>>();
        // 角色代號 -> 全部劇本加起來最多加多少好感、會加好感的劇本
        private readonly Dictionary<string, int> _gains = new Dictionary<string, int>();
        private readonly Dictionary<string, HashSet<string>> _gainWriters = new Dictionary<string, HashSet<string>>();

        public int TotalKeys { get; private set; }

        public IEnumerable<string> FlagWriters(string flag) => FlagWriters(flag, _ => true);

        // 寫進去的值能讓 wants 成立的劇本；值不固定的一律算
        public IEnumerable<string> FlagWriters(string flag, Func<int, bool> wants) =>
            _flagWriters.TryGetValue(flag, out Dictionary<string, HashSet<int>> w)
                ? w.Where(x => x.Value == null || x.Value.Any(wants)).Select(x => x.Key)
                : Enumerable.Empty<string>();

        // Lua 會把旗標寫成哪些值；沒寫過 = 空集合，null = 什麼值都可能
        public HashSet<int> FlagValues(string flag) =>
            _flagValues.TryGetValue(flag, out HashSet<int> v) ? v : new HashSet<int>();

        // 好感最多加多少：每份劇本、每個加好感的呼叫都只算一次。會重複演的劇本(地點事件)由呼叫端另外判斷
        public int Gain(string character) => _gains.TryGetValue(character, out int g) ? g : 0;

        public IEnumerable<string> GainWriters(string character) =>
            _gainWriters.TryGetValue(character, out HashSet<string> w) ? w : Enumerable.Empty<string>();

        // 只能在主執行緒呼叫(Unity API)
        public static KeyValuePair<string, string>[] Load()
        {
            return Resources.LoadAll<TextAsset>(ScriptFolder)
                .Select(a => new KeyValuePair<string, string>(a.name.ToLowerInvariant(), a.text))
                .ToArray();
        }

        // 純字串處理，在背景執行緒跑。stop = 有自己入口(任務、地點)的劇本。
        public StoryIndex(KeyValuePair<string, string>[] scripts, HashSet<string> stop)
        {
            _stop = stop;
            foreach (KeyValuePair<string, string> s in scripts)
            {
                Script p = Parse(s.Value);
                p.Cast = CastRe.Matches(s.Value).Cast<Match>().Select(m => m.Groups[1].Value)
                    .Where(c => !NotCast.IsMatch(c))
                    .GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).ToArray();
                _scripts[s.Key] = p;
                TotalKeys += p.Items.Count(i => i.Kind == ItemKind.Key);
            }
            foreach (KeyValuePair<string, Script> p in _scripts)
            {
                IndexActions(p.Value);
                foreach (Item i in p.Value.Items.Where(i => i.Kind == ItemKind.Flag))
                {
                    if (!_flagWriters.TryGetValue(i.Value, out Dictionary<string, HashSet<int>> w))
                        _flagWriters[i.Value] = w = new Dictionary<string, HashSet<int>>();
                    if (i.Add)
                        w[p.Key] = null;
                    else if (!w.TryGetValue(p.Key, out HashSet<int> mine))
                        w[p.Key] = new HashSet<int> { i.Number };
                    else
                        mine?.Add(i.Number);
                    bool known = _flagValues.TryGetValue(i.Value, out HashSet<int> vs);
                    if (i.Add)
                        _flagValues[i.Value] = null;
                    else if (!known)
                        _flagValues[i.Value] = new HashSet<int> { i.Number };
                    else
                        vs?.Add(i.Number);
                }
                foreach (KeyValuePair<string, int> g in p.Value.Gains)
                {
                    _gains[g.Key] = Gain(g.Key) + g.Value;
                    if (!_gainWriters.TryGetValue(g.Key, out HashSet<string> w))
                        _gainWriters[g.Key] = w = new HashSet<string>();
                    w.Add(p.Key);
                }
            }
        }

        public bool Has(string script) => _scripts.ContainsKey(script);

        public Script Get(string script) =>
            script != null && _scripts.TryGetValue(script, out Script s) ? s : null;

        public IEnumerable<string> AllKeys => _scripts.Values.SelectMany(s => s.Items)
            .Where(i => i.Kind == ItemKind.Key).Select(i => i.Value);

        // 從 start 沿劇本跳轉走得到的台詞、跳轉、地點事件表。只走 ok(分支條件) 成立的分支，
        // 條件由呼叫端依當下遊戲狀態判斷。stop 裡的劇本有自己的入口，不走進去(start 本身除外)。
        public IEnumerable<Item> Walk(string start, Func<Guard[], bool> ok)
        {
            HashSet<string> seen = new HashSet<string> { start };
            Queue<string> queue = new Queue<string>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Script s = Get(queue.Dequeue());
                if (s == null)
                    continue;
                foreach (Item i in s.Items.Where(i => ok(i.Guards)))
                {
                    if (i.Kind == ItemKind.Script && Has(i.Value) && !_stop.Contains(i.Value) && seen.Add(i.Value))
                        queue.Enqueue(i.Value);
                    yield return i;
                }
            }
        }

        // 同 Walk 但不過濾，每一項附上「從 start 走到它」沿途所有的分支條件(跳轉所在分支 + 自己所在分支)
        public IEnumerable<KeyValuePair<Item, Guard[]>> Paths(string start)
        {
            HashSet<string> seen = new HashSet<string> { start };
            Queue<KeyValuePair<string, Guard[]>> queue = new Queue<KeyValuePair<string, Guard[]>>();
            queue.Enqueue(new KeyValuePair<string, Guard[]>(start, new Guard[0]));
            while (queue.Count > 0)
            {
                KeyValuePair<string, Guard[]> at = queue.Dequeue();
                Script s = Get(at.Key);
                if (s == null)
                    continue;
                foreach (Item i in s.Items)
                {
                    Guard[] path = at.Value.Length == 0 ? i.Guards : at.Value.Concat(i.Guards).ToArray();
                    if (i.Kind == ItemKind.Script && Has(i.Value) && !_stop.Contains(i.Value) && seen.Add(i.Value))
                        queue.Enqueue(new KeyValuePair<string, Guard[]>(i.Value, path));
                    yield return new KeyValuePair<Item, Guard[]>(i, path);
                }
            }
        }

        public IEnumerable<string> Keys(string start, Func<Guard[], bool> ok) =>
            Walk(start, ok).Where(i => Seeable(i.Kind)).Select(i => i.Value).Distinct();

        public IEnumerable<string> LibraryKeys =>
            _scripts.Values.SelectMany(s => s.Items).Where(i => i.Kind == ItemKind.Library).Select(i => i.Value).Distinct();

        // 看過 / 沒看過的單位：台詞，和講經堂的結局、死亡
        public static bool Seeable(ItemKind k) => k == ItemKind.Key || k == ItemKind.Library;

        public string ActionOf(string positionResult) =>
            _actions.TryGetValue(positionResult, out string a) ? a : null;

        // 玩家認得出來的描述：登場人物的遊戲內名字(Character/<id>)
        public string Cast(string script, int max = 4)
        {
            Script s = Get(script);
            if (s == null || s.Cast.Length == 0)
                return "";
            return string.Join("、", s.Cast.Select(id => Text.Get("Character/" + id, id)).Distinct().Take(max));
        }

        // 地點事件表若在選單分支裡，記下是哪個選項(F2 清單要寫「去正心堂 →「靜坐冥想」」)
        private void IndexActions(Script s)
        {
            foreach (Item i in s.Items.Where(i => i.Kind == ItemKind.Position))
            {
                foreach (Guard g in i.Guards.Where(g => g.Kind == GuardKind.Choice))
                {
                    KeyValuePair<string, KeyValuePair<string, int>> o = s.Options.FirstOrDefault(x =>
                        x.Value.Key == g.Name && x.Value.Value == g.Value);
                    // 擲骰的選項(「O_xxx|>=60」)不是玩家選的行動
                    if (o.Key != null && !o.Key.Contains('|') && !_actions.ContainsKey(i.Value))
                        _actions[i.Value] = o.Key;
                }
            }
        }

        // Lua 劇本是工具產生的，格式很固定：
        //   local r = checkpointmanager.Switch("S") / local c = choose(arr) / local d = checkpointmanager.Dice(…)
        //   arr[2] = "meditate+free+meditate"   或 arr[3] = Cond and "O_xxx" or "~x"
        //   if r == 1 then … elseif r == 2 then … else … end(分支內容縮排較深)
        // 逐行用縮排追蹤巢狀分支，每句台詞、劇本跳轉、地點事件表都記下所在分支的條件。
        private static Script Parse(string text)
        {
            Script script = new Script();
            List<Item> items = new List<Item>();
            Dictionary<string, Guard> vars = new Dictionary<string, Guard>();
            Dictionary<string, Dictionary<int, string>> arrays = new Dictionary<string, Dictionary<int, string>>();
            Dictionary<string, string> chooses = new Dictionary<string, string>();   // choice 變數 -> 陣列
            Dictionary<string, string> conds = new Dictionary<string, string>();     // 變數 -> Condition 名
            Dictionary<string, string> gates = new Dictionary<string, string>();     // "陣列[第幾個]" -> Condition 名
            string roll = null;                                                       // 最近一次 ExecuteRoll 的選項陣列
            List<KeyValuePair<int, Guard?>> stack = new List<KeyValuePair<int, Guard?>>();
            Guard[] guards = new Guard[0];

            foreach (string raw in text.Split('\n'))
            {
                string l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("--"))
                    continue;
                int ind = raw.Length - raw.TrimStart(' ', '\t').Length;
                bool popped = false;
                while (stack.Count > 0 && stack[stack.Count - 1].Key >= ind)
                {
                    stack.RemoveAt(stack.Count - 1);
                    popped = true;
                }

                Guard? pushed = null;
                bool push = false;
                if ((l.StartsWith("if ") || l.StartsWith("elseif ")) && l.EndsWith(" then"))
                {
                    push = true;
                    string cond = l.Substring(l.IndexOf(' ') + 1, l.Length - l.IndexOf(' ') - 6);
                    int eq = cond.IndexOf(" == ");
                    if (eq > 0 && int.TryParse(cond.Substring(eq + 4), out int n)
                        && vars.TryGetValue(cond.Substring(0, eq), out Guard v))
                    {
                        gates.TryGetValue(v.Kind == GuardKind.Choice && chooses.TryGetValue(v.Name, out string a)
                            ? $"{a}[{n}]" : "", out string gate);
                        pushed = new Guard { Kind = v.Kind, Name = v.Name, Value = n, Gate = gate };
                    }
                }
                else if (l == "else")
                {
                    push = true;   // else 的條件是「前面都不成立」,當成可能
                }
                if (push)
                    stack.Add(new KeyValuePair<int, Guard?>(ind, pushed));
                if (push || popped)
                    guards = stack.Where(x => x.Value.HasValue).Select(x => x.Value.Value).ToArray();
                if (push)
                    continue;

                if (l.StartsWith("local ") && l.Contains(" = "))
                {
                    string name = l.Substring(6, l.IndexOf(" = ") - 6);
                    string rhs = l.Substring(l.IndexOf(" = ") + 3);
                    if (rhs.StartsWith("checkpointmanager.Switch(\""))
                        vars[name] = new Guard { Kind = GuardKind.Switch, Name = Arg(rhs, 0) };
                    else if (rhs.StartsWith("choose("))
                    {
                        vars[name] = new Guard { Kind = GuardKind.Choice, Name = name };
                        chooses[name] = rhs.Substring(7, rhs.IndexOf(')') - 7);
                    }
                    else if (rhs == "dicemenudialog.ResultSelection" && roll != null)
                    {
                        vars[name] = new Guard { Kind = GuardKind.Choice, Name = name };
                        chooses[name] = roll;
                    }
                    else if (rhs.StartsWith("checkpointmanager.Condition(\""))
                        conds[name] = Arg(rhs, 0);
                    else
                        vars[name] = new Guard { Kind = GuardKind.Random, Name = name };
                }

                int b = l.IndexOf("] = ");
                int open = l.IndexOf('[');
                if (b > 0 && open > 0 && open < b && int.TryParse(l.Substring(open + 1, b - open - 1), out int idx))
                {
                    int q = l.IndexOf('"', b);
                    int e = q < 0 ? -1 : l.IndexOf('"', q + 1);
                    if (e > q)
                    {
                        string arr = l.Substring(0, open);
                        if (!arrays.ContainsKey(arr))
                            arrays[arr] = new Dictionary<int, string>();
                        arrays[arr][idx] = l.Substring(q + 1, e - q - 1);
                        // arr[3] = Cond and "O_xxx" or "~x"
                        string head = l.Substring(b + 4, q - b - 4).Trim();
                        if (head.EndsWith(" and") && conds.TryGetValue(head.Substring(0, head.Length - 4), out string cond))
                            gates[$"{arr}[{idx}]"] = cond;
                    }
                }

                // runwait(dicemenudialog.ExecuteRoll(dice_001, 1, "Ch_7_3_2_3_001"))
                const string execRoll = "dicemenudialog.ExecuteRoll(";
                int er = l.IndexOf(execRoll);
                if (er >= 0)
                    roll = l.Substring(er + execRoll.Length).Split(',')[0].Trim();

                Collect(l, "GetStoryText(\"", ItemKind.Key, guards, items);
                CollectLibrary(l, "endgamepanel.Open(\"", Library.End, guards, items);
                CollectLibrary(l, "luamanager.ChangeScene(\"GameOver\", \"", Library.Dead, guards, items);
                CollectCombatDeath(l, guards, items);
                Collect(l, "checkpointmanager.Position(\"", ItemKind.Position, guards, items);
                CollectFlag(l, "statmodifymanager.SetFlag(\"", false, guards, items);
                CollectFlag(l, "statmodifymanager.AddFlag(\"", true, guards, items);
                CollectGain(l, "statmodifymanager.Character(\"", script.Gains);
                CollectGain(l, "statmodifymanager.SetCharacter(\"", script.Gains);
                foreach (string t in Transitions)
                    Collect(l, t, ItemKind.Script, guards, items);
            }

            foreach (KeyValuePair<string, string> c in chooses)
            {
                if (!arrays.TryGetValue(c.Value, out Dictionary<int, string> arr))
                    continue;
                foreach (KeyValuePair<int, string> o in arr)
                    script.Options[o.Value.TrimStart('~')] = new KeyValuePair<string, int>(c.Key, o.Key);
            }
            script.Items = items.ToArray();
            return script;
        }

        // 一行裡 call( 之後第一個字串參數(SetTempScript(1, "x") 的 x 也算)
        private static void Collect(string line, string call, ItemKind kind, Guard[] guards, List<Item> items)
        {
            for (int at = line.IndexOf(call); at >= 0; at = line.IndexOf(call, at + 1))
            {
                // 只看這次呼叫的括號內：SetNextScript(var1) 不能抓到同一行後面別的字串
                string rest = line.Substring(at + call.Length - (call.EndsWith("\"") ? 1 : 0));
                int close = rest.IndexOf(')');
                string arg = Arg(close >= 0 ? rest.Substring(0, close) : rest, 0);
                if (arg == null)
                    continue;
                if (kind != ItemKind.Key)
                    arg = arg.ToLowerInvariant();
                items.Add(new Item { Kind = kind, Value = arg, Guards = guards });
            }
        }

        private static void CollectLibrary(string line, string call, string prefix, Guard[] guards, List<Item> items)
        {
            int at = line.IndexOf(call);
            string id = at < 0 ? null : Arg(line.Substring(at + call.Length - 1), 0);
            if (id != null)
                items.Add(new Item { Kind = ItemKind.Library, Value = prefix + id, Guards = guards });
        }

        // 決鬥打輸會進生死簿的(SystemFlags.CombatDeaths):當成這裡看得到那一頁
        private static readonly Dictionary<string, string> CombatDeaths = SystemFlags.CombatDeaths.ToDictionary(x => x[0], x => x[1]);

        private static void CollectCombatDeath(string line, Guard[] guards, List<Item> items)
        {
            const string call = "luamanager.ChangeScene(\"Combat\", \"";
            int at = line.IndexOf(call);
            string key = at < 0 ? null : Arg(line.Substring(at + call.Length - 1), 0);
            if (key != null && CombatDeaths.TryGetValue(key, out string id))
                items.Add(new Item { Kind = ItemKind.Library, Value = Library.Dead + id, Guards = guards });
        }

        // SetFlag("F", 1) / AddFlag("F", -1);數字不是常數(SetFlag("F", var1))當成 Add:值不固定
        private static void CollectFlag(string line, string call, bool add, Guard[] guards, List<Item> items)
        {
            string name = Arg(line, call, out string num);
            if (name == null)
                return;
            bool constant = int.TryParse(num, out int n);
            items.Add(new Item { Kind = ItemKind.Flag, Value = name, Guards = guards, Number = n, Add = add || !constant });
        }

        // Character("special1", 2, 1) 加好感 2;SetCharacter("special403", 0) 設成 0。只記會變高的
        private static void CollectGain(string line, string call, Dictionary<string, int> gains)
        {
            string name = Arg(line, call, out string num);
            if (name != null && int.TryParse(num, out int n) && n > 0)
                gains[name] = (gains.TryGetValue(name, out int g) ? g : 0) + n;
        }

        // call("名字", 數字…) 的名字和第二個參數(原字串)
        private static string Arg(string line, string call, out string second)
        {
            second = null;
            int at = line.IndexOf(call);
            if (at < 0)
                return null;
            string rest = line.Substring(at + call.Length - 1);
            string name = Arg(rest, 0);
            int comma = rest.IndexOf(',');
            int close = rest.IndexOf(')');
            if (name == null || comma < 0 || close < comma)
                return null;
            int end = rest.IndexOf(',', comma + 1);
            second = rest.Substring(comma + 1, (end > comma && end < close ? end : close) - comma - 1).Trim();
            return name;
        }

        // s 裡第一個 "…" 的內容
        private static string Arg(string s, int from)
        {
            int q = s.IndexOf('"', from);
            int e = q < 0 ? -1 : s.IndexOf('"', q + 1);
            return e > q ? s.Substring(q + 1, e - q - 1) : null;
        }
    }

    internal static class Text
    {
        public static string Get(string key, string fallback) =>
            Lean.Localization.LeanLocalization.GetTranslationText(key, fallback);

        // 選項字串 → 畫面上的字：
        //   "類型+work/free+圖示" → Position/title/<圖示>(工作/自由行動)
        //   "類型+O_xxx+…" 或 "O_xxx" → Story/O_xxx(對話選項)
        //   "talk+talk+角色" → 和角色交談
        public static string OptionLabel(string option)
        {
            // 擲骰選項「O_xxx|>=60」
            int bar = option.IndexOf('|');
            if (bar > 0)
                return $"{OptionLabel(option.Substring(0, bar))}({option.Substring(bar + 1)})";
            string[] p = option.TrimStart('~').Split('+');
            if (p.Length > 2 && (p[1] == "work" || p[1] == "free"))
                return Get("Position/title/" + p[2], p[2]);
            if (p.Length > 2 && p[1] == "talk")
                return "和" + Get("Character/" + p[2], p[2]) + "交談";
            string key = p.Length > 1 ? p[1] : p[0];
            return key.StartsWith("O_") ? Get("Story/" + key, key) : key;
        }
    }

    // 講經堂項目的 key 前綴
    internal static class Library
    {
        public const string End = "end:";    // 汗青書
        public const string Dead = "dead:";  // 生死簿(講經堂內部叫奪命譜)
    }

    internal static class Game
    {
        // 看過了沒：台詞問已讀表；結局、死亡問講經堂(次數 > 0 = 已收錄)
        public static bool Seen(string key)
        {
            if (key.StartsWith(Library.End))
                return Collected(LibrarySystem.Instance.EndGame, key.Substring(Library.End.Length));
            if (key.StartsWith(Library.Dead))
                return Collected(LibrarySystem.Instance.Dead, key.Substring(Library.Dead.Length));
            return ReadStorySystem.Instance.ExistKey(key);
        }

        // 講經堂沒有這一項(插件不認得的 id)就當收過，不提示
        private static bool Collected(LibraryItemCollection c, string id)
        {
            LibraryItemData d = c.Get(id);
            return d == null || d.Count > 0;
        }

        public static bool Ready()
        {
            ReadStorySystem rs = ReadStorySystem.Instance;
            return rs != null && MissionManagerData.Instance != null
                && Traverse.Create(rs).Field("_dicStoryIndex").GetValue() != null;
        }

        public static int Unread(IEnumerable<string> keys)
        {
            ReadStorySystem rs = ReadStorySystem.Instance;
            return keys.Count(k => !rs.ExistKey(k));
        }

        public static int ReadCount()
        {
            Dictionary<string, bool> d = Traverse.Create(ReadStorySystem.Instance).Field("_dicStoryIndex")
                .GetValue<Dictionary<string, bool>>();
            return d == null ? 0 : d.Count;
        }
    }
}
