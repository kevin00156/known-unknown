using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Mortal.Story;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KnownUnknown
{
    // 地點事件表在劇情場景的資產裡，沒進過劇情就不在記憶體。照 known_unknown.py --position-tables 抽出的資料
    // 重建一份：同檔的物件(事件、結果代碼、數值變數)新建，引用到常駐資產的(屬性、旗標、書…)接回遊戲裡的活物件，
    // 所以算出來的機率就是當下的真值。真的表載入後以真表為準，順便逐一比對機率當驗證。
    internal static class Preload
    {
        private const string Resource = "KnownUnknown.PositionTables.txt";
        private const string AssetsFile = "sharedassets2.assets";   // 劇情場景的資產；大小對不上 = 遊戲更新了

        public static Action<string> Log = _ => { };

        private static readonly HashSet<Object> Copies = new HashSet<Object>();
        private static List<PositionResultData> _tables;

        public static bool IsCopy(Object o) => Copies.Contains(o);

        // 只建一次；建不出來(資料舊了、引用接不上)就回空的，照舊等進劇情
        public static List<PositionResultData> Tables()
        {
            if (_tables != null)
                return _tables;
            try
            {
                _tables = Build();
            }
            catch (Exception e)
            {
                Log("[預載] 失敗，先等進劇情：" + e);
                _tables = new List<PositionResultData>();
            }
            return _tables;
        }

        private static List<PositionResultData> Build()
        {
            List<PositionResultData> none = new List<PositionResultData>();
            // 第一行 = 劇情場景資產檔大小；E<tab>類別<tab>名字 = 常駐物件；O<tab>完整類別<tab>名字<tab>JSON<tab>引用 = 要新建的
            string[][] lines;
            using (Stream st = typeof(Preload).Assembly.GetManifestResourceStream(Resource))
            using (StreamReader r = new StreamReader(st))
                lines = r.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToArray();
            FileInfo file = new FileInfo(Path.Combine(Application.dataPath, AssetsFile));
            if (!file.Exists || file.Length != long.Parse(lines[0][0]))
            {
                Log("[預載] 遊戲資料和插件內建的事件表對不上(遊戲更新了?),要重跑 known_unknown.py --position-tables;先等進劇情");
                return none;
            }
            string[][] exts = lines.Where(l => l[0] == "E").ToArray();
            string[][] objs = lines.Where(l => l[0] == "O").ToArray();

            Dictionary<string, Object> live = new Dictionary<string, Object>();
            foreach (ScriptableObject o in Resources.FindObjectsOfTypeAll<ScriptableObject>())
                live[o.GetType().Name + "/" + o.name] = o;
            Object[] ext = new Object[exts.Length];
            for (int i = 0; i < ext.Length; i++)
            {
                if (!live.TryGetValue(exts[i][1] + "/" + exts[i][2], out ext[i]))
                {
                    Log($"[預載] 找不到 {exts[i][1]} {exts[i][2]},先等進劇情");
                    return none;
                }
            }

            ScriptableObject[] made = objs.Select(o =>
            {
                ScriptableObject so = ScriptableObject.CreateInstance(AccessTools.TypeByName(o[1]));
                so.name = o[2];
                so.hideFlags = HideFlags.HideAndDontSave;   // 換場景的 UnloadUnusedAssets 不會收掉
                return so;
            }).ToArray();
            // 一般欄位交給 JsonUtility;引用(第 5 欄「欄位路徑=i第幾個新建的 / e第幾個常駐的」)它在執行期不解，自己設
            for (int i = 0; i < objs.Length; i++)
            {
                JsonUtility.FromJsonOverwrite(objs[i][3], made[i]);
                foreach (string r in objs[i][4].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] kv = r.Split('=');
                    int n = int.Parse(kv[1].Substring(1));
                    Set(made[i], kv[0], kv[1][0] == 'i' ? made[n] : ext[n]);
                }
            }
            Copies.UnionWith(made);
            List<PositionResultData> tables = made.OfType<PositionResultData>().ToList();
            // 引用沒接上的話，事件的結果代碼會是空的
            int events = tables.Sum(t => t.List.Count), linked = tables.Sum(t => t.List.Count(e => e != null && e.ResultType != null));
            if (linked != events)
            {
                Log($"[預載] 引用沒接上({linked}/{events}),先等進劇情");
                return none;
            }
            Log($"[預載] 重建 {tables.Count} 張地點事件表、{events} 個事件");
            return tables;
        }

        private static readonly Regex Segment = new Regex(@"^(\w+)(?:\[(\d+)\])?$");

        // 「_activeCondition._items[0]._value1」:沿路取欄位 / 清單元素，最後一段設值
        private static void Set(object root, string path, object value)
        {
            string[] parts = path.Split('.');
            object cur = root;
            for (int p = 0; p < parts.Length; p++)
            {
                Match m = Segment.Match(parts[p]);
                FieldInfo f = AccessTools.Field(cur.GetType(), m.Groups[1].Value);
                bool last = p == parts.Length - 1;
                if (!m.Groups[2].Success)
                {
                    if (last)
                        f.SetValue(cur, value);
                    else
                        cur = f.GetValue(cur);
                    continue;
                }
                IList list = (IList)f.GetValue(cur);
                int i = int.Parse(m.Groups[2].Value);
                if (last)
                    list[i] = value;
                else
                    cur = list[i];
            }
        }

        // 真表和複本的每個事件，現在的機率要一樣。rate 用插件自己的算法(別呼叫 GetRate:DiceMaster 掛在上面，會吃掉玩家的指定)
        public static int Mismatches(PositionResultData copy, PositionResultData real, Func<PositionEventData, int> rate)
        {
            List<PositionEventData> a = copy.List, b = real.List;
            if (a.Count != b.Count)
            {
                Log($"[預載] {real.name} 事件數不同：{a.Count} vs {b.Count}");
                return 1;
            }
            int bad = 0;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].name != b[i].name || a[i].ResultType != b[i].ResultType || rate(a[i]) != rate(b[i]))
                {
                    Log($"[預載] {real.name} {b[i].name}:複本 {a[i].ResultType} {rate(a[i])} vs 真表 {b[i].ResultType} {rate(b[i])}");
                    bad++;
                }
            }
            return bad;
        }
    }
}
