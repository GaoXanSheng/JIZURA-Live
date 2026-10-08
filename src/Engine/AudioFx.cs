// 移植自 frontend/src/engine/10b_audiofx.js
//
// 音频特效系统 v2：效果器架（多效果串联）+ 预设包 + 变速。
// WebAudio 的 AudioContext / OfflineAudioContext 双语境在这里统一为一个样本级 DSP 图：
// 全部单元都是 NAudio ISampleProvider（立体声 float 交错），实时播放与导出离线渲染
// 走同一条 Build → Read 代码路径，天然逐样本一致——JS 版靠种子随机（mulberry32）
// 保证 LFO / 混响 IR 在预览与导出一致，C# 版同构保留（J.Rng 已位级对拍）。
//
// 对位关系：
//   J.AUDIO_EQ_BANDS               → AudioFx.EqBands
//   J.AFX_UNITS[id]                → AudioFx.Units[id]（FxUnitDef：label/note/params schema/defaults/build）
//   J.AFX_PRESETS                  → AudioFx.Presets（FxPreset：id/label/cfg）
//   J.sanitizeAudioFx              → AudioFx.SanitizeAudioFx（含 v1 preset → v2 units 迁移，逐行对齐）
//   J.fxSpeed(project)             → AudioFx.FxSpeed(project)（不进 DSP 图：由播放/导出两端缩放时长，与 JS 一致）
//   J.buildAudioFx(ctx, cfg, opts) → AudioFx.Build(cfg, opts) → FxGraph?（units 全空 → null，不建图直通）
//
// WebAudio 节点 → 自实现 DSP 原语：
//   BiquadFilterNode       → AfxBiquad（WebAudio/RBJ 公式：lowpass/highpass/bandpass/peaking/lowshelf/highshelf）
//   DelayNode              → AfxDelay（环形缓冲 + 分数延迟线性插值，支持逐样本调制）
//   Oscillator→AudioParam  → 逐样本相位累加器（WebAudio triangle/sine 波形公式，相位 = phase0 + rate·(t+anchor)）
//   ConvolverNode          → AfxConvolver（2048 样本分块 FFT 重叠保留卷积；IR 由种子 20261004 程序化生成）
//   WaveShaperNode         → 257 点 tanh 曲线查表（线性插值，索引规则对齐 WebAudio）
//   DynamicsCompressorNode → 峰值包络 + 硬拐点增益计算机（近似实现，见 LimiterUnit）
//   StereoPannerNode       → 等功率声像（gL = cos(x·π/2), gR = sin(x·π/2)，x = (pan+1)/2）
//   PannerNode(HRTF)       → 简化立体声模型：pan = sin(az) 等功率声像 + 距离衰减 1/d（无 HRTF 频谱着色/ITD）
//
// 与 JS 的行为偏差（有意/不可避免）：
//   1. spatial/orbit 的 HRTF 换成等功率声像模型（浏览器 HRTF 是实测脉冲响应卷积，无位级可复现实现）
//   2. 混响湿声延迟一个块（2048 样本 ≈ 43ms@48k）生效——混响尾是扩散噪声，听感无差
//   3. limiter 的 DynamicsCompressor 为近似增益计算机（同参数下包络平滑细节不同）
//   4. vocalcut 的 WebAudio 图右耳只有 −R（merger 只接了反相右路）——疑似 JS 笔误，
//      按「逐行对齐 JS」原则原样保留

using System.Globalization;
using NAudio.Wave;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine;

public static class AudioFx
{
    // ---------------- 表（J.AUDIO_EQ_BANDS / J.AFX_UNITS / J.AFX_PRESETS） ----------------

    /// <summary>EQ 频点（Hz），7 段 peaking。</summary>
    public static readonly double[] EqBands = { 60, 150, 400, 1000, 2400, 6000, 15000 };

    /// <summary>程序化混响 IR 的固定种子（JS makeIR 默认值），预览与导出同 IR。</summary>
    public const uint IrSeed = 20261004;

    private static Dictionary<string, FxUnitDef>? _units;
    private static List<FxPreset>? _presets;

    /// <summary>效果单元注册表（J.AFX_UNITS）。schema 是 JSON 契约（MCP/设置页读取），键名与 JS 一致。</summary>
    public static IReadOnlyDictionary<string, FxUnitDef> Units { get { Install(); return _units!; } }

    /// <summary>预设包（J.AFX_PRESETS）：一键写整条链。</summary>
    public static IReadOnlyList<FxPreset> Presets { get { Install(); return _presets!; } }

    public static void Install()
    {
        if (_units != null) return;
        var u = new Dictionary<string, FxUnitDef>();

        u["eq"] = new FxUnitDef
        {
            Id = "eq", Label = "EQ 均衡", Note = "7 段 peaking，拖把手调音色",
            Params = new List<FxParamDef> { new() { Key = "gains", Type = "eq" } },
            Defaults = new JObject { ["gains"] = new JArray(0, 0, 0, 0, 0, 0, 0) },
            Build = (input, p, ctx) => new EqUnit(input, p, ctx),
        };
        u["pan"] = new FxUnitDef
        {
            Id = "pan", Label = "环绕音", Note = "立体声左右摆动",
            Params = new List<FxParamDef> { P("rate", "速度", 0.02, 2, 0.005, 0.125), P("depth", "强度", 0, 1, 0.01, 0.7) },
            Defaults = new JObject { ["rate"] = 0.125, ["depth"] = 0.7 },
            Build = (input, p, ctx) => new PanUnit(input, p, ctx),
        };
        u["spatial"] = new FxUnitDef
        {
            Id = "spatial", Label = "3D 丽音", Note = "左右声道 HRTF 定位（∓40°）做空间展宽，建议戴耳机",
            Params = new List<FxParamDef> { P("depth", "强度", 0, 1, 0.01, 0.7) },
            Defaults = new JObject { ["depth"] = 0.7 },
            Build = (input, p, ctx) => new SpatialUnit(input, p, ctx),
        };
        u["orbit"] = new FxUnitDef
        {
            Id = "orbit", Label = "8D 旋转", Note = "HRTF 声源绕听者转圈，建议戴耳机",
            Params = new List<FxParamDef> { P("rate", "速度", 0.02, 2, 0.005, 0.125), P("depth", "半径", 0, 1, 0.01, 0.7) },
            Defaults = new JObject { ["rate"] = 0.125, ["depth"] = 0.7 },
            Build = (input, p, ctx) => new OrbitUnit(input, p, ctx),
        };
        u["reverb"] = new FxUnitDef
        {
            Id = "reverb", Label = "混响", Note = "程序化房间脉冲响应（无外部文件），种子固定保证导出一致",
            Params = new List<FxParamDef>
            {
                P("size", "空间", 0.3, 6, 0.1, 2.2, "秒"),
                P("damp", "阻尼", 0, 1, 0.01, 0.5),
                P("wet", "湿度", 0, 1, 0.01, 0.25),
            },
            Defaults = new JObject { ["size"] = 2.2, ["damp"] = 0.5, ["wet"] = 0.25 },
            Build = (input, p, ctx) => new ReverbUnit(input, p, ctx),
        };
        u["echo"] = new FxUnitDef
        {
            Id = "echo", Label = "回声", Note = "延迟 + 反馈环，低通定回声音色",
            Params = new List<FxParamDef>
            {
                P("time", "延迟", 0.05, 1, 0.01, 0.28, "秒"),
                P("feedback", "反馈", 0, 0.85, 0.01, 0.35),
                P("tone", "音色", 500, 12000, 50, 4500, "Hz"),
                P("wet", "湿度", 0, 1, 0.01, 0.35),
            },
            Defaults = new JObject { ["time"] = 0.28, ["feedback"] = 0.35, ["tone"] = 4500, ["wet"] = 0.35 },
            Build = (input, p, ctx) => new EchoUnit(input, p, ctx),
        };
        u["vocalcut"] = new FxUnitDef
        {
            Id = "vocalcut", Label = "伴唱消除", Note = "中置声道抵消（居中人声衰减留伴奏），低频回补保住贝斯；对单声道音频无效",
            Params = new List<FxParamDef> { P("bass", "低频回补", 0, 1, 0.01, 0.6) },
            Defaults = new JObject { ["bass"] = 0.6 },
            Build = (input, p, ctx) => new VocalcutUnit(input, p, ctx),
        };
        u["chorus"] = new FxUnitDef
        {
            Id = "chorus", Label = "合唱", Note = "双路反相调制延迟，轻微音高晃动与立体声展宽",
            Params = new List<FxParamDef>
            {
                P("rate", "速度", 0.1, 5, 0.05, 1.2, "Hz"),
                P("depth", "深度", 0, 1, 0.01, 0.5),
                P("wet", "湿度", 0, 1, 0.01, 0.5),
            },
            Defaults = new JObject { ["rate"] = 1.2, ["depth"] = 0.5, ["wet"] = 0.5 },
            Build = (input, p, ctx) => new ChorusUnit(input, p, ctx),
        };
        u["bass"] = new FxUnitDef
        {
            Id = "bass", Label = "低音增强", Note = "低频架式提升",
            Params = new List<FxParamDef> { P("gain", "提升", 0, 12, 0.5, 4, "dB"), P("freq", "频点", 60, 250, 5, 120, "Hz") },
            Defaults = new JObject { ["gain"] = 4, ["freq"] = 120 },
            Build = (input, p, ctx) => new BassUnit(input, p, ctx),
        };
        u["telephone"] = new FxUnitDef
        {
            Id = "telephone", Label = "电话音", Note = "窄带通 + 轻度失真，老电台/话筒质感",
            Params = new List<FxParamDef> { P("drive", "失真", 0, 1, 0.01, 0.3) },
            Defaults = new JObject { ["drive"] = 0.3 },
            Build = (input, p, ctx) => new TelephoneUnit(input, p, ctx),
        };
        u["mono"] = new FxUnitDef
        {
            Id = "mono", Label = "单声道", Note = "左右合并；修相位问题/配人声消除用",
            Params = new List<FxParamDef>(),
            Defaults = new JObject(),
            Build = (input, p, ctx) => new MonoUnit(input, p, ctx),
        };
        u["pitch"] = new FxUnitDef
        {
            Id = "pitch", Label = "音调", Note = "整体移调（不改速度，实验性）；双延迟粒度式，极端值有轻微金属感",
            Params = new List<FxParamDef> { P("semitones", "移调", -12, 12, 1, 0, "半音", "int") },
            Defaults = new JObject { ["semitones"] = 0 },
            Build = (input, p, ctx) => new PitchUnit(input, p, ctx),
        };
        u["limiter"] = new FxUnitDef
        {
            Id = "limiter", Label = "限幅器", Note = "推动电平 + 硬限幅，链尾防削波",
            Params = new List<FxParamDef> { P("gain", "推动", 0, 12, 0.5, 0, "dB") },
            Defaults = new JObject { ["gain"] = 0 },
            Build = (input, p, ctx) => new LimiterUnit(input, p, ctx),
        };

        _units = u;

        // 预设包（J.AFX_PRESETS）：U(id, overrides) = defaults 合上覆盖项
        _presets = new List<FxPreset>
        {
            new("clean", "原声", Cfg(1)),
            new("hall", "音乐厅", Cfg(1, U("reverb", new JObject { ["size"] = 2.8, ["wet"] = 0.3 }))),
            new("room", "小房间", Cfg(1, U("reverb", new JObject { ["size"] = 0.9, ["wet"] = 0.18 }))),
            new("live", "现场演出", Cfg(1,
                U("echo", new JObject { ["time"] = 0.22, ["feedback"] = 0.25, ["wet"] = 0.25 }),
                U("reverb", new JObject { ["size"] = 1.8, ["wet"] = 0.22 }))),
            new("wide", "宽阔声场", Cfg(1, U("spatial"), U("chorus", new JObject { ["depth"] = 0.3, ["wet"] = 0.3 }))),
            new("8d", "8D 环绕", Cfg(1, U("orbit"))),
            new("karaoke", "伴唱模式", Cfg(1, U("vocalcut"), U("limiter", new JObject { ["gain"] = 3 }))),
            new("nightcore", "夜核加速", Cfg(1.25, U("bass", new JObject { ["gain"] = 3 }))),
            new("slow", "慢速抒情", Cfg(0.85, U("reverb", new JObject { ["size"] = 3.5, ["wet"] = 0.3 }))),
            new("radio", "复古电台", Cfg(1, U("telephone"), U("limiter", new JObject { ["gain"] = 6 }))),
        };
    }

    private static FxParamDef P(string key, string label, double min, double max, double step, double def,
        string? unit = null, string? type = null)
        => new() { Key = key, Label = label, Min = min, Max = max, Step = step, Def = def, Unit = unit, Type = type };

    /// <summary>
    /// U(id, overrides, on)：JS 预设包助手。Object.assign 语义：覆盖项原样拷入；
    /// 值为 null/缺省视作 JS 的 undefined → 移除该键（JSON.stringify 丢弃 undefined 后的落盘形态）。
    /// </summary>
    private static JObject U(string id, JObject? over = null, bool on = true)
    {
        var merged = (JObject)Units[id].Defaults.DeepClone();
        if (over != null)
            foreach (var kv in over)
            {
                if (kv.Value == null || kv.Value.Type == JTokenType.Null) merged.Remove(kv.Key);
                else merged[kv.Key] = (JToken)kv.Value.DeepClone();
            }
        return new JObject { ["id"] = id, ["on"] = on, ["params"] = merged };
    }

    private static JObject Cfg(double speed, params JObject[] units)
        => new() { ["speed"] = speed, ["units"] = new JArray(units) };

    /// <summary>单元 schema 的 JSON 形态（J.AFX_UNITS 序列化契约，键名对齐 JS 对象字面量）。</summary>
    public static JArray SchemaJson()
    {
        var a = new JArray();
        foreach (var d in Units.Values)
        {
            var ps = new JArray();
            foreach (var s in d.Params)
            {
                var o = new JObject { ["key"] = s.Key };
                if (s.Label != null) o["label"] = s.Label;
                if (s.Unit != null) o["unit"] = s.Unit;
                if (s.Type != null) o["type"] = s.Type;
                if (s.Type != "eq")
                {
                    o["min"] = s.Min;
                    o["max"] = s.Max;
                    o["step"] = s.Step;
                    o["def"] = s.Def;
                }
                ps.Add(o);
            }
            a.Add(new JObject
            {
                ["id"] = d.Id,
                ["label"] = d.Label,
                ["note"] = d.Note,
                ["defaults"] = d.Defaults.DeepClone(),
                ["params"] = ps,
            });
        }
        return a;
    }

    // ---------------- 配置清洗 + v1 旧版迁移（J.sanitizeAudioFx，逐行对齐） ----------------

    /// <summary>
    /// v2 模型：{speed, units:[{id,on,params}]}；v1 模型 {preset, rate, depth, eq}。
    /// 就地映射成等价链，老工程无缝升级。输出键名 speed/units/id/on/params/gains 与 JS 完全一致。
    /// </summary>
    public static JObject SanitizeAudioFx(JObject? fx)
    {
        Install();
        var o = fx ?? new JObject();
        var outO = new JObject
        {
            ["speed"] = AfxMath.Cmp(o["speed"], 0.5, 2, 1),
            ["units"] = new JArray(),
        };
        var units = (JArray)outO["units"]!;
        var seen = new HashSet<string>();

        if (o["units"] is JArray arr)
        {
            foreach (var tok in arr)
            {
                if (tok is not JObject u) continue;
                var id = u["id"]?.ToString();
                // 未知/重复单元丢弃
                if (id == null || !_units!.TryGetValue(id, out var def) || !seen.Add(id)) continue;
                var ps = new JObject();
                foreach (var schema in def.Params)
                {
                    if (schema.Type == "eq") ps["gains"] = ReadEq(u);
                    else ps[schema.Key] = AfxMath.Cmp(u["params"]?[schema.Key], schema.Min, schema.Max, schema.Def);
                }
                units.Add(new JObject
                {
                    ["id"] = id,
                    // on: u.on !== false
                    ["on"] = !(u["on"] is JValue bv && bv.Type == JTokenType.Boolean && bv.Value is false),
                    ["params"] = ps,
                });
            }
        }
        else if (o["preset"]?.ToString() is { } preset && preset != "none")
        {
            // v1 → v2：单预设搬进链（o.rate/o.depth 非数值时省键，对齐 JSON.stringify 丢 undefined）
            if (preset == "pan") units.Add(U("pan", NumOf(o, "rate", "depth")));
            if (preset == "spatial") units.Add(U("spatial", NumOf(o, "depth")));
            if (preset == "orbit") units.Add(U("orbit", NumOf(o, "rate", "depth")));
            if (preset == "eq")
                units.Add(new JObject { ["id"] = "eq", ["on"] = true, ["params"] = new JObject { ["gains"] = ReadEq(o) } });
        }
        return outO;
    }

    /// <summary>
    /// v1 迁移的覆盖项取值：键恒在；缺省/非数值原样保留（字符串等 JSON 化后仍在），
    /// 缺失记为 null（U() 里视作 undefined → 移除默认值，逐项对齐 Object.assign + JSON.stringify）。
    /// </summary>
    private static JObject NumOf(JObject o, params string[] keys)
    {
        var r = new JObject();
        foreach (var k in keys)
        {
            var t = o[k];
            r[k] = t == null || t.Type == JTokenType.Null ? JValue.CreateNull() : (JToken)t.DeepClone();
        }
        return r;
    }

    /// <summary>
    /// EQ 增益三种来源：v2 单元 params.gains / 历史数组形态 params / v1 的 eq 字段；
    /// 逐段 clamp 到 ±12，缺省 0（返回固定 7 段新数组，对齐 JS map 行为）。
    /// </summary>
    private static JArray ReadEq(JObject raw)
    {
        JArray? arr = raw["params"] is JArray pa ? pa
            : raw["params"]?["gains"] is JArray ga ? ga
            : raw["eq"] is JArray ea ? ea
            : null;
        var outA = new JArray();
        for (var i = 0; i < EqBands.Length; i++)
        {
            double v = arr != null && i < arr.Count ? AfxMath.Num(arr[i], double.NaN) : double.NaN;
            outA.Add(AfxMath.Cmp(v, -12, 12, 0));
        }
        return outA;
    }

    // ---------------- 变速倍率（J.fxSpeed；不进 DSP 图） ----------------

    /// <summary>播放/导出两端的时长缩放（歌词/节拍仍按歌曲原时刻）。</summary>
    public static double FxSpeed(Project? project)
    {
        var fx = project?.AudioFx;
        return AfxMath.Cmp(AfxMath.Num(fx?["speed"], double.NaN), 0.5, 2, 1);
    }

    /// <summary>直接对 audioFx 配置取倍率。</summary>
    public static double FxSpeedOf(JObject? fx)
        => AfxMath.Cmp(AfxMath.Num(fx?["speed"], double.NaN), 0.5, 2, 1);

    // ---------------- 链式构建：预览与导出共用（J.buildAudioFx） ----------------

    /// <summary>构建特效 DSP 图。与 JS 同语义：空链返回 null（不建图直通）；on:false 的单元跳过但图仍在。</summary>
    public static FxGraph? Build(JObject? cfg, FxBuildOpts? opts = null)
    {
        var fx = SanitizeAudioFx(cfg);
        if (fx["units"] is not JArray units || units.Count == 0) return null;
        var ctx = new FxRenderContext(opts?.SampleRate ?? 44100, opts?.AnchorT ?? 0);
        var inlet = new FxInlet(ctx);
        ISampleProvider node = inlet;
        foreach (var tok in units)
        {
            if (tok is not JObject u) continue;
            if (u["on"] is JValue bv && bv.Type == JTokenType.Boolean && bv.Value is false) continue;
            var id = u["id"]?.ToString();
            if (id == null || !Units.TryGetValue(id, out var def)) continue;
            node = def.Build(node, u["params"] as JObject ?? new JObject(), ctx);
        }
        return new FxGraph(inlet, node);
    }

    // ---------------- 单元实现 ----------------

    /// <summary>立体声单元基类：从上游读（float 交错 stereo），逐帧就地变换。</summary>
    private abstract class UnitBase : ISampleProvider
    {
        protected readonly ISampleProvider In;
        protected readonly int Sr;
        protected readonly double Anchor;

        protected UnitBase(ISampleProvider input, FxRenderContext ctx)
        {
            In = input;
            Sr = ctx.SampleRate;
            Anchor = ctx.AnchorT;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(ctx.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public abstract int Read(Span<float> buffer);
    }

    /// <summary>eq：7 段 peaking（Q=1，±12dB），串联。</summary>
    private sealed class EqUnit : UnitBase
    {
        private readonly AfxBiquad[] _f = new AfxBiquad[EqBands.Length];

        public EqUnit(ISampleProvider input, JObject p, FxRenderContext ctx) : base(input, ctx)
        {
            for (var i = 0; i < EqBands.Length; i++)
                _f[i] = AfxBiquad.Design(AfxBiquad.Type.Peaking, Sr, EqBands[i], 1,
                    AfxMath.Cmp(p["gains"]?[i], -12, 12, 0));
        }

        public override int Read(Span<float> buffer)
        {
            var got = In.Read(buffer);
            var f = _f;
            for (var i = 0; i < got; i += 2)
            {
                double l = buffer[i], r = buffer[i + 1];
                for (var k = 0; k < f.Length; k++)
                {
                    l = f[k].Process((float)l, 0);
                    r = f[k].Process((float)r, 1);
                }
                buffer[i] = (float)l;
                buffer[i + 1] = (float)r;
            }
            return got;
        }
    }

    /// <summary>pan：等功率声像 + 三角 LFO（WebAudio StereoPanner + 振荡器接 pan 参数）。</summary>
    private sealed class PanUnit : UnitBase
    {
        private readonly double _rate, _depth;
        private double _phase;

        public PanUnit(ISampleProvider input, JObject p, FxRenderContext ctx) : base(input, ctx)
        {
            _rate = AfxMath.Cmp(p["rate"], 0.01, 20, 0.5);
            _depth = AfxMath.Cmp(p["depth"], 0, 1, 0.7);
            _phase = AfxMath.Frac(_rate * Anchor);
        }

        public override int Read(Span<float> buffer)
        {
            var got = In.Read(buffer);
            var dph = _rate / Sr;
            var ph = _phase;
            for (var i = 0; i < got; i += 2)
            {
                var pan = _depth * AfxMath.Tri(ph);
                var x = (pan + 1) / 2;
                var gL = Math.Cos(x * (Math.PI / 2));
                var gR = Math.Sin(x * (Math.PI / 2));
                buffer[i] = (float)(buffer[i] * gL);
                buffer[i + 1] = (float)(buffer[i + 1] * gR);
                ph = AfxMath.Frac(ph + dph);
            }
            _phase = ph;
            return got;
        }
    }

    /// <summary>
    /// spatial：左右声道各自定位 ∓40°（半径 1.5+depth），双路声像和为输出。
    /// HRTF → 简化模型：pan = sin(az) 等功率 + 距离衰减 1/d（WebAudio inverse 距离模型，refDistance=1）。
    /// </summary>
    private sealed class SpatialUnit : UnitBase
    {
        private readonly double _g, _gl0, _gr0, _gl1, _gr1;

        public SpatialUnit(ISampleProvider input, JObject p, FxRenderContext ctx) : base(input, ctx)
        {
            var r = 1.5 + AfxMath.Cmp(p["depth"], 0, 1, 0.7);
            _g = 1 / r;
            (_gl0, _gr0) = PanGains(-40 * Math.PI / 180);
            (_gl1, _gr1) = PanGains(40 * Math.PI / 180);
        }

        private static (double Gl, double Gr) PanGains(double az)
        {
            var x = (Math.Sin(az) + 1) / 2;
            return (Math.Cos(x * (Math.PI / 2)), Math.Sin(x * (Math.PI / 2)));
        }

        public override int Read(Span<float> buffer)
        {
            var got = In.Read(buffer);
            for (var i = 0; i < got; i += 2)
            {
                double l = buffer[i], r = buffer[i + 1];
                buffer[i] = (float)(_g * (_gl0 * l + _gl1 * r));
                buffer[i + 1] = (float)(_g * (_gr0 * l + _gr1 * r));
            }
            return got;
        }
    }

    /// <summary>
    /// orbit：单声道化（WebAudio 立体声入 Panner 会下混 0.5(L+R)）后绕听者旋转。
    /// JS 用两只正弦振荡器驱动 positionX/Z（z 相位超前 90°）→ x=r·sinθ, z=r·cosθ，
    /// 方位角 az = atan2(x, −z)（听者面向 −z），HRTF → pan = sin(az) 等功率 + 距离衰减 1/d。
    /// </summary>
    private sealed class OrbitUnit : UnitBase
    {
        private readonly double _r, _rate, _g;
        private long _n;

        public OrbitUnit(ISampleProvider input, JObject p, FxRenderContext ctx) : base(input, ctx)
        {
            _r = 1.2 + AfxMath.Cmp(p["depth"], 0, 1, 0.7) * 1.8;
            _rate = AfxMath.Cmp(p["rate"], 0.02, 2, 0.125);
            _g = 1 / _r;
        }

        public override int Read(Span<float> buffer)
        {
            var got = In.Read(buffer);
            var th0 = J.TAU * _rate * Anchor;
            var dth = J.TAU * _rate / Sr;
            var r = _r;
            for (var i = 0; i < got; i += 2)
            {
                var th = th0 + dth * _n++;
                var x = r * Math.Sin(th);
                var z = r * Math.Cos(th);
                var az = Math.Atan2(x, -z);
                var pan = Math.Sin(az);
                var gL = Math.Cos((pan + 1) * (Math.PI / 4));
                var gR = Math.Sin((pan + 1) * (Math.PI / 4));
                var m = 0.5 * (buffer[i] + buffer[i + 1]);
                buffer[i] = (float)(_g * m * gL);
                buffer[i + 1] = (float)(_g * m * gR);
            }
            return got;
        }
    }

    /// <summary>
    /// reverb：程序化 IR（种子固定）卷积 + 低通定湿声音色，干声直通。
    /// JS 图：input→dry(1)→output；input→conv→tone(lowpass)→wet→output。
    /// 卷积用 2048 样本分块 FFT（重叠保留），湿声带一个块的固有延迟（听感无差，预览/导出一致）。
    /// </summary>
    private sealed class ReverbUnit : UnitBase
    {
        private readonly AfxConvolver _convL, _convR;
        private readonly AfxBiquad _tone;
        private readonly double _wet;
        private readonly float[] _blkL, _blkR, _tmpL, _tmpR, _ringL, _ringR;
        private int _fill, _cnt, _head;

        public ReverbUnit(ISampleProvider input, JObject p, FxRenderContext ctx) : base(input, ctx)
        {
            var size = AfxMath.Cmp(p["size"], 0.3, 6, 2.2);
            var damp = AfxMath.Cmp(p["damp"], 0, 1, 0.5);
            _wet = AfxMath.Cmp(p["wet"], 0, 1, 0.25);
            var ir = AfxIr.Make(Sr, size, damp);
            _convL = new AfxConvolver(ir[0]);
            _convR = new AfxConvolver(ir[1]);
            // 湿声低通：800 + 10000·(1−damp)，Q 默认 1（JS 未设 Q）
            var toneF = 800 + 10000 * (1 - damp);
            _tone = AfxBiquad.Design(AfxBiquad.Type.Lowpass, Sr, toneF, 1);
            _blkL = new float[AfxConvolver.Block];
            _blkR = new float[AfxConvolver.Block];
            _tmpL = new float[AfxConvolver.Block];
            _tmpR = new float[AfxConvolver.Block];
            _ringL = new float[AfxConvolver.Block];
            _ringR = new float[AfxConvolver.Block];
        }

        public override int Read(Span<float> buffer)
        {
            var got = In.Read(buffer);
            for (var i = 0; i < got; i += 2)
            {
                _blkL[_fill] = buffer[i];
                _blkR[_fill] = buffer[i + 1];
                if (++_fill == AfxConvolver.Block)
                {
                    _convL.Process(_blkL, _tmpL);
                    _convR.Process(_blkR, _tmpR);
                    for (var j = 0; j < AfxConvolver.Block; j++)
                    {
                        _ringL[(_head + _cnt) % AfxConvolver.Block] = (float)(_wet * _tone.Process(_tmpL[j], 0));
                        _ringR[(_head + _cnt) % AfxConvolver.Block] = (float)(_wet * _tone.Process(_tmpR[j], 1));
                        _cnt++;
                    }
                    _fill = 0;
                }
                // 干声直通 + 湿声（就绪前补 0，即一个块的启动延迟）
                if (_cnt > 0)
                {
                    buffer[i] += _ringL[_head];
                    buffer[i + 1] += _ringR[_head];
                    _head = (_head + 1) % AfxConvolver.Block;
                    _cnt--;
                }
            }
            return got;
        }
    }

    /// <summary>
    /// echo：延迟 + 反馈环（delay→tone→fb→delay），tone 后引出湿声，干声直通。
    /// JS 图：input→output；input→delay；delay→tone→fb→delay；tone→wet→output。
    /// </summary>
    private sealed class EchoUnit : UnitBase
    {
        private readonly AfxDelay _delay;
        private readonly AfxBiquad _tone;
        private readonly double _fb, _wet, _d;

        public EchoUnit(ISampleProvider input, JObject p, FxRenderContext ctx) : base(input, ctx)
        {
            _d = AfxMath.Cmp(p["time"], 0.05, 1, 0.28) * Sr;
            _fb = AfxMath.Cmp(p["feedback"], 0, 0.85, 0.35);
            _wet = AfxMath.Cmp(p["wet"], 0, 1, 0.35);
            _delay = new AfxDelay(2, Sr); // JS createDelay(2)
            _tone = AfxBiquad.Design(AfxBiquad.Type.Lowpass, Sr, AfxMath.Cmp(p["tone"], 500, 12000, 4500), 1);
        }

        public override int Read(Span<float> buffer)
        {
            var got = In.Read(buffer);
            for (var i = 0; i < got; i += 2)
            {
                var (dl, dr) = _delay.Read(_d);
                var fl = _tone.Process(dl, 0);
                var fr = _tone.Process(dr, 1);
                // 反馈环：input + fb·tone(delay) 回灌 delay；tone 后引出湿声（同 JS 接线）
                _delay.Write(buffer[i] + (float)(_fb * fl), buffer[i + 1] + (float)(_fb * fr));
                buffer[i] += (float)(_wet * fl);
                buffer[i + 1] += (float)(_wet * fr);
            }
            return got;
        }
    }

    /// <summary>
    /// vocalcut：L−R 相消（右路反相后并入）+ 原信号 180Hz 低通按比例回补。
    /// 按 JS 现状逐行对齐：merger 只有三根线，右耳 = −R（非对称，疑似源码笔误，行为保留）。
    /// </summary>
    private sealed class VocalcutUnit : UnitBase
    {
        private readonly AfxBiquad _bass;
        private readonly double _bl;

        public VocalcutUnit(ISampleProvider input, JObject p, FxRenderContext ctx) : base(input, ctx)
        {
            _bl = AfxMath.Cmp(p["bass"], 0, 1, 0.6);
            _bass = AfxBiquad.Design(AfxBiquad.Type.Lowpass, Sr, 180, 1);
        }

        public override int Read(Span<float> buffer)
        {
            var got = In.Read(buffer);
            for (var i = 0; i < got; i += 2)
            {
                double l = buffer[i], r = buffer[i + 1];
                double ol = l - r, or_ = -r;
                if (_bl > 0)
                {
                    ol += _bl * _bass.Process((float)l, 0);
                    or_ += _bl * _bass.Process((float)r, 1);
                }
                buffer[i] = (float)ol;
                buffer[i + 1] = (float)or_;
            }
            return got;
        }
    }

    /// <summary>chorus：双路调制延迟（相位差半周期）反相声像（∓0.6），干声直通。</summary>
    private sealed class ChorusUnit : UnitBase
    {
        private readonly AfxDelay _d0, _d1;
        private readonly double _rate, _md, _wet, _base;
        private double _ph0, _ph1;
        private readonly double _g0l, _g0r, _g1l, _g1r;

        public ChorusUnit(ISampleProvider input, JObject p, FxRenderContext ctx) : base(input, ctx)
        {
            _rate = AfxMath.Cmp(p["rate"], 0.01, 20, 0.5);
            _md = 0.0005 + 0.004 * AfxMath.Cmp(p["depth"], 0, 1, 0.5);
            _wet = AfxMath.Cmp(p["wet"], 0, 1, 0.5);
            _base = 0.015;
            _d0 = new AfxDelay(0.05, Sr);
            _d1 = new AfxDelay(0.05, Sr);
            _ph0 = AfxMath.Frac(_rate * Anchor);
            _ph1 = AfxMath.Frac(0.5 + _rate * Anchor);
            // StereoPanner pan=∓0.6 → x=(pan+1)/2 的等功率增益
            (_g0l, _g0r) = PanGains(-0.6);
            (_g1l, _g1r) = PanGains(0.6);
        }

        private static (double Gl, double Gr) PanGains(double pan)
        {
            var x = (pan + 1) / 2;
            return (Math.Cos(x * (Math.PI / 2)), Math.Sin(x * (Math.PI / 2)));
        }

        public override int Read(Span<float> buffer)
        {
            var got = In.Read(buffer);
            var dph = _rate / Sr;
            var baseS = _base * Sr;
            var mdS = _md * Sr;
            for (var i = 0; i < got; i += 2)
            {
                var l = buffer[i];
                var r = buffer[i + 1];
                var (d0l, d0r) = _d0.Read(baseS + mdS * AfxMath.Tri(_ph0));
                _d0.Write(l, r);
                var (d1l, d1r) = _d1.Read(baseS + mdS * AfxMath.Tri(_ph1));
                _d1.Write(l, r);
                _ph0 = AfxMath.Frac(_ph0 + dph);
                _ph1 = AfxMath.Frac(_ph1 + dph);
                buffer[i] = (float)(l + _wet * (_g0l * d0l + _g1l * d1l));
                buffer[i + 1] = (float)(r + _wet * (_g0r * d0r + _g1r * d1r));
            }
            return got;
        }
    }

    /// <summary>bass：lowshelf 架式提升。</summary>
    private sealed class BassUnit : UnitBase
    {
        private readonly AfxBiquad _f;

        public BassUnit(ISampleProvider input, JObject p, FxRenderContext ctx) : base(input, ctx)
        {
            _f = AfxBiquad.Design(AfxBiquad.Type.Lowshelf, Sr,
                AfxMath.Cmp(p["freq"], 60, 250, 120), 1,
                AfxMath.Cmp(p["gain"], 0, 12, 4));
        }

        public override int Read(Span<float> buffer)
        {
            var got = In.Read(buffer);
            for (var i = 0; i < got; i += 2)
            {
                buffer[i] = _f.Process(buffer[i], 0);
                buffer[i + 1] = _f.Process(buffer[i + 1], 1);
            }
            return got;
        }
    }

    /// <summary>telephone：hp(400)→bp(1500,Q0.8)→tanh WaveShaper→lp(3200)。</summary>
    private sealed class TelephoneUnit : UnitBase
    {
        private readonly AfxBiquad _hp, _bp, _lp;
        private readonly float[] _curve = new float[257];

        public TelephoneUnit(ISampleProvider input, JObject p, FxRenderContext ctx) : base(input, ctx)
        {
            _hp = AfxBiquad.Design(AfxBiquad.Type.Highpass, Sr, 400, 1);
            _bp = AfxBiquad.Design(AfxBiquad.Type.Bandpass, Sr, 1500, 0.8);
            _lp = AfxBiquad.Design(AfxBiquad.Type.Lowpass, Sr, 3200, 1);
            var k = 1 + AfxMath.Cmp(p["drive"], 0, 1, 0.3) * 24;
            var nk = Math.Tanh(k);
            for (var i = 0; i < 257; i++)
            {
                var x = i / 128.0 - 1;
                _curve[i] = (float)(Math.Tanh(k * x) / nk);
            }
        }

        public override int Read(Span<float> buffer)
        {
            var got = In.Read(buffer);
            for (var i = 0; i < got; i += 2)
            {
                buffer[i] = _lp.Process(Shape(_bp.Process(_hp.Process(buffer[i], 0), 0)), 0);
                buffer[i + 1] = _lp.Process(Shape(_bp.Process(_hp.Process(buffer[i + 1], 1), 1)), 1);
            }
            return got;
        }

        private float Shape(float x)
        {
            // WebAudio WaveShaper：v∈[−1,1] 映射到 0..256 线性插值，越界取端点
            var idx = (x + 1) * 128;
            if (idx < 0) return _curve[0];
            if (idx >= 256) return _curve[256];
            var i0 = (int)idx;
            var f = idx - i0;
            return _curve[i0] + (_curve[i0 + 1] - _curve[i0]) * (float)f;
        }
    }

    /// <summary>mono：左右合并 0.5(L+R)（WebAudio 立体声→单声道下混增益， merger 双入各得一份）。</summary>
    private sealed class MonoUnit : UnitBase
    {
        public MonoUnit(ISampleProvider input, JObject p, FxRenderContext ctx) : base(input, ctx) { }

        public override int Read(Span<float> buffer)
        {
            var got = In.Read(buffer);
            for (var i = 0; i < got; i += 2)
            {
                var m = 0.5f * (buffer[i] + buffer[i + 1]);
                buffer[i] = m;
                buffer[i + 1] = m;
            }
            return got;
        }
    }

    /// <summary>
    /// pitch：双路延迟粒度式移调（JS 经典 WebAudio 技法）。延迟时间以 base=0.06s 为中心、
    /// 深度 base·(1−ratio) 的三角波（4Hz，两路差半周期）往返 → 读头相对写头变速 = 重采样。
    /// 干声直通保底，两路各 0.5、湿总 0.5。semitones=0 时直通不建分支（对齐 JS）。
    /// </summary>
    private sealed class PitchUnit : UnitBase
    {
        private readonly AfxDelay? _d0, _d1;
        private readonly double _md, _baseS;
        private double _ph0, _ph1;
        private const double Rate = 4;

        public PitchUnit(ISampleProvider input, JObject p, FxRenderContext ctx) : base(input, ctx)
        {
            var n = (int)Math.Round(AfxMath.Cmp(p["semitones"], -12, 12, 0));
            if (n == 0) return; // JS：input.connect(output) 直通
            var ratio = Math.Pow(2, -n / 12.0); // 延迟读速比 = 音高比
            _md = 0.06 * (1 - ratio);
            _baseS = 0.06 * Sr;
            _d0 = new AfxDelay(0.5, Sr);
            _d1 = new AfxDelay(0.5, Sr);
            _ph0 = AfxMath.Frac(Rate * Anchor);
            _ph1 = AfxMath.Frac(0.5 + Rate * Anchor);
        }

        public override int Read(Span<float> buffer)
        {
            var got = In.Read(buffer);
            if (_d0 == null || _d1 == null) return got; // 0 半音直通
            var dph = Rate / Sr;
            for (var i = 0; i < got; i += 2)
            {
                var l = buffer[i];
                var r = buffer[i + 1];
                var t0 = AfxMath.Tri(_ph0);
                var t1 = AfxMath.Tri(_ph1);
                _ph0 = AfxMath.Frac(_ph0 + dph);
                _ph1 = AfxMath.Frac(_ph1 + dph);
                var (d0l, d0r) = _d0.Read(_baseS + _md * Sr * t0);
                _d0.Write(l, r);
                var (d1l, d1r) = _d1.Read(_baseS + _md * Sr * t1);
                _d1.Write(l, r);
                buffer[i] = (float)(l + 0.5 * (0.5 * d0l + 0.5 * d1l));
                buffer[i + 1] = (float)(r + 0.5 * (0.5 * d0r + 0.5 * d1r));
            }
            return got;
        }
    }

    /// <summary>
    /// limiter：推动电平（10^(gain/20)）+ 硬限幅（threshold −1dB、knee 0、ratio 20、
    /// attack 3ms、release 120ms）。WebAudio DynamicsCompressorNode 无公开位级可复现实现，
    /// 这里用峰值包络（立体声联动，取 max(|L|,|R|)）+ 硬拐点增益计算机近似，包络平滑细节有差。
    /// </summary>
    private sealed class LimiterUnit : UnitBase
    {
        private readonly double _inGain, _atk, _rel;
        private double _env, _gainDb;

        public LimiterUnit(ISampleProvider input, JObject p, FxRenderContext ctx) : base(input, ctx)
        {
            _inGain = Math.Pow(10, AfxMath.Cmp(p["gain"], 0, 12, 0) / 20);
            _atk = Math.Exp(-1 / (Sr * 0.003));
            _rel = Math.Exp(-1 / (Sr * 0.12));
        }

        public override int Read(Span<float> buffer)
        {
            var got = In.Read(buffer);
            for (var i = 0; i < got; i += 2)
            {
                double xl = _inGain * buffer[i], xr = _inGain * buffer[i + 1];
                var a = Math.Max(Math.Abs(xl), Math.Abs(xr));
                var c1 = a > _env ? _atk : _rel;
                _env = c1 * _env + (1 - c1) * a;
                var envDb = 20 * Math.Log10(Math.Max(_env, 1e-6));
                var over = envDb - (-1);
                var targetDb = over > 0 ? -over * (1 - 1.0 / 20) : 0;
                var c2 = targetDb < _gainDb ? _atk : _rel;
                _gainDb = c2 * _gainDb + (1 - c2) * targetDb;
                var g = Math.Pow(10, _gainDb / 20);
                buffer[i] = (float)(xl * g);
                buffer[i + 1] = (float)(xr * g);
            }
            return got;
        }
    }
}

// ---------------- 图的接线件 ----------------

/// <summary>单元 schema（自描述，UI 自动出滑杆 / MCP 读取）。字段名对齐 JS：label/note/params/defaults。</summary>
public sealed class FxUnitDef
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Note { get; init; } = "";
    public IReadOnlyList<FxParamDef> Params { get; init; } = new List<FxParamDef>();
    public JObject Defaults { get; init; } = new();

    /// <summary>子图构建器：把上游接进本单元，返回单元输出。实时/离线同一路径。</summary>
    public required FxUnitBuilder Build { get; init; }
}

public sealed class FxParamDef
{
    public string Key { get; init; } = "";

    /// <summary>"eq" = 7 段增益数组；"int" = 整数步进；null = 普通浮点滑杆。</summary>
    public string? Type { get; init; }
    public string? Label { get; init; }
    public string? Unit { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; }
    public double Def { get; init; }
}

public delegate ISampleProvider FxUnitBuilder(ISampleProvider input, JObject p, FxRenderContext ctx);

/// <summary>预设包条目（J.AFX_PRESETS）：{id, label, cfg:{speed, units:[…]}}。</summary>
public sealed record FxPreset(string Id, string Label, JObject Cfg);

/// <summary>渲染语境：采样率 + LFO 锚点（JS 的 ctx.sampleRate 与 opts.anchorT）。</summary>
public sealed class FxRenderContext
{
    public FxRenderContext(int sampleRate, double anchorT = 0)
    {
        SampleRate = sampleRate;
        AnchorT = anchorT;
    }

    public int SampleRate { get; }

    /// <summary>LFO 锚点（秒）：预览可传当前时钟对齐墙钟；导出缺省 0（对齐 11_export 不传 anchorT）。</summary>
    public double AnchorT { get; }
}

/// <summary>Build 选项（JS opts 中影响建图的部分；songDur/fromT/toT 只影响外层时长，不进图）。</summary>
public sealed class FxBuildOpts
{
    public int SampleRate { get; init; } = 44100;

    /// <summary>JS opts.anchorT：LFO 起始锚点（秒）。</summary>
    public double AnchorT { get; init; }
}

/// <summary>buildAudioFx 返回 {input, output, channels} 的等价物：调用方把音源接到 Input.Source，从 Output 读。</summary>
public sealed class FxGraph
{
    internal FxGraph(FxInlet input, ISampleProvider output)
    {
        Input = input;
        Output = output;
    }

    public FxInlet Input { get; }
    public ISampleProvider Output { get; }
    public int Channels => 2;
}

/// <summary>图入口（JS 的 input GainNode）：接线点 + 声道归一（mono→stereo 拷贝，2ch 直通）。</summary>
public sealed class FxInlet : ISampleProvider
{
    private readonly WaveFormat _fmt;
    private float[]? _scratch;

    internal FxInlet(FxRenderContext ctx)
    {
        _fmt = WaveFormat.CreateIeeeFloatWaveFormat(ctx.SampleRate, 2);
    }

    /// <summary>音源（如解码后的立体声 float 流）。采样率须与图一致，调用方先重采样。</summary>
    public ISampleProvider? Source { get; set; }

    public WaveFormat WaveFormat => _fmt;

    public int Read(Span<float> buffer)
    {
        var src = Source ?? throw new InvalidOperationException("AudioFx 图未接线：先设置 FxGraph.Input.Source");
        if (src.WaveFormat.SampleRate != _fmt.SampleRate)
            throw new InvalidOperationException($"AudioFx 音源采样率 {src.WaveFormat.SampleRate} 与图采样率 {_fmt.SampleRate} 不一致（调用方需先重采样）");
        if (src.WaveFormat.Channels == 2) return src.Read(buffer);
        if (src.WaveFormat.Channels == 1)
        {
            // WebAudio mono→stereo 上混：L=R=输入（全增益拷贝）
            _scratch ??= new float[4096];
            var frames = buffer.Length / 2;
            var done = 0;
            while (done < frames)
            {
                var want = Math.Min(frames - done, _scratch.Length);
                var got = src.Read(_scratch.AsSpan(0, want));
                if (got <= 0) break;
                for (var i = 0; i < got; i++)
                {
                    buffer[(done + i) * 2] = _scratch[i];
                    buffer[(done + i) * 2 + 1] = _scratch[i];
                }
                done += got;
            }
            return done * 2;
        }
        throw new InvalidOperationException($"AudioFx 音源声道数 {src.WaveFormat.Channels} 不支持（仅 1/2）");
    }
}

// ---------------- DSP 原语（internal，预览/导出共用，可被测试工程引用） ----------------

/// <summary>JS clamp(v, a, b, d)：非有限数值回落默认值。</summary>
internal static class AfxMath
{
    public static double Cmp(double v, double a, double b, double d)
        => double.IsFinite(v) ? Math.Min(b, Math.Max(a, v)) : d;

    public static double Cmp(JToken? t, double a, double b, double d) => Cmp(Num(t, double.NaN), a, b, d);

    public static double Num(JToken? t, double d)
        => t is JValue v && v.Type is JTokenType.Integer or JTokenType.Float
            ? Convert.ToDouble(v.Value, CultureInfo.InvariantCulture)
            : d;

    public static double Frac(double x)
    {
        x %= 1;
        return x < 0 ? x + 1 : x;
    }

    /// <summary>WebAudio triangle 振荡器波形（ph ∈ [0,1)：0→+1→0→−1→0）。</summary>
    public static double Tri(double ph)
    {
        if (ph < 0.25) return 4 * ph;
        return ph < 0.75 ? 2 - 4 * ph : 4 * ph - 4;
    }
}

/// <summary>
/// 双二阶滤波器（WebAudio BiquadFilterNode 公式，RBJ cookbook；直接 I 型，双通道独立状态）。
/// NAudio 无内置通用 biquad，自实现。
/// </summary>
internal sealed class AfxBiquad
{
    public enum Type { Lowpass, Highpass, Bandpass, Peaking, Lowshelf, Highshelf }

    private double _b0, _b1, _b2, _a1, _a2;
    private readonly double[] _x1 = new double[2], _x2 = new double[2], _y1 = new double[2], _y2 = new double[2];

    public static AfxBiquad Design(Type type, double fs, double freq, double q, double gainDb = 0)
    {
        var f = new AfxBiquad();
        // 频点夹进 (0, nyquist) 防奇异（WebAudio 会 clamp 到奈奎斯特）
        freq = Math.Min(Math.Max(freq, 1e-4), fs * 0.49999);
        var w0 = 2 * Math.PI * freq / fs;
        var cw = Math.Cos(w0);
        var sw = Math.Sin(w0);
        var alpha = sw / (2 * q);
        double b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case Type.Lowpass:
                b0 = (1 - cw) / 2;
                b1 = 1 - cw;
                b2 = (1 - cw) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cw;
                a2 = 1 - alpha;
                break;
            case Type.Highpass:
                b0 = (1 + cw) / 2;
                b1 = -(1 + cw);
                b2 = (1 + cw) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cw;
                a2 = 1 - alpha;
                break;
            case Type.Bandpass: // 0dB 峰值增益变体（WebAudio 同款）
                b0 = alpha;
                b1 = 0;
                b2 = -alpha;
                a0 = 1 + alpha;
                a1 = -2 * cw;
                a2 = 1 - alpha;
                break;
            case Type.Peaking:
            {
                var a = Math.Pow(10, gainDb / 40);
                b0 = 1 + alpha * a;
                b1 = -2 * cw;
                b2 = 1 - alpha * a;
                a0 = 1 + alpha / a;
                a1 = -2 * cw;
                a2 = 1 - alpha / a;
                break;
            }
            case Type.Lowshelf:
            {
                var a = Math.Pow(10, gainDb / 40);
                var sq = sw / 2 * Math.Sqrt(2); // S=1 的 shelf slope 项
                var tsa = 2 * Math.Sqrt(a) * sq;
                b0 = a * ((a + 1) - (a - 1) * cw + tsa);
                b1 = 2 * a * ((a - 1) - (a + 1) * cw);
                b2 = a * ((a + 1) - (a - 1) * cw - tsa);
                a0 = (a + 1) + (a - 1) * cw + tsa;
                a1 = -2 * ((a - 1) + (a + 1) * cw);
                a2 = (a + 1) + (a - 1) * cw - tsa;
                break;
            }
            default: // Highshelf
            {
                var a = Math.Pow(10, gainDb / 40);
                var sq = sw / 2 * Math.Sqrt(2);
                var tsa = 2 * Math.Sqrt(a) * sq;
                b0 = a * ((a + 1) + (a - 1) * cw + tsa);
                b1 = -2 * a * ((a - 1) + (a + 1) * cw);
                b2 = a * ((a + 1) + (a - 1) * cw - tsa);
                a0 = (a + 1) - (a - 1) * cw + tsa;
                a1 = 2 * ((a - 1) - (a + 1) * cw);
                a2 = (a + 1) - (a - 1) * cw - tsa;
                break;
            }
        }
        f._b0 = b0 / a0;
        f._b1 = b1 / a0;
        f._b2 = b2 / a0;
        f._a1 = a1 / a0;
        f._a2 = a2 / a0;
        return f;
    }

    /// <summary>单样本推进；ch 为声道（0/1），状态独立。</summary>
    public float Process(float x, int ch)
    {
        var c = ch & 1;
        var y = _b0 * x + _b1 * _x1[c] + _b2 * _x2[c] - _a1 * _y1[c] - _a2 * _y2[c];
        _x2[c] = _x1[c];
        _x1[c] = x;
        _y2[c] = _y1[c];
        _y1[c] = y;
        return (float)y;
    }
}

/// <summary>立体声延迟线（WebAudio DelayNode）：环形缓冲 + 分数延迟线性插值，延迟可逐样本调制。</summary>
internal sealed class AfxDelay
{
    private readonly float[] _l, _r;
    private int _w;

    public AfxDelay(double maxSeconds, int sampleRate)
    {
        var len = Math.Max(8, (int)Math.Ceiling(maxSeconds * sampleRate) + 4);
        _l = new float[len];
        _r = new float[len];
    }

    /// <summary>读「delaySamples 之前」写入的样本（线性插值；越界夹到 0..len−3）。</summary>
    public (float L, float R) Read(double delaySamples)
    {
        if (delaySamples > _l.Length - 3) delaySamples = _l.Length - 3;
        if (delaySamples < 0) delaySamples = 0;
        var pos = _w - 1 - delaySamples;
        while (pos < 0) pos += _l.Length;
        var i0 = (int)pos;
        if (i0 >= _l.Length) i0 -= _l.Length;
        var f = (float)(pos - i0);
        var i1 = i0 + 1 == _l.Length ? 0 : i0 + 1;
        return (_l[i0] + (_l[i1] - _l[i0]) * f, _r[i0] + (_r[i1] - _r[i0]) * f);
    }

    public void Write(float l, float r)
    {
        _l[_w] = l;
        _r[_w] = r;
        if (++_w >= _l.Length) _w = 0;
    }
}

/// <summary>
/// 程序化脉冲响应：指数衰减白噪声 + 单极点低通阻尼（对齐 10b 的 makeIR）。
/// 无外部文件，种子固定 → 预览与导出同 IR。注意随机流跨声道共用一个实例（同 JS）。
/// </summary>
internal static class AfxIr
{
    public static float[][] Make(int sampleRate, double size, double damp)
    {
        var len = Math.Max(1, (int)Math.Floor(sampleRate * size));
        var rnd = J.Rng(AudioFx.IrSeed);
        var d = AfxMath.Cmp(damp, 0, 1, 0.5);
        var cut = 4000 + 9000 * (1 - d);
        var a = Math.Exp(-2 * Math.PI * cut / sampleRate);
        var k = 1 - a;
        var ir = new float[2][];
        for (var ch = 0; ch < 2; ch++)
        {
            var buf = new float[len];
            double lp = 0;
            for (var i = 0; i < len; i++)
            {
                var n = rnd.Next() * 2 - 1;
                lp += k * (n - lp); // 一极低通做阻尼
                buf[i] = (float)(lp * Math.Pow(1 - i / (double)len, 1.8 + damp)); // 衰减包络
            }
            ir[ch] = buf;
        }
        return ir;
    }
}

/// <summary>就地迭代基-2 复数 FFT（预计算旋转因子表 + 位反转表；Forward 无归一，Inverse 带 1/N）。</summary>
internal sealed class AfxFft
{
    private readonly int _n, _levels;
    private readonly float[] _cos, _sin;
    private readonly int[] _rev;

    public AfxFft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("FFT 长度须为 2 的幂");
        _n = n;
        _levels = Log2(n);
        _cos = new float[n / 2];
        _sin = new float[n / 2];
        for (var i = 0; i < n / 2; i++)
        {
            _cos[i] = (float)Math.Cos(2 * Math.PI * i / n);
            _sin[i] = (float)Math.Sin(2 * Math.PI * i / n);
        }
        _rev = new int[n];
        for (var i = 0; i < n; i++) _rev[i] = Rev(i, _levels);
    }

    public void Forward(float[] re, float[] im) => Transform(re, im, 1);

    public void Inverse(float[] re, float[] im)
    {
        Transform(re, im, -1);
        var s = 1f / _n;
        for (var i = 0; i < _n; i++)
        {
            re[i] *= s;
            im[i] *= s;
        }
    }

    private void Transform(float[] re, float[] im, int sign)
    {
        for (var i = 0; i < _n; i++)
        {
            var j = _rev[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var size = 2; size <= _n; size <<= 1)
        {
            var half = size >> 1;
            var step = _n / size;
            for (var i = 0; i < _n; i += size)
            {
                for (int j = i, k = 0; j < i + half; j++, k += step)
                {
                    var c = _cos[k];
                    var s = sign * _sin[k];
                    var l = j + half;
                    var tre = re[l] * c + im[l] * s;
                    var tim = im[l] * c - re[l] * s;
                    re[l] = re[j] - tre;
                    im[l] = im[j] - tim;
                    re[j] += tre;
                    im[j] += tim;
                }
            }
        }
    }

    private static int Log2(int x)
    {
        var l = 0;
        while ((1 << l) < x) l++;
        return l;
    }

    private static int Rev(int x, int bits)
    {
        int r = 0;
        for (var i = 0; i < bits; i++)
        {
            r = (r << 1) | (x & 1);
            x >>= 1;
        }
        return r;
    }
}

/// <summary>
/// 均匀分块 FFT 卷积（重叠保留法）：IR 按 2048 切片预变换，逐块 1 次正/逆 FFT + P 次谱乘。
/// 直接 O(n·m) 在 2s+ IR 下不可行（≈10⁹ MAC/s），故上 FFT；输出相对输入固有延迟一个块
/// （≈43ms@48k，混响湿声听感无差，且预览/导出同路径一致）。单声道实例，立体声各建一个。
/// </summary>
internal sealed class AfxConvolver
{
    public const int Block = 2048;
    private const int N = Block * 2;
    private static readonly AfxFft Fft = new(N);

    private readonly int _parts;
    private readonly float[][] _hRe, _hIm;      // IR 分块谱
    private readonly float[][] _xRe, _xIm;      // 输入窗口谱环形历史
    private readonly float[] _accRe, _accIm, _winRe, _winIm;
    private readonly float[] _prev = new float[Block];
    private int _head = -1;

    public AfxConvolver(float[] ir)
    {
        _parts = Math.Max(1, (ir.Length + Block - 1) / Block);
        _hRe = new float[_parts][];
        _hIm = new float[_parts][];
        var re = new float[N];
        var im = new float[N];
        for (var p = 0; p < _parts; p++)
        {
            Array.Clear(re);
            Array.Clear(im);
            var n = Math.Min(Block, ir.Length - p * Block);
            if (n > 0) Array.Copy(ir, p * Block, re, 0, n);
            Fft.Forward(re, im);
            _hRe[p] = (float[])re.Clone();
            _hIm[p] = (float[])im.Clone();
        }
        _xRe = new float[_parts][];
        _xIm = new float[_parts][];
        for (var p = 0; p < _parts; p++)
        {
            _xRe[p] = new float[N];
            _xIm[p] = new float[N];
        }
        _accRe = new float[N];
        _accIm = new float[N];
        _winRe = new float[N];
        _winIm = new float[N];
    }

    /// <summary>推进一个 Block 样本的输入块，产出对应的 Block 样本输出（重叠保留：取后半窗）。</summary>
    public void Process(float[] block, float[] outBlock)
    {
        Array.Copy(_prev, 0, _winRe, 0, Block);
        Array.Copy(block, 0, _winRe, Block, Block);
        Array.Clear(_winIm);
        Fft.Forward(_winRe, _winIm);
        _head = (_head + 1) % _parts;
        Array.Copy(_winRe, _xRe[_head], N);
        Array.Copy(_winIm, _xIm[_head], N);

        Array.Clear(_accRe);
        Array.Clear(_accIm);
        for (var p = 0; p < _parts; p++)
        {
            var idx = (_head - p) % _parts;
            if (idx < 0) idx += _parts;
            var hr = _hRe[p];
            var hi = _hIm[p];
            var xr = _xRe[idx];
            var xi = _xIm[idx];
            for (var k = 0; k < N; k++)
            {
                _accRe[k] += xr[k] * hr[k] - xi[k] * hi[k];
                _accIm[k] += xr[k] * hi[k] + xi[k] * hr[k];
            }
        }
        Fft.Inverse(_accRe, _accIm);
        Array.Copy(_accRe, Block, outBlock, 0, Block);
        Array.Copy(block, _prev, Block);
    }
}
