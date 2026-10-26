using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using HarmonyLib;
using Lean.Localization;
using Mortal.Core;
using Mortal.Free;
using Mortal.Story;
using UnityEngine;

namespace KnownUnknown
{
    [BepInPlugin("known-unknown", "多玩10塊錢", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        private struct Hint
        {
            public string Where;   // 去哪裡、選哪個行動
            public string Chance;  // 這一趟看到白字的機率
            public string Detail;  // 還沒看過的結果 / 登場人物
            public int Rank;       // 排序用：越大越前面
            public int Unread;
        }

        private ConfigEntry<KeyCode> _toggleKey;
        private bool _show;
        private Rect _window = new Rect(60, 60, 620, 560);
        private Vector2 _scroll;
        private volatile StoryIndex _index;
        private Thread _building;
        private bool _refreshWhenBuilt;
        private List<Hint> _hints = new List<Hint>();
        private string _summary = "";
        private GUIStyle _small;

        private void Awake()
        {
            // F3 是 LegendOfMortalMod 的選單鍵
            _toggleKey = Config.Bind("General", "ToggleKey", KeyCode.F8, "開關白字提示視窗");
            Logger.LogInfo("多玩10塊錢 loaded");
        }

        private void Update()
        {
            if (_refreshWhenBuilt && _index != null)
            {
                _refreshWhenBuilt = false;
                Refresh();
            }
            if (!Input.GetKeyDown(_toggleKey.Value))
                return;
            _show = !_show;
            if (_show)
                Refresh();
        }

        // 讀劇本要用 Unity API(主執行緒),解析 65MB 劇本丟背景，建好後 Update 自動刷新
        private void StartBuild()
        {
            KeyValuePair<string, string>[] scripts = StoryIndex.Load();
            _building = new Thread(() =>
            {
                try
                {
                    _index = new StoryIndex(scripts);
                }
                catch (Exception e)
                {
                    Logger.LogError(e);
                }
            })
            {
                IsBackground = true,
            };
            _building.Start();
        }

        private void Refresh()
        {
            _hints.Clear();
            if (!StoryIndex.Ready() || MissionManagerData.Instance == null)
            {
                _summary = "遊戲還沒載入存檔，進入遊戲後再按一次。";
                return;
            }
            if (_index == null)
            {
                if (_building == null)
                    StartBuild();
                _refreshWhenBuilt = true;
                _summary = "第一次開啟，正在整理劇本(約幾秒)…";
                return;
            }

            List<FreePositionData> places = Resources.FindObjectsOfTypeAll<FreePositionData>().ToList();
            List<MissionCheckData> missions = Resources.FindObjectsOfTypeAll<MissionCheckData>().ToList();
            // 有自己入口的劇本不算進別人的後續，免得主線一路串到結局
            HashSet<string> stop = new HashSet<string>();
            foreach (MissionCheckData m in missions)
                stop.Add(m.StartScript.ToLowerInvariant());
            foreach (FreePositionData p in places)
                stop.Add(DefaultScript(p));

            AddMissionHints(stop);
            AddPlaceHints(places, stop);
            _hints.Sort((a, b) => b.Rank != a.Rank ? b.Rank.CompareTo(a.Rank) : b.Unread.CompareTo(a.Unread));

            int unread = _index.UnreadAll();
            int total = _index.TotalKeys;
            _summary = $"全部台詞已讀 {total - unread}/{total}({100f * (total - unread) / total:F1}%),白字 {unread} 句";
        }

        private void AddMissionHints(HashSet<string> stop)
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
                int unread = _index.Unread(_index.Closure(start, Without(stop, start)));
                if (unread == 0)
                    continue;
                // 不用指定地點的任務，這一旬本來就會自己演，提示只是讓你知道
                bool auto = m.Position == PositionType.無;
                _hints.Add(new Hint
                {
                    Where = auto ? "(本旬自動發生)" : "去" + PlaceName(m.Position),
                    Chance = auto ? "" : "必定",
                    Detail = "劇情，登場：" + _index.Cast(start),
                    Rank = auto ? 0 : 1000,
                    Unread = unread,
                });
            }
        }

        private void AddPlaceHints(List<FreePositionData> places, HashSet<string> stop)
        {
            Dictionary<string, PositionResultData> results = PositionResults.ByName();
            foreach (FreePositionData place in places)
            {
                string start = DefaultScript(place);
                if (!_index.Has(start) || !place.GetActiveConditionResult())
                    continue;
                string where = "去" + place.name.Split('_').Last();
                HashSet<string> own = _index.Closure(start, Without(stop, start));

                foreach (string r in _index.RefsOf(own).Distinct())
                {
                    if (!results.TryGetValue(r, out PositionResultData pr))
                        continue;
                    // 一個行動(例如「靜坐冥想」)= 一張結果表，遊戲按當下權重抽一個結果
                    List<PositionEventData> events = pr.List.Where(e => e != null && e.GetRate() > 0).ToList();
                    int sum = events.Sum(e => e.GetRate());
                    int hit = 0, unread = 0;
                    List<string> unseen = new List<string>();
                    foreach (PositionEventData e in events)
                    {
                        string script = PositionResults.ScriptOf(pr, e);
                        int n = _index.Unread(_index.Closure(script, stop));
                        if (n == 0)
                            continue;
                        hit += e.GetRate();
                        unread += n;
                        string cast = _index.Cast(script, 2);
                        unseen.Add($"{PositionResults.Label(e)} {100 * e.GetRate() / sum}%"
                            + (cast.StartsWith("(") ? "" : $"({cast})"));
                    }
                    if (unread == 0)
                        continue;
                    int chance = 100 * hit / sum;
                    string action = _index.ActionOf(r);
                    _hints.Add(new Hint
                    {
                        Where = where + (action == null ? "" : $" →「{LeanLocalization.GetTranslationText(action, action)}」"),
                        Chance = chance + "%",
                        Detail = "沒看過的結果：" + string.Join("、", unseen),
                        Rank = chance,
                        Unread = unread,
                    });
                }
            }
        }

        // PositionType 的 [StringValue];畫面上的名字在 Position/Name/<值>(enum 名「校場」畫面上是「練功場」)
        private static readonly string[] PlaceKeys =
        {
            "", "Mall", "Center", "Alchemy", "Forge", "BackMountain", "Room1", "Door", "Study", "Kitchen", "Room2", "Secret",
        };

        private static string PlaceName(PositionType p)
        {
            int i = (int)p;
            if (i <= 0 || i >= PlaceKeys.Length)
                return p.ToString();
            return LeanLocalization.GetTranslationText("Position/Name/" + PlaceKeys[i], p.ToString());
        }

        private static string DefaultScript(FreePositionData p)
        {
            return (Traverse.Create(p).Field("_defaultScript").GetValue<string>() ?? "").ToLowerInvariant();
        }

        private static HashSet<string> Without(HashSet<string> set, string item)
        {
            HashSet<string> copy = new HashSet<string>(set);
            copy.Remove(item);
            return copy;
        }

        private void OnGUI()
        {
            if (!_show)
                return;
            if (_small == null)
            {
                // 預設 fontSize 是 0(=字型預設大小),不能拿來加減
                _small = new GUIStyle(GUI.skin.label);
                _small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
            }
            float scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            _window = GUILayout.Window(0x4B55, _window, DrawWindow, "多玩10塊錢 — 這一旬去哪裡能看到白字");
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label(_summary);
            if (GUILayout.Button("重新整理"))
                Refresh();
            _scroll = GUILayout.BeginScrollView(_scroll);
            if (_hints.Count == 0)
                GUILayout.Label("這一旬沒有找到會出現白字的任務或地點事件。");
            foreach (Hint h in _hints)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(h.Where);
                GUILayout.FlexibleSpace();
                GUILayout.Label(h.Chance, GUILayout.Width(60));
                GUILayout.Label(h.Unread + " 句", GUILayout.Width(70));
                GUILayout.EndHorizontal();
                GUILayout.Label("    " + h.Detail, _small);
            }
            GUILayout.EndScrollView();
            GUILayout.Label($"機率 = 這一旬去一次會抽到沒看過結果的機率(會隨屬性/旗標變)。按 {_toggleKey.Value} 關閉。");
            GUI.DragWindow();
        }
    }
}
