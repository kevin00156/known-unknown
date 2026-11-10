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

        // 兩個分支都跳到同一份劇本(s0213_01 擲骰兩種失敗都跳 s0213_02):後面的台詞兩條路都要記，
        // 只記第一條的話，第一條的條件不成立就把整段當成看不到
        private const string TwoWays = @"local r = checkpointmanager.Switch(""S"")
if r == 1 then
	luamanager.SetNextScript(""tail"")
elseif r == 2 then
	luamanager.SetNextScript(""tail"")
end
";

        // 先在分支裡跳、後面又無條件跳：無條件那條路就夠了
        private const string ThenAlways = @"local r = checkpointmanager.Switch(""S"")
if r == 1 then
	luamanager.SetNextScript(""tail"")
end
luamanager.SetNextScript(""tail"")
";

        private const string Tail = @"say(luamanager.GetStoryText(""T1""))
luamanager.SetNextScript(""entry"")
";

        private static List<string> Ways(StoryIndex index, string start, string key) =>
            index.Paths(start).Where(x => x.Key.Kind == ItemKind.Key && x.Key.Value == key)
                .Select(x => string.Join("&", x.Value.Select(g => $"{g.Name}={g.Value}"))).OrderBy(w => w).ToList();

        [Fact]
        public void MergedBranchesKeepEveryWay()
        {
            StoryIndex index = Index(("entry", TwoWays), ("tail", Tail));
            Assert.Equal(new[] { "S=1", "S=2" }, Ways(index, "entry", "T1"));
        }

        [Fact]
        public void UnconditionalWayReplacesConditionalOnes()
        {
            StoryIndex index = Index(("entry", ThenAlways), ("tail", Tail));
            Assert.Contains("", Ways(index, "entry", "T1"));
        }

        // 同一個變數名、陣列名用兩次(s0003_01_02):兩個選單要分開，第一個選單的選項不能對到第二個的分支
        private const string ReusedNames = @"local opt = {}
opt[1] = ""O_A""
opt[2] = ""O_B""
local choice_opt = choose(opt)
if choice_opt == 1 then
	say(luamanager.GetStoryText(""A1""))
elseif choice_opt == 2 then
	say(luamanager.GetStoryText(""B1""))
end
local opt = {}
opt[1] = ""O_C""
local choice_opt = choose(opt)
if choice_opt == 1 then
	say(luamanager.GetStoryText(""C1""))
end
";

        [Fact]
        public void ReusedMenuNamesStaySeparate()
        {
            StoryIndex index = Index(("menu", ReusedNames));
            Assert.Equal(new[] { "A1" }, Keys(index, "menu", "O_A"));
            Assert.Equal(new[] { "B1" }, Keys(index, "menu", "O_B"));
            Assert.Equal(new[] { "C1" }, Keys(index, "menu", "O_C"));
        }

        // 兩個分支各擺一個選單，選項字串一樣(s0020_02_04):照選單所在的分支挑
        private const string SameText = @"local r = checkpointmanager.Switch(""S"")
if r == 1 then
	local one = {}
	one[1] = ""O_X""
	local choice_one = choose(one)
	if choice_one == 1 then
		say(luamanager.GetStoryText(""X1""))
	end
elseif r == 2 then
	local two = {}
	two[1] = ""O_X""
	local choice_two = choose(two)
	if choice_two == 1 then
		say(luamanager.GetStoryText(""X2""))
	end
end
";

        [Fact]
        public void SameOptionTextInTwoMenus()
        {
            StoryIndex index = Index(("menu", SameText));
            Func<int, Func<Guard[], bool>> branch = n => outer => outer.All(g => g.Value == n);
            Assert.Equal(new[] { "X1" }, index.OptionItems("menu", "O_X", branch(1)).Select(i => i.Value));
            Assert.Equal(new[] { "X2" }, index.OptionItems("menu", "O_X", branch(2)).Select(i => i.Value));
            Assert.Equal(new[] { "X1", "X2" }, index.OptionItems("menu", "O_X").Select(i => i.Value));
        }

        // 遊戲裡全部的劇本：多記幾條路不能讓結果爆量(迴圈、層層會合)
        [Fact]
        public void PathsStayBounded()
        {
            KeyValuePair<string, string>[] scripts = RealScripts();
            StoryIndex index = new StoryIndex(scripts, new HashSet<string>());
            long total = 0;
            int worst = 0;
            string worstAt = null;
            foreach (KeyValuePair<string, string> s in scripts)
            {
                int n = index.Paths(s.Key).Count();
                total += n;
                if (n > worst)
                {
                    worst = n;
                    worstAt = s.Key;
                }
            }
            Console.WriteLine($"[路數] 最多 {worstAt} {worst} 項，全部 {total} 項");
            Assert.True(worst < 200000, $"{worstAt} 走出 {worst} 項(全部 {total})");
        }

        private static KeyValuePair<string, string>[] RealScripts()
        {
            string dir = Environment.GetEnvironmentVariable("KU_SCRIPTS");
            Assert.True(dir != null && Directory.Exists(dir), "要有遊戲劇本：用 tests/run.sh 跑(KU_SCRIPTS = known_unknown.py --dump-scripts 的輸出)");
            KeyValuePair<string, string>[] scripts = Directory.GetFiles(dir, "*.lua")
                .Select(f => new KeyValuePair<string, string>(Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), File.ReadAllText(f)))
                .ToArray();
            Assert.True(scripts.Length > 1000, $"劇本只有 {scripts.Length} 份");
            return scripts;
        }

        // 遊戲裡全部的選單：選了一個選項算到的台詞，不能有同一個選單別的選項分支裡的台詞
        // (KU-25:ch5_4_6_break_start_01「四師兄的意見」跳回選單，把決鬥那些選項全算進來，顯示 100 句)
        [Fact]
        public void NoOptionCountsAnotherOptionsLines()
        {
            KeyValuePair<string, string>[] scripts = RealScripts();
            StoryIndex index = new StoryIndex(scripts, new HashSet<string>());

            List<string> bad = new List<string>();
            int menus = 0;
            foreach (KeyValuePair<string, string> s in scripts)
            {
                Script script = index.Get(s.Key);
                if (script.Options.Count == 0)
                    continue;
                menus++;
                foreach (KeyValuePair<string, Pick> o in script.Options.SelectMany(x => x.Value.Select(p => new KeyValuePair<string, Pick>(x.Key, p))))
                {
                    // 同一個字串在好幾個選單裡的，另外由 SameOptionTextInTwoMenus 測
                    if (script.Options[o.Key].Count > 1)
                        continue;
                    // 只在別的選項分支、不在這個選項分支裡的台詞
                    Func<Item, bool> mine = i => i.Guards.Any(g => g.Kind == GuardKind.Choice && g.Name == o.Value.Menu && g.Value == o.Value.Index);
                    HashSet<string> others = new HashSet<string>(script.Items
                        .Where(i => i.Kind == ItemKind.Key && !mine(i)
                            && i.Guards.Any(g => g.Kind == GuardKind.Choice && g.Name == o.Value.Menu))
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
