using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
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
    [Description("Local REST API 的服务地址。")]
    public string BaseUrl { get; set; } = "";

    [DisplayName("连接·API Key")]
    [Description("Local REST API 设置页生成的密钥，按 Authorization: Bearer 发出。留空一律 401；填错累积到十次/分钟会被服务端 429 限速一整分钟")]
    public string ApiKey { get; set; } = "";

    [DisplayName("连接·自动读取 Local REST API 配置")]
    [Description("开启后，地址或密钥留空时会尝试从 vault 的 .obsidian/plugins/obsidian-local-rest-api/data.json 读取端口与密钥")]
    public bool AutoDetectRestApi { get; set; } = true;

    [DisplayName("连接·请求超时(秒)")]
    [Description("单次 HTTP 请求超时，取值 5-120。读长笔记和搜大库要留够时间")]
    public int RequestTimeoutSeconds { get; set; } = 15;

    [DisplayName("连接·信任自签证书")]
    [Description("是否信任本地自签证书，连 https 地址需要开。")]
    public bool TrustSelfSignedCertificate { get; set; } = true;

    [DisplayName("知识库·vault 本地目录")]
    [Description("wiki_* 工具用的本地知识库绝对路径，留空则报未配置。")]
    public string VaultPath { get; set; } = "";

    [DisplayName("知识库·允许文件系统写入")]
    [Description("是否允许 wiki_* 写删与索引重建，默认关。")]
    public bool WikiAllowFileWrite { get; set; } = false;

    [DisplayName("知识库·体检外部笔记断链")]
    [Description("开启后体检也会检查外部笔记里的断链；默认只查生成区，避免大库刷屏。")]
    public bool CheckExternalLinks { get; set; } = false;

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
    defaultCategory: "苏染的工具/效率工具",
    EditorUI = typeof(ObsidianUI))]
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
    string patchWarnings = "";

    sealed record NoteEntry(string Path, bool IsFolder);

    protected override Task OnAwake()
    {
        if (Configuration.AutoDetectRestApi)
        {
            TryAutoDetectRestApi();
        }
        baseUrl = Configuration.BaseUrl.Trim().TrimEnd('/');
        timeoutSeconds = Math.Clamp(Configuration.RequestTimeoutSeconds, 5, 120);
        vaultPath = Configuration.VaultPath.Trim().TrimEnd('\\', '/');
        if (baseUrl.Length > 0)
        {
            httpClient = BuildHttpClient();
            logger.LogInformation("[Obsidian] REST 半区已就绪 {BaseUrl}", baseUrl);
        }
        else
        {
            logger.LogWarning("[Obsidian] 未配置 Local REST API 地址，note_* 工具都会报未配置");
        }
        if (vaultPath.Length > 0)
        {
            logger.LogInformation("[Obsidian] 知识库半区指向 {Vault}，文件系统写入 {Write}",
                vaultPath, Configuration.WikiAllowFileWrite ? "开" : "关");
        }
        else
        {
            logger.LogWarning("[Obsidian] 未配置「知识库·vault 本地目录」，wiki_* 工具都会报未配置");
        }

        XmlHandler handler = new(this)
        {
            Description = "两半区：note_* 经 Obsidian 的 Local REST API 读写笔记（浏览、读、搜索、定点改、整篇写、附件、移动、环境检查）；"
                + "wiki_* 直接吃文件系统做知识库维护（体检、索引、BM25 检索、读写、收录、改名合并），不要求 Obsidian 在跑。",
            Explanation = "note_* 的前提是 Obsidian 开着、Local REST API 已启用；地址或密钥留空时会自动从插件的 data.json 读取。路径是 vault 内相对路径，不带盘符和前导斜杠。"
                + "wiki_* 只要配置「知识库·vault 本地目录」；写删受「允许文件系统写入」控制。"
                + "找内容优先 kb_search（Obsidian 在跑走 REST，失败转文件系统）；note_search 支持 page/limit，wiki_search 支持 section/tag/最近天数。"
                + "note_patch 支持 batch 数组一次改多处；note_upload/move/rename 处理附件与移动；wiki_rename/wiki_merge 会自动改双链。"
                + "note_edit/note_patch/wiki_page 的正文写在开闭标签之间；删除会进 Obsidian 回收站或 vault 的 .trash/，但仍要先核对路径。",
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
            TryAudit("删除 " + normalized);
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
        TryAudit(resolvedMode + " " + normalized + "（" + content.Length + " 字符）");
        interactor.Poke(resolvedMode == "replace"
            ? "已写入 " + normalized + "（" + content.Length + " 字符，整篇覆盖）"
            : "已追加到 " + normalized + "（" + content.Length + " 字符）。Obsidian 会自己补上分隔空行，追加内容总是独立成块");
    }

    [XmlFunction(FunctionMode.Content, "note_patch")]
    [Description("只改笔记里的某一处。单条：operation+targetType+target+scope；批量：batch 传 JSON 数组，每项含 operation/targetType/target/content 或 value。文件必须已存在，新建用 note_edit")]
    public async Task NotePatch(
        XmlExecutorContext context,
        [Description("笔记路径，如 01-architecture/design.md")] string path,
        [Description("append / prepend / replace / delete（单条模式用）")] string operation = "append",
        [Description("heading / block / frontmatter（单条模式用）")] string targetType = "heading",
        [Description("目标名。标题逐级用 :: 分隔或 JSON 数组；块给不带 ^ 的 id；frontmatter 给字段名")] string target = "",
        [Description("content / marker / markerAndContent")] string scope = "content",
        [Description("目标小节不存在时顺手创建，默认 false")] bool createIfMissing = false,
        [Description("批量模式：JSON 数组，每项 {operation,targetType,target,scope?,content?/value?}；给了就忽略单条参数")] string batch = "")
    {
        if (context.CallMode != CallMode.Closing)
        {
            return;
        }
        string normalized = RequirePath(path, "note_patch");
        if (batch.Trim().Length > 0)
        {
            await PatchBatchAsync(normalized, batch);
            return;
        }
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
        TryAudit("补丁 " + normalized + "：" + resolvedOperation + " " + resolvedTargetType + "「" + target + "」");
        string warningText = patchWarnings.Length > 0 ? "\n服务端补丁警告：" + patchWarnings : "";
        interactor.Poke("已局部修改 " + normalized + "：" + resolvedOperation + " " + resolvedTargetType + "「" + target + "」，改完全文 " + body.Length + " 字符（要看结果用 note_read）" + warningText);
    }

    /// <summary>Local REST API 的 PATCH 支持一次发一组指令，批量改标题/字段时省往返。</summary>
    async Task PatchBatchAsync(string normalized, string batch)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(batch);
        }
        catch (Exception error)
        {
            throw new Exception("note_patch 的 batch 不是合法 JSON：" + error.Message);
        }
        if (parsed is not JsonArray array || array.Count == 0)
        {
            throw new Exception("note_patch 的 batch 要是一个非空 JSON 数组");
        }
        JsonArray instructions = new();
        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject item)
            {
                throw new Exception("note_patch 的 batch 每项都必须是对象");
            }
            string operation = RequireOneOf(JsonField(item, "operation", "append"), "batch 的 operation", "append", "prepend", "replace", "delete");
            string targetType = RequireOneOf(JsonField(item, "targetType", "heading"), "batch 的 targetType", "heading", "block", "frontmatter");
            string scope = RequireOneOf(JsonField(item, "scope", "content"), "batch 的 scope", "content", "marker", "markerAndContent");
            JsonNode? targetNode = item["target"];
            if (targetNode == null)
            {
                throw new Exception("note_patch 的 batch 每项都要有 target");
            }
            JsonObject instruction = new()
            {
                ["targetType"] = targetType,
                ["target"] = BuildBatchTarget(targetType, targetNode),
                ["operation"] = operation,
            };
            if (scope != "content")
            {
                instruction["scope"] = scope;
            }
            if (item["createIfMissing"] is JsonValue create && create.TryGetValue<bool>(out bool createValue) && createValue)
            {
                instruction["createTargetIfMissing"] = true;
            }
            if (operation != "delete")
            {
                JsonNode? payload = item["content"] ?? item["value"];
                if (payload == null)
                {
                    throw new Exception("batch 的 " + operation + " 指令缺 content 或 value");
                }
                if (targetType == "frontmatter")
                {
                    instruction["value"] = payload.DeepClone();
                }
                else
                {
                    instruction["content"] = payload is JsonValue ? payload.DeepClone() : JsonValue.Create(payload.ToJsonString());
                }
            }
            instructions.Add(instruction);
        }
        string body = await RequestAsync(new HttpMethod("PATCH"), "/vault/" + normalized, instructions.ToJsonString(), TypePatchInstruction);
        TryAudit("批量补丁 " + normalized + "：" + instructions.Count + " 条");
        string warningText = patchWarnings.Length > 0 ? "\n服务端补丁警告：" + patchWarnings : "";
        interactor.Poke("已批量修改 " + normalized + "：" + instructions.Count + " 条指令，改完全文 " + body.Length + " 字符（要看结果用 note_read）" + warningText);
    }

    static string JsonField(JsonObject item, string name, string fallback)
    {
        JsonNode? node = item[name];
        return node is JsonValue value && value.TryGetValue<string>(out string? text) ? text ?? fallback : fallback;
    }

    static JsonNode BuildBatchTarget(string targetType, JsonNode node)
    {
        if (node is JsonArray array)
        {
            return array.DeepClone();
        }
        string value = node is JsonValue json && json.TryGetValue<string>(out string? text) ? text ?? "" : node.ToJsonString();
        return BuildTarget(targetType, value);
    }

    [XmlFunction(FunctionMode.OneShot, "note_search")]
    [Description("搜索 Obsidian 笔记库，返回 文件名 | 相关度 | 摘要；支持 page/limit 翻页。simple 走 Obsidian 全文搜索；jsonlogic 按 frontmatter/tags/path/content 规则查")]
    public async Task NoteSearch(
        [Description("simple 填关键词；jsonlogic 填 JSON 规则文本，如 {\"in\":[\"myTag\",{\"var\":\"tags\"}]}")] string query,
        [Description("simple（默认）/ jsonlogic")] string searchType = "simple",
        [Description("页码，从 1 起")] int page = 1,
        [Description("每页条数，0=用配置默认值")] int limit = 0)
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
        interactor.Poke(Truncate(FormatSearchResults(resolvedType == "simple", text, body, page, limit)));
    }

    [XmlFunction(FunctionMode.OneShot, "note_upload")]
    [Description("上传附件到 vault（图片/PDF/音频等）。path=目标路径，localPath=本地文件绝对路径")]
    public async Task NoteUpload(
        [Description("vault 内目标路径，如 assets/pic.png")] string path,
        [Description("本地文件绝对路径")] string localPath)
    {
        string normalized = RequirePath(path, "note_upload");
        string source = localPath.Trim();
        if (source.Length == 0 || File.Exists(source) == false)
        {
            throw new Exception("note_upload 找不到本地文件：" + localPath);
        }
        byte[] bytes = File.ReadAllBytes(source);
        await RequestBinaryAsync(HttpMethod.Put, "/vault/" + normalized, bytes, GuessContentType(normalized));
        TryAudit("上传附件 " + normalized + "（" + bytes.Length + " 字节）");
        interactor.Poke("已上传 " + normalized + "（" + bytes.Length + " 字节）");
    }

    [XmlFunction(FunctionMode.OneShot, "note_move")]
    [Description("移动或改名笔记/附件。fromPath、toPath 都是 vault 内相对路径；overwrite=false 时目标已存在会拒绝")]
    public async Task NoteMove(
        [Description("原路径")] string fromPath,
        [Description("目标路径")] string toPath,
        [Description("目标已存在时是否覆盖")] bool overwrite = false)
    {
        string from = RequirePath(fromPath, "note_move");
        string to = RequirePath(toPath, "note_move");
        (byte[] bytes, string contentType) = await RequestBytesAsync("/vault/" + from);
        bool targetExists = true;
        try
        {
            await RequestBytesAsync("/vault/" + to);
        }
        catch (Exception)
        {
            targetExists = false;
        }
        if (targetExists && overwrite == false)
        {
            throw new Exception("note_move 的目标已存在：" + to + "（要覆盖就把 overwrite 设为 true）");
        }
        await RequestBinaryAsync(HttpMethod.Put, "/vault/" + to, bytes, contentType);
        await RequestAsync(HttpMethod.Delete, "/vault/" + from);
        TryAudit("移动 " + from + " → " + to);
        interactor.Poke("已移动 " + from + " → " + to + "（" + bytes.Length + " 字节）");
    }

    [XmlFunction(FunctionMode.OneShot, "note_rename")]
    [Description("同目录改名。path=原路径，newName=新文件名（不带目录，可省略 .md）")]
    public async Task NoteRename(
        [Description("原路径")] string path,
        [Description("新文件名，如 design-v2")] string newName)
    {
        string from = RequirePath(path, "note_rename");
        string name = newName.Trim().Replace('\\', '/');
        if (name.Length == 0 || name.Contains('/'))
        {
            throw new Exception("note_rename 的 newName 只给文件名，不带目录");
        }
        if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) == false)
        {
            name += ".md";
        }
        int cut = from.LastIndexOf('/');
        string target = cut >= 0 ? from[..cut] + "/" + name : name;
        await NoteMove(from, target, false);
    }

    [XmlFunction(FunctionMode.OneShot, "note_env")]
    [Description("检查 Obsidian REST 连接：地址、密钥是否填、认证是否通过、vault 与文件写入状态")]
    public async Task NoteEnv()
    {
        StringBuilder lines = new();
        lines.Append("REST 地址：").Append(baseUrl.Length > 0 ? baseUrl : "未配置");
        lines.Append("\nAPI Key：").Append(Configuration.ApiKey.Length > 0
            ? "已填（" + Configuration.ApiKey.Length + " 字符）" : "未填");
        if (httpClient == null)
        {
            lines.Append("\n连接：未配置，note_* 工具不可用");
        }
        else
        {
            try
            {
                string body = await RequestAsync(HttpMethod.Get, "/");
                lines.Append("\n认证：通过｜").Append(Cut(body, 180));
            }
            catch (Exception error)
            {
                lines.Append("\n连接/认证失败：").Append(Cut(error.Message, 180));
            }
        }
        lines.Append("\nvault：").Append(vaultPath.Length > 0 ? vaultPath : "未配置");
        lines.Append("｜文件系统写入：").Append(Configuration.WikiAllowFileWrite ? "开" : "关");
        interactor.Poke(Truncate(lines.ToString()));
    }

    [XmlFunction(FunctionMode.OneShot, "kb_search")]
    [Description("统一搜索：Obsidian 在跑先走 REST 全文搜索，失败或没开则用文件系统 BM25；返回文件/路径 + 摘要")]
    public async Task KbSearch(
        [Description("关键词")] string query,
        [Description("最多几条，1-20")] int limit = 8,
        [Description("文件侧过滤分区：sources/entities/concepts/analyses/external")] string section = "",
        [Description("文件侧过滤标签")] string tag = "",
        [Description("文件侧只看最近几天，0=不限")] int sinceDays = 0)
    {
        string text = query.Trim();
        if (text.Length == 0)
        {
            throw new Exception("kb_search 缺关键词");
        }
        int count = Math.Clamp(limit, 1, 20);
        if (httpClient != null)
        {
            try
            {
                string body = await RequestAsync(HttpMethod.Post, "/search/simple/?query=" + Uri.EscapeDataString(text));
                interactor.Poke(Truncate("[REST] " + FormatSearchResults(true, text, body, 1, count)));
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning("[Obsidian] kb_search 的 REST 搜索失败，转文件系统：{Message}", error.Message);
            }
        }
        string root = RequireVault(false);
        VaultWiki.SearchOutcome outcome = await Task.Run(() => VaultWiki.Rank(root, text, count, 2600,
            Math.Clamp(Configuration.MaxOutputChars, 500, 20000), section ?? "", tag ?? "", Math.Clamp(sinceDays, 0, 3650)));
        if (outcome.Hits.Count == 0)
        {
            interactor.Poke("kb_search「" + text + "」没命中（扫了 " + outcome.ScannedPages + " 页）。");
            return;
        }
        List<string> lines = new()
        {
            "[文件] 命中 " + outcome.Hits.Count + " 页（候选 " + outcome.Candidates + "）：",
        };
        for (int index = 0; index < outcome.Hits.Count; index++)
        {
            VaultWiki.SearchHit hit = outcome.Hits[index];
            string snippet = hit.Snippet.Replace("\r", " ").Replace("\n", " ").Trim();
            if (snippet.Length > Configuration.SummaryMaxLength)
            {
                snippet = snippet[..Configuration.SummaryMaxLength] + "…";
            }
            lines.Add((index + 1) + ". " + hit.Rel + " | " + hit.Title + " | 相关度:" + hit.Score + " | " + snippet);
        }
        interactor.Poke(Truncate(string.Join("\n", lines)));
    }

    // ============================================================
    // 输出格式
    // ============================================================

    string FormatSearchResults(bool isSimple, string query, string body, int page, int limit)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        List<JsonElement> items = document.RootElement.EnumerateArray().ToList();
        if (items.Count == 0)
        {
            return isSimple
                ? "搜索「" + query + "」没有命中。可以换更短的关键词再试，或用 note_list 逐层浏览。"
                : "JsonLogic 规则「" + query + "」没有命中任何笔记。规则里字段名可先拿 note_read mode=metadata 对照一篇真实笔记。";
        }

        int shownCount = limit > 0 ? Math.Clamp(limit, 1, 50) : Math.Clamp(Configuration.MaxSearchResults, 1, 50);
        int summaryLength = Math.Clamp(Configuration.SummaryMaxLength, 20, 1000);
        if (isSimple)
        {
            items = items.OrderByDescending(item => ReadScore(item)).ToList();
        }
        int currentPage = Math.Max(page, 1);
        int start = (currentPage - 1) * shownCount;
        int totalPages = Math.Max((items.Count + shownCount - 1) / shownCount, 1);
        int end = Math.Min(start + shownCount, items.Count);
        if (start >= items.Count)
        {
            return "搜索「" + query + "」共 " + items.Count + " 条命中，只有 " + totalPages + " 页；page=" + currentPage + " 超出范围。";
        }
        List<string> lines = new()
        {
            "搜索「" + query + "」共 " + items.Count + " 条命中，第 " + currentPage + "/" + totalPages
                + " 页，显示 " + (start + 1) + "-" + end + " 条：",
        };
        for (int index = start; index < end; index++)
        {
            JsonElement item = items[index];
            string filename = ReadString(item, "filename", "file", "未知文件");
            if (isSimple)
            {
                lines.Add((index + 1) + ". " + filename + " | 相关度:" + ReadScore(item).ToString("0.00", CultureInfo.InvariantCulture)
                    + " | " + ReadContext(item, summaryLength));
                continue;
            }
            lines.Add((index + 1) + ". " + filename + " | " + ReadResult(item, summaryLength));
        }
        if (end < items.Count)
        {
            lines.Add("… 还有 " + (items.Count - end) + " 条，用 page=" + (currentPage + 1) + " 继续。");
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

    /// <summary>note_* 的写操作也记一行维护日志；没配 vault 就静默跳过。</summary>
    void TryAudit(string entry)
    {
        if (Configuration.WikiAllowFileWrite == false || vaultPath.Length == 0 || Directory.Exists(vaultPath) == false)
        {
            return;
        }
        try
        {
            VaultWiki.AppendLog(vaultPath, "note_* " + entry);
        }
        catch (Exception error)
        {
            logger.LogDebug("[Obsidian] 写维护日志失败：{Message}", error.Message);
        }
    }

    // ============================================================
    // 请求
    // ============================================================

    /// <summary>Local REST API 把端口和密钥写在自己的 data.json 里，能读到就不用手抄。</summary>
    void TryAutoDetectRestApi()
    {
        string configuredBase = Configuration.BaseUrl.Trim();
        string configuredKey = Configuration.ApiKey.Trim();
        if (configuredBase.Length > 0 && configuredKey.Length > 0)
        {
            return;
        }
        string vault = Configuration.VaultPath.Trim().TrimEnd('\\', '/');
        if (vault.Length == 0)
        {
            return;
        }
        string dataFile = Path.Combine(vault, ".obsidian", "plugins", "obsidian-local-rest-api", "data.json");
        if (!File.Exists(dataFile))
        {
            return;
        }
        try
        {
            JsonNode? data = JsonNode.Parse(File.ReadAllText(dataFile));
            if (data == null)
            {
                return;
            }
            if (configuredKey.Length == 0)
            {
                string key = NodeText(data, "apiKey");
                if (key.Length > 0)
                {
                    Configuration.ApiKey = key;
                }
            }
            if (configuredBase.Length == 0)
            {
                int port = NodeInt(data, "port", 27124);
                int insecurePort = NodeInt(data, "insecurePort", 27123);
                bool secure = NodeBool(data, "enableSecureServer", true);
                bool insecure = NodeBool(data, "enableInsecureServer", false);
                Configuration.BaseUrl = secure || !insecure
                    ? "https://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)
                    : "http://127.0.0.1:" + insecurePort.ToString(CultureInfo.InvariantCulture);
            }
            logger.LogInformation("[Obsidian] 已从 {File} 自动读取 REST 配置：{BaseUrl}", dataFile, Configuration.BaseUrl);
        }
        catch (Exception error)
        {
            logger.LogWarning("[Obsidian] 自动读取 {File} 失败：{Message}", dataFile, error.Message);
        }
    }

    static string NodeText(JsonNode? node, string name)
    {
        JsonNode? value = node?[name];
        return value?.GetValue<string>() ?? "";
    }

    static int NodeInt(JsonNode? node, string name, int fallback)
    {
        JsonNode? value = node?[name];
        if (value == null)
        {
            return fallback;
        }
        try
        {
            return value.GetValue<int>();
        }
        catch (Exception)
        {
            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
        }
    }

    static bool NodeBool(JsonNode? node, string name, bool fallback)
    {
        JsonNode? value = node?[name];
        if (value == null)
        {
            return fallback;
        }
        try
        {
            return value.GetValue<bool>();
        }
        catch (Exception)
        {
            return bool.TryParse(value.ToString(), out bool parsed) ? parsed : fallback;
        }
    }

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
        patchWarnings = "";
        using HttpResponseMessage response = await SendAsync(client, request);
        string text = await response.Content.ReadAsStringAsync();
        ReportPatchWarnings(response, path);
        if (response.StatusCode >= HttpStatusCode.BadRequest)
        {
            throw new Exception(DescribeFailure((int)response.StatusCode, text, path));
        }
        return text;
    }

    /// <summary>上传附件用：正文是二进制，Content-Type 按扩展名给。</summary>
    async Task<string> RequestBinaryAsync(HttpMethod method, string path, byte[] bytes, string contentType)
    {
        HttpClient client = httpClient ?? throw new Exception(NotConfiguredHint);
        using HttpRequestMessage request = new(method, baseUrl + EncodePath(path));
        ByteArrayContent content = new(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Content = content;
        logger.LogDebug("[Obsidian] {Method} {Path} ({Bytes} bytes)", method.Method, path, bytes.Length);
        patchWarnings = "";
        using HttpResponseMessage response = await SendAsync(client, request);
        string text = await response.Content.ReadAsStringAsync();
        ReportPatchWarnings(response, path);
        if (response.StatusCode >= HttpStatusCode.BadRequest)
        {
            throw new Exception(DescribeFailure((int)response.StatusCode, text, path));
        }
        return text;
    }

    /// <summary>移动/复制附件要拿原始字节和原 Content-Type。</summary>
    async Task<(byte[] Bytes, string ContentType)> RequestBytesAsync(string path)
    {
        HttpClient client = httpClient ?? throw new Exception(NotConfiguredHint);
        using HttpRequestMessage request = new(HttpMethod.Get, baseUrl + EncodePath(path));
        using HttpResponseMessage response = await SendAsync(client, request);
        if (response.StatusCode >= HttpStatusCode.BadRequest)
        {
            string text = await response.Content.ReadAsStringAsync();
            throw new Exception(DescribeFailure((int)response.StatusCode, text, path));
        }
        string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return (await response.Content.ReadAsByteArrayAsync(), contentType);
    }

    static string GuessContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            ".json" => "application/json",
            ".css" => "text/css",
            ".html" => "text/html",
            ".txt" => "text/plain",
            _ => "application/octet-stream",
        };
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
        patchWarnings = Uri.UnescapeDataString(string.Join("", values));
        logger.LogWarning("[Obsidian] {Path} 的补丁警告 {Warnings}", path, patchWarnings);
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
        string raw = path.Trim();
        if (raw.Length == 0)
        {
            throw new Exception(toolName + " 缺 path：给 vault 内的相对路径，如 01-architecture/design.md");
        }
        if (raw.StartsWith('/') || raw.StartsWith('\\') || raw.StartsWith('~')
            || (raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':'))
        {
            throw new Exception(toolName + " 只收 vault 内相对路径，不给绝对路径、盘符或 ~：" + path);
        }
        string normalized = NormalizePath(path);
        if (normalized.Length == 0 || normalized.Split('/').Any(segment => segment == ".."))
        {
            throw new Exception(toolName + " 的路径不能为空，也不能带 .. 穿越：" + path);
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
