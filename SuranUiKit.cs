using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Suran.Obsidian;

/// <summary>
/// 通用配置界面。
///
/// 这些插件的配置项命名一直是「分组·名称」（`检索·默认返回条数`、`网络·代理地址`），
/// 说明文字又都写在 Description 里、动辄两三百字。宿主自动生成的页面把这两样平铺成一长列，
/// 结果就是「十几个输入框 + 十几段小字」，改一个开关要滚半天，还得逐条读才知道自己在改什么。
///
/// 这个渲染器做三件事，都不碰业务：
///   1. 按 DisplayName 里的 `·` 自动分组，每组一张可折叠卡片（默认只展开第一组）
///   2. 说明不再占正文，缩成标签下一行小字；超过 8 项时给一个搜索框
///   3. 一套 CSS 变量统一圆角/间距/字号/主色，跟随宿主主题（有 alife-* 变量就用，没有跟随系统深浅色）
///
/// 每个插件只需要在 XxxUI 里调一次 Render。
/// </summary>
public static class SuranUiKit
{
    /// <summary>折叠状态按「配置类型 + 分组」记，设置页同时只开一个，静态字典够用</summary>
    static readonly Dictionary<string, bool> Open = new(StringComparer.Ordinal);
    static readonly Dictionary<string, string> Queries = new(StringComparer.Ordinal);

    /// <summary>
    /// 渲染上下文。ComponentBase.StateHasChanged 是 protected，静态方法调不到，
    /// 只能让调用方把「怎么刷新」作为委托传进来。顺带把 receiver 也放一起，少传一个参数。
    /// </summary>
    sealed class Ctx
    {
        public ComponentBase Host = null!;
        public Action Refresh = static () => { };
    }

    /// <summary>头部动作按钮。给需要"点一下跑个探测"的插件用，不用为两个按钮写一整页 UI</summary>
    public sealed record UiAction(string Label, Func<Task> Run);

    sealed class Field
    {
        public PropertyInfo Prop = null!;
        public string Group = "常规";
        public string Label = "";
        public string Hint = "";
        public int Order;
    }

    public static void Render(ComponentBase host, Action refresh, RenderTreeBuilder b, object config,
        string title, string subtitle, string accent,
        IReadOnlyList<UiAction>? actions = null, string? notice = null)
    {
        Ctx ctx = new() { Host = host, Refresh = refresh };
        int i = 0;
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-root");
        b.AddAttribute(i++, "style", "--su-accent:" + accent + ";");

        InjectStyles(b, ref i);

        string key = config.GetType().FullName ?? "config";
        List<Field> fields = Describe(config);
        string query = Queries.TryGetValue(key, out string? stored) ? stored : "";

        BuildHeader(b, ref i, title, subtitle, accent, fields.Count, fields.Select(f => f.Group).Distinct().Count());

        if (actions is { Count: > 0 })
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "su-actions");
            foreach (UiAction action in actions)
            {
                UiAction captured = action;
                b.OpenElement(i++, "button");
                b.AddAttribute(i++, "type", "button");
                b.AddAttribute(i++, "class", "su-act");
                b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(host, async _ =>
                {
                    await captured.Run();
                    refresh();
                }));
                b.AddContent(i++, captured.Label);
                b.CloseElement();
            }
            b.CloseElement();
        }

        if (!string.IsNullOrWhiteSpace(notice))
        {
            b.OpenElement(i++, "pre");
            b.AddAttribute(i++, "class", "su-notice");
            b.AddContent(i++, notice);
            b.CloseElement();
        }

        if (fields.Count > 8)
            BuildSearch(ctx, b, ref i, key, query);

        List<Field> visible = Filter(fields, query);
        if (visible.Count == 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "su-empty");
            b.AddContent(i++, "没有匹配「" + query + "」的配置项");
            b.CloseElement();
            b.CloseElement();
            return;
        }

        bool first = true;
        foreach (IGrouping<string, Field> group in visible.GroupBy(f => f.Group))
        {
            string groupKey = key + "|" + group.Key;
            bool open = Open.TryGetValue(groupKey, out bool stored2) ? stored2 : first;
            first = false;

            BuildGroup(ctx, b, ref i, groupKey, group.Key, group.ToList(), open, config);
        }

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-foot");
        b.AddContent(i++, "改完点下面的保存生效。说明里的数字和「实测」都是当初调出来的结论，改之前值得看一眼。");
        b.CloseElement();

        b.CloseElement();
    }

    // ── 反射：把 Config 的公开可写属性变成字段描述 ──────────────

    static List<Field> Describe(object config)
    {
        List<Field> list = new();
        foreach (PropertyInfo prop in config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;

            string raw = prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? prop.Name;
            (string group, string label) = Split(raw);

            list.Add(new Field {
                Prop = prop,
                Group = group,
                Label = label,
                Hint = prop.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "",
                Order = prop.MetadataToken,   // 声明顺序，GetProperties 不保证
            });
        }
        return list.OrderBy(f => f.Order).ToList();
    }

    static (string Group, string Label) Split(string raw)
    {
        int dot = raw.IndexOf('·');
        if (dot <= 0 || dot >= raw.Length - 1)
            return ("常规", raw.Trim());
        return (raw[..dot].Trim(), raw[(dot + 1)..].Trim());
    }

    static List<Field> Filter(List<Field> fields, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return fields;

        string q = query.Trim();
        return fields.Where(f =>
            f.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.Group.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.Hint.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // ── 头部 / 搜索 ────────────────────────────────────────────

    static void BuildHeader(RenderTreeBuilder b, ref int i, string title, string subtitle, string accent, int fields, int groups)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-head");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-head-main");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-title");
        b.AddContent(i++, title);
        b.CloseElement();
        if (subtitle.Length > 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "su-sub");
            b.AddContent(i++, subtitle);
            b.CloseElement();
        }
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-meta");
        b.AddContent(i++, fields + " 项 · " + groups + " 组");
        b.CloseElement();

        b.CloseElement();
    }

    static void BuildSearch(Ctx ctx, RenderTreeBuilder b, ref int i, string key, string query)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-search");

        b.OpenComponent<Input<string>>(i++);
        b.AddAttribute(i++, "Value", query);
        b.AddAttribute(i++, "Placeholder", "搜配置项（名称、分组或说明里的字）");
        b.AddAttribute(i++, "AllowClear", true);
        b.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<string>(ctx.Host, value =>
        {
            Queries[key] = value ?? "";
            ctx.Refresh();
        }));
        b.CloseComponent();

        b.CloseElement();
    }

    // ── 分组卡片 ───────────────────────────────────────────────

    static void BuildGroup(Ctx ctx, RenderTreeBuilder b, ref int i, string groupKey, string name, List<Field> fields, bool open, object config)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", open ? "su-card su-card-open" : "su-card");

        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "su-card-head");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(ctx.Host, _ =>
        {
            Open[groupKey] = !open;
            ctx.Refresh();
        }));

        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "su-caret");
        b.AddContent(i++, open ? "▾" : "▸");
        b.CloseElement();

        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "su-card-name");
        b.AddContent(i++, name);
        b.CloseElement();

        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "su-card-count");
        b.AddContent(i++, fields.Count.ToString(CultureInfo.InvariantCulture));
        b.CloseElement();

        b.CloseElement();   // head

        if (open)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "su-card-body");
            foreach (Field field in fields)
                BuildRow(ctx, b, ref i, config, field);
            b.CloseElement();
        }

        b.CloseElement();   // card
    }

    // ── 单行：标签 + 说明 + 控件 ───────────────────────────────

    static void BuildRow(Ctx ctx, RenderTreeBuilder b, ref int i, object config, Field field)
    {
        Type type = field.Prop.PropertyType;
        object? value = SafeGet(field.Prop, config);

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", type == typeof(bool) ? "su-row su-row-bool" : "su-row");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-row-text");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-label");
        b.AddContent(i++, field.Label);
        b.CloseElement();
        if (field.Hint.Length > 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "su-hint");
            b.AddContent(i++, field.Hint);
            b.CloseElement();
        }
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-ctl");

        if (type == typeof(bool))
        {
            bool current = value is bool flag && flag;
            b.OpenComponent<Switch>(i++);
            b.AddAttribute(i++, "Checked", current);
            b.AddAttribute(i++, "CheckedChildren", "开");
            b.AddAttribute(i++, "UnCheckedChildren", "关");
            b.AddAttribute(i++, "CheckedChanged", EventCallback.Factory.Create<bool>(ctx.Host, next =>
            {
                Commit(ctx, config, field.Prop, next);
            }));
            b.CloseComponent();
        }
        else if (type == typeof(int))
        {
            int current = value is int number ? number : 0;
            b.OpenComponent<InputNumber<int>>(i++);
            b.AddAttribute(i++, "Value", current);
            b.AddAttribute(i++, "Style", "width:100%;");
            b.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<int>(ctx.Host, next =>
            {
                Commit(ctx, config, field.Prop, next);
            }));
            b.CloseComponent();
        }
        else if (type == typeof(double) || type == typeof(float))
        {
            double current = value is double d ? d : value is float f ? f : 0;
            b.OpenComponent<InputNumber<double>>(i++);
            b.AddAttribute(i++, "Value", current);
            b.AddAttribute(i++, "Step", 0.1);
            b.AddAttribute(i++, "Style", "width:100%;");
            b.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<double>(ctx.Host, next =>
            {
                Commit(ctx, config, field.Prop, (float)next);
            }));
            b.CloseComponent();
        }
        else if (type == typeof(string))
        {
            b.OpenComponent<Input<string>>(i++);
            b.AddAttribute(i++, "Value", value as string ?? "");
            b.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<string>(ctx.Host, next =>
            {
                Commit(ctx, config, field.Prop, next ?? "");
            }));
            b.CloseComponent();
        }
        else
        {
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "su-unsupported");
            b.AddContent(i++, value?.ToString() ?? "（不支持的类型）");
            b.CloseElement();
        }

        b.CloseElement();   // ctl
        b.CloseElement();   // row
    }

    static object? SafeGet(PropertyInfo prop, object config)
    {
        try { return prop.GetValue(config); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// 写回配置。失败不抛——一个字段写不进去不该让整个设置页白掉，
    /// 但也不能假装成功，所以错误吞掉之后框架保存时值仍是旧的，用户会看见。
    /// </summary>
    static void Commit(Ctx ctx, object config, PropertyInfo prop, object value)
    {
        try
        {
            if (prop.PropertyType == typeof(float) && value is double d)
                value = (float)d;
            prop.SetValue(config, value);
        }
        catch (Exception)
        {
            // 类型不匹配（比如把空串写给 int）就保持旧值
        }
        ctx.Refresh();
    }

    // ── 样式 ───────────────────────────────────────────────────

    static void InjectStyles(RenderTreeBuilder b, ref int i)
    {
        b.OpenElement(i++, "style");
        b.AddContent(i++, """
            .su-root{
                --su-accent:#2f6fed;
                --su-radius:12px;
                --su-bg:var(--alife-bg-container,#ffffff);
                --su-bg-soft:var(--alife-bg-layout,#f7f8fa);
                --su-border:var(--alife-border,#e6e8eb);
                --su-text:var(--alife-text,#1f2328);
                --su-text-sub:var(--alife-text-secondary,#6b7280);
                max-width:780px;padding:20px 22px 24px;border-radius:16px;
                background:var(--su-bg);color:var(--su-text);
                border:1px solid var(--su-border);
                font-size:13px;line-height:1.6;
            }
            @media (prefers-color-scheme:dark){
                .su-root{
                    --su-bg:var(--alife-bg-container,#16181d);
                    --su-bg-soft:var(--alife-bg-layout,#1e2127);
                    --su-border:var(--alife-border,#2b2f36);
                    --su-text:var(--alife-text,#e7e9ec);
                    --su-text-sub:var(--alife-text-secondary,#9aa1ab);
                }
            }
            .su-head{display:flex;align-items:flex-start;gap:12px;margin-bottom:14px;}
            .su-head-main{flex:1 1 auto;min-width:0;}
            .su-title{font-size:19px;font-weight:700;letter-spacing:.2px;}
            .su-sub{font-size:12px;color:var(--su-text-sub);margin-top:3px;}
            .su-meta{flex:0 0 auto;font-size:11px;color:var(--su-text-sub);
                padding:3px 9px;border-radius:999px;background:var(--su-bg-soft);
                border:1px solid var(--su-border);white-space:nowrap;}
            .su-search{margin-bottom:12px;}
            .su-actions{display:flex;gap:8px;flex-wrap:wrap;margin:0 0 12px;}
            .su-act{font-size:12px;padding:4px 14px;border-radius:999px;cursor:pointer;
                font-family:inherit;font-weight:600;
                border:1px solid var(--su-accent);background:transparent;color:var(--su-accent);
                transition:background .15s ease,color .15s ease;}
            .su-act:hover{background:var(--su-accent);color:#fff;}
            .su-notice{margin:0 0 12px;padding:10px 12px;border-radius:10px;
                max-height:260px;overflow:auto;white-space:pre-wrap;word-break:break-word;
                font-size:11.5px;line-height:1.7;background:var(--su-bg-soft);
                border:1px solid var(--su-border);
                font-family:ui-monospace,Consolas,"Cascadia Mono",monospace;}
            .su-card{margin-bottom:10px;border:1px solid var(--su-border);border-radius:var(--su-radius);
                background:var(--su-bg);overflow:hidden;transition:box-shadow .18s ease;}
            .su-card-open{box-shadow:0 1px 2px rgba(15,23,42,.05);}
            .su-card-head{display:flex;align-items:center;gap:8px;width:100%;padding:10px 14px;
                border:0;background:var(--su-bg-soft);color:inherit;font:inherit;font-weight:600;
                cursor:pointer;text-align:left;}
            .su-card-head:hover{background:var(--su-bg);}
            .su-caret{width:12px;color:var(--su-accent);font-size:11px;}
            .su-card-name{flex:1 1 auto;}
            .su-card-count{font-size:11px;font-weight:500;color:var(--su-text-sub);
                padding:1px 7px;border-radius:999px;background:var(--su-bg);
                border:1px solid var(--su-border);}
            .su-card-body{padding:4px 14px 12px;}
            .su-row{display:grid;grid-template-columns:minmax(0,1fr) 210px;gap:14px;
                align-items:start;padding:9px 0;border-bottom:1px dashed var(--su-border);}
            .su-row:last-child{border-bottom:0;}
            .su-row-bool{grid-template-columns:minmax(0,1fr) auto;align-items:center;}
            .su-row-text{min-width:0;}
            .su-label{font-weight:600;font-size:12.5px;}
            .su-row-bool .su-label{font-weight:500;}
            .su-hint{font-size:11px;color:var(--su-text-sub);margin-top:2px;line-height:1.55;
                display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden;}
            .su-row:hover .su-hint{-webkit-line-clamp:unset;}
            .su-ctl{display:flex;align-items:center;justify-content:flex-end;}
            .su-ctl>*{width:100%;}
            .su-row-bool .su-ctl>*{width:auto;}
            .su-empty{padding:26px 0;text-align:center;font-size:12px;color:var(--su-text-sub);}
            .su-foot{margin-top:10px;font-size:11px;color:var(--su-text-sub);}
            .su-unsupported{font-size:11px;color:var(--su-text-sub);}
            """);
        b.CloseElement();
    }
}
