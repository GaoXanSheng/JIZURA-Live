using System.Drawing;
using System.Globalization;
using System.Text.Json;
using CefSharp;
using CefSharp.Handler;

namespace JizuraLive;

public sealed class SpoutPipeline : IDisposable
{
    public static SpoutPipeline Instance { get; } = new();

    public D3DChromiumWebBrowser? _browser;
    private MirrorRenderHandler? _renderHandler;

    private string _mirrorScript = MirrorScript;

    public bool IsRunning { get; private set; }

    public string? SenderName { get; private set; }

    public Action? SyncPoke { get; set; }

    public bool Start(string url, string senderName, int fps, int initialW, int initialH)
    {
        if (IsRunning)
            return true;

        try
        {
            SenderName = senderName;

            int safeFps = Math.Clamp(fps, 1, 240);
            int safeW = Math.Max(64, initialW);
            int safeH = Math.Max(64, initialH);

            var browserSettings = new BrowserSettings
            {
                WindowlessFrameRate = safeFps
            };

            _mirrorScript = MirrorScript.Replace("__FPS__", safeFps.ToString(CultureInfo.InvariantCulture));

            // ------------------------------------------------------------
            // 重要：
            // RenderHandler 必须在 CreateBrowser() 之前准备好。
            // ------------------------------------------------------------
            _renderHandler = new MirrorRenderHandler(this);

            _browser = new D3DChromiumWebBrowser(
                url,
                browserSettings);

            // RenderHandler 必须先设置
            _browser.RenderHandler = _renderHandler;
            _browser.AudioHandler = new CAudioHandler();
            // JS / 页面事件
            _browser.FrameLoadEnd += OnFrameLoadEnd;
            _browser.JavascriptMessageReceived += OnMirrorMessage;

            _browser.LoadError += OnMirrorLoadError;

            _browser.ConsoleMessage += OnMirrorConsoleMessage;

            // 镜像只出画面：与主窗口共享存储后会真正加载歌曲，
            // 同步播放时不静音会出现双重音频
            _browser.BrowserInitialized += OnMirrorBrowserInitialized;

            // 尺寸先设置好
            _browser.Size = new Size(safeW, safeH);

            // ------------------------------------------------------------
            // 最后才真正 CreateBrowser()
            // ------------------------------------------------------------
            _browser.CreateD3DBrowser();

            _ = WaitInitialLoadAsync(_browser);

            IsRunning = true;

            Log.Info(
                $"Spout 管线已启动：" +
                $"url={url}, " +
                $"通道={senderName}, " +
                $"fps={safeFps}, " +
                $"初始视口={safeW}x{safeH}");

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(
                "[SpoutMirror] Spout 管线启动失败",
                ex);

            CleanupLocked();

            return false;
        }
    }

    private static async Task WaitInitialLoadAsync(
        D3DChromiumWebBrowser browser)
    {
        try
        {
            await browser.WaitForInitialLoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error(
                "[SpoutMirror] 页面初始加载失败",
                ex);
        }
    }

    private void OnMirrorBrowserInitialized(
        object? sender,
        EventArgs e)
    {
        try
        {
            _browser?.GetBrowser()?.GetHost()?.SetAudioMuted(true);

            Log.Info("[SpoutMirror] 镜像浏览器已静音");
        }
        catch (Exception ex)
        {
            Log.Warn(
                "[SpoutMirror] 镜像静音失败：" +
                ex.Message);
        }
    }

    private void OnMirrorLoadError(
        object? sender,
        LoadErrorEventArgs e)
    {
        Log.Error(
            $"[SpoutMirror] 页面加载失败：" +
            $"{e.ErrorCode}, " +
            $"{e.ErrorText}, " +
            $"{e.FailedUrl}");
    }

    private void OnMirrorConsoleMessage(
        object? sender,
        ConsoleMessageEventArgs e)
    {
        Log.Info(
            $"[SpoutMirror][console] " +
            $"Level={e.Level}, " +
            $"Line={e.Line}, " +
            $"Source={e.Source}, " +
            $"Message={e.Message}");
    }

    private void OnFrameLoadEnd(
        object? sender,
        FrameLoadEndEventArgs e)
    {
        if (!e.Frame.IsMain)
            return;

        Log.Info(
            $"[SpoutMirror] 页面加载完成：" +
            $"{e.Url}，注入镜像脚本。");

        try
        {
            e.Frame.ExecuteJavaScriptAsync(
                _mirrorScript);
        }
        catch (Exception ex)
        {
            Log.Error(
                "[SpoutMirror] 注入镜像脚本失败",
                ex);
        }

        // 页面脚本需要一点时间完成初始化
        var poke = SyncPoke;

        if (poke != null)
        {
            _ = Task.Delay(1000).ContinueWith(
                _ =>
                {
                    try
                    {
                        poke();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(
                            "[SpoutMirror] SyncPoke 调用失败：" +
                            ex.Message);
                    }
                });
        }
    }

    private void OnMirrorMessage(
        object? sender,
        JavascriptMessageReceivedEventArgs e)
    {
        try
        {
            Log.Info(
                $"[SpoutMirror] JS => C#：{e.Message}");

            if (e.Message is not IDictionary<string, object> dict)
                return;

            if (!dict.TryGetValue("type", out var typeObj))
                return;

            string? type = typeObj?.ToString();

            if (string.Equals(
                type,
                "mirror-perf",
                StringComparison.OrdinalIgnoreCase))
            {
                HandleMirrorPerf(dict);
                return;
            }

            if (string.Equals(
                type,
                "plan-size",
                StringComparison.OrdinalIgnoreCase))
            {
                HandlePlanSize(dict);
                return;
            }

            // ------------------------------------------------------------
            // 注意：
            // sync_play 是主窗口浏览器发过来的，
            // 主窗口自己的 MainWindow 会处理。
            //
            // SpoutPipeline 这里只负责镜像端产生的消息。
            // 所以这里不处理 sync_play。
            // ------------------------------------------------------------
        }
        catch (Exception ex)
        {
            Log.Warn(
                "[SpoutMirror] JavascriptMessageReceived 处理失败：" +
                ex.Message);
        }
    }

    private void HandleMirrorPerf(
        IDictionary<string, object> dict)
    {
        try
        {
            Log.Info(
                $"[SpoutMirror] 绘制节奏：rAF间隔 " +
                $"avg={D(dict, "gapAvg"):F1}ms, " +
                $"max={D(dict, "gapMax"):F1}ms；" +
                $"单帧绘制 avg={D(dict, "drawAvg"):F1}ms, " +
                $"max={D(dict, "drawMax"):F1}ms " +
                $"({D(dict, "drawN"):0}帧)");
        }
        catch (Exception ex)
        {
            Log.Warn(
                "[SpoutMirror] mirror-perf 处理失败：" +
                ex.Message);
        }
    }

    private static double D(
        IDictionary<string, object> dict,
        string key)
    {
        return dict.TryGetValue(key, out var v) &&
            v != null
                ? Convert.ToDouble(v)
                : 0;
    }

    private void HandlePlanSize(
        IDictionary<string, object> dict)
    {
        try
        {
            if (!dict.TryGetValue("w", out var wObj))
                return;

            if (!dict.TryGetValue("h", out var hObj))
                return;

            int w = Convert.ToInt32(wObj);
            int h = Convert.ToInt32(hObj);

            if (w <= 0 || h <= 0)
                return;

            var browser = _browser;

            if (browser == null)
                return;

            var oldSize = browser.Size;

            if (oldSize.Width == w &&
                oldSize.Height == h)
            {
                return;
            }

            Log.Info(
                $"[SpoutMirror] 工程分辨率变更：" +
                $"{oldSize.Width}x{oldSize.Height} -> {w}x{h}");

            browser.Size = new Size(w, h);
        }
        catch (Exception ex)
        {
            Log.Warn(
                "[SpoutMirror] plan-size 消息处理失败：" +
                ex.Message);
        }
    }

    // --------------------------------------------------------------------
    // 主窗口 -> 镜像窗口
    // --------------------------------------------------------------------

    public void ApplyPlaybackSync(
        bool playing,
        double time)
    {
        var browser = _browser;

        if (browser == null)
            return;

        try
        {
            var payload = new
            {
                playing,
                t = time
            };

            string json =
                JsonSerializer.Serialize(payload);

            string script =
                $"window.__jlApplySync && " +
                $"window.__jlApplySync({json});";

            // 时间流每秒约 30 次，不逐条记日志（会拖慢 UI 线程）

            browser
                .GetMainFrame()
                .ExecuteJavaScriptAsync(script);
        }
        catch (Exception ex)
        {
            Log.Warn(
                "[SpoutMirror] ApplyPlaybackSync 失败：" +
                ex.Message);
        }
    }
    private void CleanupLocked()
    {
        IsRunning = false;

        if (_browser != null)
        {
            _browser.FrameLoadEnd -= OnFrameLoadEnd;
            _browser.JavascriptMessageReceived -= OnMirrorMessage;
            _browser.LoadError -= OnMirrorLoadError;
            _browser.ConsoleMessage -= OnMirrorConsoleMessage;
            _browser.BrowserInitialized -= OnMirrorBrowserInitialized;

            try
            {
                _browser.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn(
                    "[SpoutMirror] Browser Dispose 失败：" +
                    ex.Message);
            }

            _browser = null;
        }

        if (_renderHandler != null)
        {
            try
            {
                _renderHandler.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn(
                    "[SpoutMirror] RenderHandler Dispose 失败：" +
                    ex.Message);
            }

            _renderHandler = null;
        }
    }

    public void Stop()
    {
        if (!IsRunning)
            return;

        string? oldName = SenderName;

        CleanupLocked();

        Log.Info(
            $"Spout 管线已停止：通道={oldName}");
    }

    public void Dispose()
    {
        Stop();
    }

    // ====================================================================
    // 镜像 JS
    // ====================================================================

    private const string MirrorScript = """
(function () {
    if (window.__jl_spout_mirror)
        return;

    window.__jl_spout_mirror = true;

    var LS_KEY = 'jizura.project.v1';

    var lastW = 0;
    var lastH = 0;

    // ================================================================
    // 页面状态入口
    //
    // 注意：12_ui.js 全部包在 IIFE 里，S / play / pause / seek /
    // mergeProject / syncUI / replan 都不是全局的。
    // 页面只暴露 window.J.ui（状态）与 window.J.uiApi（动作）。
    // ================================================================

    function state() {
        return (window.J && J.ui) || null;
    }

    // ================================================================
    // C# Host IPC
    // ================================================================

    function postHost(o) {
        try {
            if (
                window.CefSharp &&
                CefSharp.PostMessage
            ) {
                CefSharp.PostMessage(o);
            }
        }
        catch (e) {
            console.error(
                '[SpoutMirror] postHost failed:',
                e
            );
        }
    }

    // ================================================================
    // 页面隔离
    // ================================================================

    function isolate() {
        try {
            var cv = document.getElementById('view');

            if (!cv)
                return;

            if (cv.parentElement !== document.body) {
                document.body.appendChild(cv);
            }

            cv.style.cssText =
                'position:fixed;' +
                'left:0;' +
                'top:0;' +
                'margin:0;' +
                'z-index:2147483647;' +
                'display:block;' +
                'visibility:visible;';

            var kids = document.body.children;

            for (var i = 0; i < kids.length; i++) {
                if (kids[i] !== cv) {
                    kids[i].style.display = 'none';
                }
            }

            document.documentElement.style.background = '#000';
            document.body.style.background = '#000';
        }
        catch (e) {
            console.error(
                '[SpoutMirror] isolate failed:',
                e
            );
        }
    }

    // ================================================================
    // 分辨率保持：画布按工程分辨率渲染，而不是镜像视口大小
    // ================================================================

    function fitPlanSize() {
        var S = state();

        if (!S || !S.plan)
            return;

        var c = document.getElementById('view');

        if (!c)
            return;

        var targetW = Number(S.plan.W) || 0;
        var targetH = Number(S.plan.H) || 0;

        if (targetW <= 0 || targetH <= 0)
            return;

        if (
            c.width !== targetW ||
            c.height !== targetH
        ) {
            c.width = targetW;
            c.height = targetH;

            S.need = true;
        }

        if (
            targetW !== lastW ||
            targetH !== lastH
        ) {
            lastW = targetW;
            lastH = targetH;

            postHost({
                type: 'plan-size',
                w: lastW,
                h: lastH
            });
        }
    }

    // ================================================================
    // 主窗口同步进度（由 C# 调用）
    //
    // 镜像是从渲染器：永远不自己播放（不点播放键、不跑本地时钟）。
    // 推送锚点落地后，泵里按墙钟外推到当前时刻——
    // 推送节奏（~30Hz）与渲染节奏（30/60fps）是两个不同步的时钟，
    // 直接用推送值会形成拍频（一帧重复、下一帧跳两格，
    // 看起来就是规律的"一卡一卡"），外推彻底消除采样抖动。
    // ================================================================

    var mirror = { playing: false, lastT: 0, stamp: 0 };

    window.__jlApplySync = function(data) {
    try {
        var S = state();

        if (!data || !S)
            return;

        // 保持从渲染器暂停态（页面 tick 会用本地时钟覆盖 S.t）
        if (S.playing) {
            var api = window.J && J.uiApi;

            if (api && typeof api.pause === 'function') {
                api.pause();
            }
        }

        var t = Number(data.t);

        if (!Number.isFinite(t))
            return;

        mirror.playing = !!data.playing;
        mirror.lastT = t;
        mirror.stamp = performance.now();

        S.t = t;
        S.need = true;
    }
    catch (e) {
        console.error(
            '[SpoutMirror] ApplySync failed:',
            e
        );
    }
};

    // ================================================================
    // 持续渲染泵（同时负责分辨率保持）
    // ================================================================

    var PUMP_MS =
        Math.max(
            8,
            Math.floor(1000 / __FPS__)
        );

    // ================================================================
    // 性能采样：泵的实际触发间隔 ≈ 镜像渲染节奏，
    // renderer.frame 耗时 ≈ 单帧绘制成本；每 60 次泵上报一次给 C#
    // ================================================================

    var perf = {
        n: 0, last: 0,
        gapSum: 0, gapMax: 0,
        drawN: 0, drawSum: 0, drawMax: 0
    };

    function perfSample(now) {

        if (perf.last) {
            var gap = now - perf.last;
            perf.gapSum += gap;
            if (gap > perf.gapMax)
                perf.gapMax = gap;
        }
        perf.last = now;
        perf.n++;

        // 包装一次 renderer.frame 统计绘制耗时
        // （replan 重建渲染器后 __perfWrapped 标记随对象消失，会自动重包）
        var S = state();

        if (S && S.renderer && !S.renderer.__perfWrapped) {
            var r = S.renderer;
            var orig = r.frame;
            r.__perfWrapped = true;
            r.frame = function (ctx, plan, t, opt) {
                var t0 = performance.now();
                var out = orig.call(r, ctx, plan, t, opt);
                var dt = performance.now() - t0;
                perf.drawN++;
                perf.drawSum += dt;
                if (dt > perf.drawMax)
                    perf.drawMax = dt;
                return out;
            };
        }

        if (perf.n >= 60) {
            postHost({
                type: 'mirror-perf',
                gapAvg: perf.gapSum / Math.max(1, perf.n - 1),
                gapMax: perf.gapMax,
                drawAvg: perf.drawN ? perf.drawSum / perf.drawN : 0,
                drawMax: perf.drawMax,
                drawN: perf.drawN
            });
            perf = { n: 0, last: now, gapSum: 0, gapMax: 0, drawN: 0, drawSum: 0, drawMax: 0 };
        }
    }

    (function pump() {

        requestAnimationFrame(pump);

        try {
            var S = state();

            if (!S)
                return;

            var now =
                performance.now();

            if (
                now - (pump.last || 0) <
                PUMP_MS
            ) {
                return;
            }

            pump.last = now;

            fitPlanSize();

            // 播放中在两次推送之间按墙钟外推（锚点见 __jlApplySync）
            if (mirror.playing) {
                var te = mirror.lastT +
                    (now - mirror.stamp) / 1000;

                if (S.plan && S.plan.duration > 0) {
                    te = Math.min(
                        te,
                        S.plan.duration - 1e-3);
                }

                S.t = te;
            }

            perfSample(now);

            S.need = true;
        }
        catch (e) {
        }

    })();

    // isolate 低频做即可，分辨率保持已移入泵里
    setInterval(function () {
        try {
            isolate();
        }
        catch (e) {
            console.error(
                '[SpoutMirror] isolate failed:',
                e
            );
        }
    }, 500);

    // ================================================================
    // 工程跟随：主窗口保存工程后 storage 事件跨浏览器触发
    // （依赖镜像与主窗口共享默认 RequestContext）
    // ================================================================

    window.addEventListener(
        'storage',
        function (ev) {

            if (
                ev.key !== LS_KEY ||
                !ev.newValue
            ) {
                return;
            }

            try {
                var S = state();

                if (!S)
                    return;

                // 主窗口保存的就是同一版本 normalize 过的工程，
                // 直接落进去即可（mergeProject 在 IIFE 里拿不到）
                S.project =
                    JSON.parse(ev.newValue);

                var api = window.J && J.uiApi;

                if (api && typeof api.syncUI === 'function')
                    api.syncUI();

                if (api && typeof api.replan === 'function')
                    api.replan();

                S.need = true;
            }
            catch (e) {
                console.error(
                    '[SpoutMirror] storage sync failed:',
                    e
                );
            }
        }
    );

    // ================================================================
    // 初始渲染
    // ================================================================

    try {
        var s0 = state();

        if (s0) {
            // 从渲染器保持暂停：若页面加载后自己进入了播放态，
            // 本地时钟会覆盖主窗口推来的时间
            var api0 = window.J && J.uiApi;

            if (s0.playing && api0 &&
                typeof api0.pause === 'function') {
                api0.pause();
            }

            s0.need = true;
        }
    }
    catch (e) {
    }

    isolate();

})();
""";
}