using System;
using System.Collections.Generic;
using HarmonyLib;
using Mortal.Story;

namespace KnownUnknown
{
    // 指定下次抽到的地點事件結果。遊戲照常抽，只把被指定的那一張事件表的結果換掉，換完就清。
    // 不依賴 DiceMaster:它的指定掛在 GetRate 上，誰呼叫 GetRate 都會吃掉它。
    internal static class Force
    {
        public static bool Enabled = true;
        public static Action<string> Log = _ => { };

        private static readonly Dictionary<PositionResultData, Outcome> Targets = new Dictionary<PositionResultData, Outcome>();

        public static IEnumerable<Outcome> All => Targets.Values;

        public static bool Is(Outcome o) => o.Table != null && Targets.TryGetValue(o.Table, out Outcome t) && t.Type == o.Type;

        // 同一張表只能指定一個結果；再指定一次同一個 = 取消
        public static void Toggle(Outcome o)
        {
            if (o.Table == null)
                return;
            if (Is(o))
                Targets.Remove(o.Table);
            else
                Targets[o.Table] = o;
        }

        public static void Patch(Harmony harmony) =>
            harmony.Patch(AccessTools.Method(typeof(PositionResultData), nameof(PositionResultData.GetResult)),
                postfix: new HarmonyMethod(typeof(Force), nameof(AfterGetResult)));

        // GetResult() 回傳「前綴_結果代碼」= 要跑的劇本名
        private static void AfterGetResult(PositionResultData __instance, ref string __result)
        {
            if (!Enabled || !Targets.TryGetValue(__instance, out Outcome o))
                return;
            Targets.Remove(__instance);
            string forced = Traverse.Create(__instance).Field("_scriptPrefix").GetValue<string>() + "_" + o.Type;
            Log($"[指定] {__instance.name}:{__result} → {forced}");
            __result = forced;
        }
    }
}
