using Newtonsoft.Json;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// planner 结构测试：移植自 frontend/tests/engine/planner.spec.ts。
/// 黄金值跨语言对拍在 ParityTests；这里对齐 vitest 的行为断言。
/// </summary>
[Collection("EngineRegistry")]
public class PlannerTests
{
    private static Project MakeProject(Action<Project>? over = null)
    {
        EngineBoot.Install();
        var p = Project.NewDefault();
        p.Lyrics = TestSupport.SpecLyrics;
        p.Title = "テスト曲";
        p.Artist = "テスト歌手";
        // snap=false：行时间直接用 LRC 时间戳，默认会吸附到节拍/能量，时间就不可预测了
        p.Timing.Snap = false;
        over?.Invoke(p);
        return p;
    }

    private static AudioData MakeAudio(double duration = 120)
    {
        // 对齐 JS：Float32Array.from({length: duration*50}, (_v,i)=>Math.abs(Math.sin(i/8)))
        var energy = new float[(int)(duration * 50)];
        for (var i = 0; i < energy.Length; i++) energy[i] = (float)Math.Abs(Math.Sin(i / 8.0));
        var peaks = new float[400];
        for (var i = 0; i < peaks.Length; i++) peaks[i] = (float)Math.Sin(i / 5.0);
        return new AudioData
        {
            Duration = duration,
            Bpm = 120,
            Beats = JAudio.BeatGrid(120, 0, duration),
            Energy = energy,
            EnergyRate = 50,
            Peaks = peaks
        };
    }

    [Fact]
    public void 同一输入产出完全一致的plan()
    {
        var a = Planner.Plan(MakeProject(), MakeAudio());
        var b = Planner.Plan(MakeProject(), MakeAudio());
        Assert.Equal(Serialize(a.Cuts), Serialize(b.Cuts));
        Assert.Equal(Serialize(a.Lines), Serialize(b.Lines));
    }

    [Fact]
    public void 不同seed产出不同编排()
    {
        var a = Planner.Plan(MakeProject(p => p.Seed = 1), MakeAudio());
        var b = Planner.Plan(MakeProject(p => p.Seed = 999999), MakeAudio());
        Assert.NotEqual(Serialize(a.Cuts), Serialize(b.Cuts));
    }

    [Fact]
    public void 切分按时间有序且时长为正()
    {
        var plan = Planner.Plan(MakeProject(), MakeAudio());
        Assert.True(plan.Cuts.Count > 0);
        for (var i = 0; i < plan.Cuts.Count; i++)
        {
            var c = plan.Cuts[i];
            Assert.True(c.End > c.Start);
            if (i > 0) Assert.True(c.Start >= plan.Cuts[i - 1].Start - 1e-6);
        }
    }

    [Fact]
    public void 歌词行全部进入plan且强调冲击被解析()
    {
        var plan = Planner.Plan(MakeProject(), MakeAudio());
        var texts = plan.Lines.Where(l => l.Text.Length > 0).Select(l => l.Text).ToArray();
        Assert.Equal(new[] { "こんにちは世界", "ふたつめのライン", "三行目二段目", "ラスト" }, texts);
        var emphLine = plan.Lines.First(l => l.Text == "ふたつめのライン");
        Assert.Contains("ライン", emphLine.Emph);
        Assert.True(emphLine.Impact);
    }

    [Fact]
    public void 手切标记把一行拆成多个切分()
    {
        var plan = Planner.Plan(MakeProject(), MakeAudio());
        var texts = plan.Cuts.Select(c => c.Text).ToArray();
        Assert.Contains("三行目", texts);
        Assert.Contains("二段目", texts);
    }

    [Fact]
    public void 间奏标记生成interlude行()
    {
        var plan = Planner.Plan(MakeProject(), MakeAudio());
        var inst = plan.Lines.Where(l => l.Interlude).ToArray();
        Assert.True(inst.Length > 0);
        Assert.Contains(inst, l => l.Secs != null && Math.Abs(l.Secs.Value - 3) < 1e-9);
    }

    [Fact]
    public void 标题切分在开头且带曲名()
    {
        var plan = Planner.Plan(MakeProject(), MakeAudio());
        var title = plan.Cuts.FirstOrDefault(c => c.Layout == "title");
        Assert.NotNull(title);
        Assert.Equal("テスト曲", title!.Text);
        Assert.Equal(-1, title.Line);
    }

    [Fact]
    public void 时长以音频为准()
    {
        var plan = Planner.Plan(MakeProject(), MakeAudio(200));
        Assert.True(plan.Duration >= 199.5);
    }

    [Fact]
    public void 音频节拍进入plan且能量表按引用()
    {
        var audio = MakeAudio();
        var plan = Planner.Plan(MakeProject(), audio);
        Assert.Equal(audio.Beats, plan.Beats);
        Assert.Same(audio.Energy, plan.Energy);
    }

    [Fact]
    public void CutAt_命中区间返回切分()
    {
        var plan = Planner.Plan(MakeProject(), MakeAudio());
        var first = plan.Cuts[0];
        Assert.Same(first, Planner.CutAt(plan, first.Start + 0.01));
        Assert.Null(Planner.CutAt(plan, plan.Duration + 10));
    }

    private static string Serialize(object o) =>
        JsonConvert.SerializeObject(o, new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        });
}
