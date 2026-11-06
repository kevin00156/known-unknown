using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KnownUnknown
{
    // 地點事件表、Switch/Condition 表在劇情場景的資產裡，沒進過劇情就不在記憶體。照 known_unknown.py --position-tables 抽出的資料
    // 重建一份：同檔的物件(事件、結果代碼、數值變數)新建，引用到常駐資產的(屬性、旗標、書…)接回遊戲裡的活物件，
    // 所以算出來的機率就是當下的真值。真的表載入後以真表為準，順便逐一比對機率當驗證。
    internal static class Preload
    {
        private const string Resource = "KnownUnknown.PositionTables.txt";
        private const string AssetsFile = "sharedassets2.assets";   // 劇情場景的資產；大小對不上 = 遊戲更新了

        public static Action<string> Log = _ => { };

        private static readonly HashSet<Object> Copies = new HashSet<Object>();
        private static List<ScriptableObject> _made;

        // 遊戲更新後內建的表會過期：進劇情時把真表照同格式存一份，下次啟動內建的對不上就用它
        private static string CacheFile => Path.Combine(BepInEx.Paths.CachePath, "KnownUnknown", "PositionTables.txt");
        private static bool _stale;   // 這次的預載資料對不上遊戲(或沒建成)，看到真表就存快取
        private static bool _saved;
        // 標題畫面時已經載入的 = 常駐物件(屬性、旗標、書…)，快取裡用 類別/名字 接回；名字重複的接不回(null)
        private static Dictionary<Object, string> _resident;

        public static bool IsCopy(Object o) => Copies.Contains(o);

        // 把遊戲裡的真表收進 into(真表優先，蓋掉複本時逐一比對);還沒有真表的名字先放複本。
        // 不能看「一張都沒有」:Condition 表地圖場景也有十幾張(名字跟劇情場景的不重複)
        public static void Merge<T>(Dictionary<string, T> into, Func<T, string> key, Func<T, T, int> mismatches, string what) where T : Object
        {
            int compared = 0, bad = 0;
            foreach (T real in Resources.FindObjectsOfTypeAll<T>().Where(x => !IsCopy(x)))
            {
                string k = key(real);
                if (into.TryGetValue(k, out T old) && IsCopy(old))
                {
                    compared++;
                    bad += mismatches(old, real);
                }
                into[k] = real;
            }
            if (bad > 0)
                _stale = true;
            if (compared > 0)
                Log($"[預載] 真的{what}載入，比對 {compared} 張：{(bad == 0 ? "全部一致" : bad + " 處不一致")}");
            foreach (T c in Made().OfType<T>())
            {
                if (!into.ContainsKey(key(c)))
                    into[key(c)] = c;
            }
        }

        // 只建一次；建不出來(資料舊了、引用接不上)就回空的，照舊等進劇情
        private static List<ScriptableObject> Made()
        {
            if (_made != null)
                return _made;
            try
            {
                _made = Build();
            }
            catch (Exception e)
            {
                Log("[預載] 失敗，先等進劇情：" + e);
                _made = new List<ScriptableObject>();
                _stale = true;
            }
            return _made;
        }

        // 內建資料優先(插件發版時對過)，對不上遊戲才用快取；一份建不起來就換下一份
        private static List<ScriptableObject> Build()
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            FileInfo file = new FileInfo(Path.Combine(Application.dataPath, AssetsFile));
            foreach (KeyValuePair<string, Func<string[][]>> source in new[]
            {
                new KeyValuePair<string, Func<string[][]>>("內建資料", ReadEmbedded),
                new KeyValuePair<string, Func<string[][]>>("快取", ReadCache),
            })
            {
                string[][] lines = source.Value();
                if (lines == null || lines.Length == 0 || !file.Exists || !long.TryParse(lines[0][0], out long size) || size != file.Length)
                    continue;
                try
                {
                    List<ScriptableObject> made = Build(lines);
                    if (made.Count > 0)
                    {
                        Log($"[預載] 用{source.Key}，{clock.ElapsedMilliseconds} ms");
                        return made;
                    }
                }
                catch (Exception e)
                {
                    Log($"[預載] {source.Key}建不起來：{e}");
                }
            }
            Log("[預載] 遊戲資料和插件內建的事件表對不上(遊戲更新了?);先等進劇情，進過一次就會存好新的");
            _stale = true;
            return new List<ScriptableObject>();
        }

        // 第一行 = 劇情場景資產檔大小；E<tab>類別<tab>名字 = 常駐物件；O<tab>完整類別<tab>名字<tab>JSON<tab>引用 = 要新建的
        private static string[][] Parse(string text) =>
            text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToArray();

        private static string[][] ReadEmbedded()
        {
            using (Stream st = typeof(Preload).Assembly.GetManifestResourceStream(Resource))
            using (StreamReader r = new StreamReader(st))
                return Parse(r.ReadToEnd());
        }

        private static string[][] ReadCache()
        {
            try
            {
                return File.Exists(CacheFile) ? Parse(File.ReadAllText(CacheFile)) : null;
            }
            catch (Exception e)
            {
                Log("[預載] 快取讀不了：" + e.Message);
                return null;
            }
        }

        private static List<ScriptableObject> Build(string[][] lines)
        {
            List<ScriptableObject> none = new List<ScriptableObject>();
            string[][] exts = lines.Where(l => l[0] == "E").ToArray();
            string[][] objs = lines.Where(l => l[0] == "O").ToArray();
            if (exts.Any(l => l.Length != 3) || objs.Any(l => l.Length != 5))
            {
                Log("[預載] 資料格式不對(檔案不完整?)");
                return none;
            }

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

            // AccessTools.TypeByName 每次都掃全部組件的型別(一次幾毫秒),5000 多個物件會卡住幾十秒;只有幾種型別，查一次就好
            Dictionary<string, Type> types = new Dictionary<string, Type>();
            ScriptableObject[] made = objs.Select(o =>
            {
                if (!types.TryGetValue(o[1], out Type t))
                    types[o[1]] = t = AccessTools.TypeByName(o[1]);
                ScriptableObject so = ScriptableObject.CreateInstance(t);
                so.name = o[2];
                so.hideFlags = HideFlags.HideAndDontSave;   // 換場景的 UnloadUnusedAssets 不會收掉
                Copies.Add(so);   // 一建出來就標記：建到一半失敗的也不能被當成真表
                return so;
            }).ToArray();
            try
            {
                return Fill(objs, made, ext);
            }
            catch
            {
                Discard(made);
                throw;
            }
        }

        private static void Discard(ScriptableObject[] made)
        {
            foreach (ScriptableObject o in made)
                Object.DestroyImmediate(o);
        }

        private static List<ScriptableObject> Fill(string[][] objs, ScriptableObject[] made, Object[] ext)
        {
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
            List<PositionResultData> tables = made.OfType<PositionResultData>().ToList();
            // 引用沒接上的話，事件的結果代碼會是空的
            int events = tables.Sum(t => t.List.Count), linked = tables.Sum(t => t.List.Count(e => e != null && e.ResultType != null));
            if (linked != events)
            {
                Log($"[預載] 引用沒接上({linked}/{events}),先等進劇情");
                Discard(made);
                return new List<ScriptableObject>();
            }
            Log($"[預載] 重建 {tables.Count} 張地點事件表、{events} 個事件、{made.OfType<SwitchResultData>().Count()} 張 Switch 表、"
                + $"{made.OfType<ConditionResultData>().Count()} 張 Condition 表");
            return made.ToList();
        }

        private static readonly Regex Segment = new Regex(@"^(\w+)(?:\[(\d+)\])?$");

        private static readonly Dictionary<string, FieldInfo> Fields = new Dictionary<string, FieldInfo>();

        private static FieldInfo Field(Type t, string name)
        {
            string k = t.FullName + "." + name;
            if (!Fields.TryGetValue(k, out FieldInfo f))
                Fields[k] = f = AccessTools.Field(t, name);
            return f;
        }

        // 「_activeCondition._items[0]._value1」:沿路取欄位 / 清單元素，最後一段設值
        private static void Set(object root, string path, object value)
        {
            string[] parts = path.Split('.');
            object cur = root;
            for (int p = 0; p < parts.Length; p++)
            {
                Match m = Segment.Match(parts[p]);
                FieldInfo f = Field(cur.GetType(), m.Groups[1].Value);
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

        // ---- 快取：照 known_unknown.py --position-tables 的格式把真表寫出來 ----

        // 標題畫面呼叫一次：這時還沒有任何劇情場景的物件
        public static void Remember()
        {
            if (_resident != null)
                return;
            List<ScriptableObject> all = Resources.FindObjectsOfTypeAll<ScriptableObject>().ToList();
            Dictionary<string, int> count = all.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
            _resident = all.ToDictionary(o => (Object)o, o => count[Key(o)] == 1 ? Key(o) : null);
        }

        private static string Key(Object o) => o.GetType().Name + "\t" + o.name;

        // 換場景抓完真表後呼叫：這次的預載資料是舊的、而且真表在，就存一份
        public static void SaveIfStale()
        {
            if (!_stale || _saved)
                return;
            if (_resident == null)
            {
                Log("[預載] 沒經過標題畫面，分不出哪些是常駐物件，這次不存快取");
                _saved = true;
                return;
            }
            List<Object> roots = Resources.FindObjectsOfTypeAll<PositionResultData>().Cast<Object>()
                .Concat(Resources.FindObjectsOfTypeAll<SwitchResultData>())
                .Concat(Resources.FindObjectsOfTypeAll<ConditionResultData>())
                .Where(o => !IsCopy(o) && !_resident.ContainsKey(o)).ToList();
            if (!roots.OfType<PositionResultData>().Any())
                return;
            _saved = true;
            try
            {
                string text = Serialize(roots);
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile));
                File.WriteAllText(CacheFile + ".tmp", text);
                if (File.Exists(CacheFile))
                    File.Delete(CacheFile);
                File.Move(CacheFile + ".tmp", CacheFile);
                Log($"[預載] 遊戲的事件表存成快取，下次啟動就用它：{CacheFile}");
            }
            catch (Exception e)
            {
                Log("[預載] 存快取失敗：" + e);
            }
        }

        // JsonUtility 把引用寫成 {"m_FileID":…,"m_PathID":…}(執行期才有意義)；跟 Python 一樣換成空引用，另外記「欄位路徑=目標」
        private static readonly Regex Pointer = new Regex("\\{\"m_FileID\":-?\\d+,\"m_PathID\":-?\\d+\\}");

        private static string Serialize(List<Object> roots)
        {
            Dictionary<Object, int> ids = new Dictionary<Object, int>();
            List<Object> queue = new List<Object>();
            Dictionary<string, int> exts = new Dictionary<string, int>();
            List<string> lines = new List<string>();
            int Id(Object o)
            {
                if (!ids.TryGetValue(o, out int n))
                {
                    ids[o] = n = queue.Count;
                    queue.Add(o);
                }
                return n;
            }
            string Target(Object o)
            {
                if (IsCopy(o))
                    throw new Exception($"真表引用到預載的複本 {o.name}");
                if (!_resident.TryGetValue(o, out string key))
                    return "i" + Id(o);
                if (key == null)
                    throw new Exception($"常駐物件 {o.GetType().Name} {o.name} 名字不唯一，無法對應");
                if (!exts.TryGetValue(key, out int e))
                    exts[key] = e = exts.Count;
                return "e" + e;
            }
            roots.ForEach(o => Id(o));
            for (int i = 0; i < queue.Count; i++)
            {
                Object o = queue[i];
                if (!(o is ScriptableObject))
                    throw new Exception($"引用到不支援的物件 {o.GetType().Name} {o.name}");
                string json = Pointer.Replace(JsonUtility.ToJson(o), "{\"instanceID\":0}");
                List<string> refs = new List<string>();
                Refs(o, "", 0, (path, t) => refs.Add(path + "=" + Target(t)));
                if (json.Contains('\t') || json.Contains('\n') || o.name.Contains('\t') || o.name.Contains('\n'))
                    throw new Exception($"{o.name} 含 tab/換行");
                lines.Add($"O\t{o.GetType().FullName}\t{o.name}\t{json}\t{string.Join("|", refs)}");
            }
            long size = new FileInfo(Path.Combine(Application.dataPath, AssetsFile)).Length;
            return size + "\n" + string.Concat(exts.OrderBy(x => x.Value).Select(x => $"E\t{x.Key}\n")) + string.Join("\n", lines) + "\n";
        }

        // 照 Unity 序列化的規則走欄位，找出所有指到 UnityEngine.Object 的(路徑格式同 Set)
        private static void Refs(object obj, string path, int depth, Action<string, Object> found)
        {
            if (depth > 10)
                return;
            for (Type t = obj.GetType(); t != null && t != typeof(ScriptableObject) && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!Serialized(f))
                        continue;
                    string p = path.Length == 0 ? f.Name : path + "." + f.Name;
                    object v = f.GetValue(obj);
                    if (v is IList list && !(v is string))
                    {
                        for (int i = 0; i < list.Count; i++)
                            Value(list[i], $"{p}[{i}]", depth, found);
                    }
                    else
                    {
                        Value(v, p, depth, found);
                    }
                }
            }
        }

        private static void Value(object v, string path, int depth, Action<string, Object> found)
        {
            if (v is Object o)
            {
                if (o != null)
                    found(path, o);
            }
            else if (v != null && !(v is string) && !v.GetType().IsPrimitive && !v.GetType().IsEnum && v.GetType().IsSerializable)
            {
                Refs(v, path, depth + 1, found);
            }
        }

        private static bool Serialized(FieldInfo f) =>
            !f.IsInitOnly && !f.IsLiteral && !f.IsNotSerialized
            && (f.IsPublic || f.IsDefined(typeof(SerializeField), true));

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
