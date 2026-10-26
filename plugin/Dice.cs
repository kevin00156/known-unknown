using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Mortal.Story;
using UnityEngine;

namespace KnownUnknown
{
    // 擲骰(天命):這次擲骰每個結果有幾面骰得到，可以指定擲出哪個結果。
    // 指定 = 從骰得到那個結果的面裡挑一面當成這次的亂數，轉盤、加成、結果都照遊戲自己算，不是硬改結果。
    internal static class Dice
    {
        public static bool Enabled = true;
        public static Action<string> Log = _ => { };

        public static string[] Options;   // Lua 傳進 ExecuteRoll 的選項原文(「O_xxx|>=60」)
        public static int Target;          // 指定的結果(1 起算);0 = 不指定
        private static DiceMenuDialog _dialog;
        private static string _check;     // DiceResultData 名

        private static readonly AccessTools.FieldRef<DiceMenuDialog, int> DiceMin = AccessTools.FieldRefAccess<DiceMenuDialog, int>("_diceMin");
        private static readonly AccessTools.FieldRef<DiceMenuDialog, int> DiceMax = AccessTools.FieldRefAccess<DiceMenuDialog, int>("_diceMax");
        private static readonly AccessTools.FieldRef<DiceMenuDialog, int> Shown = AccessTools.FieldRefAccess<DiceMenuDialog, int>("_currentRandomValue");
        private static readonly AccessTools.FieldRef<DiceMenuDialog, DiceCheckAddition[]> Additions =
            AccessTools.FieldRefAccess<DiceMenuDialog, DiceCheckAddition[]>("_additions");
        private static readonly AccessTools.FieldRef<DiceResultData, List<DiceResultItem>> Items =
            AccessTools.FieldRefAccess<DiceResultData, List<DiceResultItem>>("_resultItems");

        public static bool Active => _dialog != null && _dialog.isActiveAndEnabled && Options != null;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(DiceMenuDialog), nameof(DiceMenuDialog.ExecuteRoll)),
                prefix: new HarmonyMethod(typeof(Dice), nameof(BeforeExecuteRoll)));
            harmony.Patch(AccessTools.Method(typeof(CheckPointManager), nameof(CheckPointManager.Dice)),
                prefix: new HarmonyMethod(typeof(Dice), nameof(BeforeDice)));
        }

        // Lua:Dice() → Setup(加成) → ExecuteRoll(選項)。到這裡加成已經在對話框上
        private static void BeforeExecuteRoll(DiceMenuDialog __instance, string[] options, string checkName)
        {
            _dialog = __instance;
            _check = checkName;
            Options = options;
            Target = 0;
        }

        // 骰面範圍內，每個結果有幾面(索引 1 起算)
        public static int[] Faces()
        {
            int[] faces = new int[Options.Length + 2];
            DiceResultData d = Data();
            if (d == null)
                return faces;
            int add = Additions(_dialog)?.Sum(a => a.Value) ?? 0;
            for (int r = DiceMin(_dialog); r <= DiceMax(_dialog); r++)
            {
                int k = ResultOf(d, r + add);
                if (k < faces.Length)
                    faces[k]++;
            }
            return faces;
        }

        // 同 DiceResultData.Execute 的判定，但不碰 _count
        private static int ResultOf(DiceResultData d, int total)
        {
            List<DiceResultItem> items = Items(d);
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Check(total))
                    return i + 1;
            }
            return items.Count + 1;
        }

        private static DiceResultData Data() =>
            Resources.FindObjectsOfTypeAll<DiceResultData>().FirstOrDefault(x => x.name == _check);

        // 再按一次同一個 = 取消
        public static void Toggle(int k) => Target = Target == k ? 0 : k;

        // 對話框擲骰時先設好 _currentRandomValue 再呼叫這裡；換成骰得到指定結果的一面，轉盤顯示的數字跟著換
        private static void BeforeDice(string name, ref int random)
        {
            if (!Enabled || Target == 0 || !Active || name != _check)
                return;
            DiceResultData d = Data();
            if (d == null)
                return;
            int add = Additions(_dialog)?.Sum(a => a.Value) ?? 0;
            List<int> hits = new List<int>();
            for (int r = DiceMin(_dialog); r <= DiceMax(_dialog); r++)
            {
                if (ResultOf(d, r + add) == Target)
                    hits.Add(r);
            }
            if (hits.Count == 0 || hits.Contains(random))
                return;
            int pick = hits[UnityEngine.Random.Range(0, hits.Count)];
            Log($"[指定] 擲骰 {name}:{random} → {pick}(結果 {Target})");
            random = pick;
            Shown(_dialog) = pick;
        }
    }
}
