using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Alife.Function.FunctionCaller;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace Suran.Obsidian;

public class ObsidianConfig
{
    [DisplayName("连接·Local REST API 地址")]
    [Description("Obsidian 社区插件 Local REST API 设置页上显示的服务地址。5.x 默认是 https://127.0.0.1:27124（本地自签证书，要靠下面『信任自签证书』才连得上）；在设置里勾了 Enable HTTP server 也可以填明文 http://127.0.0.1:27123。留空则七个工具全部直接报未配置")]
    public string BaseUrl { get; set; } = "";

    [DisplayName("连接·API Key")]
    [Description("Local REST API 设置页生成的密钥，按 Authorization: Bearer 发出。留空一律 401；填错累积到十次/分钟会被服务端 429 限速一整分钟")]
    public string ApiKey { get; set; } = "";

    [DisplayName("连接·请求超时(秒)")]
    [Description("单次 HTTP 请求超时，取值 5-120。读长笔记和搜大库要留够时间")]
    public int RequestTimeoutSeconds { get; set; } = 15;

    [DisplayName("连接·信任自签证书")]
    [Description("Obsidian 的 HTTPS 端点用插件自己生成的本地 CA 签发证书，系统默认不认，开着才能连 https 地址。回调只作用于本插件这一个 HttpClient。如果已经把 https://127.0.0.1:27124/obsidian-local-rest-api.crt 下载并装进系统受信任根证书，可以关掉")]
    public bool TrustSelfSignedCertificate { get; set; } = true;

    [DisplayName("知识库·vault 本地目录")]
    [Description("半区二（wiki_* 四个工具）要的是本地磁盘绝对路径，形如 D:\\我的obsidian知识库；与 note_* 用的「vault 内相对路径」不是一回事。留空则 wiki_* 全部报未配置")]
    public string VaultPath { get; set; } = "";

    [DisplayName("知识库·允许文件系统写入")]
    [Description("开着才允许 wiki_page 写/删、wiki_index 重建索引（都限制在该目录之内的 .md，核心页 index/log/overview/conventions 不给删）。关掉只剩读与体检")]
    public bool WikiAllowFileWrite { get; set; } = true;

    [DisplayName("知识库·单次收录最多页数")
    ]
    [Description("wiki_ingest 一次最多让模型编出几页（1-200），超出的丢掉并说明")]
    public int IngestMaxPages { get; set; } = 15;

    [DisplayName("输出·结果最大字符数")]
    [Description("note_read/note_list/note_search 的输出超过这个字符数就截断并标注，取值 500-20000")]
    public int MaxOutputChars { get; set; } = 8000;

    [DisplayName("输出·搜索最多条数")]
    [Description("note_search 最多展示几条命中")]
    public int MaxSearchResults { get; set; } = 8;

    [DisplayName("输出·搜索摘要字符数")]
    [Description("note_search 每条命中的上下文摘要截断长度")]
    public int SummaryMaxLength { get; set; } = 150;
}

[Module("Obsidian笔记操作",
    "两半区：① 笔记操作（note_*）经 Obsidian 社区插件 Local REST API 读写 vault——列目录、读正文/元数据/大纲、整篇或定点改、搜索；② 知识库维护（wiki_*）直接吃文件系统——体检（空页/断链/孤立页）、重建索引、按关键词找页、读写页面，Obsidian 没开着也能用",
    defaultCategory: "苏染的工具")]
public partial class ObsidianModule(
    XmlFunctionCaller functionCaller,
    ILogger<ObsidianModule> logger,
    Interactor<ObsidianModule> interactor
) : ChatBehaviour, IConfigurable<ObsidianConfig>
{
    public ObsidianConfig Configuration { get; set; } = null!;

    const string AcceptNoteJson = "application/vnd.olrapi.note+json";
    const string AcceptDocumentMap = "application/vnd.olrapi.document-map+json";
    const string TypePatchInstruction = "application/vnd.olrapi.patch-instruction+json";
    const string TypeJsonLogic = "application/vnd.olrapi.jsonlogic+json";
    const string TypeMarkdown = "text/markdown";
    const string NotConfiguredHint = "Obsidian 笔记工具没配置：先在 Obsidian 里装好并启用社区插件 Local REST API（保持 Obsidian 运行），再把设置页上的服务地址和 API Key 填进本插件配置";

    HttpClient? httpClient;
    string baseUrl = "";
    int timeoutSeconds = 15;

    sealed record NoteEntry(string Path, bool IsFolder);

    protected override Task OnAwake()
    {
        baseUrl = Configuration.BaseUrl.Trim().TrimEnd('/');
        timeoutSeconds = Math.Clamp(Configuration.RequestTimeoutSeconds, 5, 120);
        vaultPath = Configuration.VaultPath.Trim().TrimEnd('\\', '/');
        if (baseUrl.Length > 0)
        {
            httpClient = BuildHttpClient();
            logger.LogInformation("[Obsidian] REST 半区已就绪 {BaseUrl}（5 个 note_* 工具）", baseUrl);
        }
        else
        {
            logger.LogWarning("[Obsidian] 未配置 Local REST API 地址，note_* 五个工具都会报未配置");
        }
        if (vaultPath.Length > 0)
        {
            logger.LogInformation("[Obsidian] 知识库半区指向 {Vault}，文件系统写入 {Write}（4 个 wiki_* 工具）",
                vaultPath, Configuration.WikiAllowFileWrite ? "开" : "关");
        }
        else
        {
            logger.LogWarning("[Obsidian] 未配置「知识库·vault 本地目录」，wiki_* 四个工具都会报未配置");
        }

        XmlHandler handler = new(this)
        {
            Description = "两半区：note_* 经 Obsidian 的 Local REST API 读写笔记（列目录、读、定点改、搜索、写入/追加/删除合一的 note_edit）；"
                + "wiki_* 直接吃文件系统做知识库维护（体检、重建索引、关键词找页、读写页面），不要求 Obsidian 在跑。",
            Explanation = "note_* 的先决条件是 Obsidian 开着、社区插件 Local REST API 已启用、配置里填了地址与密钥；路径形如 01-architecture/design.md，不带盘符、不带前导斜杠。"
                + "wiki_* 只要配置里填了「知识库·vault 本地目录」（本地绝对路径）就能用，路径是知识库内相对路径。"
                + "找内容：Obsidian 在跑优先 note_search（用它自己的索引，支持 tags/frontmatter 的 jsonlogic），没跑或要看生成区结构就 wiki_search。"
                + "浏览结构用 note_list。改已有笔记的某一节用 note_patch，不要整篇覆盖；整篇写/追加/删除都在 note_edit 的 mode 里。"
                + "知识库那套目录（raw/ 素材 + wiki/ 的 sources|entities|concepts|analyses 四分区 + index/log/overview/conventions 核心页）用 wiki_page 读写，"
                + "新建或大改后跑 wiki_index 重建索引；库脏了跑 wiki_status 看空页/断链/孤立页。"
                + "note_edit、note_patch、wiki_page 三个要把正文写在开闭标签之间，别塞进属性里。"
                + "删除都要先跟对方核对路径：note_edit 的 delete 走 Obsidian 回收站，wiki_page 的 delete 是文件系统直接抹掉。",
        };
        functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);
        // 纯文本区按标签名生效，这里是上面显式指定的下划线标签名，不是方法名
        functionCaller.AddPlainAreas("note_edit", "note_patch", "wiki_page");
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        httpClient?.Dispose();
        httpClient = null;
        return Task.CompletedTask;
    }

    // ============================================================
    // AI 函数
    // ============================================================

    [XmlFunction(FunctionMode.OneShot, "note_list")]
    [Description("列出 Obsidian 笔记库某个目录下的笔记与文件夹（分页）。目录条目显示为 / 结尾；这是 vault 内部路径，不是本地文件系统")]
    public async Task NoteList(
        [Description("目录路径，如 01-architecture；留空表示 vault 根目录")] string? path = null,
        [Description("页码，从 1 起")] int page = 1,
        [Description("每页条数，1-100，默认 30")] int pageSize = 30)
    {
        string directory = NormalizePath(path ?? "");
        string body = await RequestAsync(HttpMethod.Get, directory.Length == 0 ? "/vault/" : "/vault/" + directory + "/");
        List<NoteEntry> entries = ParseListing(body);

        int currentPage = Math.Max(page, 1);
        int size = Math.Clamp(pageSize, 1, 100);
        int start = (currentPage - 1) * size;
        int totalPages = Math.Max((entries.Count + size - 1) / size, 1);
        List<string> lines = new()
        {
            "[" + (directory.Length == 0 ? "/" : directory) + " | 共 " + entries.Count + " 项，第 " + currentPage + "/" + totalPages + " 页，每页 " + size + " 条]",
        };
        foreach (NoteEntry entry in entries.Skip(start).Take(size))
        {
            lines.Add((entry.IsFolder ? "[目录] " : "[笔记] ") + entry.Path);
        }
        if (entries.Count == 0)
        {
            lines.Add("这个目录是空的。目录名可以从上一层的 note_list 输出里抄。");
        }
        else if (start + size < entries.Count)
        {
            lines.Add("还有下一页，用 path=\"" + directory + "\" page=" + (currentPage + 1) + " 继续。");
        }
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    [XmlFunction(FunctionMode.OneShot, "note_read")]
    [Description("读取 Obsidian 笔记。content 返回 Markdown 正文，metadata 返回含 frontmatter 与 tags 的元数据 JSON，structure 返回文档大纲（列出可被 note_patch 定位的标题、块引用、frontmatter 字段）")]
    public async Task NoteRead(
        [Description("笔记路径，如 01-architecture/design.md")] string path,
        [Description("content / metadata / structure，默认 content")] string mode = "content")
    {
        string normalized = RequirePath(path, "note_read");
        string resolvedMode = RequireOneOf(mode, "note_read 的 mode", "content", "metadata", "structure");
        string? accept = resolvedMode switch
        {
            "metadata" => AcceptNoteJson,
            "structure" => AcceptDocumentMap,
            _ => null,
        };
        string body = await RequestAsync(HttpMethod.Get, "/vault/" + normalized, accept: accept);
        if (resolvedMode == "content")
        {
            interactor.Poke(Truncate("[" + normalized + " | " + body.Length + " 字符]\n" + body));
            return;
        }
        interactor.Poke(Truncate("[" + normalized + " | " + resolvedMode + "]\n" + body));
    }

    [XmlFunction(FunctionMode.Content, "note_edit")]
    [Description("写 Obsidian 笔记，三种模式一次只做一个：replace（整篇覆盖，不存在则创建）、append（追加到末尾，适合日记流水）、delete（删除该篇）。"
        + "replace/append 把 Markdown 正文放在开闭标签之间，delete 标签留空。只想改某一节别用 replace，用 note_patch")]
    public async Task NoteEdit(
        XmlExecutorContext context,
        [Description("笔记路径，如 reports/test.md（vault 内相对路径，不带盘符）")] string path,
        [Description("replace（默认）/ append / delete")] string mode = "replace")
    {
        if (context.CallMode != CallMode.Closing)
        {
            return;
        }
        string normalized = RequirePath(path, "note_edit");
        string resolvedMode = RequireOneOf(mode, "note_edit 的 mode", "replace", "append", "delete");
        if (resolvedMode == "delete")
        {
            await RequestAsync(HttpMethod.Delete, "/vault/" + normalized);
            interactor.Poke("已删除 " + normalized + "（按 Obsidian 的删除设置进回收站，不是物理删除，要恢复得在 Obsidian 里找）");
            return;
        }
        string content = context.FullContent;
        if (content.Trim().Length == 0)
        {
            // 空标签体意味着这次调用写漏了正文，直接放过就会把整篇笔记抹掉
            throw new Exception("note_edit 的 " + resolvedMode + " 要在开闭标签之间给出正文；"
                + (resolvedMode == "replace" ? "空着执行会把 " + normalized + " 整篇覆盖成空的，已拒绝" : "这次没有任何内容可追加"));
        }
        await RequestAsync(resolvedMode == "replace" ? HttpMethod.Put : HttpMethod.Post, "/vault/" + normalized, content);
        interactor.Poke(resolvedMode == "replace"
            ? "已写入 " + normalized + "（" + content.Length + " 字符，整篇覆盖）"
            : "已追加到 " + normalized + "（" + content.Length + " 字符）。Obsidian 会自己补上分隔空行，追加内容总是独立成块");
    }

    [XmlFunction(FunctionMode.Content, "note_patch")]
    [Description("只改 Obsidian 笔记里的某一处，不动整篇。开闭标签之间放要写进去的内容（frontmatter 时放 JSON 值，delete 时留空）。文件必须已存在，新建用 note_edit")]
    public async Task NotePatch(
        XmlExecutorContext context,
        [Description("笔记路径，如 01-architecture/design.md")] string path,
        [Description("append（目标末尾追加）/ prepend（目标开头插入）/ replace（替换目标）/ delete（删除目标）")] string operation,
        [Description("heading（标题）/ block（块引用）/ frontmatter（YAML 字段）")] string targetType,
        [Description("目标名。标题从最外层逐级往下、用 :: 分隔（如 主标题::子标题），也可以直接给 JSON 数组 [\"主标题\",\"子标题\"]；块引用给不含 ^ 的 id；frontmatter 给字段名")] string target,
        [Description("改动范围：content（默认，只动正文区）/ marker（只动标题行或块标记本身，改名用）/ markerAndContent（标记连同正文一起）")] string scope = "content",
        [Description("目标小节不存在时是否顺手创建，默认 false")] bool createIfMissing = false)
    {
        if (context.CallMode != CallMode.Closing)
        {
            return;
        }
        string normalized = RequirePath(path, "note_patch");
        string resolvedOperation = RequireOneOf(operation, "note_patch 的 operation", "append", "prepend", "replace", "delete");
        string resolvedTargetType = RequireOneOf(targetType, "note_patch 的 targetType", "heading", "block", "frontmatter");
        string resolvedScope = RequireOneOf(scope, "note_patch 的 scope", "content", "marker", "markerAndContent");
        if (resolvedOperation != "delete" && context.FullContent.Trim().Length == 0)
        {
            throw new Exception("note_patch 要在开闭标签之间给出写进去的内容；只有 operation=\"delete\" 可以留空");
        }

        JsonObject instruction = new()
        {
            ["targetType"] = resolvedTargetType,
            ["target"] = BuildTarget(resolvedTargetType, target),
            ["operation"] = resolvedOperation,
        };
        if (resolvedScope != "content")
        {
            instruction["scope"] = resolvedScope;
        }
        if (createIfMissing)
        {
            instruction["createTargetIfMissing"] = true;
        }
        if (resolvedOperation != "delete")
        {
            // frontmatter 的载荷是带类型的 JSON 值，标题与块的载荷是 Markdown 原文
            instruction[resolvedTargetType == "frontmatter" ? "value" : "content"] =
                resolvedTargetType == "frontmatter" ? ParseJsonValue(context.FullContent) : JsonValue.Create(context.FullContent);
        }

        string body = await RequestAsync(new HttpMethod("PATCH"), "/vault/" + normalized, instruction.ToJsonString(), TypePatchInstruction);
        interactor.Poke("已局部修改 " + normalized + "：" + resolvedOperation + " " + resolvedTargetType + "「" + target + "」，改完全文 " + body.Length + " 字符（要看结果用 note_read）");
    }

    [XmlFunction(FunctionMode.OneShot, "note_search")]
    [Description("搜索 Obsidian 笔记库，返回 文件名 | 相关度 | 摘要 的精简列表，要看全文对中的笔记再走 note_read。simple 走 Obsidian 自带的全文搜索；jsonlogic 按笔记元数据（frontmatter、tags、path、content）写规则查")]
    public async Task NoteSearch(
        [Description("simple 填关键词；jsonlogic 填 JSON 规则文本，如 {\"in\":[\"myTag\",{\"var\":\"tags\"}]}")] string query,
        [Description("simple（默认）/ jsonlogic")] string searchType = "simple")
    {
        string text = query.Trim();
        if (text.Length == 0)
        {
            throw new Exception("note_search 缺 query 关键词或规则");
        }
        string resolvedType = RequireOneOf(searchType, "note_search 的 searchType", "simple", "jsonlogic");

        string body = resolvedType == "simple"
            ? await RequestAsync(HttpMethod.Post, "/search/simple/?query=" + Uri.EscapeDataString(text))
            : await RequestAsync(HttpMethod.Post, "/search/", text, TypeJsonLogic);
        interactor.Poke(Truncate(FormatSearchResults(resolvedType == "simple", text, body)));
    }

    // ============================================================
    // 输出格式
    // ============================================================

    string FormatSearchResults(bool isSimple, string query, string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        List<JsonElement> items = document.RootElement.EnumerateArray().ToList();
        if (items.Count == 0)
        {
            return isSimple
                ? "搜索「" + query + "」没有命中。可以换更短的关键词再试，或用 note_list 逐层浏览。"
                : "JsonLogic 规则「" + query + "」没有命中任何笔记。规则里字段名可先拿 note_read mode=metadata 对照一篇真实笔记。";
        }

        int shownCount = Math.Clamp(Configuration.MaxSearchResults, 1, 50);
        int summaryLength = Math.Clamp(Configuration.SummaryMaxLength, 20, 1000);
        if (isSimple)
        {
            items = items.OrderByDescending(item => ReadScore(item)).ToList();
        }
        List<string> lines = new()
        {
            "搜索「" + query + "」共 " + items.Count + " 条命中，显示前 " + Math.Min(shownCount, items.Count) + " 条：",
        };
        for (int index = 0; index < Math.Min(shownCount, items.Count); index++)
        {
            JsonElement item = items[index];
            string filename = ReadString(item, "filename", "file", "未知文件");
            if (isSimple)
            {
                lines.Add(index + 1 + ". " + filename + " | 相关度:" + ReadScore(item).ToString("0.00", CultureInfo.InvariantCulture)
                    + " | " + ReadContext(item, summaryLength));
                continue;
            }
            lines.Add(index + 1 + ". " + filename + " | " + ReadResult(item, summaryLength));
        }
        if (items.Count > shownCount)
        {
            lines.Add("… 还有 " + (items.Count - shownCount) + " 条没显示，可以把关键词收紧一点再搜，或把配置里的『搜索最多条数』调大。");
        }
        lines.Add("看全文用 note_read 传上面的 filename。");
        return string.Join("\n", lines);
    }

    string ReadContext(JsonElement item, int summaryLength)
    {
        if (item.TryGetProperty("matches", out JsonElement matches) == false
            || matches.ValueKind != JsonValueKind.Array
            || matches.GetArrayLength() == 0)
        {
            return "（无摘要）";
        }
        string context = ReadString(matches[0], "context", "", "").Replace("\n", " ").Trim();
        if (context.Length == 0)
        {
            return "（无摘要）";
        }
        return context.Length <= summaryLength ? context : context[..summaryLength] + "…";
    }

    string ReadResult(JsonElement item, int summaryLength)
    {
        if (item.TryGetProperty("result", out JsonElement result) == false)
        {
            return "（命中，无返回值）";
        }
        string text = result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : result.GetRawText();
        text = text.Replace("\n", " ").Trim();
        if (text.Length == 0)
        {
            return "（命中，无返回值）";
        }
        return text.Length <= summaryLength ? text : text[..summaryLength] + "…";
    }

    static double ReadScore(JsonElement item)
    {
        if (item.TryGetProperty("score", out JsonElement score) == false || score.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }
        return score.GetDouble();
    }

    static string ReadString(JsonElement item, string propertyName, string fallbackProperty, string defaultValue)
    {
        if (item.TryGetProperty(propertyName, out JsonElement value) == false || value.ValueKind != JsonValueKind.String)
        {
            if (fallbackProperty.Length == 0)
            {
                return defaultValue;
            }
            return ReadString(item, fallbackProperty, "", defaultValue);
        }
        return value.GetString() ?? defaultValue;
    }

    string Truncate(string text)
    {
        int limit = Math.Clamp(Configuration.MaxOutputChars, 500, 20000);
        if (text.Length <= limit)
        {
            return text;
        }
        return text[..limit] + "\n…[输出已截断，超过 " + limit + " 字符]。要剩下的部分先用 note_read mode=structure 拿大纲，或 note_search 把目标缩小到某一节";
    }

    // ============================================================
    // 请求
    // ============================================================

    HttpClient BuildHttpClient()
    {
        HttpClientHandler handler = new();
        if (Configuration.TrustSelfSignedCertificate && baseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase))
        {
            handler.ServerCertificateCustomValidationCallback = delegate { return true; };
        }
        HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        if (Configuration.ApiKey.Length > 0)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Configuration.ApiKey);
        }
        return client;
    }

    async Task<string> RequestAsync(HttpMethod method, string path, string? body = null, string? contentType = null, string? accept = null)
    {
        HttpClient client = httpClient ?? throw new Exception(NotConfiguredHint);
        using HttpRequestMessage request = new(method, baseUrl + EncodePath(path));
        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType ?? TypeMarkdown);
        }
        if (accept != null)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        }

        logger.LogDebug("[Obsidian] {Method} {Path}", method.Method, path);
        using HttpResponseMessage response = await SendAsync(client, request);
        string text = await response.Content.ReadAsStringAsync();
        ReportPatchWarnings(response, path);
        if (response.StatusCode >= HttpStatusCode.BadRequest)
        {
            throw new Exception(DescribeFailure((int)response.StatusCode, text, path));
        }
        return text;
    }

    static string EncodePath(string path)
    {
        // 逐段编码：中文、空格、# 都要转义，但段之间的 / 不能转（编码成 %2F 的斜杠服务端不再当分隔符）
        string target = path;
        string query = "";
        int queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            target = path[..queryIndex];
            query = path[queryIndex..];
        }
        string[] segments = target.Split('/');
        StringBuilder builder = new();
        for (int index = 0; index < segments.Length; index++)
        {
            if (index > 0)
            {
                builder.Append('/');
            }
            builder.Append(Uri.EscapeDataString(segments[index]));
        }
        return builder.ToString() + query;
    }

    async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        try
        {
            return await client.SendAsync(request, DestroyCancellationToken);
        }
        catch (TaskCanceledException e) when (DestroyCancellationToken.IsCancellationRequested == false)
        {
            throw new Exception("Obsidian 请求超时（" + timeoutSeconds + " 秒）：确认 Obsidian 开着、Local REST API 已启用、地址和端口没填错", e);
        }
        catch (HttpRequestException e)
        {
            throw new Exception("连不上 Obsidian Local REST API（" + baseUrl + "）：" + e.Message
                + (baseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase)
                    ? "；用 https 端口时要确认『信任自签证书』是开的，或者把插件的本地 CA 装进系统"
                    : "；用明文 http 要在 Local REST API 设置里勾 Enable HTTP server"), e);
        }
    }

    void ReportPatchWarnings(HttpResponseMessage response, string path)
    {
        if (response.Headers.TryGetValues("Markdown-Patch-Warnings", out IEnumerable<string>? values) == false)
        {
            return;
        }
        // 服务端把含中文的警告做了百分号编码，HTTP 头只允许 ASCII
        logger.LogWarning("[Obsidian] {Path} 的补丁警告 {Warnings}", path, Uri.UnescapeDataString(string.Join("", values)));
    }

    string DescribeFailure(int statusCode, string body, string path)
    {
        string detail = ReadServerError(body);
        string hint = statusCode switch
        {
            400 => "请求被 Obsidian 判为非法：多半是 note_patch 的 operation×scope×targetType 组合不在允许范围里，或 target 写法不对",
            401 => "API Key 不对或没填，去 Local REST API 设置页重新复制",
            403 => "这个路径被拒绝访问：.obsidian 配置目录默认禁止读写",
            404 => "笔记或目录不存在：" + path,
            405 => "这个路径是目录，读写删改只针对文件",
            409 => "目标位置里已经有这段内容了",
            422 => "一个请求里混用了多种 target 写法，只能选一种",
            429 => "密钥连续输错被限速，等一分钟再试",
            _ => "HTTP " + statusCode,
        };
        return "Obsidian 接口拒绝（" + hint + "）；服务端：" + detail;
    }

    static string ReadServerError(string body)
    {
        if (body.Trim().Length == 0)
        {
            return "无错误详情";
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return ReadString(document.RootElement, "message", "", body[..Math.Min(300, body.Length)]);
        }
        catch (JsonException)
        {
            return body[..Math.Min(300, body.Length)];
        }
    }

    // ============================================================
    // 参数
    // ============================================================

    static string NormalizePath(string path)
    {
        // 从 Windows 那边抄过来的路径常带反斜杠，vault 路径只认正斜杠
        return path.Trim().Replace('\\', '/').Trim('/');
    }

    static string RequirePath(string path, string toolName)
    {
        string normalized = NormalizePath(path);
        if (normalized.Length == 0)
        {
            throw new Exception(toolName + " 缺 path：给 vault 内的相对路径，如 01-architecture/design.md");
        }
        return normalized;
    }

    static string RequireOneOf(string value, string label, params string[] allowed)
    {
        string lowered = value.Trim().ToLower();
        if (allowed.Contains(lowered))
        {
            return lowered;
        }
        throw new Exception(label + " 只能是 " + string.Join(" / ", allowed) + "，收到「" + value + "」");
    }

    static JsonNode BuildTarget(string targetType, string target)
    {
        string value = target.Trim();
        if (value.StartsWith('['))
        {
            return JsonNode.Parse(value) ?? throw new Exception("note_patch 的 target 数组解析失败：" + target);
        }
        if (targetType != "heading")
        {
            if (value.Length == 0)
            {
                throw new Exception("note_patch 的 target 要写块引用 id（不含 ^）或 frontmatter 字段名");
            }
            return JsonValue.Create(targetType == "block" ? value.TrimStart('^') : value)!;
        }
        // 标题在 5.x 里是一层一层的数组，:: 只是给模型省事的简写
        JsonArray levels = new();
        foreach (string level in value.Split("::", StringSplitOptions.TrimEntries))
        {
            if (level.Length > 0)
            {
                levels.Add(JsonValue.Create(level));
            }
        }
        return levels;
    }

    static JsonNode ParseJsonValue(string payload)
    {
        string text = payload.Trim();
        try
        {
            return JsonNode.Parse(text) ?? JsonValue.Create(text)!;
        }
        catch (JsonException)
        {
            // 模型常把 frontmatter 的值写成不带引号的裸词，按字符串存而不是打回
            return JsonValue.Create(text)!;
        }
    }

    static List<NoteEntry> ParseListing(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        List<NoteEntry> entries = new();
        if (document.RootElement.TryGetProperty("files", out JsonElement files) == false
            || files.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }
        foreach (JsonElement item in files.EnumerateArray())
        {
            // 5.x 的列表项是字符串、目录以 / 结尾；更老的版本回 {type, path} 对象
            if (item.ValueKind == JsonValueKind.String)
            {
                string value = item.GetString() ?? "";
                entries.Add(new NoteEntry(value.TrimEnd('/'), value.EndsWith('/')));
                continue;
            }
            string name = ReadString(item, "path", "filename", "");
            bool isFolder = item.TryGetProperty("type", out JsonElement type) && type.GetString() == "folder";
            entries.Add(new NoteEntry(name.TrimEnd('/'), isFolder || name.EndsWith('/')));
        }
        return entries;
    }
}
