using Alife.Framework;
using Microsoft.AspNetCore.Components.Rendering;

namespace Suran.Obsidian;

/// <summary>Obsidian笔记操作 的设置页。内容由 SuranUiKit 按配置项自动分组生成。</summary>
public sealed class ObsidianUI : ModuleUIBase<ObsidianModule, ObsidianConfig>
{
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "配置未加载");
            return;
        }
        SuranUiKit.Render(this, StateHasChanged, b, Configuration, "Obsidian笔记操作", "经 Local REST API 读写笔记；知识库体检、索引与检索直接走文件系统", "#7c3aed");
    }
}
