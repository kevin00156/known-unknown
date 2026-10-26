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

- Lua 劇本內部的分支(`checkpointmanager.Switch/Condition`)沒有求值，同一劇本裡拿不到的分支仍算白字。
- 入口條件只顯示，不跟目前存檔的旗標比對；「現在去哪裡能看到」要等遊戲內插件(KU-3)用 runtime 判斷。

## 遊戲內插件(plugin/,BepInEx 6)

按 **F8**(BepInEx 設定 `known-unknown.cfg` 可改)開關視窗，列出**這一旬**還看得到白字的地方；開著時會跟著遊戲狀態自動更新。

選單出現時，右上角會自動列出每個選項會看到多少白字(地點行動顯示抽到沒看過結果的機率；分支裡的 Switch 條件會用當下狀態判斷)。在自由行動地圖上會自動顯示本旬白字最多的地方。設定 `MenuHints` / `MapHints` 可關閉。算出的結果也會寫進 `BepInEx/LogOutput.log`(`[選單]`、`[本旬]`)，方便對照。

- 這一旬已經生效的任務(`MissionManagerData.ActiveSubMissions` + 主線)：要去哪個地點，或本旬會自動觸發
- 各地點的隨機事件：用遊戲自己的 `PositionEventData.GetRate()` 算當下機率，只列還有白字的

已讀判斷直接問 `ReadStorySystem.ExistKey`,不讀不寫存檔。

建置(遊戲 DLL 直接從安裝目錄引用，不進 repo):

```
cd plugin
docker run --rm -v "$PWD":/src -v "$GAME":/game:ro -w /src -e HOME=/tmp \
  mcr.microsoft.com/dotnet/sdk:8.0 dotnet build -c Release -p:GamePath=/game -o /src/bin/out
cp bin/out/KnownUnknown.dll "$GAME/BepInEx/plugins/KnownUnknown/"
```
