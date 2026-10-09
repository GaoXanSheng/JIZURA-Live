// 移植自 frontend/src/engine/11p_layoutsD.js（layout 表现包 D：34 条，pack "layoutsD"）。
//
// 契约：J.register('layout', key, def, 'layoutsD') → Reg.Register("layout", key, new LayoutDef{...}, "layoutsD")。
// 元数据（name/tags/w/treat/portrait/emph/busy/enterBias）与 plan 的键名/RNG 消费顺序逐行对位——
// plan 产物进 cut.params，随 AE 导出，是数据契约。JS 的 `ae` 字段（AE 导出提示）在 C# LayoutDef
// 里没有对应属性（registry 里无消费者），按条注释保留原值。
// Draw 逐行对位：Canvas2D → SkiaSharp 直译（ctx.transform → Ctx.Concat，clip('evenodd') →
// SKPath.FillType=EvenOdd，createPattern → SKShader.CreateBitmap，strokeText → SKPaint.IsStroke）。
// 文件内私有实现 JS 侧 helper（U/isPort/flat/chunksK/rowsOf/brk/drawAff/plateCol/onCol/shade/objCols/
// memo/poolOf/tilePts/rrPts/resample/jigPath/ledTile/mosaicSample/coverage…），Env/J/JText/JLayouts 里
// 没有对位符号的一律在本文件补齐，不改 Env.cs。

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class P11LayoutsD
{
    private static bool _installed;

    /// <summary>幂等注册本文件全部 layout（顺序 = JS 的 reg() 顺序，即抽取候选顺序）。</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        RegisterCube();
        RegisterCylinder();
        RegisterFlipCards();
        RegisterAccordion();
        RegisterFlag();
        RegisterRibbon();
        RegisterPendulum();
        RegisterPile();
        RegisterBlocks();
        RegisterBalloons();
        RegisterMagnets();
        RegisterTiles();
        RegisterBulbs();
        RegisterLedScroll();
        RegisterBillboard();
        RegisterCrowdBubbles();
        RegisterCrossword();
        RegisterWordSearch();
        RegisterPuzzle();
        RegisterShadowPlay();
        RegisterKaleido();
        RegisterDominoes();
        RegisterBurst();
        RegisterFisheye();
        RegisterWall();
        RegisterOrigami();
        RegisterZipper();
        RegisterSliceStack();
        RegisterGlitchGrid();
        RegisterMosaicTiles();
        RegisterMaskReveal();
        RegisterContour();
        RegisterHalftoneBig();
        RegisterStencil();
    }

    // ---------------- 注册（JS 顺序） ----------------

    private static void RegisterCube()
    {
        Reg.Register("layout", "cube", new LayoutDef
        {
            // JS: ae: 'pill'
            Name = "立方体",
            Tags = new[] { "graphic", "pop" },
            W = 0.8,
            Treat = "safe",
            Emph = 1.2,
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["cut"] = 1.6, ["blur"] = 1.2, ["pop"] = 1.1, ["slice"] = 0.4, ["wipe"] = 0.5, ["stretch"] = 0.5 },
            Plan = (rng, cut, st) =>
            {
                var n = cut.N;
                var mode = n >= 4 && rng.Chance(0.45) ? "pair" : "single";
                if (n >= 6 && cut.Dur > 2.6 && rng.Chance(0.35)) mode = "turn";
                var k = mode == "pair" ? 2 : mode == "turn" ? Math.Min(4, Math.Max(2, (int)Math.Ceiling(n / 5.0))) : 1;
                return new JObject
                {
                    ["mode"] = mode,
                    ["chunks"] = StrArr(k > 1 ? ChunksK(cut.Text, k) : new List<string> { Flat(cut.Text) }),
                    ["font"] = rng.Pick(FontsOf(st, "display", "display", "serif")),
                    ["pitch"] = rng.Range(20, 30),
                    ["dir"] = rng.Pick(new[] { 1, -1 }),
                    ["face"] = rng.Pick(new[] { "ink", "accent", "ink" }),
                    ["top"] = rng.Pick(new[] { "accent", "shade" }),
                    ["sway"] = rng.Range(3, 7),
                };
            },
            Draw = DrawCube,
        }, "layoutsD");
    }

    private static void RegisterCylinder()
    {
        Reg.Register("layout", "cylinder", new LayoutDef
        {
            // JS: ae: 'ring'
            Name = "円筒",
            Tags = new[] { "graphic", "calm", "emotional" },
            W = 0.9,
            Fits = n => n >= 2 && n <= 16,
            EnterBias = new JObject { ["cut"] = 1.4, ["blur"] = 1.3, ["slice"] = 0.3, ["wipe"] = 0.4, ["stretch"] = 0.5 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["fs"] = rng.Pick(FontsOf(st, "body", "display")),
                ["rings"] = rng.Pick(new[] { 2, 4, 4, 6 }),
                ["band"] = rng.Chance(0.45),
                ["dir"] = rng.Pick(new[] { 1, -1 }),
                ["speed"] = rng.Range(0.25, 0.45),
                ["tilt"] = rng.Range(0.13, 0.2),
                ["sep"] = rng.Pick(new[] { " ・ ", " / ", " — " }),
                ["glass"] = rng.Chance(0.7),
            },
            Draw = DrawCylinder,
        }, "layoutsD");
    }

    private static void RegisterFlipCards()
    {
        Reg.Register("layout", "flipCards", new LayoutDef
        {
            // JS: ae: 'labels'
            Name = "カードめくり",
            Tags = new[] { "pop", "graphic" },
            W = 0.8,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 3, ["pop"] = 0.8, ["blur"] = 0.5, ["slice"] = 0.2, ["wipe"] = 0.2, ["stretch"] = 0.3, ["assemble"] = 0.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["order"] = rng.Pick(new[] { "ltr", "ltr", "random" }),
                ["table"] = rng.Chance(0.55),
                ["back"] = rng.Pick(new[] { "accent", "ink" }),
                ["peek"] = rng.Chance(0.6),
                ["idx"] = rng.Chance(0.7),
            },
            Draw = DrawFlipCards,
        }, "layoutsD");
    }

    private static void RegisterAccordion()
    {
        Reg.Register("layout", "accordion", new LayoutDef
        {
            // JS: ae: 'labels'
            Name = "蛇腹",
            Tags = new[] { "pop", "graphic", "editorial" },
            W = 0.8,
            Treat = "safe",
            Fits = n => n >= 2 && n <= 16,
            EnterBias = new JObject { ["cut"] = 2, ["blur"] = 1, ["pop"] = 0.8, ["slice"] = 0.3, ["wipe"] = 0.4 },
            Plan = (rng, cut, st) =>
            {
                var port = cut.H > cut.W * 1.08;
                var orient = port && cut.N <= 10 && rng.Chance(0.7) ? "v" : "h"; // 短路顺序同 JS
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                    ["fold"] = rng.Range(26, 38),
                    ["orient"] = orient,
                    ["covers"] = rng.Chance(0.7),
                    ["plate"] = rng.Pick(new[] { "ink", "fg", "accent" }),
                    ["breathe"] = rng.Range(3, 7),
                    ["start"] = rng.Pick(new[] { 1, -1 }),
                };
            },
            Draw = DrawAccordion,
        }, "layoutsD");
    }

    private static void RegisterFlag()
    {
        Reg.Register("layout", "flag", new LayoutDef
        {
            // JS: ae: 'wave'
            Name = "はためく旗",
            Tags = new[] { "emotional", "pop", "graphic" },
            W = 0.8,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["cut"] = 1.6, ["blur"] = 1.2, ["pop"] = 0.6, ["slice"] = 0.3, ["wipe"] = 0.6 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["mode"] = rng.Pick(new[] { "unfurl", "unfurl", "raise" }),
                ["cloth"] = rng.Pick(new[] { "accent", "ink", "accent" }),
                ["trim"] = rng.Pick(new[] { "none", "bands", "edge" }),
                ["tail"] = rng.Pick(new[] { "rect", "rect", "swallow" }),
                ["wind"] = rng.Range(0.7, 1.15),
                ["side"] = rng.Pick(new[] { 1, 1, -1 }),
            },
            Draw = DrawFlag,
        }, "layoutsD");
    }

    private static void RegisterRibbon()
    {
        Reg.Register("layout", "ribbon", new LayoutDef
        {
            // JS: ae: 'wave'
            Name = "リボン",
            Tags = new[] { "pop", "emotional", "graphic" },
            W = 0.9,
            Treat = "safe",
            Portrait = 0.7,
            Fits = n => n >= 2 && n <= 16,
            EnterBias = new JObject { ["cut"] = 1.8, ["blur"] = 1.2, ["pop"] = 0.8, ["slice"] = 0.3, ["stretch"] = 0.4 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["amp"] = rng.Range(0.05, 0.1),
                ["freq"] = rng.Range(0.55, 0.9),
                ["ph"] = rng.Range(0, 6),
                ["col"] = rng.Pick(new[] { "accent", "accent", "ink" }),
                ["reveal"] = rng.Pick(new[] { "center", "left" }),
                ["tilt"] = rng.Range(-6, 6),
            },
            Draw = DrawRibbon,
        }, "layoutsD");
    }

    private static void RegisterPendulum()
    {
        Reg.Register("layout", "pendulum", new LayoutDef
        {
            // JS: ae: 'labels'
            Name = "振り子",
            Tags = new[] { "calm", "pop", "emotional" },
            W = 0.8,
            Treat = "safe",
            Portrait = 1.2,
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["cut"] = 2, ["drop"] = 1.4, ["pop"] = 1.2, ["blur"] = 0.8, ["slice"] = 0.2, ["wipe"] = 0.3, ["stretch"] = 0.3 },
            Plan = (rng, cut, st) =>
            {
                var n = cut.N;
                var port = cut.H > cut.W * 1.08;
                var spaced = Regex.IsMatch((cut.Text ?? "").Trim(), @"\s");
                var opts = new List<string>();
                if (n <= (port ? 5 : 8) && !spaced) { opts.Add("cradle"); opts.Add("cradle"); }
                if (n <= 12 && !spaced) { opts.Add("fan"); opts.Add(port ? "fan" : "chunks"); }
                if (n >= 4) opts.Add("chunks");
                var mode = rng.Pick(opts.Count > 0 ? opts : new List<string> { "chunks" });
                return new JObject
                {
                    ["mode"] = mode,
                    ["chunks"] = StrArr(ChunksK(cut.Text, J.Clamp((int)Math.Ceiling(n / 4.0), 2, 4))),
                    ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                    ["period"] = rng.Range(1.05, 1.4),
                    ["amp"] = rng.Range(22, 30),
                    ["phase"] = rng.Range(0, 6),
                    ["side"] = rng.Pick(new[] { 1, -1 }),
                };
            },
            Draw = DrawPendulum,
        }, "layoutsD");
    }

    private static void RegisterPile()
    {
        Reg.Register("layout", "pile", new LayoutDef
        {
            // JS: ae: 'scatter'
            Name = "文字の山",
            Tags = new[] { "pop", "emotional", "graphic" },
            W = 0.8,
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 2.4, ["drop"] = 1.2, ["pop"] = 0.8, ["slice"] = 0.2, ["wipe"] = 0.2, ["stretch"] = 0.3, ["assemble"] = 0.4 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["hf"] = rng.Pick(FontsOf(st, "body", "display", "serif")),
                ["peak"] = rng.Range(-0.12, 0.12),
                ["hgt"] = rng.Range(0.26, 0.34),
                ["order"] = rng.Pick(new[] { "ltr", "random", "random" }),
                ["dust"] = rng.Chance(0.7),
            },
            Draw = DrawPile,
        }, "layoutsD");
    }

    private static void RegisterBlocks()
    {
        Reg.Register("layout", "blocks", new LayoutDef
        {
            // JS: ae: 'labels'
            Name = "積み木",
            Tags = new[] { "pop", "graphic" },
            W = 0.8,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 2.5, ["drop"] = 1.4, ["pop"] = 1, ["blur"] = 0.4, ["slice"] = 0.2, ["wipe"] = 0.2, ["stretch"] = 0.3 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["deco"] = rng.Chance(0.75),
                ["tilt"] = rng.Chance(0.5),
                ["hop"] = rng.Chance(0.7),
                ["side"] = rng.Pick(new[] { 1, -1 }),
                ["colOff"] = rng.Int(0, 5),
            },
            Draw = DrawBlocks,
        }, "layoutsD");
    }

    private static void RegisterBalloons()
    {
        Reg.Register("layout", "balloons", new LayoutDef
        {
            // JS: ae: 'scatter'
            Name = "文字風船",
            Tags = new[] { "pop", "emotional", "calm" },
            W = 0.8,
            Treat = false,
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 1.8, ["pop"] = 1.5, ["drop"] = 0.6, ["blur"] = 0.8, ["slice"] = 0.2, ["wipe"] = 0.2, ["assemble"] = 0.3 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["tie"] = rng.Pick(new[] { "bunch", "bunch", "free" }),
                ["arc"] = rng.Range(0.02, 0.06),
                ["colOff"] = rng.Int(0, 4),
                ["mono"] = rng.Chance(0.3),
            },
            Draw = DrawBalloons,
        }, "layoutsD");
    }

    private static void RegisterMagnets()
    {
        Reg.Register("layout", "magnets", new LayoutDef
        {
            // JS: ae: 'scatter'
            Name = "マグネット",
            Tags = new[] { "pop", "graphic" },
            W = 0.7,
            Treat = false,
            Busy = true,
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 2.5, ["pop"] = 1.3, ["drop"] = 0.8, ["slice"] = 0.2, ["wipe"] = 0.2, ["stretch"] = 0.3 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["df"] = rng.Pick(FontsOf(st, "display", "body")),
                ["decoys"] = rng.Int(6, 11),
                ["handle"] = rng.Pick(new[] { 1, -1 }),
                ["colOff"] = rng.Int(0, 4),
                ["tilt"] = rng.Range(6, 12),
            },
            Draw = DrawMagnets,
        }, "layoutsD");
    }

    private static void RegisterTiles()
    {
        Reg.Register("layout", "tiles", new LayoutDef
        {
            // JS: ae: 'labels'
            Name = "文字タイル",
            Tags = new[] { "pop", "editorial", "graphic" },
            W = 0.7,
            Treat = "safe",
            Portrait = 0.7,
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 3, ["pop"] = 0.8, ["blur"] = 0.4, ["slice"] = 0.2, ["wipe"] = 0.2, ["stretch"] = 0.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["from"] = rng.Pick(new[] { "right", "right", "drop" }),
                ["rack"] = rng.Pick(new[] { "accent", "ink", "sub" }),
                ["score"] = rng.Chance(0.75),
            },
            Draw = DrawTiles,
        }, "layoutsD");
    }

    private static void RegisterBulbs()
    {
        Reg.Register("layout", "bulbs", new LayoutDef
        {
            // JS: ae: 'center'
            Name = "電球サイン",
            Tags = new[] { "pop", "emotional", "graphic" },
            W = 0.8,
            Treat = "safe",
            Emph = 1.3,
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 1.8, ["flicker"] = 1.6, ["pop"] = 1.2, ["blur"] = 0.6, ["slice"] = 0.3, ["wipe"] = 0.4 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["shape"] = rng.Pick(new[] { "board", "board", "arrow", "double" }),
                ["chase"] = rng.Pick(new[] { "chase", "chase", "alt", "wave" }),
                ["dir"] = rng.Pick(new[] { 1, -1 }),
                ["sub"] = rng.Chance(0.6),
            },
            Draw = DrawBulbs,
        }, "layoutsD");
    }

    private static void RegisterLedScroll()
    {
        Reg.Register("layout", "ledScroll", new LayoutDef
        {
            // JS: ae: 'marquee'
            Name = "電光掲示板",
            Tags = new[] { "pop", "graphic", "glitch" },
            W = 0.8,
            Treat = false,
            Portrait = 1.1,
            Fits = n => n >= 1 && n <= 24,
            EnterBias = new JObject { ["cut"] = 4, ["flicker"] = 0.8, ["type"] = 0.4, ["blur"] = 0.2, ["slice"] = 0.1, ["wipe"] = 0.2, ["assemble"] = 0.1, ["scramble"] = 0.3 },
            Plan = (rng, cut, st) =>
            {
                var fonts = new List<string> { "dot", "dot" };
                fonts.AddRange(FontsOf(st, "display", "body"));
                return new JObject
                {
                    ["font"] = rng.Pick(fonts),
                    ["col"] = rng.Pick(new[] { "accent", "accent", "fg", "accent2" }),
                    ["info"] = rng.Pick(new[] { "top", "bottom", "none", "bottom" }),
                    ["mods"] = rng.Int(3, 6),
                    ["y"] = rng.Pick(new[] { 0.5, 0.5, 0.42, 0.58 }),
                };
            },
            Draw = DrawLedScroll,
        }, "layoutsD");
    }

    private static void RegisterBillboard()
    {
        Reg.Register("layout", "billboard", new LayoutDef
        {
            // JS: ae: 'center'
            Name = "看板",
            Tags = new[] { "pop", "emotional", "graphic" },
            W = 0.8,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["cut"] = 1.6, ["flicker"] = 1.5, ["blur"] = 1, ["pop"] = 0.8, ["slice"] = 0.4, ["wipe"] = 0.6 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["face"] = rng.Pick(new[] { "light", "light", "accent" }),
                ["lamps"] = rng.Int(3, 4),
                ["sweep"] = rng.Range(8, 16),
                ["tag"] = rng.Chance(0.7),
            },
            Draw = DrawBillboard,
        }, "layoutsD");
    }

    /// <summary>crowdBubbles 的帧不变量（CutCache 载体，性能专项）。</summary>
    private sealed class CrowdInv
    {
        public string Text = "";
        public double Size, Mw, Mh;
        public List<string> Words = new();
        public List<(double X, double Y, double R)> Slots = new();
    }

    private static void RegisterCrowdBubbles()
    {
        Reg.Register("layout", "crowdBubbles", new LayoutDef
        {
            // JS: ae: 'labels'
            Name = "吹き出しの群れ",
            Tags = new[] { "pop", "emotional", "editorial" },
            W = 0.8,
            Busy = true,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["cut"] = 1.8, ["pop"] = 1.6, ["blur"] = 0.8, ["slice"] = 0.3, ["wipe"] = 0.4 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "body")),
                ["sf"] = rng.Pick(FontsOf(st, "body", "display")),
                ["big"] = rng.Pick(new[] { "round", "round", "rect", "shout" }),
                ["style"] = rng.Pick(new[] { "mixed", "outline", "filled" }),
                ["count"] = rng.Int(10, 16),
                ["side"] = rng.Pick(new[] { 1, -1 }),
            },
            Draw = DrawCrowdBubbles,
        }, "layoutsD");
    }

    private static void RegisterCrossword()
    {
        Reg.Register("layout", "crossword", new LayoutDef
        {
            // JS: ae: 'type'
            Name = "クロスワード",
            Tags = new[] { "editorial", "graphic", "pop" },
            W = 0.7,
            Treat = "safe",
            Portrait = 0.7,
            Fits = n => n >= 2 && n <= 16,
            EnterBias = new JObject { ["cut"] = 3, ["type"] = 0.6, ["flicker"] = 0.6, ["blur"] = 0.4, ["slice"] = 0.2, ["wipe"] = 0.2, ["stretch"] = 0.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["dens"] = rng.Range(0.14, 0.22),
                ["fill"] = rng.Int(2, 3),
                ["clue"] = rng.Chance(0.8),
            },
            Draw = DrawCrossword,
        }, "layoutsD");
    }

    private static void RegisterWordSearch()
    {
        Reg.Register("layout", "wordSearch", new LayoutDef
        {
            // JS: ae: 'tile'
            Name = "文字探し",
            Tags = new[] { "pop", "graphic", "editorial" },
            W = 0.7,
            Treat = "safe",
            Fits = n => n >= 2 && n <= 14,
            EnterBias = new JObject { ["cut"] = 3, ["flicker"] = 0.8, ["blur"] = 0.6, ["slice"] = 0.2, ["wipe"] = 0.2, ["stretch"] = 0.2, ["assemble"] = 0.3 },
            Plan = (rng, cut, st) =>
            {
                var port = cut.H > cut.W * 1.08;
                var n = cut.N;
                var dirs = new List<string> { "h", "h" };
                if (port && n <= 14) { dirs.Add("v"); dirs.Add("v"); dirs.Add("v"); }
                if (n <= 8) dirs.Add("d");
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "display", "body")),
                    ["gf"] = rng.Pick(FontsOf(st, "body", "display")),
                    ["dir"] = rng.Pick(dirs),
                    ["decoys"] = rng.Int(1, 3),
                    ["pad"] = rng.Int(1, 3),
                };
            },
            Draw = DrawWordSearch,
        }, "layoutsD");
    }

    private static void RegisterPuzzle()
    {
        Reg.Register("layout", "puzzle", new LayoutDef
        {
            // JS: ae: 'center'
            Name = "パズル",
            Tags = new[] { "pop", "graphic", "emotional" },
            W = 0.8,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["cut"] = 3, ["blur"] = 0.5, ["pop"] = 0.4, ["slice"] = 0.2, ["wipe"] = 0.2, ["stretch"] = 0.2, ["assemble"] = 0.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["plate"] = rng.Pick(new[] { "accent", "light", "ink" }),
                ["rows"] = rng.Pick(new[] { 2, 2, 3 }),
                ["last"] = rng.Chance(0.65),
                ["spread"] = rng.Range(0.6, 1),
            },
            Draw = DrawPuzzle,
        }, "layoutsD");
    }

    private static void RegisterShadowPlay()
    {
        Reg.Register("layout", "shadowPlay", new LayoutDef
        {
            // JS: ae: 'stack'
            Name = "影絵",
            Tags = new[] { "emotional", "calm", "graphic" },
            W = 0.9,
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 1.2, ["blur"] = 1.3, ["drop"] = 1.2, ["pop"] = 0.6, ["slice"] = 0.4 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "display", "serif")),
                ["mode"] = rng.Pick(new[] { "floor", "floor", "wall" }),
                ["dir"] = rng.Pick(new[] { 1, -1 }),
                ["sweep"] = rng.Range(0.7, 1),
                ["sun"] = rng.Chance(0.8),
            },
            Draw = DrawShadowPlay,
        }, "layoutsD");
    }

    private static void RegisterKaleido()
    {
        Reg.Register("layout", "kaleido", new LayoutDef
        {
            // JS: ae: 'ring'
            Name = "万華鏡",
            Tags = new[] { "glitch", "emotional", "graphic" },
            W = 0.8,
            Busy = true,
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 1.4, ["zoom"] = 1.4, ["spin"] = 1.3, ["blur"] = 1.2, ["slice"] = 0.3, ["wipe"] = 0.3 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["N"] = rng.Pick(new[] { 6, 8, 8, 10, 12 }),
                ["speed"] = rng.Range(4, 10) * rng.Pick(new[] { 1, -1 }),
                ["flow"] = rng.Range(0.15, 0.3),
                ["center"] = rng.Pick(new[] { "disc", "disc", "band" }),
                ["tint"] = rng.Pick(new[] { "sub", "accent", "mixed" }),
            },
            Draw = DrawKaleido,
        }, "layoutsD");
    }

    private static void RegisterDominoes()
    {
        Reg.Register("layout", "dominoes", new LayoutDef
        {
            // JS: ae: 'labels'
            Name = "ドミノ",
            Tags = new[] { "pop", "graphic" },
            W = 0.7,
            Treat = "safe",
            Portrait = 0.7,
            Fits = n => n >= 2 && n <= 14,
            EnterBias = new JObject { ["cut"] = 3, ["blur"] = 0.5, ["pop"] = 0.6, ["slice"] = 0.2, ["wipe"] = 0.2, ["stretch"] = 0.2, ["drop"] = 0.5 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["face"] = rng.Pick(new[] { "light", "light", "ink" }),
                ["pips"] = rng.Chance(0.8),
            },
            Draw = DrawDominoes,
        }, "layoutsD");
    }

    private static void RegisterBurst()
    {
        Reg.Register("layout", "burst", new LayoutDef
        {
            // JS: ae: 'circle'
            Name = "ドカン",
            Tags = new[] { "pop", "graphic", "emotional" },
            W = 0.8,
            Treat = "safe",
            Emph = 1.6,
            Fits = n => n >= 1 && n <= 12,
            EnterBias = new JObject { ["cut"] = 1.6, ["pop"] = 1.6, ["zoom"] = 1.4, ["blur"] = 0.5, ["slice"] = 0.3, ["wipe"] = 0.3, ["type"] = 0.3 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["spikes"] = rng.Int(13, 19),
                ["sharp"] = rng.Range(0.66, 0.78),
                ["tilt"] = rng.Range(-9, 9),
                ["lines"] = rng.Chance(0.75),
                ["debris"] = rng.Chance(0.8),
                ["order"] = rng.Pick(new[] { "accent", "ink" }),
            },
            Draw = DrawBurst,
        }, "layoutsD");
    }

    private static void RegisterFisheye()
    {
        Reg.Register("layout", "fisheye", new LayoutDef
        {
            // JS: ae: 'mixed'
            Name = "魚眼レンズ",
            Tags = new[] { "pop", "graphic", "glitch" },
            W = 0.8,
            Portrait = 0.7,
            Fits = n => n >= 3 && n <= 16,
            EnterBias = new JObject { ["cut"] = 1.4, ["blur"] = 1.2, ["pop"] = 1, ["slice"] = 0.4, ["wipe"] = 0.6, ["type"] = 0.8 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["mode"] = rng.Pick(new[] { "sweep", "sweep", "pingpong" }),
                ["glass"] = rng.Pick(new[] { "lens", "lens", "grid", "none" }),
                ["amp"] = rng.Range(0.7, 1.1),
            },
            Draw = DrawFisheye,
        }, "layoutsD");
    }

    private static void RegisterWall()
    {
        Reg.Register("layout", "wall", new LayoutDef
        {
            // JS: ae: 'diag'
            Name = "壁面パース",
            Tags = new[] { "graphic", "editorial", "emotional" },
            W = 0.8,
            Busy = true,
            Portrait = 0.6,
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["cut"] = 1.6, ["blur"] = 1.2, ["wipe"] = 1, ["slice"] = 0.5, ["pop"] = 0.4 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "display", "serif")),
                ["side"] = rng.Pick(new[] { 1, -1 }),
                ["yaw"] = rng.Range(36, 46),
                ["orbit"] = rng.Range(8, 14),
                ["stripe"] = rng.Chance(0.7),
                ["eye"] = rng.Range(-0.06, 0.06),
                ["tone"] = rng.Pick(new[] { "plate", "plain" }),
            },
            Draw = DrawWall,
        }, "layoutsD");
    }

    private static void RegisterOrigami()
    {
        Reg.Register("layout", "origami", new LayoutDef
        {
            // JS: ae: 'circle'
            Name = "折り紙",
            Tags = new[] { "calm", "pop", "emotional" },
            W = 0.8,
            Treat = "safe",
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 2.5, ["blur"] = 0.8, ["pop"] = 0.6, ["slice"] = 0.2, ["wipe"] = 0.3 },
            Plan = (rng, cut, st) =>
            {
                var port = cut.H > cut.W * 1.08;
                var mode = !port && cut.N > 6 && rng.Chance(0.6) ? "gate" : rng.Pick(new[] { "blintz", "blintz", "gate" });
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "serif", "display")),
                    ["mode"] = mode,
                    ["col"] = rng.Pick(new[] { "accent", "accent", "ink" }),
                    ["order"] = rng.Int(0, 3),
                };
            },
            Draw = DrawOrigami,
        }, "layoutsD");
    }

    private static void RegisterZipper()
    {
        Reg.Register("layout", "zipper", new LayoutDef
        {
            // JS: ae: 'center'
            Name = "ジッパー",
            Tags = new[] { "pop", "graphic", "emotional" },
            W = 0.7,
            Busy = true,
            Portrait = 1.1,
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 2, ["blur"] = 1, ["pop"] = 0.8, ["slice"] = 0.3, ["wipe"] = 0.5 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["cloth"] = rng.Pick(new[] { "ink", "accent", "ink" }),
                ["dir"] = rng.Pick(new[] { 1, -1 }),
                ["stitch"] = rng.Chance(0.8),
            },
            Draw = DrawZipper,
        }, "layoutsD");
    }

    private static void RegisterSliceStack()
    {
        Reg.Register("layout", "sliceStack", new LayoutDef
        {
            // JS: ae: 'stack'
            Name = "スライス積層",
            Tags = new[] { "glitch", "graphic", "pop" },
            W = 0.9,
            Emph = 1.2,
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 2, ["blur"] = 0.8, ["flicker"] = 0.8, ["slice"] = 0.2, ["wipe"] = 0.4 },
            Plan = (rng, cut, st) =>
            {
                var mode = rng.Pick(new[] { "blinds", "blinds", "stack", "wave" });
                return new JObject
                {
                    ["font"] = rng.Pick(FontsOf(st, "display")),
                    ["mode"] = mode,
                    ["slices"] = mode == "blinds" ? rng.Int(6, 9) : rng.Int(6, 8),
                    ["gap"] = rng.Range(0.3, 0.55),
                    ["side"] = rng.Pick(new[] { "accent", "accent", "sub" }),
                    ["amp"] = rng.Range(0.08, 0.16),
                    ["dir"] = rng.Pick(new[] { 1, -1 }),
                    ["plate"] = rng.Pick(new[] { "ink", "accent" }),
                };
            },
            Draw = DrawSliceStack,
        }, "layoutsD");
    }

    private static void RegisterGlitchGrid()
    {
        Reg.Register("layout", "glitchGrid", new LayoutDef
        {
            // JS: ae: 'tile'
            Name = "グリッチ格子",
            Tags = new[] { "glitch", "graphic" },
            W = 0.8,
            Busy = true,
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 2.5, ["flicker"] = 1.5, ["scramble"] = 1.2, ["blur"] = 0.4, ["slice"] = 0.8, ["wipe"] = 0.3 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["rate"] = rng.Int(3, 6),
                ["inv"] = rng.Range(0.1, 0.22),
                ["labels"] = rng.Chance(0.75),
                ["gut"] = rng.Range(0.008, 0.016),
            },
            Draw = DrawGlitchGrid,
        }, "layoutsD");
    }

    private static void RegisterMosaicTiles()
    {
        Reg.Register("layout", "mosaicTiles", new LayoutDef
        {
            // JS: ae: 'center'
            Name = "タイル画",
            Tags = new[] { "pop", "graphic", "glitch" },
            W = 0.7,
            Treat = false,
            Fits = n => n >= 1 && n <= 12,
            EnterBias = new JObject { ["cut"] = 3, ["flicker"] = 0.8, ["blur"] = 0.3, ["slice"] = 0.2, ["wipe"] = 0.3, ["assemble"] = 0.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["D"] = rng.Int(9, 12),
                ["wave"] = rng.Pick(new[] { "diag", "random", "center" }),
                ["rot"] = rng.Chance(0.4),
                ["col"] = rng.Pick(new[] { "fg", "accent", "fg" }),
                ["floor"] = rng.Chance(0.75),
            },
            Draw = DrawMosaicTiles,
        }, "layoutsD");
    }

    private static void RegisterMaskReveal()
    {
        Reg.Register("layout", "maskReveal", new LayoutDef
        {
            // JS: ae: 'huge'
            Name = "文字窓",
            Tags = new[] { "graphic", "pop", "emotional" },
            W = 0.9,
            Treat = false,
            Emph = 1.3,
            Fits = n => n >= 1 && n <= 12,
            EnterBias = new JObject { ["cut"] = 1.4, ["blur"] = 1.2, ["wipe"] = 1.2, ["slice"] = 0.8, ["stretch"] = 0.8, ["pop"] = 0.6, ["assemble"] = 0.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["scene"] = rng.Pick(new[] { "stripes", "lines", "dots", "shine", "stripes" }),
                ["ang"] = rng.Range(20, 35) * rng.Pick(new[] { 1, -1 }),
                ["speed"] = rng.Range(0.6, 1.2),
                ["rim"] = rng.Chance(0.65),
                ["label"] = rng.Chance(0.6),
            },
            Draw = DrawMaskReveal,
        }, "layoutsD");
    }

    private static void RegisterContour()
    {
        Reg.Register("layout", "contour", new LayoutDef
        {
            // JS: ae: 'huge'
            Name = "等高線",
            Tags = new[] { "calm", "graphic", "emotional" },
            W = 0.9,
            Fits = n => n >= 1 && n <= 16,
            EnterBias = new JObject { ["blur"] = 1.3, ["cut"] = 1.2, ["wipe"] = 1, ["slice"] = 0.6 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display", "serif")),
                ["fb"] = rng.Pick(FontsOf(st, "display")),
                ["side"] = rng.Pick(new[] { 1, -1 }),
                ["rings"] = rng.Int(4, 6),
                ["speed"] = rng.Range(0.25, 0.5),
                ["col"] = rng.Pick(new[] { "sub", "accent", "sub" }),
                ["place"] = rng.Pick(new[] { "low", "center", "low" }),
            },
            Draw = DrawContour,
        }, "layoutsD");
    }

    private static void RegisterHalftoneBig()
    {
        Reg.Register("layout", "halftoneBig", new LayoutDef
        {
            // JS: ae: 'huge'
            Name = "網点巨大文字",
            Tags = new[] { "pop", "graphic", "editorial" },
            W = 0.9,
            Treat = false,
            Emph = 1.3,
            Fits = n => n >= 1 && n <= 10,
            EnterBias = new JObject { ["cut"] = 1.6, ["blur"] = 1, ["wipe"] = 1, ["slice"] = 0.6, ["stretch"] = 0.6, ["assemble"] = 0.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["mode"] = rng.Pick(new[] { "duo", "duo", "tone" }),
                ["ang"] = rng.Range(15, 40) * rng.Pick(new[] { 1, -1 }),
                ["shape"] = rng.Pick(new[] { "dot", "dot", "line" }),
                ["speed"] = rng.Range(0.4, 0.8),
                ["crop"] = rng.Chance(0.35),
            },
            Draw = DrawHalftoneBig,
        }, "layoutsD");
    }

    private static void RegisterStencil()
    {
        Reg.Register("layout", "stencil", new LayoutDef
        {
            // JS: ae: 'center'
            Name = "ステンシル",
            Tags = new[] { "graphic", "pop", "editorial" },
            W = 0.8,
            Treat = false,
            Fits = n => n >= 1 && n <= 14,
            EnterBias = new JObject { ["cut"] = 1.8, ["wipe"] = 1.4, ["blur"] = 0.8, ["slice"] = 0.4, ["pop"] = 0.4, ["assemble"] = 0.2 },
            Plan = (rng, cut, st) => new JObject
            {
                ["font"] = rng.Pick(FontsOf(st, "display")),
                ["col"] = rng.Pick(new[] { "accent", "fg", "accent" }),
                ["drips"] = rng.Int(1, 3),
                ["marks"] = rng.Chance(0.75),
                ["dir"] = rng.Pick(new[] { 1, -1 }),
                ["tilt"] = rng.Range(-3, 3),
            },
            Draw = DrawStencil,
        }, "layoutsD");
    }

    // ================= JS helper 对位（本文件私有实现，不改 Env.cs） =================

    // -- 源 L7-42：几何/文本小工具 --

    private static double U(Env env) => Math.Min(env.W, env.H);
    private static bool IsPort(Env env) => env.H > env.W * 1.08;
    private static string Strip(string? t) => Regex.Replace(t ?? "", @"\s+", "");
    private static bool HasLatin(string? t) => t != null && Regex.IsMatch(t, "[A-Za-z]");
    // 整段当一个 run：拉丁文保留词间空格，日文直接去掉空白
    private static string Flat(string? t) => HasLatin(t) ? Regex.Replace((t ?? "").Trim(), @"\s+", " ") : Strip(t);
    // 字形槽位（拉丁歌词的词间空格也占一槽）
    private static List<string> SlotsOf(string? t) => JLayouts.CodePoints(Flat(t));
    private static string[] FontsOf(StyleDef st, params string[] roles) => JLayouts.FontsOf(st, roles);
    private static string BodyF(Env env) => JLayouts.Font0(env.St, "body") ?? "gothic_med";
    private static string MonoF(Env env) => JLayouts.Font0(env.St, "mono") ?? "mono";

    private static double Tin(Env env, double d = 0, double len = 0.4, Func<double, double>? ease = null) =>
        (ease ?? J.E.OutExpo)(J.Clamp((env.Lt - d) / Math.Max(0.01, len)));

    private static double Tout(Env env) => 1 - J.E.InCubic(env.POut);

    private static BBox Box(double x0, double y0, double x1, double y1) => new()
    {
        X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Cx = (x0 + x1) / 2, Cy = (y0 + y1) / 2, Boxes = new List<JObject>(),
    };

    private static string Pad2(long n) => n.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
    private static string LineNo(Env env) => Pad2(Math.Max(0, env.Cut!.Line) + 1L);
    private static double SmallSize(Env env) => J.Clamp(U(env) * 0.024, 13, 32);

    /// <summary>算 motion index，让 MainDraw 在本地时间 t 起播这一项的入场。</summary>
    private static double MiAt(Env env, double t) =>
        Math.Max(0, t) / Math.Max(0.005, env.Cut!.Stagger != 0 ? env.Cut.Stagger : 0.04);

    /// <summary>大色板飞入期间才上彩色重影。</summary>
    private static bool GIn(Env env) => env.Lt < 0.6 && env.POut <= 0;

    /// <summary>印在物体上的文字只吃不挪位的 hold。</summary>
    private static bool PlateHold(Env env) =>
        env.Cut!.Hold is not ("still" or "jitter" or "breathe" or "glitchtick");

    private static string? RomajiOf(Env env)
    {
        var c = Strip(env.Cut!.Text);
        if (HasLatin(c) || !Regex.IsMatch(c, @"[ぁ-ヿ]")) return null;
        var r = J.Romaji(c);
        return string.IsNullOrEmpty(r) ? null : r.ToUpperInvariant();
    }

    private static string AltCopy(Env env)
    {
        var c = env.Cut!;
        if (!string.IsNullOrEmpty(c.LineText) && Strip(c.LineText) != Strip(c.Text)) return Flat(c.LineText);
        return RomajiOf(env) ?? "No." + LineNo(env);
    }

    // -- 源 L42-77：配色 --

    private static double LumOf(string? c) => J.Lum(c ?? "#000000");
    private static string LightOf(Scheme sc) => LumOf(sc.Str("fg")) > LumOf(sc.Str("bg")) ? sc.Str("fg")! : sc.Str("bg")!;
    private static string DarkOf(Scheme sc) => LumOf(sc.Str("fg")) > LumOf(sc.Str("bg")) ? sc.Str("bg")! : sc.Str("fg")!;

    private static readonly Dictionary<string, string> OncCache = new();

    /// <summary>fill 底色上对比度最高的配色（源 _onc，>300 清空）。</summary>
    private static string OnCol(Scheme sc, string fill)
    {
        var key = fill + sc.Str("bg") + sc.Str("fg") + sc.Str("ink") + sc.Str("accent");
        if (OncCache.TryGetValue(key, out var hit)) return hit;
        string? best = null;
        var bv = 0.0;
        foreach (var c in new[] { sc.Str("bg"), sc.Str("fg"), sc.Str("ink"), sc.Str("accent"), sc.Str("sub") })
        {
            if (string.IsNullOrEmpty(c) || c == fill) continue;
            var k = J.Contrast(c!, fill);
            if (k > bv)
            {
                bv = k;
                best = c;
            }
        }
        var v = bv >= 2.4 ? best! : (J.Lum(fill) > 0.5 ? "#111111" : "#FFFFFF");
        if (OncCache.Count > 300) OncCache.Clear();
        OncCache[key] = v;
        return v;
    }

    /// <summary>配色里第一个在背景上够显眼的（色板和物体用）。</summary>
    private static string PlateCol(Scheme sc, IEnumerable<string?> pref)
    {
        foreach (var c in pref)
            if (!string.IsNullOrEmpty(c) && J.Contrast(c!, sc.Str("bg") ?? "#000000") >= 1.6) return c!;
        return sc.Str("fg") ?? "#FFFFFF";
    }

    /// <summary>s&gt;0 往配色亮端偏，s&lt;0 往暗端偏。</summary>
    private static string Shade(Scheme sc, string c, double s) =>
        s >= 0 ? J.Mix(c, LightOf(sc), Math.Min(0.9, s)) : J.Mix(c, DarkOf(sc), Math.Min(0.9, -s));

    /// <summary>背景上可读的物体色板（色板、磁贴、气球都用它）。</summary>
    private static List<string> ObjCols(Scheme sc)
    {
        var res = new List<string>();
        foreach (var c in new[] { sc.Str("accent"), sc.Str("fg"), sc.Str("accent2"), sc.Str("ink"), sc.Str("sub") })
            if (!string.IsNullOrEmpty(c) && J.Contrast(c!, sc.Str("bg") ?? "#000000") >= 1.7 && !res.Contains(c!))
                res.Add(c!);
        return res.Count > 0 ? res : new List<string> { sc.Str("fg") ?? "#FFFFFF" };
    }

    private static SKColor SkRgba(string? c, double a) => Env.SkColor(c ?? "#000000", a);

    private static double Adv(string font, string ch) => ch == " " ? 0.34 : JText.Metrics.Adv(font, ch);

    // -- 源 L79-177：分块 / 断行 --

    private readonly record struct WSp(string T, bool Sp);

    /// <summary>分块：超长词在自然断点再切（J.splitLines / J.chunkText）。</summary>
    private static List<string> SplitWord(string w)
    {
        var n = JLayouts.CpLen(w);
        if (Regex.IsMatch(w, "[A-Za-z]"))
        {
            if (Regex.IsMatch(w, "[^\x00-\x7F]"))
            {
                var c = Planner.ChunkText(w).Where(x => !string.IsNullOrEmpty(x)).ToList();
                if (c.Count > 1) return c;
            }
            var half = (int)Math.Ceiling(w.Length / 2.0);
            return n > 10
                ? new List<string> { w.Substring(0, half), w.Substring(half) }
                : new List<string> { w };
        }
        if (n < 2) return new List<string> { w };
        return JLayouts.SplitLines(w, (int)Math.Ceiling(n / 2.0)).Split('\n').Where(x => x.Length > 0).ToList();
    }

    private static List<string> ChunksK(string? text, int k)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return new List<string> { "" };
        var words = new List<WSp>();
        if (Regex.IsMatch(t, @"\s") && HasLatin(t))
        {
            var ws = Regex.Split(t, @"\s+").Where(x => x.Length > 0).ToList();
            for (var i = 0; i < ws.Count; i++) words.Add(new WSp(ws[i], i < ws.Count - 1));
        }
        else
        {
            foreach (var w in Planner.ChunkText(Strip(t)))
            {
                var tt = w.Trim();
                if (tt.Length > 0) words.Add(new WSp(tt, false));
            }
        }
        if (words.Count == 0) words.Add(new WSp(t, false));
        k = Math.Max(1, Math.Min(k, JLayouts.GlyphCount(t)));
        int Gl(WSp w) => JLayouts.GlyphCount(w.T);
        var hard = new HashSet<string>();

        bool SplitAt(int idx)
        {
            var w = words[idx];
            var parts = SplitWord(w.T);
            if (parts.Count < 2)
            {
                hard.Add(w.T);
                return false;
            }
            words.RemoveAt(idx);
            for (var i = 0; i < parts.Count; i++)
                words.Insert(idx + i, new WSp(parts[i], i == parts.Count - 1 ? w.Sp : false));
            return true;
        }

        for (var guard = 0; words.Count < k && guard < 20; guard++)
        {
            var bi = -1;
            var bl = 1;
            for (var i = 0; i < words.Count; i++)
            {
                if (!hard.Contains(words[i].T) && Gl(words[i]) > bl)
                {
                    bl = Gl(words[i]);
                    bi = i;
                }
            }
            if (bi < 0) break;
            SplitAt(bi);
        }

        // 连续划分成 k 组，字形数尽量均衡，小型 DP
        List<List<WSp>> Part()
        {
            var n = words.Count;
            var kk = Math.Min(k, n);
            var L = words.Select(w => Gl(w) + 0.4).ToList();
            var pre = new List<double> { 0 };
            foreach (var l in L) pre.Add(pre[^1] + l);
            var tgt = pre[n] / kk;
            var dp = new double[kk + 1, n + 1];
            var by = new int[kk + 1, n + 1];
            for (var g = 0; g <= kk; g++)
                for (var i = 0; i <= n; i++) dp[g, i] = double.PositiveInfinity;
            dp[0, 0] = 0;
            for (var g = 1; g <= kk; g++)
                for (var i = g; i <= n; i++)
                    for (var j = g - 1; j < i; j++)
                    {
                        var d = pre[i] - pre[j] - tgt;
                        var v = dp[g - 1, j] + d * d;
                        if (v < dp[g, i])
                        {
                            dp[g, i] = v;
                            by[g, i] = j;
                        }
                    }
            var cuts = new List<int>();
            var ii = n;
            for (var g = kk; g >= 1; g--)
            {
                cuts.Insert(0, ii);
                ii = by[g, ii];
            }
            var outp = new List<List<WSp>>();
            var prev = 0;
            foreach (var c in cuts)
            {
                outp.Add(words.GetRange(prev, c - prev));
                prev = c;
            }
            return outp;
        }

        var groups = Part();
        for (var it = 0; it < 3 && groups.Count > 1; it++)
        {
            var lens = groups.Select(g => g.Sum(w => Gl(w))).ToList();
            var mx = lens.Max();
            var mn = lens.Min();
            if (mx <= mn * 1.7 + 1) break;
            var big = groups[lens.IndexOf(mx)];
            WSp? bw = null;
            foreach (var w in big)
                if (!hard.Contains(w.T) && Gl(w) >= 4 && (bw == null || Gl(w) > Gl(bw.Value))) bw = w;
            if (bw == null || !SplitAt(words.IndexOf(bw.Value))) break;
            groups = Part();
        }
        return groups
            .Select(g => string.Concat(g.Select((w, i) => w.T + (i < g.Count - 1 && w.Sp ? " " : ""))))
            .Where(x => x.Length > 0).ToList();
    }

    /// <summary>字形槽位行，' ' 是词间隙，按分块边界断行。</summary>
    private static List<List<string>> RowsOf(string text, int maxPer)
    {
        var t = Flat(text);
        var n = JLayouts.GlyphCount(t);
        var rows = n > maxPer ? ChunksK(t, (int)Math.Ceiling(n / (double)maxPer)) : new List<string> { t };
        return rows.Select(r => JLayouts.CodePoints(r.Trim())).ToList();
    }

    /// <summary>显示用的均衡断行。</summary>
    private static string Brk(string text, int maxPer)
    {
        var t = Flat(text);
        var n = JLayouts.GlyphCount(t);
        return n <= maxPer ? t : string.Join("\n", ChunksK(t, (int)Math.Ceiling(n / (double)maxPer)));
    }

    // -- 源 L179-220：仿射工具 --

    private static double[] MapPt(double[] m, double x, double y) =>
        new[] { m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5] };

    private static BBox? MapBb(BBox? bb, double[] m)
    {
        if (bb == null) return null;
        var x0 = 1e9;
        var y0 = 1e9;
        var x1 = -1e9;
        var y1 = -1e9;
        foreach (var p in new[] { new[] { bb.X0, bb.Y0 }, new[] { bb.X1, bb.Y0 }, new[] { bb.X1, bb.Y1 }, new[] { bb.X0, bb.Y1 } })
        {
            var q = MapPt(m, p[0], p[1]);
            x0 = Math.Min(x0, q[0]);
            x1 = Math.Max(x1, q[0]);
            y0 = Math.Min(y0, q[1]);
            y1 = Math.Max(y1, q[1]);
        }
        return Box(x0, y0, x1, y1);
    }

    private static SKMatrix Mat(double[] m) => new()
    {
        ScaleX = (float)m[0], SkewY = (float)m[1], SkewX = (float)m[2],
        ScaleY = (float)m[3], TransX = (float)m[4], TransY = (float)m[5], Persp2 = 1,
    };

    /// <summary>歌词过一个仿射矩阵绘制，再把 bbox 映射回设计空间（源 drawAff）。</summary>
    private static BBox? DrawAff(Env env, JObject it, double[] m)
    {
        env.Ctx.Save();
        env.Ctx.Concat(Mat(m));
        var bb = JLayouts.MainDraw(env, it);
        env.Ctx.Restore();
        return MapBb(bb, m);
    }

    private static void DrawAffPlain(Env env, JObject it, double[] m)
    {
        env.Ctx.Save();
        env.Ctx.Concat(Mat(m));
        env.DrawItem(it);
        env.Ctx.Restore();
    }

    private static double[][] PolyPts(double[] m, IEnumerable<double[]> pts) => pts.Select(p => MapPt(m, p[0], p[1])).ToArray();

    private static void RrPath(SKPath path, double x, double y, double w, double h, double r)
    {
        r = Math.Max(0, Math.Min(r, Math.Min(w / 2, h / 2)));
        // 直接走 rect+rx/ry 重载（免建 SKRoundRect——原 new 后不释放，实测终结器线程 SKRoundRect.Dispose 占全进程 CPU 8.4%）
        path.AddRoundRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), (float)r, (float)r);
    }

    private static void Outline(Env env, double[][] pts, string col, double lw, double a = 1)
    {
        if (pts.Length > 1) env.Line(Closed(pts), col, lw, a, false);
    }

    private static double[][] Closed(double[][] pts)
    {
        var o = new double[pts.Length + 1][];
        Array.Copy(pts, o, pts.Length);
        o[^1] = pts[0];
        return o;
    }

    private static double[][] Rev(IEnumerable<double[]> pts)
    {
        var l = pts.ToList();
        l.Reverse();
        return l.ToArray();
    }

    private static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    // -- 源 L221-260：字形池 / memo / 立方体面 --

    private static readonly Dictionary<string, List<string>> PoolCache = new();

    private static List<string> PoolOf(Cut cut)
    {
        var key = JLang.Lang + "|" + (cut.LineText ?? "") + "|" + cut.Text;
        if (PoolCache.TryGetValue(key, out var hit)) return hit;
        var p = new List<string>();
        foreach (var c in JLayouts.CodePoints(Strip((cut.LineText ?? "") + cut.Text)))
            if (!J.IsPunct(c[0]) && !J.IsSmallKana(c[0]) && c != "ー" && !J.IsLatin(c[0])) p.Add(c);
        var hira = JLang.Pool("hira") ?? "";
        var i = 0;
        foreach (var c in JLayouts.CodePoints(hira))
        {
            if (i % 3 == 0) p.Add(c);
            i++;
        }
        if (PoolCache.Count > 100) PoolCache.Clear();
        PoolCache[key] = p;
        return p;
    }

    private const string MemoSent = "\u0001layoutsD";
    private static readonly Dictionary<string, object> MemoStore = new();

    /// <summary>字体晚到会重建度量，靠 SENT 探针把 memo 清空（源 memo）。</summary>
    private static T Memoize<T>(string key, Func<T> fn)
    {
        if (!JText.Metrics.M.ContainsKey(MemoSent))
        {
            MemoStore.Clear();
            JText.Metrics.M[MemoSent] = 1;
        }
        if (MemoStore.TryGetValue(key, out var v) && v is T t) return t;
        var r = fn();
        if (MemoStore.Count > 300) MemoStore.Clear();
        MemoStore[key] = r!;
        return r;
    }

    private sealed record CubeFace(int F, double Vis, double[] M, double Nx, double Nz, bool Top);

    /// <summary>正交投影立方体（yaw θ, pitch φ）：每个面都是平行四边形。</summary>
    private static List<CubeFace> CubeFaces(double cx, double cy, double h, double th, double ph)
    {
        var cp = Math.Cos(ph);
        var sp = Math.Sin(ph);
        var faces = new List<CubeFace>();
        for (var f = 0; f < 4; f++)
        {
            var a = f * Math.PI / 2 + th;
            var ca = Math.Cos(a);
            var sa = Math.Sin(a);
            var m = new[] { ca, -sa * sp, 0.0, cp, cx + h * sa, cy + h * ca * sp };
            faces.Add(new CubeFace(f, ca * cp, m, sa, ca, false));
        }
        var ct = Math.Cos(th);
        var s2 = Math.Sin(th);
        faces.Add(new CubeFace(4, sp, new[] { ct, -s2 * sp, s2, ct * sp, cx, cy - h * cp }, 0, 0, true));
        return faces;
    }

    // -- 松散 JSON 读取（JLayouts 的 internal 帮手直接复用） --

    private static string? S(JObject p, string k) => JLayouts.S(p, k);
    private static double D(JObject p, string k, double d = 0) => JLayouts.D(p, k, d);
    private static int I(JObject p, string k, int d = 0) => JLayouts.I(p, k, d);
    private static bool B(JObject p, string k) => JLayouts.B(p, k);

    private static List<string> StrList(JObject p, string k)
    {
        var l = new List<string>();
        if (p[k] is JArray a)
            foreach (var t in a)
                if (t.Type == JTokenType.String) l.Add((string)t!);
        return l;
    }

    private static JArray StrArr(IEnumerable<string> xs)
    {
        var a = new JArray();
        foreach (var x in xs) a.Add(x);
        return a;
    }

    // -- 引擎桥 --

    /// <summary>
    /// JS 的 env.beat = {since, len, index}（09_render 的 beatAt）。已接 Env 桥字段
    /// （BeatHas/BeatSince/BeatLen/BeatIndex，渲染器每帧二分挂载）——blocks 的拍跳分支、
    /// bulbs 的 beatFlash 都按真节拍走；无节拍（BeatHas=false）回各自的周期回退式。
    /// </summary>
    private static (double Since, double Len, int Index)? BeatOf(Env env) =>
        env.BeatHas ? (env.BeatSince, env.BeatLen, env.BeatIndex) : null;

    // ===== DRAWS =====

    private static double Or1(double x) => x != 0 ? x : 1;

    // ---------------- cube（源 L262-414） ----------------

    private static void DrawCube(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var chunks = StrList(Pm, "chunks");
        if (chunks.Count == 0) chunks = new List<string> { Flat(cut.Text) };
        var mode = chunks.Count < 2 ? "single" : S(Pm, "mode") ?? "single";
        var ph = D(Pm, "pitch") * J.DEG;
        var dir = D(Pm, "dir", 1);
        var h = Math.Min(W * (mode == "pair" ? 0.27 : 0.3), Math.Min(H * 0.285, u * 0.36));
        var cx = W / 2.0;
        var cy = H / 2 + h * Math.Sin(ph) * 0.55;
        var pc = PlateCol(sc, S(Pm, "face") == "accent"
            ? new[] { sc.Str("accent"), sc.Str("ink") }
            : new[] { sc.Str("ink"), sc.Str("fg"), sc.Str("accent") });
        var tc = OnCol(sc, pc);
        var topC = S(Pm, "top") == "accent" && J.Contrast(sc.Str("accent") ?? "", pc) > 1.3
            ? sc.Str("accent")! : Shade(sc, pc, 0.22);
        var outA = Tout(env);
        var pop = J.E.OutBack(J.Clamp(lt / 0.4), 1.3) * (1 - 0.15 * J.E.InCubic(env.POut));
        double th;
        double seg = 0;
        var k = chunks.Count;
        var spinIn = 1 - J.E.OutBack(J.Clamp(lt / 0.8), 1.1);
        var hold = Math.Sin(env.Ltb * 0.7) * D(Pm, "sway") * J.DEG * env.Fx.Motion;
        var away = J.E.InCubic(env.POut) * 70 * J.DEG * dir;
        var segT = new List<double>();
        if (mode == "turn")
        {
            var T = cut.Dur / k;
            for (var j = 0; j < k; j++) segT.Add(j * T);
            var acc = 0.0;
            for (var j = 1; j < k; j++) acc += J.E.InOutCubic(J.Clamp((lt - segT[j] + 0.25) / 0.5));
            seg = acc;
            th = -(acc * Math.PI / 2) - 20 * J.DEG - spinIn * 90 * J.DEG;
            th *= dir;
        }
        else if (mode == "pair") th = dir * (-45 * J.DEG) + spinIn * 100 * J.DEG * dir;
        else th = dir * (-24 * J.DEG) + spinIn * 100 * J.DEG * dir;
        th += hold + away;
        var hs = h * pop;
        if (hs < 1 || outA <= 0)
        {
            env.Body = null;
            return;
        }
        var faces = CubeFaces(cx, cy, hs, th, ph);
        // 接触阴影：把看不见的底面往下推一点
        var bm = (double[])faces[4].M.Clone();
        bm[5] = cy + hs * Math.Cos(ph) + hs * 0.14;
        env.Poly(PolyPts(bm, new[]
        {
            new[] { -hs * 1.04, -hs * 1.04 }, new[] { hs * 1.04, -hs * 1.04 },
            new[] { hs * 1.04, hs * 1.04 }, new[] { -hs * 1.04, hs * 1.04 },
        }), DarkOf(sc), 0.35 * outA, false);
        var lw = Math.Max(1.2, u * 0.0022);
        BBox? bb = null;
        double[][] Sq(double s) => new[]
        {
            new[] { -s, -s }, new[] { s, -s }, new[] { s, s }, new[] { -s, s },
        };
        // 哪个面贴哪块字
        int FaceOf(int f)
        {
            if (mode == "turn")
            {
                var idx = dir > 0 ? f : (4 - f) % 4;
                return idx < k ? idx : -1;
            }
            if (mode == "pair")
                return dir > 0 ? (f == 0 ? 0 : f == 1 ? 1 : -1) : (f == 3 ? 0 : f == 0 ? 1 : -1);
            return f == 0 ? 0 : -1;
        }

        var font = S(Pm, "font")!;
        foreach (var F in faces)
        {
            if (F.Vis <= 0.004) continue;
            var light = F.Top ? 0.28 : (-0.55 * F.Nx * dir + 0.55 * F.Nz) * 0.35;
            var col = F.Top ? topC : Shade(sc, pc, light);
            var pts = PolyPts(F.M, Sq(hs));
            env.Poly(pts, col, outA, GIn(env));
            Outline(env, pts, J.Mix(col, DarkOf(sc), 0.35), lw, outA);
            if (F.Vis < 0.08) continue;
            var lm = (double[])F.M.Clone();
            // 面内局部坐标按面尺寸 2h 未缩放绘制，pop 缩放并进矩阵
            var s0 = hs / h;
            lm[0] *= s0;
            lm[1] *= s0;
            lm[2] *= s0;
            lm[3] *= s0;
            if (F.Top)
            {
                var lab = "No." + LineNo(env);
                DrawAffPlain(env, new JObject
                {
                    ["text"] = lab,
                    ["font"] = MonoF(env),
                    ["size"] = h * 0.2,
                    ["x"] = 0,
                    ["y"] = 0,
                    ["track"] = 0.2,
                    ["color"] = OnCol(sc, topC),
                    ["alpha"] = outA * 0.9,
                    ["ghost"] = false,
                }, lm);
                continue;
            }
            var ci = FaceOf(F.F);
            if (ci >= 0 && ci < chunks.Count)
            {
                var t = Brk(chunks[ci], mode == "single" ? (IsPort(env) ? 4 : 5) : 4);
                var size = Math.Min(JLayouts.FitSize(t, font, h * 1.64, h * 1.5, new JObject { ["lead"] = 1.12, ["track"] = 0.02 }), h * 0.9);
                var arrive = mode == "turn" ? segT[ci] + (ci != 0 ? 0.05 : 0.12) : 0.18 + ci * 0.12;
                var r = DrawAff(env, new JObject
                {
                    ["text"] = t,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = 0,
                    ["y"] = 0,
                    ["lead"] = 1.12,
                    ["track"] = 0.02,
                    ["color"] = tc,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, arrive),
                }, lm);
                bb = JLayouts.UnionBb(bb, r);
            }
            else if (mode == "single" && F.F == (dir > 0 ? 1 : 3))
            {
                var t = Brk(AltCopy(env), 6);
                var fs = Math.Min(JLayouts.FitSize(t, BodyF(env), h * 1.5, h * 1.2, new JObject { ["lead"] = 1.4, ["track"] = 0.08 }), h * 0.2);
                DrawAffPlain(env, new JObject
                {
                    ["text"] = t,
                    ["font"] = BodyF(env),
                    ["size"] = fs,
                    ["x"] = 0,
                    ["y"] = 0,
                    ["lead"] = 1.4,
                    ["track"] = 0.08,
                    ["color"] = tc,
                    ["alpha"] = 0.75 * outA * Tin(env, 0.3, 0.4),
                    ["ghost"] = false,
                }, lm);
            }
            else if (mode == "single" && F.F == (dir > 0 ? 3 : 1))
            {
                DrawAffPlain(env, new JObject
                {
                    ["text"] = LineNo(env),
                    ["font"] = font,
                    ["size"] = h * 1.1,
                    ["x"] = 0,
                    ["y"] = 0,
                    ["fill"] = false,
                    ["stroke"] = Math.Max(1.5, h * 0.012),
                    ["strokeColor"] = tc,
                    ["alpha"] = 0.5 * outA,
                    ["ghost"] = false,
                }, lm);
            }
        }
        env.Body = bb ?? Box(cx - hs * 1.4, cy - hs * 1.6, cx + hs * 1.4, cy + hs * 1.2);
    }

    // ---------------- cylinder（源 L416-574） ----------------

    private static void DrawCylinder(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var t0 = Flat(cut.Text);
        var n = JLayouts.GlyphCount(t0);
        var rowsT = n > (port ? 7 : 9) ? ChunksK(t0, 2) : new List<string> { t0 };
        var R = Math.Min(W * (port ? 0.38 : 0.3), H * 0.42);
        var cx = W / 2.0;
        var ek = D(Pm, "tilt");
        var font = S(Pm, "font")!;
        var fsF = S(Pm, "fs")!;
        const double span = 2.1; // 歌词在正面最多占的弧度
        var size = H * 0.2;
        foreach (var r in rowsT)
        {
            var a = 0.0;
            foreach (var ch in JLayouts.CodePoints(r)) a += Adv(font, ch) * 1.04;
            size = Math.Min(size, span * R / Math.Max(1, a));
        }
        size = Math.Min(size, u * (rowsT.Count > 1 ? 0.15 : 0.19));
        var rowGap = size * 1.45;
        var nR = rowsT.Count;
        var outA = Tout(env);
        var dir = D(Pm, "dir", 1);
        var mot = env.Fx.Motion;
        var spin = -(1 - J.E.OutCubic(J.Clamp(lt / 0.8))) * 1.7 * dir
                   + Math.Sin(env.Ltb * 0.8) * 0.05 * mot + J.E.InCubic(env.POut) * 1.9 * dir;
        // 主行上下的次要环
        var ss = Math.Max(12, size * 0.3);
        var unit = JLayouts.CodePoints(Flat(string.IsNullOrEmpty(cut.LineText) ? cut.Text : cut.LineText) + S(Pm, "sep"));
        var nSec = I(Pm, "rings");
        var yMid = H / 2.0;
        var ringY = new List<(double Y, double Dir, int K)>();
        var blockH = nR * rowGap;
        for (var i = 0; i < nSec; i++)
        {
            var side = i % 2 != 0 ? 1 : -1;
            var k = i / 2;
            ringY.Add((yMid + side * (blockH / 2 + ss * 0.9 + k * ss * 1.6), (i % 2 != 0 ? 1 : -1) * dir, k));
        }
        var yTop = Math.Min(yMid - blockH / 2, ringY.Min(r => r.Y - ss * 0.8)) - ek * R - size * 0.12;
        var yBot = Math.Max(yMid + blockH / 2, ringY.Max(r => r.Y + ss * 0.8)) - ek * R + size * 0.12;
        var ea = Tin(env, 0, 0.6, J.E.OutCubic) * outA;

        double[][] Ell(double y, double a0, double a1, int N = 40)
        {
            var pts = new double[N + 1][];
            for (var i = 0; i <= N; i++)
            {
                var t = a0 + (a1 - a0) * i / N;
                pts[i] = new[] { cx + R * Math.Sin(t), y + ek * R * Math.Cos(t) };
            }
            return pts;
        }

        // 玻璃筒身
        if (B(Pm, "glass") && ea > 0)
        {
            var lw = Math.Max(1, u * 0.0016);
            env.Line(Ell(yTop, -Math.PI, Math.PI, 60), sc.Str("sub"), lw, 0.45 * ea, false);
            env.Line(Ell(yBot, -Math.PI / 2, Math.PI / 2), sc.Str("sub"), lw, 0.45 * ea, false);
            env.Line(new[] { new[] { cx - R, yTop }, new[] { cx - R, J.Lerp(yTop, yBot, ea) } }, sc.Str("sub"), lw, 0.45 * ea, false);
            env.Line(new[] { new[] { cx + R, yTop }, new[] { cx + R, J.Lerp(yTop, yBot, ea) } }, sc.Str("sub"), lw, 0.45 * ea, false);
        }
        // 歌词后面的包裹带
        var bandC = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("ink") });
        var onBand = B(Pm, "band");
        if (onBand && ea > 0)
        {
            var yb0 = yMid - blockH / 2 + size * 0.02 - ek * R;
            var yb1 = yMid + blockH / 2 - size * 0.02 - ek * R;
            var top = Ell(yb0, -Math.PI / 2, Math.PI / 2, 36);
            var bot = Rev(Ell(yb1, -Math.PI / 2, Math.PI / 2, 36));
            var pts = top.Concat(bot).ToArray();
            if (env.Pass == "main")
            {
                using var sh = SKShader.CreateLinearGradient(
                    new SKPoint((float)(cx - R), 0), new SKPoint((float)(cx + R), 0),
                    new[] { SkRgba(Shade(sc, bandC, -0.5), 1), SkRgba(bandC, 1), SkRgba(Shade(sc, bandC, 0.12), 1), SkRgba(Shade(sc, bandC, -0.55), 1) },
                    new float[] { 0, 0.35f, 0.55f, 1 }, SKShaderTileMode.Clamp);
                using var path = new SKPath();
                path.MoveTo((float)pts[0][0], (float)pts[0][1]);
                for (var i = 1; i < pts.Length; i++) path.LineTo((float)pts[i][0], (float)pts[i][1]);
                path.Close();
                env.Ctx.Save();
                env.Ctx.ClipPath(path);
                using var p = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)Math.Round(J.Clamp(ea) * 255)), Shader = sh };
                env.Ctx.DrawRect(new SKRect((float)(cx - R - 2), (float)(yb0 - 4), (float)(cx + R + 2), (float)(yb1 + ek * R * 2 + 4)), p);
                env.Ctx.Restore();
            }
            else env.Poly(pts, bandC, ea, GIn(env));
        }
        // 次要环：一环一个 item，字符绕鼓排，后半圈镜像且变暗
        if (env.Pass == "main")
        {
            foreach (var rg in ringY)
            {
                var a = Tin(env, 0.05 + rg.K * 0.08, 0.5, J.E.OutCubic) * outA;
                if (a <= 0.01) continue;
                var cnt = Math.Min(72, Math.Max(unit.Count, (int)Math.Floor(J.TAU * R / (ss * 1.05))));
                var sb = new StringBuilder();
                for (var i = 0; i < cnt; i++) sb.Append(unit[i % unit.Count]);
                var rot0 = env.Ltb * D(Pm, "speed") * rg.Dir + rg.K * 0.7 + spin * 0.5;
                var it = new JObject
                {
                    ["text"] = sb.ToString(),
                    ["font"] = fsF,
                    ["size"] = ss,
                    ["x"] = 0,
                    ["y"] = 0,
                    ["color"] = sc.Str("sub"),
                    ["ghost"] = false,
                    ["alpha"] = a,
                };
                var rgY = rg.Y;
                JText.DrawItem(env, it, new JText.TextDrawOpts
                {
                    HideText = env.HideText,
                    GlyphLog = env.GlyphLog,
                    AllowFilter = env.AllowFilter,
                    CharFn = (i, g, _) =>
                    {
                        var t = rot0 + i / (double)cnt * J.TAU;
                        var c = Math.Cos(t);
                        var s = Math.Sin(t);
                        var x = cx + R * s;
                        var y = rgY - ek * R + ek * R * c;
                        if (Math.Abs(c) < 0.06) return new CharXf { Hide = true };
                        var gx = g != null ? (double)g["x"]! : 0;
                        var gy = g != null ? (double)g["y"]! : 0;
                        return new CharXf
                        {
                            Dx = x - gx,
                            Dy = y - gy,
                            Sx = c,
                            Rot = Math.Atan2(-ek * s, 1) / J.DEG * 0.8,
                            A = c > 0 ? 0.45 + 0.55 * c : 0.12 + 0.1 * -c,
                        };
                    },
                });
            }
        }
        // 鼓面正面的歌词行
        BBox? bb = null;
        for (var ri = 0; ri < rowsT.Count; ri++)
        {
            var row = rowsT[ri];
            var y0 = yMid + (ri - (nR - 1) / 2.0) * rowGap;
            var chars = JLayouts.CodePoints(row);
            var ads = chars.Select(ch => Adv(font, ch) * size * 1.04).ToList();
            var tot = ads.Sum();
            var acc = -tot / 2;
            for (var i = 0; i < chars.Count; i++)
            {
                var ch = chars[i];
                var a0 = acc + ads[i] / 2;
                acc += ads[i];
                if (ch == " ") continue;
                var t = a0 / R + spin + (ri % 2 != 0 ? 0.08 : -0.08) * (nR > 1 ? 1 - J.E.OutCubic(J.Clamp(lt / 0.9)) : 0);
                var c = Math.Cos(t);
                var s = Math.Sin(t);
                if (c < 0.03) continue;
                var x = cx + R * s;
                var y = y0 - ek * R + ek * R * c;
                var m = new[] { c, -ek * s, 0.0, 1.0, x, y };
                var it = new JObject
                {
                    ["text"] = ch,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = 0,
                    ["y"] = 0,
                    ["color"] = onBand ? OnCol(sc, bandC) : sc.Str("fg"),
                    ["alpha"] = 0.35 + 0.65 * Math.Pow(c, 0.6),
                    ["mi"] = ri * 4 + i * 0.6,
                    ["noHold"] = true,
                };
                bb = JLayouts.UnionBb(bb, DrawAff(env, it, m));
            }
        }
        env.Body = bb ?? Box(cx - R, yMid - blockH / 2, cx + R, yMid + blockH / 2);
    }

    // ---------------- flipCards（源 L576-725） ----------------

    private readonly record struct CardFace(bool Up, double C, double Sc2, double Hs);

    private static void DrawFlipCards(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var rowsC = RowsOf(cut.Text, port ? 4 : 7);
        var cards = new List<(string Ch, int R, int J, int Cnt)>();
        for (var r = 0; r < rowsC.Count; r++)
            for (var j = 0; j < rowsC[r].Count; j++)
            {
                var ch = rowsC[r][j];
                if (ch != " ") cards.Add((ch, r, j, rowsC[r].Count));
            }
        var n = cards.Count;
        if (n == 0)
        {
            env.Body = null;
            return;
        }
        var rowsL = rowsC.Count;
        var per = rowsC.Max(r => r.Count);
        var extra = B(Pm, "table") ? 1 : 0;
        var rows = rowsL + extra * 2;
        const double asp = 1.38;
        const double gap = 0.16;
        var k = Math.Min(W * 0.86 / (per + (per - 1) * gap),
            Math.Min(H * (B(Pm, "table") ? 0.86 : 0.7) / (rows * asp + (rows - 1) * gap), u * 0.26));
        var cw = k;
        var chh = k * asp;
        var sx = k * (1 + gap);
        var sy = chh + k * gap;
        var outA = Tout(env);
        var faceC = LightOf(sc);
        var faceT = DarkOf(sc);
        var backCands = (S(Pm, "back") == "accent"
                ? new[] { sc.Str("accent"), sc.Str("ink") }
                : new[] { sc.Str("ink"), sc.Str("accent") })
            .Concat(new[] { sc.Str("accent2"), sc.Str("sub"), DarkOf(sc) }).ToList();
        var backC = backCands.FirstOrDefault(c => !string.IsNullOrEmpty(c)
            && J.Contrast(c!, faceC) >= 1.6 && J.Contrast(c!, sc.Str("bg") ?? "#000000") >= 1.25)
            ?? J.Mix(faceC, DarkOf(sc), 0.6);
        var backL = J.Mix(backC, OnCol(sc, backC), 0.35);
        var fd = J.Clamp(cut.Dur * 0.12, 0.22, 0.36);
        var gapT = J.Clamp(cut.Dur * 0.34 / n, 0.04, 0.12);
        var rank = Enumerable.Range(0, n).ToList();
        if (S(Pm, "order") == "random") rank = rank.OrderBy(v => J.R(s, v, 31)).ToList();
        var ord = new int[n];
        for (var q = 0; q < rank.Count; q++) ord[rank[q]] = q;
        var lw = Math.Max(1, k * 0.012);
        var rr = k * 0.08;

        // p：0 背面朝上 … 1 正面朝上，卡片绕竖直轴翻
        CardFace DrawCard(double x, double y, double p, string? ch, bool isFace, int key, double alpha, double lift)
        {
            var ang = p * Math.PI;
            var c = Math.Cos(ang);
            var sn = Math.Sin(ang);
            var w = cw * Math.Max(0.02, Math.Abs(c));
            var sc2 = 1 + 0.1 * sn * lift;
            var hL = chh * sc2 * (1 + 0.1 * sn * (c > 0 ? 1 : -1));
            var hR = chh * sc2 * (1 - 0.1 * sn * (c > 0 ? 1 : -1));
            var ws = w * sc2;
            var pts = new[]
            {
                new[] { x - ws / 2, y - hL / 2 }, new[] { x + ws / 2, y - hR / 2 },
                new[] { x + ws / 2, y + hR / 2 }, new[] { x - ws / 2, y + hL / 2 },
            };
            var up = p >= 0.5;
            env.Poly(pts.Select(pt => new[] { pt[0] + k * 0.05 * (1 + sn * 1.5), pt[1] + k * 0.07 * (1 + sn * 1.5) }).ToArray(),
                DarkOf(sc), 0.25 * alpha, false);
            if (up)
            {
                env.Poly(pts, faceC, alpha, GIn(env));
                Outline(env, pts, J.Mix(faceC, faceT, 0.25), lw, alpha);
            }
            else
            {
                env.Poly(pts, backC, alpha, GIn(env));
                var ins = k * 0.09;
                var iw = Math.Max(0, ws - ins * 2 * Math.Abs(c));
                if (iw > 2)
                {
                    var ih = chh * sc2 - ins * 2;
                    env.RRect(x - iw / 2, y - ih / 2, iw, ih, rr * 0.6, null, alpha, false, backL, lw);
                    var d = Math.Min(iw, ih) * 0.28;
                    env.Poly(new[]
                    {
                        new[] { x, y - d * 1.2 }, new[] { x + d * Math.Abs(c) * 0.9, y },
                        new[] { x, y + d * 1.2 }, new[] { x - d * Math.Abs(c) * 0.9, y },
                    }, backL, alpha, false);
                }
            }
            return new CardFace(up, c, sc2, (hL + hR) / 2 / chh);
        }

        BBox? bb = null;
        // 诱饵牌行，背面朝上
        if (B(Pm, "table"))
        {
            var ta = Tin(env, 0, 0.4, J.E.OutCubic) * (1 - J.E.InCubic(J.Clamp((env.POut - 0.4) / 0.6)));
            foreach (var rr0 in new[] { -1, rowsL })
                for (var j = 0; j < per; j++)
                {
                    var x = W / 2 + (j - (per - 1) / 2.0) * sx;
                    var y = H / 2 + (rr0 - (rowsL - 1) / 2.0) * sy;
                    var p = 0.0;
                    string? ch = null;
                    if (B(Pm, "peek"))
                    {
                        // 偶尔一张诱饵偷看：翻上来亮个随机字符再翻回去
                        const double T = 1.3;
                        var kk = (int)Math.Floor(env.Ltb / T);
                        var f = env.Ltb / T - kk;
                        if (kk >= 1 && (int)(J.H(s, kk, 5) % (uint)(per * 2)) == j + (rr0 < 0 ? 0 : per))
                        {
                            p = f < 0.25 ? J.E.InOutCubic(f / 0.25) : f < 0.6 ? 1 : 1 - J.E.InOutCubic((f - 0.6) / 0.25);
                            ch = J.H(s, kk, 6) % 2 != 0 ? "？" : "★";
                        }
                    }
                    var r = DrawCard(x, y, p, ch, false, 0, ta * 0.8, 1);
                    if (r.Up && ch != null)
                        env.DrawItem(new JObject
                        {
                            ["text"] = ch,
                            ["font"] = S(Pm, "font"),
                            ["size"] = k * 0.46,
                            ["x"] = x,
                            ["y"] = y,
                            ["sx"] = Math.Abs(r.C) * r.Sc2,
                            ["sy"] = r.Sc2,
                            ["color"] = sc.Str("accent") == faceC ? faceT : sc.Str("accent"),
                            ["alpha"] = ta * 0.8,
                            ["ghost"] = false,
                        });
                }
        }
        for (var i = 0; i < cards.Count; i++)
        {
            var cd = cards[i];
            var ch = cd.Ch;
            var x = W / 2 + (cd.J - (cd.Cnt - 1) / 2.0) * sx;
            var y = H / 2 + (cd.R - (rowsL - 1) / 2.0) * sy;
            var ti = 0.12 + ord[i] * gapT;
            var p = J.E.InOutCubic(J.Clamp((lt - ti) / fd));
            var po = J.Clamp(env.POut * 1.5 - ord[i] / (double)Math.Max(1, n) * 0.5);
            if (po > 0) p = Math.Min(p, 1 - J.E.InOutCubic(po));
            var appear = Tin(env, ord[i] * 0.02, 0.25, J.E.OutCubic) * (1 - J.E.InCubic(J.Clamp((env.POut - 0.55) / 0.45)));
            var r2 = DrawCard(x, y, p, ch, true, i, appear, 1);
            if (!r2.Up) continue;
            var m = new[] { Math.Abs(r2.C) * r2.Sc2, 0.0, 0.0, r2.Sc2 * r2.Hs, x, y };
            if (B(Pm, "idx") && Math.Abs(r2.C) > 0.3)
            {
                var isz = k * 0.15;
                DrawAffPlain(env, new JObject
                {
                    ["text"] = ch,
                    ["font"] = S(Pm, "font"),
                    ["size"] = isz,
                    ["x"] = -cw * 0.34,
                    ["y"] = -chh * 0.36,
                    ["color"] = sc.Str("accent"),
                    ["ghost"] = false,
                }, m);
                DrawAffPlain(env, new JObject
                {
                    ["text"] = ch,
                    ["font"] = S(Pm, "font"),
                    ["size"] = isz,
                    ["x"] = cw * 0.34,
                    ["y"] = chh * 0.36,
                    ["rot"] = 180,
                    ["color"] = sc.Str("accent"),
                    ["ghost"] = false,
                }, m);
            }
            bb = JLayouts.UnionBb(bb, DrawAff(env, new JObject
            {
                ["text"] = ch,
                ["font"] = S(Pm, "font"),
                ["size"] = k * 0.6,
                ["x"] = 0,
                ["y"] = 0,
                ["color"] = faceT,
                ["noHold"] = PlateHold(env),
                ["mi"] = MiAt(env, ti + fd * 0.5),
            }, m));
        }
        var bw = per * sx;
        var bh = rowsL * sy;
        env.Body = bb ?? Box(W / 2 - bw / 2, H / 2 - bh / 2, W / 2 + bw / 2, H / 2 + bh / 2);
    }

    // ---------------- accordion（源 L727-822） ----------------

    private static void DrawAccordion(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var n = JLayouts.GlyphCount(cut.Text);
        if (n == 0)
        {
            env.Body = null;
            return;
        }
        var port = IsPort(env);
        var vert = S(Pm, "orient") == "v" && port && n <= 10;
        var outA = Tout(env);
        var open = J.E.OutBack(J.Clamp((lt - 0.02) / 0.7), 1.25);
        var mot = env.Fx.Motion;
        var fold = D(Pm, "fold");
        var alpha = fold + (1 - open) * (86 - fold) + Math.Sin(env.Ltb * 2.1) * D(Pm, "breathe") * mot * J.Clamp(lt / 0.8)
                    + J.E.InCubic(env.POut) * (88 - fold);
        alpha = J.Clamp(alpha, 2, 88.5) * J.DEG;
        var ca = Math.Cos(alpha);
        var sa = Math.Sin(alpha);
        var rowsA = RowsOf(cut.Text, vert ? 10 : port ? 5 : 9);
        var rows = rowsA.Count;
        var per = rowsA.Max(r => r.Count);
        var cA = Math.Cos(fold * J.DEG);
        // 由静息角算展开面板的尺寸
        var Pw = Math.Min((vert ? H * 0.82 : W * 0.86) / (per * cA + 0.5),
            Math.Min((vert ? W * 0.5 : H * 0.62 / rows) / 1.25, u * 0.3));
        var Ph = Pw * 1.2;
        var plate = S(Pm, "plate");
        var pc = PlateCol(sc, plate == "accent"
            ? new[] { sc.Str("accent"), sc.Str("ink") }
            : plate == "fg" ? new[] { sc.Str("fg"), sc.Str("ink") } : new[] { sc.Str("ink"), sc.Str("fg") });
        var tc = OnCol(sc, pc);
        var lit = Shade(sc, pc, 0.1);
        var dim = Shade(sc, pc, -0.32);
        var covPlate = PlateCol(sc, new[] { sc.Str("accent"), sc.Str("ink"), sc.Str("fg") });
        var covC = covPlate == pc ? Shade(sc, pc, -0.45) : covPlate;
        const double kap = 0.13;
        BBox? bb = null;
        var lw = Math.Max(1, u * 0.0016);
        var gi = 0;
        var font = S(Pm, "font")!;
        var startPos = D(Pm, "start") > 0;
        for (var r = 0; r < rows; r++)
        {
            var row = rowsA[r];
            var cnt = row.Count;
            var pw = Pw * ca;
            var tot = cnt * pw;
            var cy = (vert ? W : H) / 2.0 + (r - (rows - 1) / 2.0) * Ph * 1.3;
            var c0 = (vert ? H : W) / 2.0 - tot / 2;
            double EdgeH(int j) => Ph * (1 + kap * sa * ((j + (startPos ? 0 : 1)) % 2 != 0 ? -1 : 1));
            // (along, across) 映到屏幕
            double[] P2(double a, double b) => vert ? new[] { b, a } : new[] { a, b };
            // 条带下的影子
            var sh = new List<double[]>();
            for (var j = 0; j <= cnt; j++) sh.Add(P2(c0 + j * pw + Ph * 0.06, cy + EdgeH(j) / 2 + Ph * 0.07));
            for (var j = cnt; j >= 0; j--) sh.Add(P2(c0 + j * pw + Ph * 0.06, cy + EdgeH(j) / 2 + Ph * 0.02));
            env.Poly(sh.ToArray(), DarkOf(sc), 0.3 * outA, false);
            for (var j = 0; j < cnt; j++)
            {
                var a0 = c0 + j * pw;
                var a1 = a0 + pw;
                var h0 = EdgeH(j);
                var h1 = EdgeH(j + 1);
                var q = J.Clamp((lt - j * 0.03) / 0.2) * outA;
                if (q <= 0) continue;
                var facing = h1 > h0 ? 1 : -1; // 面板朝光/背光
                var col = facing > 0 ? dim : lit;
                var pts = new[] { P2(a0, cy - h0 / 2), P2(a1, cy - h1 / 2), P2(a1, cy + h1 / 2), P2(a0, cy + h0 / 2) };
                env.Poly(pts, col, q, GIn(env));
                env.Line(new[] { P2(a0, cy - h0 / 2), P2(a0, cy + h0 / 2) }, J.Mix(col, DarkOf(sc), 0.4), lw, q, false);
                var ch = row[j];
                if (ch == " ") continue;
                var mid = P2((a0 + a1) / 2, cy);
                var hs = (h0 + h1) / 2 / Ph;
                var m = vert ? new[] { hs, 0.0, 0.0, ca, mid[0], mid[1] } : new[] { ca, 0.0, 0.0, hs, mid[0], mid[1] };
                var size = Math.Min(Pw * 0.66, Ph * 0.62);
                var mi = MiAt(env, 0.12 + gi++ * 0.035);
                bb = JLayouts.UnionBb(bb, DrawAff(env, new JObject
                {
                    ["text"] = ch,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = 0,
                    ["y"] = 0,
                    ["color"] = tc,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = mi,
                }, m));
            }
            if (B(Pm, "covers"))
            {
                var cq = Tin(env, 0, 0.3, J.E.OutCubic) * outA;
                var cw2 = Ph * 0.09;
                var chh = Ph * 1.16;
                foreach (var (a, hh) in new[] { (c0 - cw2, EdgeH(0)), (c0 + tot, EdgeH(cnt)) })
                {
                    var pts = new[]
                    {
                        P2(a, cy - chh / 2 * hh / Ph), P2(a + cw2, cy - chh / 2 * hh / Ph),
                        P2(a + cw2, cy + chh / 2 * hh / Ph), P2(a, cy + chh / 2 * hh / Ph),
                    };
                    env.Poly(pts, covC, cq, GIn(env));
                }
            }
        }
        env.Body = bb;
    }

    // ---------------- flag（源 L824-973） ----------------

    private sealed class FlagSample
    {
        public double U, X, Top, Bot, Sl, Hs;
    }

    private static void DrawFlag(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var side = D(Pm, "side") is var sd && sd != 0 ? sd : 1;
        var t0 = Flat(cut.Text);
        var n = JLayouts.GlyphCount(t0);
        var text = Brk(t0, port ? 5 : n <= 5 ? 5 : Math.Max(4, (int)Math.Ceiling(n / 2.0)));
        var Wf = W * (port ? 0.78 : 0.62);
        var Hf = Math.Min(H * (port ? 0.34 : 0.52), Wf * 0.64);
        var poleX = side > 0 ? W / 2 - Wf / 2 - W * 0.03 : W / 2 + Wf / 2 + W * 0.03;
        var outA = Tout(env);
        var mot = env.Fx.Motion;
        var mode = S(Pm, "mode");
        var unf = mode == "unfurl" ? J.E.OutCubic(J.Clamp((lt - 0.05) / 0.75)) : 1;
        var raise = mode == "raise" ? J.E.OutCubic(J.Clamp(lt / 0.8)) : 1;
        var lower = mode == "raise" ? J.E.InCubic(env.POut) : 0;
        var furl = mode == "unfurl" ? J.E.InCubic(env.POut) : 0;
        var y0 = H / 2 - Hf / 2 - H * 0.03 + (1 - raise) * H * 0.55 + lower * H * 0.6;
        var ext = Math.Max(0.001, unf * (1 - furl));
        var wind = D(Pm, "wind");
        var A = Hf * 0.1 * wind * (0.5 + 0.7 * mot) * (1 + (1 - unf) * 1.2);
        var dep = Wf * 1.3; // JS 的 D（深度），避开本文件 D(JObject,key) 读取帮手
        const double w2 = 2.1;
        var om = 3.2 * wind;
        var tb = env.Ltb;
        double Zf(double x) => A * Math.Pow(x, 0.85) * Math.Sin(J.TAU * w2 * x * 0.5 - om * tb)
                               + A * 0.35 * x * Math.Sin(J.TAU * 1.3 * x - om * 0.7 * tb + 1.3);
        const int N = 30;
        var col = PlateCol(sc, S(Pm, "cloth") == "ink"
            ? new[] { sc.Str("ink"), sc.Str("accent") }
            : new[] { sc.Str("accent"), sc.Str("ink") });
        var tc = OnCol(sc, col);
        var trim = S(Pm, "trim");
        var trimC = J.Contrast(sc.Str("fg") ?? "#000000", col) > 1.5
            ? (col == sc.Str("accent") ? OnCol(sc, col) : sc.Str("accent")!) : sc.Str("accent")!;
        // 采样旗面
        var Sm = new List<FlagSample>();
        var xAcc = 0.0;
        for (var i = 0; i <= N; i++)
        {
            var uu = i / (double)N * ext;
            var z = Zf(uu);
            var dz = (Zf(uu + 0.01) - Zf(uu - 0.01)) / 0.02 / Wf;
            if (i > 0) xAcc += Wf * ext / N / Math.Sqrt(1 + dz * dz);
            var hs = 1 + z / dep;
            var droop = uu * uu * Hf * 0.05 - z * 0.12;
            var x = poleX + side * xAcc;
            Sm.Add(new FlagSample { U = uu, X = x, Top = y0 + droop - (hs - 1) * Hf / 2, Bot = y0 + droop + Hf * hs - (hs - 1) * Hf / 2, Sl = dz, Hs = hs });
        }
        var pa = Tin(env, 0, 0.3, J.E.OutCubic) * (mode == "raise" ? 1 - J.E.InCubic(J.Clamp((env.POut - 0.5) / 0.5)) : outA);
        var pw = Math.Max(3, u * 0.011);
        var ptop = H / 2 - Hf / 2 - H * 0.03 - Hf * 0.3;
        env.Rect(poleX - pw / 2, ptop, pw, H * 1.2, sc.Str("sub"), pa, false);
        env.Circle(poleX, ptop, pw * 1.5, sc.Str("accent"), null, 0, pa, false);
        if (unf * outA <= 0.001 || y0 > H * 1.2)
        {
            env.Body = null;
            return;
        }
        // 旗面：一个整体轮廓，燕尾带缺口，饰带和褶皱明暗都裁在轮廓内
        var sw = S(Pm, "tail") == "swallow";
        (double X, double Top, double Bot) At0(double uu)
        {
            var f = J.Clamp(uu / ext) * N;
            var i = Math.Min(N - 1, (int)Math.Floor(f));
            var t = f - i;
            var a = Sm[i];
            var b = Sm[i + 1];
            return (J.Lerp(a.X, b.X, t), J.Lerp(a.Top, b.Top, t), J.Lerp(a.Bot, b.Bot, t));
        }

        var sil = Sm.Select(q => new[] { q.X, q.Top }).ToList();
        if (sw)
        {
            var q = At0(ext * 0.86);
            sil.Add(new[] { q.X, (q.Top + q.Bot) / 2 });
        }
        for (var i = N; i >= 0; i--) sil.Add(new[] { Sm[i].X, Sm[i].Bot });
        var silArr = sil.ToArray();
        env.Poly(silArr, col, outA, false);
        if (env.Pass == "main")
        {
            using var path = new SKPath();
            path.MoveTo((float)silArr[0][0], (float)silArr[0][1]);
            for (var i = 1; i < silArr.Length; i++) path.LineTo((float)silArr[i][0], (float)silArr[i][1]);
            path.Close();
            env.Ctx.Save();
            env.Ctx.ClipPath(path);
            void Band(double v0, double v1, string c, double a)
            {
                var pts = Sm.Select(q => new[] { q.X, J.Lerp(q.Top, q.Bot, v0) }).ToList();
                for (var i = N; i >= 0; i--) pts.Add(new[] { Sm[i].X, J.Lerp(Sm[i].Top, Sm[i].Bot, v1) });
                env.Poly(pts.ToArray(), c, a, false);
            }

            if (trim == "bands")
            {
                Band(0.08, 0.15, trimC, outA);
                Band(0.85, 0.92, trimC, outA);
            }
            if (trim == "edge")
            {
                var a = Sm[0];
                var b = Sm[Math.Min(N, 3)];
                env.Poly(new[]
                {
                    new[] { a.X - side * 2, a.Top - 4 }, new[] { b.X, b.Top - 4 },
                    new[] { b.X, b.Bot + 4 }, new[] { a.X - side * 2, a.Bot + 4 },
                }, trimC, outA, false);
            }
            for (var i = 0; i < N; i++)
            {
                var a = Sm[i];
                var b = Sm[i + 1];
                var v = J.Clamp(-(a.Sl + b.Sl) / 2 * side * 0.9, -0.4, 0.2);
                if (Math.Abs(v) < 0.02) continue;
                env.Poly(new[]
                {
                    new[] { a.X, a.Top - 4 }, new[] { b.X, b.Top - 4 },
                    new[] { b.X, b.Bot + 4 }, new[] { a.X, a.Bot + 4 },
                }, v < 0 ? DarkOf(sc) : LightOf(sc), Math.Abs(v) * 0.8 * outA, false);
            }
            env.Ctx.Restore();
        }
        // 旗杆绳圈
        env.Circle(poleX + side * pw * 0.2, Sm[0].Top + Hf * 0.04, pw * 0.9, null, sc.Str("sub"), Math.Max(1, pw * 0.3), outA, false);
        env.Circle(poleX + side * pw * 0.2, Sm[0].Bot - Hf * 0.04, pw * 0.9, null, sc.Str("sub"), Math.Max(1, pw * 0.3), outA, false);
        // 印在旗面上的歌词：字形中心用旗面局部坐标 (u, v)
        var o = new JObject { ["track"] = 0.04, ["lead"] = 1.15 };
        var font = S(Pm, "font")!;
        var size = Math.Min(JLayouts.FitSize(text, font, Wf * (S(Pm, "tail") == "swallow" ? 0.66 : 0.78),
            Hf * (trim == "bands" ? 0.58 : 0.68), o), Hf * 0.42);
        var lay = JText.LayoutText(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.04, ["lead"] = 1.15 });
        var uC = (S(Pm, "tail") == "swallow" ? 0.46 : 0.52) + (trim == "edge" ? 0.03 : 0);
        BBox? bb = null;
        (double X, double Top, double Bot, double Sl, double Hs) At(double uu)
        {
            var f = J.Clamp(uu / ext) * N;
            var i = Math.Min(N - 1, (int)Math.Floor(f));
            var t = f - i;
            var a = Sm[i];
            var b = Sm[i + 1];
            return (J.Lerp(a.X, b.X, t), J.Lerp(a.Top, b.Top, t), J.Lerp(a.Bot, b.Bot, t), J.Lerp(a.Sl, b.Sl, t), J.Lerp(a.Hs, b.Hs, t));
        }

        foreach (var g in lay.Cells)
        {
            if (g.Ch is " " or "　") continue;
            var uu = uC + side * g.X / Wf;
            if (uu > ext) continue;
            var q = At(uu);
            var v = 0.5 + g.Y / Hf;
            var y = J.Lerp(q.Top, q.Bot, v);
            var cs = 1 / Math.Sqrt(1 + q.Sl * q.Sl);
            var qa = At(uu + 0.01);
            var qb = At(uu - 0.01);
            var slope = ((qa.Top + qa.Bot) - (qb.Top + qb.Bot)) / 2 / (Math.Abs(qa.X - qb.X) + 1e-3);
            var m = new[] { cs, slope * cs * side, 0.0, q.Hs, q.X, y };
            bb = JLayouts.UnionBb(bb, DrawAff(env, new JObject
            {
                ["text"] = g.Ch,
                ["font"] = font,
                ["size"] = size,
                ["x"] = 0,
                ["y"] = 0,
                ["color"] = tc,
                ["noHold"] = PlateHold(env),
                ["mi"] = MiAt(env, 0.1 + uu * 0.6),
            }, m));
        }
        env.Body = bb ?? Box(Math.Min(Sm[0].X, Sm[N].X), Sm[0].Top, Math.Max(Sm[0].X, Sm[N].X), Sm[0].Bot);
    }

    // ---------------- ribbon（源 L975-1128） ----------------

    private sealed class RibSeg
    {
        public double F, T;
        public double[] A = Array.Empty<double>(), B = Array.Empty<double>(), P = Array.Empty<double>();
        public double Ang;
    }

    private static void DrawRibbon(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var t0 = Flat(cut.Text);
        var n = JLayouts.GlyphCount(t0);
        var rowsT = n > (port ? 6 : 10) ? ChunksK(t0, n > 12 && port ? 3 : 2) : new List<string> { t0 };
        var nR = rowsT.Count;
        var outA = Tout(env);
        var colKey = S(Pm, "col");
        var pc = PlateCol(sc, colKey == "ink" ? new[] { sc.Str("ink"), sc.Str("accent") } : new[] { sc.Str("accent"), sc.Str("ink") });
        var back = Shade(sc, pc, -0.42);
        var tc = OnCol(sc, pc);
        var mot = env.Fx.Motion;
        BBox? bb = null;
        var x0 = W * 0.09;
        var x1 = W * 0.91;
        var L = x1 - x0;
        var font = S(Pm, "font")!;
        // 各行共用一个字号
        var size = u * 0.16;
        foreach (var r in rowsT)
        {
            var a = 0.0;
            foreach (var ch in JLayouts.CodePoints(r)) a += Adv(font, ch) * 1.05;
            size = Math.Min(size, L * 0.58 / Math.Max(1, a));
        }
        size = Math.Min(size, H * 0.62 / (nR * 2.3));
        var bw = size * 0.72;
        for (var ri = 0; ri < rowsT.Count; ri++)
        {
            var row = rowsT[ri];
            var cy = H / 2 + (ri - (nR - 1) / 2.0) * size * 2.35;
            var A = H * D(Pm, "amp") * (nR > 1 ? 0.55 : 1) * (1 + 0.12 * Math.Sin(env.Ltb * 1.1 + ri));
            var ph = D(Pm, "ph") + ri * 2.1 + env.Ltb * 0.9 * mot * 0.6;
            var tl = Math.Tan(D(Pm, "tilt") * J.DEG) * (ri % 2 != 0 ? -1 : 1);
            double[] Path(double x) => new[]
            {
                x, cy + A * Math.Sin(J.TAU * D(Pm, "freq") * (x - x0) / L + ph) + (x - W / 2) * tl,
            };

            // 弧长表
            const int NS = 90;
            var pts = new List<double[]>();
            var acc = new List<double> { 0 };
            for (var i = 0; i <= NS; i++) pts.Add(Path(x0 + L * i / NS));
            for (var i = 1; i <= NS; i++) acc.Add(acc[i - 1] + Hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]));
            var TL = acc[NS];
            (double[] P, double Ang) AtS(double s)
            {
                s = J.Clamp(s, 0, TL);
                int lo = 0, hi = NS;
                while (hi - lo > 1)
                {
                    var m = (lo + hi) >> 1;
                    if (acc[m] <= s) lo = m;
                    else hi = m;
                }
                var f = (s - acc[lo]) / Math.Max(1e-6, acc[hi] - acc[lo]);
                var p = new[] { J.Lerp(pts[lo][0], pts[hi][0], f), J.Lerp(pts[lo][1], pts[hi][1], f) };
                var ang = Math.Atan2(pts[hi][1] - pts[lo][1], pts[hi][0] - pts[lo][0]);
                return (p, ang);
            }

            // 扭转：中段平展，两端折起露背面
            double Tau(double f)
            {
                var d = Math.Abs(f - 0.5);
                return d < 0.33 ? 1 : Math.Cos(Math.PI * (d - 0.33) / 0.17);
            }

            var e = J.E.InOutCubic(J.Clamp((lt - ri * 0.12) / 0.75)) * (1 - J.E.InCubic(env.POut));
            bool Vis(double f) => S(Pm, "reveal") == "left" ? f <= e : Math.Abs(f - 0.5) <= e / 2;
            // 影子加缎带
            const int NB = 64;
            var seg = new List<RibSeg>();
            for (var i = 0; i <= NB; i++)
            {
                var f = i / (double)NB;
                var q = AtS(f * TL);
                var t = Tau(f);
                var nx = -Math.Sin(q.Ang);
                var ny = Math.Cos(q.Ang);
                seg.Add(new RibSeg
                {
                    F = f,
                    T = t,
                    A = new[] { q.P[0] + nx * bw * t, q.P[1] + ny * bw * t },
                    B = new[] { q.P[0] - nx * bw * t, q.P[1] - ny * bw * t },
                    P = q.P,
                    Ang = q.Ang,
                });
            }
            var sh = size * 0.1;
            var vs = seg.Where(q => Vis(q.F)).ToList();
            if (vs.Count > 1)
                env.Poly(vs.Select(q => new[] { q.A[0] + sh, q.A[1] + sh * 1.4 })
                    .Concat(Rev(vs.Select(q => new[] { q.B[0] + sh, q.B[1] + sh * 1.4 }))).ToArray(), DarkOf(sc), 0.22, false);
            // 朝向相同的连续段各画一个多边形，扭转明暗再叠上去
            var run = new List<RibSeg>();
            void Flush()
            {
                if (run.Count > 1)
                {
                    var t = run[run.Count / 2].T;
                    env.Poly(run.Select(q => q.A).Concat(Rev(run.Select(q => q.B))).ToArray(),
                        t >= 0 ? pc : back, 1, GIn(env) && t >= 0);
                    if (t >= 0)
                        env.Line(run.Where(q => q.T > 0.6).Select(q => q.A).ToArray(), Shade(sc, pc, 0.3), Math.Max(1, size * 0.02), 0.8, false);
                }
                run = new List<RibSeg>();
            }

            for (var i = 0; i <= NB; i++)
            {
                var q = seg[i];
                if (!Vis(q.F))
                {
                    Flush();
                    continue;
                }
                if (run.Count > 0 && Math.Sign(Or1(run[^1].T)) != Math.Sign(Or1(q.T)))
                {
                    var last = run[^1];
                    Flush();
                    run.Add(last);
                }
                run.Add(q);
            }
            Flush();
            for (var i = 0; i < NB; i++)
            {
                var A0 = seg[i];
                var B0 = seg[i + 1];
                if (!Vis(A0.F) || !Vis(B0.F)) continue;
                var t = (A0.T + B0.T) / 2;
                if (Math.Abs(t) > 0.97) continue;
                env.Poly(new[] { A0.A, B0.A, B0.B, A0.B }, DarkOf(sc), (1 - Math.Abs(t)) * 0.4, false);
            }
            // 两端燕尾缺口
            foreach (var end in new[] { 0, NB })
            {
                var S0 = seg[end];
                if (!Vis(S0.F)) continue;
                var dirn = end == 0 ? 1 : -1;
                var ext = new[] { -Math.Cos(S0.Ang) * bw * 1.3 * dirn, -Math.Sin(S0.Ang) * bw * 1.3 * dirn };
                var c = Shade(sc, back, -0.12);
                env.Poly(new[]
                {
                    S0.A, new[] { S0.A[0] + ext[0], S0.A[1] + ext[1] },
                    new[] { S0.P[0] + ext[0] * 0.45, S0.P[1] + ext[1] * 0.45 },
                    new[] { S0.B[0] + ext[0], S0.B[1] + ext[1] }, S0.B,
                }, c, 1, false);
            }
            // 歌词沿中段平展处排
            var chars = JLayouts.CodePoints(row);
            var ads = chars.Select(ch => Adv(font, ch) * size * 1.05).ToList();
            var tot = ads.Sum();
            var s0 = TL / 2 - tot / 2;
            for (var i = 0; i < chars.Count; i++)
            {
                var ch = chars[i];
                var sc0 = s0 + ads[i] / 2;
                s0 += ads[i];
                if (ch == " ") continue;
                var f = sc0 / TL;
                if (!Vis(f)) continue;
                var q = AtS(sc0);
                bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = ch,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = q.P[0],
                    ["y"] = q.P[1],
                    ["rot"] = q.Ang / J.DEG,
                    ["sy"] = Tau(f),
                    ["color"] = tc,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, ri * 0.12 + (S(Pm, "reveal") == "left" ? f : Math.Abs(f - 0.5) * 2) * 0.6),
                }));
            }
        }
        env.Body = bb;
    }

    // ---------------- pendulum（源 L1130-1285） ----------------

    private sealed record PendItem(string T);

    private static void DrawPendulum(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        // 帧不变量（性能专项）：字列表只由 cut 决定，按切分缓存
        var chs = CutCache.Get<List<string>>(cut, "pendulum.chs") ?? SlotsOf(cut.Text).Where(c => c != " ").ToList();
        if (CutCache.Get<List<string>>(cut, "pendulum.chs") == null) CutCache.Put(cut, "pendulum.chs", chs);
        var n = chs.Count;
        if (n == 0)
        {
            env.Body = null;
            return;
        }
        var outA = Tout(env);
        var mot = env.Fx.Motion;
        var port = IsPort(env);
        var mode = S(Pm, "mode") ?? "chunks";
        if (mode == "cradle" && n > (port ? 6 : 9)) mode = "fan";
        if (mode == "fan" && n > 13) mode = "chunks";
        var lw = Math.Max(1, u * 0.0015);
        var pc = PlateCol(sc, new[] { sc.Str("ink"), sc.Str("fg") });
        var tc = OnCol(sc, pc);
        var fa = Tin(env, 0, 0.45, J.E.OutCubic) * outA;
        BBox? bb = null;
        var font = S(Pm, "font")!;
        if (mode == "cradle")
        {
            var r = Math.Min(W * 0.84 / (n * 2.02 + 1.2), Math.Min(H * 0.13, u * 0.12));
            var L = Math.Min(H * 0.42, r * 5.2);
            var ccx = W / 2.0;
            var x0 = ccx - (n - 1) * r;
            var py = H / 2 - L * 0.62;
            var by = py + L;
            var fw = (n - 1) * r + r * 2.6;
            var fb = by + r + H * 0.06;
            var fc = sc.Str("sub");
            env.Line(new[]
            {
                new[] { ccx - fw, fb }, new[] { ccx - fw, py }, new[] { ccx + fw, py }, new[] { ccx + fw, fb },
            }, fc, Math.Max(2, u * 0.006), fa, false);
            env.RRect(ccx - fw - r * 0.7, fb, fw * 2 + r * 1.4, r * 0.28, r * 0.1, fc, fa, false);
            var T = D(Pm, "period");
            var w = J.TAU / T;
            var A = D(Pm, "amp") * J.DEG * (0.6 + 0.5 * mot);
            const double t0 = 0.75;
            var tau = env.Ltb - t0;
            var sAng = tau > 0 ? -Math.Cos(w * tau) : -1;
            var lift = tau > 0 ? 1 : J.E.InOutCubic(J.Clamp((lt - 0.25) / 0.45));
            var decay = 1 - 0.25 * J.Clamp(tau / 6);
            for (var i = 0; i < n; i++)
            {
                var ch = chs[i];
                var px = x0 + i * r * 2;
                var th = 0.0;
                if (i == 0) th = A * decay * Math.Min(0, sAng) * (tau > 0 ? 1 : lift);
                if (i == n - 1 && n > 1) th = A * decay * Math.Max(0, sAng);
                if (n == 1) th = A * decay * sAng * lift;
                var drop = J.E.OutBack(J.Clamp((lt - i * 0.05) / 0.35), 1.4);
                var Lc = L * drop;
                var bx = px + Math.Sin(th) * Lc;
                var byy = py + Math.Cos(th) * Lc;
                if (drop <= 0) continue;
                var d = r * 0.85;
                env.Line(new[] { new[] { px - d, py }, new[] { bx, byy } }, sc.Str("sub"), lw, 0.9 * outA, false);
                env.Line(new[] { new[] { px + d, py }, new[] { bx, byy } }, sc.Str("sub"), lw, 0.9 * outA, false);
                env.Circle(bx, byy, r * 0.985, pc, null, 0, outA, GIn(env));
                env.Circle(bx - r * 0.35, byy - r * 0.38, r * 0.2, LightOf(sc), null, 0, 0.35 * outA, false);
                env.Circle(bx, byy, r * 0.985, null, J.Mix(pc, DarkOf(sc), 0.4), lw, outA, false);
                // 碰撞火花
                if (tau > 0 && ((i == 0 && n > 1) || i == n - 1))
                {
                    var ph = w * tau / Math.PI % 2;
                    var near = Math.Min(Math.Abs(ph - 0.5), Math.Abs(ph - 1.5));
                    if (near < 0.08 && (i == 0) == (Math.Abs(ph - 0.5) < 0.08))
                    {
                        var k = 1 - near / 0.08;
                        var sx = px + (i == 0 ? r : -r);
                        for (var q = -1; q <= 1; q++)
                            env.Line(new[]
                            {
                                new[] { sx + (i == 0 ? r * 0.2 : -r * 0.2), byy + q * r * 0.5 },
                                new[] { sx + (i == 0 ? r * 0.55 : -r * 0.55), byy + q * r * 0.9 },
                            }, sc.Str("accent"), Math.Max(1.5, r * 0.05), k * outA, false);
                    }
                }
                bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = ch,
                    ["font"] = font,
                    ["size"] = r * 1.08,
                    ["x"] = bx,
                    ["y"] = byy,
                    ["rot"] = -th / J.DEG,
                    ["color"] = tc,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, 0.15 + i * 0.05),
                }));
            }
            env.Body = bb;
            return;
        }
        // 单支点：多根不同摆长的摆，即摆波
        var items = mode == "fan"
            ? chs.Select(c => new PendItem(c)).ToList()
            : (StrList(Pm, "chunks") is { Count: > 0 } ch0 ? ch0 : ChunksK(cut.Text, 3)).Select(c => new PendItem(Flat(c))).ToList();
        var kk = items.Count;
        var px2 = W / 2.0;
        var py2 = H * 0.06;
        double size;
        double step;
        double L0;
        var plateW = new List<double>();
        if (mode == "fan")
        {
            step = Math.Min(H * 0.84 / (kk + 1.2), W * 0.2);
            size = step * 0.84;
            L0 = step * 1.4;
        }
        else
        {
            var wMax = W * (port ? 0.8 : 0.56);
            size = Math.Min(items.Min(q => JLayouts.FitSize(q.T, font, wMax, H * 0.8 / (kk + 1) * 0.62, new JObject { ["track"] = 0.03 })), u * 0.14);
            step = size * 1.75;
            L0 = Math.Max(H * 0.9 - step * kk, size * 2);
            plateW = items.Select(q => JLayouts.Measure(new JObject { ["text"] = q.T, ["font"] = font, ["size"] = size, ["track"] = 0.03 }).W + size * 0.8).ToList();
        }
        var Lmax = L0 + step * (kk - 1);
        var A2 = (mode == "fan" ? 7 : 5) * J.DEG * (0.5 + 0.7 * mot);
        var rel = J.E.InOutCubic(J.Clamp((lt - 0.1) / 0.6));
        env.Circle(px2, py2, Math.Max(3, u * 0.008), sc.Str("accent"), null, 0, fa, false);
        env.Rect(px2 - u * 0.06 * fa, py2 - Math.Max(2, u * 0.003), u * 0.12 * fa, Math.Max(3, u * 0.005), sc.Str("sub"), fa, false);
        var side = D(Pm, "side", 1);
        var pos = new List<(double L, double Th, double Grow)>();
        for (var i = 0; i < kk; i++)
        {
            var L = L0 + step * i;
            var Ti = D(Pm, "period") * 1.6 * Math.Sqrt(L / Lmax);
            var tau = Math.Max(0, env.Ltb - 0.2);
            var th = A2 * side * Math.Cos(J.TAU * tau / Ti) * rel + J.E.InCubic(env.POut) * 30 * J.DEG * side * (0.5 + i / (double)kk);
            var grow = J.E.OutCubic(J.Clamp((lt - i * 0.04) / 0.4));
            pos.Add((L * grow, th, grow));
        }
        // 先画摆线再画摆锤，摆线才不会切到字形
        for (var i = 0; i < kk; i++)
        {
            var q = pos[i];
            if (q.Grow > 0)
                env.Line(new[]
                {
                    new[] { px2, py2 },
                    new[] { px2 + Math.Sin(q.Th) * q.L, py2 + Math.Cos(q.Th) * q.L - (mode == "fan" ? size * 0.55 : size * 0.62) },
                }, sc.Str("sub"), lw, 0.75 * outA, false);
        }
        for (var i = 0; i < kk; i++)
        {
            var q = items[i];
            var p = pos[i];
            if (p.Grow <= 0) continue;
            var bx = px2 + Math.Sin(p.Th) * p.L;
            var byy = py2 + Math.Cos(p.Th) * p.L;
            var col = i % 2 != 0 && J.Contrast(sc.Str("accent") ?? "#000000", sc.Str("bg") ?? "#000000") > 1.6 ? sc.Str("accent")! : pc;
            if (mode == "fan") env.Circle(bx, byy, size * 0.62, col, null, 0, outA, GIn(env));
            else
            {
                var w2 = plateW[i];
                var h2 = size * 1.24;
                env.Ctx.Save();
                env.Ctx.Translate((float)bx, (float)byy);
                env.Ctx.RotateRadians((float)-p.Th);
                env.RRect(-w2 / 2, -h2 / 2, w2, h2, h2 * 0.14, col, outA, GIn(env));
                env.Circle(0, -h2 / 2 + h2 * 0.12, Math.Max(2, size * 0.06), sc.Str("bg"), null, 0, outA, false);
                env.Ctx.Restore();
            }
            bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
            {
                ["text"] = q.T,
                ["font"] = font,
                ["size"] = mode == "fan" ? size * 0.78 : size,
                ["track"] = 0.03,
                ["x"] = bx,
                ["y"] = byy + (mode == "fan" ? 0 : size * 0.06),
                ["rot"] = -p.Th / J.DEG,
                ["color"] = OnCol(sc, col),
                ["noHold"] = PlateHold(env),
                ["mi"] = MiAt(env, 0.12 + i * 0.05),
            }));
        }
        env.Body = bb;
    }

    // ---------------- pile（源 L1287-1442） ----------------

    private static void DrawPile(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var t0 = Flat(cut.Text);
        var n = JLayouts.GlyphCount(t0);
        if (n == 0)
        {
            env.Body = null;
            return;
        }
        var rowsT = n > (port ? 5 : 8) ? ChunksK(t0, port ? (int)Math.Ceiling(n / 5.0) : 2) : new List<string> { t0 };
        var nR = rowsT.Count;
        var outA = Tout(env);
        var font = S(Pm, "font")!;
        var size = Math.Min(u * 0.2, H * 0.5 / (nR + 0.6));
        foreach (var r in rowsT)
        {
            var a = 0.0;
            foreach (var ch in JLayouts.CodePoints(r)) a += Adv(font, ch) * 1.02;
            size = Math.Min(size, W * (port ? 0.86 : 0.74) / Math.Max(1, a));
        }
        var maxTot = 0.0;
        foreach (var r in rowsT)
        {
            var a = 0.0;
            foreach (var ch in JLayouts.CodePoints(r)) a += Adv(font, ch) * 1.02 * size;
            maxTot = Math.Max(maxTot, a);
        }
        var cx = J.Clamp(W * (0.5 + D(Pm, "peak") * (port ? 0.2 : 0.6)), W * 0.06 + maxTot / 2, W * 0.94 - maxTot / 2);
        var sig = Math.Max(W * 0.26, maxTot * 0.68);
        var yBase = H * 1.04;
        var Hm = yBase - (H * (0.5 + D(Pm, "hgt") * 0.25) + nR * size * 0.49);
        var rise = (1 - J.E.OutCubic(J.Clamp(lt / 0.5))) * Hm * 1.1 + J.E.InCubic(env.POut) * Hm * 0.5;
        double Bump(double x) => Math.Sin(x / W * 17 + s % 7) * H * 0.006 + Math.Sin(x / W * 31 + s % 5) * H * 0.004;
        double Surf(double x) => yBase + rise - Hm / (1 + Math.Pow((x - cx) / sig, 4)) + Bump(x);
        // 表面下的小字堆，一个 item 由 charFn 摆位
        var pool = PoolOf(cut);
        var NP = pool.Count;
        var cell = J.Clamp(u * 0.045, 16, 60);
        var heap = new List<(double X, double Y, double R, string Ch, int C, double A)>();
        // 土丘主体用暗色，只有顶部几层放字
        var mound = new List<double[]>();
        for (var i = 0; i <= 48; i++)
        {
            var x = -W * 0.02 + W * 1.04 * i / 48;
            mound.Add(new[] { x, Surf(x) + cell * 1.2 });
        }
        mound.Add(new[] { W * 1.02, H + 4.0 });
        mound.Add(new[] { -W * 0.02, H + 4.0 });
        env.Poly(mound.ToArray(), J.Mix(sc.Str("bg")!, sc.Str("sub")!, 0.14), outA, false);
        var colsN = (int)Math.Ceiling(W / cell) + 2;
        var depth = Math.Max(3, Math.Min(5, (int)Math.Floor(200.0 / colsN)));
        for (var gx = -1; gx < colsN - 1 && heap.Count < 200; gx++)
        {
            var x = (gx + 0.5) * cell;
            var top = Surf(x);
            for (var gy = 0; gy < depth && heap.Count < 200; gy++)
            {
                var y = top + (gy + 0.5) * cell * 0.82;
                if (y > H + cell) break;
                heap.Add((
                    x + J.Rs(s, gx, gy, 3) * cell * 0.3,
                    y + J.Rs(s, gx, gy, 4) * cell * 0.2,
                    J.Rs(s, gx, gy, 5) * 70,
                    pool[(int)(J.H(s, gx, gy, 6) % (uint)NP)],
                    J.R(s, gx, gy, 7) < 0.14 ? 1 : 0,
                    (0.35 + 0.5 * J.R(s, gx, gy, 8)) * (1 - gy / (double)(depth + 1))));
            }
        }
        if (env.Pass == "main" && heap.Count > 0)
        {
            var it = new JObject
            {
                ["text"] = string.Concat(heap.Select(q => q.Ch)),
                ["font"] = S(Pm, "hf"),
                ["size"] = cell * 0.8,
                ["x"] = 0,
                ["y"] = 0,
                ["color"] = sc.Str("sub"),
                ["ghost"] = false,
                ["alpha"] = outA,
            };
            JText.DrawItem(env, it, new JText.TextDrawOpts
            {
                HideText = env.HideText,
                GlyphLog = env.GlyphLog,
                AllowFilter = env.AllowFilter,
                CharFn = (i, g, _) =>
                {
                    if (i < 0 || i >= heap.Count) return new CharXf { Hide = true };
                    var q = heap[i];
                    var gx = g != null ? (double)g["x"]! : 0;
                    var gy = g != null ? (double)g["y"]! : 0;
                    return new CharXf
                    {
                        Dx = q.X - gx,
                        Dy = q.Y - gy,
                        Rot = q.R,
                        A = q.A,
                        Color = q.C != 0 ? sc.Str("accent") : null,
                    };
                },
            });
        }
        // 歌词字落到字堆上，沿坡度停稳
        const double fd = 0.34;
        var gap = J.Clamp(cut.Dur * 0.3 / n, 0.03, 0.09);
        BBox? bb = null;
        var order = new List<(int Ri, int I)>();
        for (var ri = nR - 1; ri >= 0; ri--) // 底层先落
        {
            var chars = JLayouts.CodePoints(rowsT[ri]);
            var ord = Enumerable.Range(0, chars.Count).ToList();
            if (S(Pm, "order") == "random") ord = ord.OrderBy(v => J.R(s, ri, v, 21)).ToList();
            foreach (var i in ord) order.Add((ri, i));
        }
        var when = new Dictionary<int, double>();
        for (var q = 0; q < order.Count; q++) when[order[q].Ri * 100 + order[q].I] = 0.08 + q * gap;
        for (var ri = 0; ri < rowsT.Count; ri++)
        {
            var layer = nR - 1 - ri;
            var chars = JLayouts.CodePoints(rowsT[ri]);
            var ads = chars.Select(ch => Adv(font, ch) * size * 1.02).ToList();
            var tot = ads.Sum();
            var x = cx - tot / 2;
            for (var i = 0; i < chars.Count; i++)
            {
                var ch = chars[i];
                var gxp = x + ads[i] / 2;
                x += ads[i];
                if (ch == " ") continue;
                var ys = Surf(gxp);
                var slope = Math.Atan2(Surf(gxp + size * 0.5) - Surf(gxp - size * 0.5), size);
                var tilt = slope / J.DEG * 0.7 + J.Rs(s, ri, i, 9) * 6;
                var restY = ys - size * (0.47 + layer * 0.98);
                var t1 = when[ri * 100 + i];
                var f = (lt - t1) / fd;
                if (f < 0) continue;
                double y;
                double rot;
                var sy = 1.0;
                if (f < 1)
                {
                    var e = J.E.InQuad(f);
                    y = J.Lerp(-size * 1.2 - J.R(s, ri, i, 10) * H * 0.2, restY, e);
                    rot = J.Lerp(J.Rs(s, ri, i, 11) * 50, tilt, e);
                }
                else
                {
                    var tau = (f - 1) * fd;
                    y = restY - size * 0.14 * Math.Abs(Math.Sin(tau * 16)) * Math.Exp(-tau / 0.09);
                    rot = tilt;
                    sy = 1 - 0.16 * Math.Exp(-tau / 0.05);
                    if (B(Pm, "dust") && tau < 0.35 && env.Pass == "main")
                    {
                        for (var q = 0; q < 4; q++)
                        {
                            var dir = q < 2 ? -1 : 1;
                            var sp = J.Rr(0.6, 1.2, s, ri, i, q);
                            env.Circle(gxp + dir * (size * 0.35 + tau * size * 1.6 * sp),
                                restY + size * 0.45 - tau * size * (0.9 - tau * 2.2) * sp,
                                size * 0.035 * (1 - tau / 0.35), sc.Str("sub"), null, 0, (1 - tau / 0.35) * outA, false);
                        }
                    }
                }
                bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = ch,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = gxp,
                    ["y"] = y + size * 0.47 * (1 - sy),
                    ["sy"] = sy,
                    ["rot"] = rot,
                    ["color"] = J.R(s, ri, i, 12) < 0.12 ? sc.Str("accent") : sc.Str("fg"),
                    ["mi"] = MiAt(env, t1),
                }));
            }
        }
        env.Body = bb;
    }

    // ---------------- blocks（源 L1444-1605） ----------------

    private sealed class Blk
    {
        public string? Ch;
        public int Shape;
        public double X, Y;
        public int R;
        public double Order;
        public string Col = "";
        public double Sz = 1;
    }

    private static void DrawBlocks(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var n = JLayouts.GlyphCount(cut.Text);
        if (n == 0)
        {
            env.Body = null;
            return;
        }
        var port = IsPort(env);
        var rowsB = RowsOf(cut.Text, port ? 4 : n <= 6 ? 6 : Math.Min(8, (int)Math.Ceiling(n / 2.0)));
        var rows = rowsB.Count;
        var per = rowsB.Max(r => r.Count);
        const double dk = 0.34;
        const double gk = 1.07; // 斜侧/顶面的深度系数
        var deco = B(Pm, "deco") && (!port || n <= 3);
        var extra = deco ? 2 : 0;
        var k = Math.Min(W * 0.84 / (per * gk + extra * 0.95 + dk), Math.Min(H * 0.7 / (rows * 1.02 + dk + 0.2), u * 0.26));
        var side = D(Pm, "side", 1);
        var dx = k * dk * 0.8 * side;
        var dy = -k * dk * 0.62;
        var floorY = H / 2 + rows * k / 2 + k * 0.1;
        var outA = Tout(env);
        var cols = ObjCols(sc);
        var lw = Math.Max(1, k * 0.012);
        env.Line(new[] { new[] { W * 0.08, floorY }, new[] { W * 0.92, floorY } }, sc.Str("sub"), Math.Max(1, u * 0.002), 0.6 * Tin(env, 0, 0.4) * outA, false);
        var blk = new List<Blk>();
        // 歌词方块先摆底层，上层才有着落
        var gi = 0;
        var colOff = I(Pm, "colOff");
        var byRow = new List<List<Blk>>();
        for (var r = 0; r < rows; r++) byRow.Add(new List<Blk>());
        for (var r = 0; r < rows; r++)
            for (var j = 0; j < rowsB[r].Count; j++)
            {
                var ch = rowsB[r][j];
                if (ch == " ") continue;
                var x = W / 2 + (j - (rowsB[r].Count - 1) / 2.0) * k * gk - dx / 2;
                var b0 = new Blk
                {
                    Ch = ch,
                    X = x,
                    Y = floorY - (rows - r - 0.5) * k * 1.02,
                    R = r,
                    Order = (rows - 1 - r) * per + j,
                    Col = cols[(gi++ + colOff) % cols.Count],
                };
                blk.Add(b0);
                byRow[r].Add(b0);
            }
        // 悬空处垫空白方块，上层永远踩得着
        for (var r = 0; r < rows - 1; r++)
            foreach (var b0 in byRow[r])
            {
                if (byRow[r + 1].Any(q => Math.Abs(q.X - b0.X) < k * 0.6)) continue;
                var f = new Blk
                {
                    Ch = null,
                    Shape = 3,
                    X = b0.X,
                    Y = floorY - (rows - r - 1.5) * k * 1.02,
                    R = r + 1,
                    Order = (rows - 2 - r) * per - 1,
                    Col = J.Mix(b0.Col, sc.Str("bg")!, 0.35),
                };
                blk.Add(f);
                byRow[r + 1].Add(f);
            }
        if (deco)
        {
            var cntB = Math.Min(per, n);
            var xl = W / 2 - (cntB - 1) / 2.0 * k * gk - dx / 2 - k * 1.05;
            var xr = W / 2 + (cntB - 1) / 2.0 * k * gk - dx / 2 + k * 1.05;
            blk.Add(new Blk { Ch = null, Shape = 0, X = xl, Y = floorY - k * 0.5 * 0.8, Sz = 0.8, Order = -1, Col = cols[(colOff + 2) % cols.Count] });
            blk.Add(new Blk { Ch = null, Shape = 1, X = xr, Y = floorY - k * 0.5 * 0.8, Sz = 0.8, Order = n + 1, Col = cols[(colOff + 3) % cols.Count] });
            if (rows == 1)
                blk.Add(new Blk { Ch = null, Shape = 2, X = xr + J.Rs(s, 1) * k * 0.05, Y = floorY - k * 0.8 - k * 0.3, Sz = 0.6, Order = n + 2, Col = cols[(colOff + 1) % cols.Count] });
        }
        var gap = J.Clamp(cut.Dur * 0.3 / (n + 2), 0.035, 0.1);
        const double fd = 0.3;
        BBox? bb = null;
        // 跟节拍跳一下，无节拍就每 0.6s
        var hopI = -1;
        var hopF = 0.0;
        if (B(Pm, "hop") && lt > 0.3 + (n + 2) * gap + fd)
        {
            var beat = BeatOf(env);
            if (beat is { Len: > 0.2 } bi)
            {
                hopI = (int)(J.H(s, bi.Index, 4) % (uint)n);
                hopF = bi.Since / 0.32;
            }
            else
            {
                const double P0 = 0.65;
                var kk = (int)Math.Floor(env.Ltb / P0);
                hopI = (int)(J.H(s, kk, 4) % (uint)n);
                hopF = (env.Ltb - kk * P0) / 0.32;
            }
        }
        var sorted = blk.OrderByDescending(b => b.Y).ThenBy(b => side > 0 ? b.X : -b.X).ToList();
        foreach (var b in sorted)
        {
            var kz = k * b.Sz;
            var t1 = 0.06 + (b.Order + 1) * gap;
            var f = (lt - t1) / fd;
            if (f < 0) continue;
            var y = b.Y;
            var sq = 1.0;
            if (f < 1) y = J.Lerp(-kz * 2 - (H - b.Y) * 0.2, b.Y, J.E.InQuad(f));
            else
            {
                var tau = (f - 1) * fd;
                y = b.Y - kz * 0.1 * Math.Abs(Math.Sin(tau * 14)) * Math.Exp(-tau / 0.1);
                sq = 1 - 0.1 * Math.Exp(-tau / 0.05);
            }
            var bi2 = blk.IndexOf(b);
            if (bi2 == hopI && hopF < 1) y -= kz * 0.16 * Math.Sin(Math.PI * J.Clamp(hopF));
            y += J.E.InQuad(J.Clamp(env.POut * 1.4 - (b.Order + 1) / (n + 3) * 0.4)) * H * 0.7;
            var rot = B(Pm, "tilt") && b.R == 0 && rows > 1 ? J.Rs(s, bi2, 3) * 3 : 0;
            env.Ctx.Save();
            env.Ctx.Translate((float)b.X, (float)(y + kz / 2));
            env.Ctx.RotateRadians((float)(rot * J.DEG));
            env.Ctx.Scale(1, (float)sq);
            env.Ctx.Translate(0, (float)(-kz / 2));
            var hk = kz / 2;
            var ddx = dx * b.Sz;
            var ddy = dy * b.Sz;
            var topC = Shade(sc, b.Col, 0.28);
            var sideC = Shade(sc, b.Col, -0.3);
            env.Poly(new[]
            {
                new[] { -hk, -hk }, new[] { hk, -hk }, new[] { hk + ddx, -hk + ddy }, new[] { -hk + ddx, -hk + ddy },
            }, topC, outA, false);
            env.Poly(side > 0
                ? new[] { new[] { hk, -hk }, new[] { hk + ddx, -hk + ddy }, new[] { hk + ddx, hk + ddy }, new[] { hk, hk } }
                : new[] { new[] { -hk, -hk }, new[] { -hk + ddx, -hk + ddy }, new[] { -hk + ddx, hk + ddy }, new[] { -hk, hk } },
                sideC, outA, false);
            env.Rect(-hk, -hk, kz, kz, b.Col, outA, GIn(env));
            env.RRect(-hk + kz * 0.08, -hk + kz * 0.08, kz * 0.84, kz * 0.84, kz * 0.06, null, outA, false,
                J.Mix(b.Col, OnCol(sc, b.Col), 0.35), lw * 1.5);
            if (b.Ch == null)
            {
                var tcol = J.Mix(b.Col, OnCol(sc, b.Col), 0.8);
                var q = kz * 0.26;
                if (b.Shape == 3) { }
                else if (b.Shape == 0) env.Circle(0, 0, q, tcol, null, 0, outA, false);
                else if (b.Shape == 1) env.Poly(new[] { new[] { 0, -q * 1.1 }, new[] { q * 1.05, q * 0.8 }, new[] { -q * 1.05, q * 0.8 } }, tcol, outA, false);
                else env.Poly(new[] { new[] { 0, -q }, new[] { q, 0 }, new[] { 0, q }, new[] { -q, 0 } }, tcol, outA, false);
            }
            env.Ctx.Restore();
            if (b.Ch != null)
            {
                var m = new[]
                {
                    Math.Cos(rot * J.DEG), Math.Sin(rot * J.DEG), -Math.Sin(rot * J.DEG) * sq,
                    Math.Cos(rot * J.DEG) * sq, b.X, y + kz / 2,
                };
                bb = JLayouts.UnionBb(bb, DrawAff(env, new JObject
                {
                    ["text"] = b.Ch,
                    ["font"] = S(Pm, "font"),
                    ["size"] = kz * 0.6,
                    ["x"] = 0,
                    ["y"] = -kz / 2,
                    ["color"] = OnCol(sc, b.Col),
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, t1),
                }, m));
            }
        }
        env.Body = bb;
    }

    // ---------------- balloons（源 L1607-1737） ----------------

    private sealed record Balloon(string Ch, double X, double Y, double Rot, double E, double T1, string Col, int Q);

    private static void DrawBalloons(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var t0 = Flat(cut.Text);
        var n = JLayouts.GlyphCount(t0);
        if (n == 0)
        {
            env.Body = null;
            return;
        }
        var rowsT = n > (port ? 4 : 7) ? ChunksK(t0, port ? (int)Math.Ceiling(n / 4.0) : 2) : new List<string> { t0 };
        var nR = rowsT.Count;
        var outA = Tout(env);
        var mot = env.Fx.Motion;
        var font = S(Pm, "font")!;
        var size = Math.Min(u * 0.26, H * 0.56 / (nR * 1.25 + 0.3));
        foreach (var r in rowsT)
        {
            var a = 0.0;
            foreach (var ch in JLayouts.CodePoints(r)) a += Adv(font, ch) * 1.2;
            size = Math.Min(size, W * 0.84 / Math.Max(1, a));
        }
        var cols = ObjCols(sc);
        var gx = W / 2.0;
        var gy = H * 0.93;
        var fly = J.E.InCubic(env.POut) * H * 0.9;
        var glyphs = new List<Balloon>();
        var colOff = I(Pm, "colOff");
        for (var ri = 0; ri < rowsT.Count; ri++)
        {
            var chars = JLayouts.CodePoints(rowsT[ri]);
            var ads = chars.Select(ch => Adv(font, ch) * size * 1.2).ToList();
            var tot = ads.Sum();
            var x = W / 2 - tot / 2;
            var yRow = H * 0.4 + (ri - (nR - 1) / 2.0) * size * 1.3;
            for (var i = 0; i < chars.Count; i++)
            {
                var ch = chars[i];
                var cxp = x + ads[i] / 2;
                x += ads[i];
                if (ch == " ") continue;
                var q = glyphs.Count;
                var ph = J.R(s, q, 1) * J.TAU;
                var u0 = tot > 0 ? (cxp - W / 2) / (tot / 2) : 0;
                var t1 = 0.05 + q * J.Clamp(cut.Dur * 0.3 / n, 0.03, 0.08);
                var e = J.Clamp((lt - t1) / 0.45);
                var bob = Math.Sin(env.Ltb * 1.4 + ph) * size * 0.05 * (0.4 + mot);
                var y = yRow - D(Pm, "arc") * H * (1 - u0 * u0) + bob + (1 - J.E.OutCubic(e)) * H * 0.35 - fly * (0.8 + 0.4 * J.R(s, q, 2));
                glyphs.Add(new Balloon(
                    ch,
                    cxp + Math.Sin(env.Ltb * 0.9 + ph) * size * 0.03,
                    y,
                    Math.Sin(env.Ltb * 0.8 + ph * 1.3) * 5 * (0.4 + mot) + J.Rs(s, q, 3) * 4,
                    e, t1,
                    B(Pm, "mono") ? cols[colOff % cols.Count] : cols[(q + colOff) % cols.Count],
                    q));
            }
        }
        // 气球线，画在气球后面
        var lw = Math.Max(1, u * 0.0016);
        var tie = S(Pm, "tie");
        foreach (var g in glyphs)
        {
            if (g.E <= 0) continue;
            var bx = g.X - Math.Sin(g.Rot * J.DEG) * size * 0.58;
            var by = g.Y + Math.Cos(g.Rot * J.DEG) * size * 0.58;
            var pts = new List<double[]>();
            var ex = tie == "bunch" ? gx + (g.X - gx) * 0.06 : bx + Math.Sin(env.Ltb * 1.1 + g.Q) * size * 0.2;
            var ey = tie == "bunch" ? gy - fly : Math.Min(H * 1.05, by + H * 0.3);
            for (var k = 0; k <= 10; k++)
            {
                var f = k / 10.0;
                var wig = Math.Sin(f * 7 + env.Ltb * 2 + g.Q) * size * 0.05 * f * (1 - f) * 4;
                pts.Add(new[] { J.Lerp(bx, ex, f) + wig, J.Lerp(by, ey, f) + Math.Sin(f * Math.PI) * size * 0.25 });
            }
            env.Line(pts.ToArray(), sc.Str("sub"), lw, 0.85 * outA * g.E, false);
            env.Poly(new[]
            {
                new[] { bx - size * 0.05, by + size * 0.07 }, new[] { bx + size * 0.05, by + size * 0.07 },
                new[] { bx, by - size * 0.02 },
            }, Shade(sc, g.Col, -0.35), outA * g.E, false);
        }
        if (tie == "bunch") env.Circle(gx, gy - fly, size * 0.05, sc.Str("sub"), null, 0, outA * Tin(env, 0.2, 0.3), false);
        BBox? bb = null;
        foreach (var g in glyphs)
        {
            if (g.E <= 0) continue;
            var q = J.E.OutBack(g.E, 2.2);
            var sz = size * (0.3 + 0.7 * q);
            var dark = Shade(sc, g.Col, -0.38);
            // 鼓边、主体、高光
            env.DrawItem(new JObject
            {
                ["text"] = g.Ch,
                ["font"] = font,
                ["size"] = sz,
                ["x"] = g.X,
                ["y"] = g.Y,
                ["rot"] = g.Rot,
                ["color"] = dark,
                ["stroke"] = sz * 0.13,
                ["strokeColor"] = dark,
                ["strokeUnder"] = true,
                ["alpha"] = outA,
                ["ghost"] = false,
            });
            bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
            {
                ["text"] = g.Ch,
                ["font"] = font,
                ["size"] = sz,
                ["x"] = g.X,
                ["y"] = g.Y,
                ["rot"] = g.Rot,
                ["color"] = g.Col,
                ["stroke"] = sz * 0.065,
                ["strokeColor"] = g.Col,
                ["strokeUnder"] = true,
                ["plain"] = true,
                ["mi"] = MiAt(env, g.T1),
            }));
            JText.DrawItem(env, new JObject
            {
                ["text"] = g.Ch,
                ["font"] = font,
                ["size"] = sz,
                ["x"] = g.X - sz * 0.02,
                ["y"] = g.Y - sz * 0.03,
                ["rot"] = g.Rot,
                ["color"] = LightOf(sc),
                ["alpha"] = 0.3 * outA * Math.Min(1, g.E * 2),
                ["ghost"] = false,
            }, new JText.TextDrawOpts
            {
                HideText = env.HideText,
                GlyphLog = env.GlyphLog,
                AllowFilter = env.AllowFilter,
                CharFn = (_, _, _) => new CharXf { ClipY = new[] { -0.75, -0.22 }, S = 0.93 },
            });
            env.Circle(g.X - sz * 0.22, g.Y - sz * 0.26, sz * 0.045, LightOf(sc), null, 0, 0.7 * outA * g.E, false);
        }
        env.Body = bb;
    }

    // ---------------- magnets（源 L1739-1904） ----------------

    private const string DecoyChars = "ABCDEFGHKMNPRSTXYZ0123456789★♥♪?!";

    private static void DrawMagnets(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var chs = SlotsOf(cut.Text);
        var n = chs.Count(c => c != " ");
        if (n == 0)
        {
            env.Body = null;
            return;
        }
        var outA = Tout(env);
        var ba = Tin(env, 0, 0.45, J.E.OutCubic) * outA;
        // 冰箱门底板
        var mx = W * 0.04;
        var my = H * 0.05;
        var bw = W - mx * 2;
        var bh = H - my * 2;
        var rr = u * 0.03;
        var board = J.Mix(sc.Str("bg")!, sc.Str("fg")!, J.Lum(sc.Str("bg") ?? "#000000") < 0.5 ? 0.07 : 0.06);
        env.RRect(mx, my + (1 - ba) * H * 0.04, bw, bh, rr, board, ba, false, J.Mix(sc.Str("bg")!, sc.Str("fg")!, 0.16), Math.Max(1, u * 0.002));
        if (env.Pass == "main" && ba > 0)
        {
            using var sh = SKShader.CreateLinearGradient(
                new SKPoint((float)mx, (float)my), new SKPoint((float)(mx + bw), (float)(my + bh)),
                new[] { SkRgba(LightOf(sc), 0.06), SkRgba(LightOf(sc), 0), SkRgba(LightOf(sc), 0.04), SkRgba(LightOf(sc), 0) },
                new float[] { 0, 0.45f, 0.5f, 1 }, SKShaderTileMode.Clamp);
            using var path = new SKPath();
            RrPath(path, mx, my + (1 - ba) * H * 0.04, bw, bh, rr);
            env.Ctx.Save();
            env.Ctx.ClipPath(path);
            using var p = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)Math.Round(J.Clamp(ba) * 255)), Shader = sh };
            env.Ctx.DrawRect(new SKRect((float)mx, (float)my, (float)(mx + bw), (float)(my + bh)), p);
            env.Ctx.Restore();
        }
        var handle = D(Pm, "handle", 1);
        var hx = handle > 0 ? mx + bw - u * 0.05 : mx + u * 0.035;
        var hh = bh * 0.34;
        env.RRect(hx, H / 2 - hh / 2, u * 0.016, hh, u * 0.008, J.Mix(board, sc.Str("fg")!, 0.2), ba, false);
        // 歌词分行排布
        var per = port ? 4 : 8;
        var t0 = Flat(cut.Text);
        var rowsT = n > per ? ChunksK(t0, (int)Math.Ceiling(n / (double)per)) : new List<string> { t0 };
        var nR = rowsT.Count;
        var font = S(Pm, "font")!;
        var baseSize = Math.Min(u * 0.22, H * 0.56 / (nR * 1.12));
        foreach (var r in rowsT)
        {
            var a = 0.0;
            foreach (var ch in JLayouts.CodePoints(r)) a += Adv(font, ch) * (J.IsKanji(ch[0]) ? 1 : 0.86) * 1.1;
            baseSize = Math.Min(baseSize, bw * 0.8 / Math.Max(1, a));
        }
        var cols = ObjCols(sc);
        var colOff = I(Pm, "colOff");
        var tilt = D(Pm, "tilt");
        var items = new List<(string Ch, double X, double Y, double Size, double Rot, string Col, int Q)>();
        for (var ri = 0; ri < rowsT.Count; ri++)
        {
            var chars = JLayouts.CodePoints(rowsT[ri]);
            var ks = chars.Select(ch => J.IsKanji(ch[0]) ? 1.0 : J.IsPunct(ch[0]) ? 0.6 : J.IsSmallKana(ch[0]) ? 0.7 : 0.86).ToList();
            var ads = new List<double>();
            for (var i = 0; i < chars.Count; i++) ads.Add(Adv(font, chars[i]) * baseSize * ks[i] * 1.1);
            var tot = ads.Sum();
            var x = W / 2 - tot / 2;
            var y = H / 2 + (ri - (nR - 1) / 2.0) * baseSize * 1.14;
            for (var i = 0; i < chars.Count; i++)
            {
                var ch = chars[i];
                var cx = x + ads[i] / 2;
                x += ads[i];
                if (ch == " ") continue;
                var q = items.Count;
                items.Add((ch,
                    cx + J.Rs(s, q, 1) * baseSize * 0.04,
                    y + J.Rs(s, q, 2) * baseSize * 0.08,
                    baseSize * ks[i],
                    J.Rs(s, q, 3) * tilt,
                    cols[(q * 7 + colOff) % cols.Count],
                    q));
            }
        }
        // 边缘一圈诱饵磁贴，绝不压到歌词带
        var bandY0 = H / 2 - nR * baseSize * 0.62;
        var bandY1 = H / 2 + nR * baseSize * 0.62;
        var decoys = I(Pm, "decoys");
        var df = S(Pm, "df")!;
        if (env.Pass == "main")
        {
            for (var d = 0; d < decoys; d++)
            {
                var ta = 0.05 + d * 0.04;
                var e = J.Clamp((lt - ta) / 0.2);
                if (e <= 0) continue;
                var top = d % 2 == 0;
                var half = (int)Math.Ceiling(decoys / 2.0);
                var slot = d / 2;
                var sz = Math.Min(baseSize * J.Rr(0.36, 0.58, s, d, 14), u * 0.09);
                var y = top
                    ? J.Lerp(my + bh * 0.08 + sz * 0.6, Math.Max(my + bh * 0.1 + sz * 0.6, bandY0 - sz * 0.8), J.R(s, d, 11))
                    : J.Lerp(Math.Min(my + bh * 0.9 - sz * 0.6, bandY1 + sz * 0.8), my + bh * 0.92 - sz * 0.6, J.R(s, d, 11));
                var x = mx + bw * (0.1 + 0.8 * (slot + 0.2 + 0.6 * J.R(s, d, 12)) / half);
                var ch = DecoyChars[(int)(J.H(s, d, 13) % (uint)DecoyChars.Length)].ToString();
                var rot = J.Rs(s, d, 15) * 25;
                var col = cols[(d + 2 + colOff) % cols.Count];
                var k = 1 + 0.25 * (1 - J.E.OutCubic(e));
                env.DrawItem(new JObject
                {
                    ["text"] = ch,
                    ["font"] = df,
                    ["size"] = sz * k,
                    ["x"] = x + sz * 0.05,
                    ["y"] = y + sz * 0.07,
                    ["rot"] = rot,
                    ["color"] = DarkOf(sc),
                    ["stroke"] = sz * 0.08,
                    ["strokeColor"] = DarkOf(sc),
                    ["strokeUnder"] = true,
                    ["alpha"] = 0.28 * outA * e,
                    ["ghost"] = false,
                });
                env.DrawItem(new JObject
                {
                    ["text"] = ch,
                    ["font"] = df,
                    ["size"] = sz * k,
                    ["x"] = x,
                    ["y"] = y,
                    ["rot"] = rot,
                    ["color"] = col,
                    ["stroke"] = sz * 0.08,
                    ["strokeColor"] = col,
                    ["strokeUnder"] = true,
                    ["alpha"] = 0.85 * outA * e,
                    ["ghost"] = false,
                });
            }
        }
        BBox? bb = null;
        var gap = J.Clamp(cut.Dur * 0.32 / n, 0.04, 0.1);
        foreach (var m in items)
        {
            var t1 = 0.18 + m.Q * gap;
            var f = (lt - t1) / 0.16;
            if (f < 0) continue;
            var snap = f < 1 ? 1.28 - 0.28 * J.E.InQuad(f) : 1 - 0.05 * Math.Exp(-(f - 1) * 0.16 / 0.05) * Math.Cos((f - 1) * 3);
            var lift = f < 1 ? 1 - f : 0;
            var sz = m.Size * snap;
            env.DrawItem(new JObject
            {
                ["text"] = m.Ch,
                ["font"] = font,
                ["size"] = sz,
                ["x"] = m.X + m.Size * (0.04 + lift * 0.12),
                ["y"] = m.Y + m.Size * (0.06 + lift * 0.18),
                ["rot"] = m.Rot,
                ["color"] = DarkOf(sc),
                ["stroke"] = sz * 0.08,
                ["strokeColor"] = DarkOf(sc),
                ["strokeUnder"] = true,
                ["alpha"] = (0.3 - lift * 0.15) * outA,
                ["ghost"] = false,
            });
            bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
            {
                ["text"] = m.Ch,
                ["font"] = font,
                ["size"] = sz,
                ["x"] = m.X,
                ["y"] = m.Y,
                ["rot"] = m.Rot,
                ["color"] = m.Col,
                ["stroke"] = sz * 0.08,
                ["strokeColor"] = m.Col,
                ["strokeUnder"] = true,
                ["plain"] = true,
                ["mi"] = MiAt(env, t1),
            }));
        }
        env.Body = bb;
    }

    // ---------------- tiles（源 L1906-2074） ----------------

    private static readonly Dictionary<string, int> Lpts = new()
    {
        ["A"] = 1, ["B"] = 3, ["C"] = 3, ["D"] = 2, ["E"] = 1, ["F"] = 4, ["G"] = 2, ["H"] = 4,
        ["I"] = 1, ["J"] = 8, ["K"] = 5, ["L"] = 1, ["M"] = 3, ["N"] = 1, ["O"] = 1, ["P"] = 3,
        ["Q"] = 10, ["R"] = 1, ["S"] = 1, ["T"] = 1, ["U"] = 1, ["V"] = 4, ["W"] = 4, ["X"] = 8,
        ["Y"] = 4, ["Z"] = 10,
    };

    private static int TilePts(string ch)
    {
        var up = ch.ToUpperInvariant();
        if (Lpts.TryGetValue(up, out var v)) return v;
        if (J.IsKanji(ch[0])) return 3 + (int)(J.Sid(ch) % 8);
        if (J.IsSmallKana(ch[0])) return 5;
        if (J.IsPunct(ch[0])) return 0;
        if (J.IsKata(ch[0])) return 2 + (int)(J.Sid(ch) % 2);
        return 1 + (int)(J.Sid(ch) % 3);
    }

    private static void DrawTiles(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var n = JLayouts.GlyphCount(cut.Text);
        if (n == 0)
        {
            env.Body = null;
            return;
        }
        var port = IsPort(env);
        var rowsT = RowsOf(cut.Text, port ? 5 : 9);
        var rows = rowsT.Count;
        var per = rowsT.Max(r => r.Count);
        var k = Math.Min(W * 0.84 / (per * 1.08 + 0.6), Math.Min(H * 0.62 / (rows * 1.75), u * 0.2));
        var outA = Tout(env);
        var tileC = J.Mix(LightOf(sc), sc.Str("accent")!, 0.1);
        var tileT = DarkOf(sc);
        var edge = J.Mix(tileC, DarkOf(sc), 0.3);
        var hi = J.Mix(tileC, "#FFFFFF", 0.35);
        var rack = S(Pm, "rack");
        var rackC = PlateCol(sc, rack == "accent"
            ? new[] { sc.Str("accent"), sc.Str("ink") }
            : rack == "sub" ? new[] { sc.Str("sub"), sc.Str("ink") } : new[] { sc.Str("ink"), sc.Str("accent") });
        var gap = J.Clamp(cut.Dur * 0.36 / n, 0.05, 0.12);
        const double fd = 0.28;
        BBox? bb = null;
        var score = 0;
        var total = 0;
        var ra = Tin(env, 0, 0.4, J.E.OutCubic) * outA;
        var gi = 0;
        var from = S(Pm, "from");
        for (var r = 0; r < rows; r++)
        {
            var row = rowsT[r];
            var cnt = row.Count;
            var rowY = H / 2 + (r - (rows - 1) / 2.0) * k * 1.75;
            var rw = cnt * k * 1.08 + k * 0.5;
            // 字母架：背槽、前斜唇、阴影
            var ry = rowY + k * 0.42;
            var rw2 = rw * (0.3 + 0.7 * ra);
            env.Poly(new[]
            {
                new[] { W / 2 - rw2 / 2 + k * 0.06, ry + k * 0.34 }, new[] { W / 2 + rw2 / 2 + k * 0.06, ry + k * 0.34 },
                new[] { W / 2 + rw2 / 2 + k * 0.2, ry + k * 0.42 }, new[] { W / 2 - rw2 / 2 + k * 0.2, ry + k * 0.42 },
            }, DarkOf(sc), 0.25 * ra, false);
            env.Rect(W / 2 - rw2 / 2, ry - k * 0.12, rw2, k * 0.14, Shade(sc, rackC, -0.3), ra, false);
            for (var j = 0; j < cnt; j++)
            {
                var ch = row[j];
                if (ch == " ") continue;
                var i = gi++;
                var pts = TilePts(ch);
                total += pts;
                var x = W / 2 + (j - (cnt - 1) / 2.0) * k * 1.08;
                var y = rowY;
                var t1 = 0.12 + i * gap;
                var f = J.Clamp((lt - t1) / fd);
                if (f <= 0) continue;
                if (f >= 1) score += pts;
                var e = J.E.OutCubic(f);
                var tx = x;
                var ty = y;
                var rot = 0.0;
                if (from == "drop")
                {
                    ty = J.Lerp(y - H * 0.5, y, J.E.InQuad(f));
                    rot = (1 - f) * J.Rs(s, i, 2) * 30;
                    if (f >= 1) ty -= k * 0.06 * Math.Abs(Math.Sin((lt - t1 - fd) * 18)) * Math.Exp(-(lt - t1 - fd) / 0.08);
                }
                else
                {
                    tx = J.Lerp(W + k, x, e);
                    rot = (1 - e) * 10;
                    ty = y - Math.Sin(Math.PI * f) * k * 0.15;
                }
                // 出场：错峰向左滑出
                var po = J.E.InCubic(J.Clamp(env.POut * 1.4 - j / (double)Math.Max(1, cnt) * 0.4));
                tx -= po * (x + k * 2 - -k);
                var lean = -k * 0.05;
                env.Ctx.Save();
                env.Ctx.Translate((float)tx, (float)ty);
                env.Ctx.RotateRadians((float)(rot * J.DEG));
                env.RRect(-k / 2 + k * 0.03, -k / 2 + k * 0.06 + lean, k, k, k * 0.1, edge, outA, false);
                env.RRect(-k / 2, -k / 2 + lean, k, k, k * 0.1, tileC, outA, GIn(env));
                env.Line(new[]
                {
                    new[] { -k / 2 + k * 0.12, -k / 2 + k * 0.05 + lean }, new[] { k / 2 - k * 0.12, -k / 2 + k * 0.05 + lean },
                }, hi, Math.Max(1, k * 0.025), 0.7 * outA, false);
                if (pts > 0)
                    env.DrawItem(new JObject
                    {
                        ["text"] = pts.ToString(CultureInfo.InvariantCulture),
                        ["font"] = MonoF(env),
                        ["size"] = k * 0.17,
                        ["x"] = k * 0.35,
                        ["y"] = k * 0.33 + lean,
                        ["color"] = tileT,
                        ["alpha"] = 0.85 * outA,
                        ["ghost"] = false,
                    });
                env.Ctx.Restore();
                var m = new[]
                {
                    Math.Cos(rot * J.DEG), Math.Sin(rot * J.DEG), -Math.Sin(rot * J.DEG), Math.Cos(rot * J.DEG), tx, ty + lean,
                };
                bb = JLayouts.UnionBb(bb, DrawAff(env, new JObject
                {
                    ["text"] = ch,
                    ["font"] = S(Pm, "font"),
                    ["size"] = k * 0.6,
                    ["x"] = -k * 0.06,
                    ["y"] = -k * 0.05,
                    ["color"] = tileT,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, t1),
                }, m));
            }
            env.Poly(new[]
            {
                new[] { W / 2 - rw2 / 2, ry + k * 0.02 }, new[] { W / 2 + rw2 / 2, ry + k * 0.02 },
                new[] { W / 2 + rw2 / 2 - k * 0.06, ry + k * 0.34 }, new[] { W / 2 - rw2 / 2 + k * 0.06, ry + k * 0.34 },
            }, rackC, ra, GIn(env));
            env.Line(new[] { new[] { W / 2 - rw2 / 2, ry + k * 0.02 }, new[] { W / 2 + rw2 / 2, ry + k * 0.02 } },
                Shade(sc, rackC, 0.3), Math.Max(1, k * 0.02), ra, false);
        }
        if (B(Pm, "score") && env.Pass == "main")
        {
            var fs = Math.Max(SmallSize(env), k * 0.16);
            var lastY = H / 2 + (rows - 1 - (rows - 1) / 2.0) * k * 1.75 + k * 0.42 + k * 0.34;
            var a = Tin(env, 0.2, 0.3, J.E.OutCubic) * outA;
            env.DrawItem(new JObject
            {
                ["text"] = "SCORE",
                ["font"] = MonoF(env),
                ["size"] = fs,
                ["track"] = 0.3,
                ["align"] = "left",
                ["x"] = W / 2 - Math.Min(per, n) * k * 0.54 - k * 0.25,
                ["y"] = lastY + fs * 1.6,
                ["color"] = sc.Str("sub"),
                ["alpha"] = a,
                ["ghost"] = false,
            });
            env.DrawItem(new JObject
            {
                ["text"] = score.ToString(CultureInfo.InvariantCulture).PadLeft(3, '0'),
                ["font"] = MonoF(env),
                ["size"] = fs * 1.5,
                ["track"] = 0.1,
                ["align"] = "right",
                ["x"] = W / 2 + Math.Min(per, n) * k * 0.54 + k * 0.25,
                ["y"] = lastY + fs * 1.6,
                ["color"] = score >= total && total > 0 ? sc.Str("accent") : sc.Str("fg"),
                ["alpha"] = a,
                ["ghost"] = false,
            });
        }
        env.Body = bb;
    }

    // ---------------- bulbs（源 L2109-2219） ----------------

    /// <summary>闭合多边形按固定间距重采样（源 resample）。</summary>
    private static List<double[]> Resample(List<double[]> pts, double d, int maxN = 160)
    {
        var seg = new List<double>();
        var L = 0.0;
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            var l = Hypot(b[0] - a[0], b[1] - a[1]);
            seg.Add(l);
            L += l;
        }
        var N = Math.Max(4, Math.Min(maxN, (int)Math.Round(L / d)));
        var step = L / N;
        var outp = new List<double[]>();
        var si = 0;
        var acc = 0.0;
        for (var k = 0; k < N; k++)
        {
            var t = k * step;
            while (si < seg.Count - 1 && acc + seg[si] < t)
            {
                acc += seg[si];
                si++;
            }
            var a = pts[si];
            var b = pts[(si + 1) % pts.Count];
            var f = seg[si] > 0 ? (t - acc) / seg[si] : 0;
            outp.Add(new[] { J.Lerp(a[0], b[0], f), J.Lerp(a[1], b[1], f) });
        }
        return outp;
    }

    /// <summary>圆角矩形点集（源 rrPts，每角 nc+1 点）。</summary>
    private static List<double[]> RrPts(double x, double y, double w, double h, double r, int nc = 5)
    {
        r = Math.Min(r, Math.Min(w / 2, h / 2));
        var outp = new List<double[]>();
        var C = new[]
        {
            new[] { x + w - r, y + r, -90.0 }, new[] { x + w - r, y + h - r, 0.0 },
            new[] { x + r, y + h - r, 90.0 }, new[] { x + r, y + r, 180.0 },
        };
        foreach (var c in C)
            for (var i = 0; i <= nc; i++)
            {
                var a = (c[2] + 90 * i / nc) * J.DEG;
                outp.Add(new[] { c[0] + Math.Cos(a) * r, c[1] + Math.Sin(a) * r });
            }
        return outp;
    }

    private static void DrawBulbs(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var font = S(Pm, "font")!;
        var text = Brk(cut.Text, port ? 5 : 8);
        var o = new JObject { ["track"] = 0.06, ["lead"] = 1.12 };
        var arrow = S(Pm, "shape") == "arrow" && !port;
        var maxW = W * (arrow ? 0.62 : 0.72);
        var maxH = H * (port ? 0.36 : 0.42);
        var m100 = JLayouts.Measure(new JObject
        {
            ["text"] = text, ["font"] = font, ["size"] = 100, ["track"] = 0.06, ["lead"] = 1.12,
        });
        var subT = B(Pm, "sub") ? AltCopy(env) : null;
        var size = Math.Min(Math.Min(JLayouts.FitSize(text, font, maxW, maxH, o), u * 0.24),
            Math.Min(W * (arrow ? 0.8 : 0.9) / (m100.W / 100 + 1.5), H * 0.8 / (m100.H / 100 + 1.24 + (subT != null ? 0.3 : 0))));
        var m = JLayouts.Measure(new JObject
        {
            ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.06, ["lead"] = 1.12,
        });
        var ss = Math.Max(SmallSize(env), size * 0.16);
        var padX = size * 0.75;
        var padY = size * 0.62;
        var bw = m.W + padX * 2;
        var bh = m.H + padY * 2 + (subT != null ? ss * 1.8 : 0);
        var dir = D(Pm, "dir", 1);
        var ax = arrow ? bh * 0.42 * dir : 0;
        var cx = W / 2 - ax / 2;
        var cy = H / 2.0;
        var x0 = cx - bw / 2;
        var y0 = cy - bh / 2;
        var outA = Tout(env);
        var q = J.E.OutBack(J.Clamp(lt / 0.35), 1.4) * (1 - 0.1 * J.E.InCubic(env.POut));
        var dark = J.Lum(sc.Str("bg") ?? "#000000") < 0.4;
        var board = dark ? J.Mix(sc.Str("bg")!, sc.Str("fg")!, 0.1) : DarkOf(sc);
        var bulbC = new[] { sc.Str("accent"), LightOf(sc), sc.Str("accent2") }
            .FirstOrDefault(c => !string.IsNullOrEmpty(c) && J.Contrast(c!, board) >= 2) ?? LightOf(sc);
        var textC = new[] { LightOf(sc), sc.Str("fg"), sc.Str("accent") }
            .FirstOrDefault(c => !string.IsNullOrEmpty(c) && J.Contrast(c!, board) >= 3) ?? OnCol(sc, board);
        // 招牌轮廓
        List<double[]> shape;
        if (arrow)
        {
            var tip = dir > 0 ? new[] { x0 + bw + bh * 0.5, cy } : new[] { x0 - bh * 0.5, cy };
            shape = (dir > 0
                ? new[]
                {
                    new[] { x0, y0 }, new[] { x0 + bw, y0 }, new[] { x0 + bw, y0 - bh * 0.12 }, tip,
                    new[] { x0 + bw, y0 + bh * 1.12 }, new[] { x0 + bw, y0 + bh }, new[] { x0, y0 + bh },
                }
                : new[]
                {
                    new[] { x0 + bw, y0 }, new[] { x0, y0 }, new[] { x0, y0 - bh * 0.12 }, tip,
                    new[] { x0, y0 + bh * 1.12 }, new[] { x0, y0 + bh }, new[] { x0 + bw, y0 + bh },
                }).ToList();
        }
        else shape = RrPts(x0, y0, bw, bh, size * 0.3);
        var sp = shape.Select(p => new[] { cx + (p[0] - cx) * q, cy + (p[1] - cy) * q }).ToList();
        if (q <= 0.01 || outA <= 0)
        {
            env.Body = null;
            return;
        }
        env.Poly(sp.Select(p => new[] { p[0] + u * 0.012, p[1] + u * 0.016 }).ToArray(), DarkOf(sc), 0.3 * outA, false);
        env.Poly(sp.ToArray(), board, outA, GIn(env));
        Outline(env, sp.ToArray(), J.Mix(board, bulbC, 0.35), Math.Max(1.5, u * 0.003), outA);
        // 沿边一圈灯泡，'double' 再加内圈
        var r = J.Clamp(size * 0.075, 4, u * 0.022);
        List<double[]> Inset(double k)
        {
            if (arrow) return sp.Select(p => new[] { cx + (p[0] - cx) * k, cy + (p[1] - cy) * k }).ToList();
            var d = size * 0.3 * (1 - k);
            return RrPts(x0 + d, y0 + d * (bh / bw), bw - d * 2, bh - d * 2 * (bh / bw), size * 0.3 * k)
                .Select(p => new[] { cx + (p[0] - cx) * q, cy + (p[1] - cy) * q }).ToList();
        }

        var rings = S(Pm, "shape") == "double" && !arrow ? new[] { 0.95, 0.86 } : new[] { arrow ? 0.93 : 0.95 };
        var beatFlash = BeatOf(env) is { Since: < 0.12 } ? 1 : 0;
        var step = (int)Math.Floor(env.Ltb * 9);
        var chase = S(Pm, "chase");
        for (var ri = 0; ri < rings.Length; ri++)
        {
            var pts = Resample(Inset(rings[ri]), r * 3.3, 140);
            var N = pts.Count;
            for (var i = 0; i < N; i++)
            {
                var p = pts[i];
                var on0 = i / (double)N < J.E.InOutCubic(J.Clamp((lt - 0.1 - ri * 0.1) / 0.6)) && i / (double)N >= J.E.InCubic(env.POut);
                var lit = 0.0;
                if (on0)
                {
                    var j = (i * (int)dir + N * 4) % N;
                    if (chase == "chase") lit = (j + (ri != 0 ? 0 : 1) - step + 300 * 3) % 3 == 0 ? 1 : 0.18;
                    else if (chase == "alt") lit = (i + (int)Math.Floor(env.Ltb * 3) + ri) % 2 != 0 ? 1 : 0.2;
                    else lit = 0.2 + 0.8 * Math.Pow(0.5 + 0.5 * Math.Sin(i / (double)N * J.TAU * 3 - env.Ltb * 5 * dir + ri), 2);
                    lit = Math.Max(lit, beatFlash);
                }
                var col = ri != 0
                    ? (J.Contrast(sc.Str("accent2") ?? bulbC, board) > 2 ? sc.Str("accent2") ?? bulbC : bulbC)
                    : bulbC;
                if (lit > 0.5) env.Circle(p[0], p[1], r * 2.1, col, null, 0, 0.16 * lit * outA, false);
                env.Circle(p[0], p[1], r, lit > 0 ? J.Mix(J.Mix(board, col, 0.3), col, lit) : J.Mix(board, col, 0.22), null, 0, outA, false);
                if (lit > 0.5) env.Circle(p[0] - r * 0.25, p[1] - r * 0.3, r * 0.35, "#FFFFFF", null, 0, 0.7 * lit * outA, false);
            }
        }
        var ty = cy - (subT != null ? ss * 0.9 : 0);
        var bb = JLayouts.MainDraw(env, new JObject
        {
            ["text"] = text,
            ["font"] = font,
            ["size"] = size * Math.Min(1, q),
            ["x"] = cx,
            ["y"] = ty,
            ["color"] = textC,
            ["noHold"] = PlateHold(env),
            ["track"] = 0.06,
            ["lead"] = 1.12,
        });
        if (subT != null)
        {
            var fs = ss * Math.Min(1, q);
            var t = subT.Length > 28 ? subT.Substring(0, 27) + "…" : subT;
            var sz = Math.Min(fs, JLayouts.FitSize(t, BodyF(env), bw - padX, ss * 1.2, new JObject { ["track"] = 0.18 }));
            env.DrawItem(new JObject
            {
                ["text"] = t,
                ["font"] = BodyF(env),
                ["size"] = sz,
                ["x"] = cx,
                ["y"] = cy + m.H / 2 + padY * 0.1,
                ["track"] = 0.18,
                ["color"] = bulbC,
                ["alpha"] = Tin(env, 0.3, 0.3) * outA,
                ["ghost"] = false,
            });
        }
        env.Body = bb;
    }

    // ---------------- ledScroll（源 L2221-2410） ----------------

    private static readonly Dictionary<string, SKImage> LedTiles = new();

    /// <summary>点阵 tile（源 ledTile 的离屏 canvas）：mask = 面板色挖圆孔，dot = 亮圆点。</summary>
    private static SKImage LedTile(string kind, string col, string bg, int T)
    {
        var key = kind + col + bg + T;
        if (LedTiles.TryGetValue(key, out var img)) return img;
        var bmp = new SKBitmap(Math.Max(1, T), Math.Max(1, T), SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var x = new SKCanvas(bmp))
        {
            if (kind == "mask")
            {
                using var p = new SKPaint { Color = Env.SkColor(bg), IsAntialias = true };
                x.DrawRect(new SKRect(0, 0, T, T), p);
                using var clear = new SKPaint { BlendMode = SKBlendMode.Clear };
                x.DrawCircle(T / 2f, T / 2f, (float)(T * 0.37), clear);
            }
            else
            {
                using var p = new SKPaint { Color = Env.SkColor(col), IsAntialias = true };
                x.DrawCircle(T / 2f, T / 2f, (float)(T * 0.33), p);
            }
        }
        img = SKImage.FromBitmap(bmp);
        bmp.Dispose();
        if (LedTiles.Count > 40)
        {
            // 只摘引用不 Dispose：同帧可能还持有刚取过的 tile（mask + dot 两次取用），
            // 当场释放会让随后的 DrawImage 读到已释放 SKImage（0xC0000005）。交 GC 回收。
            LedTiles.Clear();
        }
        LedTiles[key] = img;
        return img;
    }

    private static void DrawLedScroll(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var text = Flat(cut.Text);
        var n = JLayouts.GlyphCount(text);
        if (n == 0)
        {
            env.Body = null;
            return;
        }
        var outA = Tout(env);
        var fx0 = W * 0.03;
        var fw = W * 0.94;
        var font = S(Pm, "font")!;
        var adv0 = JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = 100, ["track"] = 0.08 }).W / 100;
        var size = J.Clamp((fw - W * 0.04) * 0.9 / Math.Max(0.5, adv0), u * 0.1, Math.Min(u * 0.2, H * 0.2));
        var p = size / 9.5;
        var ph = size * 1.36;
        var info = S(Pm, "info");
        var infoH = info == "none" ? 0.0 : size * 0.56;
        var fr = size * 0.2;
        var fh = ph + infoH + fr * 2 + (infoH != 0 ? fr * 0.6 : 0);
        var fy0 = H * D(Pm, "y") - fh / 2;
        var e = J.E.OutCubic(J.Clamp(lt / 0.35));
        var frameC = J.Mix(DarkOf(sc), sc.Str("sub")!, J.Lum(sc.Str("bg") ?? "#000000") < 0.4 ? 0.25 : 0.2);
        var panel = J.Mix(DarkOf(sc), "#000000", 0.5);
        var colKey = S(Pm, "col");
        var ledC = new[] { colKey == "fg" ? LightOf(sc) : colKey == "accent2" ? sc.Str("accent2") : sc.Str("accent"), sc.Str("accent"), LightOf(sc) }
            .FirstOrDefault(c => !string.IsNullOrEmpty(c) && J.Contrast(c!, panel) >= 3) ?? "#FFFFFF";
        var infoC = new[] { sc.Str("accent2"), sc.Str("accent"), LightOf(sc) }
            .FirstOrDefault(c => !string.IsNullOrEmpty(c) && c != ledC && J.Contrast(c!, panel) >= 3) ?? LightOf(sc);
        var hw = fw / 2 * e;
        env.RRect(W / 2 - hw, fy0, hw * 2, fh, fr * 0.6, frameC, outA, false);
        if (e < 0.05)
        {
            env.Body = null;
            return;
        }
        // 模块接缝、铆钉
        var mods = I(Pm, "mods");
        var mw = fw / mods;
        for (var i = 1; i < mods; i++)
        {
            var x = fx0 + i * mw;
            if (Math.Abs(x - W / 2) < hw) env.Rect(x - 1, fy0, 2, fh, J.Mix(frameC, "#000000", 0.35), outA, false);
        }
        for (var i = 0; i < mods; i++)
            foreach (var yy in new[] { fy0 + fr * 0.5, fy0 + fh - fr * 0.5 })
                foreach (var xx in new[] { fx0 + i * mw + fr * 0.6, fx0 + (i + 1) * mw - fr * 0.6 })
                    if (Math.Abs(xx - W / 2) < hw)
                        env.Circle(xx, yy, fr * 0.14, J.Mix(frameC, LightOf(sc), 0.3), null, 0, outA, false);
        var panels = new List<(double X, double Y, double W, double H)>();
        var mainY = info == "top" ? fy0 + fr + infoH + fr * 0.6 : fy0 + fr;
        panels.Add((W / 2 - hw + fr, mainY, hw * 2 - fr * 2, ph));
        if (infoH != 0)
            panels.Add((W / 2 - hw + fr, info == "top" ? fy0 + fr : mainY + ph + fr * 0.6, hw * 2 - fr * 2, infoH));
        double Snap(double v, double q) => Math.Round(v / q) * q;
        var T = Math.Max(3, (int)Math.Round(p * (env.Scale != 0 ? env.Scale : 1)));
        SKShader? Pat(string kind, string col, string bg, double x0, double y0)
        {
            var img = LedTile(kind, col, bg, T);
            var m = SKMatrix.Concat(SKMatrix.CreateTranslation((float)x0, (float)y0), SKMatrix.CreateScale((float)(p / T), (float)(p / T)));
            return img.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, m);
        }

        void FillPat(SKShader sh, double x, double y, double w, double h, double a)
        {
            using var paint = new SKPaint
            {
                IsAntialias = false,
                Color = new SKColor(255, 255, 255, (byte)Math.Round(J.Clamp(a) * 255)),
                Shader = sh,
            };
            env.Ctx.DrawRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), paint);
        }

        BBox? bb = null;
        for (var pi = 0; pi < panels.Count; pi++)
        {
            var P0 = panels[pi];
            if (P0.W <= 2) continue;
            var gx = P0.X;
            var gy = P0.Y;
            env.Rect(P0.X, P0.Y, P0.W, P0.H, panel, outA, false);
            if (env.Pass == "main")
            {
                using var pt = Pat("dot", J.Mix(panel, pi != 0 ? infoC : ledC, 0.13), "", gx, gy);
                if (pt != null) FillPat(pt, P0.X, P0.Y, P0.W, P0.H, outA);
            }
            env.Ctx.Save();
            env.Ctx.ClipRect(new SKRect((float)P0.X, (float)P0.Y, (float)(P0.X + P0.W), (float)(P0.Y + P0.H)));
            if (pi == 0)
            {
                var m = JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.08 });
                var fits = m.W <= P0.W * 0.94;
                var cyy = P0.Y + P0.H / 2;
                if (fits)
                {
                    var tIn = J.Clamp(cut.Dur * 0.28, 0.35, 0.9);
                    var tOut = Math.Max(0.2, cut.OutDur != 0 ? cut.OutDur : 0.3);
                    var a = J.E.OutCubic(J.Clamp(lt / tIn));
                    var b = J.E.InCubic(J.Clamp((lt - (cut.Dur - tOut)) / tOut));
                    var x = J.Lerp(P0.X + P0.W + m.W / 2, W / 2.0, a) - b * (W / 2 - P0.X + m.W / 2);
                    bb = JLayouts.MainDraw(env, new JObject
                    {
                        ["text"] = text,
                        ["font"] = font,
                        ["size"] = size,
                        ["x"] = Snap(x, p),
                        ["y"] = cyy,
                        ["track"] = 0.08,
                        ["color"] = ledC,
                        ["plain"] = true,
                        ["noHold"] = true,
                    });
                }
                else
                {
                    var per = m.W + size * 2;
                    var v = Math.Max(W * 0.35, per / Math.Max(0.8, cut.Dur * 0.9));
                    var off = P0.X + P0.W - env.Ltb * v % per;
                    for (var k = 0; k < 3; k++)
                    {
                        var x = off + m.W / 2 + k * per;
                        if (x - m.W / 2 > P0.X + P0.W || x + m.W / 2 < P0.X) continue;
                        var r = JLayouts.MainDraw(env, new JObject
                        {
                            ["text"] = text,
                            ["font"] = font,
                            ["size"] = size,
                            ["x"] = Snap(x, p),
                            ["y"] = cyy,
                            ["track"] = 0.08,
                            ["color"] = ledC,
                            ["plain"] = true,
                            ["noHold"] = true,
                            ["mi"] = 0,
                        });
                        bb ??= r;
                    }
                }
            }
            else if (env.Pass == "main")
            {
                var ac = AltCopy(env);
                var infoTxt = $"LINE {LineNo(env)}  ◆  {J.FmtTime(cut.Start)}{(Regex.IsMatch(ac, "^No\\.") ? "" : "  ◆  " + ac)}   ◆   ";
                var fs = P0.H * 0.72;
                var im = JLayouts.Measure(new JObject { ["text"] = infoTxt, ["font"] = font, ["size"] = fs, ["track"] = 0.1 });
                var off = env.Ltb * size * 2.2 % im.W;
                for (var k = 0; k < 4; k++)
                {
                    var x = Snap(P0.X - off + k * im.W, p);
                    if (x > P0.X + P0.W) break;
                    env.DrawItem(new JObject
                    {
                        ["text"] = infoTxt,
                        ["font"] = font,
                        ["size"] = fs,
                        ["align"] = "left",
                        ["x"] = x,
                        ["y"] = P0.Y + P0.H / 2,
                        ["track"] = 0.1,
                        ["color"] = infoC,
                        ["alpha"] = outA * Tin(env, 0.2, 0.3),
                        ["ghost"] = false,
                    });
                }
            }
            env.Ctx.Restore();
            // 打 LED 孔：点阵间隙统一填回面板色
            if (env.Pass == "main")
            {
                using var pt = Pat("mask", "", panel, gx, gy);
                if (pt != null) FillPat(pt, P0.X, P0.Y, P0.W, P0.H, outA);
            }
        }
        env.Body = bb != null
            ? Box(Math.Max(panels[0].X, bb.X0), panels[0].Y, Math.Min(panels[0].X + panels[0].W, bb.X1), panels[0].Y + panels[0].H)
            : Box(fx0, fy0, fx0 + fw, fy0 + fh);
    }

    // ---------------- billboard（源 L2412-2546） ----------------

    private static void DrawBillboard(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var text = Brk(cut.Text, port ? 5 : 9);
        var outA = Tout(env);
        var bw = W * (port ? 0.86 : 0.72);
        var bh = Math.Min(H * (port ? 0.3 : 0.42), bw * 0.5);
        var bx = W / 2 - bw / 2;
        var by = H * (port ? 0.3 : 0.16);
        var groundY = H * 0.94;
        var rise = (1 - J.E.OutCubic(J.Clamp(lt / 0.55))) * H * 0.08 + J.E.InCubic(env.POut) * H * 0.05;
        var y0 = by + rise;
        var a0 = Tin(env, 0, 0.35, J.E.OutCubic) * outA;
        var steel = J.Mix(DarkOf(sc), sc.Str("sub")!, 0.35);
        var faceC = S(Pm, "face") == "accent" || J.Lum(sc.Str("bg") ?? "#000000") > 0.55
            ? (J.Lum(sc.Str("bg") ?? "#000000") > 0.55
                ? new[] { sc.Str("accent"), sc.Str("ink"), sc.Str("fg") }.FirstOrDefault(c => J.Contrast(c ?? "#000000", sc.Str("bg") ?? "#000000") >= 1.8) ?? sc.Str("fg")
                : PlateCol(sc, new[] { sc.Str("accent"), sc.Str("ink") }))
            : J.Mix(LightOf(sc), sc.Str("accent")!, 0.06);
        var tc = OnCol(sc, faceC!);
        // 立柱、斜撑、检修走道
        var pw = bw * 0.035;
        foreach (var px in new[] { bx + bw * 0.22, bx + bw * 0.78 })
            env.Rect(px - pw / 2, y0 + bh, pw, groundY - y0 - bh, steel, a0, false);
        var lw = Math.Max(1.2, u * 0.0025);
        var x1 = bx + bw * 0.22;
        var x2 = bx + bw * 0.78;
        var yb = y0 + bh + (groundY - y0 - bh) * 0.25;
        var yb2 = groundY - (groundY - y0 - bh) * 0.1;
        env.Line(new[] { new[] { x1, yb }, new[] { x2, yb2 } }, steel, lw, a0, false);
        env.Line(new[] { new[] { x2, yb }, new[] { x1, yb2 } }, steel, lw, a0, false);
        var cwY = y0 + bh + bh * 0.1;
        env.Rect(bx + bw * 0.04, cwY, bw * 0.92, bh * 0.025, steel, a0, false);
        for (var i = 0; i <= 10; i++)
            env.Rect(bx + bw * (0.04 + 0.92 * i / 10) - lw / 2, cwY - bh * 0.07, lw, bh * 0.07, steel, a0 * 0.8, false);
        env.Line(new[] { new[] { bx + bw * 0.04, cwY - bh * 0.07 }, new[] { bx + bw * 0.96, cwY - bh * 0.07 } }, steel, lw, a0 * 0.8, false);
        env.Line(new[] { new[] { W * 0.03, groundY }, new[] { W * 0.97, groundY } }, sc.Str("sub"), lw, 0.5 * a0, false);
        env.Rect(bx - bw * 0.015, y0 - bw * 0.015, bw * 1.03, bh + bw * 0.03, steel, a0, false);
        env.Rect(bx, y0, bw, bh, J.Mix(faceC!, DarkOf(sc), 0.28), a0, GIn(env));
        // 灯逐个闪亮，光锥扫过板面
        var L = I(Pm, "lamps");
        var lampOn = new List<double>();
        for (var i = 0; i < L; i++)
        {
            var t1 = 0.15 + i * 0.12;
            var on = lt > t1 + 0.25 ? 1.0 : lt > t1 ? (J.R(s, i, (int)env.Step, 7) < 0.55 ? 1 : 0.1) : 0;
            on *= 1 - J.E.InCubic(J.Clamp(env.POut * 1.6 - i * 0.12));
            lampOn.Add(on);
        }
        if (env.Pass == "main")
        {
            env.Ctx.Save();
            env.Ctx.ClipRect(new SKRect((float)bx, (float)y0, (float)(bx + bw), (float)(y0 + bh)));
            var sweep = D(Pm, "sweep");
            for (var i = 0; i < L; i++)
            {
                if (lampOn[i] <= 0.01) continue;
                var lx = bx + bw * (i + 0.5) / L;
                var ly = y0 + bh + bh * 0.06;
                var ang = (-90 + Math.Sin(env.Ltb * 0.8 + i * 1.7) * sweep) * J.DEG;
                var tx = lx + Math.Cos(ang) * bh * 1.25;
                var ty = ly + Math.Sin(ang) * bh * 1.25;
                using var g = SKShader.CreateRadialGradient(
                    new SKPoint((float)lx, (float)ly), (float)(bh * 1.2),
                    new[] { SkRgba(faceC, 0.95 * lampOn[i]), SkRgba(faceC, 0.6 * lampOn[i]), SkRgba(faceC, 0) },
                    new float[] { 0, 0.6f, 1 }, SKShaderTileMode.Clamp);
                var spread = bw / L * 0.75;
                using var path = new SKPath();
                path.MoveTo((float)(lx - bw * 0.02), (float)ly);
                path.LineTo((float)(tx - spread), (float)ty);
                path.LineTo((float)(tx + spread), (float)ty);
                path.LineTo((float)(lx + bw * 0.02), (float)ly);
                path.Close();
                using var p = new SKPaint { IsAntialias = true, Shader = g, Color = SKColors.White };
                env.Ctx.DrawPath(path, p);
            }
            var lit = lampOn.Sum() / L;
            using var fp = new SKPaint { Color = SkRgba(faceC, 0.55 * lit * a0) };
            env.Ctx.DrawRect(new SKRect((float)bx, (float)y0, (float)(bx + bw), (float)(y0 + bh)), fp);
            env.Ctx.Restore();
        }
        // 灯罩
        for (var i = 0; i < L; i++)
        {
            var lx = bx + bw * (i + 0.5) / L;
            var ly = y0 + bh + bh * 0.06;
            env.Line(new[] { new[] { lx, cwY }, new[] { lx, ly + bh * 0.02 } }, steel, lw * 1.5, a0, false);
            env.Poly(new[]
            {
                new[] { lx - bh * 0.05, ly + bh * 0.03 }, new[] { lx + bh * 0.05, ly + bh * 0.03 },
                new[] { lx + bh * 0.035, ly - bh * 0.03 }, new[] { lx - bh * 0.035, ly - bh * 0.03 },
            }, steel, a0, false);
            if (lampOn[i] > 0.5) env.Circle(lx, ly - bh * 0.03, bh * 0.025, LightOf(sc), null, 0, a0, false);
        }
        var font = S(Pm, "font")!;
        var size = Math.Min(JLayouts.FitSize(text, font, bw * 0.84, bh * 0.66, new JObject { ["track"] = 0.04, ["lead"] = 1.1 }), bh * 0.6);
        var tag = B(Pm, "tag");
        var bb = JLayouts.MainDraw(env, new JObject
        {
            ["text"] = text,
            ["font"] = font,
            ["size"] = size,
            ["x"] = W / 2.0,
            ["y"] = y0 + bh / 2 + (tag ? bh * 0.03 : 0),
            ["track"] = 0.04,
            ["lead"] = 1.1,
            ["color"] = tc,
            ["noHold"] = PlateHold(env),
            ["mi"] = MiAt(env, 0.3),
        });
        if (tag)
        {
            var fs = Math.Max(SmallSize(env) * 0.8, bh * 0.05);
            env.DrawItem(new JObject
            {
                ["text"] = "No." + LineNo(env),
                ["font"] = MonoF(env),
                ["size"] = fs,
                ["align"] = "left",
                ["track"] = 0.2,
                ["x"] = bx + bw * 0.025,
                ["y"] = y0 + fs * 1.1,
                ["color"] = tc,
                ["alpha"] = 0.7 * a0,
                ["ghost"] = false,
            });
            env.DrawItem(new JObject
            {
                ["text"] = J.FmtTime(cut.Start),
                ["font"] = MonoF(env),
                ["size"] = fs,
                ["align"] = "right",
                ["track"] = 0.2,
                ["x"] = bx + bw * 0.975,
                ["y"] = y0 + fs * 1.1,
                ["color"] = tc,
                ["alpha"] = 0.7 * a0,
                ["ghost"] = false,
            });
        }
        env.Body = bb ?? Box(bx, y0, bx + bw, y0 + bh);
    }

    // ---------------- crowdBubbles（源 L2548-2669） ----------------

    private static readonly string[] React = { "…", "！？", "♪", "？", "！", "…！", "♡" };

    private static void DrawCrowdBubbles(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var font = S(Pm, "font")!;
        // 帧不变量（性能专项）：文案碎片/槽位/主盒度量只由 (cut, W, H) 与 plan 静态量决定，
        // 逐帧重建（ChunkText/SplitLines/OrderBy/List）曾是本版式 ~160KB/帧的分配大头
        var inv = CutCache.Get<CrowdInv>(cut, "crowdBubbles");
        if (inv == null)
        {
            var text0 = Brk(cut.Text, port ? 5 : 8);
            var size0 = Math.Min(JLayouts.FitSize(text0, font, W * (port ? 0.62 : 0.46), H * (port ? 0.22 : 0.3),
                new JObject { ["track"] = 0.03, ["lead"] = 1.15 }), u * 0.15);
            var m0 = JLayouts.Measure(new JObject { ["text"] = text0, ["font"] = font, ["size"] = size0, ["track"] = 0.03, ["lead"] = 1.15 });
            var words0 = new List<string>();
            var src = Planner.ChunkText(Flat(string.IsNullOrEmpty(cut.LineText) ? cut.Text : cut.LineText))
                .Concat(Planner.ChunkText(Flat(cut.Text)));
            foreach (var w in src)
                foreach (var q0 in JLayouts.SplitLines(w, 4).Split('\n'))
                    if (q0.Length > 0 && !words0.Contains(q0)) words0.Add(q0);
            var slots0 = new List<(double X, double Y, double R)>();
            var bw0 = m0.W + size0 * 1.4;
            var bh0 = m0.H + size0 * 1.1;
            var cx0 = W / 2.0;
            var cy0 = H / 2 - bh0 * 0.05;
            var gxN0 = port ? 3 : 6;
            var gyN0 = port ? 8 : 5;
            for (var gy = 0; gy < gyN0; gy++)
                for (var gx = 0; gx < gxN0; gx++)
                {
                    var x = W * (0.1 + 0.8 * (gx + 0.5) / gxN0) + J.Rs(s, gx, gy, 1) * W * 0.04;
                    var y = H * (0.1 + 0.8 * (gy + 0.5) / gyN0) + J.Rs(s, gx, gy, 2) * H * 0.03;
                    if (Math.Abs(x - cx0) < bw0 * 0.5 + W * (port ? 0.12 : 0.07) && Math.Abs(y - cy0) < bh0 * 0.5 + H * (port ? 0.04 : 0.07)) continue;
                    slots0.Add((x, y, J.R(s, gx, gy, 3)));
                }
            slots0 = slots0.OrderBy(v => v.R).ToList();
            inv = new CrowdInv { Text = text0, Size = size0, Mw = m0.W, Mh = m0.H, Words = words0, Slots = slots0 };
            CutCache.Put(cut, "crowdBubbles", inv);
        }
        var text = inv.Text;
        var size = inv.Size;
        var mW = inv.Mw;
        var mH = inv.Mh;
        var outA = Tout(env);
        var bw = mW + size * 1.4;
        var bh = mH + size * 1.1;
        var cx = W / 2.0;
        var cy = H / 2 - bh * 0.05;
        var bigC = new[] { sc.Str("accent"), LightOf(sc) }
            .FirstOrDefault(c => J.Contrast(c ?? "#000000", sc.Str("bg") ?? "#000000") >= 1.8) ?? LightOf(sc);
        var tc = OnCol(sc, bigC);
        var words = inv.Words;
        string Frag(int i)
        {
            var r = (int)(J.H(s, i, 41) % 10);
            if (r < 6 && words.Count > 0) return words[(int)(J.H(s, i, 42) % (uint)words.Count)];
            return React[(int)(J.H(s, i, 43) % (uint)React.Length)];
        }

        var slots = inv.Slots;
        var cnt = Math.Min(I(Pm, "count"), slots.Count);
        var gxN = port ? 3 : 6;
        var gyN = port ? 8 : 5;
        var ss = Math.Max(SmallSize(env) * 1.4, Math.Min(size * 0.42, u * 0.05));
        var style = S(Pm, "style");
        var sfFont = S(Pm, "sf")!;
        void Bubble(double x, double y, double w, double h, string kind, double tailDir, string? fill, string? stroke, double a, bool gh)
        {
            List<double[]> pts;
            if (kind == "rect") pts = RrPts(x - w / 2, y - h / 2, w, h, h * 0.35, 4);
            else
            {
                pts = new List<double[]>();
                var N = kind == "shout" ? 22 : 36;
                for (var i = 0; i < N; i++)
                {
                    var t = i / (double)N * J.TAU;
                    var rr = kind == "shout" ? (i % 2 != 0 ? 0.8 : 1.12) : 1;
                    pts.Add(new[] { x + Math.Cos(t) * w / 2 * rr, y + Math.Sin(t) * h / 2 * rr });
                }
            }
            var tx = x + tailDir * w * 0.18;
            var ty = y + h * 0.42;
            var tail = new[]
            {
                new[] { tx - w * 0.06 * tailDir, ty - h * 0.05 },
                new[] { tx + w * 0.02 * tailDir, ty - h * 0.05 },
                new[] { tx - w * 0.1 * tailDir, y + h * 0.5 + h * 0.3 },
            };
            if (fill != null)
            {
                env.Poly(pts.ToArray(), fill, a, gh);
                env.Poly(tail, fill, a, gh);
            }
            if (stroke != null)
            {
                Outline(env, pts.ToArray(), stroke, Math.Max(1.2, u * 0.0025), a);
                env.Line(new[] { tail[0], tail[2], tail[1] }, stroke, Math.Max(1.2, u * 0.0025), a, false);
                if (fill == null) env.Poly(tail, sc.Str("bg"), a, false);
            }
        }

        // 人群：每个气泡活一阵就破，另一个顶上
        if (env.Pass == "main")
        {
            var life = J.Clamp(cut.Dur * 0.7, 1.2, 3);
            var jx = W * 0.8 / gxN * 0.18;
            var jy = H * 0.8 / gyN * 0.14;
            for (var i = 0; i < cnt; i++)
            {
                var t0 = 0.05 + J.R(s, i, 5) * Math.Min(0.6, cut.Dur * 0.25);
                var gen = Math.Max(0, (int)Math.Floor((env.Ltb - t0) / life));
                var tl = env.Ltb - t0 - gen * life;
                if (env.Ltb < t0) continue;
                var e = J.E.OutBack(J.Clamp(tl / 0.22), 2.2) * (1 - J.E.InCubic(J.Clamp((tl - life + 0.2) / 0.2))) * outA;
                if (e <= 0.01) continue;
                var sl0 = slots[i];
                var slX = sl0.X + J.Rs(s, i, gen, 44) * jx;
                var slY = sl0.Y + J.Rs(s, i, gen, 45) * jy;
                var t = Frag(i * 7 + gen);
                var fs = ss * J.Rr(0.8, 1.15, s, i, gen);
                var tm = JLayouts.Measure(new JObject { ["text"] = t, ["font"] = sfFont, ["size"] = fs, ["track"] = 0.04 });
                var w = (tm.W + fs * 1.4) * e;
                var h = fs * 2 * e;
                var filled = style == "filled" || (style == "mixed" && J.R(s, i, 9) < 0.45);
                var fcol = filled ? J.Mix(sc.Str("bg")!, sc.Str("fg")!, 0.14) : null;
                Bubble(slX, slY, w, h, J.R(s, i, gen, 8) < 0.5 ? "rect" : "round", slX < cx ? 1 : -1,
                    fcol ?? sc.Str("bg")!, filled ? null : sc.Str("sub"), 0.95, false);
                if (e > 0.5)
                    env.DrawItem(new JObject
                    {
                        ["text"] = t,
                        ["font"] = sfFont,
                        ["size"] = fs,
                        ["track"] = 0.04,
                        ["x"] = slX,
                        ["y"] = slY,
                        ["color"] = filled ? sc.Str("fg") : sc.Str("sub"),
                        ["alpha"] = J.Clamp((e - 0.5) * 2),
                        ["ghost"] = false,
                    });
            }
        }
        // 装歌词的大气泡
        var q = J.E.OutBack(J.Clamp((lt - 0.05) / 0.3), 1.8) * (1 - 0.2 * J.E.InCubic(env.POut));
        var bk = S(Pm, "big")!;
        Bubble(cx, cy, bw * q * (bk == "shout" ? 1.25 : 1), bh * q * (bk == "shout" ? 1.3 : 1), bk, D(Pm, "side", 1),
            bigC, null, outA, GIn(env));
        env.Body = JLayouts.MainDraw(env, new JObject
        {
            ["text"] = text,
            ["font"] = font,
            ["size"] = size * Math.Min(1, q + 0.2),
            ["x"] = cx,
            ["y"] = cy,
            ["color"] = tc,
            ["noHold"] = PlateHold(env),
            ["mi"] = MiAt(env, 0.12),
            ["track"] = 0.03,
            ["lead"] = 1.15,
        });
    }

    // ---------------- crossword（源 L2671-2841） ----------------

    private static void DrawCrossword(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var t0 = Flat(cut.Text);
        var chs = JLayouts.CodePoints(t0).Where(c => c != " ").ToList();
        var n = chs.Count;
        if (n == 0)
        {
            env.Body = null;
            return;
        }
        var maxRow = port ? 7 : 12;
        var words = n > maxRow
            ? ChunksK(t0, (int)Math.Ceiling(n / (double)maxRow)).Select(w => JLayouts.CodePoints(w).Where(c => c != " ").ToList()).ToList()
            : new List<List<string>> { chs };
        var L = words.Max(w => w.Count);
        var C = Math.Max(L + 2, port ? 7 : 9);
        var R = words.Count > 1 ? 7 : port ? 7 : 5;
        var below = B(Pm, "clue") && H > W * 0.7;
        var clueW = B(Pm, "clue") && !below ? 0.24 : 0;
        var cell = Math.Min(W * (0.88 - clueW) / C, Math.Min(H * (below ? 0.64 : 0.8) / R, u * 0.14));
        var gw = C * cell;
        var gh = R * cell;
        var fsC = Math.Max(SmallSize(env) * 1.15, cell * 0.24);
        var gx = (W - gw - (clueW != 0 ? W * clueW : 0)) / 2;
        var gy = (H - gh - (below ? fsC * 5 : 0)) / 2;
        var rowsW = words.Count > 1 ? new List<int> { 1, 5 } : new List<int> { (int)Math.Floor(R / 2.0) };
        var c0 = words.Select(w => (int)Math.Floor((C - w.Count) / 2.0) - (J.H(s, w.Count) % 2 != 0 && C - w.Count > 2 ? 1 : 0)).ToList();
        // 黑格对称散布，但保住歌词词条和首尾封口格
        int Key(int r, int c) => r * 64 + c;
        var lyr = new Dictionary<int, (string Ch, int Wi, int J)>();
        for (var wi = 0; wi < words.Count; wi++)
            for (var j = 0; j < words[wi].Count; j++)
                lyr[Key(rowsW[wi], c0[wi] + j)] = (words[wi][j], wi, j);
        var black = new HashSet<int>();
        for (var r = 0; r < R; r++)
            for (var c = 0; c < C; c++)
            {
                var r2 = R - 1 - r;
                var c2 = C - 1 - c;
                if (lyr.ContainsKey(Key(r, c)) || lyr.ContainsKey(Key(r2, c2))) continue;
                if (J.R(s, Math.Min(Key(r, c), Key(r2, c2)), 3) < D(Pm, "dens"))
                {
                    black.Add(Key(r, c));
                    black.Add(Key(r2, c2));
                }
            }
        for (var wi = 0; wi < words.Count; wi++)
        {
            var r = rowsW[wi];
            if (c0[wi] > 0) black.Add(Key(r, c0[wi] - 1));
            if (c0[wi] + words[wi].Count < C) black.Add(Key(r, c0[wi] + words[wi].Count));
        }
        bool IsW(int r, int c) => r >= 0 && c >= 0 && r < R && c < C && !black.Contains(Key(r, c));
        // 横竖词条起点编号
        var num = new Dictionary<int, int>();
        var kk = 1;
        for (var r = 0; r < R; r++)
            for (var c = 0; c < C; c++)
            {
                if (!IsW(r, c)) continue;
                var ac = !IsW(r, c - 1) && IsW(r, c + 1);
                var dn = !IsW(r - 1, c) && IsW(r + 1, c);
                if (ac || dn) num[Key(r, c)] = kk++;
            }
        // 铅笔交叉词，穿过部分歌词格的竖词
        var pool = PoolOf(cut);
        var NP = pool.Count;
        var pencil = new Dictionary<int, string>();
        var fills = 0;
        for (var wi = 0; wi < words.Count; wi++)
            for (var j = 0; j < words[wi].Count; j++)
            {
                if (fills >= I(Pm, "fill") * words.Count || J.R(s, wi, j, 5) > 0.45) continue;
                var c = c0[wi] + j;
                var r = rowsW[wi];
                var a = r;
                var b = r;
                while (IsW(a - 1, c)) a--;
                while (IsW(b + 1, c)) b++;
                if (b - a < 2) continue;
                fills++;
                for (var rr = a; rr <= b; rr++)
                    if (!lyr.ContainsKey(Key(rr, c))) pencil[Key(rr, c)] = pool[(int)(J.H(s, rr, c, 6) % (uint)NP)];
            }
        var outA = Tout(env);
        var ga = Tin(env, 0, 0.35, J.E.OutCubic) * outA;
        var paperC = LightOf(sc);
        var inkC = DarkOf(sc);
        var lw = Math.Max(1, cell * 0.035);
        env.Rect(gx - lw * 2, gy - lw * 2, gw + lw * 4, gh * ga + lw * 4, inkC, outA, false);
        env.Rect(gx, gy, gw, gh * ga, paperC, outA, GIn(env));
        // 打字进度
        const double T1 = 0.25;
        var per = J.Clamp(cut.Dur * 0.4 / n, 0.04, 0.11);
        var typed = (int)Math.Floor((lt - T1) / per);
        var cur = -1;
        var order = new List<(int Wi, int J)>();
        for (var wi = 0; wi < words.Count; wi++)
            for (var j = 0; j < words[wi].Count; j++)
                order.Add((wi, j));
        if (typed >= 0 && typed < n) cur = typed;
        var curWi = cur >= 0 ? order[cur].Wi : -1;
        var font = S(Pm, "font")!;
        for (var r = 0; r < R; r++)
        {
            var ra = J.Clamp(ga * R - r);
            if (ra <= 0) continue;
            for (var c = 0; c < C; c++)
            {
                var x = gx + c * cell;
                var y = gy + r * cell;
                var k2 = Key(r, c);
                if (black.Contains(k2))
                {
                    env.Rect(x, y, cell, cell, inkC, ra * outA, false);
                    continue;
                }
                if (lyr.TryGetValue(k2, out var L0) && (L0.Wi == curWi || typed >= n))
                    env.Rect(x, y, cell, cell, sc.Str("accent"), (L0.Wi == curWi ? 0.2 : 0.12) * outA, false);
                if (lyr.TryGetValue(k2, out var L1) && cur >= 0 && cur < order.Count && order[cur].Wi == L1.Wi && order[cur].J == L1.J)
                    env.Rect(x, y, cell, cell, sc.Str("accent"), 0.5 * outA, false);
                env.Line(new[] { new[] { x, y + cell }, new[] { x + cell, y + cell } }, inkC, lw * 0.6, 0.55 * ra * outA, false);
                env.Line(new[] { new[] { x + cell, y }, new[] { x + cell, y + cell } }, inkC, lw * 0.6, 0.55 * ra * outA, false);
                if (num.TryGetValue(k2, out var nv))
                    env.DrawItem(new JObject
                    {
                        ["text"] = nv.ToString(CultureInfo.InvariantCulture),
                        ["font"] = MonoF(env),
                        ["size"] = cell * 0.2,
                        ["align"] = "left",
                        ["x"] = x + cell * 0.07,
                        ["y"] = y + cell * 0.15,
                        ["color"] = inkC,
                        ["alpha"] = 0.8 * ra * outA,
                        ["ghost"] = false,
                    });
                if (pencil.TryGetValue(k2, out var pc))
                    env.DrawItem(new JObject
                    {
                        ["text"] = pc,
                        ["font"] = BodyF(env),
                        ["size"] = cell * 0.46,
                        ["x"] = x + cell / 2,
                        ["y"] = y + cell * 0.56,
                        ["color"] = J.Mix(paperC, inkC, 0.38),
                        ["alpha"] = Tin(env, 0.3 + r * 0.03, 0.3) * outA,
                        ["ghost"] = false,
                    });
            }
        }
        BBox? bb = null;
        for (var q = 0; q < order.Count; q++)
        {
            var (wi, j) = order[q];
            var ch = words[wi][j];
            var x = gx + (c0[wi] + j + 0.5) * cell;
            var y = gy + (rowsW[wi] + 0.56) * cell;
            bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
            {
                ["text"] = ch,
                ["font"] = font,
                ["size"] = cell * 0.7,
                ["x"] = x,
                ["y"] = y,
                ["color"] = inkC,
                ["noHold"] = PlateHold(env),
                ["mi"] = MiAt(env, T1 + q * per),
            }));
        }
        if (B(Pm, "clue"))
        {
            var fs = fsC;
            var cxl = below ? gx : gx + gw + W * 0.035;
            var cyl = below ? gy + gh + fs * 1.6 : gy + fs;
            var a = Tin(env, 0.35, 0.4, J.E.OutCubic) * outA;
            var n1 = num.TryGetValue(Key(rowsW[0], c0[0]), out var nv1) ? nv1 : 1;
            var lines = new List<(string T, string? Col, string F)>
            {
                ("横之钥", sc.Str("accent"), MonoF(env)),
                (n1 + "  " + (string.IsNullOrEmpty(cut.Note) ? RomajiOf(env) ?? "(" + n + ")" : cut.Note), sc.Str("fg"), BodyF(env)),
                ("竖之钥", sc.Str("accent"), MonoF(env)),
                ((num.Count > 3 ? 3 : 2) + "  ─", sc.Str("sub"), BodyF(env)),
            };
            for (var i = 0; i < lines.Count; i++)
            {
                var (t, col, f) = lines[i];
                var tt = t.Length > 22 ? t.Substring(0, 21) + "…" : t;
                var yy = below ? cyl + i % 2 * fs * 1.7 : cyl + i * fs * 1.9 + (i >= 2 ? fs * 0.8 : 0);
                var xx = below && i >= 2 ? cxl + gw * 0.55 : cxl;
                env.DrawItem(new JObject
                {
                    ["text"] = tt,
                    ["font"] = f,
                    ["size"] = fs,
                    ["align"] = "left",
                    ["x"] = xx,
                    ["y"] = yy,
                    ["track"] = 0.08,
                    ["color"] = col,
                    ["alpha"] = a,
                    ["ghost"] = false,
                });
            }
        }
        env.Body = bb ?? Box(gx, gy, gx + gw, gy + gh);
    }

    // ---------------- wordSearch（源 L2843-3006） ----------------

    private static void DrawWordSearch(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var chs = JLayouts.CodePoints(Flat(cut.Text)).Where(c => c != " ").ToList();
        var n = chs.Count;
        if (n == 0)
        {
            env.Body = null;
            return;
        }
        var port = IsPort(env);
        var dir = S(Pm, "dir") ?? "h";
        if (dir == "v" && n > 14) dir = "h";
        if (dir == "d" && n > 9) dir = "h";
        int C, R;
        var pad = I(Pm, "pad");
        if (dir == "h")
        {
            C = n + pad * 2;
            R = port ? Math.Max(7, (int)Math.Round(C * 1.4)) : Math.Max(5, (int)Math.Round(C * 0.5));
            if (port && C > 9) C = n + 1;
        }
        else if (dir == "v")
        {
            R = n + pad * 2;
            C = Math.Max(5, (int)Math.Round(R * (W / (double)H) * 1.1));
        }
        else C = R = n + 2;
        C = Math.Min(C, 18);
        R = Math.Min(R, Math.Min(18, Math.Max(dir == "v" ? n + 1 : dir == "d" ? n : 5, (int)Math.Floor(150.0 / C))));
        if (dir == "v") C = Math.Min(C, Math.Max(5, (int)Math.Floor(150.0 / R)));
        var cell = Math.Min(W * 0.86 / C, Math.Min(H * 0.8 / R, u * 0.16));
        var gx = W / 2 - C * cell / 2;
        var gy = H / 2 - R * cell / 2;
        // 歌词藏的路径
        int r0, c0, dr = 0, dc = 1;
        if (dir == "h")
        {
            r0 = (int)Math.Floor(R / 2.0) + (int)(J.H(s, 1) % 3) - 1;
            c0 = (int)Math.Floor((C - n) / 2.0);
        }
        else if (dir == "v")
        {
            dr = 1;
            dc = 0;
            c0 = (int)Math.Floor(C / 2.0) + (int)(J.H(s, 1) % 3) - 1;
            r0 = (int)Math.Floor((R - n) / 2.0);
        }
        else
        {
            dr = 1;
            dc = 1;
            r0 = (int)Math.Floor((R - n) / 2.0);
            c0 = (int)Math.Floor((C - n) / 2.0);
        }
        r0 = J.Clamp(r0, 0, R - 1 - dr * (n - 1));
        c0 = J.Clamp(c0, 0, C - 1 - dc * (n - 1));
        var onPath = new Dictionary<int, int>();
        for (var i = 0; i < chs.Count; i++) onPath[(r0 + dr * i) * 64 + (c0 + dc * i)] = i;
        var pool = PoolOf(cut);
        var NP = pool.Count;
        var outA = Tout(env);
        var ga = Tin(env, 0, 0.4, J.E.OutCubic) * outA;
        var panelC = J.Mix(sc.Str("bg")!, sc.Str("fg")!, J.Lum(sc.Str("bg") ?? "#000000") < 0.5 ? 0.06 : 0.05);
        env.RRect(gx - cell * 0.35, gy - cell * 0.35, C * cell + cell * 0.7, R * cell + cell * 0.7, cell * 0.3,
            panelC, ga, false, J.Mix(sc.Str("bg")!, sc.Str("fg")!, 0.2), Math.Max(1, u * 0.0018));
        // 诱饵圈，像之前圈过的词
        void Cap(double ra, double ca, double rb, double cb, string? col, double a, bool stroke)
        {
            var x1 = gx + (ca + 0.5) * cell;
            var y1 = gy + (ra + 0.5) * cell;
            var x2 = gx + (cb + 0.5) * cell;
            var y2 = gy + (rb + 0.5) * cell;
            var ang = Math.Atan2(y2 - y1, x2 - x1);
            var len = Hypot(x2 - x1, y2 - y1);
            var rr = cell * 0.42;
            env.Ctx.Save();
            env.Ctx.Translate((float)x1, (float)y1);
            env.Ctx.RotateRadians((float)ang);
            env.RRect(-rr, -rr, len + rr * 2, rr * 2, rr, stroke ? null : col, a, false, stroke ? col : null, Math.Max(1.5, cell * 0.06));
            env.Ctx.Restore();
        }

        var decoys = I(Pm, "decoys");
        for (var d = 0; d < decoys; d++)
        {
            var rr = (int)(J.H(s, d, 21) % (uint)R);
            var len = 3 + (int)(J.H(s, d, 22) % 2);
            var cc = (int)(J.H(s, d, 23) % (uint)Math.Max(1, C - len));
            var clash = false;
            for (var q = 0; q < len; q++)
                if (onPath.ContainsKey(rr * 64 + cc + q)) clash = true;
            if (clash) continue;
            Cap(rr, cc, rr, cc + len - 1, sc.Str("sub"), 0.5 * ga, true);
        }
        // 格子字母，一个 item 按格摆字形（JS 的 letterSpacing 分支在 Skia 无对位，走 JS 的 charFn 回退分支）
        if (env.Pass == "main" && ga > 0)
        {
            var gs = cell * 0.56;
            var cellsTxt = new List<string>();
            var pos = new List<(double X, double Y, int R)>();
            for (var r = 0; r < R; r++)
                for (var c = 0; c < C; c++)
                {
                    if (onPath.ContainsKey(r * 64 + c)) continue;
                    cellsTxt.Add(pool[(int)(J.H(s, r, c, 9) % (uint)NP)]);
                    pos.Add((gx + (c + 0.5) * cell, gy + (r + 0.5) * cell, r));
                }
            var it = new JObject
            {
                ["text"] = string.Concat(cellsTxt),
                ["font"] = S(Pm, "gf"),
                ["size"] = gs,
                ["x"] = 0,
                ["y"] = 0,
                ["color"] = sc.Str("sub"),
                ["ghost"] = false,
                ["alpha"] = 0.75 * outA,
            };
            JText.DrawItem(env, it, new JText.TextDrawOpts
            {
                HideText = env.HideText,
                GlyphLog = env.GlyphLog,
                AllowFilter = env.AllowFilter,
                CharFn = (i, g, _) =>
                {
                    if (i < 0 || i >= pos.Count) return new CharXf { Hide = true };
                    var q = pos[i];
                    var gx0 = g != null ? (double)g["x"]! : 0;
                    var gy0 = g != null ? (double)g["y"]! : 0;
                    var a = J.Clamp(ga * (R + 2) - q.R);
                    return new CharXf { Dx = q.X - gx0, Dy = q.Y - gy0, A = a };
                },
            });
        }
        // 找到词：胶囊沿歌词扫过，字逐个点亮
        const double T1 = 0.35;
        var sweep = J.Clamp(cut.Dur * 0.3, 0.35, 0.8);
        var f = J.E.InOutCubic(J.Clamp((lt - T1) / sweep)) * (1 - J.E.InCubic(J.Clamp(env.POut * 1.3)));
        if (f > 0)
        {
            var endI = (n - 1) * f;
            Cap(r0, c0, r0 + dr * endI, c0 + dc * endI, sc.Str("accent"), 0.3 * outA, false);
            Cap(r0, c0, r0 + dr * endI, c0 + dc * endI, sc.Str("accent"), outA, true);
        }
        BBox? bb = null;
        var font = S(Pm, "font")!;
        for (var i = 0; i < chs.Count; i++)
        {
            var x = gx + (c0 + dc * i + 0.5) * cell;
            var y = gy + (r0 + dr * i + 0.5) * cell;
            var lit = J.Clamp(((n > 1 ? f * (n - 1) : f) - i + 0.6) / 0.6);
            bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
            {
                ["text"] = chs[i],
                ["font"] = font,
                ["size"] = cell * (0.56 + 0.08 * lit),
                ["x"] = x,
                ["y"] = y,
                ["color"] = lit > 0.5 ? sc.Str("fg") : sc.Str("sub"),
                ["alpha"] = 0.75 + 0.25 * lit,
                ["noHold"] = PlateHold(env),
                ["mi"] = MiAt(env, 0.1 + i * 0.02),
            }));
        }
        env.Body = bb ?? Box(gx, gy, gx + C * cell, gy + R * cell);
    }

    // ---------------- puzzle（源 L3008-3189） ----------------

    /// <summary>一条拼图边 (x0,y0)→(x1,y1)，s=±1 是凸凹方向，0 为直边（源 jigEdge）。</summary>
    private static void JigEdge(SKPath ctx, double x0, double y0, double x1, double y1, double s)
    {
        if (s == 0)
        {
            ctx.LineTo((float)x1, (float)y1);
            return;
        }
        var dx = x1 - x0;
        var dy = y1 - y0;
        var L = Hypot(dx, dy);
        var ux = dx / L;
        var uy = dy / L;
        var nx = uy * s;
        var ny = -ux * s;
        double[] P(double t, double h) => new[] { x0 + ux * t * L + nx * h * L, y0 + uy * t * L + ny * h * L };
        var a = P(0.37, 0);
        var b1 = P(0.42, 0.1);
        var b2 = P(0.3, 0.26);
        var c = P(0.5, 0.27);
        var d1 = P(0.7, 0.26);
        var d2 = P(0.58, 0.1);
        var e = P(0.63, 0);
        ctx.LineTo((float)a[0], (float)a[1]);
        ctx.CubicTo((float)b1[0], (float)b1[1], (float)b2[0], (float)b2[1], (float)c[0], (float)c[1]);
        ctx.CubicTo((float)d1[0], (float)d1[1], (float)d2[0], (float)d2[1], (float)e[0], (float)e[1]);
        ctx.LineTo((float)x1, (float)y1);
    }

    private static void JigPath(SKPath ctx, double x, double y, double w, double h, double t, double r, double b, double l)
    {
        ctx.MoveTo((float)x, (float)y);
        JigEdge(ctx, x, y, x + w, y, t);
        JigEdge(ctx, x + w, y, x + w, y + h, r);
        JigEdge(ctx, x + w, y + h, x, y + h, b);
        JigEdge(ctx, x, y + h, x, y, l);
        ctx.Close();
    }

    private static void DrawPuzzle(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var text = Brk(cut.Text, port ? 5 : 8);
        var o = new JObject { ["track"] = 0.04, ["lead"] = 1.12 };
        var font = S(Pm, "font")!;
        var size = Math.Min(JLayouts.FitSize(text, font, W * 0.7, H * (port ? 0.3 : 0.4), o), u * 0.22);
        var m = JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.04, ["lead"] = 1.12 });
        var bw = Math.Min(W * 0.92, m.W + size * 1.3);
        var bh = Math.Min(H * 0.8, m.H + size * 1.1);
        var x0 = W / 2 - bw / 2;
        var y0 = H / 2 - bh / 2;
        var R = I(Pm, "rows");
        var C = Math.Max(2, (int)Math.Round(bw / bh * R));
        var pw = bw / C;
        var phh = bh / R;
        var plateKey = S(Pm, "plate");
        var plate = plateKey == "light"
            ? J.Mix(LightOf(sc), sc.Str("accent")!, 0.08)
            : PlateCol(sc, plateKey == "accent" ? new[] { sc.Str("accent"), sc.Str("ink") } : new[] { sc.Str("ink"), sc.Str("accent") });
        var tc = OnCol(sc, plate);
        var outA = Tout(env);
        double HS(int r, int c) => (J.H(s, r, c, 1) & 1) == 1 ? 1 : -1; // r 行下方的横缝方向
        double VS(int r, int c) => (J.H(s, r, c, 2) & 1) == 1 ? 1 : -1; // c 列右侧的竖缝方向
        var N = R * C;
        var ord = Enumerable.Range(0, N).OrderBy(v => J.R(s, v, 3)).ToList();
        var rank = new int[N];
        for (var k = 0; k < ord.Count; k++) rank[ord[k]] = k;
        const double fd = 0.42;
        var span = J.Clamp(cut.Dur * 0.3, 0.3, 0.8);
        var last = B(Pm, "last");
        double TOf(int p)
        {
            var k = rank[p];
            if (last && k == N - 1) return 0.1 + span + 0.25;
            return 0.06 + (N > 1 ? k / (double)(N - 1) : 0) * span * (last ? 0.85 : 1);
        }

        double[] Edges(int r, int c) => new[]
        {
            r == 0 ? 0 : -HS(r - 1, c), c == C - 1 ? 0 : VS(r, c), r == R - 1 ? 0 : HS(r, c), c == 0 ? 0 : -VS(r, c - 1),
        };
        void Path(SKPath p, int r, int c)
        {
            var e = Edges(r, c);
            JigPath(p, x0 + c * pw, y0 + r * phh, pw, phh, e[0], e[1], e[2], e[3]);
        }

        BBox? DrawContent()
        {
            env.Rect(x0 - pw * 0.4, y0 - phh * 0.4, bw + pw * 0.8, bh + phh * 0.8, plate, 1, false);
            return JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = W / 2.0,
                ["y"] = H / 2.0,
                ["color"] = tc,
                ["noHold"] = PlateHold(env),
                ["mi"] = 0,
                ["track"] = 0.04,
                ["lead"] = 1.12,
            });
        }

        var settled = new List<(int R, int C)>();
        var moving = new List<(int R, int C, double F, double Fo)>();
        for (var r = 0; r < R; r++)
            for (var c = 0; c < C; c++)
            {
                var p = r * C + c;
                var f = J.Clamp((lt - TOf(p)) / fd);
                var fo = J.Clamp(env.POut * 1.5 - rank[p] / (double)N * 0.5);
                if (f <= 0) continue;
                if (f >= 1 && fo <= 0) settled.Add((r, c));
                else moving.Add((r, c, f, fo));
            }
        BBox? bb = null;
        var lw = Math.Max(1, u * 0.0018);
        if (settled.Count > 0)
        {
            using var path = new SKPath();
            foreach (var (r, c) in settled) Path(path, r, c);
            env.Ctx.Save();
            env.Ctx.ClipPath(path);
            bb = DrawContent();
            env.Ctx.Restore();
            if (env.Pass == "main")
            {
                env.Ctx.Save();
                using var sp = new SKPaint
                {
                    IsAntialias = true, IsStroke = true, StrokeWidth = (float)lw,
                    Color = SkRgba(J.Mix(plate, DarkOf(sc), 0.45), 0.55 * outA),
                };
                env.Ctx.DrawPath(path, sp);
                env.Ctx.Restore();
            }
        }
        if (env.Pass == "main")
        {
            foreach (var (r, c, f, fo) in moving)
            {
                var p = r * C + c;
                var hx = x0 + (c + 0.5) * pw;
                var hy = y0 + (r + 0.5) * phh;
                var ang = J.R(s, p, 4) * J.TAU;
                var dist = Hypot(W, H) * 0.45 * D(Pm, "spread") * (0.6 + 0.4 * J.R(s, p, 5));
                var sx = hx + Math.Cos(ang) * dist;
                var sy = hy + Math.Sin(ang) * dist * 0.7;
                var e = f < 1 ? J.E.OutCubic(f) : 1;
                var eo = J.E.InCubic(fo);
                var px = J.Lerp(sx, hx, e) + (hx - W / 2) * eo * 1.4 + Math.Cos(ang) * eo * dist * 0.5;
                var py = J.Lerp(sy, hy, e) + (hy - H / 2) * eo * 1.4 + Math.Sin(ang) * eo * dist * 0.4;
                var rot = (J.Rs(s, p, 6) * 70 * (1 - e) + J.Rs(s, p, 7) * 50 * eo) * J.DEG;
                var lift = 1 + 0.08 * (1 - e) + 0.06 * eo;
                var a = (1 - eo) * outA;
                if (a <= 0.01) continue;
                using var piece = new SKPath();
                Path(piece, r, c);
                env.Ctx.Save();
                env.Ctx.Translate((float)(px + u * 0.012 * lift), (float)(py + u * 0.018 * lift));
                env.Ctx.RotateRadians((float)rot);
                env.Ctx.Scale((float)lift, (float)lift);
                env.Ctx.Translate((float)-hx, (float)-hy);
                using (var shadow = new SKPaint { IsAntialias = true, Color = SkRgba(DarkOf(sc), 0.3 * a) })
                    env.Ctx.DrawPath(piece, shadow);
                env.Ctx.Restore();
                env.Ctx.Save();
                env.Ctx.Translate((float)px, (float)py);
                env.Ctx.RotateRadians((float)rot);
                env.Ctx.Scale((float)lift, (float)lift);
                env.Ctx.Translate((float)-hx, (float)-hy);
                env.Ctx.Save();
                env.Ctx.ClipPath(piece);
                DrawContent();
                env.Ctx.Restore();
                using (var sp = new SKPaint
                {
                    IsAntialias = true, IsStroke = true, StrokeWidth = (float)lw,
                    Color = SkRgba(J.Mix(plate, DarkOf(sc), 0.4), a),
                })
                    env.Ctx.DrawPath(piece, sp);
                env.Ctx.Restore();
            }
        }
        // 最后一块落位时闪光
        if (last && N > 1)
        {
            var p = ord[N - 1];
            var tl = lt - TOf(p) - fd;
            if (tl > 0 && tl < 0.35)
            {
                var r = p / C;
                var c = p % C;
                var k = 1 - tl / 0.35;
                if (env.Pass == "main")
                {
                    using var piece = new SKPath();
                    Path(piece, r, c);
                    var strokeCol = OnCol(sc, plate) == tc ? sc.Str("accent") : tc;
                    using var sp = new SKPaint
                    {
                        IsAntialias = true, IsStroke = true, StrokeWidth = (float)(Math.Max(2, u * 0.006) * k),
                        Color = SkRgba(strokeCol, k * outA),
                    };
                    env.Ctx.Save();
                    env.Ctx.DrawPath(piece, sp);
                    env.Ctx.Restore();
                }
            }
        }
        env.Body = bb ?? Box(x0, y0, x0 + bw, y0 + bh);
    }

    // ---------------- shadowPlay（源 L3191-3289） ----------------

    private static void DrawShadowPlay(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var outA = Tout(env);
        var dark = J.Lum(sc.Str("bg") ?? "#000000") < 0.45;
        var text = Brk(cut.Text, port ? 5 : 9);
        var o = new JObject { ["track"] = 0.04, ["lead"] = 1.08 };
        var font = S(Pm, "font")!;
        var k = J.Clamp(lt / Math.Max(0.5, cut.Dur), 0, 1);
        var shC = dark ? J.Mix(sc.Str("bg")!, "#000000", 0.55) : J.Mix(DarkOf(sc), sc.Str("bg")!, 0.25);
        if (S(Pm, "mode") == "floor")
        {
            var size = Math.Min(JLayouts.FitSize(text, font, W * 0.8, H * (port ? 0.22 : 0.3), o), u * 0.22);
            var m = JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.04, ["lead"] = 1.08 });
            var yb = H * (port ? 0.56 : 0.6);
            var ty = yb - m.H / 2 - size * 0.04;
            // 地面
            var fa = Tin(env, 0, 0.5, J.E.OutCubic) * outA;
            var floorC = J.Mix(sc.Str("bg")!, sc.Str("fg")!, dark ? 0.13 : 0.07);
            env.Rect(0, yb, W, (H - yb) * fa + 2, floorC, fa, false);
            env.Line(new[] { new[] { W * 0.5 - W * 0.5 * fa, yb }, new[] { W * 0.5 + W * 0.5 * fa, yb } }, sc.Str("sub"), Math.Max(1, u * 0.002), 0.6, false);
            // 太阳横穿天空，投影像日晷一样扫
            var phi = J.Lerp(74, 38, J.E.InOutSine(k)) * (0.85 + 0.15 * D(Pm, "sweep")) * D(Pm, "dir") * J.DEG;
            if (B(Pm, "sun"))
            {
                var sr0 = u * 0.045;
                var sr = sr0 * Tin(env, 0.1, 0.5, x => J.E.OutBack(x));
                var sxs = W / 2 - Math.Sin(phi) * W * 0.4;
                var sys = yb - sr0 * 1.6 - (yb - H * 0.1) * Math.Cos(phi) * 1.1;
                if (Math.Abs(sxs - W / 2) < m.W / 2 + sr0 * 2.2) sys = Math.Min(sys, ty - m.H / 2 - sr0 * 2.4);
                if (sr > 0)
                {
                    env.Circle(sxs, sys, sr * 1.9, sc.Str("accent"), null, 0, 0.12 * outA, false);
                    env.Circle(sxs, sys, sr, sc.Str("accent"), null, 0, outA, false);
                    for (var i = 0; i < 10; i++)
                    {
                        var a = i / 10.0 * J.TAU + env.Ltb * 0.4;
                        env.Line(new[]
                        {
                            new[] { sxs + Math.Cos(a) * sr * 1.35, sys + Math.Sin(a) * sr * 1.35 },
                            new[] { sxs + Math.Cos(a) * sr * 1.75, sys + Math.Sin(a) * sr * 1.75 },
                        }, sc.Str("accent"), Math.Max(1.5, sr * 0.1), 0.8 * outA, false);
                    }
                }
            }
            var sh = J.Clamp(Math.Tan(phi) * 0.75, -1.9, 1.9);
            var d = 0.3 + 0.3 * Math.Abs(Math.Sin(phi));
            var mS = new[] { 1.0, 0.0, sh, -d, -sh * yb, (1 + d) * yb };
            var baseIt = new JObject
            {
                ["text"] = text, ["font"] = font, ["size"] = size, ["x"] = W / 2.0, ["y"] = ty, ["mi"] = 0,
                ["track"] = 0.04, ["lead"] = 1.08,
            };
            if (env.Pass == "main")
            {
                env.Ctx.Save();
                env.Ctx.ClipRect(new SKRect((float)-W, (float)yb, (float)(W * 2), (float)(H * 2)));
                DrawAff(env, new JObject
                {
                    ["text"] = text, ["font"] = font, ["size"] = size, ["x"] = W / 2.0, ["y"] = ty, ["mi"] = 0,
                    ["track"] = 0.04, ["lead"] = 1.08,
                    ["color"] = shC, ["alpha"] = 0.85, ["ghost"] = false, ["plain"] = true,
                }, mS);
                // 影子远端渐隐进地面
                using var g = SKShader.CreateLinearGradient(
                    new SKPoint(0, (float)yb), new SKPoint(0, (float)(yb + m.H * d * 1.15 + size * 0.1)),
                    new[] { SkRgba(floorC, 0), SkRgba(floorC, 0), SkRgba(floorC, 0.7) },
                    new float[] { 0, 0.35f, 1 }, SKShaderTileMode.Clamp);
                using var p = new SKPaint { IsAntialias = true, Shader = g, Color = SKColors.White };
                env.Ctx.DrawRect(new SKRect(0, (float)yb, W, H), p);
                env.Ctx.Restore();
            }
            var main = (JObject)baseIt.DeepClone();
            main["color"] = sc.Str("fg");
            env.Body = JLayouts.MainDraw(env, main);
            return;
        }
        // 墙模式：身前小灯在背后墙上投出放大的软影
        var size2 = Math.Min(JLayouts.FitSize(text, font, W * 0.6, H * (port ? 0.2 : 0.26), o), u * 0.17);
        var tx = W / 2.0;
        var ty2 = H * 0.5;
        var lx = W / 2 + Math.Sin(env.Ltb * 0.45 + (D(Pm, "dir") > 0 ? 0 : 2)) * W * 0.16;
        var ly = H * 0.86 + Math.Sin(env.Ltb * 1.7) * H * 0.006;
        var kS = 1.42 + 0.1 * Math.Sin(env.Ltb * 0.6);
        var flick = 0.92 + 0.08 * J.R((int)cut.Seed, (int)env.Step, 3);
        var la = Tin(env, 0, 0.5, J.E.OutCubic) * outA;
        if (env.Pass == "main" && la > 0)
        {
            using var g = SKShader.CreateRadialGradient(
                new SKPoint((float)lx, (float)ly), (float)(Math.Max(W, H) * 0.75),
                new[]
                {
                    SkRgba(dark ? sc.Str("accent") : LightOf(sc), (dark ? 0.2 : 0.35) * flick * la),
                    SkRgba(dark ? sc.Str("accent") : LightOf(sc), (dark ? 0.07 : 0.12) * la),
                    SkRgba(sc.Str("bg"), 0),
                }, new float[] { 0, 0.5f, 1 }, SKShaderTileMode.Clamp);
            using var p = new SKPaint { IsAntialias = true, Shader = g, Color = SKColors.White };
            env.Ctx.Save();
            env.Ctx.DrawRect(new SKRect(0, 0, W, H), p);
            env.Ctx.Restore();
        }
        var mS2 = new[] { kS, 0.0, 0.0, kS, lx * (1 - kS), ly * (1 - kS) };
        if (env.Pass == "main")
            DrawAff(env, new JObject
            {
                ["text"] = text, ["font"] = font, ["size"] = size2, ["x"] = tx, ["y"] = ty2, ["mi"] = 0,
                ["track"] = 0.04, ["lead"] = 1.08,
                ["color"] = shC, ["alpha"] = (dark ? 0.9 : 0.55) * la, ["blur"] = size2 * 0.04,
                ["ghost"] = false, ["plain"] = true,
            }, mS2);
        var fr = u * 0.02;
        env.Circle(lx, ly, fr * 3.5, sc.Str("accent"), null, 0, 0.15 * la * flick, false);
        env.Poly(new[]
        {
            new[] { lx, ly - fr * 2.2 * flick }, new[] { lx + fr * 0.8, ly - fr * 0.2 },
            new[] { lx, ly + fr * 0.6 }, new[] { lx - fr * 0.8, ly - fr * 0.2 },
        }, sc.Str("accent"), la, false);
        env.Rect(lx - fr * 0.9, ly + fr * 0.6, fr * 1.8, fr * 1.6, sc.Str("sub"), la, false);
        env.Body = JLayouts.MainDraw(env, new JObject
        {
            ["text"] = text, ["font"] = font, ["size"] = size2, ["x"] = tx, ["y"] = ty2, ["mi"] = 0,
            ["track"] = 0.04, ["lead"] = 1.08, ["color"] = sc.Str("fg"),
        });
    }

    // ---------------- kaleido（源 L3291-3376） ----------------

    private static void DrawKaleido(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var t0 = Flat(cut.Text);
        var n = JLayouts.GlyphCount(t0);
        var unit = n > 5 ? ChunksK(t0, (int)Math.Ceiling(n / 4.0))[0] : t0;
        var cx = W / 2.0;
        var cy = H / 2.0;
        var N = I(Pm, "N");
        var al = J.TAU / N;
        var Rr = Hypot(W, H) * 0.56;
        var outA = Tout(env);
        var e = J.E.OutCubic(J.Clamp(lt / 0.6));
        var rot0 = (env.Ltb * D(Pm, "speed") + (1 - e) * 60) * J.DEG;
        var tint = S(Pm, "tint");
        var cols = tint == "accent"
            ? new[] { sc.Str("accent"), sc.Str("sub"), sc.Str("sub") }
            : tint == "mixed"
                ? new[] { sc.Str("sub"), sc.Str("accent"), sc.Str("sub"), sc.Str("accent2") ?? sc.Str("sub") }
                : new[] { sc.Str("sub"), J.Mix(sc.Str("sub")!, sc.Str("bg")!, 0.4) };
        var font = S(Pm, "font")!;
        if (env.Pass == "main" && e > 0)
        {
            const int M = 4;
            var spacing = 1.0 / M;
            var ph = env.Ltb * D(Pm, "flow") % spacing;
            for (var w = 0; w < N; w++)
            {
                var a0 = rot0 + w * al;
                using var path = new SKPath();
                path.MoveTo((float)cx, (float)cy);
                path.LineTo((float)(cx + Math.Cos(a0) * Rr), (float)(cy + Math.Sin(a0) * Rr));
                path.LineTo((float)(cx + Math.Cos(a0 + al) * Rr), (float)(cy + Math.Sin(a0 + al) * Rr));
                path.Close();
                env.Ctx.Save();
                env.Ctx.ClipPath(path);
                env.Ctx.Translate((float)cx, (float)cy);
                env.Ctx.RotateRadians((float)(a0 + al / 2));
                if (w % 2 != 0) env.Ctx.Scale(1, -1);
                for (var j = 0; j < M + 1; j++)
                {
                    var f = j * spacing + ph; // 0..1 半径比例，向外流动
                    var r = Rr * (0.12 + f * 0.95);
                    var sz = r * Math.Tan(al / 2) * 1.2;
                    var a = Math.Min(1, f * 4) * (1 - f * 0.55) * outA * e;
                    if (a <= 0.02 || sz < 4) continue;
                    env.DrawItem(new JObject
                    {
                        ["text"] = unit,
                        ["font"] = font,
                        ["size"] = Math.Min(sz, r * 0.5),
                        ["x"] = r,
                        ["y"] = 0,
                        ["rot"] = 90,
                        ["color"] = cols[(j + w) % cols.Length],
                        ["alpha"] = a * 0.5,
                        ["ghost"] = false,
                    });
                }
                env.Line(new[] { new[] { Rr * 0.1, 0 }, new[] { Rr, 0 } }, sc.Str("dim"), 1, 0.5 * outA * e, false);
                env.Ctx.Restore();
            }
        }
        // 中心：歌词放圆盘或横带
        var disc = S(Pm, "center") == "disc" && n <= (IsPort(env) ? 8 : 9);
        var text = disc ? Brk(t0, n <= 4 ? 4 : (int)Math.Ceiling(n / 2.0)) : Brk(t0, IsPort(env) ? 5 : 8);
        var o = new JObject { ["track"] = 0.03, ["lead"] = 1.1 };
        double size;
        if (disc)
        {
            var Rc = Math.Min(W, H) * (n > 5 ? 0.36 : 0.3);
            size = Math.Min(JLayouts.FitSize(text, font, Rc * 1.55, Rc * 1.2, o), u * 0.16);
            var q = J.E.OutBack(J.Clamp(lt / 0.4), 1.5) * (1 - 0.3 * J.E.InCubic(env.POut));
            env.Circle(cx, cy, Rc * q, sc.Str("bg"), null, 0, outA, false);
            env.Circle(cx, cy, Rc * q, null, sc.Str("accent"), Math.Max(2, u * 0.004), outA, false);
            env.Circle(cx, cy, Rc * q * 1.06, null, sc.Str("sub"), 1, 0.6 * outA, false);
        }
        else
        {
            size = Math.Min(JLayouts.FitSize(text, font, W * 0.84, H * 0.26, o), u * 0.18);
            var m = JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.03, ["lead"] = 1.1 });
            var bh = (m.H + size * 0.6) * J.E.OutExpo(J.Clamp(lt / 0.4)) * (1 - J.E.InCubic(env.POut));
            env.Rect(0, cy - bh / 2, W, bh, sc.Str("bg"), 1, false);
            env.Line(new[] { new[] { 0, cy - bh / 2 }, new[] { W, cy - bh / 2 } }, sc.Str("accent"), Math.Max(2, u * 0.003), outA, false);
            env.Line(new[] { new[] { 0, cy + bh / 2 }, new[] { W, cy + bh / 2 } }, sc.Str("accent"), Math.Max(2, u * 0.003), outA, false);
        }
        env.Body = JLayouts.MainDraw(env, new JObject
        {
            ["text"] = text, ["font"] = font, ["size"] = size, ["x"] = cx, ["y"] = cy, ["color"] = sc.Str("fg"),
            ["track"] = 0.03, ["lead"] = 1.1,
        });
    }

    // ---------------- dominoes（源 L3378-3469） ----------------

    private static readonly int[][][] Pips =
    {
        Array.Empty<int[]>(), new[] { new[] { 0, 0 } }, new[] { new[] { -1, -1 }, new[] { 1, 1 } },
        new[] { new[] { -1, -1 }, new[] { 0, 0 }, new[] { 1, 1 } },
        new[] { new[] { -1, -1 }, new[] { 1, -1 }, new[] { -1, 1 }, new[] { 1, 1 } },
        new[] { new[] { -1, -1 }, new[] { 1, -1 }, new[] { 0, 0 }, new[] { -1, 1 }, new[] { 1, 1 } },
        new[] { new[] { -1, -1 }, new[] { 1, -1 }, new[] { -1, 0 }, new[] { 1, 0 }, new[] { -1, 1 }, new[] { 1, 1 } },
    };

    private static void DrawDominoes(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var n = JLayouts.GlyphCount(cut.Text);
        if (n < 1)
        {
            env.Body = null;
            return;
        }
        var port = IsPort(env);
        var rowsD = RowsOf(cut.Text, port ? 5 : n <= 7 ? 7 : (int)Math.Ceiling(n / 2.0));
        var rows = rowsD.Count;
        var per = rowsD.Max(r => r.Count);
        var asp = B(Pm, "pips") ? 1.9 : 1.45;
        const double gapK = 0.5;
        var w = Math.Min(W * 0.84 / (per + (per - 1) * gapK), Math.Min(H * 0.72 / (rows * asp + (rows - 1) * 0.35), u * 0.2));
        var h = w * asp;
        var g = w * gapK;
        var outA = Tout(env);
        var faceC = S(Pm, "face") == "ink" ? PlateCol(sc, new[] { sc.Str("ink"), sc.Str("fg") }) : J.Mix(LightOf(sc), sc.Str("accent")!, 0.06);
        var tc = OnCol(sc, faceC);
        var sideC = Shade(sc, faceC, -0.35);
        var pipC = J.Mix(faceC, tc, 0.7);
        var thick = w * 0.12;
        var lean = Math.Asin(J.Clamp((g - thick * 0.2) / h, 0, 0.95)) / J.DEG;
        const double T0 = 0.1;
        var dT = J.Clamp(cut.Dur * 0.3 / n, 0.04, 0.09);
        const double rise = 0.26;
        var outDur = Math.Max(0.3, Math.Min(cut.Dur * 0.35, (cut.OutDur != 0 ? cut.OutDur : 0.3) + 0.25));
        var oStart = cut.Dur - outDur;
        BBox? bb = null;
        var gi = 0;
        var font = S(Pm, "font")!;
        for (var r = 0; r < rows; r++)
        {
            var row = rowsD[r];
            var cnt = row.Count;
            var yF = H / 2 + (r - (rows - 1) / 2.0) * (h + w * 0.35) + h / 2;
            env.Line(new[]
            {
                new[] { W / 2 - cnt * (w + g) / 2 - w * 0.4, yF }, new[] { W / 2 + cnt * (w + g) / 2 + w * 0.4, yF },
            }, sc.Str("sub"), Math.Max(1, u * 0.0018), 0.5 * Tin(env, 0, 0.3) * outA, false);
            for (var j = 0; j < cnt; j++)
            {
                if (row[j] == " ") continue;
                var i = gi++;
                var xc = W / 2 + (j - (cnt - 1) / 2.0) * (w + g);
                // 从左起波浪式立起，结尾向右连锁倒下
                var ti = T0 + i * dT;
                var fu = J.Clamp((lt - ti) / rise);
                var th = 0.0;
                var pivot = 0.0;
                if (fu < 1)
                {
                    th = -(j == 0 ? 88 : lean) * (1 - J.E.OutBack(fu, 1.6));
                    pivot = -1;
                }
                var fo = (lt - oStart - j * 0.05) / 0.3;
                if (fo > 0)
                {
                    th = (j == cnt - 1 ? 88 : lean) * J.E.InQuad(Math.Min(1, fo));
                    pivot = 1;
                }
                var px = xc + pivot * w / 2;
                var py = yF;
                var ca = Math.Cos(th * J.DEG);
                var sa = Math.Sin(th * J.DEG);
                var m = new[] { ca, sa, -sa, ca, px - ca * pivot * w / 2, py - sa * pivot * w / 2 };
                // 牌面角点，局部原点在牌底中点
                double[] T(double x, double y) => new[] { m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5] };
                var vis = J.Clamp((lt - ti + 0.08) / 0.1) * (1 - J.E.InCubic(J.Clamp((env.POut - 0.7) / 0.3)));
                if (vis <= 0) continue;
                env.Poly(new[]
                {
                    T(-w / 2, 0), T(w / 2, 0), T(w / 2 + w * 0.1, w * 0.04), T(-w / 2 + w * 0.1, w * 0.04),
                }, DarkOf(sc), 0.25 * vis, false);
                env.Poly(new[]
                {
                    T(w / 2, -h + thick * 0.4), T(w / 2 + thick, -h + thick * 0.4 + thick * 0.3),
                    T(w / 2 + thick, thick * 0.3), T(w / 2, 0),
                }, sideC, vis, false);
                env.Ctx.Save();
                env.Ctx.Concat(Mat(m));
                env.RRect(-w / 2, -h, w, h, w * 0.1, faceC, vis, GIn(env));
                if (B(Pm, "pips"))
                {
                    env.Line(new[] { new[] { -w * 0.36, -h * 0.47 }, new[] { w * 0.36, -h * 0.47 } }, J.Mix(faceC, tc, 0.45), Math.Max(1, w * 0.03), vis, false);
                    var pn = (int)(J.H(s, i, 11) % 7);
                    var pr = w * 0.07;
                    foreach (var pp in Pips[pn]) env.Circle(pp[0] * w * 0.22, -h * 0.235 + pp[1] * w * 0.22, pr, pipC, null, 0, vis, false);
                }
                env.Ctx.Restore();
                var cyT = B(Pm, "pips") ? -h * 0.735 : -h / 2;
                bb = JLayouts.UnionBb(bb, DrawAff(env, new JObject
                {
                    ["text"] = row[j],
                    ["font"] = font,
                    ["size"] = w * 0.7,
                    ["x"] = 0,
                    ["y"] = cyT,
                    ["color"] = tc,
                    ["alpha"] = vis,
                    ["noHold"] = PlateHold(env),
                    ["mi"] = MiAt(env, ti),
                }, m));
            }
        }
        env.Body = bb;
    }

    // ---------------- burst（源 L3471-3553） ----------------

    private static void DrawBurst(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var font = S(Pm, "font")!;
        var text = Brk(cut.Text, port ? 4 : 6);
        var o = new JObject { ["track"] = 0.02, ["lead"] = 1.05 };
        var rxM = W * 0.355;
        var ryM = Math.Min(H * 0.355, rxM * (port ? 1.5 : 1.1));
        var size = Math.Min(JLayouts.FitSize(text, font, rxM * 1.08, ryM * 1.0, o), u * 0.24);
        var m = JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.02, ["lead"] = 1.05 });
        var rx = Math.Min(rxM, m.W / 2 / 0.54 + size * 0.15);
        var ry = Math.Min(ryM, Math.Max(m.H / 2 / 0.54 + size * 0.15, rx * 0.55));
        var cx = W / 2.0;
        var cy = H / 2.0;
        var outA = Tout(env);
        var q = J.E.OutBack(J.Clamp(lt / 0.3), 2.2) * (1 - 0.6 * J.E.InCubic(env.POut)) * (1 + 0.015 * Math.Sin(env.Ltb * 7) * J.Clamp(lt - 0.3));
        var rot = (D(Pm, "tilt") + (1 - J.E.OutCubic(J.Clamp(lt / 0.3))) * -25 + J.E.InCubic(env.POut) * 20) * J.DEG;
        var boil = (int)Math.Floor(env.Step / 3);
        var A = PlateCol(sc, S(Pm, "order") == "accent" ? new[] { sc.Str("accent"), sc.Str("ink") } : new[] { sc.Str("ink"), sc.Str("accent") });
        var Bcol = new[] { DarkOf(sc), LightOf(sc), sc.Str("bg"), sc.Str("fg") }
            .FirstOrDefault(c => J.Contrast(c ?? "#000000", A) >= 2.2 && c != A) ?? OnCol(sc, A);
        double[][] Star(int k, double scale, double jit)
        {
            var N = I(Pm, "spikes");
            var pts = new double[N * 2][];
            for (var i = 0; i < N * 2; i++)
            {
                var a = i / (double)(N * 2) * J.TAU + rot;
                var rr = i % 2 != 0 ? D(Pm, "sharp") + J.Rs(s, i, k, boil) * 0.04 : 1 + J.R(s, i, k, boil) * jit;
                pts[i] = new[] { cx + Math.Cos(a) * rx * rr * scale * q, cy + Math.Sin(a) * ry * rr * scale * q };
            }
            return pts;
        }

        // 冲击环、速度线、碎片
        var ring = J.Clamp(lt / 0.45);
        if (ring < 1)
            env.Circle(cx, cy, Math.Max(rx, ry) * (0.6 + ring * 1.1), null, A, Math.Max(2, u * 0.012) * (1 - ring), (1 - ring) * outA, false);
        if (B(Pm, "lines") && env.Pass == "main")
        {
            const int L = 28;
            for (var i = 0; i < L; i++)
            {
                if (J.R(s, i, (int)env.Step, 9) < 0.3) continue;
                var a = i / (double)L * J.TAU + J.Rs(s, i, 8) * 0.08;
                var r0 = 1.25 + J.R(s, i, (int)env.Step, 10) * 0.15;
                var r1 = r0 + 0.2 + J.R(s, i, 11) * 0.25;
                env.Line(new[]
                {
                    new[] { cx + Math.Cos(a) * rx * r0 * q, cy + Math.Sin(a) * ry * r0 * q },
                    new[] { cx + Math.Cos(a) * rx * r1 * q, cy + Math.Sin(a) * ry * r1 * q },
                }, sc.Str("sub"), Math.Max(1.5, u * 0.004), 0.7 * outA, false);
            }
        }
        if (B(Pm, "debris") && lt < 0.9)
        {
            for (var i = 0; i < 12; i++)
            {
                var a = J.R(s, i, 21) * J.TAU;
                var sp = J.Rr(0.5, 1.1, s, i, 22);
                var f = J.E.OutCubic(J.Clamp(lt / 0.8));
                var d = 1.05 + f * 0.6 * sp;
                var x = cx + Math.Cos(a) * rx * d;
                var y = cy + Math.Sin(a) * ry * d + f * f * H * 0.05;
                var sz = u * 0.018 * (1 - f);
                var rr = J.R(s, i, 23) * J.TAU + f * 6;
                if (sz > 0.5)
                    env.Poly(new[]
                    {
                        new[] { x + Math.Cos(rr) * sz, y + Math.Sin(rr) * sz },
                        new[] { x + Math.Cos(rr + 2.2) * sz, y + Math.Sin(rr + 2.2) * sz },
                        new[] { x + Math.Cos(rr + 4.1) * sz * 0.7, y + Math.Sin(rr + 4.1) * sz * 0.7 },
                    }, i % 2 != 0 ? A : Bcol!, outA, false);
            }
        }
        if (q <= 0.01)
        {
            env.Body = null;
            return;
        }
        env.Poly(Star(1, 1.12, 0.22).Select(p => new[] { p[0] + u * 0.012, p[1] + u * 0.016 }).ToArray(), DarkOf(sc), 0.3 * outA, false);
        env.Poly(Star(1, 1.12, 0.22), A, outA, GIn(env));
        env.Poly(Star(2, 0.93, 0.12), Bcol!, outA, false);
        env.Poly(Star(3, 0.86, 0.08), A, outA, false);
        env.Body = JLayouts.MainDraw(env, new JObject
        {
            ["text"] = text,
            ["font"] = font,
            ["size"] = size * Math.Min(1, q),
            ["x"] = cx,
            ["y"] = cy,
            ["rot"] = rot / J.DEG * 0.6,
            ["color"] = OnCol(sc, A),
            ["track"] = 0.02,
            ["lead"] = 1.05,
        });
    }

    // ---------------- fisheye（源 L3555-3669） ----------------

    private static void DrawFisheye(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var t0 = Flat(cut.Text);
        var n = JLayouts.GlyphCount(t0);
        var rowsT = n > (port ? 6 : 10) ? ChunksK(t0, port ? (int)Math.Ceiling(n / 6.0) : 2) : new List<string> { t0 };
        var nR = rowsT.Count;
        var A = D(Pm, "amp");
        var font = S(Pm, "font")!;
        var s0 = u * 0.16;
        foreach (var r in rowsT)
        {
            var a = 0.0;
            foreach (var ch in JLayouts.CodePoints(r)) a += Adv(font, ch);
            s0 = Math.Min(s0, W * 0.84 / (a + A * 1.6));
        }
        s0 = Math.Min(s0, H * 0.6 / (nR * (1.4 + A * 0.5)));
        var sig = s0 * 1.25;
        var outA = Tout(env);
        // 透镜位置：sweep 一趟扫过所有行，pingpong 每行来回
        var T = J.Clamp((lt - 0.2) / Math.Max(0.4, cut.Dur - 0.5));
        var lensIn = J.E.OutCubic(J.Clamp((lt - 0.1) / 0.4)) * (1 - J.E.InCubic(env.POut));
        BBox? bb = null;
        (double X, double Y, double R, double K)? lensPos = null;
        var mode = S(Pm, "mode");
        var glass = S(Pm, "glass");
        for (var ri = 0; ri < rowsT.Count; ri++)
        {
            var chars = JLayouts.CodePoints(rowsT[ri]);
            var baseAd = chars.Select(ch => Adv(font, ch) * s0).ToList();
            var tot0 = baseAd.Sum();
            var cy = H / 2 + (ri - (nR - 1) / 2.0) * s0 * (1.35 + A * 0.55);
            var xL = W / 2 - tot0 / 2;
            double? lx;
            if (mode == "sweep")
            {
                var f = T * nR - ri;
                lx = f < 0 || f > 1 ? null : xL - sig + (tot0 + sig * 2) * J.E.InOutSine(f);
            }
            else lx = xL + tot0 * (0.5 - 0.5 * Math.Cos(env.Ltb * 1.6 + ri * 1.3));
            // 按静止位置的鼓包缩放每个字，再绕中心重排整行
            var acc = 0.0;
            var sc0 = new List<double>();
            foreach (var w in baseAd)
            {
                var x = xL + acc + w / 2;
                acc += w;
                sc0.Add(lx == null ? 1 : 1 + A * lensIn * Math.Exp(-Math.Pow((x - lx.Value) / sig, 2)));
            }
            var widths = new List<double>();
            for (var i = 0; i < baseAd.Count; i++) widths.Add(baseAd[i] * sc0[i]);
            var tot = widths.Sum();
            var x2 = W / 2 - tot / 2;
            if (lx != null)
            {
                // 重排后让透镜对准鼓包中心
                var acc2 = 0.0;
                var best = 0;
                var bw = 0.0;
                for (var i = 0; i < sc0.Count; i++)
                    if (sc0[i] > bw)
                    {
                        bw = sc0[i];
                        best = i;
                    }
                for (var i = 0; i < widths.Count; i++)
                    if (i < best) acc2 += widths[i];
                lensPos = (x2 + acc2 + widths[best] / 2, cy, s0 * (0.5 + 0.5 * bw) * 0.95, bw);
            }
            for (var i = 0; i < chars.Count; i++)
            {
                var w = widths[i];
                var gx = x2 + w / 2;
                x2 += w;
                var ch = chars[i];
                if (ch == " ") continue;
                var k = sc0[i];
                bb = JLayouts.UnionBb(bb, JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = ch,
                    ["font"] = font,
                    ["size"] = s0 * k,
                    ["x"] = gx,
                    ["y"] = cy,
                    ["color"] = k > 1.35 ? sc.Str("accent") : sc.Str("fg"),
                    ["mi"] = ri * 3 + i * 0.5,
                }));
            }
            if (glass == "grid" && lx != null && env.Pass == "main")
            {
                var g = s0 * 0.5;
                var lw = Math.Max(1, u * 0.0012);
                var lxv = lx.Value;
                double[] Warp(double px, double py)
                {
                    var dx = px - lxv;
                    var dy = py - cy;
                    var rr = Hypot(dx, dy);
                    var f = 1 + 0.9 * A * lensIn * Math.Exp(-Math.Pow(rr / (sig * 1.4), 2));
                    return new[] { lxv + dx * f, cy + dy * f };
                }

                for (var k = -3; k <= 3; k++)
                {
                    var pts = new List<double[]>();
                    for (var q = -12; q <= 12; q++) pts.Add(Warp(lxv + q * g * 0.5, cy + k * g));
                    env.Line(pts.ToArray(), sc.Str("sub"), lw, 0.35 * outA * lensIn, false);
                }
                for (var k = -6; k <= 6; k++)
                {
                    var pts = new List<double[]>();
                    for (var q = -6; q <= 6; q++) pts.Add(Warp(lxv + k * g, cy + q * g * 0.5));
                    env.Line(pts.ToArray(), sc.Str("sub"), lw, 0.35 * outA * lensIn, false);
                }
            }
        }
        if (glass == "lens" && lensPos is { } lp && lensIn > 0.01)
        {
            var x = lp.X;
            var y = lp.Y;
            var R = Math.Max(lp.R * 1.25, s0 * 1.05);
            var lw = Math.Max(2, u * 0.005);
            env.Circle(x, y, R, LightOf(sc), null, 0, 0.06 * lensIn, false);
            env.Circle(x, y, R, null, sc.Str("sub"), lw, lensIn, false);
            env.Arc(x, y, R * 0.8, 200, 250, LightOf(sc), lw * 0.8, 0.7 * lensIn, false);
            var a = 50 * J.DEG;
            env.Line(new[]
            {
                new[] { x + Math.Cos(a) * R, y + Math.Sin(a) * R },
                new[] { x + Math.Cos(a) * R * 1.75, y + Math.Sin(a) * R * 1.75 },
            }, sc.Str("sub"), lw * 2.6, lensIn, false);
        }
        env.Body = bb;
    }

    // ---------------- wall（源 L3671-3796） ----------------

    private static void DrawWall(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var side = D(Pm, "side", 1); // +1：歌词在右手边的墙上
        var outA = Tout(env);
        var f = Math.Max(W, H) * 1.05;
        var Dp = f;
        var a0 = J.E.OutCubic(J.Clamp(lt / 0.9));
        var orbit = D(Pm, "orbit");
        var yaw = (D(Pm, "yaw") + (1 - a0) * orbit * 2.2 + Math.Sin(env.Ltb * 0.35) * orbit * 0.15 + J.E.InCubic(env.POut) * orbit * 1.5) * J.DEG;
        // 歌词墙从墙角伸向 side 一侧，另一面墙朝反向
        var dL = new[] { side * Math.Sin(yaw), Math.Cos(yaw) };
        var dO = new[] { -side * Math.Cos(yaw), Math.Sin(yaw) };
        var ccx = W / 2 - side * W * (port ? 0.3 : 0.22);
        var ccy = H * (0.5 + D(Pm, "eye"));
        double[] Proj(double X, double Y, double Z) => new[] { ccx + f * X / Z, ccy + f * Y / Z };
        var Ht = H * (port ? 0.26 : 0.4);
        var Hb = H * (port ? 0.2 : 0.34);
        var LL = Dp * (port ? 1.5 : 1.15);
        var LO = Dp * 1.2;
        double[] FacePt(double[] d, double s, double Y) => Proj(d[0] * s, Y, Dp + d[1] * s);
        double[][] Face(double[] d, double L, double y0, double y1) => new[]
        {
            FacePt(d, 0, y0), FacePt(d, L, y0), FacePt(d, L, y1), FacePt(d, 0, y1),
        };
        var wa = Tin(env, 0, 0.5, J.E.OutCubic) * outA;
        var tone = S(Pm, "tone");
        var plate = tone == "plate" ? PlateCol(sc, new[] { sc.Str("ink"), sc.Str("fg") }) : J.Mix(sc.Str("bg")!, sc.Str("fg")!, J.Lum(sc.Str("bg") ?? "#000000") < 0.5 ? 0.1 : 0.07);
        var litW = Shade(sc, plate, 0.06);
        var dimW = Shade(sc, plate, -0.3);
        var tc = tone == "plate" ? OnCol(sc, plate) : sc.Str("fg")!;
        // 地面和地平线
        env.Rect(0, ccy, W, H - ccy, J.Mix(sc.Str("bg")!, DarkOf(sc), 0.35), 0.5 * wa, false);
        env.Line(new[] { new[] { 0, ccy }, new[] { W, ccy } }, sc.Str("sub"), 1, 0.25 * wa, false);
        env.Poly(Face(dO, LO * wa, -Ht, Hb), dimW, outA, false);
        env.Poly(Face(dL, LL * wa, -Ht, Hb), litW, outA, false);
        var lw = Math.Max(1, u * 0.0016);
        env.Line(new[] { Proj(0, -Ht, Dp), Proj(0, Hb, Dp) }, J.Mix(litW, DarkOf(sc), 0.3), lw * 1.5, outA, false);
        if (B(Pm, "stripe"))
            foreach (var (d, L) in new[] { (dL, LL), (dO, LO) })
                env.Poly(Face(d, L * wa, Hb * 0.55, Hb * 0.72), sc.Str("accent"), 0.9 * outA, false);
        // 逐字透视：局部 x 沿墙面，局部 y 世界竖直，数值雅可比
        double[] GlyphM(double[] d, double s, double Y, bool flip)
        {
            var p0 = FacePt(d, s, Y);
            const double e = 1;
            var p1 = FacePt(d, s + e * (flip ? -1 : 1), Y);
            var p2 = FacePt(d, s, Y + e);
            return new[] { p1[0] - p0[0], p1[1] - p0[1], p2[0] - p0[0], p2[1] - p0[1], p0[0], p0[1] };
        }

        // 阅读偏移 u 对应的墙面位置：向右延伸的墙向外读，向左的朝墙角读
        double PosOn(double[] d, double uu, double tot, double m0) => d[0] >= 0 ? m0 + uu : m0 + tot - uu;
        // 另一面墙：整行歌词的小字排
        var oc = Flat(string.IsNullOrEmpty(cut.LineText) ? cut.Text : cut.LineText);
        var rowsO = port ? 3 : 4;
        var fsO = Ht * 0.12;
        if (env.Pass == "main")
        {
            var fo = BodyF(env);
            var chars = JLayouts.CodePoints(oc + "　・　" + oc + "　・　" + oc);
            if (chars.Count > 40) chars = chars.Take(40).ToList();
            var ads = chars.Select(ch => Adv(fo, ch) * fsO * 1.1).ToList();
            var tot = 0.0;
            var cnt = 0;
            foreach (var a in ads)
            {
                if (tot + a > LO * 0.86) break;
                tot += a;
                cnt++;
            }
            for (var r = 0; r < rowsO; r++)
            {
                var Y = -Ht * 0.72 + r * fsO * 1.9;
                var uu = 0.0;
                for (var i = 0; i < cnt; i++)
                {
                    var ch = chars[i];
                    var a = ads[i];
                    var mid = uu + a / 2;
                    uu += a;
                    if (ch is " " or "　") continue;
                    DrawAffPlain(env, new JObject
                    {
                        ["text"] = ch,
                        ["font"] = fo,
                        ["size"] = fsO,
                        ["x"] = 0,
                        ["y"] = 0,
                        ["color"] = J.Mix(dimW, OnCol(sc, dimW), 0.55),
                        ["alpha"] = wa * J.Clamp((lt - 0.2 - r * 0.08) / 0.3),
                        ["ghost"] = false,
                    }, GlyphM(dO, PosOn(dO, mid, tot, LO * 0.07), Y, dO[0] < 0));
                }
            }
        }
        // 主墙上的歌词
        var t0 = Flat(cut.Text);
        var n = JLayouts.GlyphCount(t0);
        var lines = n > (port ? 5 : 8) ? ChunksK(t0, port ? (int)Math.Ceiling(n / 5.0) : 2) : new List<string> { t0 };
        var nl = lines.Count;
        var font = S(Pm, "font")!;
        var g = (Ht + Hb * 0.5) * 0.62 / (nl * 1.15);
        foreach (var l in lines)
        {
            var a = 0.0;
            foreach (var ch in JLayouts.CodePoints(l)) a += Adv(font, ch) * 1.04;
            g = Math.Min(g, LL * 0.86 / Math.Max(1, a));
        }
        BBox? bb = null;
        for (var li = 0; li < lines.Count; li++)
        {
            var Y = -Ht * 0.25 + (li - (nl - 1) / 2.0) * g * 1.15;
            var chars = JLayouts.CodePoints(lines[li]);
            var ads = chars.Select(ch => Adv(font, ch) * g * 1.04).ToList();
            var tot = ads.Sum();
            var uu = 0.0;
            for (var i = 0; i < chars.Count; i++)
            {
                var ch = chars[i];
                var a = ads[i];
                var mid = uu + a / 2;
                uu += a;
                if (ch == " ") continue;
                var sp = PosOn(dL, mid, tot, LL * 0.07);
                if (sp > LL * wa) continue;
                bb = JLayouts.UnionBb(bb, DrawAff(env, new JObject
                {
                    ["text"] = ch,
                    ["font"] = font,
                    ["size"] = g,
                    ["x"] = 0,
                    ["y"] = 0,
                    ["color"] = tc,
                    ["noHold"] = true,
                    ["mi"] = li * 3 + i * 0.6,
                }, GlyphM(dL, sp, Y, dL[0] < 0)));
            }
        }
        env.Body = bb;
    }

    // ---------------- origami（源 L3798-3924） ----------------

    private static void DrawOrigami(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var outA = Tout(env);
        var paper0 = LightOf(sc);
        var back = (S(Pm, "col") == "ink" ? new[] { sc.Str("accent2"), sc.Str("accent"), sc.Str("ink") } : new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ink") })
            .FirstOrDefault(c => !string.IsNullOrEmpty(c) && J.Contrast(c!, paper0) >= 1.5 && J.Contrast(c!, sc.Str("bg") ?? "#000000") >= 1.3) ?? DarkOf(sc);
        var paper = J.Mix(paper0, back, 0.05);
        var onp = OnCol(sc, paper);
        var tc = onp == paper ? DarkOf(sc) : (J.Contrast(DarkOf(sc), paper) > 3 ? DarkOf(sc) : onp);
        var crease = J.Mix(paper, DarkOf(sc), 0.22);
        var cx = W / 2.0;
        var cy = H / 2.0;
        const double fl = 0.36;
        const double stp = 0.13; // 翻折时长、错峰间隔
        var outF = J.Clamp(env.POut * 1.3);
        double FlapAng(double k)
        {
            var tin0 = 0.1 + k * stp;
            var tout0 = k * 0.12;
            var p = J.E.InOutCubic(J.Clamp((lt - tin0) / fl)) * 166;
            if (outF > 0) p = Math.Min(p, 166 * (1 - J.E.InOutCubic(J.Clamp((outF - tout0 * 0.8) / 0.6))));
            return p * J.DEG;
        }

        var shrink = 1 - J.E.InCubic(J.Clamp((env.POut - 0.75) / 0.25));
        var text0 = Flat(cut.Text);
        var n = JLayouts.GlyphCount(text0);
        var font = S(Pm, "font")!;
        BBox? bb = null;
        if (S(Pm, "mode") == "gate" && !port)
        {
            // 宽纸两扇门从中间折开
            var text = Brk(text0, 9);
            var size = Math.Min(JLayouts.FitSize(text, font, W * 0.66, H * 0.4, new JObject { ["track"] = 0.04, ["lead"] = 1.12 }), u * 0.2);
            var m = JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.04, ["lead"] = 1.12 });
            var sw = Math.Min(W * 0.86, m.W + size * 1.6) * shrink;
            var sh = Math.Min(H * 0.72, m.H + size * 1.4) * shrink;
            if (sw < 2)
            {
                env.Body = null;
                return;
            }
            var x0 = cx - sw / 2;
            var y0 = cy - sh / 2;
            env.Rect(x0 + u * 0.012, y0 + u * 0.018, sw, sh, DarkOf(sc), 0.3 * outA, false);
            env.Rect(x0, y0, sw, sh, paper, outA, GIn(env));
            env.Line(new[] { new[] { x0 + sw / 4, y0 }, new[] { x0 + sw / 4, y0 + sh } }, crease, 1, 0.8 * outA, false);
            env.Line(new[] { new[] { x0 + sw * 3 / 4, y0 }, new[] { x0 + sw * 3 / 4, y0 + sh } }, crease, 1, 0.8 * outA, false);
            void DrawGate(bool under)
            {
                for (var k = 0; k < 2; k++)
                {
                    var ph = FlapAng(k);
                    var c = Math.Cos(ph);
                    if ((c < 0) != under) continue;
                    var hx = k != 0 ? x0 + sw * 3 / 4 : x0 + sw / 4;
                    var dir = k != 0 ? -1 : 1; // 铰链线与折页指向
                    var ex = hx + dir * sw / 4 * c;
                    var skew = Math.Sin(ph) * sh * 0.05;
                    var pts = new[] { new[] { hx, y0 }, new[] { ex, y0 - skew }, new[] { ex, y0 + sh + skew }, new[] { hx, y0 + sh } };
                    var col = c > 0 ? Shade(sc, back, -0.25 * (1 - c)) : Shade(sc, paper, (k != 0 ? -0.09 : 0.03) * -c - 0.35 * (1 + c));
                    env.Poly(pts, col, outA, false);
                    if (c > 0) env.Line(new[] { new[] { ex, y0 - skew }, new[] { ex, y0 + sh + skew } }, Shade(sc, back, 0.25), 1.5, outA, false);
                    else env.Line(new[] { new[] { hx, y0 }, new[] { hx, y0 + sh } }, crease, 1, outA, false);
                }
            }

            DrawGate(true);
            bb = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size * shrink,
                ["x"] = cx,
                ["y"] = cy,
                ["color"] = tc,
                ["noHold"] = PlateHold(env),
                ["mi"] = MiAt(env, 0.15),
                ["track"] = 0.04,
                ["lead"] = 1.12,
            });
            DrawGate(false);
            env.Body = bb ?? Box(x0, y0, x0 + sw, y0 + sh);
            return;
        }
        // blintz 折法：菱形纸四角折向中心，再逐个翻开
        var Sz = Math.Min(W * 0.84, Math.Min(H * 0.84, u * 0.95)) * shrink; // 纸的对角线
        if (Sz < 2)
        {
            env.Body = null;
            return;
        }
        var R = Sz / 2;
        var C = new[] { new[] { cx, cy - R }, new[] { cx + R, cy }, new[] { cx, cy + R }, new[] { cx - R, cy } };
        var M = new double[4][];
        for (var i = 0; i < 4; i++) M[i] = new[] { (C[i][0] + C[(i + 1) % 4][0]) / 2, (C[i][1] + C[(i + 1) % 4][1]) / 2 };
        env.Poly(C.Select(p => new[] { p[0] + u * 0.012, p[1] + u * 0.018 }).ToArray(), DarkOf(sc), 0.3 * outA, false);
        env.Poly(C, paper, outA, GIn(env));
        // 折痕
        for (var i = 0; i < 4; i++) env.Line(new[] { M[i], M[(i + 1) % 4] }, crease, 1, 0.8 * outA, false);
        env.Line(new[] { C[0], C[2] }, crease, 1, 0.35 * outA, false);
        env.Line(new[] { C[1], C[3] }, crease, 1, 0.35 * outA, false);
        var text2 = n <= 4 ? text0 : Brk(text0, n <= 9 ? (int)Math.Ceiling(n / 2.0) : (int)Math.Ceiling(n / 3.0));
        var o2 = new JObject { ["track"] = 0.02, ["lead"] = 1.08 };
        var inner = R * 0.94; // 内接正方形边长 ≈ R
        var size2 = Math.Min(JLayouts.FitSize(text2, font, inner * 0.84, inner * 0.84, o2), u * 0.2);
        // 折页铰链在内接正方形边上，顶点从中心移向角；翻开的压在文字下，未翻开的盖在上面
        var ord = I(Pm, "order");
        void DrawFlaps(bool under)
        {
            for (var q = 0; q < 4; q++)
            {
                var k = (q + ord) % 4;
                var ph = FlapAng(q);
                var c = Math.Cos(ph);
                if ((c < 0) != under) continue;
                var a = M[(k + 3) % 4];
                var b = M[k];
                var hm = new[] { (a[0] + b[0]) / 2, (a[1] + b[1]) / 2 };
                var apex = new[] { hm[0] + (cx - hm[0]) * c, hm[1] + (cy - hm[1]) * c };
                var lk = new[] { 0.04, -0.1, -0.05, 0.02 }[k];
                var col = c > 0 ? Shade(sc, back, -0.3 * (1 - c)) : Shade(sc, paper, lk * -c - 0.32 * (1 + c));
                env.Poly(new[] { a, b, apex }, col, outA, false);
                if (c > -0.95) env.Line(new[] { a, apex, b }, c > 0 ? Shade(sc, back, 0.2) : crease, 1, outA, false);
                else env.Line(new[] { a, b }, crease, 1, outA, false);
            }
        }

        DrawFlaps(true);
        bb = JLayouts.MainDraw(env, new JObject
        {
            ["text"] = text2,
            ["font"] = font,
            ["size"] = size2 * shrink,
            ["x"] = cx,
            ["y"] = cy,
            ["color"] = tc,
            ["noHold"] = PlateHold(env),
            ["mi"] = MiAt(env, 0.12),
            ["track"] = 0.02,
            ["lead"] = 1.08,
        });
        DrawFlaps(false);
        env.Body = bb ?? Box(cx - R, cy - R, cx + R, cy + R);
    }

    // ---------------- zipper（源 L3926-4028） ----------------

    private static void DrawZipper(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var t0 = Flat(cut.Text);
        var n = JLayouts.GlyphCount(t0);
        var vert = port && n <= 10 && !HasLatin(t0);
        var outA = Tout(env);
        // 在接缝沿 +X 的坐标系里算，竖版拉链靠交换 xy 映回屏幕
        var Lw = vert ? H : W;
        var Lh = vert ? W : H;
        double[] Sp(double a, double b) => vert ? new[] { b, a } : new[] { a, b };
        var x0 = Lw * 0.05;
        var x1 = Lw * 0.95;
        var text = vert ? t0 : Brk(t0, port ? 5 : 9);
        var font = S(Pm, "font")!;
        var size = Math.Min(JLayouts.FitSize(text, font, vert ? Lh * 0.44 : Lw * 0.66, vert ? Lw * 0.64 : Lh * 0.36,
            new JObject { ["track"] = 0.05, ["lead"] = 1.1, ["vertical"] = vert }), u * 0.2);
        var m = JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.05, ["lead"] = 1.1, ["vertical"] = vert });
        var G = (vert ? m.W : m.H) / 2 + size * 0.55;
        var tIn = J.Clamp(cut.Dur * 0.3, 0.45, 1);
        var tOut = Math.Max(0.3, (cut.OutDur != 0 ? cut.OutDur : 0.3) + 0.2);
        var cin = J.E.OutCubic(J.Clamp(lt / 0.28));
        var cout = J.E.InCubic(J.Clamp((lt - (cut.Dur - 0.22)) / 0.22));
        var tOut2 = tOut + 0.22;
        var sp = J.E.InOutCubic(J.Clamp((lt - 0.22) / tIn)) * (1 - J.E.InOutCubic(J.Clamp((lt - (cut.Dur - tOut2)) / tOut)));
        var dir = D(Pm, "dir", 1);
        var xs = dir > 0 ? J.Lerp(x0, x1, sp) : J.Lerp(x1, x0, sp);
        var a = dir > 0 ? x0 : xs;
        var b = dir > 0 ? xs : x1;
        double Gap(double x) => x <= a || x >= b || b - a < 1
            ? 0
            : G * Math.Pow(Math.Sin(Math.PI * (x - a) / (b - a)), 0.75) * (1 + 0.02 * Math.Sin(env.Ltb * 2));
        var cy = Lh / 2;
        var cloth = PlateCol(sc, S(Pm, "cloth") == "accent" ? new[] { sc.Str("accent"), sc.Str("ink") } : new[] { sc.Str("ink"), sc.Str("accent") });
        var tape = Shade(sc, cloth, -0.25);
        var toothC = J.Mix(sc.Str("sub")!, LightOf(sc), 0.4);
        // 歌词压在布料下面
        var bb = JLayouts.MainDraw(env, new JObject
        {
            ["text"] = text,
            ["font"] = font,
            ["size"] = size,
            ["x"] = W / 2.0,
            ["y"] = H / 2.0,
            ["color"] = sc.Str("fg"),
            ["mi"] = MiAt(env, 0.15),
            ["track"] = 0.05,
            ["lead"] = 1.1,
            ["vertical"] = vert,
        });
        const int N = 90;
        var pts = new List<double[]>();
        for (var i = 0; i <= N; i++)
        {
            var x = J.Lerp(-Lw * 0.02, Lw * 1.02, i / (double)N);
            pts.Add(new[] { x, Gap(x) });
        }
        var tw = u * 0.022;
        var pitch = u * 0.02;
        var off = (1 - cin + cout) * Lh * 0.62; // 两片从两侧滑入，结尾再滑出
        if (off > Lh * 0.6)
        {
            env.Body = bb;
            return;
        }
        foreach (var s2 in new[] { -1.0, 1.0 })
        {
            var poly = new List<double[]> { Sp(-Lw * 0.05, cy + s2 * (Lh * 0.6 + off)) };
            poly.AddRange(pts.Select(p => Sp(p[0], cy + s2 * (p[1] + off))));
            poly.Add(Sp(Lw * 1.05, cy + s2 * (Lh * 0.6 + off)));
            env.Poly(poly.ToArray(), cloth, 1, false);
            env.Line(pts.Select(p => Sp(p[0], cy + s2 * (p[1] + tw * 0.5 + off))).ToArray(), tape, tw, 1, false);
            if (B(Pm, "stitch") && env.Pass == "main")
            {
                var st = pts.Select(p => Sp(p[0], cy + s2 * (p[1] + tw * 1.6 + off))).ToArray();
                env.Ctx.Save();
                using var dashPaint = new SKPaint
                {
                    IsAntialias = true, IsStroke = true, StrokeWidth = (float)Math.Max(1, u * 0.0022),
                    Color = SkRgba(Shade(sc, cloth, 0.25), 0.8 * outA),
                    PathEffect = SKPathEffect.CreateDash(new[] { (float)(u * 0.012), (float)(u * 0.01) }, 0),
                };
                using var path = new SKPath();
                path.MoveTo((float)st[0][0], (float)st[0][1]);
                for (var i = 1; i < st.Length; i++) path.LineTo((float)st[i][0], (float)st[i][1]);
                env.Ctx.DrawPath(path, dashPaint);
                env.Ctx.Restore();
            }
        }
        // 链齿：闭合处互相咬合，拉开处沿各自边走
        if (off < 1 && (env.Pass == "main" || GIn(env)))
        {
            for (var x = x0; x <= x1; x += pitch)
            {
                var gg = Gap(x);
                var k = (int)Math.Round((x - x0) / pitch);
                var up = k % 2 != 0 ? -1 : 1;
                var d = (Gap(x + 1) - Gap(x - 1)) / 2;
                var ang = gg < 0.5 ? 0 : Math.Atan(up * d);
                var tl = tw * 0.7;
                var th = pitch * 0.34;
                var yc = gg < 0.5 ? cy + up * tl * 0.28 : cy + up * (gg + tw * 0.15);
                var ca = Math.Cos(ang);
                var sa = Math.Sin(ang);
                var q = new[]
                {
                    new[] { -th, -tl / 2 }, new[] { th, -tl / 2 }, new[] { th, tl / 2 }, new[] { -th, tl / 2 },
                }.Select(pr => Sp(x + pr[0] * ca - pr[1] * sa, yc + pr[0] * sa + pr[1] * ca)).ToArray();
                env.Poly(q, toothC, outA, false);
            }
        }
        // 拉头和拉片
        if (off >= 1)
        {
            env.Body = bb;
            return;
        }
        var sxp = xs;
        var syp = cy;
        var bw = u * 0.05;
        var bh = u * 0.035;
        env.Poly(new[]
        {
            Sp(sxp - bw * 0.6 * dir, syp - bh), Sp(sxp + bw * 0.5 * dir, syp - bh * 0.7),
            Sp(sxp + bw * 0.5 * dir, syp + bh * 0.7), Sp(sxp - bw * 0.6 * dir, syp + bh),
        }, toothC, outA, false);
        var tabL = u * 0.075;
        var sw = Math.Sin(env.Ltb * 3) * 0.15;
        var tx = sxp + dir * bw * 0.1;
        var ty = syp + bh * 0.4;
        var tp = new[]
        {
            new[] { -bw * 0.28, 0.0 }, new[] { bw * 0.28, 0.0 }, new[] { bw * 0.34, tabL }, new[] { -bw * 0.34, tabL },
        }.Select(pr => Sp(tx + pr[0] * Math.Cos(sw) - pr[1] * Math.Sin(sw), ty + pr[0] * Math.Sin(sw) + pr[1] * Math.Cos(sw))).ToArray();
        env.Poly(tp, J.Mix(toothC, DarkOf(sc), 0.15), outA, false);
        var hole = Sp(tx - Math.Sin(sw) * tabL * 0.75, ty + Math.Cos(sw) * tabL * 0.75);
        env.Circle(hole[0], hole[1], bw * 0.12, cloth, null, 0, outA, false);
        env.Body = bb;
    }

    // ---------------- sliceStack（源 L4030-4168） ----------------

    private static void DrawSliceStack(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var font = S(Pm, "font")!;
        var text = Brk(cut.Text, port ? 5 : 8);
        var o = new JObject { ["track"] = 0.02, ["lead"] = 1.02 };
        var blinds = S(Pm, "mode") == "blinds";
        var size = Math.Min(JLayouts.FitSize(text, font, W * (blinds ? 0.74 : 0.78), H * (blinds ? 0.46 : 0.42), o), u * 0.3);
        var m = JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.02, ["lead"] = 1.02 });
        var Sn = I(Pm, "slices");
        var outA = Tout(env);
        var tbx = W / 2.0;
        var tby = H / 2.0;
        BBox? bb = null;
        if (blinds)
        {
            // 百叶窗：载着歌词的叶片转开，波纹掠过再合上
            var padX = size * 0.45;
            var padY = size * 0.3;
            var bh = m.H + padY * 2;
            var hs = bh / Sn;
            var gp = hs * 0.08;
            var x0 = tbx - m.W / 2 - padX;
            var w = m.W + padX * 2;
            var yTop = tby - bh / 2;
            var pc = PlateCol(sc, S(Pm, "plate") == "accent" ? new[] { sc.Str("accent"), sc.Str("ink") } : new[] { sc.Str("ink"), sc.Str("accent") });
            var tc = OnCol(sc, pc);
            var backC = Shade(sc, pc, -0.45);
            var mot = env.Fx.Motion;
            // 拉绳和导轨
            var ra = Tin(env, 0, 0.3, J.E.OutCubic) * outA;
            env.Rect(x0 - size * 0.08, yTop - hs * 0.55, w + size * 0.16, hs * 0.3, Shade(sc, pc, -0.2), ra, false);
            foreach (var cxr in new[] { x0 + w * 0.2, x0 + w * 0.8 })
                env.Line(new[] { new[] { cxr, yTop - hs * 0.3 }, new[] { cxr, yTop + bh * ra } }, sc.Str("sub"), Math.Max(1, u * 0.0015), 0.6 * ra, false);
            for (var i = 0; i < Sn; i++)
            {
                var ff = Sn > 1 ? i / (double)(Sn - 1) : 0;
                var ph = (1 - J.E.OutBack(J.Clamp((lt - 0.05 - ff * 0.25) / 0.45), 1.3)) * 90;
                ph += Math.Sin(env.Ltb * 2.4 - i * 0.7) * 16 * mot * J.Clamp((lt - 0.6) / 0.4);
                ph -= J.E.InCubic(J.Clamp(env.POut * 1.3 - ff * 0.3)) * 90;
                var c = Math.Cos(ph * J.DEG);
                var slotY = yTop + i * hs;
                var yc = slotY + hs / 2;
                var hv = (hs - gp) * Math.Abs(c);
                if (hv < 0.6) continue;
                var face = c > 0;
                env.Rect(x0, yc - hv / 2, w, hv, face ? Shade(sc, pc, -0.35 * (1 - c)) : backC, outA, false);
                env.Line(new[] { new[] { x0, yc + hv / 2 }, new[] { x0 + w, yc + hv / 2 } }, DarkOf(sc), Math.Max(1, hs * 0.04), 0.35 * outA, false);
                if (!face || c < 0.08) continue;
                env.Ctx.Save();
                env.Ctx.ClipRect(new SKRect((float)x0, (float)(yc - hv / 2), (float)(x0 + w), (float)(yc + hv / 2)));
                var by0 = tby - bh / 2 + i * hs;
                env.Ctx.Translate(0, (float)yc);
                env.Ctx.Scale(1, (float)c);
                env.Ctx.Translate(0, (float)(-yc + (slotY - by0)));
                var r = JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = text,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = tbx,
                    ["y"] = tby,
                    ["color"] = tc,
                    ["noHold"] = true,
                    ["plain"] = true,
                    ["mi"] = 0,
                    ["track"] = 0.02,
                    ["lead"] = 1.02,
                });
                env.Ctx.Restore();
                if (r != null) bb ??= r;
            }
            env.Body = bb != null ? Box(x0, yTop, x0 + w, yTop + bh) : null;
            return;
        }
        var hs2 = (m.H + size * 0.1) / Sn;
        var gp2 = hs2 * D(Pm, "gap");
        var totH = Sn * hs2 + (Sn - 1) * gp2;
        var y0 = H / 2 - totH / 2;
        var sideC = S(Pm, "side") == "accent" ? sc.Str("accent") : J.Mix(sc.Str("fg")!, sc.Str("bg")!, 0.55);
        var dep = Math.Min(gp2 * 0.9, size * 0.08);
        var A = size * D(Pm, "amp") * (0.5 + 0.7 * env.Fx.Motion);
        var mode = S(Pm, "mode");
        var dir = D(Pm, "dir", 1);
        for (var i = 0; i < Sn; i++)
        {
            var ff = Sn > 1 ? i / (double)(Sn - 1) : 0.5;
            double dx;
            if (mode == "wave") dx = A * Math.Sin(ff * J.TAU * 0.9 + env.Ltb * 2.2 * dir);
            else dx = A * 1.4 * (ff - 0.5) * 2 * dir * (0.75 + 0.25 * Math.Cos(env.Ltb * 1.1));
            var ei = J.Clamp((lt - i * 0.025) / 0.45);
            dx += (1 - J.E.OutExpo(ei)) * W * 0.7 * (i % 2 != 0 ? 1 : -1);
            dx += J.E.InCubic(J.Clamp(env.POut * 1.3 - ff * 0.3)) * W * 0.8 * (i % 2 != 0 ? -1 : 1);
            var by0 = tby - Sn * hs2 / 2 + i * hs2;
            var slotY = y0 + i * (hs2 + gp2);
            var dy = slotY - by0;
            if (env.Pass == "main")
            {
                env.Ctx.Save();
                env.Ctx.ClipRect(new SKRect((float)-W, (float)(slotY + hs2 - 0.5), (float)(W * 2), (float)(slotY + hs2 - 0.5 + dep + 0.5)));
                env.Ctx.Translate((float)(dx + dep * 0.5), (float)(dy + dep));
                JLayouts.MainDraw(env, new JObject
                {
                    ["text"] = text,
                    ["font"] = font,
                    ["size"] = size,
                    ["x"] = tbx,
                    ["y"] = tby,
                    ["color"] = sideC,
                    ["ghost"] = false,
                    ["noHold"] = true,
                    ["mi"] = 0,
                    ["track"] = 0.02,
                    ["lead"] = 1.02,
                });
                env.Ctx.Restore();
            }
            env.Ctx.Save();
            env.Ctx.ClipRect(new SKRect((float)-W, (float)slotY, (float)(W * 2), (float)(slotY + hs2 + 0.5)));
            env.Ctx.Translate((float)dx, (float)dy);
            var r2 = JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = tbx,
                ["y"] = tby,
                ["color"] = sc.Str("fg"),
                ["noHold"] = true,
                ["mi"] = 0,
                ["track"] = 0.02,
                ["lead"] = 1.02,
            });
            env.Ctx.Restore();
            if (r2 != null) bb ??= r2;
        }
        var w2 = m.W / 2 + A * 1.4;
        env.Body = bb != null ? Box(tbx - w2, y0, tbx + w2, y0 + totH) : null;
    }

    // ---------------- glitchGrid（源 L4170-4268） ----------------

    private static void DrawGlitchGrid(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var sq = !port && W < H * 1.45;
        var C = port ? 3 : sq ? 3 : 4;
        var R = port ? 5 : 3;
        var g = u * D(Pm, "gut");
        var mx = W * 0.04;
        var my = H * 0.05;
        var cw = (W - mx * 2 - g * (C - 1)) / C;
        var ch = (H - my * 2 - g * (R - 1)) / R;
        // 干净格：竖屏占满中间一行，横屏是中间两三格
        var cr = (int)Math.Floor(R / 2.0);
        var cc0 = port ? 0 : C == 4 ? 1 : 0;
        var cc1 = port ? C - 1 : C == 4 ? 2 : C - 1;
        var outA = Tout(env);
        var text = Flat(cut.Text);
        var t2 = Brk(cut.Text, port ? 5 : 8);
        var cols = new[] { sc.Str("fg"), sc.Str("accent"), sc.Str("sub"), sc.Str("ghostA"), sc.Str("ghostB") }
            .Where(c => !string.IsNullOrEmpty(c) && J.Contrast(c!, sc.Str("bg") ?? "#000000") > 1.4).ToList();
        if (cols.Count == 0) cols.Add(sc.Str("fg") ?? "#FFFFFF");
        var lw = Math.Max(1, u * 0.0015);
        double[] CellRect(int r, int c0, int c1) => new[]
        {
            mx + c0 * (cw + g), my + r * (ch + g), (c1 - c0 + 1) * cw + (c1 - c0) * g, ch,
        };
        var font = S(Pm, "font")!;
        var rate = I(Pm, "rate");
        var labels = B(Pm, "labels");
        for (var r = 0; r < R; r++)
            for (var c = 0; c < C; c++)
            {
                if (r == cr && c >= cc0 && c <= cc1) continue;
                var id = r * C + c;
                var t1 = 0.03 + J.R(s, id, 1) * 0.35;
                var on = lt > t1 && (lt - t1 > 0.12 || J.R(s, id, (int)env.Step, 2) < 0.6) && !(env.POut > J.R(s, id, 3) * 0.8 + 0.1);
                if (!on) continue;
                var rect = CellRect(r, c, c);
                var x = rect[0];
                var y = rect[1];
                var w = rect[2];
                var h = rect[3];
                var kk = (int)Math.Floor((env.Step + J.H(s, id, 4) % 7) / (double)rate);
                var inv = J.R(s, id, kk, 5) < D(Pm, "inv");
                var col = J.Mix(cols[(int)(J.H(s, id, kk, 6) % (uint)cols.Count)]!, sc.Str("bg")!, 0.25);
                var zoom = J.Rr(1.4, 3.6, s, id, kk, 7);
                var fsz = Math.Min(h * 0.9, w * 0.9) * zoom * 0.5;
                var ox = J.Rs(s, id, kk, 8) * w * 0.5;
                var oy = J.Rs(s, id, kk, 9) * h * 0.3;
                if (inv) env.Rect(x, y, w, h, col, outA * 0.85, false);
                else env.Rect(x, y, w, h, J.Mix(sc.Str("bg")!, sc.Str("fg")!, 0.04), outA, false);
                if (env.Pass == "main")
                {
                    env.Ctx.Save();
                    env.Ctx.ClipRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)));
                    var fresh = (env.Step + J.H(s, id, 4) % 7) % rate == 0;
                    var sl = fresh ? J.Rs(s, id, (int)env.Step, 10) * w * 0.12 : 0;
                    env.DrawItem(new JObject
                    {
                        ["text"] = text,
                        ["font"] = font,
                        ["size"] = fsz,
                        ["x"] = x + w / 2 + ox + sl,
                        ["y"] = y + h / 2 + oy,
                        ["color"] = inv ? OnCol(sc, col) : col,
                        ["alpha"] = outA * (inv ? 0.9 : 0.6),
                        ["ghost"] = false,
                    });
                    if (fresh) env.Rect(x, y + h * J.R(s, id, (int)env.Step, 11), w, h * 0.06, inv ? OnCol(sc, col) : col, 0.6 * outA, false);
                    env.Ctx.Restore();
                    if (labels)
                        env.DrawItem(new JObject
                        {
                            ["text"] = $"CH.{Pad2(id + 1)}  x{zoom.ToString("F1", CultureInfo.InvariantCulture)}",
                            ["font"] = MonoF(env),
                            ["size"] = Math.Max(10, u * 0.013),
                            ["align"] = "left",
                            ["x"] = x + u * 0.01,
                            ["y"] = y + u * 0.016,
                            ["color"] = inv ? OnCol(sc, col) : sc.Str("sub"),
                            ["alpha"] = 0.8 * outA,
                            ["ghost"] = false,
                        });
                }
                env.RRect(x, y, w, h, 0, null, outA, false, J.Mix(sc.Str("bg")!, sc.Str("fg")!, 0.2), lw);
            }
        var cr2 = CellRect(cr, cc0, cc1);
        var x3 = cr2[0];
        var y3 = cr2[1];
        var w3 = cr2[2];
        var h3 = cr2[3];
        var e = J.E.OutExpo(J.Clamp(lt / 0.35));
        env.RRect(x3 + w3 / 2 - w3 / 2 * e, y3, w3 * e, h3, 0, null, outA, false, sc.Str("accent"), Math.Max(2, u * 0.003));
        var size = Math.Min(JLayouts.FitSize(t2, font, w3 * 0.88, h3 * 0.8, new JObject { ["track"] = 0.04, ["lead"] = 1.1 }), u * 0.24);
        if (labels && env.Pass == "main")
            env.DrawItem(new JObject
            {
                ["text"] = "REC ● CLEAN",
                ["font"] = MonoF(env),
                ["size"] = Math.Max(10, u * 0.014),
                ["align"] = "left",
                ["x"] = x3 + u * 0.012,
                ["y"] = y3 + u * 0.018,
                ["color"] = sc.Str("accent"),
                ["alpha"] = outA * (env.Step % 4 < 3 ? 1 : 0.3),
                ["ghost"] = false,
            });
        env.Body = JLayouts.MainDraw(env, new JObject
        {
            ["text"] = t2,
            ["font"] = font,
            ["size"] = size,
            ["x"] = x3 + w3 / 2,
            ["y"] = y3 + h3 / 2,
            ["color"] = sc.Str("fg"),
            ["track"] = 0.04,
            ["lead"] = 1.1,
        });
    }

    // ---------------- mosaicTiles（源 L4270-4403） ----------------

    private sealed record MosaicSmp(int Cols, int Rows, List<int> Lit);

    /// <summary>文字 alpha 覆盖采样（源 mosaicSample，离屏 4× 超采样后按格求均值）。</summary>
    private static MosaicSmp MosaicSample(string text, string font, int D, double lead) =>
        Memoize($"mos|{text}|{font}|{D}|{lead.ToString(CultureInfo.InvariantCulture)}", () =>
        {
            const int SS = 4;
            var fpx = D * SS;
            var lay = JText.LayoutText(new JObject { ["text"] = text, ["font"] = font, ["size"] = fpx, ["lead"] = lead });
            var cols = (int)Math.Ceiling(lay.W / SS) + 2;
            var rows = (int)Math.Ceiling(lay.H / SS) + 2;
            var cw = cols * SS;
            var chh = rows * SS;
            var bmp = new SKBitmap(Math.Max(1, cw), Math.Max(1, chh), SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var x = new SKCanvas(bmp))
            {
                x.Clear(SKColors.Transparent);
                var f = JText.FaceFont(font, fpx); // 缓存实例，禁 Dispose
                f.GetFontMetrics(out var fm);
                var yMid = (float)((-fm.Ascent - fm.Descent) / 2);
                using var fill = new SKPaint { IsAntialias = true, Color = SKColors.White };
                using var stroke = new SKPaint
                {
                    IsAntialias = true, Color = SKColors.White, IsStroke = true,
                    StrokeWidth = (float)(SS * 0.7), StrokeJoin = SKStrokeJoin.Round,
                };
                foreach (var g in lay.Cells)
                {
                    if (g.Ch is " " or "　") continue;
                    var px = cw / 2f + (float)g.X;
                    var py = chh / 2f + (float)g.Y + yMid;
                    x.DrawText(g.Ch, px, py, SKTextAlign.Center, f, fill);
                    x.DrawText(g.Ch, px, py, SKTextAlign.Center, f, stroke);
                }
            }
            var lit = new List<int>();
            using (var px = bmp.PeekPixels())
                for (var r = 0; r < rows; r++)
                    for (var c = 0; c < cols; c++)
                    {
                        var a = 0.0;
                        for (var yy = 0; yy < SS; yy++)
                            for (var xx = 0; xx < SS; xx++)
                                a += px.GetPixelColor(c * SS + xx, r * SS + yy).Alpha;
                        if (a / (SS * SS * 255.0) > 0.42)
                        {
                            lit.Add(c);
                            lit.Add(r);
                        }
                    }
            bmp.Dispose();
            return new MosaicSmp(cols, rows, lit);
        });

    private static void DrawMosaicTiles(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var font = S(Pm, "font")!;
        var text = Brk(cut.Text, port ? 4 : 7);
        const double lead = 1.15;
        var D = I(Pm, "D");
        var smp = MosaicSample(text, font, D, lead);
        if (smp.Lit.Count == 0)
        {
            env.Body = null;
            return;
        }
        var p = Math.Min(W * 0.88 / smp.Cols, H * 0.66 / smp.Rows);
        var gw = smp.Cols * p;
        var gh = smp.Rows * p;
        var gx = W / 2 - gw / 2;
        var gy = H / 2 - gh / 2;
        var outA = Tout(env);
        var col = S(Pm, "col") == "accent" && J.Contrast(sc.Str("accent") ?? "#000000", sc.Str("bg") ?? "#000000") > 1.8
            ? sc.Str("accent")! : sc.Str("fg")!;
        var tileW = p * 0.86;
        var rr = p * 0.14;
        // 底板：整面板每格一块淡瓷砖
        if (B(Pm, "floor") && env.Pass == "main")
        {
            var fa = Tin(env, 0, 0.4, J.E.OutCubic) * outA;
            using var fp = new SKPaint { IsAntialias = true, Color = SkRgba(J.Mix(sc.Str("bg")!, sc.Str("fg")!, 0.07), fa) };
            const int padT = 1;
            for (var r = -padT; r < smp.Rows + padT; r++)
                for (var c = -padT; c < smp.Cols + padT; c++)
                {
                    var x = gx + (c + 0.5) * p;
                    var y = gy + (r + 0.5) * p;
                    env.Ctx.DrawRect(new SKRect((float)(x - tileW / 2), (float)(y - tileW / 2), (float)(x + tileW / 2), (float)(y + tileW / 2)), fp);
                }
        }
        // 亮格：把动画中的格子拼成裁剪路径，歌词加粗透过它画
        var L = smp.Lit;
        var NT = L.Count / 2;
        var span = J.Clamp(cut.Dur * 0.3, 0.3, 0.8);
        var wave = S(Pm, "wave");
        double Ord(int c, int r) => wave == "diag"
            ? (c + r) / (double)(smp.Cols + smp.Rows)
            : wave == "center"
                ? Hypot(c - smp.Cols / 2.0, r - smp.Rows / 2.0) / Hypot(smp.Cols / 2.0, smp.Rows / 2.0)
                : J.R(s, c, r, 3);
        using var clipPath = new SKPath();
        var any = false;
        var rotOn = B(Pm, "rot");
        for (var k = 0; k < L.Count; k += 2)
        {
            var c = L[k];
            var r = L[k + 1];
            var t1 = 0.08 + Ord(c, r) * span;
            var e = J.E.OutBack(J.Clamp((lt - t1) / 0.22), 1.8);
            var eo = J.Clamp(env.POut * 1.4 - J.R(s, c, r, 4) * 0.4);
            e *= 1 - J.E.InCubic(eo);
            if (e <= 0.02) continue;
            var x = gx + (c + 0.5) * p;
            var y = gy + (r + 0.5) * p;
            var hw = tileW / 2 * e;
            if (rotOn)
            {
                var a = J.Rs(s, c, r, 5) * 0.2 + (1 - e) * 1.2;
                var ca = Math.Cos(a) * hw;
                var sa = Math.Sin(a) * hw;
                clipPath.MoveTo((float)(x - ca + sa), (float)(y - sa - ca));
                clipPath.LineTo((float)(x + ca + sa), (float)(y + sa - ca));
                clipPath.LineTo((float)(x + ca - sa), (float)(y + sa + ca));
                clipPath.LineTo((float)(x - ca - sa), (float)(y - sa + ca));
                clipPath.Close();
            }
            else clipPath.AddRect(new SKRect((float)(x - hw), (float)(y - hw), (float)(x + hw), (float)(y + hw)));
            any = true;
        }
        if (!any) clipPath.AddRect(new SKRect(-10, -10, -9, -9));
        env.Ctx.Save();
        env.Ctx.ClipPath(clipPath);
        var fsz = D * p;
        var bb = JLayouts.MainDraw(env, new JObject
        {
            ["text"] = text,
            ["font"] = font,
            ["size"] = fsz,
            ["x"] = W / 2.0,
            ["y"] = H / 2.0,
            ["lead"] = lead,
            ["color"] = col,
            ["stroke"] = p * 1.2,
            ["strokeColor"] = col,
            ["strokeUnder"] = true,
            ["noHold"] = true,
            ["mi"] = 0,
        });
        // 瓷砖间色差和斜面高光
        if (env.Pass == "main")
            for (var k = 0; k < L.Count; k += 2)
            {
                var c = L[k];
                var r = L[k + 1];
                var h = J.R(s, c, r, 6);
                if (h > 0.45) continue;
                var x = gx + c * p + (p - tileW) / 2;
                var y = gy + r * p + (p - tileW) / 2;
                env.Rect(x, y, tileW, tileW,
                    h < 0.18 ? DarkOf(sc) : h < 0.3 ? LightOf(sc) : sc.Str("accent2") ?? sc.Str("accent"),
                    (h < 0.18 ? 0.16 : 0.14) * outA, false);
            }
        env.Ctx.Restore();
        env.Body = bb != null ? Box(gx, gy, gx + gw, gy + gh) : null;
    }

    // ---------------- maskReveal（源 L4405-4553） ----------------

    private static void DrawMaskReveal(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var font = S(Pm, "font")!;
        var text = Brk(cut.Text, port ? 4 : 7);
        var o = new JObject { ["track"] = 0.0, ["lead"] = 1.0 };
        var size = Math.Min(JLayouts.FitSize(text, font, W * 0.88, H * (port ? 0.56 : 0.66), o), u * 0.46);
        var m = JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.0, ["lead"] = 1.0 });
        var outA = Tout(env);
        var A = new[] { sc.Str("accent"), sc.Str("fg") }
            .FirstOrDefault(c => J.Contrast(c ?? "#000000", sc.Str("bg") ?? "#000000") >= 1.8) ?? sc.Str("fg")!;
        var B2 = new[] { sc.Str("fg"), LightOf(sc), sc.Str("accent2") }
            .FirstOrDefault(c => !string.IsNullOrEmpty(c) && c != A && J.Contrast(c!, sc.Str("bg") ?? "#000000") >= 1.8) ?? A;
        var tb = env.Ltb * D(Pm, "speed");
        var scene = S(Pm, "scene")!;
        // 透过字母看到的场景：字形空间里的 pattern 或渐变，随时间移动。
        // TODO(11p)：JS 用 Canvas pattern 的 setTransform 做角度/流动（stripes/dots/lines 的 Pm.ang、
        // tb 位移）；C# 走 DrawItem 的 pattern/gradient 键（Skia 图案不随时间变换），
        // 静止图案方向/相位与 JS 有差（'shine' 走逐帧重算的多段渐变，是完全对位的）。
        var baseIt = new JObject
        {
            ["text"] = text,
            ["font"] = font,
            ["size"] = size,
            ["x"] = W / 2.0,
            ["y"] = H / 2.0,
            ["color"] = B2,
            ["mi"] = 0,
            ["track"] = 0.0,
            ["lead"] = 1.0,
        };
        if (scene == "shine")
        {
            var g = new JArray();
            var ph = (tb * 0.45 % 1 + 1) % 1;
            var gTok = sc.Get("grad") as JArray;
            var cA = gTok is { Count: >= 2 } ? (string?)gTok[0] : A;
            var cB = gTok is { Count: >= 2 } ? (string?)gTok[1] : B2;
            for (var k = 0; k <= 6; k++)
            {
                var f = k / 6.0;
                var v = 0.5 + 0.5 * Math.Cos((f - ph) * J.TAU);
                g.Add(new JArray(f, J.Mix(cA ?? A, cB ?? B2, v)));
            }
            baseIt["gradient"] = g;
        }
        else
        {
            baseIt["pattern"] = scene == "stripes" ? "stripes" : scene == "dots" ? "dots" : "lines";
            baseIt["patternColor"] = A;
            baseIt["patternBg"] = B2;
        }
        var bb = JLayouts.MainDraw(env, baseIt);
        if (B(Pm, "rim"))
            JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text,
                ["font"] = font,
                ["size"] = size,
                ["x"] = W / 2.0,
                ["y"] = H / 2.0,
                ["mi"] = 0,
                ["track"] = 0.0,
                ["lead"] = 1.0,
                ["fill"] = false,
                ["stroke"] = Math.Max(1.5, size * 0.012),
                ["strokeColor"] = A,
                ["alpha"] = 0.9,
                ["ghost"] = false,
            });
        if (B(Pm, "label"))
        {
            var ls = SmallSize(env);
            var lab = AltCopy(env);
            var a = Tin(env, 0.25, 0.4, J.E.OutCubic) * outA;
            env.DrawItem(new JObject
            {
                ["text"] = lab.Length > 30 ? lab.Substring(0, 29) + "…" : lab,
                ["font"] = BodyF(env),
                ["size"] = ls,
                ["track"] = 0.2,
                ["align"] = "left",
                ["x"] = W / 2 - m.W / 2,
                ["y"] = Math.Min(H * 0.95, H / 2 + m.H / 2 + ls * 1.8),
                ["color"] = sc.Str("sub"),
                ["alpha"] = a,
                ["ghost"] = false,
            });
        }
        env.Body = bb;
    }

    // ---------------- contour（源 L4555-4640） ----------------

    private static void DrawContour(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var outA = Tout(env);
        var t0 = Strip(cut.Text);
        var kan = JLayouts.CodePoints(t0).Where(c => J.IsKanji(c[0])).ToList();
        var big = kan.Count > 0
            ? kan[0]
            : JLayouts.CodePoints(t0).FirstOrDefault(c => !J.IsPunct(c[0]) && !J.IsSmallKana(c[0]))
              ?? JLayouts.CodePoints(t0).FirstOrDefault() ?? "";
        var bs = Math.Min(H * 0.95, W * (port ? 1.05 : 0.7));
        var bx = W / 2 + D(Pm, "side") * W * (port ? 0.12 : 0.2);
        var by = H * 0.5;
        var lineC = S(Pm, "col") == "accent" ? sc.Str("accent") : sc.Str("sub");
        // 等高线：线色粗描一遍，再用略细的背景色描一遍，得到每级一圈细线
        var cin = Tin(env, 0, 0.5, J.E.OutCubic) * outA;
        if (env.Pass == "main" && big.Length > 0 && cin > 0.01)
        {
            var K = I(Pm, "rings");
            var gap = bs * 0.03;
            var lw = Math.Max(1.2, bs * 0.0028);
            var ph = (env.Ltb * D(Pm, "speed") % 1 + 1) % 1;
            var grow = J.E.OutCubic(J.Clamp(lt / 0.9));
            var fontKey = S(Pm, "fb")!;
            var skf = JText.FaceFont(fontKey, bs); // 缓存实例，禁 Dispose
            skf.GetFontMetrics(out var fm);
            var yMid = (float)((-fm.Ascent - fm.Descent) / 2);
            var yy = (float)by + yMid;
            env.Ctx.Save();
            for (var k = K; k >= 1; k--)
            {
                var wk = 2 * gap * (k - 1 + ph) * grow + gap * 0.6;
                var a = (k == K ? 1 - ph : 1) * (1 - 0.1 * k) * cin;
                if (a <= 0.01) continue;
                using (var p = new SKPaint
                {
                    IsAntialias = true, IsStroke = true, StrokeWidth = (float)(wk + lw),
                    StrokeJoin = SKStrokeJoin.Round, Color = SkRgba(lineC, a * 0.8),
                })
                    env.Ctx.DrawText(big, (float)bx, yy, SKTextAlign.Center, skf, p);
                using (var p = new SKPaint
                {
                    IsAntialias = true, IsStroke = true, StrokeWidth = (float)Math.Max(0.1, wk - lw),
                    StrokeJoin = SKStrokeJoin.Round, Color = SkRgba(sc.Str("bg"), 0.94 * cin),
                })
                    env.Ctx.DrawText(big, (float)bx, yy, SKTextAlign.Center, skf, p);
            }
            using (var p = new SKPaint { IsAntialias = true, Color = SkRgba(sc.Str("bg"), 0.94 * cin) })
                env.Ctx.DrawText(big, (float)bx, yy, SKTextAlign.Center, skf, p);
            using (var p = new SKPaint
            {
                IsAntialias = true, IsStroke = true, StrokeWidth = (float)(lw * 1.6),
                StrokeJoin = SKStrokeJoin.Round, Color = SkRgba(lineC, cin),
            })
                env.Ctx.DrawText(big, (float)bx, yy, SKTextAlign.Center, skf, p);
            env.Ctx.Restore();
        }
        // 歌词小字实色，放在大字旁的安静区
        var text = Brk(cut.Text, port ? 6 : 8);
        var o = new JObject { ["track"] = 0.06, ["lead"] = 1.25 };
        var font = S(Pm, "font")!;
        var size = Math.Min(JLayouts.FitSize(text, font, W * (port ? 0.8 : 0.5), H * 0.3, o), u * 0.12);
        var m = JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.06, ["lead"] = 1.25 });
        var tx = port ? W / 2.0 : W / 2 - D(Pm, "side") * W * 0.2;
        var ty = S(Pm, "place") == "low" ? H * (port ? 0.78 : 0.7) : H / 2.0;
        var mx = J.Clamp(tx, W * 0.06 + m.W / 2, W * 0.94 - m.W / 2);
        var bb = JLayouts.MainDraw(env, new JObject
        {
            ["text"] = text, ["font"] = font, ["size"] = size, ["x"] = mx, ["y"] = ty, ["color"] = sc.Str("fg"),
            ["track"] = 0.06, ["lead"] = 1.25,
        });
        var la = Tin(env, 0.3, 0.4, J.E.OutCubic) * outA;
        if (bb != null && la > 0)
        {
            var ls = SmallSize(env) * 0.9;
            env.Line(new[]
            {
                new[] { bb.X0, bb.Y0 - ls * 1.2 }, new[] { bb.X0 + (bb.X1 - bb.X0) * la, bb.Y0 - ls * 1.2 },
            }, lineC, Math.Max(1, u * 0.0015), 0.8, false);
            env.DrawItem(new JObject
            {
                ["text"] = $"{big}  ─  No.{LineNo(env)}",
                ["font"] = MonoF(env),
                ["size"] = ls,
                ["track"] = 0.2,
                ["align"] = "left",
                ["x"] = bb.X0,
                ["y"] = bb.Y0 - ls * 2.3,
                ["color"] = sc.Str("sub"),
                ["alpha"] = la,
                ["ghost"] = false,
            });
        }
        env.Body = bb;
    }

    // ---------------- halftoneBig（源 L4642-4743） ----------------

    private sealed record CovSmp(int S, int Cw, int Ch, byte[] A);

    /// <summary>文字块的 alpha 覆盖，按 em=64px 采样一次并 memo（源 coverage）。</summary>
    private static CovSmp Coverage(string text, string font, double lead, double track) =>
        Memoize($"cov|{text}|{font}|{lead.ToString(CultureInfo.InvariantCulture)}|{track.ToString(CultureInfo.InvariantCulture)}", () =>
        {
            const int S = 64;
            var lay = JText.LayoutText(new JObject { ["text"] = text, ["font"] = font, ["size"] = S, ["lead"] = lead, ["track"] = track });
            const int pad = 8;
            var cw = (int)Math.Ceiling(lay.W) + pad * 2;
            var ch = (int)Math.Ceiling(lay.H) + pad * 2;
            var bmp = new SKBitmap(Math.Max(1, cw), Math.Max(1, ch), SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var x = new SKCanvas(bmp))
            {
                x.Clear(SKColors.Transparent);
                var f = JText.FaceFont(font, S); // 缓存实例，禁 Dispose
                f.GetFontMetrics(out var fm);
                var yMid = (float)((-fm.Ascent - fm.Descent) / 2);
                using var fill = new SKPaint { IsAntialias = true, Color = SKColors.White };
                foreach (var g in lay.Cells)
                {
                    if (g.Ch is " " or "　") continue;
                    x.DrawText(g.Ch, cw / 2f + (float)g.X, ch / 2f + (float)g.Y + yMid, SKTextAlign.Center, f, fill);
                }
            }
            var a = new byte[cw * ch];
            using (var px = bmp.PeekPixels())
                for (var i = 0; i < a.Length; i++)
                    a[i] = px.GetPixelColor(i % cw, i / cw).Alpha;
            bmp.Dispose();
            return new CovSmp(S, cw, ch, a);
        });

    private static void DrawHalftoneBig(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var font = S(Pm, "font")!;
        var text = Brk(cut.Text, port ? 4 : 5);
        var o = new JObject { ["track"] = -0.02, ["lead"] = 0.98 };
        var fitW = B(Pm, "crop") ? 1.12 : 0.9;
        var size = Math.Min(JLayouts.FitSize(text, font, W * fitW, H * (port ? 0.6 : 0.8), o), u * 0.7);
        var m = JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = -0.02, ["lead"] = 0.98 });
        var mode = S(Pm, "mode");
        var dotC = new[] { sc.Str("fg"), LightOf(sc) }
            .FirstOrDefault(c => J.Contrast(c ?? "#000000", sc.Str("bg") ?? "#000000") >= 3) ?? sc.Str("fg")!;
        var duoC = new[] { sc.Str("accent"), sc.Str("accent2") }
            .FirstOrDefault(c => !string.IsNullOrEmpty(c) && J.Contrast(c!, sc.Str("bg") ?? "#000000") >= 1.6 && c != dotC) ?? sc.Str("sub")!;
        JObject It() => new()
        {
            ["text"] = text, ["font"] = font, ["size"] = size, ["x"] = W / 2.0, ["y"] = H / 2.0, ["color"] = dotC, ["mi"] = 0,
            ["track"] = -0.02, ["lead"] = 0.98,
        };

        if (mode == "duo")
        {
            var d = size * 0.045;
            JLayouts.MainDraw(env, new JObject
            {
                ["text"] = text, ["font"] = font, ["size"] = size, ["x"] = W / 2 + d, ["y"] = H / 2 + d,
                ["color"] = duoC, ["ghost"] = false, ["mi"] = 0, ["track"] = -0.02, ["lead"] = 0.98,
            });
        }
        var cov = Coverage(text, font, 0.98, -0.02);
        if (env.Pass != "main")
        {
            env.Body = JLayouts.MainDraw(env, It());
            return;
        }
        // 旋转网屏上落在字母内的网点，文字透过这些点画
        var pitch = Math.Max(6, size * 0.068);
        var a = D(Pm, "ang") * J.DEG;
        var ca = Math.Cos(a);
        var sa = Math.Sin(a);
        var grow = J.E.OutCubic(J.Clamp(lt / 0.7)) * (1 - 0.6 * J.E.InCubic(env.POut));
        var ph = env.Ltb * D(Pm, "speed");
        var k = cov.S / size;
        var cx = W / 2.0;
        var cy = H / 2.0;
        var R = Hypot(m.W, m.H) / 2 + pitch;
        var N = (int)Math.Ceiling(R / pitch);
        var shapeLine = S(Pm, "shape") == "line";
        using var dots = new SKPath();
        var cnt = 0;
        for (var j = -N; j <= N && cnt < 4000; j++)
            for (var i = -N; i <= N && cnt < 4000; i++)
            {
                var lx = i * pitch;
                var ly = j * pitch;
                var x = cx + lx * ca - ly * sa;
                var y = cy + lx * sa + ly * ca;
                var px = (int)Math.Round((x - cx) * k + cov.Cw / 2.0);
                var py = (int)Math.Round((y - cy) * k + cov.Ch / 2.0);
                if (px < 0 || py < 0 || px >= cov.Cw || py >= cov.Ch || cov.A[py * cov.Cw + px] < 60) continue;
                var f = lx / R * 0.5 + 0.5;
                var tone = J.Clamp(0.3 + 0.7 * (0.5 + 0.5 * Math.Sin((f * 1.6 - ph) * Math.PI)));
                var r = pitch * 0.66 * Math.Sqrt(tone) * grow;
                if (r < 0.4) continue;
                if (shapeLine)
                {
                    var hw = pitch * 0.5;
                    var hh = r * 0.72;
                    dots.MoveTo((float)(x - hw * ca + hh * sa), (float)(y - hw * sa - hh * ca));
                    dots.LineTo((float)(x + hw * ca + hh * sa), (float)(y + hw * sa - hh * ca));
                    dots.LineTo((float)(x + hw * ca - hh * sa), (float)(y + hw * sa + hh * ca));
                    dots.LineTo((float)(x - hw * ca - hh * sa), (float)(y - hw * sa + hh * ca));
                    dots.Close();
                }
                else
                {
                    dots.MoveTo((float)(x + r), (float)y);
                    dots.AddCircle((float)x, (float)y, (float)r);
                }
                cnt++;
            }
        if (cnt == 0) dots.AddRect(new SKRect(-10, -10, -9, -9));
        env.Ctx.Save();
        env.Ctx.ClipPath(dots);
        var bb = JLayouts.MainDraw(env, new JObject
        {
            ["text"] = text, ["font"] = font, ["size"] = size, ["x"] = W / 2.0, ["y"] = H / 2.0, ["color"] = dotC, ["mi"] = 0,
            ["track"] = -0.02, ["lead"] = 0.98,
            ["stroke"] = pitch * 0.5, ["strokeColor"] = dotC, ["strokeUnder"] = true,
        });
        env.Ctx.Restore();
        env.Body = bb;
    }

    // ---------------- stencil（源 L4745-4891） ----------------

    private static void DrawStencil(Env env)
    {
        var W = env.W;
        var H = env.H;
        var sc = env.Sc!;
        var cut = env.Cut!;
        var Pm = env.P;
        var s = (int)cut.Seed;
        var u = U(env);
        var lt = env.Lt;
        var port = IsPort(env);
        var font = S(Pm, "font")!;
        var text = Brk(cut.Text, port ? 4 : 7);
        var o = new JObject { ["track"] = 0.08, ["lead"] = 1.12 };
        var size = Math.Min(JLayouts.FitSize(text, font, W * 0.84, H * (port ? 0.5 : 0.56), o), u * 0.32);
        var m = JLayouts.Measure(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.08, ["lead"] = 1.12 });
        var outA = Tout(env);
        var tilt = D(Pm, "tilt");
        var col = S(Pm, "col") == "accent" && J.Contrast(sc.Str("accent") ?? "#000000", sc.Str("bg") ?? "#000000") >= 1.8
            ? sc.Str("accent")! : sc.Str("fg")!;
        JObject It() => new()
        {
            ["text"] = text, ["font"] = font, ["size"] = size, ["x"] = W / 2.0, ["y"] = H / 2.0, ["rot"] = tilt,
            ["color"] = col, ["mi"] = 0, ["track"] = 0.08, ["lead"] = 1.12,
        };

        var x0 = W / 2 - m.W / 2;
        var x1 = W / 2 + m.W / 2;
        var y0 = H / 2 - m.H / 2;
        var y1 = H / 2 + m.H / 2;
        // 静止时的字形盒，item 空间经倾斜映射到设计空间
        var lay = JText.LayoutText(new JObject { ["text"] = text, ["font"] = font, ["size"] = size, ["track"] = 0.08, ["lead"] = 1.12 });
        var tr = tilt * J.DEG;
        var ct = Math.Cos(tr);
        var st2 = Math.Sin(tr);
        var boxes = new List<(double Lx, double Ly, double W, double H)>();
        foreach (var g in lay.Cells)
        {
            if (g.Ch is " " or "　" || J.IsPunct(g.Ch[0])) continue;
            boxes.Add((g.X, g.Y, g.W, g.H));
        }
        var sw = J.Clamp(cut.Dur * 0.3, 0.35, 0.9);
        var f = J.E.InOutSine(J.Clamp((lt - 0.05) / sw));
        var dir = D(Pm, "dir", 1);
        var head = dir > 0 ? J.Lerp(x0 - size * 0.3, x1 + size * 0.3, f) : J.Lerp(x1 + size * 0.3, x0 - size * 0.3, f);
        if (B(Pm, "marks"))
        {
            var a = Tin(env, 0, 0.35, J.E.OutCubic) * outA;
            var L = size * 0.18;
            var lw = Math.Max(1, u * 0.0016);
            var p = size * 0.28;
            foreach (var (cx, cy) in new[] { (x0 - p, y0 - p), (x1 + p, y0 - p), (x1 + p, y1 + p), (x0 - p, y1 + p) })
            {
                env.Line(new[] { new[] { cx - L, cy }, new[] { cx + L, cy } }, sc.Str("sub"), lw, a, false);
                env.Line(new[] { new[] { cx, cy - L }, new[] { cx, cy + L } }, sc.Str("sub"), lw, a, false);
                env.Circle(cx, cy, L * 0.45, null, sc.Str("sub"), lw, a, false);
            }
            env.DrawItem(new JObject
            {
                ["text"] = $"No.{LineNo(env)}  /  {J.FmtTime(cut.Start)}",
                ["font"] = MonoF(env),
                ["size"] = SmallSize(env),
                ["track"] = 0.25,
                ["align"] = "left",
                ["x"] = x0 - p + L * 1.4,
                ["y"] = y0 - p,
                ["color"] = sc.Str("sub"),
                ["alpha"] = a,
                ["ghost"] = false,
            });
        }
        // 裁剪：喷头已喷的一侧，再减去模板桥接处，evenodd 挖洞
        env.Ctx.Save();
        if (dir > 0) env.Ctx.ClipRect(new SKRect((float)-W, (float)-H, (float)(head + W), (float)(H * 3)));
        else env.Ctx.ClipRect(new SKRect((float)head, (float)-H, (float)(head + W * 2), (float)(H * 3)));
        using (var stencilPath = new SKPath { FillType = SKPathFillType.EvenOdd })
        {
            stencilPath.AddRect(new SKRect((float)-W, (float)-H, (float)(W * 2), (float)(H * 2)));
            var bw = size * 0.05;
            for (var i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                var vx = b.Lx + (i % 3 - 1) * b.W * 0.06;
                stencilPath.AddRect(new SKRect((float)(vx - bw / 2), (float)(b.Ly - b.H * 0.62), (float)(vx + bw / 2), (float)(b.Ly + b.H * 0.62)));
                if (i % 2 != 0)
                {
                    var hy = b.Ly - b.H * 0.06 - bw / 2;
                    stencilPath.AddRect(new SKRect((float)(b.Lx - b.W * 0.62), (float)hy, (float)(vx - bw / 2), (float)(hy + bw)));
                    stencilPath.AddRect(new SKRect((float)(vx + bw / 2), (float)hy, (float)(b.Lx + b.W * 0.62), (float)(hy + bw)));
                }
            }
            var mm = SKMatrix.Concat(SKMatrix.CreateTranslation((float)(W / 2), (float)(H / 2)), SKMatrix.CreateRotationDegrees((float)tilt));
            stencilPath.Transform(mm);
            env.Ctx.ClipPath(stencilPath, SKClipOperation.Intersect, true);
        }
        var bb = JLayouts.MainDraw(env, It());
        env.Ctx.Restore();
        if (env.Pass != "main")
        {
            env.Body = bb;
            return;
        }
        // 飞雾、流挂、喷涂中的雾点
        env.Ctx.Save();
        env.Ctx.Translate((float)(W / 2), (float)(H / 2));
        env.Ctx.RotateDegrees((float)tilt);
        env.Ctx.Translate((float)(-W / 2), (float)(-H / 2));
        using (var spray = new SKPaint { IsAntialias = true, Color = SkRgba(col, 0.5 * outA) })
        {
            for (var i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                for (var k = 0; k < 22; k++)
                {
                    var ang = J.R(s, i, k, 1) * J.TAU;
                    var rr = 0.5 + J.R(s, i, k, 2) * 0.35;
                    var x = W / 2 + b.Lx + Math.Cos(ang) * b.W * rr * 0.62;
                    var y = H / 2 + b.Ly + Math.Sin(ang) * b.H * rr * 0.62;
                    if ((dir > 0 && x > head) || (dir < 0 && x < head)) continue;
                    var r = size * (0.004 + J.R(s, i, k, 3) * 0.008);
                    env.Ctx.DrawCircle((float)x, (float)y, (float)r, spray);
                }
            }
        }
        using (var drip = new SKPaint { IsAntialias = true, Color = SkRgba(col, outA) })
        {
            var drips = I(Pm, "drips");
            for (var d = 0; d < drips && boxes.Count > 0; d++)
            {
                var b = boxes[(int)(J.H(s, d, 7) % (uint)boxes.Count)];
                var dx = W / 2 + b.Lx + J.Rs(s, d, 8) * b.W * 0.3;
                var top = H / 2 + b.Ly + b.H * 0.35;
                var t1 = 0.3 + d * 0.25 + (dir > 0 ? (dx - x0) / Math.Max(1, x1 - x0) : (x1 - dx) / Math.Max(1, x1 - x0)) * sw;
                var L = size * J.Rr(0.25, 0.55, s, d, 9) * J.E.OutCubic(J.Clamp((lt - t1) / 1.4));
                if (L <= 1) continue;
                var w0 = size * 0.035;
                using var pth = new SKPath();
                pth.MoveTo((float)(dx - w0 / 2), (float)top);
                pth.LineTo((float)(dx + w0 / 2), (float)top);
                pth.LineTo((float)(dx + w0 * 0.35), (float)(top + L));
                pth.LineTo((float)(dx - w0 * 0.35), (float)(top + L));
                pth.Close();
                env.Ctx.DrawPath(pth, drip);
                env.Ctx.DrawCircle((float)dx, (float)(top + L), (float)(w0 * 0.6), drip);
            }
        }
        env.Ctx.Restore();
        if (f > 0 && f < 1)
            for (var k = 0; k < 24; k++)
            {
                var yy = J.Lerp(y0, y1, J.R(s, k, (int)env.Step, 11));
                var xx = head + J.Rs(s, k, (int)env.Step, 12) * size * 0.25;
                env.Circle(xx, yy, size * (0.01 + J.R(s, k, 13) * 0.02), col, null, 0, 0.35 * outA, false);
            }
        env.Body = bb;
    }
}
