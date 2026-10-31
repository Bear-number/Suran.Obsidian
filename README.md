# Suran.Obsidian

Obsidian 笔记库操作，**两个半区**：

- **note_\*（走 Obsidian 的 Local REST API）**：列目录、读正文/元数据/大纲、整篇或定点改、搜索。frontmatter 解析、文档结构、搜索打分都在 Obsidian 侧完成，本插件只是社区插件 [Local REST API](https://github.com/LLincd/obsidian-local-rest-api) 的薄壳。笔记工具本身移植自工作区的 `kira-ai-plugin-obsidian`（KiraAI Python 插件版）。
- **wiki_\*（直接吃文件系统）**：知识库体检（空页/断链/孤立页/未初始化）、重建索引、关键词找页、读写页面、把素材编译成整本页面（`wiki_ingest`）。**Obsidian 没开着也能用**，路径限制在配置的 vault 目录之内。这一半移植自 KiraAI 的 `kira_wiki` 插件（`wiki_core.py`，纯标准库那部分；它的面板带不过来——Alife 没有侧边栏宿主，图谱与双链跳转直接用 Obsidian 本体看）。

## 版本
5.1.0（对应 Alife 客户端 5.0.x）

## 5.1.0 的改动
- **加知识库半区**：新工具 `wiki_status`（统计 + 体检 + 维护日志）、`wiki_index`（按盘上实际页面重建 `wiki/index.md`，没有骨架就连 `raw/` 与四个分区一起建）、`wiki_search`（文件侧关键词检索，中文按二字组、英文按词，标题命中权重高，页数与字符双上限）、`wiki_page`（read/write/append/delete 一个 .md 页面）、`wiki_ingest`（素材 → `raw/` + 模型编译成页面）
- **note_write / note_append / note_delete 合成 `note_edit(mode=replace|append|delete)`**：三个工具的骨架每轮都要注入，合成一个省两份；工具面从 7 个变成 note 5 + wiki 4
- 新配置：`知识库·vault 本地目录`（**本地绝对路径**，留空则 wiki_* 全部报未配置）、`知识库·允许文件系统写入`（默认开；关掉后 wiki 半区只读）、`知识库·单次收录最多页数`（默认 15，1-200）
- 路径安全照 `kira_wiki` 的硬边界实现：写只允许落在知识库根之内的 `.md`；`../`、绝对路径、盘符、`~` 一律拒绝；「外部笔记」区不许写进 `wiki/`、`raw/`，也不碰 `.obsidian` 之类隐藏目录；`wiki/` 根下只许放 `index/log/overview/conventions` 四个核心页，核心页不给删；单页 512 KiB 上限
- 移植时改掉两处 Python 原实现的毛病（都写了断言）：frontmatter 正则只认 `\n`，Windows 上 CRLF 的笔记解析不出来 → 改成 `\r?\n`；`log.md` 行数用 `split('\n')` 数会把末尾换行多算一行 → 改成按行读
- 验证：`scratch/ObsidianWikiProbe` 用临时目录跑 **82 项自检全绿**（扫描/统计/体检/索引/检索/路径拒绝与放行/写入与追加/删除限制/只读操作不落盘/幂等建骨架/slug/**收录：raw 落料与撞名、JSON 抠取与裹代码块、分区归位、slug、上限丢弃、空正文跳过、名字里带 `../` 被磨平、与已有页面重名标冲突且一字不动**）；宿主引用环境编译门 0 错误。**模型那半（wiki_ingest 的编译步骤）也还没实弹**——它走日报同一条裸调路径（`ChatBot.LanguageModel` + 绕人设的 system 提示），编译结果的落盘逻辑已全测，本插件的 wiki_* 半区已实弹可用（它只读文件系统），note_* 要 Obsidian 开着才算

## 依赖
- Alife.Function.FunctionCaller

## AI 函数
### 笔记半区（要 Obsidian 在跑）
| 函数 | 说明 |
|---|---|
| `<note_list path? page? pagesize?/>` | 列某目录下的笔记与文件夹，分页（每页最多 100）。目录条目以 `/` 结尾 |
| `<note_read path mode?/>` | `content`（默认）/ `metadata`（含 frontmatter 与 tags）/ `structure`（大纲，列出可被 note_patch 定位的标题、块、字段） |
| `<note_edit path mode?>正文</note_edit>` | `replace` 整篇覆盖（不存在则创建）/ `append` 追加（适合日记流水）/ `delete` 删除。标签留空只允许配 delete |
| `<note_patch path operation targettype target scope? createifmissing?>正文</note_patch>` | 只改某一处：`append`/`prepend`/`replace`/`delete` 作用于 `heading`/`block`/`frontmatter` |
| `<note_search query searchtype?/>` | `simple` 全文搜索 / `jsonlogic` 按元数据写规则，返回「文件名 \| 相关度 \| 摘要」 |

### 知识库半区（不要求 Obsidian 在跑）
| 函数 | 说明 |
|---|---|
| `<wiki_status check? recent?/>` | 分区统计 + 体检清单（空页/断链/孤立页/未初始化/没素材）+ 最近维护日志 |
| `<wiki_index/>` | 按盘上实际页面重建 `wiki/index.md`（写操作，缺骨架时把 `raw/` 与四分区一起建，并记一行日志） |
| `<wiki_search query top? budgetChars?/>` | 文件侧关键词检索，返回 `rel｜标题｜分区｜相关度｜片段`；与 note_search 的区别是不依赖 Obsidian |
| `<wiki_page path mode?>正文</wiki_page>` | `read`（默认）/ `write` / `append` / `delete` 一个页面；path 是**知识库内相对路径** |
| `<wiki_ingest title? sourceUrl?>素材正文</wiki_ingest>` | 素材先落 `raw/`，再让生成模型编译成来源摘要页与实体/概念/分析页（带 `[[双链]]`），然后重建索引、记日志。模型不通时**只存 raw/ 并如实说** |

写入类的正文都放在**开闭标签之间**（`note_edit`、`note_patch`、`wiki_page` 注册成了纯文本区，Markdown 里的 `<`、`&` 不用转义），不要塞进属性。

## 两套路径别混
- `note_*` 的 path = **vault 内相对路径**（`01-架构/设计.md`），不带盘符、不带前导斜杠；带盘符一定读不到
- `wiki_*` 的 path 也是知识库内相对路径，但**生成区要写成 `entities/xxx.md`** 这种（落在 `wiki/` 下），你自己的笔记写成相对根的路径（`想法/AAA.md`）；而配置里的「知识库·vault 本地目录」才是本地绝对路径
- 生成区目录约定：`raw/`（素材）+ `wiki/{sources,entities,concepts,analyses}/` + `wiki/{index,log,overview,conventions}.md`；其余都算「外部笔记」，可读可写，但插件不擅自挪动

## 前置准备
1. **笔记半区**：Obsidian → 设置 → 第三方插件 → 浏览，安装并启用 **Local REST API**，保持 Obsidian 运行；在该插件设置页复制 **API Key**、看清服务地址，填进本插件的 `连接·Local REST API 地址` 与 `连接·API Key`
2. **知识库半区**：把 vault 的本地目录填进 `知识库·vault 本地目录`（例：`D:\小熊的obsidian知识库`）。只想要笔记那半边就别填，wiki_* 会老实报「没配」而不会乱猜
3. 地址与端口（按 Local REST API 5.4.0 的实测口径，别照旧教程填）：默认 **HTTPS，端口 27124**，证书由插件自签，`连接·信任自签证书` 保持开着才连得上；想用明文要在它设置里勾 **Enable HTTP server**，地址填 `http://127.0.0.1:27123`

## 接口核对（2026-10-16 按 Local REST API 5.4.0 的 openapi.yaml 逐条核对，本机未实弹）
- `GET /vault/{目录}/` → `{files: [...]}`，条目是**字符串**，目录以 `/` 结尾；4.x 及更早回 `{type, path}` 对象，两种形状都兼容
- `GET /vault/{笔记}` → 正文；`Accept: application/vnd.olrapi.note+json` 回元数据，`application/vnd.olrapi.document-map+json` 回大纲（2.x 的大纲是嵌套标题树 + `version`，不再是 `::` 拼的扁平键）
- `PUT` / `POST /vault/{笔记}` → 成功 **204** 空响应体
- `PATCH /vault/{笔记}` → 成功 **200 + 修改后的全文**；`Markdown-Patch-Warnings` 响应头里是百分号编码的 JSON 警告数组（插件写进日志警告级，UI 可见）
- `DELETE /vault/{笔记}` → 204；**默认不是物理删除**，带 `?permanent=true` 才永久删（参数没暴露，见下面的口径）
- `POST /search/simple/?query=` → `[{filename, score, matches:[{context, match}]}]`；`POST /search/` + `Content-Type: application/vnd.olrapi.jsonlogic+json` → `[{filename, result}]`
- `.obsidian` 配置目录一律 **403**（官方主动封的，防止拿 key Remote Code Execution），密钥填错累积十次/分钟会 **429** 限速一整分钟

## 与 KiraAI 版的差异（都是 5.4.0 那边变了，不是随手改的）
- **PATCH 换格式**。KiraAI 版用 `Operation`/`Target-Type`/`Target` 请求头 + `::` 拼接定位，这套在 5.x 已标记废弃、**6.0 移除**，而且在 GET/PUT/POST 上不显式带 `Markdown-Patch-Version: 1` 会被 `400 HeaderTargetingRequiresVersion1` 打回。改成把整条指令作为 JSON 体发送（`application/vnd.olrapi.patch-instruction+json`），标题目标为层级数组，`::` 写法保留为简写、也接受直接给 JSON 数组（标题文本本身含 `::` 时用数组那条）
- **`dataview` 搜索类型砍掉**。5.4.0 的 openapi 里 `dataview` 命中 0 次，只剩 `simple` 与 `jsonlogic`
- **删除不再声称不可逆**。接口默认按 Obsidian 的「删除文件」设置进 `.trash` 或系统回收站；没暴露 `permanent` 参数，避免让模型一句话把笔记彻底抹掉。（`wiki_page` 的 delete 是文件系统直删，**不走回收站**，描述里写明了）
- **结果出口换了**。KiraAI 工具靠返回值喂回模型；Alife 的 `[XmlFunction]` 返回值会被丢弃（`XmlHandler` 的 Invoker 签名没有结果位），所以结果统一走 `interactor.Poke`，参数与接口错误一律 `throw`（框架会把异常回灌给模型并触发一轮「需要处理函数异常」的思考，比返回一段错误文本更好使）

## 汇报与输出格式
- `note_read` 头部写 `[路径 | N 字符]`，超长按 `输出·结果最大字符数`（默认 8000）截断并标注；`wiki_page mode=read` 头部多给区划、分区、字节数、修改时间、tags
- `note_list` 头部写 `[目录 | 共 N 项，第 X/Y 页，每页 M 条]`，有下一页时直接把下一页的调用参数写出来
- `note_search` 每条 `序号. 文件名 | 相关度:0.87 | 摘要`；`wiki_search` 每条 `序号. rel | 标题 | 分区 | 相关度 | 片段`，末尾说明用了多少字符预算、怎么读全文
- `wiki_status` 先一行统计，再逐条问题（`!` 是 warn、`·` 是 info），没问题就直说「体检没发现问题」
- `note_patch` 成功后只报「改了哪一处 + 改完全文多少字符」，不把整篇回灌

## 注意事项
- `note_*` 只连你在 `连接·Local REST API 地址` 里填的地址，不连协议端，因此与 NapCat/LLOneBot/SnowLuma 无关
- 两个半区互相独立：**Obsidian 关着时 `note_*` 全部报连不上，但 `wiki_*` 照常能用**（它读的是盘上的 .md）；反过来 Obsidian 开着时两条路都通，找内容优先 `note_search`（实时索引 + 能按 tags/frontmatter 写规则）
- 「知识库·vault 本地目录」填错不会误伤：所有路径都在该目录之内解析，越界一律抛错；但它指到别的盘 Large 目录上，`wiki_index` 会真的在那儿建骨架 —— 第一次用先跑 `wiki_status check=true` 看看它认不认识这个库
- 改 `连接·` / `知识库·` 那几项配置不会即时生效：Alife 的配置系统只在模块构造时灌一次值，要重新激活角色或重载插件才按新地址/新目录重建
- API Key 明文存在插件配置 JSON 里（`D:\Alife\Storage\Character\苏染\Configuration\Suran.Obsidian.ObsidianModule.json`），别把配置目录分享出去
- 本机现状（2026-10-31 复测）：vault 是 `D:\小熊的obsidian知识库`，Local REST API **已安装且已启用**（HTTPS 27124，明文服务关着，apiKey 已生成），Obsidian 在 `D:\Program Files (x86)\Obsidian\Obsidian.exe`；刚才探 27123/27124 都不通 = **Obsidian 没开着**。库里目前笔记很少，所以知识库那半区的骨架大概率还没建 —— 对 bot 说「给知识库做个体检」就能开始
