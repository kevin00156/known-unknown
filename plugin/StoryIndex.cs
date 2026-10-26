using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Mortal.Core;
using UnityEngine;

namespace KnownUnknown
{
    // 選單上一個選項對應的 Lua 分支：if choice_x == N then … 裡面出現的台詞與地點事件表
    internal class OptionBranch
    {
        public string[] Keys;        // 分支內直接 say 的台詞，加上分支點名劇本的後續台詞
        public string[] Positions;   // 分支內 checkpointmanager.Position("X") 的 X(小寫)
    }

    // 劇本 → 台詞 key、劇本之間的字串引用、選單分支。遊戲啟動後建一次，內容不會變。
    // 劇本共 65MB,Unity 的 Mono 正規表示式很慢：讀檔(Load)在主執行緒，解析(建構子)丟背景執行緒。
    internal class StoryIndex
    {
        // LuaManager 從 Resources 的 Story/ChineseTraditional/<劇本> 載入；Resources 路徑不分大小寫，一律小寫
        private const string ScriptFolder = "Story/ChineseTraditional";
        private static readonly Regex KeyRe = new Regex("GetStoryText\\(\"([^\"]+)\"\\)");
        private static readonly Regex StrRe = new Regex("\"([A-Za-z0-9_]+)\"");
        private static readonly Regex CastRe = new Regex("characters\\.Get\\(\"([^\"]+)\"\\)");
        private static readonly Regex PositionRe = new Regex("checkpointmanager\\.Position\\(\"([^\"]+)\"\\)");
        // 主角自己、？？？、信鴿不算登場人物
        private static readonly Regex NotCast = new Regex("player|unknown|pigeon");

        private readonly Dictionary<string, string[]> _keys = new Dictionary<string, string[]>();
        private readonly Dictionary<string, string[]> _refs = new Dictionary<string, string[]>();
        // 劇本 -> checkpointmanager.Position("X") 的 X。事件表名常和其中一個結果劇本同名
        // (Mall_Free_001 抽到「正常_001」= mall_free_001),所以不能當成劇本引用，否則隨機結果會被當成必定發生。
        private readonly Dictionary<string, string[]> _positions = new Dictionary<string, string[]>();
        private readonly Dictionary<string, string[]> _cast = new Dictionary<string, string[]>();
        // 劇本 -> 它和它沿字串引用接下去的劇本的所有台詞(遇到有自己入口的劇本就停)
        private readonly Dictionary<string, string[]> _closureKeys = new Dictionary<string, string[]>();
        private readonly Dictionary<string, string[]> _closurePositions = new Dictionary<string, string[]>();
        // 劇本 -> 選項字串 -> 分支
        private readonly Dictionary<string, Dictionary<string, OptionBranch>> _options =
            new Dictionary<string, Dictionary<string, OptionBranch>>();
        // 地點事件表名 -> 地點選單上那個選項的字串
        private readonly Dictionary<string, string> _actions = new Dictionary<string, string>();

        public int TotalKeys { get; private set; }

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
            foreach (KeyValuePair<string, string> s in scripts)
            {
                string text = s.Value;
                _keys[s.Key] = KeyRe.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
                _positions[s.Key] = Positions(text);
                _refs[s.Key] = Refs(text, _positions[s.Key]);
                _cast[s.Key] = CastRe.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value)
                    .Where(c => !NotCast.IsMatch(c))
                    .GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).ToArray();
                TotalKeys += _keys[s.Key].Length;
            }
            foreach (string name in _keys.Keys.ToArray())
            {
                HashSet<string> closure = Closure(name, stop);
                _closureKeys[name] = closure.SelectMany(n => _keys[n]).Distinct().ToArray();
                _closurePositions[name] = closure.SelectMany(n => _positions[n]).Distinct().ToArray();
            }
            foreach (KeyValuePair<string, string> s in scripts)
            {
                if (s.Value.Contains("choose("))
                    _options[s.Key] = ParseOptions(s.Value);
            }
        }

        public bool Has(string script) => _keys.ContainsKey(script);

        public IEnumerable<string> AllKeys => _keys.Values.SelectMany(k => k);

        public string[] ClosureKeys(string script) =>
            _closureKeys.TryGetValue(script, out string[] k) ? k : new string[0];

        // 劇本和它後續劇本會抽的地點事件表
        public string[] ClosurePositions(string script) =>
            _closurePositions.TryGetValue(script, out string[] r) ? r : new string[0];

        private static string[] Positions(string text) =>
            PositionRe.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToArray();

        private static string[] Refs(string text, string[] positions) =>
            StrRe.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value.ToLowerInvariant())
                .Except(positions).ToArray();

        public OptionBranch Option(string script, string option)
        {
            if (script == null || !_options.TryGetValue(script, out Dictionary<string, OptionBranch> d))
                return null;
            return d.TryGetValue(option.TrimStart('~'), out OptionBranch b) ? b : null;
        }

        // 地點事件表是從哪個選項進去的(F8 清單要寫「去正心堂 →「靜坐冥想」」)
        public string ActionOf(string positionResult) =>
            _actions.TryGetValue(positionResult, out string a) ? a : null;

        // 玩家認得出來的描述：登場人物的遊戲內名字(Character/<id>)
        public string Cast(string script, int max = 4)
        {
            if (!_cast.TryGetValue(script, out string[] ids) || ids.Length == 0)
                return "";
            return string.Join("、", ids.Select(id => Text.Get("Character/" + id, id)).Distinct().Take(max));
        }

        // 從 start 沿字串引用走到的劇本(含自己)。stop 裡的劇本有自己的入口，不算進來(start 本身除外)。
        private HashSet<string> Closure(string start, HashSet<string> stop)
        {
            HashSet<string> seen = new HashSet<string> { start };
            Queue<string> queue = new Queue<string>(seen);
            while (queue.Count > 0)
            {
                foreach (string s in _refs[queue.Dequeue()])
                {
                    if (Has(s) && !stop.Contains(s) && seen.Add(s))
                        queue.Enqueue(s);
                }
            }
            return seen;
        }

        // Lua 選單長這樣(Fungus 的 choose 依序 AddOption,第 i 個選項回傳 i):
        //   mall[2] = "meditate+free+meditate"          或 x[3] = Cond and "O_xxx" or "~x"
        //   local choice_mall = choose(mall)
        //   elseif choice_mall == 2 then …(縮排較深的這一段就是分支)
        // 用逐行字串處理，不用正規表示式(Mono 上太慢)
        private Dictionary<string, OptionBranch> ParseOptions(string text)
        {
            string[] lines = text.Split('\n');
            Dictionary<string, string> chooses = new Dictionary<string, string>();   // choice 變數 -> 陣列名
            Dictionary<string, string> entries = new Dictionary<string, string>();   // "陣列[i]" -> 選項字串
            Dictionary<string, int[]> branches = new Dictionary<string, int[]>();    // "變數==i" -> [起, 迄)
            for (int n = 0; n < lines.Length; n++)
            {
                string l = lines[n].Trim();
                if (l.StartsWith("local ") && l.Contains(" = choose("))
                {
                    int p = l.IndexOf("choose(") + 7;
                    chooses[l.Substring(6, l.IndexOf(" =") - 6)] = l.Substring(p, l.IndexOf(')', p) - p);
                    continue;
                }
                int b = l.IndexOf("] = ");
                int open = l.IndexOf('[');
                if (b > 0 && open > 0 && open < b && l.IndexOf('"', b) > 0)
                {
                    int q = l.IndexOf('"', b);
                    int e = l.IndexOf('"', q + 1);
                    if (e > q)
                        entries[l.Substring(0, b + 1)] = l.Substring(q + 1, e - q - 1);
                    continue;
                }
                if ((l.StartsWith("if ") || l.StartsWith("elseif ")) && l.EndsWith(" then") && l.Contains(" == "))
                {
                    int sp = l.IndexOf(' ') + 1;
                    string cond = l.Substring(sp, l.Length - sp - 5);
                    int eq = cond.IndexOf(" == ");
                    string val = cond.Substring(eq + 4);
                    if (!int.TryParse(val, out _))
                        continue;
                    int ind = Indent(lines[n]);
                    int end = lines.Length;
                    for (int m = n + 1; m < lines.Length; m++)
                    {
                        if (lines[m].Trim().Length > 0 && Indent(lines[m]) <= ind)
                        {
                            end = m;
                            break;
                        }
                    }
                    branches[cond.Substring(0, eq) + "==" + val] = new[] { n + 1, end };
                }
            }

            Dictionary<string, OptionBranch> result = new Dictionary<string, OptionBranch>();
            foreach (KeyValuePair<string, string> c in chooses)
            {
                for (int i = 1; entries.TryGetValue(c.Value + "[" + i + "]", out string option); i++)
                {
                    if (!branches.TryGetValue(c.Key + "==" + i, out int[] range))
                        continue;
                    string seg = string.Join("\n", lines, range[0], range[1] - range[0]);
                    string[] positions = Positions(seg);
                    IEnumerable<string> keys = KeyRe.Matches(seg).Cast<Match>().Select(m => m.Groups[1].Value);
                    foreach (string s in Refs(seg, positions))
                    {
                        if (Has(s))
                            keys = keys.Concat(ClosureKeys(s));
                    }
                    result[option.TrimStart('~')] = new OptionBranch
                    {
                        Keys = keys.Distinct().ToArray(),
                        Positions = positions,
                    };
                    foreach (string p in positions)
                        _actions[p] = option;
                }
            }
            return result;
        }

        private static int Indent(string line) => line.Length - line.TrimStart(' ', '\t').Length;
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
            string[] p = option.TrimStart('~').Split('+');
            if (p.Length > 2 && (p[1] == "work" || p[1] == "free"))
                return Get("Position/title/" + p[2], p[2]);
            if (p.Length > 2 && p[1] == "talk")
                return "和" + Get("Character/" + p[2], p[2]) + "交談";
            string key = p.Length > 1 ? p[1] : p[0];
            return key.StartsWith("O_") ? Get("Story/" + key, key) : key;
        }
    }

    internal static class Game
    {
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
