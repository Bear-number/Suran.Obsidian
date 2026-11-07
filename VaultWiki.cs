using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Suran.Obsidian;

/// <summary>
/// 知识库半区的纯逻辑，逐条移植自 KiraAI 的 kira_wiki 插件（wiki_core.py，纯标准库）。
/// 不碰 HTTP、不碰框架：Obsidian 没开着也能用。
/// </summary>
public static class VaultWiki
{
    public static readonly string[] Sections = { "sources", "entities", "concepts", "analyses" };
    public const string Imported = "imported";
    static readonly Dictionary<string, string> SectionLabels = new()
    {
        ["sources"] = "来源", ["entities"] = "实体", ["concepts"] = "概念", ["analyses"] = "分析", [Imported] = "外部笔记"
    };
    public static readonly string[] RootPages = { "index", "log", "overview", "conventions" };
    public const string IndexStart = "<!-- auto-index:start -->";
    public const string IndexEnd = "<!-- auto-index:end -->";
    const long MaxPageBytes = 512 * 1024;
    const int MaxExternalPages = 5000;
    static readonly HashSet<string> ExternalSkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "wiki", "raw", "node_modules", "assets", "templates" };
    static readonly string[] RawExtensions = { ".md", ".txt", ".pdf", ".png", ".jpg", ".jpeg", ".webp" };

    /// <summary>数 raw/ 下的素材文件；wiki_status 只报统计时也要用它，别让素材数永远是 0。</summary>
    public static int CountRaw(string root)
    {
        string rawFolder = Path.Combine(root, "raw");
        if (!Directory.Exists(rawFolder))
        {
            return 0;
        }
        return Directory.EnumerateFiles(rawFolder, "*", SearchOption.AllDirectories)
            .Count(file => RawExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
    }

    // 全库扫描很贵：按路径缓存正文/分词/双链，文件 mtime+size 变了才重读
    sealed class CachedText
    {
        public long Length;
        public DateTime Modified;
        public string Text = "";
        public string Body = "";
        public string[] Terms = Array.Empty<string>();
        public string[] Links = Array.Empty<string>();
    }

    static readonly object TextCacheLock = new();
    static readonly Dictionary<string, CachedText> TextCache = new(StringComparer.OrdinalIgnoreCase);

    static CachedText GetCachedText(string file)
    {
        FileInfo info = new(file);
        lock (TextCacheLock)
        {
            CachedText? cached = TextCache.GetValueOrDefault(file);
            if (cached != null && cached.Length == info.Length && cached.Modified == info.LastWriteTimeUtc)
            {
                return cached;
            }
        }
        string text = ReadText(file, MaxPageBytes);
        (Dictionary<string, string> _, _, string body) = ParseFrontmatter(text);
        string[] links = WikiLinkRegex.Matches(text).Cast<Match>()
            .Select(link => link.Groups[1].Value.Trim().Split('/')[^1].Trim())
            .Where(name => name.Length > 0)
            .ToArray();
        CachedText entry = new()
        {
            Length = info.Length,
            Modified = info.LastWriteTimeUtc,
            Text = text,
            Body = body,
            Terms = Terms(body),
            Links = links,
        };
        lock (TextCacheLock)
        {
            if (TextCache.Count >= 800)
            {
                TextCache.Clear();
            }
            TextCache[file] = entry;
        }
        return entry;
    }

    static readonly Regex WikiLinkRegex = new(@"(?<!\!)\[\[([^\]|#]+)(?:[|#][^\]]*)?\]\]", RegexOptions.Compiled);
    static readonly Regex AnyLinkRegex = new(@"\[\[([^\]|#]+)([|#][^\]]*)?\]\]", RegexOptions.Compiled);
    // Python 那份只认 \n，Windows 上 CRLF 的笔记会解析不出 frontmatter，这里补上 \r?\n
    static readonly Regex FrontmatterRegex = new(@"^---[ \t]*\r?\n(.*?)\r?\n---[ \t]*\r?\n?",
        RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex LatinTerms = new(@"[a-z0-9_]{2,}", RegexOptions.Compiled);
    static readonly Regex CjkRuns = new(@"[\u4e00-\u9fff]+", RegexOptions.Compiled);
    static readonly Regex SlugNoise = new(@"[^\w一-鿿-]+", RegexOptions.Compiled);

    public sealed class PathRejectedException(string reason) : Exception(reason);

    public sealed record Page(string Section, string Name, string Rel, long Size, DateTime Modified,
        string Title, string[] Tags, bool Generated)
    {
        public string Label => SectionLabels.TryGetValue(Section, out string? text) ? text : Section;
    }

    public sealed record Issue(string Level, string Kind, string Page, string Detail);

    public sealed record SearchHit(string Rel, string Title, string Section, bool Generated, double Score, string Snippet);

    public sealed class SearchOutcome
    {
        public List<SearchHit> Hits { get; } = new();
        public int Candidates;
        public int Chars;
        public int ScannedPages;
    }

    public sealed class LintReport
    {
        public List<Page> Pages { get; } = new();
        public List<Issue> Issues { get; } = new();
        public Dictionary<string, int> BySection { get; } = new();
        public int RootPageCount;
        public int RawCount;
        public long ContentBytes;
        public int LogLines;
    }

    // ============================================================
    // 路径安全：写只允许落在知识库根之内，外部笔记不许写进 wiki/ 与 raw/
    // ============================================================

    public static string SafePath(string root, string rel, bool mustExist = false)
    {
        string cleaned = (rel ?? "").Trim().Replace('\\', '/');
        if (cleaned.Length == 0 || cleaned.StartsWith('/') || cleaned.StartsWith('~'))
        {
            throw new PathRejectedException("不给绝对路径：只接受知识库根之内的相对路径");
        }
        if (cleaned.Length >= 2 && char.IsAscii(cleaned[0]) && cleaned[1] == ':')
        {
            throw new PathRejectedException("不给带盘符的路径：只接受知识库根之内的相对路径");
        }
        List<string> parts = cleaned.Split('/').Where(segment => segment.Length > 0 && segment != ".").ToList();
        if (parts.Any(segment => segment == ".."))
        {
            throw new PathRejectedException("不给 .. 穿越：只接受知识库根之内的相对路径");
        }
        if (parts.Count == 0)
        {
            throw new PathRejectedException("路径是空的");
        }
        string rootFull = NormalizeRoot(root);
        string target = Path.GetFullPath(Path.Combine(new[] { rootFull }.Concat(parts).ToArray()));
        if (target != rootFull && !target.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new PathRejectedException("解析后跑到了知识库根之外");
        }
        if (mustExist && !File.Exists(target) && !Directory.Exists(target))
        {
            throw new PathRejectedException("找不到：" + string.Join("/", parts));
        }
        return target;
    }

    static string NormalizeRoot(string root) => Path.GetFullPath(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public static bool LooksGenerated(string rel)
    {
        string cleaned = (rel ?? "").Trim().Replace('\\', '/');
        if (RootPages.Any(name => string.Equals(cleaned, name + ".md", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        string head = cleaned.Split('/')[0];
        return Sections.Contains(head, StringComparer.OrdinalIgnoreCase) && cleaned.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把（相对路径, 是否生成区）解成绝对路径。generated 给 null 时按路径自己判。</summary>
    public static (string Path, bool Generated, string Rel) ResolveTarget(string root, string rel, bool? generated = null, bool mustExist = false)
    {
        string cleaned = (rel ?? "").Trim().Replace('\\', '/');
        bool isGenerated = generated ?? LooksGenerated(cleaned);
        string rootFull = NormalizeRoot(root);
        if (!isGenerated)
        {
            string target = SafePath(rootFull, cleaned, mustExist);
            string[] inside = Path.GetRelativePath(rootFull, target).Split(Path.DirectorySeparatorChar);
            if (inside.Length > 0 && (inside[0].Equals("wiki", StringComparison.OrdinalIgnoreCase)
                || inside[0].Equals("raw", StringComparison.OrdinalIgnoreCase)))
            {
                throw new PathRejectedException("那是生成区，写它要把区划填成 wiki（生成区）");
            }
            if (inside.Any(segment => segment.StartsWith('.')))
            {
                throw new PathRejectedException("不碰隐藏目录（.obsidian 之类）");
            }
            return (target, false, cleaned);
        }
        string wikiRoot = Path.Combine(rootFull, "wiki");
        string generatedTarget = SafePath(wikiRoot, cleaned, mustExist);
        if (Path.GetFullPath(Path.GetDirectoryName(generatedTarget) ?? wikiRoot) == Path.GetFullPath(wikiRoot)
            && !RootPages.Any(name => string.Equals(Path.GetFileNameWithoutExtension(generatedTarget), name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PathRejectedException("wiki/ 根下只许放 index/log/overview/conventions，其余要落到分区目录里");
        }
        return (generatedTarget, true, cleaned);
    }

    // ============================================================
    // 扫描与统计
    // ============================================================

    public static (Dictionary<string, string> Fields, string[] Tags, string Body) ParseFrontmatter(string text)
    {
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        List<string> tags = new();
        if (string.IsNullOrEmpty(text))
        {
            return (fields, tags.ToArray(), text ?? "");
        }
        Match match = FrontmatterRegex.Match(text);
        if (!match.Success)
        {
            return (fields, tags.ToArray(), text);
        }
        foreach (string line in match.Groups[1].Value.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (key.Equals("tags", StringComparison.OrdinalIgnoreCase))
            {
                tags.AddRange(value.Trim('[', ']').Split(',')
                    .Select(tag => tag.Trim().Trim('\'', '"'))
                    .Where(tag => tag.Length > 0));
                continue;
            }
            fields[key] = value.Trim('\'', '"');
        }
        return (fields, tags.ToArray(), text[match.Length..]);
    }

    /// <summary>frontmatter 里的 aliases：支持 [a, b]、a,b 和单个值三种写法。</summary>
    static IEnumerable<string> AliasNames(Dictionary<string, string> fields)
    {
        string raw = fields.TryGetValue("aliases", out string? aliases) && aliases.Length > 0
            ? aliases
            : fields.GetValueOrDefault("alias", "");
        raw = raw.Trim();
        if (raw.Length == 0)
        {
            return Array.Empty<string>();
        }
        if (raw.StartsWith('[') && raw.EndsWith(']'))
        {
            raw = raw[1..^1];
        }
        return raw.Split(',')
            .Select(part => part.Trim().Trim('"', '\''))
            .Where(part => part.Length > 0)
            .ToArray();
    }

    static bool IsAssetName(string name) =>
        RawExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    public static List<Page> Scan(string root, bool includeExternal = true)
    {
        List<Page> pages = new();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return pages;
        }
        string wikiRoot = Path.Combine(root, "wiki");
        if (Directory.Exists(wikiRoot))
        {
            foreach (string section in Sections)
            {
                string folder = Path.Combine(wikiRoot, section);
                if (!Directory.Exists(folder))
                {
                    continue;
                }
                foreach (string file in Directory.GetFiles(folder, "*.md").OrderBy(path => path, StringComparer.Ordinal))
                {
                    pages.Add(MetaOf(file, section, section + "/" + Path.GetFileName(file), true));
                }
            }
            foreach (string name in RootPages)
            {
                string file = Path.Combine(wikiRoot, name + ".md");
                if (File.Exists(file))
                {
                    pages.Add(MetaOf(file, "root", Path.GetFileName(file), true));
                }
            }
        }
        if (includeExternal)
        {
            pages.AddRange(ScanExternal(root));
        }
        return pages;
    }

    static Page MetaOf(string file, string section, string rel, bool generated)
    {
        FileInfo info = new(file);
        string head = ReadHead(file, 2000);
        (Dictionary<string, string> fields, string[] tags, _) = ParseFrontmatter(head);
        string title = fields.TryGetValue("title", out string? declared) && declared.Trim().Length > 0 ? declared : Path.GetFileNameWithoutExtension(file);
        return new Page(section, Path.GetFileNameWithoutExtension(file), rel, info.Length, info.LastWriteTimeUtc,
            title, tags, generated);
    }

    static List<Page> ScanExternal(string root)
    {
        List<Page> pages = new();
        foreach (string file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
                 .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (pages.Count >= MaxExternalPages)
            {
                break;
            }
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string[] segments = relative.Split('/');
            if (segments.Length == 0 || segments[0].Length == 0)
            {
                continue;
            }
            if (ExternalSkipDirs.Contains(segments[0]))
            {
                continue;
            }
            if (segments.Skip(1).Any(segment => segment.StartsWith('.')) || segments[0].StartsWith('.'))
            {
                continue;
            }
            pages.Add(MetaOf(file, Imported, relative, false));
        }
        return pages;
    }

    static string ReadHead(string file, int limit)
    {
        try
        {
            using FileStream stream = File.OpenRead(file);
            int length = (int)Math.Min(limit, stream.Length);
            byte[] buffer = new byte[length];
            stream.ReadExactly(buffer, 0, length);
            return Encoding.UTF8.GetString(buffer);
        }
        catch (Exception)
        {
            return "";
        }
    }

    static string ReadText(string file, long limit)
    {
        try
        {
            FileInfo info = new(file);
            if (!info.Exists)
            {
                return "";
            }
            using FileStream stream = File.OpenRead(file);
            int length = (int)Math.Min(limit, stream.Length);
            byte[] buffer = new byte[length];
            stream.ReadExactly(buffer, 0, length);
            return Encoding.UTF8.GetString(buffer);
        }
        catch (Exception)
        {
            return "";
        }
    }

    // ============================================================
    // 体检与索引（体检只读，绝不落盘；重建索引才写）
    // ============================================================

    public static LintReport Lint(string root, bool includeExternalLinks = false)
    {
        LintReport report = new();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            report.Issues.Add(new Issue("warn", "目录不存在", "-", "知识库路径读不到，检查配置里的「知识库·vault 本地目录」"));
            return report;
        }
        List<Page> pages = Scan(root);
        report.Pages.AddRange(pages);
        HashSet<string> names = pages.Select(page => page.Name).ToHashSet(StringComparer.Ordinal);
        //同名页面在真实 vault 里躲不掉（每个目录一个 README.md、index.md），
        //用 ToDictionary 会直接抛「An item with the same key has already been added」，所以按键去重、只留一份计数
        Dictionary<string, int> nameCounts = new(StringComparer.Ordinal);
        foreach (Page page in pages)
        {
            nameCounts[page.Name] = nameCounts.TryGetValue(page.Name, out int count) ? count + 1 : 1;
            if (page.Size > MaxPageBytes)
            {
                report.Issues.Add(new Issue("warn", "超大页", page.Rel, "超过单页 512 KiB 上限，建议拆分"));
            }
        }
        foreach (KeyValuePair<string, int> pair in nameCounts.Where(pair => pair.Value > 1))
        {
            report.Issues.Add(new Issue("info", "重名页", pair.Key, "有 " + pair.Value + " 个同名页面，双链会指向不确定的那一个"));
        }
        // Obsidian 的 [[别名]] 靠 frontmatter aliases 解析，不把别名也算进来会误报断链
        Dictionary<string, string> aliases = new(StringComparer.Ordinal);
        foreach (Page page in pages.Where(page => page.Section != "root"))
        {
            string head = ReadHead(PageFile(root, page), 4000);
            (Dictionary<string, string> fields, _, _) = ParseFrontmatter(head);
            foreach (string alias in AliasNames(fields))
            {
                if (alias.Length > 0 && names.Contains(alias) == false)
                {
                    aliases.TryAdd(alias, page.Name);
                }
            }
        }
        Dictionary<string, int> incoming = new(StringComparer.Ordinal);
        foreach (Page page in pages)
        {
            incoming.TryAdd(page.Name, 0);
        }
        foreach (Page page in pages.Where(page => page.Section != "root"))
        {
            string file = PageFile(root, page);
            CachedText cached = GetCachedText(file);
            string text = cached.Text;
            if (page.Generated && text.Trim().Length < 50)
            {
                report.Issues.Add(new Issue("warn", "空页", page.Rel, "生成区页面正文不到 50 字"));
            }
            foreach (string targetName in cached.Links)
            {
                if (targetName.Length == 0 || RootPages.Contains(targetName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!names.Contains(targetName) && aliases.ContainsKey(targetName) == false)
                {
                    // 外部笔记爱引用没进扫描的附件与资源，只对生成区报断链，免得刷屏
                    if ((page.Generated || includeExternalLinks) && IsAssetName(targetName) == false)
                    {
                        report.Issues.Add(new Issue("warn", "断链", page.Rel, "[[" + targetName + "]] 找不到对应页面"));
                    }
                    continue;
                }
                string canonical = names.Contains(targetName) ? targetName : aliases[targetName];
                incoming[canonical] = incoming.TryGetValue(canonical, out int count) ? count + 1 : 1;
            }
        }
        foreach (Page page in pages.Where(page => page.Generated
                     && (page.Section == "entities" || page.Section == "concepts" || page.Section == "analyses")))
        {
            if (incoming.TryGetValue(page.Name, out int count) && count == 0)
            {
                report.Issues.Add(new Issue("info", "孤立页", page.Rel, "没有任何页面引用它"));
            }
        }
        foreach (string section in Sections.Append(Imported))
        {
            report.BySection[section] = pages.Count(page => page.Section == section);
        }
        report.RootPageCount = pages.Count(page => page.Section == "root");
        report.ContentBytes = pages.Where(page => page.Section != "root").Sum(page => page.Size);
        report.RawCount = CountRaw(root);
        string logFile = Path.Combine(root, "wiki", "log.md");
        if (File.Exists(logFile))
        {
            // Python 那份用 splitlines()（末尾换行不算一行），C# 的 Split('\n') 会多算一行，故按行读
            int logLineCount = 0;
            string logText = ReadText(logFile, MaxPageBytes);
            using (StringReader logReader = new(logText))
            {
                while (logReader.ReadLine() != null)
                {
                    logLineCount++;
                }
            }
            report.LogLines = Math.Max(0, logLineCount - 1);
        }
        if (!Directory.Exists(Path.Combine(root, "wiki")))
        {
            report.Issues.Add(new Issue("warn", "未初始化", "-",
                "这个目录还没有生成区（wiki/）。你的原有笔记照样能读能搜能改；要开生成区就说一声，我来建骨架"));
        }
        if (report.RawCount == 0)
        {
            report.Issues.Add(new Issue("info", "没素材", "-", "raw/ 里还没有素材"));
        }
        return report;
    }

    static string PageFile(string root, Page page) =>
        Path.Combine(page.Generated ? Path.Combine(root, "wiki") : root, page.Rel.Replace('/', Path.DirectorySeparatorChar));

    public static void EnsureStructure(string root)
    {
        foreach (string folder in new[] { "raw", Path.Combine("raw", "assets") }
                 .Concat(Sections.Select(section => Path.Combine("wiki", section))))
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
        }
        WriteIfMissing(Path.Combine(root, "wiki", "index.md"),
            "# 知识库索引\n\n> 由笔记插件自动维护\n\n" + IndexStart + "\n## 总览\n\n- [[overview]]\n" + IndexEnd + "\n");
        WriteIfMissing(Path.Combine(root, "wiki", "log.md"), "# 操作日志\n\n");
        WriteIfMissing(Path.Combine(root, "wiki", "overview.md"), "# 总览\n\n");
        WriteIfMissing(Path.Combine(root, "wiki", "conventions.md"),
            "# 使用约定\n\n- 素材放 raw/，生成区在 wiki/ 的四个分区\n- 你自己的笔记放根目录或任意子目录，插件不擅自挪动\n");
    }

    static void WriteIfMissing(string file, string content)
    {
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content, new UTF8Encoding(false));
        }
    }

    public static void AppendLog(string root, string entry)
    {
        string logFile = Path.Combine(root, "wiki", "log.md");
        if (!Directory.Exists(Path.GetDirectoryName(logFile)))
        {
            EnsureStructure(root);
        }
        File.AppendAllText(logFile, "- " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            + " " + entry + Environment.NewLine, new UTF8Encoding(false));
    }

    public static int RebuildIndex(string root)
    {
        EnsureStructure(root);
        List<Page> pages = Scan(root);
        // 只重建标记块，保留用户在 index.md 里手写的其它段落
        StringBuilder auto = new();
        auto.Append("## 总览\n\n- [[overview]]\n");
        int total = 0;
        foreach (string section in Sections)
        {
            List<Page> inSection = pages.Where(page => page.Section == section).ToList();
            auto.Append("\n## ").Append(SectionLabels[section]).Append("（").Append(inSection.Count).Append("）\n\n");
            foreach (Page page in inSection)
            {
                auto.Append("- [[").Append(page.Name).Append("]]\n");
                total++;
            }
        }
        List<Page> external = pages.Where(page => page.Section == Imported).ToList();
        if (external.Count > 0)
        {
            auto.Append("\n## ").Append(SectionLabels[Imported]).Append("（").Append(external.Count).Append("）\n\n");
            foreach (Page page in external)
            {
                auto.Append("- [[").Append(page.Name).Append("]]\n");
            }
        }
        string indexFile = Path.Combine(root, "wiki", "index.md");
        string existing = File.Exists(indexFile) ? ReadText(indexFile, MaxPageBytes) : "";
        string block = IndexStart + "\n" + auto + IndexEnd;
        int start = existing.IndexOf(IndexStart, StringComparison.Ordinal);
        int end = start < 0 ? -1 : existing.IndexOf(IndexEnd, start, StringComparison.Ordinal);
        string merged;
        if (start >= 0 && end > start)
        {
            merged = existing[..start] + block + existing[(end + IndexEnd.Length)..];
        }
        else if (existing.Trim().Length == 0 || existing.Contains("> 由笔记插件自动维护", StringComparison.Ordinal))
        {
            merged = "# 知识库索引\n\n> 由笔记插件自动维护\n\n" + block + "\n";
        }
        else
        {
            merged = existing.TrimEnd() + "\n\n## 自动索引\n\n" + block + "\n";
        }
        File.WriteAllText(indexFile, merged, new UTF8Encoding(false));
        return total;
    }

    // ============================================================
    // 检索：中英混排轻量分词 + 相关度排序 + 页数与字符双上限
    // ============================================================

    public static string[] Terms(string text)
    {
        List<string> terms = new(LatinTerms.Matches((text ?? "").ToLowerInvariant()).Cast<Match>().Select(m => m.Value));
        foreach (Match run in CjkRuns.Matches(text ?? "").Cast<Match>())
        {
            string chunk = run.Value;
            if (chunk.Length == 1)
            {
                terms.Add(chunk);
                continue;
            }
            for (int index = 0; index + 1 < chunk.Length; index++)
            {
                terms.Add(chunk.Substring(index, 2));
            }
        }
        return terms.ToArray();
    }

    public static SearchOutcome Rank(string root, string query, int topK = 8, int perPageChars = 2600, int budgetChars = 14000,
        string section = "", string tag = "", int sinceDays = 0)
    {
        SearchOutcome outcome = new();
        HashSet<string> wanted = Terms(query).ToHashSet(StringComparer.Ordinal);
        DateTime cutoff = sinceDays > 0 ? DateTime.UtcNow.AddDays(-sinceDays) : DateTime.MinValue;
        List<(Page Page, string Body, string[] Terms)> docs = new();
        Dictionary<string, int> documentFrequency = new(StringComparer.Ordinal);
        double totalTerms = 0;
        foreach (Page page in Scan(root))
        {
            if (page.Section == "root")
            {
                continue;
            }
            string wantedSection = section.Trim();
            if (wantedSection.Length > 0
                && string.Equals(page.Section, wantedSection, StringComparison.OrdinalIgnoreCase) == false
                && (wantedSection.Equals("external", StringComparison.OrdinalIgnoreCase)
                    || wantedSection.Equals("imported", StringComparison.OrdinalIgnoreCase)) == false)
            {
                continue;
            }
            if (wantedSection.Equals("external", StringComparison.OrdinalIgnoreCase)
                || wantedSection.Equals("imported", StringComparison.OrdinalIgnoreCase))
            {
                if (page.Section != Imported)
                {
                    continue;
                }
            }
            if (tag.Trim().Length > 0 && page.Tags.Any(item => string.Equals(item, tag.Trim(), StringComparison.OrdinalIgnoreCase)) == false)
            {
                continue;
            }
            if (cutoff > DateTime.MinValue && page.Modified < cutoff)
            {
                continue;
            }
            outcome.ScannedPages++;
            CachedText cached = GetCachedText(PageFile(root, page));
            docs.Add((page, cached.Body, cached.Terms));
            totalTerms += cached.Terms.Length;
            if (wanted.Count > 0)
            {
                foreach (string term in wanted)
                {
                    if (cached.Terms.Contains(term, StringComparer.Ordinal))
                    {
                        documentFrequency[term] = documentFrequency.GetValueOrDefault(term, 0) + 1;
                    }
                }
            }
        }
        int documentCount = docs.Count;
        double averageLength = documentCount > 0 ? totalTerms / documentCount : 1;
        List<(double Score, Page Page, string Body)> scored = new();
        foreach ((Page Page, string Body, string[] Terms) doc in docs)
        {
            double score = 0;
            if (wanted.Count > 0)
            {
                double length = Math.Max(doc.Terms.Length, 1);
                foreach (string term in wanted)
                {
                    int tf = doc.Terms.Count(item => string.Equals(item, term, StringComparison.Ordinal));
                    if (tf == 0)
                    {
                        continue;
                    }
                    int df = documentFrequency.GetValueOrDefault(term, 0);
                    double idf = Math.Log(1 + (documentCount - df + 0.5) / (df + 0.5));
                    score += idf * (tf * 2.2) / (tf + 1.2 * (1 - 0.75 + 0.75 * length / Math.Max(averageLength, 1)));
                }
                int titleHits = Terms(doc.Page.Title).Count(term => wanted.Contains(term));
                score += titleHits * 2.5;
                if (doc.Page.Tags.Any(item => wanted.Contains(item.ToLowerInvariant())))
                {
                    score += 1.5;
                }
                string trimmed = query.Trim();
                if (trimmed.Length >= 2 && doc.Body.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    score += 2;
                }
            }
            if (score > 0 || wanted.Count == 0)
            {
                scored.Add((score, doc.Page, doc.Body));
            }
        }
        outcome.Candidates = scored.Count;
        foreach ((double Score, Page Page, string Body) entry in scored
                 .OrderByDescending(item => item.Score).ThenByDescending(item => item.Page.Modified).ToList())
        {
            if (outcome.Hits.Count >= Math.Max(1, topK) || outcome.Chars >= budgetChars)
            {
                break;
            }
            string snippet = SnippetAround(entry.Body, wanted, query, perPageChars);
            outcome.Chars += snippet.Length;
            outcome.Hits.Add(new SearchHit(entry.Page.Rel, entry.Page.Title, entry.Page.Section,
                entry.Page.Generated, Math.Round(entry.Score, 2), snippet));
        }
        return outcome;
    }

    static string SnippetAround(string body, HashSet<string> wanted, string query, int max)
    {
        if (body.Length <= max)
        {
            return body;
        }
        int first = query.Trim().Length > 0 ? body.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) : -1;
        if (first < 0)
        {
            foreach (string term in wanted)
            {
                int index = body.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (first < 0 || index < first))
                {
                    first = index;
                }
            }
        }
        if (first < 0)
        {
            first = 0;
        }
        int start = Math.Max(0, first - max / 3);
        if (start + max > body.Length)
        {
            start = Math.Max(0, body.Length - max);
        }
        int length = Math.Min(max, body.Length - start);
        return (start > 0 ? "…" : "") + body.Substring(start, length) + (start + length < body.Length ? "…" : "");
    }

    // ============================================================
    // 页面写（生成区与外部笔记都能写，但一律在知识库根之内）
    // ============================================================

    public static (string Rel, bool Generated, long Size) WritePage(string root, string rel, string content, bool append = false)
    {
        string cleaned = (rel ?? "").Trim().Replace('\\', '/');
        if (!cleaned.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new PathRejectedException("只能写 .md 页面");
        }
        (string target, bool generated, _) = ResolveTarget(root, cleaned);
        byte[] bytes = Encoding.UTF8.GetBytes(content ?? "");
        string body = content ?? "";
        if (append && File.Exists(target))
        {
            string old = ReadText(target, MaxPageBytes);
            body = old.TrimEnd('\n', '\r') + "\n\n" + (content ?? "").Trim() + "\n";
            bytes = Encoding.UTF8.GetBytes(body);
        }
        if (bytes.LongLength > MaxPageBytes)
        {
            throw new PathRejectedException("单页上限 512 KiB，这份有 " + bytes.LongLength + " 字节");
        }
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        // 先写临时文件再覆盖，避免进程中断把原页面留成半截
        string temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, body, new UTF8Encoding(false));
            File.Move(temp, target, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        return (cleaned, generated, new FileInfo(target).Length);
    }

    public static string DeletePage(string root, string rel)
    {
        (string target, bool generated, _) = ResolveTarget(root, rel, mustExist: true);
        if (generated && Path.GetFileNameWithoutExtension(target) is string stem && RootPages.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            throw new PathRejectedException("index/log/overview/conventions 这四个核心页不给删");
        }
        // 不物理删除：挪进 vault 的 .trash/，Obsidian 和用户都还能找回
        string cleaned = rel.Trim().Replace('\\', '/');
        string trash = Path.Combine(root, ".trash", cleaned.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(trash))
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            trash = trash + "-" + stamp;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
        File.Move(target, trash);
        return cleaned;
    }

    /// <summary>改名并把全库双链里的旧名换掉；返回更新了多少个文件。</summary>
    public static (string NewRel, int UpdatedFiles) RenamePage(string root, string oldRel, string newRel, bool updateLinks = true)
    {
        string oldCleaned = (oldRel ?? "").Trim().Replace('\\', '/');
        string newCleaned = (newRel ?? "").Trim().Replace('\\', '/');
        (string oldTarget, _, _) = ResolveTarget(root, oldCleaned, mustExist: true);
        (string newTarget, _, _) = ResolveTarget(root, newCleaned);
        if (File.Exists(newTarget))
        {
            throw new PathRejectedException("目标页面已存在：" + newCleaned);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(newTarget)!);
        File.Move(oldTarget, newTarget);
        string oldName = Path.GetFileNameWithoutExtension(oldTarget);
        string newName = Path.GetFileNameWithoutExtension(newTarget);
        int updated = updateLinks ? ReplaceLinks(root, oldName, newName) : 0;
        return (newCleaned, updated);
    }

    /// <summary>把 source 正文并入 target，再把指向 source 的链接改成 target；source 进 .trash/。</summary>
    public static (string Target, int UpdatedFiles, bool SourceRemoved) MergePages(string root, string sourceRel, string targetRel,
        bool deleteSource = true)
    {
        string sourceCleaned = (sourceRel ?? "").Trim().Replace('\\', '/');
        string targetCleaned = (targetRel ?? "").Trim().Replace('\\', '/');
        (string sourceTarget, _, _) = ResolveTarget(root, sourceCleaned, mustExist: true);
        (string targetTarget, _, _) = ResolveTarget(root, targetCleaned, mustExist: true);
        if (sourceTarget == targetTarget)
        {
            throw new PathRejectedException("源页面和目标页面是同一个");
        }
        string sourceName = Path.GetFileNameWithoutExtension(sourceTarget);
        string targetName = Path.GetFileNameWithoutExtension(targetTarget);
        string sourceText = ReadText(sourceTarget, MaxPageBytes);
        string sourceBody = ParseFrontmatter(sourceText).Body.Trim();
        string targetText = ReadText(targetTarget, MaxPageBytes).TrimEnd();
        string merged = targetText + "\n\n## 合并自 [[" + sourceName + "]]\n\n" + sourceBody + "\n";
        WriteAtomic(targetTarget, merged);
        int updated = ReplaceLinks(root, sourceName, targetName);
        bool removed = false;
        if (deleteSource)
        {
            DeletePage(root, sourceCleaned);
            removed = true;
        }
        return (targetCleaned, updated, removed);
    }

    static int ReplaceLinks(string root, string oldName, string newName)
    {
        int updated = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string head = relative.Split('/')[0];
            if (head.Equals(".trash", StringComparison.OrdinalIgnoreCase)
                || head.Equals(".obsidian", StringComparison.OrdinalIgnoreCase)
                || head.Equals("raw", StringComparison.OrdinalIgnoreCase)
                || head.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || head.Equals("assets", StringComparison.OrdinalIgnoreCase)
                || head.Equals("templates", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string text = ReadText(file, MaxPageBytes);
            string replaced = AnyLinkRegex.Replace(text, match =>
            {
                string targetPart = match.Groups[1].Value.Trim();
                int slash = targetPart.LastIndexOf('/');
                string prefix = slash >= 0 ? targetPart[..(slash + 1)] : "";
                string stem = slash >= 0 ? targetPart[(slash + 1)..] : targetPart;
                if (stem.Equals(oldName, StringComparison.OrdinalIgnoreCase) == false)
                {
                    return match.Value;
                }
                return "[[" + prefix + newName + (match.Groups[2].Value ?? "") + "]]";
            });
            if (replaced != text)
            {
                WriteAtomic(file, replaced);
                updated++;
            }
        }
        return updated;
    }

    static void WriteAtomic(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, file, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    // ============================================================
    // 收录：raw 落料 + 把模型编译出来的 JSON 变成生成区页面（只算不写，好测）
    // ============================================================

    public sealed record Draft(string Rel, string Title, string Body, bool Conflict);

    /// <summary>素材原文存进 raw/（同名不覆盖，加时间后缀，把撞名的情况报回去）。</summary>
    public static (string Rel, bool Renamed) SaveRaw(string root, string title, string content, string sourceUrl)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string slug = Slugify(string.IsNullOrWhiteSpace(title) ? "素材-" + stamp : title);
        string rel = slug + ".md";
        string target = Path.Combine(root, "raw", rel);
        bool renamed = false;
        if (File.Exists(target))
        {
            rel = slug + "-" + stamp + ".md";
            target = Path.Combine(root, "raw", rel);
            renamed = true;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        StringBuilder builder = new();
        builder.Append("# ").Append(string.IsNullOrWhiteSpace(title) ? slug : title.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            builder.Append("> 来源：").Append(sourceUrl.Trim()).Append("\n\n");
        }
        builder.Append((content ?? "").Trim()).Append('\n');
        File.WriteAllText(target, builder.ToString(), new UTF8Encoding(false));
        return (rel, renamed);
    }

    /// <summary>
    /// 把模型给的 JSON 编译成页面计划：一页一名字，名字与已有生成区页面撞上就标 Conflict（让调用方决定，绝不静默覆盖），
    /// 超过 maxPages 就截断并报数；顺带把模型回复里裹的说明文字剥掉。
    /// </summary>
    public static (List<Draft> Pages, int Dropped, List<string> Problems) PlanCompiled(string modelJson, string rawRel,
        IReadOnlyCollection<Page> existing, int maxPages, string sourceUrl = "", string tags = "")
    {
        List<Draft> pages = new();
        List<string> problems = new();
        string cleaned = modelJson ?? "";
        int start = cleaned.IndexOf('{');
        int end = cleaned.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return (pages, 0, new List<string> { "模型回复里找不到 JSON 对象" });
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(cleaned[start..(end + 1)]);
        }
        catch (Exception parseError)
        {
            return (pages, 0, new List<string> { "JSON 解析失败：" + parseError.Message });
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            string rawLink = Path.GetFileNameWithoutExtension(rawRel);
            string sourceTitle = Text(root, "title");
            void Add(string section, string name, string body)
            {
                string trimmedName = (name ?? "").Trim();
                if (trimmedName.Length == 0)
                {
                    return;
                }
                if ((body ?? "").Trim().Length == 0)
                {
                    problems.Add(section + "「" + trimmedName + "」正文是空的，跳过");
                    return;
                }
                string rel = section + "/" + Slugify(trimmedName) + ".md";
                if (pages.Any(draft => string.Equals(draft.Rel, rel, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add("同一批里重名，只留第一条：" + rel);
                    return;
                }
                StringBuilder builder = new();
                List<string> pageTags = new() { section };
                foreach (string extraTag in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    pageTags.Add(extraTag);
                }
                builder.Append("---\ntitle: ").Append(trimmedName.Replace(':', ' '))
                    .Append("\ntags: [").Append(string.Join(", ", pageTags)).Append("]\n---\n\n");
                builder.Append("# ").Append(trimmedName).Append("\n\n");
                if (section == "sources" && sourceUrl.Trim().Length > 0)
                {
                    builder.Append("> 来源：").Append(sourceUrl.Trim()).Append("\n\n");
                }
                builder.Append(body.Trim()).Append("\n\n");
                builder.Append("来源素材：[[").Append(rawLink).Append("]]\n");
                pages.Add(new Draft(rel, trimmedName, builder.ToString(), existing.Any(page => page.Generated
                    && string.Equals(page.Name, Path.GetFileNameWithoutExtension(rel), StringComparison.Ordinal))));
            }

            string summary = Text(root, "summary");
            if (summary.Trim().Length > 0)
            {
                Add("sources", sourceTitle.Length > 0 ? sourceTitle : rawLink, summary);
            }
            int limit = Math.Clamp(maxPages, 1, 200);
            int dropped = 0;
            foreach (string section in new[] { "entities", "concepts", "analyses" })
            {
                if (root.TryGetProperty(section, out JsonElement items) == false)
                {
                    continue;
                }
                if (items.ValueKind != JsonValueKind.Array)
                {
                    problems.Add(section + " 不是数组，忽略了");
                    continue;
                }
                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (pages.Count >= limit)
                    {
                        dropped++;
                        continue;
                    }
                    string name = Text(item, "name");
                    if (name.Length == 0)
                    {
                        name = Text(item, "title");
                    }
                    string body = Text(item, "text");
                    if (body.Length == 0)
                    {
                        body = Text(item, "body");
                    }
                    Add(section, name, body);
                }
            }
            if (dropped > 0)
            {
                problems.Add("超过单次收录上限 " + limit + " 页，丢了 " + dropped + " 条");
            }
            if (pages.Count == 0)
            {
                problems.Add("JSON 里没有可用页面（要 title+summary，或 entities/concepts/analyses 数组）");
            }
            return (pages, dropped, problems);
        }
    }

    static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>按计划落盘；冲突页按 skip/merge/overwrite 处理，dryRun 时只算不写。</summary>
    public static int WriteDrafts(string root, IReadOnlyList<Draft> drafts, string conflictPolicy = "skip", bool dryRun = false)
    {
        string policy = conflictPolicy.Trim().ToLowerInvariant();
        int written = 0;
        foreach (Draft draft in drafts)
        {
            if (draft.Conflict)
            {
                if (policy == "skip")
                {
                    continue;
                }
                if (policy == "merge")
                {
                    if (dryRun)
                    {
                        written++;
                        continue;
                    }
                    (string target, _, _) = ResolveTarget(root, draft.Rel);
                    if (File.Exists(target))
                    {
                        string old = ReadText(target, MaxPageBytes).TrimEnd();
                        string body = ParseFrontmatter(draft.Body).Body.Trim();
                        WriteAtomic(target, old + "\n\n## 补充\n\n" + body + "\n");
                    }
                    else
                    {
                        WritePage(root, draft.Rel, draft.Body);
                    }
                    written++;
                    continue;
                }
                if (policy != "overwrite")
                {
                    continue;
                }
            }
            if (dryRun == false)
            {
                WritePage(root, draft.Rel, draft.Body);
            }
            written++;
        }
        return written;
    }

    public static string Slugify(string name)
    {
        string slug = SlugNoise.Replace((name ?? "").Trim().ToLowerInvariant(), "-");
        slug = Regex.Replace(slug, "-{2,}", "-").Trim('-');
        return slug.Length > 0 ? slug : "untitled";
    }
}
