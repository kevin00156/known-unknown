using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Preloader.Core.Patching;

namespace KnownUnknown.Updater
{
    // 插件載入前，把插件下載好的新版換進 plugins/KnownUnknown/。執行中的 DLL 換不了自己，所以拆成這一小段。
    // 插件把驗證過的檔案放進 cache/KnownUnknown/update/ready/(整個資料夾改名 = 放好了)，這裡只負責搬。
    // 任何一步失敗都還原成原本的檔案，ready/ 留著下次再試。
    [PatcherPluginInfo("known-unknown.updater", "多玩10塊錢 更新", "1.0.0")]
    public class Updater : BasePatcher
    {
        public override void Initialize()
        {
            // 讓插件知道有裝這段(沒裝的話插件只提示有新版，不下載)
            AppDomain.CurrentDomain.SetData("KnownUnknown.Updater", Info.Version.ToString());
            string ready = Path.Combine(Paths.CachePath, "KnownUnknown", "update", "ready");
            string dir = Path.Combine(Paths.PluginPath, "KnownUnknown");
            try
            {
                Recover(dir);
                if (Directory.Exists(ready))
                    Apply(ready, dir);
            }
            catch (Exception e)
            {
                Log.LogError("更新失敗，沿用現有版本：" + e);
            }
        }

        private void Apply(string ready, string dir)
        {
            string[] files = Directory.GetFiles(ready);
            if (!files.Any(f => Path.GetFileName(f) == "KnownUnknown.dll"))
            {
                Log.LogWarning("下載的更新不完整，丟掉");
                Directory.Delete(ready, true);
                return;
            }
            // 下載之後玩家又手動裝了更新的版本：別換回舊的
            string current = Path.Combine(dir, "KnownUnknown.dll");
            if (File.Exists(current) && Version(Path.Combine(ready, "KnownUnknown.dll")) <= Version(current))
            {
                Log.LogMessage("已經裝了一樣或更新的版本，丟掉下載的");
                Directory.Delete(ready, true);
                return;
            }
            Directory.CreateDirectory(dir);
            string[] targets = files.Select(f => Path.Combine(dir, Path.GetFileName(f))).ToArray();
            try
            {
                // 先全部複製成 .new,都成功了才換：換到一半失敗不會只有一半新
                for (int i = 0; i < files.Length; i++)
                    File.Copy(files[i], targets[i] + ".new", true);
                foreach (string t in targets)
                {
                    if (File.Exists(t))
                        File.Move(t, t + ".old");
                    File.Move(t + ".new", t);
                }
            }
            catch
            {
                Recover(dir);
                throw;
            }
            foreach (string t in targets.Where(t => File.Exists(t + ".old")))
                File.Delete(t + ".old");
            Directory.Delete(ready, true);
            Log.LogMessage("已換上新版");
            AppDomain.CurrentDomain.SetData("KnownUnknown.Updated", true);
        }

        private static Version Version(string dll) => AssemblyName.GetAssemblyName(dll).Version;

        // 上次換到一半(當機、斷電)：有 .old 就是還沒換完，換回舊的；.new 一律丟掉
        private static void Recover(string dir)
        {
            if (!Directory.Exists(dir))
                return;
            foreach (string old in Directory.GetFiles(dir, "*.old"))
            {
                string t = old.Substring(0, old.Length - 4);
                if (File.Exists(t))
                    File.Delete(t);
                File.Move(old, t);
            }
            foreach (string n in Directory.GetFiles(dir, "*.new"))
                File.Delete(n);
        }
    }
}
