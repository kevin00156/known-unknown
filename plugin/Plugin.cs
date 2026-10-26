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
    [BepInPlugin("known-unknown", "多玩10塊錢", "0.4.0")]
    public class Plugin : BaseUnityPlugin
    {
        private static Plugin _instance;

        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<bool> _menuHints;
        private ConfigEntry<bool> _mapHints;
        private ConfigEntry<CountUnit> _countUnit;
        private ConfigEntry<bool> _showLocked;
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
            _mapHints = Config.Bind("General", "MapHints", true, "在自由行動地圖上，顯示本旬去哪裡能看到白字");
            _countUnit = Config.Bind("General", "CountUnit", CountUnit.Both,
                "白字怎麼算：Events = 幾段劇情(還有白字的事件/結果),Lines = 幾句台詞,Both = 兩個都顯示");
            _showLocked = Config.Bind("General", "ShowLocked", false,
                "也列出目前條件不足的白字和需要的條件(好感、屬性、旗標從哪拿)。會劇透有哪些劇情。現版本拿不到的永遠不列");
            Hints.ShowLocked = _showLocked.Value;
            _showLocked.SettingChanged += (_, __) =>
            {
                Hints.ShowLocked = _showLocked.Value;
                _signature = null;
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
            if ((_show || OnMap()) && Time.unscaledTime >= _nextCheck)
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
                OptionHint hint = p._hints.ForOption(script, text);
                p.Logger.LogInfo($"[選單] {script} | {text} → {hint?.Text ?? "(不認得)"} {hint?.Detail}");
                if (hint == null)
                    return;
                p._menuLines.Add(new KeyValuePair<string, string>(Text.OptionLabel(text), hint.Text));
                foreach (string l in hint.Locked)
                {
                    p.Logger.LogInfo($"[選單]   未開放 {l}");
                    p._menuLines.Add(new KeyValuePair<string, string>("", Locked + l));
                }
            }
            catch (Exception e)
            {
                p.Logger.LogError(e);
            }
        }

        // 自由行動地圖(選地點的畫面)。跑劇情時 Free 場景還在，要看 FreeManager 是否正在處理
        private static bool OnMap()
        {
            FreeManager f = FreeManager.Instance;
            return f != null && f.isActiveAndEnabled && !f.IsProcessing;
        }

        private void OnGUI()
        {
            bool menu = _menu != null && _menu.IsActive() && _menuLines.Count > 0;
            bool map = !menu && !_show && _mapHints.Value && _hints != null && OnMap();
            if (!_show && !menu && !map)
                return;
            if (_small == null)
            {
                // 預設 fontSize 是 0(=字型預設大小),不能拿來加減
                _small = new GUIStyle(GUI.skin.label) { wordWrap = true };
                _small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
            }
            float scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            if (menu)
                DrawMenuHints();
            if (map)
                DrawMapHints();
            if (_show)
                _window = GUILayout.Window(0x4B55, _window, DrawWindow, "多玩10塊錢 — 這一旬去哪裡能看到白字");
        }

        // 右上角小框，避開左上角的心相/行動列
        private const string Locked = "未開放 ";

        // 右上角小框，高度跟著內容(條件說明會換行)
        private void DrawBox(string title, List<KeyValuePair<string, string>> lines)
        {
            float width = 600;
            float right = Screen.width * 1080f / Screen.height;   // GUI.matrix 縮放後的畫面寬
            GUILayout.BeginArea(new Rect(right - width - 20, 150, width, 1080 - 200));
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(title);
            foreach (KeyValuePair<string, string> l in lines)
            {
                // 沒有標籤的列(未開放的說明)佔滿整行
                if (l.Key.Length == 0)
                {
                    GUILayout.Label("    " + l.Value, _small);
                    continue;
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label(l.Key, GUILayout.Width(200));
                GUILayout.Label(l.Value, l.Value == "已看完" ? _small : GUI.skin.label);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private void DrawMenuHints()
        {
            DrawBox("多玩10塊錢 — 選了會看到", _menuLines);
        }

        // 地圖上只列機率最高的幾個，完整清單按 F8
        private void DrawMapHints()
        {
            List<KeyValuePair<string, string>> lines = _list.Where(h => h.Rank >= 0).Take(6)
                .Select(h => new KeyValuePair<string, string>(h.Where, $"{h.Chance} {h.Count}")).ToList();
            // 本旬拿得到、之後會解鎖白字的旗標(ShowLocked 才有)
            lines.AddRange(_list.Where(h => h.Flag).Take(3)
                .Select(h => new KeyValuePair<string, string>("", $"{h.Where} → 之後 {h.Count}:{h.Detail}")));
            if (!_hints.HasResults)
                lines.Add(new KeyValuePair<string, string>("地點機率要等進過一次地點才算得出來", ""));
            else if (lines.Count == 0)
                lines.Add(new KeyValuePair<string, string>("本旬各地點都看完了", ""));
            DrawBox($"多玩10塊錢 — 本旬白字(完整清單按 {_toggleKey.Value})", lines);
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label(_summary);
            _scroll = GUILayout.BeginScrollView(_scroll);
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
            GUILayout.Label($"機率 = 這一旬去一次會抽到沒看過結果的機率(會隨屬性/旗標變)。會自動更新，按 {_toggleKey.Value} 關閉。");
            GUI.DragWindow();
        }
    }
}
