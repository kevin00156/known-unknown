using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using Fungus;
using HarmonyLib;
using Mortal.Core;
using Mortal.Free;
using UnityEngine;

namespace KnownUnknown
{
    [BepInPlugin("known-unknown", "多玩10塊錢", "0.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        private static Plugin _instance;

        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<bool> _menuHints;
        private Harmony _harmony;

        // 劇本索引：讀檔在主執行緒，解析在背景
        private volatile StoryIndex _index;
        private Thread _building;
        private Hints _hints;

        // F8 視窗：開著時每秒比對遊戲狀態，有變才重算
        private bool _show;
        private Rect _window = new Rect(60, 60, 640, 560);
        private Vector2 _scroll;
        private List<Hint> _list = new List<Hint>();
        private string _summary = "";
        private string _signature;
        private float _nextCheck;

        // 選單提示：選單出現時，每個選項選了會看到多少白字
        private MenuDialog _menu;
        private readonly List<KeyValuePair<string, string>> _menuLines = new List<KeyValuePair<string, string>>();

        private GUIStyle _small;

        private void Awake()
        {
            _instance = this;
            // F3 是 LegendOfMortalMod 的選單鍵
            _toggleKey = Config.Bind("General", "ToggleKey", KeyCode.F8, "開關「這一旬去哪裡能看到白字」視窗");
            _menuHints = Config.Bind("General", "MenuHints", true, "選單出現時，顯示每個選項會看到多少白字");

            // Lua 的 choose() 每個選項呼叫一次這個多載；只加 postfix,不改遊戲行為
            MethodInfo addOption = typeof(MenuDialog).GetMethods().First(m =>
                m.Name == "AddOption" && m.GetParameters().Length == 4
                && m.GetParameters()[2].ParameterType.Name == "LuaEnvironment");
            _harmony = new Harmony("known-unknown");
            _harmony.Patch(addOption, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterAddOption)));
            MenuSignals.OnMenuStart += OnMenuStart;
            MenuSignals.OnMenuEnd += OnMenuEnd;
            Logger.LogInfo("多玩10塊錢 loaded");
        }

        private void OnDestroy()
        {
            MenuSignals.OnMenuStart -= OnMenuStart;
            MenuSignals.OnMenuEnd -= OnMenuEnd;
            _harmony?.UnpatchSelf();
        }

        private void Update()
        {
            // 讀完存檔就在背景先建索引，第一次跳出選單時就有資料可用
            if (_building == null && Game.Ready())
                StartBuild();
            if (_hints == null && _index != null)
                _hints = new Hints(_index, _places);

            if (Input.GetKeyDown(_toggleKey.Value))
            {
                _show = !_show;
                _signature = null;
            }
            if (_show && Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = Time.unscaledTime + 1;
                RefreshIfChanged();
            }
        }

        private List<FreePositionData> _places;

        private void StartBuild()
        {
            // Unity API 只能在主執行緒：劇本文字、任務與地點資料先在這裡抓好
            KeyValuePair<string, string>[] scripts = StoryIndex.Load();
            _places = Resources.FindObjectsOfTypeAll<FreePositionData>().ToList();
            HashSet<string> stop = Hints.StopSet(_places);
            _building = new Thread(() =>
            {
                try
                {
                    _index = new StoryIndex(scripts, stop);
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

        // 旬、已讀句數、本旬任務有變才重算；看完一段劇情、換旬後清單自己更新
        private void RefreshIfChanged()
        {
            if (_hints == null || !Game.Ready())
            {
                _summary = _building == null ? "進入遊戲後就會開始整理劇本。" : "正在整理劇本(約幾秒)…";
                _list.Clear();
                return;
            }
            MissionManagerData mm = MissionManagerData.Instance;
            GameTime t = PlayerStatManagerData.Instance.GameTime;
            string sig = $"{t.Year}/{t.Month}/{(int)t.Stage}/{Game.ReadCount()}/"
                + $"{mm.ActiveSubMissions?.Count}/{mm.MainMissionStart}";
            if (sig == _signature)
                return;
            _signature = sig;
            _list = _hints.ForTurn();
            int unread = Game.Unread(_index.AllKeys);
            int total = _index.TotalKeys;
            _summary = $"全部台詞已讀 {total - unread}/{total}({100f * (total - unread) / total:F1}%),白字 {unread} 句";
        }

        private void OnMenuStart(MenuDialog menu)
        {
            _menu = menu;
            _menuLines.Clear();
        }

        private void OnMenuEnd(MenuDialog menu)
        {
            if (menu == _menu)
            {
                _menu = null;
                _menuLines.Clear();
            }
        }

        // 參數名要和原方法一致(text / interactable),Harmony 靠名字對應
        private static void AfterAddOption(MenuDialog __instance, string text, bool interactable)
        {
            Plugin p = _instance;
            if (p == null || !p._menuHints.Value || p._hints == null || !interactable || __instance != p._menu)
                return;
            try
            {
                string script = PlayerStatManagerData.Instance.CurrentStoryScript?.ToLowerInvariant();
                string info = p._hints.ForOption(script, text);
                if (info != null)
                    p._menuLines.Add(new KeyValuePair<string, string>(Text.OptionLabel(text), info));
            }
            catch (Exception e)
            {
                p.Logger.LogError(e);
            }
        }

        private void OnGUI()
        {
            bool menu = _menu != null && _menu.IsActive() && _menuLines.Count > 0;
            if (!_show && !menu)
                return;
            if (_small == null)
            {
                // 預設 fontSize 是 0(=字型預設大小),不能拿來加減
                _small = new GUIStyle(GUI.skin.label);
                _small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
            }
            float scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            if (menu)
                DrawMenuHints();
            if (_show)
                _window = GUILayout.Window(0x4B55, _window, DrawWindow, "多玩10塊錢 — 這一旬去哪裡能看到白字");
        }

        // 選單旁的小框：不擋選項，固定在右上角
        private void DrawMenuHints()
        {
            float width = 460;
            float right = Screen.width * 1080f / Screen.height;   // GUI.matrix 縮放後的畫面寬
            GUILayout.BeginArea(new Rect(right - width - 20, 20, width, 40 + 30 * _menuLines.Count), GUI.skin.box);
            GUILayout.Label("多玩10塊錢");
            foreach (KeyValuePair<string, string> l in _menuLines)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(l.Key, GUILayout.Width(170));
                GUILayout.Label(l.Value, l.Value == "已看完" ? _small : GUI.skin.label);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label(_summary);
            _scroll = GUILayout.BeginScrollView(_scroll);
            if (_list.Count == 0 && _hints != null)
                GUILayout.Label("這一旬沒有找到會出現白字的任務或地點事件。");
            foreach (Hint h in _list)
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
            GUILayout.Label($"機率 = 這一旬去一次會抽到沒看過結果的機率(會隨屬性/旗標變)。會自動更新，按 {_toggleKey.Value} 關閉。");
            GUI.DragWindow();
        }
    }
}
