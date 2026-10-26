using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Lean.Localization;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;

namespace KnownUnknown
{
    // 劇本 → 台詞 key、劇本之間的字串引用。遊戲啟動後建一次就好，內容不會變。
    // 劇本共 65MB,Unity 的 Mono 正規表示式很慢：讀檔(Load)在主執行緒，解析(建構子)丟背景執行緒。
    internal class StoryIndex
    {
        // LuaManager 從 Resources 的 Story/ChineseTraditional/<劇本> 載入；Resources 路徑不分大小寫，一律小寫
        private const string ScriptFolder = "Story/ChineseTraditional";
        private static readonly Regex KeyRe = new Regex("GetStoryText\\(\"([^\"]+)\"\\)");
        private static readonly Regex StrRe = new Regex("\"([A-Za-z0-9_]+)\"");
        private static readonly Regex CastRe = new Regex("characters\\.Get\\(\"([^\"]+)\"\\)");
        // 主角自己、？？？、信鴿不算登場人物
        private static readonly Regex NotCast = new Regex("player|unknown|pigeon");

        public readonly Dictionary<string, string[]> Keys = new Dictionary<string, string[]>();
        // 劇本 -> 登場角色 id,出場次數多的在前
        private readonly Dictionary<string, string[]> _cast = new Dictionary<string, string[]>();
        private readonly Dictionary<string, string[]> _refs = new Dictionary<string, string[]>();

        // checkpointmanager.Position("X") 的 X(小寫) -> 選單上那個選項的在地化 key
        private readonly Dictionary<string, string> _actions = new Dictionary<string, string>();
        private static readonly Regex OptionRe = new Regex("(?m)^[ \\t]*[a-z_0-9]+\\[(\\d+)\\] = [^\"\\n]*\"([^\"\\n]+)\"");
        private static readonly Regex BranchRe = new Regex("choice_\\w+ == (\\d+)|checkpointmanager\\.Position\\(\"([^\"]+)\"\\)");

        public int TotalKeys { get; private set; }

        // 只能在主執行緒呼叫(Unity API)
        public static KeyValuePair<string, string>[] Load()
        {
            return Resources.LoadAll<TextAsset>(ScriptFolder)
                .Select(a => new KeyValuePair<string, string>(a.name.ToLowerInvariant(), a.text))
                .ToArray();
        }

        // 純字串處理，可在背景執行緒跑
        public StoryIndex(KeyValuePair<string, string>[] scripts)
        {
            foreach (KeyValuePair<string, string> script in scripts)
            {
                string name = script.Key;
                string text = script.Value;
                List<string> keys = new List<string>();
                foreach (Match m in KeyRe.Matches(text))
                    keys.Add(m.Groups[1].Value);
                List<string> refs = new List<string>();
                foreach (Match m in StrRe.Matches(text))
                    refs.Add(m.Groups[1].Value.ToLowerInvariant());
                Dictionary<string, int> cast = new Dictionary<string, int>();
                foreach (Match m in CastRe.Matches(text))
                {
                    string c = m.Groups[1].Value;
                    if (!NotCast.IsMatch(c))
                        cast[c] = cast.TryGetValue(c, out int n) ? n + 1 : 1;
                }
                _cast[name] = cast.OrderByDescending(x => x.Value).Select(x => x.Key).ToArray();
                Keys[name] = keys.ToArray();
                // 選單表的正規表示式最貴(全掃要 20 秒),只有同時有選單和地點事件的劇本才需要
                if (text.Contains("choose(") && text.Contains("checkpointmanager.Position("))
                    IndexActions(text);
                _refs[name] = refs.ToArray();
                TotalKeys += keys.Count;
            }
        }

        public bool Has(string script) => Keys.ContainsKey(script);

        public string ActionOf(string positionResult) =>
            _actions.TryGetValue(positionResult, out string a) ? a : null;

        // 地點劇本長這樣：mall[2] = "meditate+free+meditate" … if choice_mall == 2 then … Position("Mall_Free_001")
        // 選項字串 "類型+文字+…":工作/自由行動的文字在 Position/title/<第三段>,對話選單的在 Story/<O_ 開頭的第二段>
        private void IndexActions(string text)
        {
            Dictionary<string, string> options = new Dictionary<string, string>();
            foreach (Match m in OptionRe.Matches(text))
            {
                string[] p = m.Groups[2].Value.Split('+');
                if (p.Length > 2 && (p[1] == "work" || p[1] == "free"))
                    options[m.Groups[1].Value] = "Position/title/" + p[2];
                else if (p.Length > 1 && p[1].StartsWith("O_"))
                    options[m.Groups[1].Value] = "Story/" + p[1];
            }
            if (options.Count == 0)
                return;
            string branch = null;
            foreach (Match m in BranchRe.Matches(text))
            {
                if (m.Groups[1].Success)
                    branch = m.Groups[1].Value;
                else if (branch != null && options.TryGetValue(branch, out string id))
                    _actions[m.Groups[2].Value.ToLowerInvariant()] = id;
            }
        }

        // 玩家認得出來的描述：登場人物的遊戲內名字(Character/<id>)
        public string Cast(string script, int max = 4)
        {
            if (!_cast.TryGetValue(script, out string[] ids) || ids.Length == 0)
                return "(無其他角色)";
            return string.Join("、", ids.Select(id => LeanLocalization.GetTranslationText("Character/" + id, id))
                .Distinct().Take(max));
        }

        // 從 start 沿字串引用走到的劇本(含自己)。stop 裡的劇本有自己的入口，不算進來。
        public HashSet<string> Closure(string start, HashSet<string> stop)
        {
            HashSet<string> seen = new HashSet<string>();
            Queue<string> queue = new Queue<string>();
            if (Has(start))
            {
                seen.Add(start);
                queue.Enqueue(start);
            }
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

        // 劇本裡點名到的字串(用來找 checkpointmanager.Position("X") 的 X)
        public IEnumerable<string> RefsOf(IEnumerable<string> scripts)
        {
            foreach (string s in scripts)
                foreach (string r in _refs[s])
                    yield return r;
        }

        public static bool Ready()
        {
            ReadStorySystem rs = ReadStorySystem.Instance;
            return rs != null && Traverse.Create(rs).Field("_dicStoryIndex").GetValue() != null;
        }

        public int Unread(IEnumerable<string> scripts)
        {
            ReadStorySystem rs = ReadStorySystem.Instance;
            int n = 0;
            foreach (string s in scripts)
                foreach (string k in Keys[s])
                    if (!rs.ExistKey(k))
                        n++;
            return n;
        }

        public int UnreadAll()
        {
            return Unread(Keys.Keys);
        }
    }

    // 地點的隨機事件：PositionResultData 的劇本名在執行時才拼成 _scriptPrefix + "_" + 結果代碼
    internal static class PositionResults
    {
        public static Dictionary<string, PositionResultData> ByName()
        {
            Dictionary<string, PositionResultData> d = new Dictionary<string, PositionResultData>();
            foreach (PositionResultData p in Resources.FindObjectsOfTypeAll<PositionResultData>())
                d[p.name.ToLowerInvariant()] = p;
            return d;
        }

        // 結果的中文名：StoryMappingItem.Key 是「大失敗_001」這種，去掉尾碼
        public static string Label(PositionEventData e)
        {
            string key = Traverse.Create(e).Field("_config").Field("Key").GetValue<string>() ?? e.name;
            int i = key.LastIndexOf('_');
            return i > 0 ? key.Substring(0, i) : key;
        }

        public static string ScriptOf(PositionResultData p, PositionEventData e)
        {
            string prefix = Traverse.Create(p).Field("_scriptPrefix").GetValue<string>();
            return (prefix + "_" + e.ResultType).ToLowerInvariant();
        }
    }
}
