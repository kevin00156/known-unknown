using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Threading;

namespace KnownUnknown
{
    // GitHub releases/latest 回應裡用到的欄位。JsonUtility 在插件組件裡填不進自訂類別的陣列，改用 DataContract
#pragma warning disable 0649
    [DataContract]
    internal class ReleaseInfo
    {
        [DataMember] public string tag_name;
        [DataMember] public ReleaseAsset[] assets;
    }

    [DataContract]
    internal class ReleaseAsset
    {
        [DataMember] public string name;
        [DataMember] public string browser_download_url;
        [DataMember] public string digest;   // "sha256:…";舊的 release 可能沒有
    }
#pragma warning restore 0649

    // 自動更新：啟動時在背景查一次 GitHub 最新 release,比自己新就下載、驗證，放進 cache/KnownUnknown/update/ready/,
    // 下次啟動由 patchers/KnownUnknown 的 KnownUnknown.Updater 在插件載入前換上。
    // 任何一步失敗都只寫日誌，沿用現有版本；ready/ 只有整包驗證完才會出現(資料夾改名)。
    // 用 HttpWebRequest 不用 UnityWebRequest:後者在 Wine 下從 release-assets.githubusercontent.com 下載會收完資料卻一直不結束
    internal static class SelfUpdate
    {
        public static Action<string> Log = _ => { };
        public static volatile string Status;   // F2 視窗顯示的一行；null = 沒事

        private static string Root => Path.Combine(BepInEx.Paths.CachePath, "KnownUnknown", "update");
        private static string Ready => Path.Combine(Root, "ready");
        private static Version Current => Normalize(typeof(SelfUpdate).Assembly.GetName().Version);
        private static readonly string PatcherVersion = AppDomain.CurrentDomain.GetData("KnownUnknown.Updater") as string;

        public static void Start(string source)
        {
            if (AppDomain.CurrentDomain.GetData("KnownUnknown.Updated") is bool)
                Log($"[更新] 已換上 {Short(Current)}");
            new Thread(() =>
            {
                try
                {
                    Check(source);
                }
                catch (Exception e)
                {
                    Log($"[更新] 沿用現有版本：{e.Message}");
                }
            }) { IsBackground = true }.Start();
        }

        private static void Check(string source)
        {
            ReleaseInfo r;
            using (MemoryStream m = new MemoryStream(Get(source, 20)))
                r = (ReleaseInfo)new DataContractJsonSerializer(typeof(ReleaseInfo)).ReadObject(m);
            if (!Version.TryParse((r.tag_name ?? "").TrimStart('v'), out Version latest))
                throw new Exception("看不懂版本 " + r.tag_name);
            latest = Normalize(latest);
            Version staged = Staged();
            if (latest <= Current)
            {
                Log($"[更新] 已經是最新版 {Short(Current)}");
                if (staged != null && staged <= Current)
                    Delete(Ready);
                return;
            }
            if (PatcherVersion == null)
            {
                Status = $"有新版 {Short(latest)}:到 GitHub Releases 下載(重新解壓 zip 會連自動更新一起裝好)";
                Log("[更新] 沒裝 patchers/KnownUnknown,不自動下載。" + Status);
                return;
            }
            if (staged == latest)
            {
                Status = $"已下載新版 {Short(latest)},下次啟動遊戲生效";
                return;
            }
            ReleaseAsset a = r.assets?.FirstOrDefault(x => x.name != null && x.name.StartsWith("KnownUnknown-", StringComparison.Ordinal) && x.name.EndsWith(".zip", StringComparison.Ordinal));
            if (a == null)
                throw new Exception($"{r.tag_name} 沒有 zip");
            Stage(Get(a.browser_download_url, 300), a.digest, latest);
            Status = $"已下載新版 {Short(latest)},下次啟動遊戲生效";
            Log("[更新] " + Status);
        }

        private static byte[] Get(string url, int seconds)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = req.ReadWriteTimeout = seconds * 1000;
            req.UserAgent = "known-unknown/" + Short(Current);
            req.Accept = "application/vnd.github+json, application/octet-stream";
            using (WebResponse res = req.GetResponse())
            using (Stream st = res.GetResponseStream())
            using (MemoryStream m = new MemoryStream())
            {
                st.CopyTo(m);
                return m.ToArray();
            }
        }

        // 驗證 → 解到 tmp/ → 改名成 ready/。tmp/ 裡就是 plugins/KnownUnknown/ 要放的檔案
        private static void Stage(byte[] zip, string digest, Version latest)
        {
            if (digest != null && digest.StartsWith("sha256:", StringComparison.Ordinal))
            {
                string hash;
                using (SHA256 sha = SHA256.Create())
                    hash = string.Concat(sha.ComputeHash(zip).Select(b => b.ToString("x2")));
                if (hash != digest.Substring(7).ToLowerInvariant())
                    throw new Exception("sha256 不符");
            }
            const string prefix = "BepInEx/plugins/KnownUnknown/";
            string tmp = Path.Combine(Root, "tmp");
            Delete(tmp);
            Directory.CreateDirectory(tmp);
            try
            {
                using (ZipArchive z = new ZipArchive(new MemoryStream(zip)))
                {
                    foreach (ZipArchiveEntry e in z.Entries)
                    {
                        string name = e.FullName.Replace('\\', '/');
                        if (name.EndsWith("/", StringComparison.Ordinal))
                            continue;
                        if (name.StartsWith("BepInEx/patchers/KnownUnknown/", StringComparison.Ordinal))
                        {
                            CheckPatcher(e);
                            continue;
                        }
                        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Substring(prefix.Length).Contains("/"))
                            continue;
                        e.ExtractToFile(Path.Combine(tmp, name.Substring(prefix.Length)));
                    }
                }
                string dll = Path.Combine(tmp, "KnownUnknown.dll");
                if (!File.Exists(dll))
                    throw new Exception("zip 裡沒有 KnownUnknown.dll");
                Version v = Normalize(AssemblyName.GetAssemblyName(dll).Version);
                if (v != latest)
                    throw new Exception($"DLL 版本 {v} 和 release {latest} 不同");
                Delete(Ready);
                Directory.Move(tmp, Ready);
            }
            catch
            {
                Delete(tmp);
                throw;
            }
        }

        // 換插件的那段換不了自己；新版有改的話只能請玩家重新解壓 zip
        private static void CheckPatcher(ZipArchiveEntry e)
        {
            if (!e.Name.EndsWith(".dll", StringComparison.Ordinal))
                return;
            string tmp = Path.Combine(Root, "patcher.tmp");
            try
            {
                e.ExtractToFile(tmp, true);
                Version v = AssemblyName.GetAssemblyName(tmp).Version;
                if (Short(Normalize(v)) != PatcherVersion)
                    Log($"[更新] 更新程式本身有新版({PatcherVersion} → {Short(v)}),要手動重新解壓 zip 才會換");
            }
            finally
            {
                Delete(tmp);
            }
        }

        // ready/ 裡放好的版本；沒有 = null
        private static Version Staged()
        {
            try
            {
                string dll = Path.Combine(Ready, "KnownUnknown.dll");
                return File.Exists(dll) ? Normalize(AssemblyName.GetAssemblyName(dll).Version) : null;
            }
            catch
            {
                return null;
            }
        }

        // 0.13.0 和 0.13.0.0 當成一樣
        private static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

        private static string Short(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

        private static void Delete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception e)
            {
                Log($"[更新] 刪不掉 {path}:{e.Message}");
            }
        }
    }
}
