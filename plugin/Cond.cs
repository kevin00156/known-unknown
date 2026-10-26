using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;

namespace KnownUnknown
{
    // 遊戲條件(Switch、選項的 Condition、任務條件)的只讀求值與白話說明。
    // 遊戲自己的 Execute() 會讓計數 +1;Check() 大多是純函式，但取值類型「條件設定」會呼叫 Execute(),
    // 「隨機值」每次不同，所以整棵樹自己走：ConditionResultItem → StatCompareItem → StatGroupVariable → StatValueReference。
    internal class Cond
    {
        private static readonly AccessTools.FieldRef<SwitchResultData, bool> UseCount =
            AccessTools.FieldRefAccess<SwitchResultData, bool>("_useCount");
        private static readonly AccessTools.FieldRef<SwitchResultData, int> Count =
            AccessTools.FieldRefAccess<SwitchResultData, int>("_count");
        private static readonly AccessTools.FieldRef<SwitchResultData, List<ConditionResultItem>> SwitchItems =
            AccessTools.FieldRefAccess<SwitchResultData, List<ConditionResultItem>>("_items");
        private static readonly AccessTools.FieldRef<SwitchResultData, List<ValueCompareItem>> CountItems =
            AccessTools.FieldRefAccess<SwitchResultData, List<ValueCompareItem>>("_countItems");
        private static readonly AccessTools.FieldRef<ConditionResultData, List<ConditionResultItem>> ConditionItems =
            AccessTools.FieldRefAccess<ConditionResultData, List<ConditionResultItem>>("_items");
        private static readonly AccessTools.FieldRef<ConditionResultItem, LogicOperatorType> LogicOp =
            AccessTools.FieldRefAccess<ConditionResultItem, LogicOperatorType>("_logicOp");
        private static readonly AccessTools.FieldRef<ConditionResultItem, List<StatCompareItem>> Compares =
            AccessTools.FieldRefAccess<ConditionResultItem, List<StatCompareItem>>("_items");
        private static readonly AccessTools.FieldRef<StatCompareItem, StatCompareType> CompareOp =
            AccessTools.FieldRefAccess<StatCompareItem, StatCompareType>("_compareOp");
        private static readonly AccessTools.FieldRef<StatCompareItem, StatGroupVariable> Value1 =
            AccessTools.FieldRefAccess<StatCompareItem, StatGroupVariable>("_value1");
        private static readonly AccessTools.FieldRef<StatCompareItem, StatGroupVariable> Value2 =
            AccessTools.FieldRefAccess<StatCompareItem, StatGroupVariable>("_value2");
        private static readonly AccessTools.FieldRef<StatGroupVariable, StatGroupType> GroupType =
            AccessTools.FieldRefAccess<StatGroupVariable, StatGroupType>("_groupType");
        private static readonly AccessTools.FieldRef<StatGroupVariable, List<StatValueReference>> Refs =
            AccessTools.FieldRefAccess<StatGroupVariable, List<StatValueReference>>("_value");
        private static readonly AccessTools.FieldRef<StatValueReference, FlagData> FlagOf =
            AccessTools.FieldRefAccess<StatValueReference, FlagData>("_flag");
        private static readonly AccessTools.FieldRef<StatValueReference, ConditionResultData> ConditionOf =
            AccessTools.FieldRefAccess<StatValueReference, ConditionResultData>("_condition");
        private static readonly AccessTools.FieldRef<StatValueReference, StatGroupVariable> GroupOf =
            AccessTools.FieldRefAccess<StatValueReference, StatGroupVariable>("_statGroup");
        private static readonly AccessTools.FieldRef<StatValueReference, bool> Negative =
            AccessTools.FieldRefAccess<StatValueReference, bool>("_negative");
        private static readonly AccessTools.FieldRef<ValueCompareItem, StatCompareType> CountOp =
            AccessTools.FieldRefAccess<ValueCompareItem, StatCompareType>("_compareOp");
        private static readonly AccessTools.FieldRef<ValueCompareItem, int> CountValue =
            AccessTools.FieldRefAccess<ValueCompareItem, int>("_value");

        private static readonly Dictionary<StatCompareType, string> Ops = new Dictionary<StatCompareType, string>
        {
            { StatCompareType.Equal, "=" },
            { StatCompareType.NotEqual, "≠" },
            { StatCompareType.Less, "<" },
            { StatCompareType.LessEqual, "≤" },
            { StatCompareType.Greater, ">" },
            { StatCompareType.GreaterEqual, "≥" },
        };

        // 變數名 → 畫面用字：「好感度_小師妹」→「小師妹好感」
        private static readonly KeyValuePair<string, string>[] Prefixes =
        {
            new KeyValuePair<string, string>("數值_", "{0}"),
            new KeyValuePair<string, string>("好感度_", "{0}好感"),
            new KeyValuePair<string, string>("書籍_", "書籍「{0}」"),
            new KeyValuePair<string, string>("雜物_", "持有「{0}」"),
            new KeyValuePair<string, string>("貴重品_", "持有「{0}」"),
            new KeyValuePair<string, string>("技能_", "技能「{0}」"),
            new KeyValuePair<string, string>("秘笈等級_", "「{0}」等級"),
            new KeyValuePair<string, string>("已開發項目_", "已開發「{0}」"),
            new KeyValuePair<string, string>("開發項目等級_", "「{0}」開發等級"),
            new KeyValuePair<string, string>("成就_", "成就 {0}"),
            new KeyValuePair<string, string>("條件_", "{0}"),
        };

        private readonly Dictionary<string, SwitchResultData> _switches = new Dictionary<string, SwitchResultData>();
        private readonly Dictionary<string, ConditionResultData> _conditions = new Dictionary<string, ConditionResultData>();
        private readonly Dictionary<string, int> _switchMemo = new Dictionary<string, int>();
        private readonly Dictionary<string, bool?> _conditionMemo = new Dictionary<string, bool?>();

        // 假設某些旗標已經是某個值(「去拿這個旗標之後會怎樣」)。null = 照遊戲現況
        private Dictionary<FlagData, int> _assume;

        // 劇情場景裡才載入，抓到就留著引用
        // 還沒進過劇情時用預載的複本(Preload)
        public void Capture()
        {
            Forget();
            Preload.Merge(_conditions, c => c.name, (a, b) => Same(Evaluate(a), Evaluate(b), b.name), "Condition 表");
            Preload.Merge(_switches, s => s.name, (a, b) => Same(Evaluate(a), Evaluate(b), b.name), "Switch 表");
            Forget();
        }

        private static int Same<T>(T a, T b, string name)
        {
            if (Equals(a, b))
                return 0;
            Preload.Log($"[預載] {name}:複本 {a} vs 真表 {b}");
            return 1;
        }

        // 遊戲狀態可能變了，清掉這次查詢的快取
        public void Forget()
        {
            _switchMemo.Clear();
            _conditionMemo.Clear();
        }

        public void Assume(Dictionary<FlagData, int> flags)
        {
            _assume = flags;
            Forget();
        }

        // Switch 現在會回傳第幾個分支；-1 = 找不到(當成都可能)
        public int Switch(string name)
        {
            if (_switchMemo.TryGetValue(name, out int v))
                return v;
            v = _switches.TryGetValue(name, out SwitchResultData s) ? Evaluate(s) : -1;
            return _switchMemo[name] = v;
        }

        private int Evaluate(SwitchResultData s)
        {
            if (UseCount(s))
            {
                List<ValueCompareItem> counts = CountItems(s);
                int n = Count(s) + 1;
                int i = counts.FindIndex(c => GameStatUtils.StatCompare(n, CountValue(c), CountOp(c)));
                return i >= 0 ? i + 1 : counts.Count + 1;
            }
            List<ConditionResultItem> items = SwitchItems(s);
            int k = items.FindIndex(Eval);
            return k >= 0 ? k + 1 : items.Count + 1;
        }

        private bool Evaluate(ConditionResultData c) => ConditionItems(c).Any(Eval);

        // Condition 現在成不成立；null = 找不到
        public bool? Condition(string name)
        {
            if (_conditionMemo.TryGetValue(name, out bool? v))
                return v;
            v = _conditions.TryGetValue(name, out ConditionResultData c) ? Evaluate(c) : (bool?)null;
            return _conditionMemo[name] = v;
        }

        // 這個分支條件現在成立嗎；null = 不知道(找不到資料、隨機)
        public bool? Holds(Guard g)
        {
            if (g.Kind == GuardKind.Switch)
            {
                int v = Switch(g.Name);
                return v < 0 ? (bool?)null : v == g.Value;
            }
            if (g.Kind == GuardKind.Choice && g.Gate != null)
                return Condition(g.Gate);
            return null;
        }

        public bool Eval(ConditionResultItem c)
        {
            List<StatCompareItem> xs = Compares(c);
            if (xs.Count == 0)
                return false;
            bool and = LogicOp(c) == LogicOperatorType.AND;
            bool r = Compare(xs[0]);
            for (int i = 1; i < xs.Count; i++)
                r = and ? r && Compare(xs[i]) : r || Compare(xs[i]);
            return r;
        }

        // 用到隨機值的比較一律當成可能成立
        private bool Compare(StatCompareItem x)
        {
            int? a = Value(Value1(x));
            int? b = Value(Value2(x));
            return a == null || b == null || GameStatUtils.StatCompare(a.Value, b.Value, CompareOp(x));
        }

        private int? Value(StatGroupVariable v)
        {
            if (v == null)
                return 0;
            List<int> xs = new List<int>();
            foreach (StatValueReference r in Refs(v))
            {
                int? x = Value(r);
                if (x == null)
                    return null;
                xs.Add(x.Value);
            }
            if (xs.Count == 0)
                return 0;
            switch (GroupType(v))
            {
                case StatGroupType.Max: return xs.Max();
                case StatGroupType.Min: return xs.Min();
                case StatGroupType.Multiply: return xs.Aggregate(1, (a, b) => a * b);
                default: return xs.Sum();
            }
        }

        private int? Value(StatValueReference r)
        {
            int sign = Negative(r) ? -1 : 1;
            switch (r.CheckType)
            {
                case StatCheckType.隨機值:
                    return null;
                case StatCheckType.條件設定:
                    ConditionResultData c = ConditionOf(r);
                    return c != null && ConditionItems(c).Any(Eval) ? 1 : 0;
                case StatCheckType.數值群組:
                    return Value(GroupOf(r));
                case StatCheckType.旗標:
                    FlagData f = FlagOf(r);
                    if (f == null)
                        return 0;
                    return sign * (_assume != null && _assume.TryGetValue(f, out int assumed) ? assumed : f.State);
                default:
                    return r.GetValue();
            }
        }

        // 一個分支條件牽涉到的旗標
        public IEnumerable<FlagData> Flags(Guard g) => Items(g).SelectMany(Flags).Distinct();

        private IEnumerable<ConditionResultItem> Items(Guard g)
        {
            if (g.Kind == GuardKind.Switch && _switches.TryGetValue(g.Name, out SwitchResultData s) && !UseCount(s))
                return SwitchItems(s);
            if (g.Kind == GuardKind.Choice && g.Gate != null && _conditions.TryGetValue(g.Gate, out ConditionResultData c))
                return ConditionItems(c);
            return Enumerable.Empty<ConditionResultItem>();
        }

        public IEnumerable<FlagData> Flags(ConditionResultItem c) =>
            Compares(c).SelectMany(x => Flags(Value1(x)).Concat(Flags(Value2(x))));

        private IEnumerable<FlagData> Flags(StatGroupVariable v)
        {
            if (v == null)
                yield break;
            foreach (StatValueReference r in Refs(v))
            {
                if (r.CheckType == StatCheckType.旗標 && FlagOf(r) != null)
                    yield return FlagOf(r);
                else if (r.CheckType == StatCheckType.數值群組)
                {
                    foreach (FlagData f in Flags(GroupOf(r)))
                        yield return f;
                }
                else if (r.CheckType == StatCheckType.條件設定 && ConditionOf(r) != null)
                {
                    foreach (FlagData f in ConditionItems(ConditionOf(r)).SelectMany(Flags))
                        yield return f;
                }
            }
        }

        // 現版本不可能成立：要成立的條件裡，有比較只牽涉到拿不到的旗標(永遠停在現在的值)而且現在不成立
        public bool Impossible(Guard g, System.Func<FlagData, bool> obtainable)
        {
            List<ConditionResultItem> need = Needed(g);
            return need.Count > 0 && need.All(c => Impossible(c, obtainable));
        }

        private bool Impossible(ConditionResultItem c, System.Func<FlagData, bool> obtainable)
        {
            bool Dead(StatCompareItem x)
            {
                List<StatValueReference> refs = Refs(Value1(x)).Concat(Value2(x) == null ? new List<StatValueReference>() : Refs(Value2(x))).ToList();
                return refs.Any(r => r.CheckType == StatCheckType.旗標)
                    && refs.All(r => r.CheckType == StatCheckType.常數 || r.CheckType == StatCheckType.旗標 && !obtainable(FlagOf(r)))
                    && !Compare(x);
            }
            List<StatCompareItem> xs = Compares(c);
            return LogicOp(c) == LogicOperatorType.AND ? xs.Any(Dead) : xs.All(Dead);
        }

        // 分支成立要哪些條件之一成立(Switch 第 N 個分支 = 第 N 個條件；選項 = 任一條件)。最後的「其他」分支不算
        private List<ConditionResultItem> Needed(Guard g)
        {
            if (g.Kind == GuardKind.Choice && g.Gate != null && _conditions.TryGetValue(g.Gate, out ConditionResultData c))
                return ConditionItems(c);
            if (g.Kind == GuardKind.Switch && _switches.TryGetValue(g.Name, out SwitchResultData s) && !UseCount(s)
                && g.Value >= 1 && g.Value <= SwitchItems(s).Count)
                return new List<ConditionResultItem> { SwitchItems(s)[g.Value - 1] };
            return new List<ConditionResultItem>();
        }

        // 這個分支要成立需要什麼(白話)。flagSource:旗標 → 取得處的說明
        public string Need(Guard g, System.Func<FlagData, string> flagSource)
        {
            if (g.Kind == GuardKind.Choice && g.Gate != null && _conditions.TryGetValue(g.Gate, out ConditionResultData c))
                return "選項出現要：" + string.Join(" 或 ", ConditionItems(c).Select(i => Describe(i, flagSource)));
            if (g.Kind != GuardKind.Switch || !_switches.TryGetValue(g.Name, out SwitchResultData s))
                return null;
            if (UseCount(s))
            {
                List<ValueCompareItem> counts = CountItems(s);
                return g.Value <= counts.Count
                    ? $"第 {Ops[CountOp(counts[g.Value - 1])]} {CountValue(counts[g.Value - 1])} 次來(目前第 {Count(s) + 1} 次)"
                    : "次數條件";
            }
            List<ConditionResultItem> items = SwitchItems(s);
            if (g.Value >= 1 && g.Value <= items.Count)
                return Describe(items[g.Value - 1], flagSource);
            // 最後一個分支：前面全部不成立
            return "以下都不成立：" + string.Join("；", items.Select(i => Describe(i, flagSource)));
        }

        public string Describe(ConditionResultItem c, System.Func<FlagData, string> flagSource)
        {
            // 「且」只列還沒成立的；「或」全部列出(任一個成立就好)
            List<StatCompareItem> xs = Compares(c);
            bool and = LogicOp(c) == LogicOperatorType.AND;
            if (and && xs.Any(x => !Compare(x)))
                xs = xs.Where(x => !Compare(x)).ToList();
            return string.Join(and ? " 且 " : " 或 ", xs.Select(x => Describe(x, flagSource)));
        }

        private string Describe(StatCompareItem x, System.Func<FlagData, string> flagSource)
        {
            StatGroupVariable a = Value1(x), b = Value2(x);
            // 現在時間 vs 劇情用 SetMissionTime 記下的時間點：日期由劇情決定，寫日期反而誤導
            if (IsTime(a) && IsTime(b))
                return CompareOp(x) == StatCompareType.Equal ? "到了劇情安排的那一旬" : $"時間 {Ops[CompareOp(x)]} 劇情安排的時間";
            string now = Constant(a) ? "" : $"(目前 {Show(a, Value(a))})";
            string text = $"{Name(a)} {Ops[CompareOp(x)]} {Show(a, Value(b))}{now}";
            FlagData f = a == null ? null : Flags(a).FirstOrDefault();
            string src = f == null ? null : flagSource(f);
            return src == null ? text : $"{text}[{src}]";
        }

        private static bool IsTime(StatGroupVariable v) =>
            v != null && Refs(v).Count > 0 && Refs(v).All(r => r.CheckType == StatCheckType.遊戲時間);

        private static bool Constant(StatGroupVariable v) =>
            v == null || Refs(v).All(r => r.CheckType == StatCheckType.常數);

        // 值是代號的變數 → 遊戲在地化表的前綴(心上人 8 =「PlayerInfo/Lover/8」= 龍湘)
        private static readonly Dictionary<string, string> Named = new Dictionary<string, string>
        {
            { "數值_心上人", "PlayerInfo/Lover/" },
        };

        // 數字的顯示：時間變數換成「第2年1月下旬」，代號換成名字
        private static string Show(StatGroupVariable v, int? n)
        {
            if (n == null)
                return "隨機";
            if (v != null && Named.TryGetValue(v.name, out string key))
                return Lean.Localization.LeanLocalization.GetTranslationText(key + n.Value, n.Value.ToString());
            if (v != null && Refs(v).Any(r => r.CheckType == StatCheckType.遊戲時間))
                return Date(n.Value);
            return n.Value.ToString();
        }

        // GameTime.ConvertToRounds 的反函數(每月 3 旬，上旬 = 1)
        public static string Date(int rounds)
        {
            int r = rounds - 1;
            int y = r / 36 + 1, m = r % 36 / 3 + 1, s = r % 3;
            return $"第{y}年{m}月{"上中下"[s]}旬";
        }

        private static string Name(StatGroupVariable v)
        {
            if (v == null)
                return "?";
            List<StatValueReference> refs = Refs(v);
            if (refs.Count == 1 && refs[0].CheckType == StatCheckType.旗標 && FlagOf(refs[0]) != null)
                return FlagName(FlagOf(refs[0]));
            if (refs.Count == 1 && refs[0].CheckType == StatCheckType.遊戲時間)
                return "時間";
            foreach (KeyValuePair<string, string> p in Prefixes)
            {
                if (v.name.StartsWith(p.Key))
                    return string.Format(p.Value, v.name.Substring(p.Key.Length));
            }
            return v.name;
        }

        // 備註有的是多行的開發筆記，只取第一行。約 1/6 的旗標遊戲沒寫備註，代號(B00001)對玩家沒意義，後面會接取得處
        public static string FlagName(FlagData f)
        {
            string note = (f.DevNote ?? "").Split('\n')[0].Trim();
            return note.Length == 0 ? "未命名旗標" : $"旗標「{note}」";
        }
    }
}
