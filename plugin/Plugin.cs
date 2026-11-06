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
using UnityEngine.EventSystems;
using Mouse = UnityEngine.InputSystem.Mouse;
using InputSystem = UnityEngine.InputSystem.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace KnownUnknown
{
    [BepInPlugin("known-unknown", "多玩10塊錢", "0.13.0")]
    public class Plugin : BaseUnityPlugin
    {
        private static Plugin _instance;

        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<CountUnit> _countUnit;
        private ConfigEntry<bool> _showLocked;
        private ConfigEntry<bool> _allowForce;
        private Harmony _harmony;

        // 劇本索引：讀檔在主執行緒，解析在背景
        private volatile StoryIndex _index;
        private Thread _building;
        private Hints _hints;

        // F2 視窗：開著時每秒比對遊戲狀態，有變才重算
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

        // 在地圖上勾選 = 直接去：替玩家點地點，選單出現時按下通往事件表的那個選項
        private string _goAction;     // 等著按的選項(劇本原文)
        private Button _goButton;
        private float _goAt;

        // 擲骰：每個結果選了會看到什麼。換一次擲骰(選項陣列換了)或切換「顯示未開放」才重算
        private string[] _diceFor;
        private List<OptionHint> _diceHints = new List<OptionHint>();

        private GUIStyle _small;
        private GUIStyle _windowStyle;
        private GUIStyle _picked;
        private GUIStyle _check;

        private void Awake()
        {
            _instance = this;
            // F3 是 LegendOfMortalMod 的選單鍵。F2 左手按得到，右手不用離開滑鼠
            _toggleKey = Config.Bind("General", "ToggleKey", KeyCode.F2, "開關視窗(本旬去哪裡能看到白字；選單開著時也列出每個選項)");
            _countUnit = Config.Bind("General", "CountUnit", CountUnit.Both,
                "白字怎麼算：Events = 幾段劇情(還有白字的事件/結果),Lines = 幾句台詞,Both = 兩個都顯示");
            _showLocked = Config.Bind("General", "ShowLocked", false,
                "也列出現版本未開放的白字(條件永遠不會成立，例如沒有任何地方會設的旗標)，灰色。會劇透。角色條件還不夠的白字一律會列，橘色，附需要的條件。視窗裡也能直接勾選");
            Hints.ShowLocked = _showLocked.Value;
            _showLocked.SettingChanged += (_, __) =>
            {
                Hints.ShowLocked = _showLocked.Value;
                _signature = null;
                _diceFor = null;
                RefreshMenu();
            };
            _allowForce = Config.Bind("General", "AllowForce", true,
                "清單裡結果旁邊的勾選框 = 指定下次去那裡一定抽到它(取消勾選 = 取消);在地圖上勾會直接替你點地點、選行動。擲骰也一樣。會改變遊戲結果，不想要就關掉");
            Force.Enabled = Dice.Enabled = _allowForce.Value;
            _allowForce.SettingChanged += (_, __) => Force.Enabled = Dice.Enabled = _allowForce.Value;
            Force.Log = Dice.Log = Preload.Log = m => Logger.LogInfo(m);
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
            Force.Patch(_harmony);
            Dice.Patch(_harmony);
            MenuSignals.OnMenuStart += OnMenuStart;
            MenuSignals.OnMenuEnd += OnMenuEnd;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo("多玩10塊錢 loaded");
        }

        private void OnDestroy()
        {
            BlockGameMouse(false);
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
            bool over = MouseOverWindow();
            if (over || _muted.Count > 0 || _mouseOff)
                BlockGameMouse(over);
            // 選單剛跳出來就按，玩家看不到選單；等一下再按
            if (_goButton != null && Time.unscaledTime >= _goAt)
            {
                if (_goButton.isActiveAndEnabled && _goButton.interactable)
                    _goButton.onClick.Invoke();
                _goButton = null;
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
            // 已讀句數只在地圖上算：劇情中每讀一句就重算(一次 0.1~0.2 秒)會一頓一頓的；回到地圖(OnMap 變了)自然會更新
            bool map = OnMap();
            string sig = $"{t.Year}/{t.Month}/{(int)t.Stage}/{(map ? Game.ReadCount() : -1)}/"
                + $"{mm.ActiveSubMissions?.Count}/{mm.MainMissionStart}/{map}";
            if (sig == _signature)
                return;
            _signature = sig;
            _list = _hints.ForTurn();
            int unread = Game.Unread(_index.AllKeys);
            int total = _index.TotalKeys;
            LibrarySystem lib = LibrarySystem.Instance;
            _summary = $"全部台詞已讀 {total - unread}/{total}({100f * (total - unread) / total:F1}%),白字 {unread} 句;"
                + $"汗青書 {lib.EndGame.List.Count(x => x.Count > 0)}/{lib.EndGame.List.Count}、生死簿 {lib.Dead.List.Count(x => x.Count > 0)}/{lib.Dead.List.Count}";
            // 寫進 BepInEx 日誌，方便對照驗證
            Logger.LogInfo($"[本旬] {t.Year}年{t.Month}月{t.Stage} " + _summary + (_hints.HasResults ? "" : "(尚無地點事件表)"));
            foreach (Hint h in _list)
            {
                Logger.LogInfo($"[本旬]   {h.Where} {h.Chance} {(h.Count.Any ? h.Count.ToString() : "")} | {h.Detail}");
                foreach (string l in h.Locked)
                    Logger.LogInfo($"[本旬]     {Paint.Plain(l)}");
            }
        }

        // 進劇情時地點事件表才載入，趁這時抓
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "Title")
                Preload.Remember();
            _hints?.Capture();
            _signature = null;
            // 回到地圖：這一趟結束了，沒按到的選項不再等
            if (scene.name == "Free")
                _goAction = null;
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

        // IMGUI 視窗擋不住遊戲的輸入：UI 按鈕/地圖地點走 EventSystem,對話推進走 Input System 的滑鼠動作。
        // 滑鼠在視窗上時兩個都關掉(IMGUI 吃的是另一條平台事件，不受影響),移開再打開。
        // 關的是 EventSystem 底下的 input module,不是 EventSystem 本身：EventSystem 關掉 EventSystem.current 就變 null,
        // 遊戲的 MenuDialog.AddOption 會 NRE(在視窗裡勾「直接去」時選單正好在滑鼠還在視窗上時建立，選項沒填、劇情卡死)
        private readonly List<BaseInputModule> _muted = new List<BaseInputModule>();
        private bool _mouseOff;

        private void BlockGameMouse(bool block)
        {
            if (block)
            {
                // 換場景會有新的 EventSystem;已經關掉的不用再記
                foreach (BaseInputModule m in FindObjectsOfType<BaseInputModule>().Where(x => x.enabled))
                {
                    m.enabled = false;
                    _muted.Add(m);
                }
                if (!_mouseOff && Mouse.current != null && Mouse.current.enabled)
                {
                    InputSystem.DisableDevice(Mouse.current);
                    _mouseOff = true;
                }
                return;
            }
            foreach (BaseInputModule m in _muted.Where(x => x != null))
                m.enabled = true;
            _muted.Clear();
            if (_mouseOff && Mouse.current != null)
                InputSystem.EnableDevice(Mouse.current);
            _mouseOff = false;
        }

        // Input.mousePosition 原點在左下；視窗座標原點在左上、又被 GUI.matrix 縮放過
        private bool MouseOverWindow()
        {
            if (!_show)
                return false;
            float scale = Screen.height / 1080f;
            Vector2 m = Input.mousePosition;
            return _window.Contains(new Vector2(m.x / scale, (Screen.height - m.y) / scale));
        }

        // 參數名要和原方法一致(text / interactable),Harmony 靠名字對應
        private static void AfterAddOption(MenuDialog __instance, string text, bool interactable)
        {
            Plugin p = _instance;
            if (p == null || p._hints == null || !interactable || __instance != p._menu)
                return;
            if (p._goAction != null && text.TrimStart('~') == p._goAction.TrimStart('~'))
            {
                p._goButton = __instance.CachedButtons[Traverse.Create(__instance).Field("nextOptionIndex").GetValue<int>() - 1];
                p._goAt = Time.unscaledTime + 0.6f;
                p._goAction = null;
            }
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
                    Logger.LogInfo($"[選單]   {Paint.Plain(l)}");
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
        // 跟玩家自己點地圖同一條路(行動點、主線、支線任務照遊戲規則),所以不一定會去成
        private void Go(Outcome o)
        {
            Place place = _hints.PlaceOf(o.Table);
            PositionController pc = place == null ? null : Resources.FindObjectsOfTypeAll<PositionController>().FirstOrDefault(c =>
                c.isActiveAndEnabled && Traverse.Create(c).Field("_positionData").GetValue<FreePositionData>() == place.Data);
            if (pc == null)
            {
                Logger.LogWarning($"[前往] 地圖上找不到 {o.Table.name} 的入口");
                return;
            }
            Logger.LogInfo($"[前往] {place.Where}");
            _goAction = place.Action;
            pc.OnPositionClick();
            // 沒行動點、主線在別處：遊戲沒出發(地圖照樣可以點),不必再等選單
            if (!FreeManager.Instance.IsProcessing)
                _goAction = null;
        }

        private static bool OnMap()
        {
            FreeManager f = FreeManager.Instance;
            return f != null && f.isActiveAndEnabled && !f.IsProcessing;
        }

        private void OnGUI()
        {
            if (!_show)
                return;
            // 換場景時遊戲會卸載沒人用的資源，背景貼圖被清掉視窗就變全透明
            if (_small == null || _windowStyle.normal.background == null || _check.normal.background == null)
            {
                // 預設 fontSize 是 0(=字型預設大小),不能拿來加減
                _small = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true };
                _small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
                // 預設視窗是半透明的，疊在遊戲畫面上讀不清楚
                Texture2D bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(0.08f, 0.08f, 0.08f, 0.94f));
                bg.Apply();
                bg.hideFlags = HideFlags.HideAndDontSave;
                _windowStyle = new GUIStyle(GUI.skin.window);
                _windowStyle.normal.background = bg;
                _windowStyle.onNormal.background = bg;
                _picked = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true };
                _picked.normal.textColor = new Color(1f, 0.85f, 0.3f);
                // 指定用的勾選框：預設的太暗看不到。沒勾 = 金框，勾了 = 整格金色
                _check = new GUIStyle
                {
                    fixedWidth = 16, fixedHeight = 16, margin = new RectOffset(4, 6, 4, 0),
                    normal = { background = Box(false) }, onNormal = { background = Box(true) },
                    hover = { background = Box(false) }, onHover = { background = Box(true) },
                    active = { background = Box(true) }, onActive = { background = Box(true) },
                };
            }
            float scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            if (_window.x < 0)
                _window.x = Screen.width / scale - _window.width - 40;
            _window = GUILayout.Window(0x4B55, _window, DrawWindow, "多玩10塊錢", _windowStyle);
        }

        // 寫回設定檔；SettingChanged 會重算清單和選單
        private void ToggleLocked() => _showLocked.Value = !_showLocked.Value;

        private static readonly Color Gold = new Color(1f, 0.8f, 0.2f);

        private static Texture2D Box(bool filled)
        {
            const int n = 16;
            Texture2D t = new Texture2D(n, n) { hideFlags = HideFlags.HideAndDontSave };
            for (int x = 0; x < n; x++)
            {
                for (int y = 0; y < n; y++)
                {
                    bool edge = x < 2 || y < 2 || x >= n - 2 || y >= n - 2;
                    t.SetPixel(x, y, filled || edge ? Gold : new Color(0, 0, 0, 0.6f));
                }
            }
            t.Apply();
            return t;
        }

        private bool Check(bool on) => GUILayout.Toggle(on, GUIContent.none, _check);

        // 地點事件的一個結果；勾 = 指定下次抽到它，在地圖上就直接去。現在抽到也看不到白字的整行橘色
        private void DrawOutcome(Outcome o, string prefix)
        {
            bool on = Force.Is(o);
            string label = prefix + o.Label;
            GUILayout.BeginHorizontal();
            GUILayout.Space(16);
            if (_allowForce.Value && o.Table != null && Check(on) != on)
            {
                Force.Toggle(o);
                if (Force.Is(o) && OnMap())
                    Go(o);
            }
            GUILayout.Label(o.Count.Any ? label : Paint.Color(Paint.Lacking, label), on ? _picked : _small, GUILayout.Width(510));
            GUILayout.Label(o.Count.Any ? o.Count.ToString() : "", _small, GUILayout.Width(110));
            GUILayout.EndHorizontal();
            foreach (string l in o.Locked)
                GUILayout.Label("              " + l, _small);
        }

        // 測試用(dev 插件 invoke):跟勾選框同一條路
        private string ToggleForce(string table, string type)
        {
            Outcome o = _list.SelectMany(h => h.Outcomes).FirstOrDefault(x =>
                x.Table != null && x.Table.name == table && x.Type == type);
            if (o == null)
                return "not found";
            Force.Toggle(o);
            if (Force.Is(o) && OnMap())
                Go(o);
            return Force.Is(o) ? "forced " + o.Label : "cleared";
        }

        // 測試用(dev 插件 invoke):某劇本某選項的提示(跟選單同一條路)
        private string OptionTest(string script, string option)
        {
            OptionHint h = _hints.ForOption(script, option);
            return h == null ? "null" : h.Text + " | " + string.Join(" / ", h.Locked.Select(Paint.Plain));
        }

        // 測試用(dev 插件 invoke):劇本裡抓到的講經堂項目，和講經堂實際收錄的對照
        private string LibraryTest()
        {
            List<string> keys = _index.LibraryKeys.ToList();
            LibrarySystem lib = LibrarySystem.Instance;
            int ends = keys.Count(k => k.StartsWith(Library.End)), deaths = keys.Count(k => k.StartsWith(Library.Dead));
            int seenEnds = keys.Count(k => k.StartsWith(Library.End) && Game.Seen(k)), seenDeaths = keys.Count(k => k.StartsWith(Library.Dead) && Game.Seen(k));
            int unknown = keys.Count(k => (k.StartsWith(Library.End) ? lib.EndGame : lib.Dead).Get(k.Substring(k.IndexOf(':') + 1)) == null);
            return $"汗青書 劇本 {ends} 個(已收 {seenEnds}) 生死簿 劇本 {deaths} 個(已收 {seenDeaths}) 講經堂查不到 {unknown};"
                + $" 講經堂實際：汗青書 {lib.EndGame.List.Count(x => x.Count > 0)} 生死簿 {lib.Dead.List.Count(x => x.Count > 0)}; 例 {string.Join(",", keys.Take(4))}";
        }

        // 測試用(dev 插件 invoke):把視窗移到滑鼠底下 / 移開，看遊戲的滑鼠輸入有沒有關掉
        private string MouseTest(string under)
        {
            float scale = Screen.height / 1080f;
            Vector2 m = Input.mousePosition;
            Vector2 at = new Vector2(m.x / scale, (Screen.height - m.y) / scale);
            if (under == "1")
                _window.position = at - new Vector2(50, 50);
            else if (under == "0")
                _window.position = at + new Vector2(200, 200);
            return $"mouse {at} window {_window} over {MouseOverWindow()} muted {_muted.Count} mouseOff {_mouseOff} "
                + $"device {Mouse.current?.enabled} inputModules {string.Join(",", FindObjectsOfType<BaseInputModule>().Select(x => x.enabled))}";
        }

        // 測試用(dev 插件 invoke):跟擲骰那一列的勾選框同一條路
        private string ToggleDice(string k)
        {
            if (!Dice.Active)
                return "no dice";
            Dice.Toggle(int.Parse(k));
            return $"target {Dice.Target} faces {string.Join(",", Dice.Faces())}";
        }

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
                    GUILayout.Label("      " + l, _small);
            }
            GUILayout.Space(10);
        }

        // 擲骰時：每個結果的機率、看到什麼；勾 = 指定擲出它
        private void DrawDice()
        {
            if (_diceFor != Dice.Options)
            {
                _diceFor = Dice.Options;
                string script = PlayerStatManagerData.Instance.CurrentStoryScript?.ToLowerInvariant();
                _diceHints = Dice.Options.Select(o => _hints.ForOption(script, o)).ToList();
            }
            int[] faces = Dice.Faces();
            int total = faces.Sum();
            GUILayout.Label("這次擲骰 — 機率、會看到");
            for (int i = 0; i < Dice.Options.Length; i++)
            {
                int k = i + 1;
                OptionHint h = _diceHints[i];
                bool on = Dice.Target == k;
                string chance = faces[k] == 0 ? "擲不到" : $"{100 * faces[k] / Math.Max(total, 1)}%";
                GUILayout.BeginHorizontal();
                if (!_allowForce.Value || faces[k] == 0)
                    GUILayout.Space(26);   // 跟勾選框同寬(16 + 左右邊界)
                else if (Check(on) != on)
                    Dice.Toggle(k);
                GUILayout.Label(Text.OptionLabel(Dice.Options[i]), on ? _picked : GUI.skin.label, GUILayout.Width(304));
                GUILayout.Label(chance, GUILayout.Width(60));
                GUILayout.Label(h?.Text ?? "", h == null || h.Text == "已看完" ? _small : GUI.skin.label);
                GUILayout.EndHorizontal();
                foreach (string l in h?.Locked ?? new List<string>())
                    GUILayout.Label("      " + l, _small);
            }
            GUILayout.Space(10);
        }

        private void DrawWindow(int id)
        {
            if (GUILayout.Toggle(_showLocked.Value, " 也顯示現版本未開放的劇情(灰色，會劇透)") != _showLocked.Value)
                ToggleLocked();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(560));
            // 指定了、但現在清單上沒列的(條件變了、換旬了)才另外列，不然勾了就找不到地方取消
            HashSet<string> listed = new HashSet<string>(_list.SelectMany(h => h.Outcomes).Where(Force.Is).Select(o => o.Table.name + "/" + o.Type));
            List<Outcome> unlisted = Force.All.Where(o => !listed.Contains(o.Table.name + "/" + o.Type)).ToList();
            if (_allowForce.Value && unlisted.Count > 0)
            {
                GUILayout.Label("已指定(清單上沒列出的):", _picked);
                foreach (Outcome o in unlisted)
                    DrawOutcome(o, "");
                GUILayout.Space(10);
            }
            if (_menu != null && _menu.IsActive() && _menuHints.Count > 0)
                DrawMenu();
            if (_hints != null && Dice.Active)
                DrawDice();
            GUILayout.Label("本旬 — " + _summary);
            if (_hints != null && !_hints.HasResults)
                GUILayout.Label("內建的地點事件表和遊戲版本對不上(見日誌 [預載]),進過一次地點後才算得出各地點機率。");
            else if (_list.Count == 0 && _hints != null)
                GUILayout.Label("這一旬沒有找到會出現白字的任務或地點事件。");
            foreach (Hint h in _list)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(h.Where, GUILayout.Width(500));
                GUILayout.Label(h.Chance, GUILayout.Width(50));
                GUILayout.Label(h.Count.Any ? h.Count.ToString() : "", GUILayout.Width(110));
                GUILayout.EndHorizontal();
                // 地點：逐一列出還有白字的結果；旗標：取得處。其他(任務)照舊一行說明
                if (h.Outcomes.Count == 0 || h.Flag)
                    GUILayout.Label("    " + h.Detail, _small);
                foreach (Outcome o in h.Outcomes)
                    DrawOutcome(o, h.Flag ? "在 " : "");
                if (h.Outcomes.Count == 0)
                {
                    foreach (string l in h.Locked)
                        GUILayout.Label("    " + l, _small);
                }
            }
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }
    }
}
