using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Unity.Mono;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace KnownUnknownDev
{
    // 每幀看 BepInEx/ku-cmd.txt,有就執行、刪掉，結果寫到 ku-out.txt。
    //   ls [字串]           列出畫面上可點的東西(過濾名稱/文字)
    //   click 字串 [第幾個]  點第 N 個符合的(預設 0)
    //   scenes              列出已載入場景
    //   force 事件表 結果代碼  透過 DiceMaster 指定下次地點事件(例:force Kitchen_Work_001 大失敗_001 的結果代碼)
    //   events 事件表         列出事件表的結果代碼
    //   snap / diff           記下已讀台詞集合 / 列出之後新讀的
    //   isread key...         這些台詞是否已讀
    //   settext 字串 值        對第 N 個符合的 InputField 打字(觸發 onValueChanged)
    //   setstatic 型別 欄位 整數  設定靜態欄位
    //   dice 點數            之後所有 CheckPointManager.Dice 都用這個點數，dice 0 取消
    //   sw 名稱...            Switch 現在的值(只讀，同插件的算法)
    //   count 型別名          Resources.FindObjectsOfTypeAll 數量
    //   fields 型別名         第一個該型別物件的欄位(集合印數量)
    [BepInPlugin("known-unknown-dev", "多玩10塊錢-dev", "0.0.1")]
    public class Remote : BaseUnityPlugin
    {
        private string _cmd, _out;

        private void Awake()
        {
            _cmd = Path.Combine(Paths.BepInExRootPath, "ku-cmd.txt");
            _out = Path.Combine(Paths.BepInExRootPath, "ku-out.txt");
            Application.runInBackground = true;
            System.Reflection.Assembly.Load("Mortal.Story");
            new HarmonyLib.Harmony("known-unknown-dev").Patch(
                HarmonyLib.AccessTools.Method(TypeOf("Mortal.Story.CheckPointManager"), "Dice"),
                prefix: new HarmonyLib.HarmonyMethod(typeof(Remote), nameof(DicePrefix)));
        }

        private void Update()
        {
            if (!File.Exists(_cmd))
                return;
            string[] lines = File.ReadAllLines(_cmd);
            File.Delete(_cmd);
            StringBuilder sb = new StringBuilder();
            foreach (string line in lines.Where(l => l.Trim().Length > 0))
            {
                sb.AppendLine("> " + line);
                try
                {
                    Run(line.Trim(), sb);
                }
                catch (Exception e)
                {
                    sb.AppendLine("ERR " + e);
                }
            }
            File.WriteAllText(_out + ".tmp", sb.ToString());
            File.Copy(_out + ".tmp", _out, true);
        }

        private static void Run(string line, StringBuilder sb)
        {
            string[] a = line.Split(new[] { ' ' }, 2);
            string arg = a.Length > 1 ? a[1] : "";
            switch (a[0])
            {
                case "ls":
                    foreach (GameObject go in Clickables(arg))
                        sb.AppendLine($"{Label(go)}  [{PathOf(go)}]");
                    break;
                case "click":
                    int n = 0;
                    int sp = arg.LastIndexOf(' ');
                    if (sp > 0 && int.TryParse(arg.Substring(sp + 1), out int k))
                    {
                        n = k;
                        arg = arg.Substring(0, sp);
                    }
                    List<GameObject> hits = Clickables(arg);
                    if (n >= hits.Count)
                    {
                        sb.AppendLine($"找不到「{arg}」#{n}(共 {hits.Count})");
                        break;
                    }
                    Click(hits[n]);
                    sb.AppendLine("clicked " + Label(hits[n]) + "  [" + PathOf(hits[n]) + "]");
                    break;
                case "scenes":
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                        sb.AppendLine(SceneManager.GetSceneAt(i).name);
                    break;
                case "events":
                case "force":
                    string[] w = arg.Split(' ');
                    UnityEngine.Object table = Resources.FindObjectsOfTypeAll(TypeOf("Mortal.Story.PositionResultData"))
                        .FirstOrDefault(x => string.Equals(x.name, w[0], StringComparison.OrdinalIgnoreCase));
                    if (table == null)
                    {
                        sb.AppendLine("沒有事件表 " + w[0]);
                        break;
                    }
                    var list = (System.Collections.IEnumerable)table.GetType().GetProperty("List").GetValue(table);
                    foreach (object ev in list)
                    {
                        string type = (string)ev.GetType().GetProperty("ResultType").GetValue(ev);
                        if (a[0] == "events")
                            sb.AppendLine(type);
                        else if (type == w[1])
                        {
                            TypeOf("DiceMaster.OptionInfoPatch").GetField("TargetEvent").SetValue(null, ev);
                            sb.AppendLine("forced " + type);
                        }
                    }
                    break;
                case "snap":
                    _snap = new HashSet<string>(ReadKeys());
                    sb.AppendLine("snap " + _snap.Count);
                    break;
                case "diff":
                    List<string> now = ReadKeys();
                    sb.AppendLine($"total {now.Count}");
                    foreach (string key in now.Where(x => !_snap.Contains(x)))
                        sb.AppendLine("+ " + key);
                    break;
                case "isread":
                    HashSet<string> r = new HashSet<string>(ReadKeys());
                    foreach (string key in arg.Split(' '))
                        sb.AppendLine($"{key} {(r.Contains(key) ? "讀過" : "白字")}");
                    break;
                case "settext":
                    int sp2 = arg.LastIndexOf(' ');
                    string want = arg.Substring(0, sp2), val = arg.Substring(sp2 + 1);
                    UnityEngine.UI.InputField input = FindObjectsOfType<UnityEngine.UI.InputField>()
                        .FirstOrDefault(x => PathOf(x.gameObject).Contains(want));
                    if (input == null)
                    {
                        sb.AppendLine("沒有 InputField " + want);
                        break;
                    }
                    input.text = val;
                    sb.AppendLine($"set {PathOf(input.gameObject)} = {val}");
                    break;
                case "setstatic":
                    string[] sv = arg.Split(' ');
                    TypeOf(sv[0]).GetField(sv[1], System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic).SetValue(null, int.Parse(sv[2]));
                    sb.AppendLine("ok");
                    break;
                case "dice":
                    _dice = int.Parse(arg);
                    sb.AppendLine("next dice " + _dice);
                    break;
                case "sw":
                    var all = Resources.FindObjectsOfTypeAll(TypeOf("Mortal.Story.SwitchResultData"));
                    foreach (string name in arg.Split(' '))
                    {
                        UnityEngine.Object o2 = all.FirstOrDefault(x => x.name == name);
                        if (o2 == null)
                        {
                            sb.AppendLine(name + " 找不到");
                            continue;
                        }
                        var t2 = HarmonyLib.Traverse.Create(o2);
                        var items = (System.Collections.IList)t2.Field("_items").GetValue();
                        int swv = items.Count + 1;
                        for (int i = 0; i < items.Count; i++)
                            if ((bool)items[i].GetType().GetMethod("Check", Type.EmptyTypes).Invoke(items[i], null))
                            {
                                swv = i + 1;
                                break;
                            }
                        sb.AppendLine($"{name} = {swv} (useCount={t2.Field("_useCount").GetValue()}, count={t2.Field("_count").GetValue()})");
                    }
                    break;
                case "count":
                    sb.AppendLine(Resources.FindObjectsOfTypeAll(TypeOf(arg)).Length.ToString());
                    break;
                case "fields":
                    UnityEngine.Object o = Resources.FindObjectsOfTypeAll(TypeOf(arg)).FirstOrDefault();
                    if (o == null)
                    {
                        sb.AppendLine("none");
                        break;
                    }
                    foreach (var f in o.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    {
                        object v = f.GetValue(o);
                        string shown = v is System.Collections.ICollection c ? $"Count={c.Count}" : v?.ToString() ?? "null";
                        // 欄位是普通物件時，再展開一層集合欄位
                        if (v != null && !(v is string) && !(v is UnityEngine.Object) && !(v is System.Collections.ICollection) && !f.FieldType.IsPrimitive && !f.FieldType.IsEnum)
                            foreach (var g in v.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                                if (g.GetValue(v) is System.Collections.ICollection cc)
                                    shown += $" {g.Name}.Count={cc.Count}";
                        sb.AppendLine($"{f.Name} = {shown}");
                    }
                    break;
                default:
                    sb.AppendLine("unknown command");
                    break;
            }
        }

        private static int _dice;

        private static void DicePrefix(ref int random)
        {
            if (_dice <= 0)
                return;
            random = _dice;
        }

        private static HashSet<string> _snap = new HashSet<string>();

        private static List<string> ReadKeys()
        {
            Type t = TypeOf("Mortal.Core.ReadStorySystem");
            object rs = t.GetProperty("Instance").GetValue(null);
            var d = (System.Collections.IDictionary)t.GetField("_dicStoryIndex",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(rs);
            return d.Keys.Cast<string>().ToList();
        }

        private static Type TypeOf(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null)
            ?? throw new Exception("no type " + name);

        // 會接滑鼠點擊的物件：Button 之類的 Selectable,或自己實作點擊介面的元件
        private static List<GameObject> Clickables(string filter)
        {
            HashSet<GameObject> set = new HashSet<GameObject>();
            foreach (MonoBehaviour mb in FindObjectsOfType<MonoBehaviour>())
            {
                if (!mb.isActiveAndEnabled)
                    continue;
                if (mb is UnityEngine.UI.Selectable s ? s.IsInteractable()
                    : mb is IPointerClickHandler || mb is IPointerDownHandler)
                    set.Add(mb.gameObject);
            }
            return set.Where(go => filter.Length == 0 || Label(go).Contains(filter) || PathOf(go).Contains(filter))
                .OrderBy(PathOf).ToList();
        }

        private static void Click(GameObject go)
        {
            PointerEventData e = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
            ExecuteEvents.Execute(go, e, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.Execute(go, e, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(go, e, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(go, e, ExecuteEvents.pointerClickHandler);
        }

        // 子物件裡任何有 string text 屬性的元件(UI.Text、TMP_Text…)
        private static string Label(GameObject go)
        {
            List<string> texts = new List<string>();
            foreach (Component c in go.GetComponentsInChildren<Component>())
            {
                var p = c?.GetType().GetProperty("text", typeof(string));
                if (p == null)
                    continue;
                string t = (p.GetValue(c) as string)?.Replace("\n", " ").Trim();
                if (!string.IsNullOrEmpty(t))
                    texts.Add(t.Length > 40 ? t.Substring(0, 40) : t);
            }
            return string.Join(" / ", texts.Distinct().Take(3));
        }

        private static string PathOf(GameObject go)
        {
            string p = go.name;
            for (Transform t = go.transform.parent; t != null; t = t.parent)
                p = t.name + "/" + p;
            return p;
        }
    }
}
