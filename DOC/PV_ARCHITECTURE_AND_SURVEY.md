# 独立 PV 舞台子系统：行业调研、架构规划与效果升级方案

> **文档状态**：正式规划与架构落地规范  
> **核心目标**：彻底解决「软件越来越混合、PV 概念混淆、耦合严重、效果生硬粗糙」的核心痛点，还原 PV（音乐视觉影像）的独立性与专业品质。

---

## 一、业界调研：常见 PV 的真实形态与工业标准

### 1.1 什么是 PV？—— 概念的正本清源
在 ACG、Vocaloid、VTuber、同人音乐与现代流行音乐工业中，**PV（Promotional Video）即音乐视觉影像（Music Video）**。  
它是一个**以音频（节奏、旋律、情绪）为时间轴基石，通过分镜（Cut）、动效排版（Typography）、视觉主体（角色/插画/舞台）、运镜摄像机（Camera）与后期光影调色（Post FX）融合而成的独立视听艺术作品**。

### 1.2 业界常见 PV 的四种主流形态

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                               常见 PV 核心流派                                  │
├─────────────────────┬─────────────────────┬───────────────────┬─────────────────┤
│ 1. 动效歌词 PV      │ 2. 手书/分镜漫 PV   │ 3. 角色演出版 PV  │ 4. 歌回/轻量 PV │
│ (Kinetic Typography)│ (Manga/Storyboard)  │ (3D/Live2D Stage) │ (Visualizer)    │
├─────────────────────┼─────────────────────┼───────────────────┼─────────────────┤
│ · 极大冲击力的排版  │ · 漫画分格、Q版立绘 │ · 虚拟舞台与灯光  │ · 唱片机/磁带机 │
│ · 错落节奏与卡点打击│ · 对白气泡、集中线  │ · 空间层次穿插    │ · 律动波形谱    │
│ · 几何/HUD/故障美学 │ · 剧情向叙事镜头    │ · 角色受环境光照  │ · 优雅极简排版  │
│ · 代表：《KING》    │ · 代表：翻唱同人手书│ · 代表：演唱会MV  │ · 代表：Lofi/歌回│
│   《神っぽいな》    │   《劣等上等》手书  │   Vocaloid 舞台PV │   伴奏背景      │
└─────────────────────┴─────────────────────┴───────────────────┴─────────────────┘
```

#### ① 动效歌词 PV (Kinetic Typography PV)
- **视觉灵魂**：歌词不再是死板的阅读辅助，而是画面的**主角与视觉符号**。
- **排版张力**：非对称布局、大字破格（Punchline）、字形变形、汉字部首拆解、多语言排版（正文假名/汉字 + 装饰性英文/罗马字）。
- **极简对比色调**：通常全局只使用 2~3 个核心主色（如 黑/白/朱红、深灰/荧光绿、紫/粉霓虹），色调纯粹高级，杜绝杂乱。
- **律动冲击感**：强烈的重音击打（Beat Hit）、变焦冲击（Zoom Punch）、瞬间白闪（Flash）、色差故障（Chromatic Glitch）。

#### ② 手书/动态漫 PV (Illustration & Manga Panel PV)
- **视觉灵魂**：分镜（Storyboard）与角色演技。
- **画面构成**：分格画框、立绘表情切换、对白气泡、漫画集中线、汗滴/红晕/星瞳等二次元演出符号。
- **镜头节奏**：全景建立空间 -> 特写表现情绪 -> 快速跳切表现包袱。

#### ③ 角色舞台演出版 PV (Performance / Stage PV)
- **视觉灵魂**：主体置身于三维空间舞台之中。
- **空间层次（3D Depth）**：
  - **背景层**：巨幕、灯光阵列、粒子流、虚焦建筑。
  - **主体层**：角色（Live2D / 3D / 插画），**必须吃场景环境光染（Ambient Light）与阴影（Drop Shadow）**。
  - **穿插层**：歌词浮动于角色周围，或从角色身后穿透到身前。
  - **前景与运镜**：前景虚化光斑、飞扬粒子，摄像机推拉摇移时呈现明显的**视差（Parallax）**。

#### ④ 歌回/轻量伴奏 PV (Stream & Visualizer PV)
- **视觉灵魂**：伴随伴奏轻盈呼吸，主次分明，不喧宾夺主。
- **画面构成**：优雅的文字排版、轻微缓动的波形、细腻的底纹质感（纸纹/胶片颗粒/暗角）。

---

## 二、现有软件问题剖析：为什么“混合起来了，效果太垃圾，设计不合理”？

### 2.1 概念错位与张冠李戴
- **致命混淆**：现有项目把「直播推流时把 VTube Studio / 游戏 Spout 输入作为图层叠加推给 OBS」的功能，直接冠名为**「PV 模式」**。
- **造成的严重后果**：
  - 用户打开 JIZURA-Live 想做歌词 PV，点击顶部「PV」，出现的居然是「VTS 模型大小旋转调节、4路 Spout 接收卡片、热键触发」！
  - 真正的 PV（音乐视觉影像）本是一套**完整的制作、排版、渲染、多图层合成、视听输出体系**，却被降格成了直播开播的「视频浮层接收台」。

### 2.2 架构严重耦合与反向依赖（意大利面条）
1. **核心渲染引擎严重受污**：
   - `Renderer.cs` 负责歌词动效排版渲染，本应是纯粹的无状态纯函数（$f(plan, t) \to Image$）。
   - 结果硬塞入了 `opt.Live`、`DrawLive`、`opt.FrameCamera` 等外部硬件/Spout 纹理借用逻辑，导致引擎丧失纯粹性，测试脆弱。
2. **业务反向依赖与越权**：
   - 播放会话 `CloudPlayerSession.cs` 放音乐时，竟然直接在换歌时调用 `MainWindow.Vts.TriggerHotkey(...)`。播放器认识角色驱动硬件，职责严重越界。
   - `SpoutOut.cs` 推流发送端里面硬塞了 `BakeSubtitle`（把字幕强行画进主画面帧），导致字幕有两套互不兼容的路径。
3. **全局配置与 UI 大杂烩**：
   - `settings.json` 把编辑器的 UI 设置、音频设置、PV 的 4 路 Spout 源设置、VTS 连接状态搅在一起。
   - 右侧栏把样式、特效与 Spout 采集混在一个 Tab 里。

### 2.3 为什么“现在效果做的太垃圾了”？（视觉缺陷根因）
1. **生硬的“贴片浮层感”，毫无融合度**：
   - 外部源（如 VTS 角色）只是被 `DrawLive` 作为一个简单的透明矩形贴在画面上。
   - **零环境光融合**：PV 背景是紫红色的，角色还是白惨惨的；背景变暗，角色依然刺眼；毫无环境光漫射（Ambient Tint）与边缘高光（Rim Light）。
   - **零空间层次**：没有阴影、没有背部光晕，像粗制滥造的 PPT 贴图。
2. **镜头粗暴晃动，严重穿帮露底**：
   - 所谓“整幅一起吃镜头（`FrameCamera`）”，仅仅是对整个输出 Canvas 执行矩阵旋转/平移。
   - 一旦相机有推拉（Zoom Out）或手持震颤（Shake），四周立即露出难看的**黑色空洞底边**！完全缺少专业视频软件必备的 **Overscan 安全扩展与边缘保护机制**。
3. **平铺直叙，毫无视差纵深（No Parallax）**：
   - 背景、角色、歌词被钉死在同一个平面上，镜头运动时像一块铁板一起晃动，完全没有三维影视镜头中“近快远慢”的深度呼吸感。

---

## 三、全新架构规划：解耦 + 专业独立 PV 体系

### 3.1 三层解耦原则与架构图

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                               JIZURA-Live 系统架构                              │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                 │
│  【 Layer 1: Pure Core Engine (纯净核心渲染引擎) 】                             │
│   - JizuraLive.Engine: 纯净时间-像素渲染管线 (Renderer.Frame(Canvas, Plan, t))  │
│   - 纯粹专注：日系排版、动效手法(Enter/Hold/Exit)、文字动效(Typo/Kinetic)、   │
│               背景装饰(Decor/HUD)、摄像机运镜(Cam)、后期质感(Post/FX)。         │
│   - 彻底解耦：移除 LiveLayer/DrawLive/Spout 等外部依赖，保持纯净与极高测试性。  │
│                                                                                 │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                 │
│  【 Layer 2: Independent PV System (独立 PV 舞台与合成子系统) 】                │
│   - JizuraLive.Pv: 独立子系统，拥有自己的场景文档、图层体系与合成器             │
│   - 核心组件：                                                                  │
│     * PvScene: 独立场景文档（支持多图层定义、深度规划、融合调色、存档 pv.json） │
│     * PvCompositor: 专业级多图层合成器                                          │
│       - 图层栈：Background(背景) -> Asset/Character(角色/素材)                 │
│                 -> LyricPv(动效歌词) -> Foreground(前景装饰/粒子) -> Subtitle   │
│       - 视差运镜引擎 (Parallax Camera Engine)：每层独立 Depth 系数，呼吸纵深    │
│       - 环境光影融合 (Ambient Light Tinting & Drop Shadow & Rim Glow)           │
│       - 防露底自适应安全边缘 (Anti-Bleed Overscan Guard)                        │
│     * IPvDriver: 音乐驱动器抽象（云歌单/本地音频/编辑器实拍/手动控制）          │
│     * PvTrackEventBus: 统一歌曲事件总线（换歌/节拍打点通过总线解耦广播）        │
│                                                                                 │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                 │
│  【 Layer 3: Adapters & IO (外围输入与输出适配器) 】                            │
│   - SpoutIn / VTS API / SpoutOut / 视频导出 / OBS 采集                          │
│   - 作为独立驱动器或图层 Provider 接入，绝不反向侵入底层逻辑。                  │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### 3.2 独立 PV 子系统核心接口规划

```csharp
namespace JizuraLive.Pv;

/// <summary>PV 场景中的独立图层</summary>
public interface IPvLayer : IDisposable
{
    string Id { get; }
    string Name { get; }
    bool Enabled { get; set; }
    
    /// <summary>图层在空间中的视差深度（0.0=无限远背景, 1.0=歌词主体基准面, >1.0=近景前景）</summary>
    double Depth { get; set; }
    
    /// <summary>图层不透明度 0.0 ~ 1.0</summary>
    double Opacity { get; set; }
    
    /// <summary>图层渲染与合成（由合成器驱动）</summary>
    void Render(SKCanvas canvas, PvRenderContext ctx);
}

/// <summary>PV 场景合成上下文</summary>
public sealed class PvRenderContext
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double Time { get; set; }
    public CamState Camera { get; set; } = new();
    public SKColor PrimaryColor { get; set; }
    public SKColor AccentColor { get; set; }
    public bool FastMode { get; set; }
}

/// <summary>音轨驱动器（解耦音频来源）</summary>
public interface IPvDriver : IDisposable
{
    string DriverName { get; }
    double CurrentTime { get; }
    double Duration { get; }
    bool IsPlaying { get; }
    void Play();
    void Pause();
    void Seek(double time);
    event Action<PvTrackInfo> TrackChanged;
}
```

---

## 四、视觉效果升级体系设计（彻底终结“垃圾效果”）

为彻底解决用户痛斥的“效果做的太垃圾了”，新架构在合成器（PvCompositor）中引入四大工业级渲染增强：

### 4.1 空间视差运镜体系 (Parallax Camera Matrix)
- **原理**：真实镜头中，远景几乎不动，近景飞速掠过。
- **数学映射**：
  设摄影机主移动为 $(\Delta X, \Delta Y)$，旋转为 $\theta$，缩放为 $S$：
  对于深度为 $D$ 的图层：
  $$\Delta X_L = \Delta X \times D, \quad \Delta Y_L = \Delta Y \times D$$
  $$S_L = 1.0 + (S - 1.0) \times D$$
- **推荐标准深度配置**：
  - **背景层（Background）**：$D = 0.35$（深远沉稳）
  - **角色/素材层（Character/Asset）**：$D = 0.85$（主体稳定）
  - **动效歌词层（Lyric PV）**：$D = 1.00$（标准焦点平面）
  - **前景粒子/HUD（Foreground FX）**：$D = 1.35$（近景飞掠，张力倍增）

### 4.2 环境光影融入 (Ambient Light Tinting & Drop Shadow)
- **环境光染色（Ambient Grading）**：
  从当前歌词工程的配色方案（Scheme）中提取背景与主色彩，以轻量权重（$15\% \sim 25\%$）对外部素材进行微色彩校正，消除素材生硬发白的“塑料贴片感”。
- **投影与光晕（Ambient Shadow & Rim Glow）**：
  根据背景明暗自适应：
  - 深色背景：为主体后方生成柔和泛光（Bloom Glow），增强层次分离度。
  - 浅色背景：为主体后方生成柔和接触阴影（Soft Drop Shadow），提供实体立足感。

### 4.3 防露底自适应安全边缘 (Anti-Bleed Overscan Guard)
- 镜头运动导致边缘偏移超过画布边界时：
  1. 合成器默认开辟 $5\% \sim 8\%$ 的 Overscan 缓冲区。
  2. 针对背景层采用边缘自适应延伸（Edge Clamp / Mirror Padding），彻底消除镜头旋转平移时的难看黑缝与穿帮漏洞。

---

## 五、实施与迁移步骤

1. **Phase 1: 规范与独立 Compositor 模块建立**
   - 建立 `src/Pv/` 模块，实现 `PvCompositor`、`PvScene`、`PvParallaxEngine`、`PvColorGrading`。
   - 纳入全套单测，验证视差矩阵计算、环境融合矩阵、图层合成正确性。
2. **Phase 2: 核心渲染管线瘦身与去耦**
   - 彻底将外部输入隔离在引擎外；Renderer 只负责输出歌词层或背景层。
   - 移除 `CloudPlayerSession` 中的 VTS 反向强调用，改为事件总线订阅。
3. **Phase 3: 效果升级验证与全量测试回归**
   - 运行完整测试套件，确保 400+ 个现有测试无损通过，新增 PV 独立测试全绿。
   - 在真实画面上验证视差运镜与环境光融合的显著画质提升。
