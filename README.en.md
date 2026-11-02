# MyTree

> **Language**: [繁體中文](README.md) | **English**

A lightweight Windows CLI directory tree viewer and file cleanup utility written in a single C# source file with zero third-party dependencies.

Beyond standard directory trees, **MyTree** supports **timeline trees (grouped by year-month / date)**, **automatic duplicate filename detection & cleanup script generation (`-dup` / `-dc` / `-c`)**, **independent directory and file depth control (`-L` / `-fL`)**, **developer preset groups with wildcard / RegExp filtering**, and **extensible multi-language support via `.lang` files**.

---

## Why This Tool Exists?

1. **Visualizing File Structures**: Sometimes you need a clean directory tree to document or communicate the file composition of a project or folder.
2. **Overcoming Built-in `tree` Limitations**: The built-in Windows `tree` command only lets you print *all* files or *no* files at all. When a folder contains massive third-party libraries (such as `node_modules`, `.git`, `bin`/`obj`) or deeply nested directories, you rarely want them flooding your output. **MyTree** solves this with exclude/include preset groups (`-eg` / `-ig`), custom filters (`-e` / `-i`), and separate depth limits for directories (`-L`) and files (`-fL`).
3. **Reclaiming Disk Space & Cleaning Up Duplicates**: When system drive space runs low and you need to clean up files, hunting down duplicate files or browser download copies (e.g. `report (1).docx`, `file - Copy.pdf`) across folders is tedious. **MyTree** adds **duplicate detection (`-dup`)** and **safe cleanup batch script generation (`-dc` to keep the newest file per group, or `-c` to remove all matched files)**.

---

## Build Instructions

No Visual Studio or heavy SDK installation is required. Compile directly into a standalone `.exe` using the `.NET Framework` compiler built into Windows:

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /optimize+ /out:MyTree.exe MyTree.cs
```

---

## Quick Start

```powershell
# View current directory (auto-excludes .git, .vs, .vscode, .idea and marks "(dup)" files)
MyTree

# Group by duplicate filenames at root, expanding their directory locations (includes (1), - Copy variants)
MyTree -dup -t -s

# [Duplicate Cleanup -dc] Exclude backup folders and generate clean_<dir>.txt (keeps 1 newest per group, deletes the rest)
MyTree -e backup,archive -dup -dc

# [Full Cleanup -c] Filter a specific file series (via RegExp /.../ or wildcard *) and generate clean_<dir>.txt to delete ALL matches
MyTree -i "/^P\d+\.png$/" -c
MyTree -i "ChatGPT Image*.png" -L 1 -t -c

# Combine timeline tree with duplicate view: see when and where duplicate files were modified
MyTree -dt -dup

# Expand 4 directory levels, but only list files within the first 2 levels, showing timestamp & size
MyTree -L 4 -fL 2 -t -s

# Group files by "YearMonth (YYYYMM) -> Day (DD)" timeline
MyTree -dt

# Exclude common Node.js & Python cache/package folders and only show source code files
MyTree -eg node,py -ig code

# Export English language template to <ExecutableName>.lang for custom translation
MyTree -el
```

---

## Command-Line Options

| Option | Description |
| :--- | :--- |
| `-L <depth>` | Limit directory display depth (e.g., `-L 5`) |
| `-fL <depth>` | Limit file display depth (default: same as `-L`, must be `<= -L`) |
| `-dt`, `--date-tree` | Group files by `"YearMonth(YYYYMM) -> Day(DD)"` at the root level |
| `-mt`, `--month-tree` | Group files by `"YearMonth(YYYYMM)"` at the root level |
| `-dup`, `--duplicates` | Group by duplicate filenames at the root level, expanding their directory trees (normalizes `(1)`, `- Copy`, `- 複製` variants; works with `-dt` / `-mt`) |
| `-dc`, `--dup-clean` | Generate a duplicate cleanup list `clean_<dir>.txt` (**keeps 1 newest file per group**, generates `del` commands for the rest) |
| `-c`, `--clean` | Generate a cleanup list `clean_<dir>.txt` for **ALL matched files in scope** (**deletes all matched files without keeping any**; useful with `-i`) |
| `-d` | Show directories only, no files (equivalent to `-fL 0`) |
| `-a` | Show hidden files and directories |
| `-t`, `--time` | Show last modified time (`yyyy-MM-dd HH:mm`) |
| `-s` | Show file size (displayed after modified time when used with `-t`) |
| `-o <file>` | Export tree output to a UTF-8 file (ANSI colors stripped automatically) |
| `-e`, `--exclude <rule>` | Exclude specified directories or files (supports extensions, exact names, `*`/`?` wildcards, `/.../` RegExp) |
| `-eg <group>` | Apply exclude groups (built-in: `git`, `ide`, `node`, `py`, `dotnet`, `java`, `cpp`) |
| `-i`, `--include <rule>` | Include matching files only (supports extensions, exact names, `*`/`?` wildcards, `/.../` RegExp; auto-prunes empty directory branches) |
| `-ig <group>` | Apply include groups (built-in: `node`, `py`, `dotnet`, `java`, `cpp`, `code`, `doc`, `media`, `config`, `test`) |
| `--no-default` | Disable default exclude groups (`git`, `ide`) |
| `--groups` | Show active config path, active language, and all available filter groups |
| `-l`, `--lang <code\|file>` | Specify built-in language (`en` \| `zh-TW`) or path to a custom `.lang` file |
| `-el`, `--export-lang` | Export the English language template to `<ExecutableName>.lang` |
| `-h`, `--help` | Show help message |

---

## Multi-Language Support & Creating a Custom Language File (`.lang`)

**MyTree** includes built-in **English (`en`)** and **Traditional Chinese (`zh-TW`)** strings (automatically selected based on your OS UI language), and allows anyone to create or customize translations for any language without recompiling.

Note that both the configuration file (`.ini`) and language file (`.lang`) **dynamically match the executable filename**:
- If the executable is named `MyTree.exe`, it looks for `MyTree.ini` and `MyTree.lang`.
- If you rename `MyTree.exe` to `mt.exe`, it automatically looks for `mt.ini` and `mt.lang`.

### 1. Using Built-in English
- **Per-command**: Run `MyTree -l en ...`
- **Persistent via `.ini`**: Open `<ExecutableName>.ini` and set `Language = en` under `[Defaults]`.
- **Persistent via `.lang`**: Run `MyTree -el` to generate `<ExecutableName>.lang` (which contains all English strings) in the executable's directory; **MyTree** will automatically load it on startup.

### 2. Step-by-Step: Creating Your Own Custom Language (`.lang`)

#### Step 1: Export the English Language Template
Run the following command to export the complete English language dictionary:
```powershell
MyTree -el
# (or: MyTree --export-lang)
```
This creates `<ExecutableName>.lang` (e.g., `MyTree.lang`) in the same directory as the executable.

#### Step 2: Translate the `.lang` File
Open `<ExecutableName>.lang` in any text editor (UTF-8 encoding). Each entry is a `Key = "Value"` pair:

```ini
# MyTree Custom Language File (MyTree.lang)
DupTag = "(dup)"
DupCountTag = "(dup x{0})"
AccessDenied = "[Access Denied]"
SummaryDirs = "{0} directories"
SummaryFiles = ", {0} files"
SummaryDups = " ({0} duplicates)"
CopySuffixWords = "Copy, 複製, 副本"
HintFileDepthAdjusted = "[Hint] File depth (-fL {0}) exceeds max depth (-L {1}); auto-adjusted to {1}"
...
```

**Translation Guidelines:**
- **Keep Quotes Intact**: Keep the surrounding double quotes `"..."` so leading spaces (such as `", {0} files"`) and indentation are preserved.
- **Placeholders (`{0}`, `{1}`, `{2}`)**: Dynamic values are injected into `{0}`, `{1}`, etc. You may freely reorder `{0}` and `{1}` to match the natural grammar of your target language.
- **Localizing Copy Suffix Detection (`CopySuffixWords`)**: You can add your OS language's file-copy suffix words (comma-separated) to `CopySuffixWords`—for example, adding `Kopie` (German), `Copie` (French), or `コピー` (Japanese) so `file - Kopie.docx` is automatically recognized as a duplicate of `file.docx`!

#### Step 3: Apply Your Custom Language File
You can load your translated `.lang` file in either of two ways:
1. **Automatic Loading (Recommended)**:
   Keep the file named `<ExecutableName>.lang` (e.g., `MyTree.lang`) in either your current working directory or the directory where `MyTree.exe` resides. It will be loaded automatically every time you run `MyTree`.
2. **Explicit Path via `-l` / `--lang`**:
   Save the file under any name (e.g., `ja.lang`, `de.lang`) and pass its path with `-l`:
   ```powershell
   MyTree -l ja.lang -dup -t
   ```
   *(Tip: Run `MyTree --groups` to verify which `.ini` and `.lang` files are currently active.)*

---

## Configuration File (`<ExecutableName>.ini`)

On first run, if `<ExecutableName>.ini` does not exist in the executable's directory, **MyTree** generates a default configuration file automatically.
- **Loading Priority**: If `<ExecutableName>.ini` exists in the **current working directory**, it takes precedence; otherwise, the global config in the **executable's directory** is used.
- **Customizable Sections**:
  - `[Defaults]`: `Language` (`auto` | `zh-TW` | `en`), `MaxDepth`, `MaxFileDepth`, `ShowSize`, `ShowTime`, `ShowHidden`, `DirsOnly`, `DupOnly`, `DateTreeMode`, `DefaultExcludeGroups`
  - `[Colors]`: Customize console colors for `Root`, `Directory`, `Executable`, `Size`, `Time`, `Summary`, `DateNode`, and `Duplicate`
  - `[ExcludeGroups]` & `[IncludeGroups]`: Define or customize reusable filter groups.
