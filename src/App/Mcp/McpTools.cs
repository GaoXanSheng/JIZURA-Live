using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using JizuraLive.App.I18n;
using JizuraLive.App.Views;
using JizuraLive.Engine;
using JizuraLive.Export;
using JizuraLive.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Mcp;

// MCP 17 个工具的 C# 直调实现（对位旧前端的 agent-api.ts）。
// 协议语义（initialize / tools/list / tools/call、revision 乐观锁、工具级错误码映射）逐条对位；
// 区别只有一处本质变化：旧版经页面求值桥，这里直接读写 EditorState / PreviewEngine /
// Planner / ExportJob —— 无页面、无脚本求值。
//
// revision 契约：带 revision 的写操作要求参数与 EditorState.Revision 相等，否则 stale_revision（-32602）；
// 写操作返回值附加当前 revision（与 JS 一致，代理可直接用于下一次写）。
// 线程：所有工具体都在 UI 线程上跑（HTTP 线程经 Dispatcher 分发），与面板编辑同一把闸。
public sealed class McpTools
{
    private readonly EditorState _state;
    private readonly PreviewEngine _preview;

    private static readonly string[] ChipGroups =
        { "layout", "enter", "hold", "exit", "decor", "treat", "bg", "cam", "trans" };

    private static readonly string[] LegacyKinds =
        { "mp4", "png", "pnga", "pngl", "pngf", "webm", "webmfront", "subwebm" };

    private static readonly string[] Aspects = { "16:9", "9:16", "4:3", "3:4", "1:1", "4:5", "21:9" };

    public McpTools(EditorState state, PreviewEngine preview)
    {
        _state = state;
        _preview = preview;
        Omakase.Install(); // Reg.RandomOk 接线（MCP 直调路径也保证装上）
    }

    // ============================================================ 协议层

    /// <summary>顶级工具实例（App 启动时构造；测试可另行构造）。</summary>
    public static McpTools? Current { get; set; }

    /// <summary>JSON-RPC 入口：返回响应对象；通知（notifications/*）返回 null（HTTP 层回 202）。</summary>
    public JObject? Rpc(JObject body)
    {
        try
        {
            Check(body["jsonrpc"]?.Type == JTokenType.String && (string?)body["jsonrpc"] == "2.0"
                  && body["method"]?.Type == JTokenType.String, L.T("Mcp.Err.BadJsonRpc"));
            var method = (string)body["method"]!;
            if (method.StartsWith("notifications/", StringComparison.Ordinal)) return null; // 通知：无响应
            var result = RpcMethod(method, body["params"] as JObject);
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = body["id"] != null ? body["id"]!.DeepClone() : JValue.CreateNull(),
                ["result"] = result
            };
        }
        catch (Exception e)
        {
            var code = e is ToolError te
                ? te.Code switch
                {
                    "method_not_found" => -32601,
                    "invalid_input" or "stale_revision" => -32602,
                    _ => -32603
                }
                : -32603;
            Log.Warn($"[Mcp] 工具调用失败：{e.Message}");
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = body["id"]?.DeepClone() ?? JValue.CreateNull(),
                ["error"] = new JObject { ["code"] = code, ["message"] = e.Message }
            };
        }
    }

    private JObject RpcMethod(string method, JObject? p)
    {
        if (method == "initialize")
        {
            var ver = typeof(McpTools).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            return new JObject
            {
                ["protocolVersion"] = p?.Value<string>("protocolVersion") ?? "2025-06-18",
                ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = true } },
                ["serverInfo"] = new JObject { ["name"] = "jizura-live", ["version"] = ver }
            };
        }
        if (method == "ping") return new JObject();
        if (method == "tools/list") return new JObject { ["tools"] = ToolList() };
        if (method == "tools/call")
        {
            Check(p?["name"]?.Type == JTokenType.String, L.T("Mcp.Err.MissingName"));
            var name = (string)p!["name"]!;
            var def = ToolList().OfType<JObject>().FirstOrDefault(t => (string?)t["name"] == name);
            if (def == null)
                return new JObject
                {
                    ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = L.F("Mcp.Err.UnknownTool", name) } },
                    ["isError"] = true
                };
            var args = p["arguments"] as JObject ?? new JObject();
            var outObj = ExecTool(def, args);
            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject { ["type"] = "text", ["text"] = outObj.ToString(Formatting.None) }
                },
                ["isError"] = false
            };
        }
        throw new ToolError("method_not_found", L.F("Mcp.Err.UnknownMethod", method));
    }

    /// <summary>tools/call 的公共闸：初始化检查、导出期只挡写、revision 契约、写回附加 revision。</summary>
    private JObject ExecTool(JObject def, JObject args)
    {
        var readOnly = def["annotations"]?["readOnlyHint"]?.Type == JTokenType.Boolean
                       && (bool)def["annotations"]!["readOnlyHint"]!;
        var run = OnUi(() =>
        {
            Check(_state.Plan != null, L.T("Mcp.Err.EditorNotReady"));
            if (!readOnly) Check(!Exporting, L.T("Mcp.Err.ExportBusy"));
            if (!readOnly)
            {
                // schema 里 revision 必填的工具缺参即拒；带 revision 但与当前值不符即拒
                var required = def["inputSchema"]?["required"] as JArray;
                if (required != null && required.Any(x => (string?)x == "revision")
                    && args["revision"]?.Type != JTokenType.Integer)
                    Fail("stale_revision", L.F("Mcp.Err.MissingRevision", _state.Revision));
                if (args["revision"] != null && (int)args["revision"]! != _state.Revision)
                    Fail("stale_revision",
                        L.F("Mcp.Err.StaleRevision", args["revision"], _state.Revision));
            }
            var name = (string?)def["name"] ?? "";
            var outObj = RunTool(name, args) ?? new JObject { ["ok"] = true };
            // 写操作返回值带上新 revision（重规划已递增）：代理直接用返回值做下一次写
            if (!readOnly) outObj["revision"] = _state.Revision;
            return outObj;
        });
        return run;
    }

    private JObject? RunTool(string name, JObject a) => name switch
    {
        "get_state" => ToolGetState(a),
        "list_options" => ToolListOptions(a),
        "set_lyrics" => ToolSetLyrics(a),
        "update_settings" => ToolUpdateSettings(a),
        "set_timing" => ToolSetTiming(a),
        "edit_line" => ToolEditLine(a),
        "edit_cut" => ToolEditCut(a),
        "set_techniques" => ToolSetTechniques(a),
        "set_locks" => ToolSetLocks(a),
        "randomize" => ToolRandomize(a),
        "history" => ToolHistory(a),
        "preview" => ToolPreview(a),
        "tap_sync" => ToolTap(a),
        "project" => ToolProject(a),
        "start_export" => ToolStartExport(a),
        "get_export_status" => ToolGetExportStatus(),
        "reset_project" => ToolReset(),
        _ => throw new ToolError("method_not_found", L.F("Mcp.Err.UnknownTool", name))
    };

    // ============================================================ 工具实现

    private JObject ToolGetState(JObject a)
    {
        var detail = a["detail"]?.Type != JTokenType.Boolean || (bool)a["detail"]!;
        var plan = _state.Plan;
        var t = _preview.CurrentTime;
        var c = plan != null ? Planner.CutAt(plan, t) : null;
        var (hi, hlen, hb, hf) = _state.LookHistoryInfo();
        var baseObj = new JObject
        {
            ["revision"] = _state.Revision,
            ["playing"] = _preview.IsPlaying,
            ["t"] = Round3(t),
            ["duration"] = plan != null ? Round3(plan.Duration) : 0d,
            ["loop"] = _preview.Loop ? "all" : "off",
            ["loading"] = new JObject
            {
                ["audio"] = _state.AudioFileName != null && _state.Audio == null,
                // WPF 无 document.fonts：字体随进程就绪，恒 false
                ["fonts"] = false,
                ["exporting"] = Exporting,
                ["tap"] = TapSession.Active
            },
            ["history"] = new JObject { ["i"] = hi, ["len"] = hlen, ["canBack"] = hb, ["canFwd"] = hf }
        };
        if (!detail) return baseObj;

        var p = _state.Project;
        var pj = ProjectJson(p);
        var outp = (JObject)baseObj.DeepClone();
        outp["project"] = new JObject
        {
            ["title"] = p.Title,
            ["artist"] = p.Artist,
            ["lang"] = string.IsNullOrEmpty(p.Lang) ? "auto" : p.Lang,
            ["style"] = p.Style,
            ["seed"] = p.Seed,
            ["aspect"] = p.Aspect,
            ["resolution"] = p.Res,
            ["fps"] = p.Fps,
            ["bpm"] = p.Timing.Bpm,
            ["audioName"] = _state.AudioFileName != null ? (JToken)_state.AudioFileName : JValue.CreateNull(),
            ["lyrics"] = p.Lyrics,
            ["timing"] = pj["timing"]?.DeepClone() ?? new JObject(),
            ["overrides"] = pj["overrides"]?.DeepClone() ?? new JObject(),
            ["enabled"] = pj["enabled"]?.DeepClone() ?? new JObject(),
            // C# Project 模型没有 exportRange 字段（导出范围尚未接入 Project/导出层）
            ["exportRange"] = JValue.CreateNull()
        };

        var lines = new JArray();
        for (var i = 0; i < plan!.Lines.Count; i++)
        {
            var ln = plan.Lines[i];
            var ov = OverrideAt(p, i);
            lines.Add(new JObject
            {
                ["line"] = i + 1,
                ["start"] = Round3(ln.Start),
                ["end"] = Round3(ln.End),
                ["interlude"] = ln.Interlude,
                ["text"] = ln.Text,
                ["manual"] = p.Timing.LineTimes.ContainsKey(i.ToString()),
                ["layout"] = ov?["layout"]?.Type == JTokenType.String && (string)ov["layout"]! != ""
                    ? (JToken)ov["layout"]!.DeepClone()
                    : JValue.CreateNull(),
                ["cuts"] = plan.Cuts.Count(x => x.Line == i),
                ["locked"] = Truthy(ov?["lock"])
            });
        }
        outp["lines"] = lines;

        if (c == null)
        {
            outp["cut"] = JValue.CreateNull();
        }
        else
        {
            // cut=全局序号（与界面切分信息面板一致）；cutInLine=行内序号，传给 edit_cut / randomize cut 用的就是它
            var same = plan.Cuts
                .Where(x => x.Line == c.Line && Reg.Layouts.TryGetValue(x.Layout, out var ld) && ld != null && !ld.Special)
                .ToList();
            var inLine = same.FindIndex(x => x.Index == c.Index);
            outp["cut"] = new JObject
            {
                ["cut"] = c.Index + 1,
                ["cutInLine"] = inLine >= 0 ? inLine + 1 : 0,
                ["line"] = c.Line + 1,
                ["text"] = c.Text,
                ["start"] = Round3(c.Start),
                ["end"] = Round3(c.End),
                ["layout"] = c.Layout,
                ["enter"] = c.Enter,
                ["hold"] = c.Hold,
                ["exit"] = c.Exit,
                ["cam"] = c.Cam,
                ["trans"] = c.Trans != null ? (JToken)c.Trans : JValue.CreateNull()
            };
        }
        return outp;
    }

    private JObject ToolListOptions(JObject a)
    {
        var cat = a.Value<string>("category");
        if (cat == "settings")
            return new JObject { ["note"] = L.T("Mcp.Tool.list_options.SettingsNote") };

        var items = new JArray();
        if (cat == "style")
        {
            foreach (var id in JStyles.StyleOrder)
            {
                if (!Reg.Styles.TryGetValue(id, out var sd)) continue;
                items.Add(new JObject
                {
                    ["id"] = id,
                    ["label"] = sd.Name,
                    ["description"] = Truncate(sd.Extra?.GetValueOrDefault("desc")?.ToString() ?? "", 200),
                    ["isNew"] = Omakase.IsExtra("style", id) ? true : null
                });
            }
        }
        else if (cat == "font")
        {
            foreach (var (id, ff) in JFonts.Table)
            {
                items.Add(new JObject
                {
                    ["id"] = id,
                    ["label"] = ff.Label,
                    ["description"] = "",
                    ["isNew"] = Omakase.IsExtra("font", id) ? true : null
                });
            }
        }
        else
        {
            Check(cat != null && Reg.GroupKeys.Contains(cat), L.F("Mcp.Err.UnknownCategory", cat));
            var p = _state.Project;
            foreach (var id in Reg.OrderOf(cat!))
            {
                var d = RegDef(cat!, id);
                if (d == null || d.Special) continue;
                var item = new JObject
                {
                    ["id"] = id,
                    ["label"] = d.Name,
                    // C# 的注册条目没有 desc 字段（JS 的 v.desc 未移植）：描述统一留空
                    ["description"] = "",
                    ["isNew"] = Omakase.IsExtra(cat!, id) ? true : null
                };
                // 与 JS 一致：只有 project.enabled 里存在该组时才给 enabled（否则该字段缺省）
                if (p.Enabled.TryGetValue(cat!, out var en))
                    item["enabled"] = !en.TryGetValue(id, out var v) || v;
                item["eligibleForRandom"] = Omakase.RandomOk(p, cat!, id);
                items.Add(item);
            }
        }

        var list = items.ToList();
        var query = a.Value<string>("query");
        if (!string.IsNullOrEmpty(query))
            list = list.Where(x => ((string?)x["id"] + " " + (string?)x["label"] + " " + (string?)x["description"])
                .ToLowerInvariant().Contains(query.ToLowerInvariant(), StringComparison.Ordinal)).ToList();
        var page = Math.Max(1, a["page"]?.Type == JTokenType.Integer ? (int)a["page"]! : 1);
        var size = Math.Min(100, Math.Max(1, a["pageSize"]?.Type == JTokenType.Integer ? (int)a["pageSize"]! : 30));
        return new JObject
        {
            ["total"] = list.Count,
            ["page"] = page,
            ["items"] = new JArray(list.Skip((page - 1) * size).Take(size))
        };
    }

    private JObject ToolSetLyrics(JObject a)
    {
        if (a["clear"]?.Type == JTokenType.Boolean && (bool)a["clear"]!)
        {
            var p0 = _state.Project;
            // 已是空的：JS 里只把焦点还给歌词框，不改工程
            if (p0.Lyrics.Trim().Length > 0 || p0.Timing.LineTimes.Count > 0)
            {
                _preview.Pause();
                _state.Mutate(p =>
                {
                    p.Lyrics = "";
                    p.Timing.LineTimes.Clear();
                    p.Overrides.Clear();
                }, L.T("Mcp.Mutate.ClearLyrics"));
                _state.ReplanNow();
                ProjectIO.FlushAutosave(_state.Project);
                _preview.Seek(0);
            }
            return new JObject { ["cleared"] = true };
        }
        Check(a["text"]?.Type == JTokenType.String, L.T("Mcp.Err.ProvideTextOrClear"));
        var text = (string)a["text"]!;
        _state.Mutate(p => p.Lyrics = text, L.T("Mcp.Mutate.Lyrics"));
        _state.ReplanNow();
        ProjectIO.FlushAutosave(_state.Project);
        return new JObject
        {
            ["lines"] = _state.Plan?.Lines.Count ?? 0,
            ["cuts"] = _state.Plan?.Cuts.Count ?? 0
        };
    }

    private JObject ToolUpdateSettings(JObject a)
    {
        var s = a["settings"] as JObject;
        Check(s != null, L.T("Mcp.Err.MissingSettings"));
        var p = _state.Project;

        // 先校验后落地：校验失败不留半套改动（JS 是边改边 check，失败会留下半套——这里更严）
        if (s!["style"] != null)
            Check(Reg.Styles.ContainsKey(s.Value<string>("style") ?? ""), L.F("Mcp.Err.UnknownStyle", s["style"]));
        if (s["fonts"] is JObject fo)
            foreach (var role in new[] { "display", "serif", "body" })
                if (fo[role]?.Type == JTokenType.String)
                {
                    var f = (string)fo[role]!;
                    Check(f.Length == 0 || JFonts.Has(f), L.F("Mcp.Err.UnknownFont", f));
                }
        if (s["aspect"] != null)
            Check(Aspects.Contains(s.Value<string>("aspect") ?? ""), L.T("Mcp.Err.BadAspect"));
        // C# 差异：画质档位与导出范围尚未接入（Project 模型无 quality/exportRange，导出层不吃范围裁剪）
        Check(s["quality"] == null, L.T("Mcp.Err.QualityNotSupported"));
        Check(s["exportRange"] == null, L.T("Mcp.Err.ExportRangeNotSupported"));

        var applied = s.Properties().Select(x => x.Name).ToArray();
        _state.Mutate(proj =>
        {
            if (s["title"] != null) proj.Title = Truncate(s.Value<string>("title") ?? "", 1000);
            if (s["artist"] != null) proj.Artist = Truncate(s.Value<string>("artist") ?? "", 1000);
            if (s["lang"] != null) proj.Lang = s.Value<string>("lang") ?? proj.Lang;
            if (s["style"] != null) proj.Style = s.Value<string>("style")!;
            if (s["seed"] != null) proj.Seed = unchecked((uint)s.Value<int>("seed"));
            if (s["fx"] is JObject fx)
            {
                // 与 agent-api.ts 的 FX_KEYS 同集合：滑杆 7 项 + flash/hud（其余 fx 键不走 MCP）
                foreach (var key in new[]
                         {
                             "motion", "glitch", "chroma", "decor", "density", "texture", "bgSwitch", "flash", "hud"
                         })
                {
                    var v = fx[key];
                    if (v == null) continue;
                    if (v.Type is JTokenType.Float or JTokenType.Integer) Omakase.FxSet(proj.Fx, key, (double)v);
                    else if (v.Type == JTokenType.Boolean) Omakase.FxSet(proj.Fx, key, (bool)v);
                    else if (v.Type == JTokenType.String) Omakase.FxSet(proj.Fx, key, (string)v);
                }
            }
            if (s["fonts"] is JObject fonts)
                foreach (var role in new[] { "display", "serif", "body" })
                    if (fonts[role]?.Type == JTokenType.String)
                    {
                        var f = (string)fonts[role]!;
                        if (f.Length > 0) proj.Fonts[role] = f;
                        else proj.Fonts.Remove(role);
                    }
            foreach (var k in new[]
                     {
                         "extra", "wa", "horror", "typo", "kinetic", "unify", "typeset", "centerFree", "includeAudio"
                     })
                if (s[k]?.Type == JTokenType.Boolean)
                {
                    var v = (bool)s[k]!;
                    switch (k)
                    {
                        case "extra": proj.Extra = v; break;
                        case "wa": proj.Wa = v; break;
                        case "horror": proj.Horror = v; break;
                        case "typo": proj.Typo = v; break;
                        case "kinetic": proj.Kinetic = v; break;
                        case "unify": proj.Unify = v; break;
                        case "typeset": proj.Typeset = v; break;
                        case "centerFree": proj.CenterFree = v; break;
                    }
                    // includeAudio 已由导出勾选模型的 exportCfg.audio 接管：MCP 老问法保持可用，双向镜像
                    if (k == "includeAudio")
                    {
                        var c = ExportSpec.CfgOf(proj);
                        ExportSpec.SetCfg(proj, c with { Audio = v });
                    }
                }
            if (s["centerDir"] != null) proj.CenterDir = s.Value<string>("centerDir") ?? proj.CenterDir;
            if (s["keyBg"] != null)
            {
                var keyBg = s.Value<string>("keyBg") ?? proj.KeyBg;
                proj.KeyBg = keyBg;
                // keyBg 与导出勾选的 bgStyle 是同一件事（bgStyle 镜像 keyBg），MCP 设置也要同步
                var c = ExportSpec.CfgOf(proj);
                ExportSpec.SetCfg(proj, c with { BgStyle = keyBg });
            }
            if (s["aspect"] != null) proj.Aspect = s.Value<string>("aspect")!;
            if (s["res"] != null) proj.Res = s.Value<int>("res");
            if (s["fps"] != null) proj.Fps = s.Value<int>("fps");
        }, L.T("Mcp.Mutate.Settings"), look: true);
        _state.ReplanNow();
        ProjectIO.FlushAutosave(_state.Project);
        return new JObject { ["applied"] = new JArray(applied) };
    }

    private JObject ToolSetTiming(JObject a)
    {
        var plan = _state.Plan;
        Check(plan != null, L.T("Mcp.Err.EditorNotReady"));
        var st = a["settings"] as JObject;
        // times 先校验（原子：任一行不存在就整单拒绝，JS 是边改边抛）
        var times = (a["times"] as JArray)?.OfType<JObject>().ToArray() ?? Array.Empty<JObject>();
        foreach (var t in times) LineIdx(t.Value<int?>("line") ?? 0);

        _state.Mutate(p =>
        {
            if (st != null)
            {
                if (st["bpm"] != null) p.Timing.Bpm = st.Value<double>("bpm");
                if (st["offset"] != null) p.Timing.Offset = st.Value<double>("offset");
                if (st["lineScale"] != null) p.Timing.LineScale = st.Value<double>("lineScale");
                if (st["snap"]?.Type == JTokenType.Boolean) p.Timing.Snap = (bool)st["snap"]!;
            }
            foreach (var t in times)
            {
                var i = (t.Value<int?>("line") ?? 0) - 1;
                if (t["time"] == null || t["time"]!.Type == JTokenType.Null) p.Timing.LineTimes.Remove(i.ToString());
                else p.Timing.LineTimes[i.ToString()] = t.Value<double>("time");
            }
            if (a["clear"]?.Type == JTokenType.Boolean && (bool)a["clear"]!)
                p.Timing.LineTimes.Clear();
        }, L.T("Mcp.Mutate.LineTimes"));
        _state.ReplanNow();
        return new JObject
        {
            ["bpm"] = _state.Project.Timing.Bpm,
            ["manualLines"] = _state.Project.Timing.LineTimes.Count
        };
    }

    private JObject ToolEditLine(JObject a)
    {
        var lineNo = a.Value<int?>("line") ?? 0;
        var i = LineIdx(lineNo);
        var plan = _state.Plan!;
        var hasText = a["text"] != null;
        Check(!hasText || a["text"]!.Type == JTokenType.String, L.T("Mcp.Err.TextMustBeString"));
        if (a["layout"] != null)
        {
            var lay = a.Value<string>("layout") ?? "";
            Check(lay.Length == 0 || Reg.Layouts.ContainsKey(lay), L.F("Mcp.Err.UnknownLayout", lay));
        }
        if (a["cuts"] != null)
        {
            var cuts = a.Value<int>("cuts");
            Check(cuts is >= 0 and <= 6, L.T("Mcp.Err.CutsRange"));
        }

        var text = a.Value<string>("text");
        var layout = a.Value<string>("layout");
        var cutsV = a.Value<int?>("cuts");
        _state.Mutate(p =>
        {
            if (hasText)
            {
                var rows = p.Lyrics.Replace("\r", "").Split('\n').ToList();
                var src = plan.Lines[i].Src;
                if (src < 0 || src >= rows.Count) src = i;
                var row = src >= 0 && src < rows.Count ? rows[src] : "";
                if (rows.Count == 0) rows.Add("");
                var pre = Regex.Match(row, @"^\s*(?:\[\d+:\d+(?:[.:]\d+)?\])*").Value;
                rows[src] = pre + text;
                p.Lyrics = string.Join("\n", rows);
            }
            if (layout != null) SetOv(p, i, "layout", layout.Length == 0 ? null : layout);
            if (cutsV != null)
            {
                SetOv(p, i, "cuts", cutsV.Value == 0 ? null : cutsV.Value);
                SetOv(p, i, "single", null);
            }
        }, L.T("Mcp.Mutate.EditLine"));
        if (hasText || layout != null || cutsV != null)
            _state.ReplanNow(); // 锁快照必须来自已应用本次文本/布局/切分的新 plan，否则存下旧内容

        if (a["lock"]?.Type == JTokenType.Boolean)
        {
            var locked = (bool)a["lock"]!;
            if (locked)
            {
                var fresh = _state.Plan!;
                var seed = (long)fresh.Lines[i].Seed;
                var snap = Planner.LineSnapshot(fresh, i);
                _state.Mutate(p =>
                {
                    SetOv(p, i, "lock", true);
                    SetOv(p, i, "lockedSeed", seed);
                    if (snap != null) SetOv(p, i, "lockedCuts", snap);
                }, L.T("Mcp.Mutate.LockLine"), replan: false, look: true);
            }
            else
            {
                _state.Mutate(p =>
                {
                    SetOv(p, i, "lock", null);
                    SetOv(p, i, "lockedSeed", null);
                    SetOv(p, i, "lockedCuts", null);
                }, L.T("Mcp.Mutate.UnlockLine"), replan: false, look: true);
            }
        }
        _state.ReplanNow();
        ProjectIO.FlushAutosave(_state.Project);
        var applied = a.Properties()
            .Select(x => x.Name)
            .Where(k => k != "revision" && k != "line")
            .ToArray();
        return new JObject { ["line"] = lineNo, ["applied"] = new JArray(applied) };
    }

    private JObject ToolEditCut(JObject a)
    {
        var lineNo = a.Value<int?>("line") ?? 0;
        var i = LineIdx(lineNo);
        var group = a.Value<string>("group") ?? "";
        Check(Reg.GroupKeys.Contains(group), L.F("Mcp.Err.UnknownGroup", group));
        var plan = _state.Plan!;
        var cuts = plan.Cuts
            .Where(c => c.Line == i && Reg.Layouts.TryGetValue(c.Layout, out var ld) && ld != null && !ld.Special)
            .ToList();
        var cutNo = a.Value<int?>("cut") ?? 0;
        Check(cutNo >= 1 && cutNo <= cuts.Count, L.T("Mcp.Err.NoSuchCut"));
        // cutTech/cutQuiet 槽位按「行内第 N 个切分」（0 基）编址，与界面 CutCell.k、planner cutTechOf 同一套序号
        var k = cutNo - 1;
        Check(a["key"] != null || a["quiet"] != null, L.T("Mcp.Err.ProvideKeyOrQuiet"));
        string? key = null;
        if (a["key"] != null)
        {
            key = a.Value<string>("key") ?? "";
            Check(key.Length == 0 || RegDef(group, key) is { Special: false }, L.F("Mcp.Err.UnknownTech", group, key));
        }
        var quiet = a["quiet"]?.Type == JTokenType.Boolean ? (bool?)a["quiet"]! : null;

        _state.Mutate(p =>
        {
            if (key != null) SetCutTech(p, i, k, group, key);
            if (quiet != null) MarkCutQuiet(p, i, k, new[] { group }, quiet.Value);
        }, L.T("Mcp.Mutate.CutTech"), replan: false, look: true);
        _state.ReplanNow();
        return new JObject
        {
            ["cut"] = cutNo,
            ["line"] = lineNo,
            ["group"] = group,
            ["key"] = key != null ? key : "unchanged"
        };
    }

    private JObject ToolSetTechniques(JObject a)
    {
        var group = a.Value<string>("group") ?? "";
        Check(Reg.GroupKeys.Contains(group), L.F("Mcp.Err.UnknownGroup", group));
        var enabled = a["enabled"]?.Type == JTokenType.Boolean && (bool)a["enabled"]!;
        var keys = (a["keys"] as JArray)?.Select(x => (string?)x ?? "").ToArray()
                   ?? Reg.OrderOf(group).Where(k => RegDef(group, k) is { Special: false }).ToArray();
        foreach (var k in keys) Check(RegDef(group, k) != null, L.F("Mcp.Err.UnknownTech", group, k));

        _state.Mutate(p =>
        {
            if (!p.Enabled.TryGetValue(group, out var en))
            {
                en = new Dictionary<string, bool>();
                p.Enabled[group] = en;
            }
            foreach (var k in keys) en[k] = enabled;
            p.Mood = null; // 手动改开关后氛围标签不再成立
        }, L.T("Mcp.Mutate.TechToggle"));
        _state.ReplanNow();
        return new JObject { ["group"] = group, ["enabled"] = enabled, ["keys"] = keys.Length };
    }

    private JObject ToolSetLocks(JObject a)
    {
        var kind = a.Value<string>("kind") ?? "";
        Check(kind is "tech" or "params", L.F("Mcp.Err.UnknownLockKind", kind));
        var keys = (a["keys"] as JArray)?.Select(x => (string?)x ?? "").ToArray();
        Check(keys != null, L.T("Mcp.Err.MissingKeys"));
        var locked = a["locked"]?.Type == JTokenType.Boolean && (bool)a["locked"]!;
        foreach (var k in keys!)
        {
            // tech 锁的对象是手法组名（layout/enter/...）；params 锁的是 fx 参数名（koma/flash/fx 的键）
            if (kind == "tech") Check(Reg.GroupKeys.Contains(k), L.F("Mcp.Err.UnknownLockItem", k));
            else Check(Omakase.IsFxKey(k), L.F("Mcp.Err.UnknownLockItem", k));
        }
        _state.Mutate(p =>
        {
            var table = kind == "tech" ? p.Locks.Tech : p.Locks.Params;
            foreach (var k in keys) table[k] = locked;
        }, L.T("Mcp.Mutate.RandomLock"));
        // 即时 replan（不用后台防抖）：返回值携带的 revision 必须是真实当前值
        _state.ReplanNow();
        return new JObject { ["kind"] = kind, ["locked"] = new JArray(keys) };
    }

    private JObject ToolRandomize(JObject a)
    {
        var target = a.Value<string>("target") ?? "";
        var plan = _state.Plan!;
        if (target == "all")
        {
            var themeId = _state.Project.ThemeId;
            if (themeId != null && !Omakase.Themes.ContainsKey(themeId)) themeId = null;
            _preview.Pause(); // 与界面「一键随机」一致：暂停 + 回到片头（重规划期间不继续推进）
            _state.Mutate(p => Omakase.OmakaseApply(p, null, themeId), L.T("Mcp.Mutate.OmakaseAll"), replan: false, look: true);
            _state.ReplanNow();
            _preview.Seek(0);
            return new JObject { ["target"] = "all" };
        }
        if (target == "line")
        {
            Check(a["line"] != null, L.T("Mcp.Err.ProvideLine"));
            var lineNo = a.Value<int?>("line") ?? 0;
            var i = LineIdx(lineNo);
            Check(!plan.Lines[i].Interlude, L.T("Mcp.Err.InterludeNoReroll"));
            _preview.Seek(plan.Lines[i].Start + 0.001);
            RerollCurrentCut("omakase");
            return new JObject { ["target"] = "line", ["line"] = lineNo };
        }
        if (target == "cut")
        {
            Check(a["line"] != null && a["cut"] != null, L.T("Mcp.Err.ProvideLineAndCut"));
            var i = LineIdx(a.Value<int?>("line") ?? 0);
            var cuts = plan.Cuts
                .Where(c => c.Line == i && Reg.Layouts.TryGetValue(c.Layout, out var ld) && ld != null && !ld.Special)
                .ToList();
            var cutNo = a.Value<int?>("cut") ?? 0;
            Check(cutNo >= 1 && cutNo <= cuts.Count, L.T("Mcp.Err.NoSuchCut"));
            var c = cuts[cutNo - 1];
            _preview.Seek(c.Start + Math.Min(c.Dur * 0.5, c.InDur + 0.05));
            RerollCurrentCut(a.Value<string>("strategy") == "shuffle" ? "shuffle" : "omakase");
            return new JObject { ["target"] = "cut", ["line"] = a["line"]!.DeepClone(), ["cut"] = cutNo };
        }
        throw new ToolError("invalid_input", L.T("Mcp.Err.UnsupportedTarget"));
    }

    private JObject ToolHistory(JObject a)
    {
        var kind = a.Value<string>("kind") ?? "";
        var d = a.Value<string>("direction") == "back" ? -1 : 1;
        if (kind == "edit")
        {
            if (d < 0) _state.Undo();
            else _state.Redo();
        }
        else
        {
            if (d < 0) _state.UndoLook();
            else _state.RedoLook();
        }
        _state.ReplanNow();
        if (kind != "edit") _preview.Seek(0); // 对位 histGo 末尾的 restartPreview()
        return new JObject { ["kind"] = kind, ["moved"] = d };
    }

    private JObject ToolPreview(JObject a)
    {
        var action = a.Value<string>("action");
        if (action == "play") _preview.Play();
        if (action == "pause") _preview.Pause();
        if (a["time"] != null) _preview.Seek(a.Value<double>("time"));
        if (a["loop"] != null)
        {
            var loop = a.Value<string>("loop") ?? "";
            Check(loop is "all" or "off", L.T("Mcp.Err.LoopModesLimited"));
            _preview.Loop = loop == "all";
        }
        // 预览音量/消音（只动预览出声，导出不变；不写设置——MCP 是会话级控制）
        if (a["volume"] != null || a["muted"] != null)
            _preview.SetVolume(a["volume"]?.Value<double>(), a["muted"]?.Value<bool>());
        return new JObject
        {
            ["playing"] = _preview.IsPlaying,
            ["t"] = Round2(_preview.CurrentTime),
            ["loop"] = _preview.Loop ? "all" : "off",
            ["volume"] = Math.Round(_preview.Volume, 3),
            ["muted"] = _preview.Muted
        };
    }

    private JObject ToolTap(JObject a)
    {
        Check(App.MainInstance != null, L.T("Mcp.Err.WindowNotReady"));
        var action = a.Value<string>("action") ?? "";
        if (action == "start")
        {
            // startTap 收的是行索引（0 基），不是行开始的秒数
            var i = a["line"] != null && a.Value<int?>("line") is > 0
                ? LineIdx(a.Value<int>("line"))
                : 0;
            TapSession.Start(i);
        }
        else if (action == "record") TapSession.TapNow();
        else if (action == "back") TapSession.TapBack();
        else TapSession.Stop();
        return new JObject { ["active"] = TapSession.Active };
    }

    private JObject ToolProject(JObject a)
    {
        var action = a.Value<string>("action");
        if (action == "get") return new JObject { ["project"] = ProjectJson(_state.Project) };
        if (action == "save")
        {
            ProjectIO.FlushAutosave(_state.Project);
            return new JObject
            {
                ["saved"] = L.T("Mcp.Tool.project.SavedNote")
            };
        }
        throw new ToolError("invalid_input", L.T("Mcp.Err.ProjectActionsLimited"));
    }

    private JObject ToolGetExportStatus() =>
        new() { ["job"] = ExportJobJson() };

    private JObject ToolReset()
    {
        // 不可逆操作：绝不绕过用户确认。工具立即返回，确认框异步弹（MCP 请求不被模态框卡住）
        var app = Application.Current;
        app?.Dispatcher.BeginInvoke(new Action(() =>
        {
            var ok = MessageBox.Show(L.T("Mcp.Tool.reset_project.Confirm"), L.T("Mcp.Tool.reset_project.ConfirmTitle"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok == MessageBoxResult.OK)
            {
                _state.ResetAll();
                _state.ReplanNow();
            }
        }));
        return new JObject
        {
            ["status"] = "needs_user_action",
            ["nextAction"] = L.T("Mcp.Tool.reset_project.NextAction")
        };
    }

    // ============================================================ start_export / 任务状态

    private sealed class ExportJobState
    {
        public string Id = "";
        public string Kind = "";
        public string Status = "running";
        public string? Error;
    }

    private sealed class JobProgress : IProgress<(double Pct, string Stage)>
    {
        public double Pct;
        public string Stage = "准备…";
        public void Report((double Pct, string Stage) value)
        {
            Pct = value.Pct;
            Stage = value.Stage;
        }
    }

    private readonly object _exportLock = new();
    private ExportJobState? _exportJob;

    /// <summary>导出进行中（对位 S.exporting：只挡写操作，只读工具照常）。</summary>
    public bool Exporting
    {
        get
        {
            lock (_exportLock) return _exportJob is { Status: "running" };
        }
    }

    private JToken ExportJobJson()
    {
        lock (_exportLock)
        {
            if (_exportJob == null) return JValue.CreateNull();
            var o = new JObject
            {
                ["id"] = _exportJob.Id,
                ["kind"] = _exportJob.Kind,
                ["status"] = _exportJob.Status
            };
            if (_exportJob.Error != null) o["error"] = _exportJob.Error;
            return o;
        }
    }

    private JObject ToolStartExport(JObject a)
    {
        var kind = a.Value<string>("kind") ?? "";
        Check(LegacyKinds.Contains(kind),
            L.T("Mcp.Err.KindLimited"));
        var p = _state.Project;
        var plan = _state.Plan;
        Check(plan != null, L.T("Mcp.Err.EditorNotReady"));
        var spec = ExportSpec.SpecFromLegacyKind(kind, p)!;
        Check(!spec.CustomDrawOnly, L.T("Mcp.Err.SubwebmNotSupported"));
        // C# 无字幕系统：旧 kind 里 mp4/mp4file 的「烤字幕」在此一律关闭（否则导出层直接拒绝）
        spec = spec with { BakeSubs = false };

        var dir = a.Value<string>("dir");
        if (a["dir"] != null)
            Check(!string.IsNullOrWhiteSpace(dir), L.T("Mcp.Err.BadDir"));
        var outDir = dir != null && dir.Trim().Length > 0 ? dir.Trim() : DefaultExportDir();
        var outPath = Path.Combine(outDir, ExportFileName(kind, p));

        lock (_exportLock)
        {
            if (_exportJob is { Status: "running" }) throw new ToolError("invalid_input", L.T("Mcp.Err.ExportAlreadyRunning"));
            _exportJob = new ExportJobState
            {
                Id = "job-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = kind,
                Status = "running"
            };
        }

        // 快照：后台编码线程不读 UI 线程正在改的活对象（JS 版传的是活引用，这里更保守）
        var project = CloneProject(p);
        var audioPath = _state.AudioPath;
        var req = new ExportRequest
        {
            Project = project,
            Plan = plan!,
            Spec = spec,
            OutputPath = outPath,
            AudioPath = audioPath,
            WavToo = false
        };
        var job = _exportJob!;
        var progress = new JobProgress();
        if (App.MainInstance != null) App.MainInstance.ExportBusy = true; // 预览渲染泵让路
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(outDir);
                ExportJob.Run(req, progress, CancellationToken.None);
                lock (_exportLock) job.Status = "completed";
                Log.Info($"[Mcp] 导出完成：{outPath}");
            }
            catch (Exception ex)
            {
                lock (_exportLock)
                {
                    job.Status = "failed";
                    job.Error = ex.Message;
                }
                Log.Error("[Mcp] 导出失败：" + outPath, ex);
            }
            finally
            {
                if (App.MainInstance != null) App.MainInstance.ExportBusy = false;
            }
        });

        return new JObject { ["job"] = ExportJobJson() };
    }

    private static Project CloneProject(Project p) =>
        JsonConvert.DeserializeObject<Project>(ProjectIO.SerializeProject(p)) ?? p;

    /// <summary>未指定 dir 时的落点：视频目录下的 JIZURA（桌面版没有浏览器下载目录）。</summary>
    private static string DefaultExportDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "JIZURA");

    /// <summary>各 kind 的最终文件名（对位 export.ts 的 exportFileName + baseName）。</summary>
    private static string ExportFileName(string kind, Project p)
    {
        var spec = ExportSpec.SpecFromLegacyKind(kind, p)!;
        var suffix =
            spec.Kind is "mp4" or "mp4file" ? ".mp4"
            : spec.Kind == "webm" ? spec.CustomDrawOnly ? "_subtitle.webm"
                : spec.Layer == "front" ? "_front.webm"
                : spec.Transparent ? "_alpha.webm" : ".webm"
            : spec.CustomDrawOnly ? "_subtitle_png.zip"
            : spec.Layers ? "_layers_png.zip"
            : spec.Layer == "front" ? "_front_png.zip"
            : spec.Transparent ? "_alpha_png.zip" : "_png.zip";
        var title = (p.Title ?? "").Trim();
        var safe = Regex.Replace(title, @"[\\/:*?""<>|]+", "_");
        if (safe.Length > 60) safe = safe[..60];
        if (safe.Length == 0) safe = "jizura";
        var keyBg = p.KeyBg is "green" or "black" ? p.KeyBg : null;
        return safe + (keyBg == null ? "" : keyBg == "green" ? "_greenback" : "_blackback") + suffix;
    }

    // ============================================================ look.ts 的覆盖层/锁语义

    private void RerollCurrentCut(string kind)
    {
        var plan = _state.Plan!;
        var t0 = _preview.CurrentTime;
        var cut = Planner.CutAt(plan, t0);
        Check(cut != null, L.T("Mcp.Err.CutNotRerollable"));
        var k = LyricCutK(plan, cut!);
        Check(k >= 0, L.T("Mcp.Err.CutNotRerollable"));
        var n = CodePointCount(Regex.Replace(cut!.Text ?? "", @"\s+", ""));
        var groups = kind == "omakase"
            ? ChipGroups
            : new[] { "layout", "enter", "hold", "exit", "cam", "trans" };
        _state.Mutate(p =>
        {
            foreach (var g in groups)
            {
                var allowNone = g is "decor" or "trans";
                var avoid = kind == "omakase" ? CutGroupVal(cut, g) : null;
                var key = PickEnabledTech(p, g, n, allowNone, avoid);
                if (key.Length > 0) SetCutTech(p, cut.Line, k, g, key);
            }
            MarkCutQuiet(p, cut.Line, k, groups, true);
        }, kind == "omakase" ? L.T("Mcp.Mutate.CutOmakase") : L.T("Mcp.Mutate.CutReroll"), replan: false, look: true);
        _state.ReplanNow();
        _preview.Seek(t0);
    }

    /// <summary>行内第 k 个非 special 切分（对位 look.ts 的 lyricCutK）。</summary>
    private static int LyricCutK(Plan plan, Cut cut)
    {
        if (cut == null || cut.Line < 0) return -1;
        var k = 0;
        foreach (var c in plan.Cuts)
        {
            if (c.Line != cut.Line) continue;
            if (!Reg.Layouts.TryGetValue(c.Layout, out var ld) || ld == null || ld.Special) continue;
            if (c.Index == cut.Index) return k;
            k++;
        }
        return -1;
    }

    /// <summary>当前切分在某组上的既有手法（重抽时避免同款，对位 look.ts cutGroupVal）。</summary>
    private static string CutGroupVal(Cut cut, string g) => g switch
    {
        "layout" => cut.Layout ?? "",
        "enter" => cut.Enter ?? "",
        "hold" => cut.Hold ?? "",
        "exit" => cut.Exit ?? "",
        "treat" => cut.Treat ?? "none",
        "bg" => cut.Bg ?? "none",
        "cam" => cut.Cam ?? "push",
        "trans" => cut.Trans ?? "",
        "decor" => cut.Decor is { Count: > 0 } d ? d[0].Id : "",
        _ => ""
    };

    /// <summary>候选挑选（对位 look.ts pickEnabledTech）。</summary>
    private static string PickEnabledTech(Project p, string g, int? n, bool allowNone, string? avoid)
    {
        p.Enabled.TryGetValue(g, out var en);
        var order = Reg.OrderOf(g);
        bool On(string key) => en == null || !en.TryGetValue(key, out var v) || v;
        var keys = order.Where(key => RegDef(g, key) is { Special: false } && On(key) && Omakase.RandomOk(p, g, key)).ToList();
        if (keys.Count == 0) keys = order.Where(key => RegDef(g, key) is { Special: false } && On(key)).ToList();
        if (g == "layout" && n != null)
        {
            var fit = keys.Where(key => Reg.Layouts.TryGetValue(key, out var ld) && (ld.Fits == null || ld.Fits(n.Value))).ToList();
            if (fit.Count > 0) keys = fit;
        }
        if (allowNone && !keys.Contains("none")) keys.Insert(0, "none");
        if (!string.IsNullOrEmpty(avoid) && keys.Count > 1) keys = keys.Where(key => key != avoid).ToList();
        if (keys.Count == 0) return "";
        return keys[Random.Shared.Next(keys.Count)];
    }

    // ---- overrides（setOv / setCutTech / markCutQuiet 的逐行对位） ----

    private static JObject TakeOverride(Project p, int line) =>
        p.Overrides.TryGetValue(line.ToString(), out var o) && o != null ? new JObject(o) : new JObject();

    private static void PutOverride(Project p, int line, JObject ov)
    {
        if (ov.Count > 0) p.Overrides[line.ToString()] = ov;
        else p.Overrides.Remove(line.ToString());
    }

    private static JObject? OverrideAt(Project p, int line) =>
        p.Overrides.TryGetValue(line.ToString(), out var o) ? o : null;

    /// <summary>setOv：空对象删除，undefined/false/"" 删键（对位 look.ts）。</summary>
    private static void SetOv(Project p, int line, string key, JToken? value)
    {
        var cur = TakeOverride(p, line);
        var drop = value == null || value.Type == JTokenType.Null
                   || value.Type == JTokenType.Boolean && !(bool)value
                   || value.Type == JTokenType.String && (string)value! == "";
        if (drop) cur.Remove(key);
        else cur[key] = value!.DeepClone();
        PutOverride(p, line, cur);
    }

    private static void SetCutTech(Project p, int line, int k, string group, string key)
    {
        var cur = TakeOverride(p, line);
        var cutTech = cur["cutTech"] as JObject ?? new JObject();
        var slot = cutTech[k.ToString()] as JObject ?? new JObject();
        cutTech.Remove(k.ToString());
        if (key.Length == 0) slot.Remove(group);
        else slot[group] = key;
        if (slot.Count > 0) cutTech[k.ToString()] = slot;
        else cutTech.Remove(k.ToString());
        if (cutTech.Count > 0) cur["cutTech"] = cutTech;
        else cur.Remove("cutTech");

        var cutQuiet = cur["cutQuiet"] as JObject ?? new JObject();
        var q = cutQuiet[k.ToString()] as JObject ?? new JObject();
        cutQuiet.Remove(k.ToString());
        q.Remove(group);
        if (q.Count > 0) cutQuiet[k.ToString()] = q;
        else cutQuiet.Remove(k.ToString());
        if (cutQuiet.Count > 0) cur["cutQuiet"] = cutQuiet;
        else cur.Remove("cutQuiet");

        if (group == "layout")
        {
            var cutLayouts = cur["cutLayouts"] as JObject ?? new JObject();
            if (key.Length == 0) cutLayouts.Remove(k.ToString());
            else cutLayouts[k.ToString()] = key;
            if (cutLayouts.Count > 0) cur["cutLayouts"] = cutLayouts;
            else cur.Remove("cutLayouts");
        }
        PutOverride(p, line, cur);
    }

    private static void MarkCutQuiet(Project p, int line, int k, string[] groups, bool on)
    {
        var cur = TakeOverride(p, line);
        var cutQuiet = cur["cutQuiet"] as JObject ?? new JObject();
        var q = cutQuiet[k.ToString()] as JObject ?? new JObject();
        cutQuiet.Remove(k.ToString());
        foreach (var g in groups)
        {
            if (on) q[g] = true;
            else q.Remove(g);
        }
        if (q.Count > 0) cutQuiet[k.ToString()] = q;
        else cutQuiet.Remove(k.ToString());
        if (cutQuiet.Count > 0) cur["cutQuiet"] = cutQuiet;
        else cur.Remove("cutQuiet");
        PutOverride(p, line, cur);
    }

    // ============================================================ 工具定义（tools/list）

    /// <summary>工具清单：结构/文案对位 agent-api.ts 的 TOOLS（{{...}} 占位在载入时按注册表展开）。</summary>
    private static JArray? _toolList;

    private static JArray ToolList()
    {
        var list = _toolList;
        if (list == null)
        {
            var json = ToolsJson
                .Replace("{{STYLE_ORDER}}", JsonConvert.SerializeObject(JStyles.StyleOrder))
                .Replace("{{GROUPS}}", JsonConvert.SerializeObject(Reg.GroupKeys));
            list = (JArray)JObject.Parse(json)["tools"]!;
            _toolList = list;
        }
        // description 在使用点经 L.T 解析：静态缓存只存结构不缓存文案，换语言后下次 tools/list 生效
        foreach (var t in list)
            t["description"] = L.T("Mcp.Tool." + (string)t["name"]! + ".Desc");
        return list;
    }

    private const string ToolsJson = """
    {"tools":[
      {"name":"get_state","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"detail":{"type":"boolean"}},"required":[],"additionalProperties":false},"annotations":{"readOnlyHint":true}},
      {"name":"list_options","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"category":{"type":"string","enum":["style","font","layout","enter","hold","exit","decor","treat","bg","cam","fx","trans"]},"query":{"type":"string","maxLength":100},"page":{"type":"integer","minimum":1,"maximum":10000},"pageSize":{"type":"integer","minimum":1,"maximum":100}},"required":["category"],"additionalProperties":false},"annotations":{"readOnlyHint":true}},
      {"name":"set_lyrics","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"text":{"type":"string","maxLength":200000},"clear":{"type":"boolean"},"revision":{"type":"integer","minimum":0,"maximum":9007199254740991}},"required":[],"additionalProperties":false},"annotations":{"readOnlyHint":false}},
      {"name":"update_settings","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"revision":{"type":"integer","minimum":0,"maximum":9007199254740991},"settings":{"type":"object","properties":{"title":{"type":"string","maxLength":1000},"artist":{"type":"string","maxLength":1000},"lang":{"type":"string","enum":["auto","ja","en","zh-Hant","zh-Hans","ko"]},"style":{"type":"string","enum":{{STYLE_ORDER}}},"seed":{"type":"integer","minimum":-2147483648,"maximum":2147483647},"fx":{"type":"object","properties":{"motion":{"type":"number","minimum":0,"maximum":1},"glitch":{"type":"number","minimum":0,"maximum":1},"chroma":{"type":"number","minimum":0,"maximum":1},"decor":{"type":"number","minimum":0,"maximum":1},"density":{"type":"number","minimum":0,"maximum":1},"texture":{"type":"number","minimum":0,"maximum":1},"bgSwitch":{"type":"number","minimum":0,"maximum":1},"flash":{"type":"boolean"},"hud":{"type":"string","enum":["auto","on","off"]}}},"fonts":{"type":"object","properties":{"display":{"type":"string","maxLength":80},"serif":{"type":"string","maxLength":80},"body":{"type":"string","maxLength":80}}},"extra":{"type":"boolean"},"wa":{"type":"boolean"},"horror":{"type":"boolean"},"typo":{"type":"boolean"},"kinetic":{"type":"boolean"},"unify":{"type":"boolean"},"typeset":{"type":"boolean"},"centerFree":{"type":"boolean"},"includeAudio":{"type":"boolean"},"centerDir":{"type":"string","enum":["tb","lr"]},"keyBg":{"type":"string","enum":["off","green","black"]},"aspect":{"type":"string","enum":["16:9","9:16","4:3","3:4","1:1","4:5","21:9"]},"res":{"type":"integer","minimum":360,"maximum":2160},"fps":{"type":"integer","minimum":12,"maximum":120},"quality":{"type":"string","enum":["standard","high","max"]},"exportRange":{"type":["object","null"],"properties":{"from":{"type":"integer","minimum":1,"maximum":100000},"to":{"type":"integer","minimum":1,"maximum":100000}},"required":["from","to"]}}}},"required":["settings"],"additionalProperties":false},"annotations":{"readOnlyHint":false}},
      {"name":"set_timing","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"settings":{"type":"object","properties":{"bpm":{"type":"number","minimum":0,"maximum":300},"offset":{"type":"number","minimum":0,"maximum":86400},"lineScale":{"type":"number","minimum":0.3,"maximum":4},"snap":{"type":"boolean"}}},"times":{"type":"array","items":{"type":"object","properties":{"line":{"type":"integer","minimum":1,"maximum":100000},"time":{"type":["number","null"],"minimum":0,"maximum":86400}},"required":["line","time"],"additionalProperties":false}},"clear":{"type":"boolean"},"revision":{"type":"integer","minimum":0,"maximum":9007199254740991}},"required":["revision"],"additionalProperties":false},"annotations":{"readOnlyHint":false}},
      {"name":"edit_line","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"revision":{"type":"integer","minimum":0,"maximum":9007199254740991},"line":{"type":"integer","minimum":1,"maximum":100000},"text":{"type":"string","maxLength":10000},"layout":{"type":"string","maxLength":80},"cuts":{"type":"integer","minimum":0,"maximum":6},"lock":{"type":"boolean"}},"required":["revision","line"],"additionalProperties":false},"annotations":{"readOnlyHint":false}},
      {"name":"edit_cut","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"revision":{"type":"integer","minimum":0,"maximum":9007199254740991},"line":{"type":"integer","minimum":1,"maximum":100000},"cut":{"type":"integer","minimum":1,"maximum":1000},"group":{"type":"string","enum":["layout","enter","hold","exit","decor","treat","bg","cam","trans"]},"key":{"type":"string","maxLength":80},"quiet":{"type":"boolean"}},"required":["revision","line","cut","group"],"additionalProperties":false},"annotations":{"readOnlyHint":false}},
      {"name":"set_techniques","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"group":{"type":"string","enum":{{GROUPS}}},"keys":{"type":"array","items":{"type":"string","maxLength":80}},"enabled":{"type":"boolean"}},"required":["group","enabled"],"additionalProperties":false},"annotations":{"readOnlyHint":false}},
      {"name":"set_locks","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"kind":{"type":"string","enum":["tech","params"]},"keys":{"type":"array","items":{"type":"string","maxLength":40}},"locked":{"type":"boolean"}},"required":["kind","keys","locked"],"additionalProperties":false},"annotations":{"readOnlyHint":false}},
      {"name":"randomize","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"target":{"type":"string","enum":["all","line","cut"]},"revision":{"type":"integer","minimum":0,"maximum":9007199254740991},"line":{"type":"integer","minimum":1,"maximum":100000},"cut":{"type":"integer","minimum":1,"maximum":1000},"strategy":{"type":"string","enum":["omakase","shuffle"]}},"required":["target","revision"],"additionalProperties":false},"annotations":{"readOnlyHint":false}},
      {"name":"history","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"kind":{"type":"string","enum":["edit","look"]},"direction":{"type":"string","enum":["back","forward"]}},"required":["kind","direction"],"additionalProperties":false},"annotations":{"readOnlyHint":false}},
      {"name":"preview","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"action":{"type":"string","enum":["play","pause"]},"time":{"type":"number","minimum":0,"maximum":86400},"loop":{"type":"string","enum":["all","line","cut","off"]},"volume":{"type":"number","minimum":0,"maximum":1},"muted":{"type":"boolean"}},"required":["action"],"additionalProperties":false},"annotations":{"readOnlyHint":false}},
      {"name":"tap_sync","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"action":{"type":"string","enum":["start","record","back","stop"]},"line":{"type":"integer","minimum":1,"maximum":100000},"revision":{"type":"integer","minimum":0,"maximum":9007199254740991}},"required":["action"],"additionalProperties":false},"annotations":{"readOnlyHint":false}},
      {"name":"project","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"action":{"type":"string","enum":["get","save"]}},"required":["action"],"additionalProperties":false},"annotations":{"readOnlyHint":false}},
      {"name":"start_export","description":"{{DESC}}","inputSchema":{"type":"object","properties":{"kind":{"type":"string","enum":["mp4","png","pnga","pngl","pngf","webm","webmfront","subwebm"]},"dir":{"type":"string","maxLength":500}},"required":["kind"],"additionalProperties":false},"annotations":{"readOnlyHint":false}},
      {"name":"get_export_status","description":"{{DESC}}","inputSchema":{"type":"object","properties":{},"required":[],"additionalProperties":false},"annotations":{"readOnlyHint":true}},
      {"name":"reset_project","description":"{{DESC}}","inputSchema":{"type":"object","properties":{},"required":[],"additionalProperties":false},"annotations":{"readOnlyHint":false}}
    ]}
    """;

    // ============================================================ 小工具

    private sealed class ToolError : Exception
    {
        public readonly string Code;
        public ToolError(string code, string message) : base(message) => Code = code;
    }

    private static void Fail(string code, string message) => throw new ToolError(code, message);

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new ToolError("invalid_input", message);
    }

    private int LineIdx(int n)
    {
        var plan = _state.Plan;
        Check(plan != null && n >= 1 && n <= plan.Lines.Count, L.F("Mcp.Err.UnknownLine", n));
        return n - 1;
    }

    private static bool Truthy(JToken? t) => t != null && t.Type switch
    {
        JTokenType.Boolean => (bool)t,
        JTokenType.Integer or JTokenType.Float => (double)t != 0,
        _ => false
    };

    private static IRegEntry? RegDef(string g, string k) => g switch
    {
        "layout" => Reg.Layouts.GetValueOrDefault(k),
        "enter" => Reg.Enter.GetValueOrDefault(k),
        "hold" => Reg.Hold.GetValueOrDefault(k),
        "exit" => Reg.Exit.GetValueOrDefault(k),
        "decor" => Reg.Decor.GetValueOrDefault(k),
        "treat" => Reg.Treat.GetValueOrDefault(k),
        "bg" => Reg.Bg.GetValueOrDefault(k),
        "cam" => Reg.Camera.GetValueOrDefault(k),
        "fx" => Reg.Fxe.GetValueOrDefault(k),
        "trans" => Reg.Trans.GetValueOrDefault(k),
        _ => null
    };

    /// <summary>工程 → camelCase JSON（.jizuralive / MCP 的同一份契约，见 ProjectIO）。</summary>
    private static JObject ProjectJson(Project p) => JObject.Parse(ProjectIO.SerializeProject(p));

    private static double Round3(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);

    private static double Round2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    /// <summary>码点数（JS 的 [...str].length 语义）。</summary>
    private static int CodePointCount(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++, n++)
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
        return n;
    }

    /// <summary>UI 线程分发（无 Application 的测试环境直接跑）。</summary>
    private static T OnUi<T>(Func<T> f)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) return f();
        return d.Invoke(f);
    }
}
