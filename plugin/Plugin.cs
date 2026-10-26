using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using HarmonyLib;
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
            public string Where;
            public string What;
            public int Unread;
        }

        private ConfigEntry<KeyCode> _toggleKey;
        private bool _show;
        private Rect _window = new Rect(60, 60, 620, 560);
        private Vector2 _scroll;
        private StoryIndex _index;
        private List<Hint> _hints = new List<Hint>();
        private string _summary = "";

        private void Awake()
        {
            // F3 是 LegendOfMortalMod 的選單鍵
            _toggleKey = Config.Bind("General", "ToggleKey", KeyCode.F8, "開關白字提示視窗");
            Logger.LogInfo("多玩10塊錢 loaded");
        }

        private void Update()
        {
            if (!Input.GetKeyDown(_toggleKey.Value))
                return;
            _show = !_show;
            if (_show)
                Refresh();
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
                _index = new StoryIndex();

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
            _hints.Sort((a, b) => b.Unread.CompareTo(a.Unread));

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
                string desc = Traverse.Create(m).Field("_desc").GetValue<string>();
                _hints.Add(new Hint
                {
                    Where = m.Position == PositionType.無 ? "本旬自動觸發" : "前往 " + m.Position,
                    What = "任務 " + m.Key + (string.IsNullOrEmpty(desc) ? "" : " " + desc),
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
                string where = "前往 " + place.name.Split('_').Last();
                HashSet<string> own = _index.Closure(start, Without(stop, start));

                foreach (string r in _index.RefsOf(own).Distinct())
                {
                    if (!results.TryGetValue(r, out PositionResultData pr))
                        continue;
                    List<PositionEventData> events = pr.List.Where(e => e != null).ToList();
                    int sum = events.Sum(e => e.GetRate());
                    foreach (PositionEventData e in events)
                    {
                        int rate = e.GetRate();
                        if (rate <= 0)
                            continue;
                        int unread = _index.Unread(_index.Closure(PositionResults.ScriptOf(pr, e), stop));
                        if (unread == 0)
                            continue;
                        _hints.Add(new Hint
                        {
                            Where = where,
                            What = $"{pr.name} → {e.name}(機率 {100 * rate / sum}%)",
                            Unread = unread,
                        });
                    }
                }
            }
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
            float scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            _window = GUILayout.Window(0x4B55, _window, DrawWindow, "多玩10塊錢 — 這一旬還有白字的地方");
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
                GUILayout.Label(h.Where, GUILayout.Width(150));
                GUILayout.Label(h.What);
                GUILayout.FlexibleSpace();
                GUILayout.Label(h.Unread + " 句", GUILayout.Width(70));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.Label($"按 {_toggleKey.Value} 關閉。機率是這一旬的當下值，劇本內的分支不保證都看得到。");
            GUI.DragWindow();
        }
    }
}
