# MyTree

輕量級 Windows 命令列目錄樹檢視工具（單一 C# 原始檔、零第三方相依性）。
除了標準目錄樹外，支援**時間軸樹（按修改年月/日期歸納）**、**重覆檔名自動識別與過濾（`-dup`）**、**目錄與檔案層數分離控制**，以及**開發者常用群組與正則表達式過濾**。

## 編譯方式

無須安裝 Visual Studio 或龐大的 SDK，直接使用 Windows 內建的 .NET Framework 編譯器即可編譯為單一執行檔：

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /out:MyTree.exe MyTree.cs
```

## 快速上手

```powershell
# 檢視當前目錄（預設自動排除 .git, .vs, .vscode, .idea，並自動標示 "(重覆)" 檔名）
MyTree

# 只顯示同名（重覆）檔案散落在哪些目錄分支
MyTree -dup

# 結合時間軸樹與重覆檔案過濾：檢視同名檔案在不同日期的散落位置
MyTree -dt -dup

# 展開 4 層目錄，但只列出前 2 層內的檔案，並顯示檔案大小
MyTree -L 4 -fL 2 -s

# 以「年月 -> 日」時間軸歸納檔案異動
MyTree -dt

# 排除 node 與 python 常見快取/套件目錄，且只顯示程式碼檔案
MyTree -eg node,py -ig code

# 使用正則表達式或副檔名自訂過濾，並匯出至檔案
MyTree -i .cs,Dockerfile,/\.(test|spec)\.ts$/ -o tree.txt
```

## 參數說明

| 參數 | 說明 |
| :--- | :--- |
| `-L <層數>` | 限制顯示的目錄深度（例如 `-L 5`） |
| `-fL <層數>` | 限制顯示檔案的深度（預設同 `-L`，需 `<= -L`） |
| `-dt`, `--date-tree` | 以「年月(`YYYYMM`) -> 日(`DD`)」作為根層級歸納檔案 |
| `-mt`, `--month-tree` | 以「年月(`YYYYMM`)」作為根層級歸納檔案 |
| `-dup`, `--duplicates` | 以「重覆檔名」為根層級，展開其散落的目錄樹（同步支援 `-dt` / `-mt`） |
| `-d` | 只顯示目錄，不顯示檔案（等同於 `-fL 0`） |
| `-a` | 顯示隱藏檔案與目錄 |
| `-s` | 顯示檔案大小 |
| `-o <檔名>` | 將結果匯出至檔案（UTF-8，自動移除顏色碼） |
| `-e`, `--exclude <規則>` | 排除指定的目錄或檔案（支援副檔名、完整名稱、`/.../` 正則） |
| `-eg <群組名>` | 套用排除群組（內建：`git`, `ide`, `node`, `py`, `dotnet`, `java`, `cpp`） |
| `-i`, `--include <規則>` | 只顯示符合的檔案（自動隱藏不含目標檔案的空目錄分支） |
| `-ig <群組名>` | 套用包含群組（內建：`node`, `py`, `dotnet`, `java`, `cpp`, `code`, `doc`, `media`, `config`, `test`） |
| `--no-default` | 停用預設自動排除的群組（`git`, `ide`） |
| `--groups` | 查看目前生效的設定檔路徑與所有群組清單 |
| `-h`, `--help` | 顯示說明訊息 |

## 設定檔 (`MyTree.ini`)

首次執行時，若執行檔所在目錄沒有 `MyTree.ini`，程式會自動產生一份預設設定檔。
- **讀取優先權**：若「當前工作目錄」下有 `MyTree.ini`，優先使用工作目錄設定；否則讀取執行檔所在目錄的全域設定檔。
- 可於設定檔中自訂 `[Defaults]`（預設層數與開關，含 `DupOnly`）、`[Colors]`（終端機配色，含 `Duplicate` 重覆標記顏色）、`[ExcludeGroups]` 與 `[IncludeGroups]`。
