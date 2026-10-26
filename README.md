# known-unknown(多玩10塊錢)

活俠傳(Legend of Mortal)未讀劇情盤點：列出哪些劇本還有白字，並排除現版本拿不到的劇本。

```
uv run python known_unknown.py               # 自動找最新的 Save_universe.json
uv run python known_unknown.py --top 0       # 全列
uv run python known_unknown.py --only 第8章   # 只看某分類(第N章/支線/談話/養成…)
uv run python known_unknown.py --unreachable # 順便列出判定不可達的劇本與原因
uv run python known_unknown.py --rebuild     # 遊戲更新後重建 ku-db.json
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

按 **F8**(BepInEx 設定 `known-unknown.cfg` 可改)開關視窗；關掉就什麼都不顯示。視窗內容跟著遊戲狀態自動更新：

- 選單開著時，上方列出每個選項會看到多少白字(地點行動顯示抽到沒看過結果的機率)
- 下方是**這一旬**還看得到白字的地方，每個地點行動底下逐一列出還有白字的結果
- 勾選「顯示未開放劇情和需要的條件」= `ShowLocked`(會劇透)
- **雙擊某個結果 = 指定下次去那裡一定抽到它**(再雙擊取消，用過一次就清掉)。旗標提示的「取得處」也能雙擊。
  遊戲照常抽籤，只把被指定的那張事件表的結果換掉；設定 `AllowForce = false` 可整個關掉
- **在地圖上雙擊 = 直接去**:替你點那個地點(照常扣行動點)，選單跳出來時自動按通往那張事件表的選項。
  走的是跟自己點地圖同一條路：沒行動點、主線指定別處、地點有支線任務時，照遊戲規則不會去成或先演任務，指定會留到下次
- 可以同時指定好幾個地點行動(每個行動一個結果)，用掉的才會清掉。滑鼠在視窗上時，地圖的地點不會被點到
- **擲骰**:轉盤出來時，視窗列出每個結果在目前骰面範圍內的機率和會看到的白字(「擲不到」= 這次範圍內骰不出來)。
  雙擊 = 指定擲出它：從骰得到那個結果的面裡挑一面當亂數，轉盤數字、加成、結果都照遊戲自己算

要先進過一次地點，各地點的機率才算得出來(地點事件表跟著劇情場景才載入)。算出的結果也會寫進 `BepInEx/LogOutput.log`(`[選單]`、`[本旬]`、`[指定]`)，方便對照。

- 這一旬已經生效的任務(`MissionManagerData.ActiveSubMissions` + 主線)：要去哪個地點，或本旬會自動觸發
- 各地點的隨機事件：照遊戲的抽法(依序取機率 > 0、同結果代碼只算第一筆)算當下機率，只列還有白字的
- 劇本裡的分支(Switch、選項的 Condition)用當下遊戲狀態判斷，只算現在走得到的白字；
  同一段劇情裡自己會設的旗標(暫存旗標、前面選項設的)當成可能成立

設定(`BepInEx/config/known-unknown.cfg`):

| 設定 | 預設 | 說明 |
|---|---|---|
| `ToggleKey` | F8 | 開關完整清單 |
| `CountUnit` | Both | `Events` = 幾段劇情、`Lines` = 幾句台詞、`Both` = 「2 段・11 句」 |
| `AllowForce` | true | 雙擊結果指定下次抽到它；在地圖上雙擊直接帶你去；雙擊擲骰結果指定擲出它 |
| `ShowLocked` | false | **會劇透**。也列出目前條件不足的白字和需要的條件(好感、屬性、物品、旗標從哪拿)，以及「本旬拿得到、之後會解鎖白字的旗標」。現版本拿不到的(旗標沒有任何地方寫入)永遠不列 |

條件判斷全部自己重寫成只讀：遊戲的 `SwitchResultData/ConditionResultData.Execute()` 會讓計數 +1,
DiceMaster 在 `PositionEventData.GetRate()` 上掛了 patch(呼叫會吃掉玩家指定的事件)，插件都不呼叫。

`plugin/SystemFlags.cs` 是 Lua 以外(戰鬥、商店、設施…)會寫入的旗標清單，遊戲更新後用
`uv run python known_unknown.py --system-flags plugin/SystemFlags.cs` 重新產生。

已讀判斷直接問 `ReadStorySystem.ExistKey`,不讀不寫存檔。

建置(遊戲 DLL 直接從安裝目錄引用，不進 repo):

```
cd plugin
docker run --rm -v "$PWD":/src -v "$GAME":/game:ro -w /src -e HOME=/tmp \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release -p:GamePath=/game -o /src/bin/out
cp bin/out/KnownUnknown.dll "$GAME/BepInEx/plugins/KnownUnknown/"
```

開發用遙控插件(`dev/`,不隨 mod 發佈)：讀 `BepInEx/ku-cmd.txt` 的指令在遊戲內點 UI、透過 DiceMaster 指定地點事件、
指定骰子點數、比對已讀台詞(`snap`/`diff`)，不用搶滑鼠鍵盤焦點就能自動測試。指令見 `dev/Remote.cs` 開頭。
