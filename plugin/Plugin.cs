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
using UnityEngine.SceneManagement;

namespace KnownUnknown
{
    [BepInPlugin("known-unknown", "多玩10塊錢", "0.5.0")]
    public class Plugin : BaseUnityPlugin
    {
        private static Plugin _instance;

        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<CountUnit> _countUnit;
        private ConfigEntry<bool> _showLocked;
        private Harmony _harmony;

        // 劇本索引：讀檔在主執行緒，解析在背景
        private volatile StoryIndex _index;
        private Thread _building;
        private Hints _hints;

        // F8 視窗：開著時每秒比對遊戲狀態，有變才重算
        private bool _show;
        private Rect _window = new Rect(-1, 110, 720, 640);   // x < 0:第一次畫的時候放到右邊，避開左上角的狀態列
        private Vector2 _scroll;
        private List<Hint> _list = new List<Hint>();
        private string _summary = "";
        private string _signature;
        private float _nextCheck;

        // 選單提示：選單出現時，每個選項選了會看到多少白字。留著選項，切換「顯示未開放」時重算
        private MenuDialog _menu;
        private readonly List<KeyValuePair<string, string>> _menuOptions = new List<KeyValuePair<string, string>>();   // (劇本, 選項)
        private readonly List<KeyValuePair<string, OptionHint>> _menuHints = new List<KeyValuePair<string, OptionHint>>();

        private GUIStyle _small;
        private GUIStyle _windowStyle;

        private void Awake()
        {
            _instance = this;
            // F3 是 LegendOfMortalMod 的選單鍵
            _toggleKey = Config.Bind("General", "ToggleKey", KeyCode.F8, "開關視窗(本旬去哪裡能看到白字；選單開著時也列出每個選項)");
            _countUnit = Config.Bind("General", "CountUnit", CountUnit.Both,
                "白字怎麼算：Events = 幾段劇情(還有白字的事件/結果),Lines = 幾句台詞,Both = 兩個都顯示");
            _showLocked = Config.Bind("General", "ShowLocked", false,
                "也列出目前條件不足的白字和需要的條件(好感、屬性、旗標從哪拿)。會劇透有哪些劇情。現版本拿不到的永遠不列。視窗裡也能直接勾選");
            Hints.ShowLocked = _showLocked.Value;
            _showLocked.SettingChanged += (_, __) =>
            {
                Hints.ShowLocked = _showLocked.Value;
                _signature = null;
                RefreshMenu();
            };
            Tally.Unit = _countUnit.Value;
            _countUnit.SettingChanged += (_, __) =>
            {
                Tally.Unit = _countUnit.Value;
                _signature = null;
            };

            // Lua 的 choose() 每個選項呼叫一次這個多載；只加 postfix,不改遊戲行為
            MethodInfo addOption = typeof(MenuDialog).GetMethods().First(m =>
                m.Name == "AddOption" && m.GetParameters().Length == 4
                && m.GetParameters()[2].ParameterType.Name == "LuaEnvironment");
            _harmony = new Harmony("known-unknown");
            _harmony.Patch(addOption, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterAddOption)));
            MenuSignals.OnMenuStart += OnMenuStart;
            MenuSignals.OnMenuEnd += OnMenuEnd;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo("多玩10塊錢 loaded");
        }

        private void OnDestroy()
        {
            MenuSignals.OnMenuStart -= OnMenuStart;
            MenuSignals.OnMenuEnd -= OnMenuEnd;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
        }

        private void Update()
        {
            // 讀完存檔就在背景先建索引，第一次跳出選單時就有資料可用
            if (_building == null && Game.Ready())
                StartBuild();
            if (_hints == null && _index != null)
                _hints = new Hints(_index);

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

        private void StartBuild()
        {
            // Unity API 只能在主執行緒：劇本文字、任務資料先在這裡抓好
            KeyValuePair<string, string>[] scripts = StoryIndex.Load();
            HashSet<string> stop = Hints.StopSet();
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
                + $"{mm.ActiveSubMissions?.Count}/{mm.MainMissionStart}/{OnMap()}";
            if (sig == _signature)
                return;
            _signature = sig;
            _list = _hints.ForTurn();
            int unread = Game.Unread(_index.AllKeys);
            int total = _index.TotalKeys;
            _summary = $"全部台詞已讀 {total - unread}/{total}({100f * (total - unread) / total:F1}%),白字 {unread} 句";
            // 寫進 BepInEx 日誌，方便對照驗證
            Logger.LogInfo($"[本旬] {t.Year}年{t.Month}月{t.Stage} " + _summary + (_hints.HasResults ? "" : "(尚無地點事件表)"));
            foreach (Hint h in _list)
            {
                Logger.LogInfo($"[本旬]   {h.Where} {h.Chance} {(h.Count.Any ? h.Count.ToString() : "")} | {h.Detail}");
                foreach (string l in h.Locked)
                    Logger.LogInfo($"[本旬]     未開放 {l}");
            }
        }

        // 進劇情時地點事件表才載入，趁這時抓
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _hints?.Capture();
            _signature = null;
        }

        private void OnMenuStart(MenuDialog menu)
        {
            _menu = menu;
            _menuOptions.Clear();
            _menuHints.Clear();
        }

        private void OnMenuEnd(MenuDialog menu)
        {
            if (menu == _menu)
                OnMenuStart(null);
        }

        // 參數名要和原方法一致(text / interactable),Harmony 靠名字對應
        private static void AfterAddOption(MenuDialog __instance, string text, bool interactable)
        {
            Plugin p = _instance;
            if (p == null || p._hints == null || !interactable || __instance != p._menu)
                return;
            string script = PlayerStatManagerData.Instance.CurrentStoryScript?.ToLowerInvariant();
            p._menuOptions.Add(new KeyValuePair<string, string>(script, text));
            p.AddMenuHint(script, text);
        }

        private void AddMenuHint(string script, string text)
        {
            try
            {
                OptionHint hint = _hints.ForOption(script, text);
                Logger.LogInfo($"[選單] {script} | {text} → {hint?.Text ?? "(不認得)"} {hint?.Detail}");
                if (hint == null)
                    return;
                foreach (string l in hint.Locked)
                    Logger.LogInfo($"[選單]   未開放 {l}");
                _menuHints.Add(new KeyValuePair<string, OptionHint>(Text.OptionLabel(text), hint));
            }
            catch (Exception e)
            {
                Logger.LogError(e);
            }
        }

        private void RefreshMenu()
        {
            _menuHints.Clear();
            foreach (KeyValuePair<string, string> o in _menuOptions)
                AddMenuHint(o.Key, o.Value);
        }

        // 自由行動地圖(選地點的畫面)。跑劇情時 Free 場景還在，要看 FreeManager 是否正在處理
        private static bool OnMap()
        {
            FreeManager f = FreeManager.Instance;
            return f != null && f.isActiveAndEnabled && !f.IsProcessing;
        }

        private void OnGUI()
        {
            if (!_show)
                return;
            if (_small == null)
            {
                // 預設 fontSize 是 0(=字型預設大小),不能拿來加減
                _small = new GUIStyle(GUI.skin.label) { wordWrap = true };
                _small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
                // 預設視窗是半透明的，疊在遊戲畫面上讀不清楚
                Texture2D bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(0.08f, 0.08f, 0.08f, 0.94f));
                bg.Apply();
                _windowStyle = new GUIStyle(GUI.skin.window);
                _windowStyle.normal.background = bg;
                _windowStyle.onNormal.background = bg;
            }
            float scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            if (_window.x < 0)
                _window.x = Screen.width / scale - _window.width - 40;
            _window = GUILayout.Window(0x4B55, _window, DrawWindow, "多玩10塊錢", _windowStyle);
        }

        private const string Locked = "未開放 ";

        // 寫回設定檔；SettingChanged 會重算清單和選單
        private void ToggleLocked() => _showLocked.Value = !_showLocked.Value;

        // 選單開著時：每個選項選了會看到什麼
        private void DrawMenu()
        {
            GUILayout.Label("這個選單 — 選了會看到");
            foreach (KeyValuePair<string, OptionHint> o in _menuHints)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("  " + o.Key, GUILayout.Width(220));
                GUILayout.Label(o.Value.Text, o.Value.Text == "已看完" ? _small : GUI.skin.label);
                GUILayout.EndHorizontal();
                foreach (string l in o.Value.Locked)
                    GUILayout.Label("      " + Locked + l, _small);
            }
            GUILayout.Space(10);
        }

        private void DrawWindow(int id)
        {
            if (GUILayout.Toggle(_showLocked.Value, " 顯示未開放劇情和需要的條件(會劇透)") != _showLocked.Value)
                ToggleLocked();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(560));
            if (_menu != null && _menu.IsActive() && _menuHints.Count > 0)
                DrawMenu();
            GUILayout.Label("本旬 — " + _summary);
            if (_hints != null && !_hints.HasResults)
                GUILayout.Label("地點事件表跟著劇情場景載入，進過一次地點後才算得出各地點機率。");
            else if (_list.Count == 0 && _hints != null)
                GUILayout.Label("這一旬沒有找到會出現白字的任務或地點事件。");
            foreach (Hint h in _list)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(h.Where);
                GUILayout.FlexibleSpace();
                GUILayout.Label(h.Chance, GUILayout.Width(60));
                GUILayout.Label(h.Count.Any ? h.Count.ToString() : "", GUILayout.Width(110));
                GUILayout.EndHorizontal();
                GUILayout.Label("    " + h.Detail, _small);
                foreach (string l in h.Locked)
                    GUILayout.Label("    " + Locked + l, _small);
            }
            GUILayout.EndScrollView();
            GUILayout.Label($"機率 = 這一旬去一次會抽到沒看過結果的機率(會隨屬性/旗標變)。會自動更新，按 {_toggleKey.Value} 關閉。", _small);
            GUI.DragWindow();
        }
    }
}
