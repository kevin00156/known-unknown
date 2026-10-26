using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;

namespace KnownUnknown
{
    // 劇本 → 台詞 key、劇本之間的字串引用。遊戲啟動後建一次就好，內容不會變。
    internal class StoryIndex
    {
        // LuaManager 從 Resources 的 Story/ChineseTraditional/<劇本> 載入；Resources 路徑不分大小寫，一律小寫
        private const string ScriptFolder = "Story/ChineseTraditional";
        private static readonly Regex KeyRe = new Regex("GetStoryText\\(\"([^\"]+)\"\\)");
        private static readonly Regex StrRe = new Regex("\"([A-Za-z0-9_]+)\"");

        public readonly Dictionary<string, string[]> Keys = new Dictionary<string, string[]>();
        private readonly Dictionary<string, string[]> _refs = new Dictionary<string, string[]>();

        public int TotalKeys { get; private set; }

        public StoryIndex()
        {
            foreach (TextAsset asset in Resources.LoadAll<TextAsset>(ScriptFolder))
            {
                string name = asset.name.ToLowerInvariant();
                string text = asset.text;
                List<string> keys = new List<string>();
                foreach (Match m in KeyRe.Matches(text))
                    keys.Add(m.Groups[1].Value);
                List<string> refs = new List<string>();
                foreach (Match m in StrRe.Matches(text))
                    refs.Add(m.Groups[1].Value.ToLowerInvariant());
                Keys[name] = keys.ToArray();
                _refs[name] = refs.ToArray();
                TotalKeys += keys.Count;
            }
        }

        public bool Has(string script) => Keys.ContainsKey(script);

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

        public static string ScriptOf(PositionResultData p, PositionEventData e)
        {
            string prefix = Traverse.Create(p).Field("_scriptPrefix").GetValue<string>();
            return (prefix + "_" + e.ResultType).ToLowerInvariant();
        }
    }
}
