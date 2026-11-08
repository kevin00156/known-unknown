# 多玩10塊錢(known-unknown)

活俠傳(Legend of Mortal)的 mod。遊戲裡按 **F2**，列出這一旬去哪裡、選哪個行動還看得到沒讀過的劇情(白字)、
汗青書和生死簿；也可以勾選指定下次抽到哪個事件、擲出哪個結果。

## 安裝

1. [下載最新版](https://github.com/kevin00156/known-unknown/releases/latest/download/KnownUnknown.zip)(或到 [Releases](https://github.com/kevin00156/known-unknown/releases) 下載 `KnownUnknown-x.y.z.zip`,內容一樣)。
2. 解壓到遊戲資料夾(有 `Mortal.exe` 的那層；Steam 收藏庫對遊戲按右鍵 → 管理 → 瀏覽本機檔案)，有重複的檔案就覆蓋。
3. 進遊戲按 F2。

- zip 已經附了 BepInEx 6，不用另外裝。原本裝 BepInEx 5 的 mod 會失效。
- Steam Deck / Linux：遊戲的啟動選項加上 `WINEDLLOVERRIDES="winhttp=n,b" %command%`。
- 之後會自動更新。移除：刪掉 `BepInEx/plugins/KnownUnknown` 和 `BepInEx/patchers/KnownUnknown`。
- 有問題請附上 `BepInEx/LogOutput.log` 開 issue。

## 顏色

- 白色：現在去就看得到
- 土黃：角色條件還不夠，旁邊寫需要什麼、去哪拿
- 灰色：現版本拿不到(要勾「也顯示現版本未開放的劇情」才會列，會劇透)

## 設定

`BepInEx/config/known-unknown.cfg`(第一次啟動後產生)：

| 設定 | 預設 | 說明 |
|---|---|---|
| `ToggleKey` | F2 | 開關視窗 |
| `CountUnit` | Both | `Events` = 幾段劇情、`Lines` = 幾句台詞、`Both` = 兩個都顯示 |
| `AllowForce` | true | 勾選框：指定下次抽到的事件、擲骰結果 |
| `ShowLocked` | false | 也列出現版本拿不到的劇情(灰色，會劇透) |
| `AutoUpdate` | true | 自動更新；關掉就完全不連網 |

開發相關見 [DEVELOPING.md](DEVELOPING.md)。

## 授權

MIT。zip 內附的 [BepInEx](https://github.com/BepInEx/BepInEx) 是 LGPL-2.1。與 Obb Studio 無關。
