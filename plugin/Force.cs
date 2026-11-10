using System;
using System.Collections.Generic;
using HarmonyLib;
using Mortal.Core;
using Mortal.Story;

namespace KnownUnknown
{
    // 指定下次抽到的地點事件結果。遊戲照常抽，只把被指定的那一張事件表的結果換掉，換完就清。
    // 指定不會一直留著：讀了別的存檔就全部取消；去到那裡時那個結果已經抽不到(條件變了、過了時間)就不套用。
    // 不依賴 DiceMaster:它的指定掛在 GetRate 上，誰呼叫 GetRate 都會吃掉它。
    internal static class Force
    {
        public static bool Enabled = true;
        public static Action<string> Log = _ => { };
        // 這張事件表現在抽不抽得到這個結果代碼(機率 > 0)。由 Plugin 接到 Hints;還沒準備好就當抽得到
        public static Func<PositionResultData, string, bool> CanRoll = (_, __) => true;

        // 用事件表名當鍵：每進一次劇情場景，事件表會重新載入成新的物件
        private static readonly Dictionary<string, Outcome> Targets = new Dictionary<string, Outcome>();

        public static IEnumerable<Outcome> All => Targets.Values;

        public static bool Is(Outcome o) => o.Table != null && Targets.TryGetValue(o.Table.name, out Outcome t) && t.Type == o.Type;

        // 同一張表只能指定一個結果；再指定一次同一個 = 取消
        public static void Toggle(Outcome o)
        {
            if (o.Table == null)
                return;
            if (Is(o))
                Targets.Remove(o.Table.name);
            else
                Targets[o.Table.name] = o;
        }

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(PositionResultData), nameof(PositionResultData.GetResult)),
                postfix: new HarmonyMethod(typeof(Force), nameof(AfterGetResult)));
            // 讀檔(手動、自動存檔)和開新遊戲：指定是替上一個進度勾的
            foreach (string load in new[] { nameof(SaveSystem.LoadGameData), nameof(SaveSystem.AutoLoadGameData), nameof(SaveSystem.NewGameData) })
                harmony.Patch(AccessTools.Method(typeof(SaveSystem), load), postfix: new HarmonyMethod(typeof(Force), nameof(AfterLoad)));
        }

        private static void AfterLoad()
        {
            if (Targets.Count == 0)
                return;
            Log($"[指定] 讀了存檔，取消 {Targets.Count} 個指定");
            Targets.Clear();
        }

        // GetResult() 回傳「前綴_結果代碼」= 要跑的劇本名
        private static void AfterGetResult(PositionResultData __instance, ref string __result)
        {
            if (!Enabled || !Targets.TryGetValue(__instance.name, out Outcome o))
                return;
            Targets.Remove(__instance.name);
            // 例外不能丟回遊戲：照遊戲抽的結果走
            try
            {
                if (!CanRoll(__instance, o.Type))
                {
                    Log($"[指定] {__instance.name}:指定的「{o.Type}」現在抽不到了，照遊戲抽的 {__result}");
                    return;
                }
                string prefix = Traverse.Create(__instance).Field("_scriptPrefix").GetValue<string>();
                if (prefix == null)
                    return;   // 讀不到前綴：不要給遊戲一個不存在的劇本名
                string forced = prefix + "_" + o.Type;
                Log($"[指定] {__instance.name}:{__result} → {forced}");
                __result = forced;
            }
            catch (Exception e)
            {
                Log($"[指定] {__instance.name} 沒套用：{e}");
            }
        }
    }
}
