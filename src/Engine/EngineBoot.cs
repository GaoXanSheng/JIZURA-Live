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
}
