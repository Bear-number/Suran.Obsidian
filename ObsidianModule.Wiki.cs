using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.FunctionCaller;
using Alife.Framework;
using Microsoft.SemanticKernel.Agents;

namespace Suran.Obsidian;

/// <summary>
/// 知识库半区：体检、索引重建、文件侧检索与页面读写。全部走文件系统，Obsidian 没开着也能用；
/// 逻辑都在 VaultWiki 里，路径一律限制在配置的 vault 目录之内。
/// </summary>
public partial class ObsidianModule
{
    const string VaultNotConfigured = "知识库半区没配：把 Obsidian 那个 vault 的本地目录（形如 D:\\我的obsidian知识库）填进本插件配置的「知识库·vault 本地目录」";

    string vaultPath = "";

    string RequireVault(bool needsWrite)
    {
        if (vaultPath.Length == 0)
        {
            throw new Exception(VaultNotConfigured);
        }
        if (!Directory.Exists(vaultPath))
        {
            throw new Exception("知识库路径读不到：" + vaultPath + "（检查「知识库·vault 本地目录」，注意这是本地磁盘路径，不是 note_* 那种 vault 内相对路径）");
        }
        if (needsWrite && Configuration.WikiAllowFileWrite == false)
        {
            throw new Exception("「知识库·允许文件系统写入」是关的，这一版只准读与体检；要写让主人打开那个开关");
        }
        return vaultPath;
    }

    [XmlFunction(FunctionMode.OneShot, "wiki_status")]
    [Description("知识库体检（不碰 Obsidian、不发 HTTP，Obsidian 关着也能用）：给页面数与分区统计、空页/断链/孤立页/未初始化/无素材这些毛病，外加最近的维护日志。要不要体检由 check 决定，只看数就 check=false")]
    public async Task WikiStatus(
        [Description("顺带做一遍体检，默认 true；false 只报统计")] bool check = true,
        [Description("列几行最近维护日志，0=不列")] int recent = 5)
    {
        string root = RequireVault(false);
        VaultWiki.LintReport report = check
            ? await Task.Run(() => VaultWiki.Lint(root, Configuration.CheckExternalLinks))
            : BuildStatsOnly(root);
        List<string> lines = new()
        {
            "[" + Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) + "] 页面 " + report.Pages.Count
            + "（生成区 " + report.BySection.GetValueOrDefault("sources", 0) + "+" + report.BySection.GetValueOrDefault("entities", 0)
            + "+" + report.BySection.GetValueOrDefault("concepts", 0) + "+" + report.BySection.GetValueOrDefault("analyses", 0)
            + "，外部笔记 " + report.BySection.GetValueOrDefault(VaultWiki.Imported, 0) + "，核心页 " + report.RootPageCount + "）"
            + "｜素材 " + report.RawCount + " 份｜正文共 " + (report.ContentBytes / 1024) + " KiB"
        };
        foreach (VaultWiki.Issue issue in report.Issues)
        {
            lines.Add("  " + (issue.Level == "warn" ? "!" : "·") + " [" + issue.Kind + "] " + issue.Page + " — " + issue.Detail);
        }
        if (report.Issues.Count == 0 && check)
        {
            lines.Add("  体检没发现问题");
        }
        if (recent > 0)
        {
            List<string> log = await Task.Run(() => RecentLog(root, Math.Clamp(recent, 1, 40)));
            if (log.Count > 0)
            {
                lines.Add("  最近维护：");
                lines.AddRange(log.Select(entry => "    " + entry));
            }
        }
        lines.Add("  要看图谱与双链跳转就直接用 Obsidian 打开这个目录；要重建索引 wiki_index，要按关键词找内容 wiki_search。");
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    [XmlFunction(FunctionMode.OneShot, "wiki_index")]
    [Description("按盘上实际页面重建知识库的 wiki/index.md（生成区没骨架时会连 raw/ 与四个分区一起建，并记一行维护日志）。写操作，会动文件")]
    public async Task WikiIndex()
    {
        string root = RequireVault(true);
        int pages = 0;
        await Task.Run(() =>
        {
            pages = VaultWiki.RebuildIndex(root);
            VaultWiki.AppendLog(root, "重建索引，收录分区页 " + pages + " 条");
        });
        interactor.Poke("已重建 " + Path.Combine("wiki", "index.md") + "：分区页 " + pages
            + " 条（外部笔记不进索引正文，但会被统计与体检看到）。要看细节用 wiki_status");
    }

    [XmlFunction(FunctionMode.OneShot, "wiki_search")]
    [Description("在知识库里按关键词找页（纯文件侧，BM25 排序，标题/标签加权），支持 section/tag/最近天数过滤，返回 rel｜标题｜分区｜相关度 + 命中附近片段。与 note_search 的区别：不要求 Obsidian 在跑")]
    public async Task WikiSearch(
        [Description("关键词，中文或英文都行；留空等于按新鲜度列页")] string query,
        [Description("最多几页，1-20，默认 8")] int top = 8,
        [Description("总字符预算，先到为止，默认 14000")] int budgetChars = 14000,
        [Description("只搜某分区：sources/entities/concepts/analyses/external，留空全库")] string section = "",
        [Description("只搜带该标签的页面，留空不限")] string tag = "",
        [Description("只看最近几天，0=不限")] int sinceDays = 0)
    {
        string root = RequireVault(false);
        int pages = Math.Clamp(top, 1, 20);
        int effectiveBudget = Math.Min(Math.Clamp(budgetChars, 2000, 60000), Math.Clamp(Configuration.MaxOutputChars, 500, 20000));
        VaultWiki.SearchOutcome outcome = await Task.Run(() => VaultWiki.Rank(root, query ?? "", pages, 2600, effectiveBudget,
            section ?? "", tag ?? "", Math.Clamp(sinceDays, 0, 3650)));
        if (outcome.Hits.Count == 0)
        {
            interactor.Poke("wiki_search「" + query + "」没命中。扫了 " + outcome.ScannedPages + " 页（不含 index 等核心页）；"
                + "换个说法（中文可以试更短的词），或用 wiki_status 看库里有什么");
            return;
        }
        List<string> lines = new()
        {
            "[" + Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) + "] 命中 " + outcome.Hits.Count + " 页"
            + "（候选 " + outcome.Candidates + "，用了 " + outcome.Chars + " 字符；要看全文接着用 wiki_page mode=read）"
        };
        for (int index = 0; index < outcome.Hits.Count; index++)
        {
            VaultWiki.SearchHit hit = outcome.Hits[index];
            string snippet = hit.Snippet.Replace("\r", " ").Replace("\n", " ").Trim();
            if (snippet.Length > Configuration.SummaryMaxLength)
            {
                snippet = snippet[..Configuration.SummaryMaxLength] + "…";
            }
            lines.Add((index + 1) + ". " + hit.Rel + " | " + hit.Title + " | " + hit.Section + " | 相关度:"
                + hit.Score.ToString("0.##", CultureInfo.InvariantCulture) + " | " + snippet);
        }
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    [XmlFunction(FunctionMode.Content, "wiki_page")]
    [Description("直接读写知识库里的一个 .md 页面（走文件系统，不要求 Obsidian 在跑）。mode：read 读全文 / write 整篇覆盖 / append 追加 / delete 删除。"
        + "path 是知识库内相对路径：生成区写 sources|entities|concepts|analyses/xxx.md 或 index.md 等核心页名，你自己的笔记写相对知识库根的路径（如 想法/AAA.md）。"
        + "write/append 把正文放在开闭标签之间；read 与 delete 标签留空。核心页 index/log/overview/conventions 不给删")]
    public async Task WikiPage(
        XmlExecutorContext context,
        [Description("知识库内相对路径，如 entities/steam-plugin.md 或 想法/AAA.md")] string path,
        [Description("read（默认）/ write / append / delete")] string mode = "read")
    {
        if (context.CallMode != CallMode.Closing)
        {
            return;
        }
        string resolvedMode = RequireOneOf(mode, "wiki_page 的 mode", "read", "write", "append", "delete");
        if (path == null || path.Trim().Length == 0)
        {
            throw new Exception("wiki_page 缺 path（知识库内相对路径）");
        }
        string root = RequireVault(resolvedMode != "read");
        string normalized = path.Trim().Replace('\\', '/');
        if (resolvedMode == "read")
        {
            (string target, bool generated, _) = VaultWiki.ResolveTarget(root, normalized, mustExist: true);
            VaultWiki.Page meta = DescribeFile(root, target, generated, normalized);
            string text = ReadFull(target);
            interactor.Poke(Truncate("[" + normalized + " | " + (generated ? "生成区" : "外部笔记") + " | " + meta.Section
                + " | " + meta.Size + " 字节 | 改于 " + meta.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                + (meta.Tags.Length > 0 ? " | tags: " + string.Join(",", meta.Tags) : "") + "]\n" + text));
            return;
        }
        if (resolvedMode == "delete")
        {
            string deleted = await Task.Run(() => VaultWiki.DeletePage(root, normalized));
            VaultWiki.AppendLog(root, "删除页面 " + deleted);
            interactor.Poke("已删除 " + deleted + "（挪进 vault 的 .trash/，Obsidian 和文件系统都还能找回）");
            return;
        }
        string content = context.FullContent;
        if (content.Trim().Length == 0)
        {
            throw new Exception("wiki_page 的 " + resolvedMode + " 要在开闭标签之间给出正文（空着执行会把 " + normalized + " 覆盖成空的）");
        }
        (string rel, bool isGenerated, long size) = await Task.Run(() => VaultWiki.WritePage(root, normalized, content, resolvedMode == "append"));
        interactor.Poke((resolvedMode == "write" ? "已写入 " : "已追加到 ") + rel + "（" + size + " 字节，"
            + (isGenerated ? "生成区" : "外部笔记") + "）。新建或大改页面后建议跑一次 wiki_index 更新索引");
    }

    [XmlFunction(FunctionMode.OneShot, "wiki_rename")]
    [Description("改名知识库页面，并把全库 [[旧名]] 替换成 [[新名]]（写操作）。oldPath/newPath 是库内相对路径")]
    public async Task WikiRename(
        [Description("原相对路径，如 entities/old.md")] string oldPath,
        [Description("新相对路径，如 concepts/new.md")] string newPath,
        [Description("是否同时替换全库双链，默认 true")] bool updateLinks = true)
    {
        string root = RequireVault(true);
        (string rel, int updated) = await Task.Run(() => VaultWiki.RenamePage(root, oldPath, newPath, updateLinks));
        VaultWiki.AppendLog(root, "改名 " + oldPath.Trim() + " → " + rel + "，更新双链 " + updated + " 个文件");
        interactor.Poke("已改名 " + oldPath.Trim() + " → " + rel + "，更新双链 " + updated + " 个文件");
    }

    [XmlFunction(FunctionMode.OneShot, "wiki_merge")]
    [Description("把 source 页面正文并入 target，并把指向 source 的双链改成 target（写操作）。默认 source 移入 .trash/")]
    public async Task WikiMerge(
        [Description("源页面相对路径")] string sourcePath,
        [Description("目标页面相对路径")] string targetPath,
        [Description("合并后是否删除源页面，默认 true")] bool deleteSource = true)
    {
        string root = RequireVault(true);
        (string target, int updated, bool removed) = await Task.Run(() => VaultWiki.MergePages(root, sourcePath, targetPath, deleteSource));
        VaultWiki.AppendLog(root, "合并 " + sourcePath.Trim() + " → " + target + "，更新双链 " + updated + " 个文件");
        interactor.Poke("已合并 " + sourcePath.Trim() + " → " + target + "，更新双链 " + updated + " 个文件"
            + (removed ? "，源页面已移入 .trash/" : ""));
    }

    [XmlFunction(FunctionMode.Content, "wiki_ingest")]
    [Description("把一段素材收进知识库：原文落进 raw/，再让生成模型编译成来源/实体/概念/分析页（带 [[双链]]），最后重建索引。支持 dryRun、conflictPolicy、tags")]
    public async Task WikiIngest(
        XmlExecutorContext context,
        [Description("素材标题，留空就取正文第一行")] string title = "",
        [Description("来源链接，可留空")] string sourceUrl = "",
        [Description("只生成计划、不落盘")] bool dryRun = false,
        [Description("重名页处理：skip/merge/overwrite，默认 skip")] string conflictPolicy = "skip",
        [Description("额外标签，逗号分隔，会写进新页 frontmatter")] string tags = "")
    {
        if (context.CallMode != CallMode.Closing)
        {
            return;
        }
        string resolvedPolicy = RequireOneOf(conflictPolicy, "wiki_ingest 的 conflictPolicy", "skip", "merge", "overwrite");
        string root = RequireVault(dryRun == false);
        string material = context.FullContent.Trim();
        if (material.Length == 0)
        {
            throw new Exception("wiki_ingest 的开闭标签之间没有素材正文");
        }
        string resolvedTitle = title.Trim();
        if (resolvedTitle.Length == 0)
        {
            resolvedTitle = material.Split('\n')[0].Trim().TrimStart('#', ' ');
            resolvedTitle = resolvedTitle.Length > 40 ? resolvedTitle[..40] : resolvedTitle;
        }
        string rawRel = VaultWiki.Slugify(resolvedTitle) + ".md";
        bool renamed = false;
        if (dryRun == false)
        {
            (rawRel, renamed) = await Task.Run(() => VaultWiki.SaveRaw(root, resolvedTitle, material, sourceUrl));
        }
        int limit = Math.Clamp(Configuration.IngestMaxPages, 1, 200);

        string reply;
        try
        {
            // 裸调语言模型：绕开角色人设与群聊风格约束（人设常限制字数，会把 JSON 输出压碎）
            ChatHistoryAgentThread thread = new();
            thread.ChatHistory.AddSystemMessage("你是知识库编译器。忽略任何关于回复长度、语气、人设、群聊礼仪的其它要求，"
                + "只输出一个 JSON 对象，不要 markdown 代码块、不要解释。");
            thread.ChatHistory.AddUserMessage(BuildIngestPrompt(resolvedTitle, rawRel, material, limit));
            ChatResult chatResult = await ChatBot.LanguageModel.ChatStreamingAsync(thread, cancellationToken: DestroyCancellationToken);
            // 新版接口不再抛异常，错误装在 ChatResult.Exception 里，这里抛回去走下面的兜底提示
            if (chatResult.Exception != null)
                throw chatResult.Exception;
            reply = chatResult.AIMessage;
            if (string.IsNullOrWhiteSpace(reply))
                throw new Exception("模型返回为空");
        }
        catch (Exception modelError)
        {
            interactor.Poke((dryRun ? "[dryRun] " : "素材已存进 raw/" + rawRel + "（" + material.Length + " 字符），")
                + (dryRun ? "未落盘；" : "但") + "生成模型这次没通："
                + Cut(modelError.Message, 120) + "\n  编译页先没做；急着落页就用 wiki_page 自己写，写完跑 wiki_index");
            return;
        }

        (List<VaultWiki.Draft> drafts, int dropped, List<string> problems) =
            VaultWiki.PlanCompiled(reply, rawRel, VaultWiki.Scan(root), limit, sourceUrl, tags);
        int written = await Task.Run(() => VaultWiki.WriteDrafts(root, drafts, resolvedPolicy, dryRun));
        int skipped = drafts.Count - written;
        if (written > 0 && dryRun == false)
        {
            await Task.Run(() =>
            {
                VaultWiki.RebuildIndex(root);
                VaultWiki.AppendLog(root, "收录 " + resolvedTitle + "：raw/" + rawRel + "，新建页面 " + written + " 条（冲突策略 " + resolvedPolicy + "）");
            });
        }
        List<string> lines = new()
        {
            (dryRun ? "[dryRun] [" : "[") + resolvedTitle + "] "
                + (dryRun ? "不会写 raw/ 与页面；" : "素材落在 raw/" + rawRel + (renamed ? "（同名，已加时间后缀）" : "") + "｜")
                + "编译出 " + drafts.Count + " 页，" + (dryRun ? "计划写 " : "写了 ") + written + " 页"
                + (skipped > 0 ? "，跳过 " + skipped + " 个重名" : "")
        };
        foreach (string problem in problems.Take(8))
        {
            lines.Add("  · " + problem);
        }
        if (drafts.Count > 0)
        {
            lines.Add("  页面：" + string.Join("、", drafts.Take(8).Select(draft => draft.Rel)) + (drafts.Count > 8 ? "…" : ""));
        }
        if (skipped > 0)
        {
            lines.Add("  重名页按 " + resolvedPolicy + " 处理（要逐个改就用 wiki_page，或先 wiki_rename）");
        }
        lines.Add(dryRun ? "  dryRun 没有落盘；确认后去掉 dryRun 再跑"
            : written > 0 ? "  索引已重建，想看断链空页跑 wiki_status"
            : "  这次只落了 raw/，索引没动");
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    static string Cut(string text, int max)
    {
        string one = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }

    string BuildIngestPrompt(string title, string rawRel, string material, int limit)
    {
        int budget = Math.Clamp(Configuration.MaxOutputChars, 2000, 20000) * 2;
        string body = material.Length > budget ? material[..budget] + "\n…（素材过长，后面截断了）" : material;
        StringBuilder prompt = new();
        prompt.Append("素材标题：").Append(title).Append("\n素材文件：raw/").Append(rawRel)
            .Append("\n最多产出 ").Append(limit).Append(" 页。\n\n只输出这个形状的 JSON：\n")
            .Append("{\"title\":\"来源页标题\",\"summary\":\"这素材到底讲了什么（150-300字，别抄目录）\",")
            .Append("\"entities\":[{\"name\":\"人物/组织/工具名\",\"text\":\"它是什么、与素材的关系\"}],")
            .Append("\"concepts\":[{\"name\":\"方法/理论/模式名\",\"text\":\"讲清概念本身\"}],")
            .Append("\"analyses\":[{\"name\":\"值得单独成页的论断\",\"text\":\"综合论述\"}]}\n\n")
            .Append("要求：name 用页面标题的写法（会被转成文件名，别带路径与 .md）；每页正文 60-240 字；")
            .Append("提到已知的实体或概念时用 [[名字]] 互链；没有那一类就给空数组，别编。\n\n素材正文：\n")
            .Append(body);
        return prompt.ToString();
    }

    VaultWiki.LintReport BuildStatsOnly(string root)
    {
        VaultWiki.LintReport report = new();
        List<VaultWiki.Page> pages = VaultWiki.Scan(root);
        report.Pages.AddRange(pages);
        foreach (string section in VaultWiki.Sections.Append(VaultWiki.Imported))
        {
            report.BySection[section] = pages.Count(page => page.Section == section);
        }
        report.RootPageCount = pages.Count(page => page.Section == "root");
        report.ContentBytes = pages.Where(page => page.Section != "root").Sum(page => page.Size);
        report.RawCount = VaultWiki.CountRaw(root);
        return report;
    }

    static List<string> RecentLog(string root, int limit)
    {
        string logFile = Path.Combine(root, "wiki", "log.md");
        if (!File.Exists(logFile))
        {
            return new List<string>();
        }
        return File.ReadLines(logFile)
            .Where(line => line.TrimStart().StartsWith("- "))
            .TakeRight(limit)
            .Reverse()
            .Select(line => line.TrimStart("- ".ToCharArray()))
            .ToList();
    }

    static VaultWiki.Page DescribeFile(string root, string target, bool generated, string rel)
    {
        FileInfo info = new(target);
        (Dictionary<string, string> fields, string[] tags, _) = VaultWiki.ParseFrontmatter(ReadHeadOf(target, 2000));
        string section = !generated ? VaultWiki.Imported
            : VaultWiki.LooksGenerated(rel) ? rel.Split('/')[0] : "root";
        string title = fields.TryGetValue("title", out string? declared) && declared.Trim().Length > 0 ? declared : Path.GetFileNameWithoutExtension(target);
        return new VaultWiki.Page(section, Path.GetFileNameWithoutExtension(target), rel, info.Length, info.LastWriteTimeUtc, title, tags, generated);
    }

    static string ReadHeadOf(string file, int limit) => ReadFull(file, limit);

    static string ReadFull(string file, int limit = -1)
    {
        try
        {
            byte[] all = File.ReadAllBytes(file);
            if (limit > 0 && all.LongLength > limit)
            {
                all = all[..limit];
            }
            return System.Text.Encoding.UTF8.GetString(all);
        }
        catch (Exception)
        {
            return "";
        }
    }
}

static class EnumerableTail
{
    public static IEnumerable<T> TakeRight<T>(this IEnumerable<T> source, int count)
    {
        List<T> items = source.ToList();
        return count >= items.Count ? items : items.GetRange(items.Count - count, count);
    }
}
