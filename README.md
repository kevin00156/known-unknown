# known-unknown(多玩10塊錢)

活俠傳(Legend of Mortal)未讀劇情盤點：遊戲內按 F2，列出這一旬去哪裡、選哪個行動還看得到白字(沒讀過的台詞)、
汗青書和生死簿，也可以指定下次抽到哪個事件、擲出哪個結果。功能說明見下方「遊戲內插件」。

## 安裝(玩家)

1. 安裝 **BepInEx 6**(Unity.Mono 版，**不是** BepInEx 5)：下載
   [BepInEx-Unity.Mono-win-x64-6.0.0-be.789](https://builds.bepinex.dev/projects/bepinex_be/789/BepInEx-Unity.Mono-win-x64-6.0.0-be.789%2B6cea286.zip)，
   解壓到遊戲資料夾(有 `Mortal.exe` 的那層；Steam 收藏庫裡對遊戲按右鍵 → 管理 → 瀏覽本機檔案)。
   啟動遊戲一次再關掉，資料夾裡會多出 `BepInEx/plugins`。
2. 到 [Releases](https://github.com/kevin00156/known-unknown/releases) 下載最新的 `KnownUnknown-x.y.z.zip`,
   解壓到同一個遊戲資料夾(zip 裡已經是 `BepInEx/plugins/KnownUnknown/` 和 `BepInEx/patchers/KnownUnknown/`)。
3. 進遊戲按 **F2** 開關視窗。

- **自動更新**：每次啟動遊戲，插件在背景查一次 GitHub 有沒有新版，有就下載、驗證(sha256、版本號)，
  F2 視窗顯示「已下載新版 x.y.z，下次啟動遊戲生效」；下次啟動時 `patchers/KnownUnknown` 在插件載入前換上。
  沒網路、下載壞掉、換檔失敗都沿用現有版本，只寫日誌。不想連網就在 `known-unknown.cfg` 的 `[Update]` 設 `AutoUpdate = false`,
  之後要更新就自己下載新版 zip 覆蓋。只裝了 `plugins/KnownUnknown`(沒有 `patchers/`)的話不會自動下載，只在視窗提示有新版。
- 移除：刪掉 `BepInEx/plugins/KnownUnknown` 和 `BepInEx/patchers/KnownUnknown` 兩個資料夾。
- Steam Deck / Linux(Proton)：遊戲的啟動選項加上 `WINEDLLOVERRIDES="winhttp=n,b" %command%`,否則 BepInEx 不會載入。
- 插件不讀也不寫存檔；「已讀」直接問遊戲。指定事件/擲骰的功能不想要可以在 `BepInEx/config/known-unknown.cfg` 設 `AllowForce = false`。
- 沒反應時看 `BepInEx/LogOutput.log` 裡有沒有 `多玩10塊錢` 和 `[預載]` 開頭的行，回報問題時附上這個檔案。
- 遊戲改版後若插件還沒跟上，事件機率在第一次進劇情後會自己修好；灰色(未開放)的判斷可能暫時不準。

## 離線 CLI(開發用)

列出哪些劇本還有白字，並排除現版本拿不到的劇本。路徑寫死 Linux Steam 安裝位置，玩家不需要。

```
uv run python known_unknown.py               # 自動找最新的 Save_universe.json
uv run python known_unknown.py --top 0       # 全列
uv run python known_unknown.py --only 第8章   # 只看某分類(第N章/支線/談話/養成…)
uv run python known_unknown.py --unreachable # 順便列出判定不可達的劇本與原因
uv run python known_unknown.py --rebuild     # 強制重建 ku-db.json(遊戲版本變了會自動重建)
```

## 原理

- **已讀**:`Mortal.Core.ReadStorySystem`,每句台詞經 `LuaManager.GetStoryText(key)` 顯示時加入；
  存在跨存檔的 `Save_universe.json` → `ReadStoryData`。白字 = 劇本裡的 key − 已讀 key。
- **劇本**:`globalgamemanagers` 的 ResourceManager 下 `story/chinesetraditional/*` 的 Lua TextAsset
  (`chinesesimplified` 是沒在用的舊草稿)。台詞全部是 `say(luamanager.GetStoryText("..."))`。
- **可達性**:引用圖 BFS。
  - 入口：`MissionData`、`MissionCheckData`、`GameTimeData`、`FreePositionData`、
    `PositionController`、`PlayerStatManagerData`、`FlagData` 裡出現的劇本名。
  - 邊：劇本裡的字串常數若是劇本名或 `PositionResultData`/`SwitchResultData`/`ConditionResultData` 名。
  - `PositionResultData` 的劇本名是執行時拼的：`_scriptPrefix + "_" + StoryMappingItem.Value`,
    用 TypeTreeGenerator 從 Managed DLL 產生型別樹解開。
  - 排除：`Test*` 除錯按鈕、標題畫面隱藏的「啟程(活動)」展場模式(`act*`)。
- **條件**:`MissionCheckData`(`_active` + 條件 AND + 時間)、`PositionEventData._activeCondition`
  依遊戲語意求值。只有「整個遊戲沒有任何地方會寫入」的旗標用預設值代入，其他一律當成可能成立。
  寫入者 = Lua `statmodifymanager.SetFlag/AddFlag` 字面值、C# 寫死的名字、
  以及 `FLAG_READERS` 以外任何用 PPtr 直接指到 `FlagData` 的物件(寧可多算)。
- **校準**:判成不可達卻已讀的劇本會用 ⚠ 標出來。目前唯一一份是 `ch7_5_4_4_3`
  (要 `G11007` 攻略小菊=1,現版沒有任何地方會設這個旗標)，推測是舊版或 DiceMaster 強制觸發看到的。

## 已知限制

- 離線工具不對劇本內部分支求值，也不跟目前存檔的旗標比對；這些由遊戲內插件用當下狀態判斷。
- 插件用「現在」的屬性/好感判斷分支；同一段劇情前面才加的好感不會預先算進去(旗標有處理)。
- 「本旬拿得到的旗標」只算光靠這一個旗標就能解鎖的內容，還缺其他條件的不列。

## 遊戲內插件(plugin/,BepInEx 6)

按 **F2**(BepInEx 設定 `known-unknown.cfg` 可改)開關視窗；關掉就什麼都不顯示。視窗內容跟著遊戲狀態自動更新：

- 選單開著時，上方列出每個選項會看到多少白字(地點行動顯示抽到沒看過結果的機率)
- 下方是**這一旬**還看得到白字的地方，每個地點行動底下逐一列出還有白字的結果
- 除了白字，也算講經堂還沒收錄的**汗青書**(結局，劇本 `endgamepanel.Open`)和**生死簿**(死亡，劇本 `ChangeScene("GameOver")`)，
  例如「白字 1 段・6 句・生死簿 1」；「本旬」那行顯示收集進度。決鬥打輸會進的生死簿也算(戰鬥資料的 `_deadEnd`/`_deadLibrary`，由 `--system-flags` 一起產生)，那場決鬥所在的劇情就標「生死簿 N」;戰場(Battle)打輸只會重來，不進生死簿。講經堂 122 頁裡有 3 頁(10002、10063、10074)找不到觸發的地方，不提示
- 角色條件還不夠的白字一律列出，**土黃色**，附需要的條件(好感、屬性、物品、旗標從哪拿；心上人等代號顯示成名字；遊戲沒寫名字的旗標顯示「未命名旗標(旗標 ID)」)。
  旗標的「取得：」只列寫進去的值合要求的劇本；要的是開新遊戲時的值、這一輪改不回去的標「新一輪的初始值」，劇情只會寫成別的值的標「劇情只會設成別的值」，
  也列出「本旬拿得到、之後會解鎖白字的旗標」
- 勾選「也顯示現版本未開放的劇情」= `ShowLocked`(會劇透)：連條件永遠不會成立的白字也列，**灰色**。
  「永遠不會成立」= 哪一輪都一樣，從開新遊戲的預設值算起：旗標只會變成劇本寫過的值(遊戲系統會寫的當成什麼值都可能)，
  好感最多是預設值加上劇本裡所有加好感的總和。有可重複抽的地點事件會加好感、或新一輪會繼承好感的角色，當成沒上限。
  這一局錯過、換一輪還看得到的，算條件不足(土黃)
- **勾選結果旁的金色勾選框 = 指定下次去那裡一定抽到它**(取消勾選 = 取消，用過一次就清掉)。旗標提示的「取得處」也能勾。
  清單上沒列出的指定(條件變了、換旬了)會在最上面另外列，方便取消。
  遊戲照常抽籤，只把被指定的那張事件表的結果換掉；設定 `AllowForce = false` 可整個關掉
- **在地圖上勾選 = 直接去**:替你點那個地點(照常扣行動點)，選單跳出來時自動按通往那張事件表的選項。
  走的是跟自己點地圖同一條路：沒行動點、主線指定別處、地點有支線任務時，照遊戲規則不會去成或先演任務，指定會留到下次
- 可以同時指定好幾個地點行動(每個行動一個結果)，用掉的才會清掉。滑鼠在視窗上時，遊戲收不到滑鼠(地圖地點、按鈕、對話推進都不會被點到)，移開就恢復
- **擲骰**:轉盤出來時，視窗列出每個結果在目前骰面範圍內的機率和會看到的白字(「擲不到」= 這次範圍內骰不出來)。
  勾選 = 指定擲出它：從骰得到那個結果的面裡挑一面當亂數，轉盤數字、加成、結果都照遊戲自己算

地點事件表本來跟著劇情場景才載入；插件內建一份抽出來的資料(`plugin/PositionTables.txt`)，讀檔後在地圖上就重建好，
條件接的是遊戲裡的活屬性/旗標，機率是當下真值。第一次進劇情時會拿真表逐一比對，結果寫進日誌(`[預載]`)。
遊戲更新後內建資料對不上時，插件第一次進劇情就把真表照同格式存到 `BepInEx/cache/KnownUnknown/PositionTables.txt`,
下次啟動改用它(內建資料優先，對不上才用快取)，玩家不用做任何事。算出的結果也會寫進 `BepInEx/LogOutput.log`(`[選單]`、`[本旬]`、`[指定]`)，方便對照。

- 這一旬已經生效的任務(`MissionManagerData.ActiveSubMissions` + 主線)：要去哪個地點，或本旬會自動觸發
- 各地點的隨機事件：照遊戲的抽法(依序取機率 > 0、同結果代碼只算第一筆)算當下機率，只列還有白字的
- 劇本裡的分支(Switch、選項的 Condition)用當下遊戲狀態判斷，只算現在走得到的白字；
  同一段劇情裡自己會設的旗標(暫存旗標、前面選項設的)當成可能成立

設定(`BepInEx/config/known-unknown.cfg`):

| 設定 | 預設 | 說明 |
|---|---|---|
| `ToggleKey` | F2 | 開關完整清單 |
| `CountUnit` | Both | `Events` = 幾段劇情、`Lines` = 幾句台詞、`Both` = 「2 段・11 句」 |
| `AllowForce` | true | 結果旁的勾選框：指定下次抽到它、在地圖上直接帶你去、擲骰指定擲出它 |
| `AutoUpdate` | true | `[Update]` 段。啟動時查 GitHub 有沒有新版，有就背景下載，下次啟動換上；關掉就完全不連網 |
| `UpdateSource` | GitHub API | `[Update]` 段。查新版的網址(releases/latest 的 JSON 格式)，開發測試用，平常不用改 |
| `ShowLocked` | false | **會劇透**。也列出現版本未開放的白字(條件永遠不會成立，例如旗標不會變成要的值、好感加不到)，灰色。角色條件不夠的白字不受這個設定影響，一律列出(土黃色) |

條件判斷全部自己重寫成只讀：遊戲的 `SwitchResultData/ConditionResultData.Execute()` 會讓計數 +1,
DiceMaster 在 `PositionEventData.GetRate()` 上掛了 patch(呼叫會吃掉玩家指定的事件)，插件都不呼叫。

`plugin/SystemFlags.cs` 是 Lua 以外(戰鬥、商店、設施、談話選項重設…)會寫入的旗標、會改好感的角色(新一輪繼承、商店、戰鬥)，和決鬥打輸會進的生死簿，遊戲更新後用
`uv run python known_unknown.py --system-flags plugin/SystemFlags.cs` 重新產生。
`plugin/PositionTables.txt`(預載的地點事件表)同樣在遊戲更新後用
`uv run python known_unknown.py --position-tables plugin/PositionTables.txt` 重新產生。`release.sh` 發版前會自動重跑這兩個，
內容有變(遊戲更新過)就停下來，看過、在遊戲裡測過再 commit。

兩份產物都記下產生時的遊戲版本(`Application.version` = PlayerSettings 的 bundleVersion,例如 `release_1.0.6001.0`)。
插件啟動時比對，遊戲版本不同就在日誌和 F2 視窗最上面警告「灰色(未開放)的判斷可能不準」(SystemFlags 可能少了新的寫入點)。
PositionTables 版本或資產檔大小對不上就不用：那一次啟動退回「進過一次劇情才有機率」，之後用插件自己存的快取(快取也記版本)。

已讀判斷直接問 `ReadStorySystem.ExistKey`,不讀不寫存檔。

建置(遊戲 DLL 直接從安裝目錄引用，不進 repo):

```
cd plugin
docker run --rm -v "$PWD":/src -v "$GAME":/game:ro -w /src -e HOME=/tmp \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release -p:GamePath=/game -o /src/bin/out
cp bin/out/KnownUnknown.dll "$GAME/BepInEx/plugins/KnownUnknown/"
```

發版：先改 `plugin/KnownUnknown.csproj` 和 `plugin/Plugin.cs` 的版本號並 commit,再跑 `./release.sh [說明檔]`:
建置、打包 `KnownUnknown-<版本>.zip`(`BepInEx/plugins/KnownUnknown/` + `BepInEx/patchers/KnownUnknown/`)、push、建 GitHub Release `v<版本>`。
玩家端自動更新看的就是這個 release 的 zip(檔名要是 `KnownUnknown-*.zip`，裡面 DLL 的版本要等於 tag)。
`updater/`(patcher)換不了自己，盡量別改；改了玩家要手動重新解壓 zip,插件會在日誌提醒。

自動更新的本機測試：`UpdateSource` 指到本機 `python -m http.server` 放的假 `latest.json`
(`{"tag_name":"v0.99.0","assets":[{"name":"KnownUnknown-0.99.0.zip","browser_download_url":"http://127.0.0.1:8765/…","digest":"sha256:…"}]}`)。
下載用 `HttpWebRequest`:`UnityWebRequest` 在 Wine 下從 `release-assets.githubusercontent.com` 下載會收完資料卻一直不結束；
`JsonUtility` 在插件組件裡填不進自訂類別的陣列，JSON 改用 `DataContractJsonSerializer`。

開發用遙控插件(`dev/`,不隨 mod 發佈)：讀 `BepInEx/ku-cmd.txt` 的指令在遊戲內點 UI、透過 DiceMaster 指定地點事件、
指定骰子點數、比對已讀台詞(`snap`/`diff`)，不用搶滑鼠鍵盤焦點就能自動測試。指令見 `dev/Remote.cs` 開頭。

## 授權

MIT,見 `LICENSE`。與 Obb Studio 無關；`plugin/PositionTables.txt` 是從遊戲資產抽出的事件表數值，著作權屬於原作者。
