# Suran.Obsidian

Obsidian 笔记库读写，移植自工作区的 `kira-ai-plugin-obsidian`（KiraAI Python 插件版）：七个 LLM 工具覆盖列目录、读笔记、整篇写入、追加、局部修改、删除、搜索。所有解析工作（frontmatter、文档结构、搜索打分）都在 Obsidian 一侧完成，插件只是 Obsidian 社区插件 [Local REST API](https://github.com/coddingtonbear/obsidian-local-rest-api) 的薄壳。

## 版本
4.0.0（对应 Alife 客户端 4.2.x）

## 依赖
- Alife.Function.FunctionCaller

## AI 函数
| 函数 | 说明 |
|---|---|
| `<note_list path? page? pagesize?/>` | 列某目录下的笔记与文件夹，分页（每页最多 100）。目录条目以 `/` 结尾 |
| `<note_read path mode?/>` | `content`（默认，Markdown 正文）/ `metadata`（含 frontmatter 与 tags 的元数据 JSON）/ `structure`（大纲，列出可被 note_patch 定位的标题、块、字段） |
| `<note_write path>正文</note_write>` | 整篇写入，已存在的会被**全部覆盖**；不存在则创建 |
| `<note_append path>正文</note_append>` | 追加到末尾，不动已有内容；不存在则创建 |
| `<note_patch path operation targettype target scope? createifmissing?>正文</note_patch>` | 只改某一处：`append`/`prepend`/`replace`/`delete` 作用于 `heading`/`block`/`frontmatter` |
| `<note_delete path/>` | 删除一篇笔记 |
| `<note_search query searchtype?/>` | `simple` 全文搜索 / `jsonlogic` 按元数据写规则，返回「文件名 \| 相关度 \| 摘要」 |

写入类的正文都放在**开闭标签之间**（这三个标签注册成了纯文本区，Markdown 里的 `<`、`&` 不需要转义），不要塞进属性。

## 前置准备
1. Obsidian → 设置 → 第三方插件 → 浏览，安装并启用 **Local REST API**，保持 Obsidian 运行
2. 在该插件设置页复制 **API Key**，并看清它显示的服务地址
3. 填进本插件配置：`连接·Local REST API 地址` + `连接·API Key`

地址与端口（5.4.0 的实测口径，别按旧教程填）：
- 默认是 **HTTPS，端口 27124**，证书由插件自己生成的本地 CA 签发，系统默认不认 → 配置项 `连接·信任自签证书` 保持开启才连得上；想关掉它就把 `https://127.0.0.1:27124/obsidian-local-rest-api.crt` 下载后装进系统受信任根证书
- 想用明文，需要在 Local REST API 设置里勾 **Enable HTTP server**，地址填 `http://127.0.0.1:27123`
- KiraAI 版配置提示里写的 `http://127.0.0.1:27123` 指的是这条明文服务，且老版本 HTTPS 用的也是 27123；两个版本对不上端口会一直连不上

## 接口核对（2026-10-16 按 Local REST API 5.4.0 的 openapi.yaml 逐条核对，本机未实弹）
- `GET /vault/{目录}/` → `{files: [...]}`，条目是**字符串**，目录以 `/` 结尾；4.x 及更早回 `{type, path}` 对象，两种形状都兼容
- `GET /vault/{笔记}` → 正文；`Accept: application/vnd.olrapi.note+json` 回元数据，`application/vnd.olrapi.document-map+json` 回大纲（2.x 的大纲是嵌套标题树 + `version`，不再是 `::` 拼的扁平键）
- `PUT` / `POST /vault/{笔记}` → 成功 **204** 空响应体
- `PATCH /vault/{笔记}` → 成功 **200 + 修改后的全文**；`Markdown-Patch-Warnings` 响应头里是百分号编码的 JSON 警告数组（插件写进日志警告级，UI 可见）
- `DELETE /vault/{笔记}` → 204；**默认不是物理删除**，带 `?permanent=true` 才永久删（参数没暴露，见下面的口径）
- `POST /search/simple/?query=` → `[{filename, score, matches:[{context, match}]}]`；`POST /search/` + `Content-Type: application/vnd.olrapi.jsonlogic+json` → `[{filename, result}]`
- `.obsidian` 配置目录一律 **403**（官方主动封的，防止拿 key  Remote Code Execution），密钥填错累积十次/分钟会 **429** 限速一整分钟

## 与 KiraAI 版的差异（都是 5.4.0 那边变了，不是随手改的）
- **PATCH 换格式**。KiraAI 版用 `Operation`/`Target-Type`/`Target` 请求头 + `::` 拼接定位，这套在 5.x 已标记废弃、**6.0 移除**，而且在 GET/PUT/POST 上不显式带 `Markdown-Patch-Version: 1` 会被 `400 HeaderTargetingRequiresVersion1` 打回。改成把整条指令作为 JSON 体发送（`application/vnd.olrapi.patch-instruction+json`），标题目标为层级数组，`::` 写法保留为简写、也接受直接给 JSON 数组（标题文本本身含 `::` 时用数组那条）
- **`dataview` 搜索类型砍掉**。5.4.0 的 openapi 里 `dataview` 命中 0 次，只剩 `simple` 与 `jsonlogic`，所以 `searchtype` 只有这两个取值
- **`delete` 操作补上**。新版指令集里 patch 的 operation 多了 `delete`（删掉目标小节），一并放开
- **删除不再声称不可逆**。接口的默认行为是按 Obsidian 的「删除文件」设置进 `.trash` 或系统回收站，描述按实情写；没有暴露 `permanent` 参数，避免让模型一句话把笔记彻底抹掉
- **结果出口换了**。KiraAI 工具靠返回值把结果喂回模型；Alife 的 `[XmlFunction]` 返回值会被丢弃（`XmlHandler` 的 Invoker 签名没有结果位），所以结果统一走 `interactor.Poke`，参数与接口错误一律 `throw`（框架会把异常回灌给模型并触发一轮"需要处理函数异常"的思考，比返回一段错误文本更好使）

## 汇报与输出格式
- `note_read` 头部写 `[路径 | N 字符]`，超长按配置 `输出·结果最大字符数`（默认 8000）截断并标注，提示改用 `note_patch` 定位小节或分段读
- `note_list` 头部写 `[目录 | 共 N 项，第 X/Y 页，每页 M 条]`，有下一页时直接把下一页的调用参数写出来
- `note_search` 每条 `序号. 文件名 | 相关度:0.87 | 摘要`，摘要取第一条命中上下文按 `输出·搜索摘要字符数` 截断；末尾固定提示用 `note_read` 读全文，命中超出 `输出·搜索最多条数` 时说明还剩多少
- `note_patch` 成功后只报"改了哪一处 + 改完全文多少字符"，不把整篇回灌

## 注意事项
- 只连你自己在配置里填的 Local REST API 地址，不连协议端，因此与 NapCat/LLOneBot/SnowLuma 无关，任何协议端都能用
- 本机现在**没装** Local REST API（vault `D:\obsidian知识库` 的 `.obsidian` 下没有 `plugins` 目录，27123/27124 都没监听），所以这版只做到编译通过（0 警告）+ 离线核对：注册面文档行、URL 逐段编码、`note_patch` 的 target/值构造、目录列表两种返回形状（5.x 字符串数组 / ≤4.x 对象数组）、搜索排序与摘要截断、各错误码话术，都在 `scratch/obsidian-doc` 探针里用 5.4.0 形状的假响应跑过一遍（`dotnet run --project ObsidianDoc.csproj`）；**七个工具的实弹验证还没做**，需要先在 Obsidian 里装好插件、把地址与 key 填进配置
- 改 `连接·` 那几项配置后不会即时生效：Alife 的配置系统只在模块构造时灌一次值，要重新激活角色或重载插件才会按新地址重建连接
- API Key 是明文存在插件配置 JSON 里的（`D:\Alife\Storage\Character\苏染\Configuration\Suran.Obsidian.ObsidianModule.json`），别把配置目录分享出去
- 路径都是 vault 内相对路径，反斜杠会被自动换成正斜杠，但带盘符的本地路径一定读不到——那是 `Alife.Function.FileService` 的活，两者别混
