using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

class FilterRule
{
    public string RawText;
    public int RuleType; // 0: 完整檔名/目錄名, 1: 副檔名, 2: RegExp (/.../)
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
        else if (RawText.StartsWith("*."))
        {
            RuleType = 1;
            Value = RawText.Substring(1);
        }
        else if (RawText.StartsWith(".") && !RawText.Contains("/") && !RawText.Contains("\\"))
        {
            RuleType = 1;
            Value = RawText;
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
            return string.Equals(info.Name, Value, StringComparison.OrdinalIgnoreCase);
        }
    }
}

// 用於 -dt、-mt 與 -dup 模式的虛擬樹節點
class VNode
{
    public string Name;
    public int NodeType; // 0: 虛擬日期節點 (無括號), 1: 實體目錄 ([名稱]), 2: 實體檔案, 3: 虛擬重覆檔名節點
    public FileInfo FileRef;
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
    static string loadedConfigPath = "";

    static int maxDepth = int.MaxValue;
    static int maxFileDepth = int.MaxValue;
    static bool fileDepthSpecified = false;
    static bool showHidden = false;
    static bool dirsOnly = false;
    static bool showSize = false;
    static bool dupOnly = false;
    static bool useDefaultExcludes = true;
    static int dateMode = 0; // 0: 一般模式, 1: -dt (年月+日), 2: -mt (僅年月)

    static List<string> defaultExcludeGroupNames = new List<string> { "git", "ide" };

    // 配色設定
    static ConsoleColor colorRoot = ConsoleColor.Cyan;
    static ConsoleColor colorDir = ConsoleColor.Blue;
    static ConsoleColor colorExe = ConsoleColor.Green;
    static ConsoleColor colorSize = ConsoleColor.DarkGray;
    static ConsoleColor colorSummary = ConsoleColor.Green;
    static ConsoleColor colorDate = ConsoleColor.Yellow;
    static ConsoleColor colorDup = ConsoleColor.Magenta;

    // 實際生效的過濾規則
    static List<FilterRule> excludeRules = new List<FilterRule>();
    static List<FilterRule> includeRules = new List<FilterRule>();

    // 群組字典
    static Dictionary<string, string[]> excludeGroups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, string[]> includeGroups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

    static string outputFile = null;
    static StreamWriter fileWriter = null;
    static bool useColor = true;

    static int dirCount = 0;
    static int fileCount = 0;
    static int dupFileCount = 0;
    static HashSet<string> uniqueDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, int> fileNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 1. 動態取得執行檔名稱與對應的 .ini 檔名
        InitAppIdentity();

        // 2. 載入內建預設群組，並處理設定檔 (工作目錄優先 OR 全域設定自動生成)
        InitBuiltInGroups();
        HandleConfigLoading();

        string targetPath = ".";
        List<string> selectedEg = new List<string>();
        List<string> selectedIg = new List<string>();
        List<string> cliExcludes = new List<string>();
        List<string> cliIncludes = new List<string>();

        // 3. 解析命令列參數
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            int d;
            int fd;
            if (arg == "-L" && i + 1 < args.Length && int.TryParse(args[++i], out d))
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
            else if (arg == "-a")
                showHidden = true;
            else if (arg == "-d")
                dirsOnly = true;
            else if (arg == "-s")
                showSize = true;
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

        // 4. 處理檔案層數 (-fL) 與目錄層數 (-L) 的同步與防呆限制
        if (!fileDepthSpecified)
        {
            maxFileDepth = maxDepth;
        }
        else if (maxFileDepth > maxDepth)
        {
            WriteColorLine(string.Format("[提示] 檔案顯示層數 (-fL {0}) 大於目錄層數 (-L {1})，已自動調整為 {1}", maxFileDepth, maxDepth), ConsoleColor.Yellow);
            maxFileDepth = maxDepth;
        }
        else if (maxFileDepth <= 0)
        {
            dirsOnly = true;
        }

        if (dupOnly && dirsOnly)
        {
            dirsOnly = false;
            if (maxFileDepth <= 0) maxFileDepth = maxDepth;
        }

        // 5. 套用預設排除群組
        if (useDefaultExcludes)
        {
            foreach (var defGroup in defaultExcludeGroupNames)
                ApplyExcludeGroup(defGroup);
        }

        // 6. 套用使用者指定的排除與包含群組 (-eg / -ig)
        foreach (var g in selectedEg)
        {
            if (!ApplyExcludeGroup(g))
                WriteColorLine(string.Format("[警告] 未知的排除群組: '{0}' (可使用 --groups 查看支援清單)", g), ConsoleColor.Yellow);
        }
        foreach (var g in selectedIg)
        {
            if (!ApplyIncludeGroup(g))
                WriteColorLine(string.Format("[警告] 未知的包含群組: '{0}' (可使用 --groups 查看支援清單)", g), ConsoleColor.Yellow);
        }

        // 7. 套用命令列直接指定的單一規則 (-e / -i)
        foreach (var raw in cliExcludes) AddRule(excludeRules, raw);
        foreach (var raw in cliIncludes) AddRule(includeRules, BuildIncludeRawString(raw));

        if (!Directory.Exists(targetPath))
        {
            WriteColorLine(string.Format("錯誤: 找不到路徑 '{0}'", targetPath), ConsoleColor.Red);
            return;
        }

        try
        {
            if (!string.IsNullOrEmpty(outputFile))
                fileWriter = new StreamWriter(outputFile, false, Encoding.UTF8);

            DirectoryInfo rootDir = new DirectoryInfo(targetPath);

            // 8. 預先掃描檢視範圍內的檔名出現次數，供重覆檔名標示與 -dup 模式使用
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
            string summary = string.Format("{0} 個目錄", dirCount)
                + (dirsOnly ? "" : string.Format(", {0} 個檔案", fileCount))
                + (dupFileCount > 0 ? string.Format(" (含 {0} 個重覆檔案)", dupFileCount) : "");
            WriteColorLine(summary, colorSummary);
        }
        catch (Exception ex)
        {
            WriteColorLine(string.Format("執行發生錯誤: {0}", ex.Message), ConsoleColor.Red);
        }
        finally
        {
            if (fileWriter != null)
            {
                fileWriter.Flush();
                fileWriter.Close();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(string.Format("\n[已將樹狀結構匯出至: {0}]", outputFile));
                Console.ResetColor();
            }
        }
    }

    // 預先統計檢視範圍內各檔名出現次數（不分大小寫）
    static void ScanFileNameCounts(DirectoryInfo dir, int depth)
    {
        if (depth > maxDepth || depth > maxFileDepth) return;
        try
        {
            foreach (var f in dir.GetFiles())
            {
                if (ShouldExclude(f) || !ShouldIncludeFile(f)) continue;
                int count;
                if (fileNameCounts.TryGetValue(f.Name, out count))
                    fileNameCounts[f.Name] = count + 1;
                else
                    fileNameCounts[f.Name] = 1;
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

    static bool IsDuplicateFile(FileInfo file)
    {
        int count;
        return fileNameCounts.TryGetValue(file.Name, out count) && count > 1;
    }

    static bool IsExecutable(FileInfo file)
    {
        return file != null && (
            file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            file.Extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            file.Extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
            file.Extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase));
    }

    static void PrintFileItem(FileInfo file)
    {
        fileCount++;
        bool isDup = IsDuplicateFile(file);
        if (isDup) dupFileCount++;

        if (IsExecutable(file))
            WriteColor(file.Name, colorExe);
        else
            Write(file.Name);

        if (showSize)
            WriteColor(string.Format(" ({0})", FormatSize(file.Length)), colorSize);

        if (isDup)
            WriteColor(" (重覆)", colorDup);

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
        // 第一層：重覆檔名節點 (NodeType = 3)
        VNode curr = vRoot.GetOrAddChild("DUP:" + file.Name, file.Name, 3, null);
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

        curr.FileRef = file;
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
                if (!dirsOnly && includeRules.Count > 0 && depth + 1 <= maxFileDepth && !HasMatchingFiles(d, depth + 1))
                    continue;

                List<DirectoryInfo> nextRel = new List<DirectoryInfo>(relPath);
                nextRel.Add(d);

                bool childAdded = false;
                if (depth + 1 <= maxDepth)
                {
                    childAdded = CollectDateTree(d, depth + 1, nextRel, vRoot);
                }

                // 若該資料夾底下沒有任何檔案/子目錄被掛載 (例如空目錄、或受限於 -d / -fL / -L)，
                // 且無設定檔案過濾條件 (或為純目錄模式)，則以「資料夾本身的異動日期」掛上去
                if (!childAdded && (dirsOnly || includeRules.Count == 0 || depth >= maxFileDepth))
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
                if (node.FileRef != null)
                {
                    fileCount++;
                    dupFileCount++;
                    if (showSize)
                        WriteColor(string.Format(" ({0})", FormatSize(node.FileRef.Length)), colorSize);
                    else if (node.Children.Count > 0)
                        WriteColor(" (此目錄亦含同名檔)", colorDup);
                }
                WriteLine("");
                PrintVNodeChildren(node, nextIndent);
            }
            else if (node.NodeType == 2)
            {
                PrintFileItem(node.FileRef);
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

                WriteColorLine(string.Format(" (重覆 x{0})", count), colorDup);
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
        }
        catch
        {
            appName = "MyTree";
            configFileName = "MyTree.ini";
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
            WriteColorLine(string.Format("[警告] 讀取設定檔 ({0}) 失敗: {1}", path, ex.Message), ConsoleColor.Yellow);
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
                sw.WriteLine("#   2. 完整名稱 : node_modules, Dockerfile, Makefile");
                sw.WriteLine("#   3. RegExp   : 使用 /.../ 包覆，例如 /\\.(test|spec)\\.ts$/ 或 /^temp_[0-9]+/");
                sw.WriteLine("");
                sw.WriteLine("[Defaults]");
                sw.WriteLine("# MaxDepth / MaxFileDepth 設為 0 代表不限制層數");
                sw.WriteLine("MaxDepth = 0");
                sw.WriteLine("MaxFileDepth = 0");
                sw.WriteLine("ShowSize = false");
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
        if (s.StartsWith(".") || s.StartsWith("*.")) return s;
        bool hasUpperOrSpecial = false;
        foreach (char c in s)
        {
            if (char.IsUpper(c) || c == '-' || c == '_') { hasUpperOrSpecial = true; break; }
        }
        if (!hasUpperOrSpecial && s.Length <= 5)
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
            WriteColorLine(string.Format("[警告] 無效的過濾規則或 RegExp '{0}': {1}", raw, ex.Message), ConsoleColor.Yellow);
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
                if (!dirsOnly && includeRules.Count > 0 && depth + 1 <= maxFileDepth && !HasMatchingFiles(d, depth + 1))
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
            WriteColorLine(indent + "└── [拒絕存取]", ConsoleColor.Red);
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
                PrintFileItem(file);
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
        WriteColorLine(string.Format("[生效設定檔] {0}", loadedConfigPath), ConsoleColor.Cyan);
        Console.WriteLine(string.Format("[預設排除群組] {0}\n", defaultExcludeGroupNames.Count > 0 ? string.Join(", ", defaultExcludeGroupNames.ToArray()) : "(無)"));

        Console.WriteLine("=== 排除群組 (-eg <群組名>) ===");
        foreach (var kvp in excludeGroups)
            Console.WriteLine(string.Format("  {0,-8} : {1}", kvp.Key, string.Join(", ", kvp.Value)));

        Console.WriteLine("\n=== 包含群組 (-ig <群組名>) ===");
        foreach (var kvp in includeGroups)
            Console.WriteLine(string.Format("  {0,-8} : {1}", kvp.Key, string.Join(", ", kvp.Value)));
    }

    static void PrintHelp()
    {
        Console.WriteLine(string.Format("用法: {0} [目錄路徑] [選項]", appName));
        Console.WriteLine(string.Format("設定檔: 自動對應為 {0} (工作目錄優先，若無則讀取執行檔所在目錄)", configFileName));
        Console.WriteLine("\n基本與時間軸選項:");
        Console.WriteLine("  -L <層數>            限制顯示的目錄深度 (例如: -L 5)");
        Console.WriteLine("  -fL <層數>           限制顯示檔案的深度 (預設同 -L，需 <= -L，如: -L 5 -fL 2)");
        Console.WriteLine("  -dt, --date-tree     以「年月(YYYYMM) -> 日(DD)」作為根層級歸納檔案");
        Console.WriteLine("  -mt, --month-tree    以「年月(YYYYMM)」作為根層級歸納檔案");
        Console.WriteLine("  -dup, --duplicates   以「重覆檔名」為根層級，展開其散落的目錄樹 (同步支援 -dt / -mt)");
        Console.WriteLine("  -d                   只顯示目錄，不顯示檔案 (等同於 -fL 0)");
        Console.WriteLine("  -a                   顯示隱藏檔案與目錄");
        Console.WriteLine("  -s                   顯示檔案大小");
        Console.WriteLine("  -o <檔名>            將結果匯出至檔案 (自動移除顏色碼)");
        Console.WriteLine("\n過濾與群組選項 (支援 副檔名、完整名稱、以及 /.../ 正則表達式):");
        Console.WriteLine("  -e, --exclude <規則> 排除指定的目錄或檔案 (如: -e temp,logs,/^bak_/)");
        Console.WriteLine("  -eg <群組名>         套用排除群組 (如: -eg node,py)");
        Console.WriteLine("  -i, --include <規則> 只顯示符合的檔案 (如: -i .cs,Dockerfile,/\\.test\\.ts$/)");
        Console.WriteLine("  -ig <群組名>         套用包含群組 (如: -ig code,doc,test)");
        Console.WriteLine("  --no-default         停用預設自動排除的群組");
        Console.WriteLine("  --groups             查看目前生效的設定檔路徑與所有群組清單");
        Console.WriteLine("  -h, --help           顯示此說明訊息");
    }
}
