using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using JizuraLive.App;
using JizuraLive.App.I18n;
using JizuraLive.App.Mcp;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// EditorState 的一切改动（含 MCP 写工具）经 ProjectIO 落到固定路径的 autosave：
/// 测试跑前备份、跑完还原，别把测试工程写进用户工程。
/// （放在命名空间层：基类列表里不能引用自己的嵌套类型）
/// </summary>
[Collection("EngineRegistry")]
public sealed class AutosaveGuard : IDisposable
{
    private readonly string _path = Path.Combine(AppDirs.ConfigDir, "autosave.json");
    private readonly byte[]? _original;

    public AutosaveGuard()
    {
        try
        {
            _original = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
        }
        catch
        {
            _original = null;
        }
    }

    public void Dispose()
    {
        // EditorState 的存盘是 700ms 防抖：等挂起的定时器落地后再还原，否则会被它覆盖
        Thread.Sleep(900);
        try
        {
            if (_original != null) File.WriteAllBytes(_path, _original);
        }
        catch
        {
            // 还原失败（文件被占用等）不阻塞测试
        }
    }
}

/// <summary>
/// M6：MCP 端点（HttpListener）与 17 工具直调的进程内集成测试。
/// 起真端点（随机空闲端口）→ 走 HTTP → 断言 JSON-RPC 结构；工具调 get_state 验证直读 EditorState。
/// 不依赖运行中的应用窗口：EditorState/PreviewEngine 在本进程内构造（无 Application/Dispatcher 时同步执行）。
/// </summary>
[Collection("EngineRegistry")] // Install 全量包 + PreviewEngine 构造（NewDefault 枚举注册表）→ 与对拍夹具同 collection 串行
public class McpTests : IDisposable, IClassFixture<AutosaveGuard>
{
    private readonly McpHttpServer _server;
    private readonly HttpClient _client;
    private readonly PreviewEngine _preview;
    private readonly EditorState _state;

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public McpTests()
    {
        L.Initialize(null);
        EngineBoot.Install();
        Omakase.Install();
        _preview = new PreviewEngine();
        _state = new EditorState(_preview);
        // EditorState 会装载用户的 autosave：换回确定性默认工程，断言才不依赖本机工程
        _state.ReplaceProject(Project.NewDefault(), "测试工程");
        var tools = new McpTools(_state, _preview);
        McpHttpServer? server = null;
        for (var i = 0; i < 5 && server == null; i++)
            server = McpHttpServer.StartOn(FreePort(), tools);
        _server = server ?? throw new InvalidOperationException("MCP 测试端点起不来（HttpListener 端口全被占）");
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
        // 不 Dispose EditorState：它的 Dispose 会把测试工程写进用户 autosave
    }

    private async Task<(HttpStatusCode Code, string Body)> PostAsync(string json,
        string contentType = "application/json")
    {
        using var content = new StringContent(json, Encoding.UTF8, contentType);
        var res = await _client.PostAsync("/mcp", content);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task tools_list_返回18个工具与JSON_RPC结构()
    {
        var (code, body) = await PostAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.False(body.StartsWith('\uFEFF'), "响应不能带 BOM");

        var res = JObject.Parse(body);
        Assert.Equal("2.0", (string?)res["jsonrpc"]);
        Assert.Equal(1, (int?)res["id"]);
        var tools = (JArray?)res["result"]?["tools"];
        Assert.NotNull(tools);
        Assert.Equal(18, tools!.Count);

        var names = tools.Select(t => (string?)t["name"]).ToArray();
        Assert.Equal(new[]
        {
            "get_state", "get_frame", "list_options", "set_lyrics", "update_settings", "set_timing", "edit_line",
            "edit_cut", "set_techniques", "set_locks", "randomize", "history", "preview", "tap_sync",
            "project", "start_export", "get_export_status", "reset_project"
        }, names);

        // 每个工具都要有 description 与 inputSchema；写操作带 revision 契约声明
        foreach (var t in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)t["description"]));
            Assert.NotNull(t["inputSchema"]);
            Assert.NotNull(t["annotations"]?["readOnlyHint"]);
        }
        var setLyrics = tools.First(t => (string?)t["name"] == "set_lyrics");
        Assert.NotNull(setLyrics["inputSchema"]?["properties"]?["revision"]);
    }

    [Fact]
    public async Task tools_call_get_state_直读编辑器状态()
    {
        var (code, body) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"get_state\",\"arguments\":{\"detail\":false}}}");
        Assert.Equal(HttpStatusCode.OK, code);
        var res = JObject.Parse(body);
        Assert.Equal(2, (int?)res["id"]);

        var call = res["result"]!;
        Assert.False((bool?)call["isError"]);
        var text = (string?)call["content"]?[0]?["text"];
        Assert.False(string.IsNullOrEmpty(text));
        var state = JObject.Parse(text!);
        Assert.Equal("all", (string?)state["loop"]);
        Assert.NotNull(state["playing"]);
        Assert.True(state["t"]!.Type is JTokenType.Float or JTokenType.Integer);
        Assert.True((double?)state["duration"] >= 0);
        Assert.NotNull(state["loading"]?["audio"]);
        Assert.NotNull(state["history"]?["canBack"]);
        Assert.Equal(_state.Revision, (int?)state["revision"]);
        // detail=false：轻量状态不带工程/行/切分
        Assert.Null(state["project"]);
        Assert.Null(state["lines"]);
    }

    [Fact]
    public async Task tools_call_get_frame_渲出当前帧并落盘()
    {
        var (code, body) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_frame\",\"arguments\":{\"t\":1.0,\"width\":320}}}");
        Assert.Equal(HttpStatusCode.OK, code);
        var res = JObject.Parse(body);
        var call = res["result"]!;
        Assert.False((bool?)call["isError"]);
        var outObj = JObject.Parse((string?)call["content"]?[0]?["text"] ?? "{}");
        Assert.True((bool?)outObj["ok"]);
        Assert.Equal(320, (int?)outObj["w"]);
        var path = (string?)outObj["path"];
        Assert.False(string.IsNullOrEmpty(path));
        Assert.True(File.Exists(path), "get_frame 没有落盘 PNG");
        var bytes = File.ReadAllBytes(path!);
        Assert.True(bytes.Length > 1000, $"PNG 过小：{bytes.Length} 字节");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4).ToArray()); // PNG 魔数
        Assert.True((int?)outObj["h"] > 0);
    }

    [Fact]
    public async Task tools_call_get_state_详情含工程与行()
    {
        _state.Mutate(p => p.Lyrics = "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った", "歌词");
        _state.ReplanNow();

        var (code, body) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"get_state\",\"arguments\":{}}}");
        Assert.Equal(HttpStatusCode.OK, code);
        var state = JObject.Parse((string?)JObject.Parse(body)["result"]!["content"]?[0]?["text"] ?? "{}");
        Assert.NotNull(state["project"]);
        Assert.Equal("noir", (string?)state["project"]!["style"]);
        var lines = (JArray?)state["lines"];
        Assert.NotNull(lines);
        Assert.Equal(2, lines!.Count);
        Assert.Equal(1, (int?)lines[0]!["line"]);
        Assert.Empty(state["project"]!["overrides"]!.ToObject<Dictionary<string, JObject>>()!);
    }

    [Fact]
    public async Task tools_call_preview_音量与消音落到引擎()
    {
        var (code, body) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"preview\",\"arguments\":{\"action\":\"play\",\"volume\":0.35,\"muted\":true}}}");
        Assert.Equal(HttpStatusCode.OK, code);
        var outText = (string?)JObject.Parse(body)["result"]?["content"]?[0]?["text"];
        var r = JObject.Parse(outText!);
        Assert.Equal(0.35, (double?)r["volume"]);
        Assert.True((bool?)r["muted"]);
        Assert.Equal(0.35, _preview.Volume, 3);
        Assert.True(_preview.Muted);

        // 只传 muted（volume 缺省不动）
        await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"preview\",\"arguments\":{\"action\":\"pause\",\"muted\":false}}}");
        Assert.Equal(0.35, _preview.Volume, 3);
        Assert.False(_preview.Muted);
    }

    /// <summary>
    /// 回归：换到分段循环档时 SetLoop → RefreshLoopHold → loopRange 取的是 CurrentTime，
    /// 而 CurrentTime 又回调 ActiveLoopRange——_loopHold 尚未赋值就互相递归，点一次「循环」
    /// 直接栈溢出（0xc00000fd）。取范围改用 RawTime（对位 JS 的 S.t）后这条链必须走得通。
    /// 用 all→line 起手，正是崩的那一步（_loopHold 为 null）。
    /// </summary>
    [Fact]
    public async Task tools_call_preview_换分段循环档不递归崩溃()
    {
        var id = 20;
        foreach (var mode in new[] { "line", "cut", "range", "off", "all", "line" })
        {
            var (code, body) = await PostAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":" + id++ +
                ",\"method\":\"tools/call\",\"params\":{\"name\":\"preview\",\"arguments\":{\"action\":\"pause\",\"loop\":\"" +
                mode + "\"}}}");
            Assert.Equal(HttpStatusCode.OK, code);
            var r = JObject.Parse((string?)JObject.Parse(body)["result"]?["content"]?[0]?["text"]!);
            Assert.Equal(mode, (string?)r["loop"]);
            Assert.Equal(Ranges.ParseMode(mode), _preview.Loop);
        }
    }

    [Fact]
    public async Task 写工具_revision过期返回stale_revision()
    {
        var before = _state.Project.Lyrics;
        var (_, body) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"set_lyrics\",\"arguments\":{\"text\":\"x\",\"revision\":999999}}}");
        var res = JObject.Parse(body);
        Assert.Equal(-32602, (int?)res["error"]?["code"]);
        Assert.Contains("revision 过期", (string?)res["error"]?["message"]);
        Assert.Equal(before, _state.Project.Lyrics); // 被拒的写没有落盘
    }

    [Fact]
    public async Task 写工具_带最新revision成功并回新revision()
    {
        var rev = _state.Revision;
        var (_, body) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"set_lyrics\",\"arguments\":{\"text\":\"*測試* 行一/行二\"}}}");
        var textOut = (string?)JObject.Parse(body)["result"]?["content"]?[0]?["text"];
        var revNo = textOut == null ? null : (int?)JObject.Parse(textOut)["revision"];
        Assert.True(revNo > rev, "写操作应返回递增后的 revision");
        Assert.Contains("測試", _state.Project.Lyrics);
    }

    [Fact]
    public async Task 批量请求被拒_32600()
    {
        var (code, body) = await PostAsync("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}]");
        Assert.Equal(HttpStatusCode.OK, code);
        var res = JObject.Parse(body);
        Assert.Equal(-32600, (int?)res["error"]?["code"]);
        Assert.Null(res["result"]);
    }

    [Fact]
    public async Task 非法JSON_32700()
    {
        var (code, body) = await PostAsync("{\"jsonrpc\":");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(-32700, (int?)JObject.Parse(body)["error"]?["code"]);
    }

    [Fact]
    public async Task 通知返回202空体()
    {
        using var content = new StringContent("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            Encoding.UTF8, "application/json");
        var res = await _client.PostAsync("/mcp", content);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal("", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 非POST返回405且Allow_POST()
    {
        var res = await _client.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        Assert.Contains("POST", res.Content.Headers.Allow);
    }

    [Fact]
    public async Task 跨站Origin被拦403()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8,
                "application/json")
        };
        req.Headers.Add("Origin", "https://evil.example");
        req.Headers.Add("Sec-Fetch-Site", "cross-site");
        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task randomize_all_改风格与氛围并保留锁定()
    {
        _state.Mutate(p => p.Locks.Tech["layout"] = true, "锁定布局组");
        _state.ReplanNow();
        var revBefore = _state.Revision;
        var styleBefore = _state.Project.Style;
        var lockedBefore = string.Join(",", _state.Project.Enabled["layout"].OrderBy(kv => kv.Key)
            .Select(kv => kv.Key + "=" + kv.Value));

        var (code, body) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"randomize\",\"arguments\":{\"target\":\"all\",\"revision\":" + revBefore + "}}}");
        Assert.Equal(HttpStatusCode.OK, code);
        var call = JObject.Parse(body)["result"]!;
        Assert.False((bool?)call["isError"]);
        var outObj = JObject.Parse((string?)call["content"]?[0]?["text"] ?? "{}");
        Assert.Equal("all", (string?)outObj["target"]);
        Assert.True((int?)outObj["revision"] > revBefore, "一键随机应递增 revision");

        // 氛围与风格都换新（JS 的候选池显式排除当前值）
        Assert.Contains(_state.Project.Mood, Omakase.MoodOrder);
        Assert.NotEqual(styleBefore, _state.Project.Style);
        Assert.True(_state.Project.Seed != 0);
        // tech 锁定的 layout 组开关原样保留，未锁的组被重掷
        var lockedAfter = string.Join(",", _state.Project.Enabled["layout"].OrderBy(kv => kv.Key)
            .Select(kv => kv.Key + "=" + kv.Value));
        Assert.Equal(lockedBefore, lockedAfter);
        Assert.NotNull(_state.Project.Enabled["enter"]);
    }

    [Fact]
    public async Task list_options_风格与手法组()
    {
        var (_, body) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\",\"params\":{\"name\":\"list_options\",\"arguments\":{\"category\":\"style\"}}}");
        var textOut = (string?)JObject.Parse(body)["result"]?["content"]?[0]?["text"];
        var items = textOut == null ? null : (JArray?)JObject.Parse(textOut)["items"];

        Assert.NotNull(items);
        Assert.True(items!.Count >= 12);
        Assert.True(items.Any(x => (string?)x["id"] == "noir"), "风格列表里应有 noir");
        foreach (var it in items)
        {
            Assert.False(string.IsNullOrEmpty((string?)it["id"]));
            Assert.False(string.IsNullOrEmpty((string?)it["label"]));
        }

        var (_, body2) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/call\",\"params\":{\"name\":\"list_options\",\"arguments\":{\"category\":\"layout\",\"query\":\"center\"}}}");
        var page = JObject.Parse((string?)JObject.Parse(body2)["result"]?["content"]?[0]?["text"] ?? "{}");
        var layouts = (JArray?)page["items"];
        Assert.NotNull(layouts);
        Assert.True(layouts!.Count > 0);
        Assert.True(layouts.Any(x => (string?)x["id"] == "center"), "查询 center 应命中 center 版式");
        Assert.NotNull(layouts[0]!["eligibleForRandom"]);

        // 未知分类 → invalid_input
        var (_, body3) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"tools/call\",\"params\":{\"name\":\"list_options\",\"arguments\":{\"category\":\"nope\"}}}");
        Assert.Equal(-32602, (int?)JObject.Parse(body3)["error"]?["code"]);
    }

    [Fact]
    public async Task project_get_返回camelCase工程()
    {
        var (_, body) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"tools/call\",\"params\":{\"name\":\"project\",\"arguments\":{\"action\":\"get\"}}}");
        var proj = (JObject?)JObject.Parse((string?)JObject.Parse(body)["result"]?["content"]?[0]?["text"] ?? "{}")["project"];
        Assert.NotNull(proj);
        Assert.NotNull(proj!["lyrics"]);
        Assert.NotNull(proj["timing"]);
        Assert.NotNull(proj["overrides"]);
        Assert.NotNull(proj["enabled"]);
    }

    [Fact]
    public async Task set_locks_未知锁定项被拒()
    {
        var (_, body) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":14,\"method\":\"tools/call\",\"params\":{\"name\":\"set_locks\",\"arguments\":{\"kind\":\"params\",\"keys\":[\"nope\"],\"locked\":true}}}");
        Assert.Equal(-32602, (int?)JObject.Parse(body)["error"]?["code"]);
        Assert.Contains("未知锁定项", (string?)JObject.Parse(body)["error"]?["message"]);
    }

    [Fact]
    public void 氛围标签表_一键随机的prefer集合命中()
    {
        // 主题 pop 只含 pop 氛围 → mood 确定；rnd 恒 0.9 → 非偏好条目一律撒不中，
        // 于是 Enabled 里留下的正是「08b 载入时补的 mood 标签」命中的集合（对位 J.taggedWith）
        var p = Project.NewDefault();
        p.Mood = null;
        var r = Omakase.Randomize(p, () => 0.9, "pop");
        Assert.Equal("pop", r.Mood);
        foreach (var k in new[] { "dots", "arrows", "sparks", "blobs", "shapes" })
            Assert.True(r.Enabled["decor"].GetValueOrDefault(k), "decor." + k + " 应命中 pop 偏好");
        foreach (var k in new[] { "chroma", "shake", "flash", "zoom" })
            Assert.True(r.Enabled["fx"].GetValueOrDefault(k), "fx." + k + " 应命中 pop 偏好");
        Assert.True(r.Enabled["hold"].GetValueOrDefault("wave"), "hold.wave 应命中 pop 偏好");
        // 套装门禁：default 工程里 horror 关着 → ホラー氛围不参与
        Assert.False(Omakase.RandomOk(p, "style", "sakura") || !Omakase.IsWa("style", "sakura"));
    }

    [Fact]
    public async Task initialize回serverInfo()
    {
        var (_, body) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}");
        var res = JObject.Parse(body);
        Assert.Equal("jizura-live", (string?)res["result"]?["serverInfo"]?["name"]);
        Assert.Equal("2025-06-18", (string?)res["result"]?["protocolVersion"]);
        Assert.True((bool?)res["result"]?["capabilities"]?["tools"]?["listChanged"]);
    }

    [Fact]
    public async Task start_export_subwebm_无字幕数据报错()
    {
        _state.SetSubtitles(null);
        var (_, body) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":15,\"method\":\"tools/call\",\"params\":{\"name\":\"start_export\",\"arguments\":{\"kind\":\"subwebm\"}}}");
        var err = JObject.Parse(body)["error"];
        Assert.NotNull(err);
        Assert.Equal(-32602, (int?)err!["code"]);
        Assert.Contains("subwebm", (string?)err["message"]);
    }

    [Fact]
    public async Task start_export_subwebm_有字幕数据接受启动()
    {
        _state.SetSubtitles(new List<JizuraLive.App.Cloud.KrcLine>
        {
            new(0, 1000, "测试", new[] { new JizuraLive.App.Cloud.KrcWord(0, 1000, "测试") })
        });
        var tempDir = Path.Combine(Path.GetTempPath(), $"mcp-export-{Guid.NewGuid():N}");
        var (_, body) = await PostAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":16,\"method\":\"tools/call\",\"params\":{\"name\":\"start_export\",\"arguments\":{\"kind\":\"subwebm\",\"dir\":\""
            + tempDir.Replace("\\", "\\\\") + "\"}}}");
        var res = JObject.Parse(body)["result"];
        Assert.NotNull(res);
        var text = (string?)res!["content"]?[0]?["text"];
        Assert.NotNull(text);
        var job = JObject.Parse(text!)["job"];
        Assert.NotNull(job);
        Assert.Equal("subwebm", (string?)job!["kind"]);
        Assert.Equal("running", (string?)job["status"]);
    }
}
