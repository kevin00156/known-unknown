using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace KnownUnknown.Tests
{
    public class OptionTests
    {
        // 閒聊選單：選項 1 問完話跳回選單；選項 2 跳到另一份劇本，演完也跳回選單
        private const string Menu = @"local talk = {}
talk[1] = ""talk+O_A+0+0""
talk[2] = ""talk+O_B+0+0""
local choice_talk = choose(talk)
if choice_talk == 1 then
	say(luamanager.GetStoryText(""A1""))
	luamanager.SetNextScript(""menu"")
elseif choice_talk == 2 then
	say(luamanager.GetStoryText(""B1""))
	luamanager.SetNextScript(""after_b"")
end
";

        private const string AfterB = @"say(luamanager.GetStoryText(""B2""))
luamanager.SetNextScript(""menu"")
";

        private static StoryIndex Index(params (string name, string text)[] scripts) =>
            new StoryIndex(scripts.Select(s => new KeyValuePair<string, string>(s.name, s.text)).ToArray(), new HashSet<string>());

        private static HashSet<string> Keys(StoryIndex index, string menu, string option)
        {
            Item[] items = index.OptionItems(menu, option);
            Assert.NotNull(items);
            return new HashSet<string>(items
                .Where(i => i.Kind == ItemKind.Key).Select(i => i.Value)
                .Concat(items.Where(i => i.Kind == ItemKind.Script)
                    .SelectMany(i => index.Follow(i, menu)).Where(x => x.Key.Kind == ItemKind.Key).Select(x => x.Key.Value)));
        }

        [Fact]
        public void JumpBackToMenuCountsOnlyThisOption()
        {
            StoryIndex index = Index(("menu", Menu), ("after_b", AfterB));
            Assert.Equal(new[] { "A1" }, Keys(index, "menu", "talk+O_A+0+0"));
        }

        [Fact]
        public void FollowedScriptStopsAtMenu()
        {
            StoryIndex index = Index(("menu", Menu), ("after_b", AfterB));
            Assert.Equal(new[] { "B1", "B2" }, Keys(index, "menu", "talk+O_B+0+0").OrderBy(k => k));
        }

        // 遊戲裡全部的選單：選了一個選項算到的台詞，不能有同一個選單別的選項分支裡的台詞
        // (KU-25:ch5_4_6_break_start_01「四師兄的意見」跳回選單，把決鬥那些選項全算進來，顯示 100 句)
        [Fact]
        public void NoOptionCountsAnotherOptionsLines()
        {
            string dir = Environment.GetEnvironmentVariable("KU_SCRIPTS");
            Assert.True(dir != null && Directory.Exists(dir), "要有遊戲劇本：用 tests/run.sh 跑(KU_SCRIPTS = known_unknown.py --dump-scripts 的輸出)");
            KeyValuePair<string, string>[] scripts = Directory.GetFiles(dir, "*.lua")
                .Select(f => new KeyValuePair<string, string>(Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), File.ReadAllText(f)))
                .ToArray();
            Assert.True(scripts.Length > 1000, $"劇本只有 {scripts.Length} 份");
            StoryIndex index = new StoryIndex(scripts, new HashSet<string>());

            List<string> bad = new List<string>();
            int menus = 0;
            foreach (KeyValuePair<string, string> s in scripts)
            {
                Script script = index.Get(s.Key);
                if (script.Options.Count == 0)
                    continue;
                menus++;
                foreach (KeyValuePair<string, KeyValuePair<string, int>> o in script.Options)
                {
                    // 只在別的選項分支、不在這個選項分支裡的台詞
                    Func<Item, bool> mine = i => i.Guards.Any(g => g.Kind == GuardKind.Choice && g.Name == o.Value.Key && g.Value == o.Value.Value);
                    HashSet<string> others = new HashSet<string>(script.Items
                        .Where(i => i.Kind == ItemKind.Key && !mine(i)
                            && i.Guards.Any(g => g.Kind == GuardKind.Choice && g.Name == o.Value.Key))
                        .Select(i => i.Value));
                    others.ExceptWith(script.Items.Where(i => i.Kind == ItemKind.Key && mine(i)).Select(i => i.Value));
                    int leaked = Keys(index, s.Key, o.Key).Count(others.Contains);
                    if (leaked > 0)
                        bad.Add($"{s.Key} | {o.Key}:{leaked} 句");
                }
            }
            Assert.True(menus > 50, $"選單只有 {menus} 個");
            Assert.True(bad.Count == 0, $"{bad.Count} 個選項算到別的選項的台詞：\n" + string.Join("\n", bad.Take(30)));
        }
    }
}
