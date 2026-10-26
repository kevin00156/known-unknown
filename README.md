# known-unknown(多玩10塊錢)

活俠傳(Legend of Mortal)未讀劇情盤點：列出哪些劇本還有白字，並排除現版本拿不到的劇本。

```
uv run python known_unknown.py               # 自動找最新的 Save_universe.json
uv run python known_unknown.py --top 0       # 全列
uv run python known_unknown.py --unreachable # 順便列出判定不可達的劇本
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
- **校準**:判成不可達卻已讀的句數應該是 0。不是 0 就代表漏了某種引用方式。

## 已知限制

可達性只看「有沒有路」,不看條件成不成立(旗標、好感、周目)。條件分析與觸發位置說明是下一步。
