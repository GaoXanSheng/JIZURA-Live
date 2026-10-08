using JizuraLive.Engine.Packs;

namespace JizuraLive.Engine;

/// <summary>
/// 引擎装配入口：对位 frontend/src/engine/index.ts 的导入顺序。
/// 各表现文件在 JS 里是「导入即执行」；C# 侧显式按同一顺序 Install，
/// 保证注册表顺序（=抽取候选顺序）与旧引擎一致。幂等。
/// </summary>
public static class EngineBoot
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        // 导入序（index.ts）：01_util / 02b_lang / 02_fonts / 03_text / 04_styles / 05_anim /
        // 05b_registry / 06_layouts / 07_decor / …——02_fonts 之后依次是 03_text、05_anim、
        // 05b_registry、06_layouts、07_decor，按这个顺序排（注册表 order 列表互不交错，序即对齐）。
        _ = JLang.Lang; // 02b_lang：静态数据初始化
        JFonts.Install(); // 02_fonts：字体目录
        JText.Install(); // 03_text：GlyphCache/Metrics 登记进 JLang（SetLang 时统一清缓存）
        JStyles.RegisterAll(); // 04_styles
        JAnim.Install(); // 05_anim：enter/hold/exit
        Reg.SeedCore(); // 05b_registry：核心条目（TREAT.none/BG.none/CAMERA.push/FXE 内置 8 项）
        JLayouts.Install(); // 06_layouts
        JDecor.Install(); // 07_decor：15 decor + drawHUD
        ZhLabels.Apply(); // zh-labels：简中标签覆盖（index.ts 里最后导入，须在所有注册之后）
    }

    private static bool _packsInstalled;

    /// <summary>
    /// 全量装配 = 核心 + 已移植表现包（对位 index.ts 的 11p_* 导入序）+ omakase 随机 + 标签重放。
    /// 应用运行时走这里；包移植波次推进时在下方按 JS 导入序补 Install()。
    /// 与 Install() 分离：对拍夹具（core-only 黄金值）依赖核心表面。
    /// </summary>
    public static void InstallPacks()
    {
        if (_packsInstalled) return;
        Install();
        _packsInstalled = true;

        // —— 已移植表现包（顺序 = index.ts 导入序）——
        P11BgcamB.Install();    // 11p_bgcamB：37 bg + 12 cam
        P11Decor.Install();     // 11p_decor：decor 第一批
        P11DecorB.Install();    // 11p_decorB：decor 第二批
        P11Enter.Install();     // 11p_enter：40 条 enter（第一批）
        P11EnterB.Install();    // 11p_enterB：47 条 enter（第二批）
        P11Exit.Install();      // 11p_exit：36 exit + 20 hold
        P11ExitB.Install();     // 11p_exitB：39 exit + 12 hold
        P11FxB.Install();       // 11p_fxB：34 条非内置 fx
        P11Horror1.Install();   // 11p_horror1：12 layout
        P11Horror2.Install();   // 11p_horror2：7 enter + 7 exit + 4 hold + 4 treat
        P11Horror3.Install();   // 11p_horror3：7 decor + 4 bg + 2 cam + 3 fx + 2 trans + 3 style
        P11Kinetic1.Install();  // 11p_kinetic1：14 layout
        P11Kinetic2.Install();  // 11p_kinetic2：10 enter + 8 exit + 6 hold
        P11Kinetic3.Install();  // 11p_kinetic3：6 cam + 3 trans + 2 treat + 2 decor
        P11LayoutsA.Install();
        P11LayoutsB.Install();
        P11LayoutsC.Install();
        P11LayoutsD.Install();
        P11Looks.Install();     // 11p_looks：24 treat + 24 bg + 15 cam + 24 fx
        P11Styles.Install();    // 11p_styles：12 风格
        P11TreatTrans.Install(); // 11p_treattrans：文字加工 + 切到切转场
        P11Typo1.Install();     // 11p_typo1：18 layout
        P11Typo2.Install();     // 11p_typo2：8 enter/exit + 4 hold
        P11Typo3.Install();     // 11p_typo3：6 decor + 4 treat + 2 trans
        P11AudioFx.Install();   // 11p_audiofx：山脊线/随音高游移/心电图/换台（ASCII化 fx 待插件路径）

        Omakase.Install();      // 08b_omakase + 11q_sets：随机/锁定门禁（Reg.RandomOk 接线）
        ZhLabels.Apply();       // 包条目的简中标签（Apply 幂等，重放一次；含 Moods/Themes 显示名覆盖）
    }
}
