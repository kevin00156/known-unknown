using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Mortal.Core;
using Mortal.Free;
using Mortal.Story;
using UnityEngine;

namespace KnownUnknown
{
    internal class Hint
    {
        public string Where;   // 去哪裡、選哪個行動
        public string Chance;  // 這一趟看到白字的機率
        public string Detail;  // 沒看過的結果 / 登場人物
        public int Rank;       // 排序用：越大越前面
        public int Unread;
    }

    // 地點事件表抽一次的結果
    internal struct Roll
    {
        public int Chance;           // 抽到還有白字的結果的機率(%)
        public int Unread;
        public List<string> Unseen;  // 「失敗 25%(唐中翎)」
    }

    // 依當下遊戲狀態算提示。遊戲資料(ScriptableObject)在建構時抓一次，之後只做查表。
    internal class Hints
    {
        private readonly StoryIndex _index;
        private readonly List<FreePositionData> _places;
        private readonly Dictionary<string, PositionResultData> _results;

        // PositionType 的 [StringValue];畫面上的名字在 Position/Name/<值>(enum 名「校場」畫面上是「練功場」)
        private static readonly string[] PlaceKeys =
        {
            "", "Mall", "Center", "Alchemy", "Forge", "BackMountain", "Room1", "Door", "Study", "Kitchen", "Room2", "Secret",
        };

        public Hints(StoryIndex index, List<FreePositionData> places)
        {
            _index = index;
            _places = places;
            _results = new Dictionary<string, PositionResultData>();
            foreach (PositionResultData p in Resources.FindObjectsOfTypeAll<PositionResultData>())
                _results[p.name.ToLowerInvariant()] = p;
        }

        // 有自己入口的劇本：任務起點、地點預設劇本。算「後續劇情」時走到這裡就停，免得主線一路串到結局。
        public static HashSet<string> StopSet(List<FreePositionData> places)
        {
            HashSet<string> stop = new HashSet<string>();
            foreach (MissionCheckData m in Resources.FindObjectsOfTypeAll<MissionCheckData>())
                stop.Add(m.StartScript.ToLowerInvariant());
            foreach (FreePositionData p in places)
                stop.Add(DefaultScript(p));
            return stop;
        }

        // 完全照 PositionResultData.GetResult / PositionEvent:依清單順序取機率 > 0 的事件，
        // 同一個結果代碼只算第一筆，在 1..總和 之間均勻抽。
        public Roll RollOf(PositionResultData pr)
        {
            HashSet<string> types = new HashSet<string>();
            List<KeyValuePair<PositionEventData, int>> events = new List<KeyValuePair<PositionEventData, int>>();
            foreach (PositionEventData e in pr.List)
            {
                if (e == null)
                    continue;
                int rate = e.GetRate();
                if (rate > 0 && types.Add(e.ResultType))
                    events.Add(new KeyValuePair<PositionEventData, int>(e, rate));
            }
            int sum = events.Sum(x => x.Value);
            Roll roll = new Roll { Unseen = new List<string>() };
            int hit = 0;
            foreach (KeyValuePair<PositionEventData, int> x in events)
            {
                string script = ScriptOf(pr, x.Key);
                int n = Game.Unread(_index.ClosureKeys(script));
                if (n == 0)
                    continue;
                hit += x.Value;
                roll.Unread += n;
                string cast = _index.Cast(script, 2);
                roll.Unseen.Add($"{Label(x.Key)} {100 * x.Value / sum}%" + (cast.Length > 0 ? $"({cast})" : ""));
            }
            roll.Chance = sum == 0 ? 0 : 100 * hit / sum;
            return roll;
        }

        // 選單上某個選項：選了會看到多少白字
        public string ForOption(string script, string option)
        {
            OptionBranch b = _index.Option(script, option);
            if (b == null)
                return null;
            int unread = Game.Unread(b.Keys);
            List<string> parts = new List<string>();
            foreach (string p in b.Positions)
            {
                if (!_results.TryGetValue(p, out PositionResultData pr))
                    continue;
                Roll r = RollOf(pr);
                if (r.Unread > 0)
                    parts.Add($"{r.Chance}% 抽到白字({r.Unread} 句)");
            }
            if (unread > 0)
                parts.Add($"白字 {unread} 句");
            return parts.Count == 0 ? "已看完" : string.Join(",", parts);
        }

        // F8 清單：這一旬去哪裡能看到白字
        public List<Hint> ForTurn()
        {
            List<Hint> hints = new List<Hint>();
            AddMissions(hints);
            AddPlaces(hints);
            hints.Sort((a, b) => b.Rank != a.Rank ? b.Rank.CompareTo(a.Rank) : b.Unread.CompareTo(a.Unread));
            return hints;
        }

        private void AddMissions(List<Hint> hints)
        {
            MissionManagerData mm = MissionManagerData.Instance;
            List<MissionCheckData> active = new List<MissionCheckData>();
            if (mm.ActiveSubMissions != null)
                active.AddRange(mm.ActiveSubMissions.Values);
            if (mm.MainMissionStart && mm.MainCheckData != null)
                active.Add(mm.MainCheckData);

            foreach (MissionCheckData m in active)
            {
                string start = m.StartScript.ToLowerInvariant();
                int unread = Game.Unread(_index.ClosureKeys(start));
                if (unread == 0)
                    continue;
                // 不用指定地點的任務，這一旬本來就會自己演，提示只是讓你知道
                bool auto = m.Position == PositionType.無;
                string cast = _index.Cast(start);
                hints.Add(new Hint
                {
                    Where = auto ? "(本旬自動發生)" : "去" + PlaceName(m.Position),
                    Chance = auto ? "" : "必定",
                    Detail = "劇情" + (cast.Length > 0 ? ",登場：" + cast : ""),
                    Rank = auto ? -1 : 1000,
                    Unread = unread,
                });
            }
        }

        private void AddPlaces(List<Hint> hints)
        {
            foreach (FreePositionData place in _places)
            {
                string start = DefaultScript(place);
                if (!_index.Has(start) || !place.GetActiveConditionResult())
                    continue;
                string where = "去" + place.name.Split('_').Last();
                foreach (string r in _index.ClosurePositions(start))
                {
                    if (!_results.TryGetValue(r, out PositionResultData pr))
                        continue;
                    Roll roll = RollOf(pr);
                    if (roll.Unread == 0)
                        continue;
                    string action = _index.ActionOf(r);
                    hints.Add(new Hint
                    {
                        Where = where + (action == null ? "" : $" →「{Text.OptionLabel(action)}」"),
                        Chance = roll.Chance + "%",
                        Detail = "沒看過的結果：" + string.Join("、", roll.Unseen),
                        Rank = roll.Chance,
                        Unread = roll.Unread,
                    });
                }
            }
        }

        private static string PlaceName(PositionType p)
        {
            int i = (int)p;
            return i > 0 && i < PlaceKeys.Length ? Text.Get("Position/Name/" + PlaceKeys[i], p.ToString()) : p.ToString();
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
