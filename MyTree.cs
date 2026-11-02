using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

class FilterRule
{
    public string RawText;
    public int RuleType; // 0: 完整檔名/目錄名, 1: 副檔名, 2: RegExp (/.../ 或萬用字元 * ?)
    public string Value;
    public Regex RegexPattern;

    public FilterRule(string raw)
    {
        RawText = raw.Trim();
        if (RawText.Length > 2 && RawText.StartsWith("/") && RawText.EndsWith("/"))
        {
            RuleType = 2;
            string pattern = RawText.Substring(1, RawText.Length - 2);
            RegexPattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
        else if (RawText.StartsWith("*.") && RawText.IndexOf('*', 2) < 0 && !RawText.Contains("?"))
        {
            RuleType = 1;
            Value = RawText.Substring(1);
        }
        else if (RawText.StartsWith(".") && !RawText.Contains("/") && !RawText.Contains("\\") && !RawText.Contains("*") && !RawText.Contains("?"))
        {
            RuleType = 1;
            Value = RawText;
        }
        else if (RawText.Contains("*") || RawText.Contains("?"))
        {
            RuleType = 2;
            string pattern = "^" + Regex.Escape(RawText).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            RegexPattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
        else
        {
            RuleType = 0;
            Value = RawText;
        }
    }

    public bool Matches(FileSystemInfo info)
    {
        if (RuleType == 2)
        {
            return RegexPattern.IsMatch(info.Name);
        }
        else if (RuleType == 1)
        {
            if (string.Equals(info.Extension, Value, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(info.Name, Value, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
        else
        {
            if (string.Equals(info.Name, Value, StringComparison.OrdinalIgnoreCase))
                return true;
            if (info is FileInfo && string.Equals(Program.GetCanonicalFileName(info.Name), Value, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
    }
}

// 用於 -dt、-mt 與 -dup 模式的虛擬樹節點
class VNode
{
    public string Name;
    public int NodeType; // 0: 虛擬日期節點 (無括號), 1: 實體目錄 ([名稱]), 2: 實體檔案, 3: 虛擬重覆檔名節點
    public FileInfo FileRef;
    public List<FileInfo> DupFiles = new List<FileInfo>();
    public string DirFullPath;
    public Dictionary<string, VNode> Children = new Dictionary<string, VNode>(StringComparer.OrdinalIgnoreCase);

    public VNode(string name, int nodeType)
    {
        Name = name;
        NodeType = nodeType;
    }

    public VNode GetOrAddChild(string key, string displayName, int nodeType, string dirFullPath)
    {
        VNode child;
        if (!Children.TryGetValue(key, out child))
        {
            child = new VNode(displayName, nodeType);
            child.DirFullPath = dirFullPath;
            Children[key] = child;
        }
        return child;
    }
}

class Program
{
    static string appName = "MyTree";
    static string configFileName = "MyTree.ini";
    static string langFileName = "MyTree.lang";
    static string loadedConfigPath = "";
    static string loadedLangPath = "";
    static string currentLang = "auto";

    static int maxDepth = int.MaxValue;
    static int maxFileDepth = int.MaxValue;
    static bool fileDepthSpecified = false;
    static bool showHidden = false;
    static bool dirsOnly = false;
    static bool showSize = false;
    static bool showTime = false;
    static bool dupOnly = false;
    static int cleanMode = 0; // 0: 不產生, 1: -dc (重覆清理：保留最新), 2: -c (全數清理：刪除檢視範圍內所有符合檔案)
    static bool useDefaultExcludes = true;
    static int dateMode = 0; // 0: 一般模式, 1: -dt (年月+日), 2: -mt (僅年月)

    static List<string> defaultExcludeGroupNames = new List<string> { "git", "ide" };

    // 配色設定
    static ConsoleColor colorRoot = ConsoleColor.Cyan;
    static ConsoleColor colorDir = ConsoleColor.Blue;
    static ConsoleColor colorExe = ConsoleColor.Green;
    static ConsoleColor colorSize = ConsoleColor.DarkGray;
    static ConsoleColor colorTime = ConsoleColor.DarkCyan;
    static ConsoleColor colorSummary = ConsoleColor.Green;
    static ConsoleColor colorDate = ConsoleColor.Yellow;
    static ConsoleColor colorDup = ConsoleColor.Magenta;

    // 實際生效的過濾規則
    static List<FilterRule> excludeRules = new List<FilterRule>();
    static List<FilterRule> includeRules = new List<FilterRule>();

    // 群組字典
    static Dictionary<string, string[]> excludeGroups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, string[]> includeGroups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    // 多語系字典與順序清單
    static Dictionary<string, string> langStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static List<string> langKeysOrder = new List<string>();

    static string outputFile = null;
    static StreamWriter fileWriter = null;
    static bool useColor = true;

    static int dirCount = 0;
    static int fileCount = 0;
    static int dupFileCount = 0;
    static HashSet<string> uniqueDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, int> fileNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, List<FileInfo>> fileGroups = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
    static List<FileInfo> allMatchedFiles = new List<FileInfo>();

    // 比對常見的副本尾綴：如 " (1)", "(2)", "（1）", " - 複製", " - 副本", " - Copy"
    static Regex copySuffixRegex = new Regex(
        @"^(?<base>.+?)(?:\s*(?:\([0-9]{1,3}\)|（[0-9]{1,3}）)|\s*-\s*(?:複製|副本|Copy))$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 1. 動態取得執行檔名稱與對應的 .ini / .lang 檔名
        InitAppIdentity();

        // 2. 初始化預設語系（依系統語系自動選擇 zh-TW 或 en）
        InitLanguage("auto");

        // 3. 載入內建預設群組，並處理設定檔與外部 .lang 語系檔
        InitBuiltInGroups();
        HandleConfigLoading();
        HandleLangFileLoading();

        string targetPath = ".";
        string cliLang = null;
        bool exportLangRequested = false;
        List<string> selectedEg = new List<string>();
        List<string> selectedIg = new List<string>();
        List<string> cliExcludes = new List<string>();
        List<string> cliIncludes = new List<string>();

        // 4. 先掃描是否有 -l/--lang 或 -el/--export-lang，確保說明與警告訊息使用正確語系
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if ((arg == "-l" || arg == "--lang" || arg == "-lang") && i + 1 < args.Length)
            {
                cliLang = args[++i];
            }
            else if (arg == "-el" || arg == "--export-lang" || arg == "-export_lang" || arg == "--export_lang")
            {
                exportLangRequested = true;
            }
        }

        if (!string.IsNullOrEmpty(cliLang))
        {
            if (File.Exists(cliLang))
                LoadLangFile(cliLang);
            else
                InitLanguage(cliLang);
        }

        if (exportLangRequested)
        {
            ExportEnglishLangTemplate();
            return;
        }

        // 5. 解析命令列參數
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            int d;
            int fd;
            if ((arg == "-l" || arg == "--lang" || arg == "-lang") && i + 1 < args.Length)
            {
                i++; // 已處理
            }
            else if (arg == "-L" && i + 1 < args.Length && int.TryParse(args[++i], out d))
            {
                maxDepth = d > 0 ? d : int.MaxValue;
            }
            else if ((arg == "-fL" || arg == "--file-level") && i + 1 < args.Length && int.TryParse(args[++i], out fd))
            {
                maxFileDepth = fd;
                fileDepthSpecified = true;
            }
            else if (arg == "-dt" || arg == "--date-tree")
                dateMode = 1;
            else if (arg == "-mt" || arg == "--month-tree")
                dateMode = 2;
            else if (arg == "-dup" || arg == "--dup" || arg == "--duplicates")
                dupOnly = true;
            else if (arg == "-dc" || arg == "--dup-clean")
                cleanMode = 1;
            else if (arg == "-c" || arg == "--clean")
                cleanMode = 2;
            else if (arg == "-a")
                showHidden = true;
            else if (arg == "-d")
                dirsOnly = true;
            else if (arg == "-s")
                showSize = true;
            else if (arg == "-t" || arg == "--time")
                showTime = true;
            else if (arg == "--no-default")
                useDefaultExcludes = false;
            else if ((arg == "-e" || arg == "--exclude" || arg == "-I") && i + 1 < args.Length)
            {
                foreach (var item in SplitRules(args[++i]))
                    cliExcludes.Add(item);
            }
            else if ((arg == "-eg" || arg == "--exclude-group") && i + 1 < args.Length)
            {
                foreach (var g in args[++i].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    selectedEg.Add(g.Trim());
            }
            else if ((arg == "-i" || arg == "--include") && i + 1 < args.Length)
            {
                foreach (var item in SplitRules(args[++i]))
                    cliIncludes.Add(item);
            }
            else if ((arg == "-ig" || arg == "--include-group") && i + 1 < args.Length)
            {
                foreach (var g in args[++i].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    selectedIg.Add(g.Trim());
            }
            else if (arg == "-o" && i + 1 < args.Length)
                outputFile = args[++i];
            else if (arg == "--groups")
            {
                PrintGroups();
                return;
            }
            else if (arg == "-h" || arg == "--help" || arg == "/?")
            {
                PrintHelp();
                return;
            }
            else if (!arg.StartsWith("-"))
                targetPath = arg;
        }

        if (Console.IsOutputRedirected || !string.IsNullOrEmpty(outputFile))
            useColor = false;

        // 6. 處理檔案層數 (-fL) 與目錄層數 (-L) 的同步與防呆限制
        if (!fileDepthSpecified)
        {
            maxFileDepth = maxDepth;
        }
        else if (maxFileDepth > maxDepth)
        {
            WriteColorLine(TF("HintFileDepthAdjusted", maxFileDepth, maxDepth), ConsoleColor.Yellow);
            maxFileDepth = maxDepth;
        }
        else if (maxFileDepth <= 0)
        {
            dirsOnly = true;
        }

        if ((dupOnly || cleanMode != 0) && dirsOnly)
        {
            dirsOnly = false;
            if (maxFileDepth <= 0) maxFileDepth = maxDepth;
        }

        // 7. 套用預設排除群組
        if (useDefaultExcludes)
        {
            foreach (var defGroup in defaultExcludeGroupNames)
                ApplyExcludeGroup(defGroup);
        }

        // 8. 套用使用者指定的排除與包含群組 (-eg / -ig)
        foreach (var g in selectedEg)
        {
            if (!ApplyExcludeGroup(g))
                WriteColorLine(TF("WarnUnknownExcludeGroup", g), ConsoleColor.Yellow);
        }
        foreach (var g in selectedIg)
        {
            if (!ApplyIncludeGroup(g))
                WriteColorLine(TF("WarnUnknownIncludeGroup", g), ConsoleColor.Yellow);
        }

        // 9. 套用命令列直接指定的單一規則 (-e / -i)
        foreach (var raw in cliExcludes) AddRule(excludeRules, raw);
        foreach (var raw in cliIncludes) AddRule(includeRules, BuildIncludeRawString(raw));

        if (!Directory.Exists(targetPath))
        {
            WriteColorLine(TF("ErrPathNotFound", targetPath), ConsoleColor.Red);
            return;
        }

        try
        {
            if (!string.IsNullOrEmpty(outputFile))
                fileWriter = new StreamWriter(outputFile, false, Encoding.UTF8);

            DirectoryInfo rootDir = new DirectoryInfo(targetPath);

            // 10. 預先掃描檢視範圍內的基底檔名出現次數，供重覆檔名標示、-dup、-dc 與 -c 模式使用
            if (!dirsOnly)
            {
                ScanFileNameCounts(rootDir, 1);
            }

            WriteColorLine(rootDir.FullName, colorRoot);

            if (dupOnly)
            {
                VNode vRoot = new VNode("ROOT", 0);
                CollectDupTree(rootDir, 1, new List<DirectoryInfo>(), vRoot, rootDir);
                PrintVNodeChildren(vRoot, "");
                dirCount = uniqueDirs.Count;
            }
            else if (dateMode == 0)
            {
                PrintTree(rootDir, "", 1);
            }
            else
            {
                VNode vRoot = new VNode("ROOT", 0);
                CollectDateTree(rootDir, 1, new List<DirectoryInfo>(), vRoot);
                PrintVNodeChildren(vRoot, "");
                dirCount = uniqueDirs.Count;
            }

            WriteLine("");
            string summary = TF("SummaryDirs", dirCount)
                + (dirsOnly ? "" : TF("SummaryFiles", fileCount))
                + (dupFileCount > 0 ? TF("SummaryDups", dupFileCount) : "");
            WriteColorLine(summary, colorSummary);

            if (cleanMode == 1)
            {
                GenerateDupCleanFile(rootDir);
            }
            else if (cleanMode == 2)
            {
                GenerateAllCleanFile(rootDir);
            }
        }
        catch (Exception ex)
        {
            WriteColorLine(TF("ErrExecution", ex.Message), ConsoleColor.Red);
        }
        finally
        {
            if (fileWriter != null)
            {
                fileWriter.Flush();
                fileWriter.Close();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n" + TF("ExportedTreeMsg", outputFile));
                Console.ResetColor();
            }
        }
    }

    // 取得語系字串
    static string T(string key)
    {
        string val;
        if (langStrings.TryGetValue(key, out val))
            return val;
        return key;
    }

    // 取得並格式化帶參數 ({0}, {1}...) 的語系字串
    static string TF(string key, params object[] args)
    {
        string pattern = T(key);
        try
        {
            return string.Format(pattern, args);
        }
        catch
        {
            return pattern;
        }
    }

    static void SetLangEntry(Dictionary<string, string> dict, string key, string val)
    {
        if (!dict.ContainsKey(key) && !langKeysOrder.Contains(key))
            langKeysOrder.Add(key);
        dict[key] = val;
    }

    static Dictionary<string, string> BuildEnglishDictionary()
    {
        Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Tree display & summary
        SetLangEntry(d, "DupTag", "(dup)");
        SetLangEntry(d, "DupCountTag", "(dup x{0})");
        SetLangEntry(d, "AccessDenied", "[Access Denied]");
        SetLangEntry(d, "SummaryDirs", "{0} directories");
        SetLangEntry(d, "SummaryFiles", ", {0} files");
        SetLangEntry(d, "SummaryDups", " ({0} duplicates)");
        SetLangEntry(d, "ExportedTreeMsg", "[Tree exported to: {0}]");
        SetLangEntry(d, "CopySuffixWords", "Copy, 複製, 副本");

        // Warnings & errors
        SetLangEntry(d, "HintFileDepthAdjusted", "[Hint] File depth (-fL {0}) exceeds max depth (-L {1}); auto-adjusted to {1}");
        SetLangEntry(d, "WarnUnknownExcludeGroup", "[Warn] Unknown exclude group: '{0}' (use --groups to list)");
        SetLangEntry(d, "WarnUnknownIncludeGroup", "[Warn] Unknown include group: '{0}' (use --groups to list)");
        SetLangEntry(d, "WarnConfigLoadFailed", "[Warn] Failed to load config ({0}): {1}");
        SetLangEntry(d, "WarnInvalidRule", "[Warn] Invalid filter rule or RegExp '{0}': {1}");
        SetLangEntry(d, "ErrPathNotFound", "Error: Path not found '{0}'");
        SetLangEntry(d, "ErrExecution", "Execution error: {0}");
        SetLangEntry(d, "ExportedLangMsg", "[English language template exported to: {0}]");

        // Duplicate / All cleanup script (-dc / -c)
        SetLangEntry(d, "CleanNoDupsHint", "[Hint] No duplicate files found in scope; cleanup list was not generated.");
        SetLangEntry(d, "CleanNoFilesHint", "[Hint] No matching files found in scope; cleanup list was not generated.");
        SetLangEntry(d, "CleanBannerTitle", "{0} Duplicate File Cleanup List (-dc)");
        SetLangEntry(d, "CleanAllBannerTitle", "{0} Matched File Cleanup List (-c)");
        SetLangEntry(d, "CleanBannerTarget", "Target Path : {0}");
        SetLangEntry(d, "CleanBannerTime", "Generated At: {0}");
        SetLangEntry(d, "CleanBannerRule", "Keep Policy : Keep the newest file per group (prefer original name without (1)/Copy on tie)");
        SetLangEntry(d, "CleanAllBannerRule", "Delete Policy: Delete ALL matched files listed below (NO files kept)");
        SetLangEntry(d, "CleanBannerUsage", "Instructions: Review the list below, then rename this file from .txt to .bat to execute");
        SetLangEntry(d, "CleanGroupHeader", "[Group {0}] {1} ({2} files)");
        SetLangEntry(d, "CleanKeepLine", "  [KEEP NEWEST] ({0} | {1}) \"{2}\"");
        SetLangEntry(d, "CleanDeleteLine", "  [DELETE]      ({0} | {1})");
        SetLangEntry(d, "CleanFooterSummary", "Total: keeping {0} newest files, deleting {1} duplicate files (~{2} to free)");
        SetLangEntry(d, "CleanAllFooterSummary", "Total: deleting {0} matched files (~{1} to free)");
        SetLangEntry(d, "CleanDoneEcho", "Cleanup complete!");
        SetLangEntry(d, "CleanConsoleTitle", "[Cleanup list generated: {0}]");
        SetLangEntry(d, "CleanConsoleStats", " -> {0} duplicate groups: keeping {0} newest, deleting {1} old/copy files (~{2})");
        SetLangEntry(d, "CleanAllConsoleStats", " -> Deleting all {0} matched files in scope (~{1})");
        SetLangEntry(d, "CleanConsoleHint", " -> Review the .txt file first, then rename it to .bat to execute.");

        // Groups & help output
        SetLangEntry(d, "GroupsActiveConfig", "[Active Config] {0}");
        SetLangEntry(d, "GroupsActiveLang", "[Active Language] {0}");
        SetLangEntry(d, "GroupsDefaultExclude", "[Default Exclude Groups] {0}");
        SetLangEntry(d, "GroupsNone", "(none)");
        SetLangEntry(d, "GroupsExcludeHeader", "=== Exclude Groups (-eg <group>) ===");
        SetLangEntry(d, "GroupsIncludeHeader", "=== Include Groups (-ig <group>) ===");

        SetLangEntry(d, "HelpUsage", "Usage: {0} [path] [options]");
        SetLangEntry(d, "HelpConfig", "Config & Lang: Auto-matched to executable name as {0} & {1} (current dir first, fallback to exe dir)");
        SetLangEntry(d, "HelpSectionBasic", "Basic & Timeline Options:");
        SetLangEntry(d, "HelpOptL", "  -L <depth>             Limit directory display depth (e.g. -L 5)");
        SetLangEntry(d, "HelpOptFL", "  -fL <depth>            Limit file display depth (default: same as -L, must be <= -L)");
        SetLangEntry(d, "HelpOptDt", "  -dt, --date-tree       Group files by \"YearMonth(YYYYMM) -> Day(DD)\" at root");
        SetLangEntry(d, "HelpOptMt", "  -mt, --month-tree      Group files by \"YearMonth(YYYYMM)\" at root");
        SetLangEntry(d, "HelpOptDup", "  -dup, --duplicates     Group by duplicate filenames at root, expanding their directory trees");
        SetLangEntry(d, "HelpOptDc", "  -dc, --dup-clean       Generate duplicate cleanup list clean_<dir>.txt (keep newest 1, del rest)");
        SetLangEntry(d, "HelpOptC", "  -c, --clean            Generate cleanup list clean_<dir>.txt to delete ALL matched files in scope");
        SetLangEntry(d, "HelpOptD", "  -d                     Show directories only (equivalent to -fL 0)");
        SetLangEntry(d, "HelpOptA", "  -a                     Show hidden files and directories");
        SetLangEntry(d, "HelpOptT", "  -t, --time             Show last modified time (yyyy-MM-dd HH:mm)");
        SetLangEntry(d, "HelpOptS", "  -s                     Show file size (shown after time when used with -t)");
        SetLangEntry(d, "HelpOptO", "  -o <file>              Export tree output to file (colors stripped automatically)");
        SetLangEntry(d, "HelpSectionFilter", "Filter & Group Options (supports extensions, exact/wildcard names, and /.../ RegExp):");
        SetLangEntry(d, "HelpOptE", "  -e, --exclude <rule>   Exclude directories or files (e.g. -e temp,logs,/^bak_/)");
        SetLangEntry(d, "HelpOptEg", "  -eg <group>            Apply exclude groups (e.g. -eg node,py)");
        SetLangEntry(d, "HelpOptI", "  -i, --include <rule>   Include matching files only (e.g. -i .cs,P*.png,/\\.test\\.ts$/)");
        SetLangEntry(d, "HelpOptIg", "  -ig <group>            Apply include groups (e.g. -ig code,doc,test)");
        SetLangEntry(d, "HelpOptNoDefault", "  --no-default           Disable default exclude groups");
        SetLangEntry(d, "HelpOptGroups", "  --groups               Show active config path and all available groups");
        SetLangEntry(d, "HelpOptLang", "  -l, --lang <code|file> Specify language (en|zh-TW) or .lang path (auto-loads {0} if present)");
        SetLangEntry(d, "HelpOptExportLang", "  -el, --export-lang     Export English language template to {0} (matches executable name)");
        SetLangEntry(d, "HelpOptH", "  -h, --help             Show this help message");

        return d;
    }

    static Dictionary<string, string> BuildTraditionalChineseDictionary()
    {
        Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 樹狀圖標記與摘要
        SetLangEntry(d, "DupTag", "(重覆)");
        SetLangEntry(d, "DupCountTag", "(重覆 x{0})");
        SetLangEntry(d, "AccessDenied", "[拒絕存取]");
        SetLangEntry(d, "SummaryDirs", "{0} 個目錄");
        SetLangEntry(d, "SummaryFiles", ", {0} 個檔案");
        SetLangEntry(d, "SummaryDups", " (含 {0} 個重覆檔案)");
        SetLangEntry(d, "ExportedTreeMsg", "[已將樹狀結構匯出至: {0}]");
        SetLangEntry(d, "CopySuffixWords", "Copy, 複製, 副本");

        // 警告與錯誤訊息
        SetLangEntry(d, "HintFileDepthAdjusted", "[提示] 檔案顯示層數 (-fL {0}) 大於目錄層數 (-L {1})，已自動調整為 {1}");
        SetLangEntry(d, "WarnUnknownExcludeGroup", "[警告] 未知的排除群組: '{0}' (可使用 --groups 查看支援清單)");
        SetLangEntry(d, "WarnUnknownIncludeGroup", "[警告] 未知的包含群組: '{0}' (可使用 --groups 查看支援清單)");
        SetLangEntry(d, "WarnConfigLoadFailed", "[警告] 讀取設定檔 ({0}) 失敗: {1}");
        SetLangEntry(d, "WarnInvalidRule", "[警告] 無效的過濾規則或 RegExp '{0}': {1}");
        SetLangEntry(d, "ErrPathNotFound", "錯誤: 找不到路徑 '{0}'");
        SetLangEntry(d, "ErrExecution", "執行發生錯誤: {0}");
        SetLangEntry(d, "ExportedLangMsg", "[已將英文語系範本匯出至: {0}]");

        // 重覆檔案與全數清理清單 (-dc / -c)
        SetLangEntry(d, "CleanNoDupsHint", "[提示] 檢視範圍內無重覆檔案，未產生清理指令檔。");
        SetLangEntry(d, "CleanNoFilesHint", "[提示] 檢視範圍內無符合的檔案，未產生清理指令檔。");
        SetLangEntry(d, "CleanBannerTitle", "{0} 重覆檔案清理清單 (-dc)");
        SetLangEntry(d, "CleanAllBannerTitle", "{0} 符合條件檔案全數清理清單 (-c)");
        SetLangEntry(d, "CleanBannerTarget", "掃描目標: {0}");
        SetLangEntry(d, "CleanBannerTime", "產生時間: {0}");
        SetLangEntry(d, "CleanBannerRule", "保留規則: 每組重覆檔名保留「最後異動時間最新」的 1 個檔案（若時間相同優先保留無 (1)/複製 尾綴之原檔）");
        SetLangEntry(d, "CleanAllBannerRule", "刪除規則: 將本次篩選範圍內的檔案「全數刪除」（不保留最新檔，常搭配 -i 指定欲清除的檔案系列）");
        SetLangEntry(d, "CleanBannerUsage", "使用方式: 請先檢視下方清單，確認無誤後將此檔案副檔名由 .txt 改為 .bat 再執行");
        SetLangEntry(d, "CleanGroupHeader", "[群組 {0}] {1} (共 {2} 個檔案)");
        SetLangEntry(d, "CleanKeepLine", "  [保留最新] ({0} | {1}) \"{2}\"");
        SetLangEntry(d, "CleanDeleteLine", "  [預計刪除] ({0} | {1})");
        SetLangEntry(d, "CleanFooterSummary", "共計保留 {0} 個最新檔案，預計刪除 {1} 個重覆檔案 (約可釋放 {2})");
        SetLangEntry(d, "CleanAllFooterSummary", "共計預計刪除 {0} 個符合條件的檔案 (約可釋放 {1})");
        SetLangEntry(d, "CleanDoneEcho", "清理完成！");
        SetLangEntry(d, "CleanConsoleTitle", "[已產生檔案清理清單: {0}]");
        SetLangEntry(d, "CleanConsoleStats", " -> 共 {0} 組重覆檔名：保留最新 {0} 個，預計刪除 {1} 個舊檔/副本 (約 {2})");
        SetLangEntry(d, "CleanAllConsoleStats", " -> 全數清理模式：預計刪除檢視範圍內全部 {0} 個檔案 (約 {1})");
        SetLangEntry(d, "CleanConsoleHint", " -> 請先開啟該 .txt 確認內容，無誤後改名為 .bat 即可執行清理。");

        // 群組清單與說明選單
        SetLangEntry(d, "GroupsActiveConfig", "[生效設定檔] {0}");
        SetLangEntry(d, "GroupsActiveLang", "[生效語系] {0}");
        SetLangEntry(d, "GroupsDefaultExclude", "[預設排除群組] {0}");
        SetLangEntry(d, "GroupsNone", "(無)");
        SetLangEntry(d, "GroupsExcludeHeader", "=== 排除群組 (-eg <群組名>) ===");
        SetLangEntry(d, "GroupsIncludeHeader", "=== 包含群組 (-ig <群組名>) ===");

        SetLangEntry(d, "HelpUsage", "用法: {0} [目錄路徑] [選項]");
        SetLangEntry(d, "HelpConfig", "設定與語系檔: 自動依執行檔名對應為 {0} 與 {1} (工作目錄優先，若無則讀取執行檔所在目錄)");
        SetLangEntry(d, "HelpSectionBasic", "基本與時間軸選項:");
        SetLangEntry(d, "HelpOptL", "  -L <層數>              限制顯示的目錄深度 (例如: -L 5)");
        SetLangEntry(d, "HelpOptFL", "  -fL <層數>             限制顯示檔案的深度 (預設同 -L，需 <= -L，如: -L 5 -fL 2)");
        SetLangEntry(d, "HelpOptDt", "  -dt, --date-tree       以「年月(YYYYMM) -> 日(DD)」作為根層級歸納檔案");
        SetLangEntry(d, "HelpOptMt", "  -mt, --month-tree      以「年月(YYYYMM)」作為根層級歸納檔案");
        SetLangEntry(d, "HelpOptDup", "  -dup, --duplicates     以「重覆檔名」為根層級，展開其散落的目錄樹 (同步支援 -dt / -mt)");
        SetLangEntry(d, "HelpOptDc", "  -dc, --dup-clean       產生重覆檔案清理清單 clean_<目錄名>.txt (每組保留最新 1 個，其餘 del)");
        SetLangEntry(d, "HelpOptC", "  -c, --clean            產生全數清理清單 clean_<目錄名>.txt (將篩選出的檔案全部 del，可搭配 -i)");
        SetLangEntry(d, "HelpOptD", "  -d                     只顯示目錄，不顯示檔案 (等同於 -fL 0)");
        SetLangEntry(d, "HelpOptA", "  -a                     顯示隱藏檔案與目錄");
        SetLangEntry(d, "HelpOptT", "  -t, --time             顯示檔案最後異動時間 (格式: yyyy-MM-dd HH:mm)");
        SetLangEntry(d, "HelpOptS", "  -s                     顯示檔案大小 (若與 -t 同時使用，先顯示時間再顯示大小)");
        SetLangEntry(d, "HelpOptO", "  -o <檔名>              將結果匯出至檔案 (自動移除顏色碼)");
        SetLangEntry(d, "HelpSectionFilter", "過濾與群組選項 (支援 副檔名、完整名稱、* 萬用字元、以及 /.../ 正則表達式):");
        SetLangEntry(d, "HelpOptE", "  -e, --exclude <規則>   排除指定的目錄或檔案 (如: -e temp,logs,/^bak_/)");
        SetLangEntry(d, "HelpOptEg", "  -eg <群組名>           套用排除群組 (如: -eg node,py)");
        SetLangEntry(d, "HelpOptI", "  -i, --include <規則>   只顯示符合的檔案 (如: -i .cs,P*.png,/\\.test\\.ts$/)");
        SetLangEntry(d, "HelpOptIg", "  -ig <群組名>           套用包含群組 (如: -ig code,doc,test)");
        SetLangEntry(d, "HelpOptNoDefault", "  --no-default           停用預設自動排除的群組");
        SetLangEntry(d, "HelpOptGroups", "  --groups               查看目前生效的設定檔路徑與所有群組清單");
        SetLangEntry(d, "HelpOptLang", "  -l, --lang <語系|檔名> 指定語系 (en|zh-TW) 或自訂 .lang 路徑 (預設自動載入同名 {0})");
        SetLangEntry(d, "HelpOptExportLang", "  -el, --export-lang     將英文語系範本輸出至與執行檔同名的 {0} 供自訂翻譯");
        SetLangEntry(d, "HelpOptH", "  -h, --help             顯示此說明訊息");

        return d;
    }

    static void InitLanguage(string langCode)
    {
        bool useZh = false;
        if (string.IsNullOrEmpty(langCode) || langCode.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            string uiName = CultureInfo.CurrentUICulture.Name;
            useZh = uiName.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            currentLang = useZh ? "zh-TW (auto)" : "en (auto)";
        }
        else if (langCode.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                 langCode.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
                 langCode.Equals("cht", StringComparison.OrdinalIgnoreCase))
        {
            useZh = true;
            currentLang = "zh-TW";
        }
        else
        {
            useZh = false;
            currentLang = "en";
        }

        // 先載入英文基底確保順序與所有 Key 完整，若為繁中再覆蓋
        langStrings = BuildEnglishDictionary();
        if (useZh)
        {
            Dictionary<string, string> zhDict = BuildTraditionalChineseDictionary();
            foreach (var kvp in zhDict)
                langStrings[kvp.Key] = kvp.Value;
        }

        RebuildCopySuffixRegex();
    }

    static void RebuildCopySuffixRegex()
    {
        string rawWords = T("CopySuffixWords");
        List<string> escapedWords = new List<string>();
        foreach (string w in rawWords.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = w.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                escapedWords.Add(Regex.Escape(trimmed));
        }

        string copyPart = escapedWords.Count > 0 ? string.Join("|", escapedWords.ToArray()) : "Copy|複製|副本";
        string pattern = string.Format(@"^(?<base>.+?)(?:\s*(?:\([0-9]{{1,3}}\)|（[0-9]{{1,3}}）)|\s*-\s*(?:{0}))$", copyPart);
        copySuffixRegex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    static void HandleLangFileLoading()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string globalLang = Path.Combine(exeDir, langFileName);
        string localLang = Path.Combine(Environment.CurrentDirectory, langFileName);

        if (File.Exists(localLang))
        {
            LoadLangFile(localLang);
        }
        else if (File.Exists(globalLang))
        {
            LoadLangFile(globalLang);
        }
    }

    static void LoadLangFile(string path)
    {
        try
        {
            foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";") || (line.StartsWith("[") && line.EndsWith("]")))
                    continue;

                int eqIdx = line.IndexOf('=');
                if (eqIdx <= 0) continue;

                string key = line.Substring(0, eqIdx).Trim();
                string val = line.Substring(eqIdx + 1);

                string trimmedVal = val.Trim();
                if (trimmedVal.Length >= 2 && trimmedVal.StartsWith("\"") && trimmedVal.EndsWith("\""))
                {
                    trimmedVal = trimmedVal.Substring(1, trimmedVal.Length - 2).Replace("\\\"", "\"");
                }

                langStrings[key] = trimmedVal;
            }
            loadedLangPath = Path.GetFullPath(path);
            currentLang = loadedLangPath;
            RebuildCopySuffixRegex();
        }
        catch { }
    }

    static void ExportEnglishLangTemplate()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string targetPath = Path.Combine(exeDir, langFileName);
        Dictionary<string, string> enDict = BuildEnglishDictionary();

        try
        {
            using (StreamWriter sw = new StreamWriter(targetPath, false, Encoding.UTF8))
            {
                sw.WriteLine(string.Format("# {0} Custom Language File ({1})", appName, langFileName));
                sw.WriteLine("# Placement Priority: Current working directory first, then executable directory.");
                sw.WriteLine("# Formatting Notes:");
                sw.WriteLine("#   - Values are enclosed in double quotes \"...\" so leading spaces are preserved.");
                sw.WriteLine("#   - Keep placeholders like {0}, {1}, {2} intact; you may reorder them to fit your language's grammar.");
                sw.WriteLine("");

                foreach (string key in langKeysOrder)
                {
                    string val;
                    if (!enDict.TryGetValue(key, out val)) continue;
                    string escaped = val.Replace("\"", "\\\"");
                    sw.WriteLine(string.Format("{0} = \"{1}\"", key, escaped));
                }
            }

            WriteColorLine(TF("ExportedLangMsg", Path.GetFullPath(targetPath)), ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            WriteColorLine(TF("ErrExecution", ex.Message), ConsoleColor.Red);
        }
    }

    // 將含有 (1)、(2)、（1）、- 複製、- Copy 等副本尾綴的檔名正規化為基底檔名
    public static string GetCanonicalFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return fileName;
        string ext = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(stem)) return fileName;

        while (true)
        {
            Match m = copySuffixRegex.Match(stem);
            if (!m.Success) break;
            string nextStem = m.Groups["base"].Value.TrimEnd();
            if (string.IsNullOrEmpty(nextStem)) break;
            stem = nextStem;
        }

        return stem + ext;
    }

    // 預先統計檢視範圍內各基底檔名出現次數（不分大小寫）並收集檔案清單
    static void ScanFileNameCounts(DirectoryInfo dir, int depth)
    {
        if (depth > maxDepth || depth > maxFileDepth) return;
        try
        {
            foreach (var f in dir.GetFiles())
            {
                if (ShouldExclude(f) || !ShouldIncludeFile(f)) continue;
                allMatchedFiles.Add(f);

                string key = GetCanonicalFileName(f.Name);
                int count;
                if (fileNameCounts.TryGetValue(key, out count))
                    fileNameCounts[key] = count + 1;
                else
                    fileNameCounts[key] = 1;

                List<FileInfo> groupList;
                if (!fileGroups.TryGetValue(key, out groupList))
                {
                    groupList = new List<FileInfo>();
                    fileGroups[key] = groupList;
                }
                groupList.Add(f);
            }

            if (depth + 1 <= maxDepth && depth + 1 <= maxFileDepth)
            {
                foreach (var d in dir.GetDirectories())
                {
                    if (ShouldExclude(d)) continue;
                    ScanFileNameCounts(d, depth + 1);
                }
            }
        }
        catch { }
    }

    static string GetCleanScriptFilePath(DirectoryInfo rootDir)
    {
        string rawDirName = rootDir.Name;
        if (string.IsNullOrEmpty(rawDirName) || rawDirName == "\\" || rawDirName == "/")
            rawDirName = "root";
        foreach (char invalid in Path.GetInvalidFileNameChars())
            rawDirName = rawDirName.Replace(invalid, '_');
        rawDirName = rawDirName.TrimEnd(':');

        string cleanFileName = string.Format("clean_{0}.txt", rawDirName);
        return Path.GetFullPath(cleanFileName);
    }

    // -dc: 產生重覆檔案清理指令檔 clean_<dirname>.txt (每組保留最新 1 個，其餘產生 del)
    static void GenerateDupCleanFile(DirectoryInfo rootDir)
    {
        string cleanFilePath = GetCleanScriptFilePath(rootDir);

        List<string> dupKeys = new List<string>();
        foreach (var kvp in fileGroups)
        {
            if (kvp.Value.Count > 1)
                dupKeys.Add(kvp.Key);
        }
        dupKeys.Sort(StringComparer.OrdinalIgnoreCase);

        if (dupKeys.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n" + T("CleanNoDupsHint"));
            Console.ResetColor();
            return;
        }

        int totalKeep = 0;
        int totalDelete = 0;
        long totalSavedBytes = 0;

        using (StreamWriter sw = new StreamWriter(cleanFilePath, false, new UTF8Encoding(false)))
        {
            sw.WriteLine("@echo off");
            sw.WriteLine("chcp 65001 >nul");
            sw.WriteLine("REM ============================================================");
            sw.WriteLine("REM " + TF("CleanBannerTitle", appName));
            sw.WriteLine("REM " + TF("CleanBannerTarget", rootDir.FullName));
            sw.WriteLine("REM " + TF("CleanBannerTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            sw.WriteLine("REM " + T("CleanBannerRule"));
            sw.WriteLine("REM " + T("CleanBannerUsage"));
            sw.WriteLine("REM ============================================================");
            sw.WriteLine("");

            for (int i = 0; i < dupKeys.Count; i++)
            {
                string key = dupKeys[i];
                List<FileInfo> list = new List<FileInfo>(fileGroups[key]);

                list.Sort((a, b) =>
                {
                    int cmpTime = b.LastWriteTime.CompareTo(a.LastWriteTime);
                    if (cmpTime != 0) return cmpTime;

                    bool aIsExact = string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase);
                    bool bIsExact = string.Equals(b.Name, key, StringComparison.OrdinalIgnoreCase);
                    if (aIsExact != bIsExact) return bIsExact.CompareTo(aIsExact);

                    int cmpLen = a.FullName.Length.CompareTo(b.FullName.Length);
                    if (cmpLen != 0) return cmpLen;

                    return string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
                });

                FileInfo keepFile = list[0];
                totalKeep++;

                sw.WriteLine("REM " + TF("CleanGroupHeader", i + 1, key, list.Count));
                sw.WriteLine("REM " + TF("CleanKeepLine",
                    keepFile.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                    FormatSize(keepFile.Length),
                    keepFile.FullName));

                for (int j = 1; j < list.Count; j++)
                {
                    FileInfo delFile = list[j];
                    totalDelete++;
                    totalSavedBytes += delFile.Length;
                    sw.WriteLine("REM " + TF("CleanDeleteLine",
                        delFile.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        FormatSize(delFile.Length)));
                    sw.WriteLine(string.Format("del \"{0}\"", delFile.FullName));
                }
                sw.WriteLine("");
            }

            sw.WriteLine("REM ============================================================");
            sw.WriteLine("REM " + TF("CleanFooterSummary", totalKeep, totalDelete, FormatSize(totalSavedBytes)));
            sw.WriteLine("REM ============================================================");
            sw.WriteLine("echo " + T("CleanDoneEcho"));
            sw.WriteLine("pause");
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n" + TF("CleanConsoleTitle", cleanFilePath));
        Console.WriteLine(TF("CleanConsoleStats", totalKeep, totalDelete, FormatSize(totalSavedBytes)));
        Console.WriteLine(T("CleanConsoleHint"));
        Console.ResetColor();
    }

    // -c: 產生全數清理指令檔 clean_<dirname>.txt (將篩選範圍內的檔案全部產生 del，常搭配 -i 指定特定檔名系列)
    static void GenerateAllCleanFile(DirectoryInfo rootDir)
    {
        string cleanFilePath = GetCleanScriptFilePath(rootDir);

        List<FileInfo> targetFiles = new List<FileInfo>();
        for (int i = 0; i < allMatchedFiles.Count; i++)
        {
            FileInfo f = allMatchedFiles[i];
            if (dupOnly && !IsDuplicateFile(f)) continue;
            targetFiles.Add(f);
        }

        if (targetFiles.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n" + T("CleanNoFilesHint"));
            Console.ResetColor();
            return;
        }

        targetFiles.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase));

        long totalSavedBytes = 0;

        using (StreamWriter sw = new StreamWriter(cleanFilePath, false, new UTF8Encoding(false)))
        {
            sw.WriteLine("@echo off");
            sw.WriteLine("chcp 65001 >nul");
            sw.WriteLine("REM ============================================================");
            sw.WriteLine("REM " + TF("CleanAllBannerTitle", appName));
            sw.WriteLine("REM " + TF("CleanBannerTarget", rootDir.FullName));
            sw.WriteLine("REM " + TF("CleanBannerTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            sw.WriteLine("REM " + T("CleanAllBannerRule"));
            sw.WriteLine("REM " + T("CleanBannerUsage"));
            sw.WriteLine("REM ============================================================");
            sw.WriteLine("");

            for (int i = 0; i < targetFiles.Count; i++)
            {
                FileInfo delFile = targetFiles[i];
                totalSavedBytes += delFile.Length;
                sw.WriteLine("REM " + TF("CleanDeleteLine",
                    delFile.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                    FormatSize(delFile.Length)));
                sw.WriteLine(string.Format("del \"{0}\"", delFile.FullName));
            }
            sw.WriteLine("");

            sw.WriteLine("REM ============================================================");
            sw.WriteLine("REM " + TF("CleanAllFooterSummary", targetFiles.Count, FormatSize(totalSavedBytes)));
            sw.WriteLine("REM ============================================================");
            sw.WriteLine("echo " + T("CleanDoneEcho"));
            sw.WriteLine("pause");
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n" + TF("CleanConsoleTitle", cleanFilePath));
        Console.WriteLine(TF("CleanAllConsoleStats", targetFiles.Count, FormatSize(totalSavedBytes)));
        Console.WriteLine(T("CleanConsoleHint"));
        Console.ResetColor();
    }

    static bool IsDuplicateFile(FileInfo file)
    {
        string key = GetCanonicalFileName(file.Name);
        int count;
        return fileNameCounts.TryGetValue(key, out count) && count > 1;
    }

    static bool IsExecutable(FileInfo file)
    {
        return file != null && (
            file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            file.Extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            file.Extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
            file.Extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase));
    }

    static void PrintTimeAndSize(FileInfo file)
    {
        if (showTime)
            WriteColor(string.Format(" ({0})", file.LastWriteTime.ToString("yyyy-MM-dd HH:mm")), colorTime);

        if (showSize)
            WriteColor(string.Format(" ({0})", FormatSize(file.Length)), colorSize);
    }

    static void PrintFileItem(FileInfo file, bool showDupTag)
    {
        fileCount++;
        bool isDup = IsDuplicateFile(file);
        if (isDup) dupFileCount++;

        if (IsExecutable(file))
            WriteColor(file.Name, colorExe);
        else
            Write(file.Name);

        PrintTimeAndSize(file);

        if (showDupTag && isDup)
            WriteColor(" " + T("DupTag"), colorDup);

        WriteLine("");
    }

    // 遞迴收集並建構「以重覆檔名為主、所在目錄為輔」的虛擬樹 (-dup，同步支援 -dt / -mt)
    static void CollectDupTree(DirectoryInfo dir, int depth, List<DirectoryInfo> relPath, VNode vRoot, DirectoryInfo rootDir)
    {
        if (depth > maxDepth || depth > maxFileDepth) return;

        try
        {
            foreach (var f in dir.GetFiles())
            {
                if (ShouldExclude(f) || !ShouldIncludeFile(f)) continue;
                if (!IsDuplicateFile(f)) continue;

                AddDupFileToVRoot(vRoot, relPath, f, f.LastWriteTime, rootDir);
            }

            if (depth + 1 <= maxDepth && depth + 1 <= maxFileDepth)
            {
                foreach (var d in dir.GetDirectories())
                {
                    if (ShouldExclude(d)) continue;
                    List<DirectoryInfo> nextRel = new List<DirectoryInfo>(relPath);
                    nextRel.Add(d);
                    CollectDupTree(d, depth + 1, nextRel, vRoot, rootDir);
                }
            }
        }
        catch { }
    }

    static void AddDupFileToVRoot(VNode vRoot, List<DirectoryInfo> relDirs, FileInfo file, DateTime dt, DirectoryInfo rootDir)
    {
        string canonicalName = GetCanonicalFileName(file.Name);

        // 第一層：重覆基底檔名節點 (NodeType = 3)
        VNode curr = vRoot.GetOrAddChild("DUP:" + canonicalName, canonicalName, 3, null);
        if (curr.FileRef == null) curr.FileRef = file;

        // 若搭配 -dt 或 -mt，第二層為日期節點 (NodeType = 0)
        if (dateMode == 1 || dateMode == 2)
        {
            curr = GetDateNode(curr, dt);
        }

        // 展開所在目錄階層 (NodeType = 1)
        if (relDirs.Count == 0)
        {
            curr = curr.GetOrAddChild("DIR:.", ".", 1, rootDir.FullName);
            uniqueDirs.Add(rootDir.FullName);
        }
        else
        {
            for (int i = 0; i < relDirs.Count; i++)
            {
                DirectoryInfo d = relDirs[i];
                curr = curr.GetOrAddChild("DIR:" + d.Name, d.Name, 1, d.FullName);
                uniqueDirs.Add(d.FullName);
            }
        }

        curr.DupFiles.Add(file);
    }

    // 遞迴收集並建構日期樹 (-dt / -mt)
    static bool CollectDateTree(DirectoryInfo dir, int depth, List<DirectoryInfo> relPath, VNode vRoot)
    {
        if (depth > maxDepth) return false;

        bool addedAny = false;
        try
        {
            foreach (var d in dir.GetDirectories())
            {
                if (ShouldExclude(d)) continue;
                if (!dirsOnly && includeRules.Count > 0 && !HasMatchingFiles(d, depth + 1))
                    continue;

                List<DirectoryInfo> nextRel = new List<DirectoryInfo>(relPath);
                nextRel.Add(d);

                bool childAdded = false;
                if (depth + 1 <= maxDepth)
                {
                    childAdded = CollectDateTree(d, depth + 1, nextRel, vRoot);
                }

                if (!childAdded && includeRules.Count == 0 && (dirsOnly || depth >= maxFileDepth))
                {
                    AddDirToVRoot(vRoot, nextRel, d.LastWriteTime);
                    addedAny = true;
                }
                else if (childAdded)
                {
                    addedAny = true;
                }
            }

            if (!dirsOnly && depth <= maxFileDepth)
            {
                foreach (var f in dir.GetFiles())
                {
                    if (ShouldExclude(f) || !ShouldIncludeFile(f)) continue;
                    AddFileToVRoot(vRoot, relPath, f, f.LastWriteTime);
                    addedAny = true;
                }
            }
        }
        catch { }

        return addedAny;
    }

    static VNode GetDateNode(VNode vRoot, DateTime dt)
    {
        string ym = dt.ToString("yyyyMM");
        VNode ymNode = vRoot.GetOrAddChild("D:" + ym, ym, 0, null);
        if (dateMode == 1) // -dt: 年月 -> 日
        {
            string dd = dt.ToString("dd");
            return ymNode.GetOrAddChild("D:" + dd, dd, 0, null);
        }
        return ymNode; // -mt: 僅年月
    }

    static void AddDirToVRoot(VNode vRoot, List<DirectoryInfo> relDirs, DateTime dt)
    {
        VNode curr = GetDateNode(vRoot, dt);
        for (int i = 0; i < relDirs.Count; i++)
        {
            DirectoryInfo d = relDirs[i];
            curr = curr.GetOrAddChild("DIR:" + d.Name, d.Name, 1, d.FullName);
            uniqueDirs.Add(d.FullName);
        }
    }

    static void AddFileToVRoot(VNode vRoot, List<DirectoryInfo> relDirs, FileInfo file, DateTime dt)
    {
        VNode curr = GetDateNode(vRoot, dt);
        for (int i = 0; i < relDirs.Count; i++)
        {
            DirectoryInfo d = relDirs[i];
            curr = curr.GetOrAddChild("DIR:" + d.Name, d.Name, 1, d.FullName);
            uniqueDirs.Add(d.FullName);
        }
        VNode fNode = curr.GetOrAddChild("FILE:" + file.Name, file.Name, 2, null);
        fNode.FileRef = file;
    }

    static void PrintVNodeChildren(VNode parent, string indent)
    {
        List<VNode> list = new List<VNode>(parent.Children.Values);
        // 排序：日期節點(0) -> 實體目錄(1) -> 實體檔案(2) -> 重覆檔名節點(3)，同類型依名稱遞增排序
        list.Sort((a, b) =>
        {
            if (a.NodeType != b.NodeType) return a.NodeType.CompareTo(b.NodeType);
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        for (int i = 0; i < list.Count; i++)
        {
            VNode node = list[i];
            bool isLast = (i == list.Count - 1);
            string branch = isLast ? "└── " : "├── ";
            string nextIndent = indent + (isLast ? "    " : "│   ");

            Write(indent + branch);

            if (node.NodeType == 0)
            {
                // 虛擬日期節點：不加 []
                WriteColorLine(node.Name, colorDate);
                PrintVNodeChildren(node, nextIndent);
            }
            else if (node.NodeType == 1)
            {
                // 實體目錄：加上 []
                WriteColor(string.Format("[{0}]", node.Name), colorDir);

                if (node.DupFiles.Count == 1 && node.Children.Count == 0)
                {
                    FileInfo f = node.DupFiles[0];
                    fileCount++;
                    dupFileCount++;
                    string canonical = GetCanonicalFileName(f.Name);
                    if (!string.Equals(f.Name, canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        Write(" -> ");
                        if (IsExecutable(f))
                            WriteColor(f.Name, colorExe);
                        else
                            Write(f.Name);
                    }
                    PrintTimeAndSize(f);
                }
                else if (node.DupFiles.Count > 0)
                {
                    // 同一個目錄下有多個同組檔案（如 xxx.docx 與 xxx (1).docx），或該目錄同時還有子目錄，將檔案展開為子節點
                    for (int j = 0; j < node.DupFiles.Count; j++)
                    {
                        FileInfo f = node.DupFiles[j];
                        VNode fNode = node.GetOrAddChild("FILE:" + f.Name, f.Name, 2, null);
                        fNode.FileRef = f;
                    }
                }

                WriteLine("");
                PrintVNodeChildren(node, nextIndent);
            }
            else if (node.NodeType == 2)
            {
                PrintFileItem(node.FileRef, !dupOnly);
            }
            else if (node.NodeType == 3)
            {
                // 虛擬重覆檔名節點 (用於 -dup 模式第一層)
                int count = 0;
                fileNameCounts.TryGetValue(node.Name, out count);

                if (IsExecutable(node.FileRef))
                    WriteColor(node.Name, colorExe);
                else
                    Write(node.Name);

                WriteColorLine(" " + TF("DupCountTag", count), colorDup);
                PrintVNodeChildren(node, nextIndent);
            }
        }
    }

    static void InitAppIdentity()
    {
        try
        {
            string exePath = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(exePath))
                exePath = Process.GetCurrentProcess().MainModule.FileName;
            appName = Path.GetFileNameWithoutExtension(exePath);
            configFileName = appName + ".ini";
            langFileName = appName + ".lang";
        }
        catch
        {
            appName = "MyTree";
            configFileName = "MyTree.ini";
            langFileName = "MyTree.lang";
        }
    }

    static void InitBuiltInGroups()
    {
        excludeGroups["git"]    = new[] { ".git", ".svn", ".hg" };
        excludeGroups["ide"]    = new[] { ".vs", ".idea", ".vscode" };
        excludeGroups["node"]   = new[] { "node_modules", "dist", "build", ".next", ".nuxt", "coverage", ".cache", ".parcel-cache" };
        excludeGroups["py"]     = new[] { "__pycache__", ".venv", "venv", "env", ".pytest_cache", ".mypy_cache", ".tox", "build", "dist" };
        excludeGroups["dotnet"] = new[] { "bin", "obj", "packages", "TestResults", ".vs" };
        excludeGroups["java"]   = new[] { "target", "build", ".gradle", ".mvn", "out" };
        excludeGroups["cpp"]    = new[] { "Debug", "Release", "x64", "x86", "ipch", "CMakeFiles", "build" };

        includeGroups["node"]   = new[] { ".js", ".jsx", ".ts", ".tsx", ".json", ".vue", ".svelte", ".html", ".css", ".scss" };
        includeGroups["py"]     = new[] { ".py", ".pyw", ".ipynb", ".pyi" };
        includeGroups["dotnet"] = new[] { ".cs", ".csproj", ".sln", ".xaml", ".razor", ".config", ".json" };
        includeGroups["java"]   = new[] { ".java", ".kt", ".kts", ".jar", ".gradle", ".xml", ".properties" };
        includeGroups["cpp"]    = new[] { ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hxx", ".cmake" };
        includeGroups["code"]   = new[] { ".cs", ".js", ".jsx", ".ts", ".tsx", ".py", ".java", ".kt", ".c", ".cpp", ".h", ".hpp", ".go", ".rs", ".php", ".rb", ".swift", ".html", ".css", ".scss", ".sql", ".sh", ".ps1", ".bat" };
        includeGroups["doc"]    = new[] { ".md", ".txt", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".csv", ".rst" };
        includeGroups["media"]  = new[] { ".jpg", ".jpeg", ".png", ".gif", ".svg", ".webp", ".bmp", ".ico", ".mp4", ".mkv", ".avi", ".mov", ".mp3", ".wav", ".flac" };
        includeGroups["config"] = new[] { ".json", ".yaml", ".yml", ".xml", ".ini", ".toml", ".env", ".config", ".conf", "Dockerfile", "Makefile" };
        includeGroups["test"]   = new[] { @"/\.(test|spec)\.(js|jsx|ts|tsx|cs|py)$/", @"/^test_.+\.py$/" };
    }

    static void HandleConfigLoading()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string globalConfig = Path.Combine(exeDir, configFileName);
        string localConfig = Path.Combine(Environment.CurrentDirectory, configFileName);

        if (!File.Exists(globalConfig))
        {
            GenerateDefaultConfig(globalConfig);
        }

        if (File.Exists(localConfig))
        {
            loadedConfigPath = Path.GetFullPath(localConfig);
            LoadConfigExclusive(loadedConfigPath);
        }
        else if (File.Exists(globalConfig))
        {
            loadedConfigPath = Path.GetFullPath(globalConfig);
            LoadConfigExclusive(loadedConfigPath);
        }
    }

    static void LoadConfigExclusive(string path)
    {
        try
        {
            excludeGroups.Clear();
            includeGroups.Clear();

            string currentSection = "";
            foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";"))
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                int eqIdx = line.IndexOf('=');
                if (eqIdx <= 0) continue;

                string key = line.Substring(0, eqIdx).Trim();
                string val = line.Substring(eqIdx + 1).Trim();

                if (currentSection.Equals("Defaults", StringComparison.OrdinalIgnoreCase))
                {
                    ParseDefaultsSection(key, val);
                }
                else if (currentSection.Equals("Colors", StringComparison.OrdinalIgnoreCase))
                {
                    ParseColorsSection(key, val);
                }
                else if (currentSection.Equals("ExcludeGroups", StringComparison.OrdinalIgnoreCase))
                {
                    excludeGroups[key] = SplitRules(val).ToArray();
                }
                else if (currentSection.Equals("IncludeGroups", StringComparison.OrdinalIgnoreCase))
                {
                    List<string> rawList = SplitRules(val);
                    for (int i = 0; i < rawList.Count; i++)
                        rawList[i] = BuildIncludeRawString(rawList[i]);
                    includeGroups[key] = rawList.ToArray();
                }
            }
        }
        catch (Exception ex)
        {
            WriteColorLine(TF("WarnConfigLoadFailed", path, ex.Message), ConsoleColor.Yellow);
        }
    }

    static void ParseDefaultsSection(string key, string val)
    {
        int num;
        bool flag;
        if (key.Equals("MaxDepth", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out num))
        {
            maxDepth = num > 0 ? num : int.MaxValue;
        }
        else if (key.Equals("MaxFileDepth", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out num))
        {
            if (num > 0)
            {
                maxFileDepth = num;
                fileDepthSpecified = true;
            }
        }
        else if (key.Equals("ShowSize", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out flag))
        {
            showSize = flag;
        }
        else if (key.Equals("ShowTime", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out flag))
        {
            showTime = flag;
        }
        else if (key.Equals("ShowHidden", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out flag))
        {
            showHidden = flag;
        }
        else if (key.Equals("DirsOnly", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out flag))
        {
            dirsOnly = flag;
        }
        else if (key.Equals("DupOnly", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out flag))
        {
            dupOnly = flag;
        }
        else if (key.Equals("Language", StringComparison.OrdinalIgnoreCase))
        {
            InitLanguage(val);
        }
        else if (key.Equals("DateTreeMode", StringComparison.OrdinalIgnoreCase))
        {
            if (val.Equals("dt", StringComparison.OrdinalIgnoreCase) || val.Equals("date", StringComparison.OrdinalIgnoreCase))
                dateMode = 1;
            else if (val.Equals("mt", StringComparison.OrdinalIgnoreCase) || val.Equals("month", StringComparison.OrdinalIgnoreCase))
                dateMode = 2;
            else
                dateMode = 0;
        }
        else if (key.Equals("DefaultExcludeGroups", StringComparison.OrdinalIgnoreCase))
        {
            defaultExcludeGroupNames.Clear();
            foreach (var g in val.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                defaultExcludeGroupNames.Add(g.Trim());
        }
    }

    static void ParseColorsSection(string key, string val)
    {
        try
        {
            ConsoleColor c = (ConsoleColor)Enum.Parse(typeof(ConsoleColor), val, true);
            if (key.Equals("Root", StringComparison.OrdinalIgnoreCase)) colorRoot = c;
            else if (key.Equals("Directory", StringComparison.OrdinalIgnoreCase)) colorDir = c;
            else if (key.Equals("Executable", StringComparison.OrdinalIgnoreCase)) colorExe = c;
            else if (key.Equals("Size", StringComparison.OrdinalIgnoreCase)) colorSize = c;
            else if (key.Equals("Time", StringComparison.OrdinalIgnoreCase)) colorTime = c;
            else if (key.Equals("Summary", StringComparison.OrdinalIgnoreCase)) colorSummary = c;
            else if (key.Equals("DateNode", StringComparison.OrdinalIgnoreCase)) colorDate = c;
            else if (key.Equals("Duplicate", StringComparison.OrdinalIgnoreCase)) colorDup = c;
        }
        catch { }
    }

    static void GenerateDefaultConfig(string path)
    {
        try
        {
            using (StreamWriter sw = new StreamWriter(path, false, Encoding.UTF8))
            {
                sw.WriteLine(string.Format("# {0} 設定檔", appName));
                sw.WriteLine("# 讀取優先權: 若「當前工作目錄」下有此檔案，將完全以工作目錄為準；否則讀取執行檔所在目錄之全域設定檔。");
                sw.WriteLine("# 支援語法:");
                sw.WriteLine("#   1. 副檔名   : .cs, .json 或 *.log");
                sw.WriteLine("#   2. 完整名稱 : node_modules, Dockerfile, Makefile (亦支援 * 與 ? 萬用字元，如 P*.png)");
                sw.WriteLine("#   3. RegExp   : 使用 /.../ 包覆，例如 /\\.(test|spec)\\.ts$/ 或 /^temp_[0-9]+/");
                sw.WriteLine("");
                sw.WriteLine("[Defaults]");
                sw.WriteLine("# Language 可設為: auto (依系統自動選擇), zh-TW, en");
                sw.WriteLine("Language = auto");
                sw.WriteLine("# MaxDepth / MaxFileDepth 設為 0 代表不限制層數");
                sw.WriteLine("MaxDepth = 0");
                sw.WriteLine("MaxFileDepth = 0");
                sw.WriteLine("ShowSize = false");
                sw.WriteLine("ShowTime = false");
                sw.WriteLine("ShowHidden = false");
                sw.WriteLine("DirsOnly = false");
                sw.WriteLine("DupOnly = false");
                sw.WriteLine("# DateTreeMode 可設為: none (預設), dt (年月/日兩層), mt (年月一層)");
                sw.WriteLine("DateTreeMode = none");
                sw.WriteLine("DefaultExcludeGroups = git, ide");
                sw.WriteLine("");
                sw.WriteLine("[Colors]");
                sw.WriteLine("# 可選顏色: Black, DarkBlue, DarkGreen, DarkCyan, DarkRed, DarkMagenta, DarkYellow, Gray, DarkGray, Blue, Green, Cyan, Red, Magenta, Yellow, White");
                sw.WriteLine("Root = Cyan");
                sw.WriteLine("Directory = Blue");
                sw.WriteLine("Executable = Green");
                sw.WriteLine("Size = DarkGray");
                sw.WriteLine("Time = DarkCyan");
                sw.WriteLine("Summary = Green");
                sw.WriteLine("DateNode = Yellow");
                sw.WriteLine("Duplicate = Magenta");
                sw.WriteLine("");
                sw.WriteLine("[ExcludeGroups]");
                foreach (var kvp in excludeGroups)
                {
                    sw.WriteLine(string.Format("{0} = {1}", kvp.Key, string.Join(", ", kvp.Value)));
                }
                sw.WriteLine("");
                sw.WriteLine("[IncludeGroups]");
                foreach (var kvp in includeGroups)
                {
                    sw.WriteLine(string.Format("{0} = {1}", kvp.Key, string.Join(", ", kvp.Value)));
                }
            }
        }
        catch { }
    }

    static List<string> SplitRules(string input)
    {
        List<string> result = new List<string>();
        if (string.IsNullOrEmpty(input)) return result;

        StringBuilder current = new StringBuilder();
        bool inRegex = false;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '/')
            {
                if (!inRegex && current.ToString().Trim().Length == 0)
                {
                    inRegex = true;
                    current.Append(c);
                    continue;
                }
                else if (inRegex && (i == 0 || input[i - 1] != '\\'))
                {
                    inRegex = false;
                    current.Append(c);
                    continue;
                }
            }

            if (!inRegex && (c == ',' || c == ';'))
            {
                string s = current.ToString().Trim();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
                current.Length = 0;
            }
            else
            {
                current.Append(c);
            }
        }

        string last = current.ToString().Trim();
        if (!string.IsNullOrEmpty(last)) result.Add(last);
        return result;
    }

    static string BuildIncludeRawString(string raw)
    {
        string s = raw.Trim();
        if (string.IsNullOrEmpty(s)) return s;
        if (s.StartsWith("/") && s.EndsWith("/")) return s;
        if (s.StartsWith(".") || s.StartsWith("*.") || s.Contains("*") || s.Contains("?")) return s;
        bool hasUpperOrSpecial = false;
        foreach (char c in s)
        {
            if (char.IsUpper(c) || c == '-' || c == '_') { hasUpperOrSpecial = true; break; }
        }
        if (!hasUpperOrSpecial && s.Length <= 5 && !s.Contains("."))
            return "." + s;
        return s;
    }

    static void AddRule(List<FilterRule> targetList, string raw)
    {
        if (string.IsNullOrEmpty(raw)) return;
        try
        {
            targetList.Add(new FilterRule(raw));
        }
        catch (Exception ex)
        {
            WriteColorLine(TF("WarnInvalidRule", raw, ex.Message), ConsoleColor.Yellow);
        }
    }

    static bool ApplyExcludeGroup(string groupName)
    {
        string[] items;
        if (excludeGroups.TryGetValue(groupName, out items))
        {
            foreach (var item in items) AddRule(excludeRules, item);
            return true;
        }
        return false;
    }

    static bool ApplyIncludeGroup(string groupName)
    {
        string[] items;
        if (includeGroups.TryGetValue(groupName, out items))
        {
            foreach (var item in items) AddRule(includeRules, item);
            return true;
        }
        return false;
    }

    static void PrintTree(DirectoryInfo dir, string indent, int depth)
    {
        if (depth > maxDepth) return;

        List<FileSystemInfo> items = new List<FileSystemInfo>();
        try
        {
            foreach (var d in dir.GetDirectories())
            {
                if (ShouldExclude(d)) continue;
                if (!dirsOnly && includeRules.Count > 0 && !HasMatchingFiles(d, depth + 1))
                    continue;
                items.Add(d);
            }
            if (!dirsOnly && depth <= maxFileDepth)
            {
                foreach (var f in dir.GetFiles())
                {
                    if (ShouldExclude(f) || !ShouldIncludeFile(f)) continue;
                    items.Add(f);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            WriteColorLine(indent + "└── " + T("AccessDenied"), ConsoleColor.Red);
            return;
        }
        catch (Exception)
        {
            return;
        }

        items.Sort((a, b) =>
        {
            bool aIsDir = a is DirectoryInfo;
            bool bIsDir = b is DirectoryInfo;
            if (aIsDir != bIsDir) return bIsDir.CompareTo(aIsDir);
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool isLast = (i == items.Count - 1);
            string branch = isLast ? "└── " : "├── ";
            string nextIndent = indent + (isLast ? "    " : "│   ");

            Write(indent + branch);

            DirectoryInfo subDir = item as DirectoryInfo;
            FileInfo file = item as FileInfo;

            if (subDir != null)
            {
                dirCount++;
                WriteColorLine(string.Format("[{0}]", subDir.Name), colorDir);
                PrintTree(subDir, nextIndent, depth + 1);
            }
            else if (file != null)
            {
                PrintFileItem(file, true);
            }
        }
    }

    static bool HasMatchingFiles(DirectoryInfo dir, int currentDepth)
    {
        if (currentDepth > maxDepth || currentDepth > maxFileDepth) return false;
        try
        {
            foreach (var f in dir.GetFiles())
            {
                if (!ShouldExclude(f) && ShouldIncludeFile(f)) return true;
            }
            foreach (var d in dir.GetDirectories())
            {
                if (ShouldExclude(d)) continue;
                if (HasMatchingFiles(d, currentDepth + 1)) return true;
            }
        }
        catch { }
        return false;
    }

    static bool ShouldExclude(FileSystemInfo info)
    {
        if (!showHidden && (info.Attributes & FileAttributes.Hidden) != 0)
            return true;

        for (int i = 0; i < excludeRules.Count; i++)
        {
            if (excludeRules[i].Matches(info)) return true;
        }
        return false;
    }

    static bool ShouldIncludeFile(FileInfo file)
    {
        if (includeRules.Count == 0) return true;
        for (int i = 0; i < includeRules.Count; i++)
        {
            if (includeRules[i].Matches(file)) return true;
        }
        return false;
    }

    static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return string.Format("{0:0.##} {1}", len, sizes[order]);
    }

    static void Write(string text)
    {
        if (fileWriter != null) fileWriter.Write(text);
        else Console.Write(text);
    }

    static void WriteLine(string text)
    {
        if (fileWriter != null) fileWriter.WriteLine(text);
        else Console.WriteLine(text);
    }

    static void WriteColor(string text, ConsoleColor color)
    {
        if (fileWriter != null)
        {
            fileWriter.Write(text);
        }
        else
        {
            if (useColor) Console.ForegroundColor = color;
            Console.Write(text);
            if (useColor) Console.ResetColor();
        }
    }

    static void WriteColorLine(string text, ConsoleColor color)
    {
        if (fileWriter != null)
        {
            fileWriter.WriteLine(text);
        }
        else
        {
            if (useColor) Console.ForegroundColor = color;
            Console.WriteLine(text);
            if (useColor) Console.ResetColor();
        }
    }

    static void PrintGroups()
    {
        WriteColorLine(TF("GroupsActiveConfig", loadedConfigPath), ConsoleColor.Cyan);
        WriteColorLine(TF("GroupsActiveLang", currentLang), ConsoleColor.Cyan);
        Console.WriteLine(TF("GroupsDefaultExclude", defaultExcludeGroupNames.Count > 0 ? string.Join(", ", defaultExcludeGroupNames.ToArray()) : T("GroupsNone")) + "\n");

        Console.WriteLine(T("GroupsExcludeHeader"));
        foreach (var kvp in excludeGroups)
            Console.WriteLine(string.Format("  {0,-8} : {1}", kvp.Key, string.Join(", ", kvp.Value)));

        Console.WriteLine("\n" + T("GroupsIncludeHeader"));
        foreach (var kvp in includeGroups)
            Console.WriteLine(string.Format("  {0,-8} : {1}", kvp.Key, string.Join(", ", kvp.Value)));
    }

    static void PrintHelp()
    {
        Console.WriteLine(TF("HelpUsage", appName));
        Console.WriteLine(TF("HelpConfig", configFileName, langFileName));
        Console.WriteLine("\n" + T("HelpSectionBasic"));
        Console.WriteLine(T("HelpOptL"));
        Console.WriteLine(T("HelpOptFL"));
        Console.WriteLine(T("HelpOptDt"));
        Console.WriteLine(T("HelpOptMt"));
        Console.WriteLine(T("HelpOptDup"));
        Console.WriteLine(T("HelpOptDc"));
        Console.WriteLine(T("HelpOptC"));
        Console.WriteLine(T("HelpOptD"));
        Console.WriteLine(T("HelpOptA"));
        Console.WriteLine(T("HelpOptT"));
        Console.WriteLine(T("HelpOptS"));
        Console.WriteLine(T("HelpOptO"));
        Console.WriteLine("\n" + T("HelpSectionFilter"));
        Console.WriteLine(T("HelpOptE"));
        Console.WriteLine(T("HelpOptEg"));
        Console.WriteLine(T("HelpOptI"));
        Console.WriteLine(T("HelpOptIg"));
        Console.WriteLine(T("HelpOptNoDefault"));
        Console.WriteLine(T("HelpOptGroups"));
        Console.WriteLine(TF("HelpOptLang", langFileName));
        Console.WriteLine(TF("HelpOptExportLang", langFileName));
        Console.WriteLine(T("HelpOptH"));
    }
}
