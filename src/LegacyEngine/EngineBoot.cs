using JizuraLive.LegacyEngine.Packs;

namespace JizuraLive.LegacyEngine;

public static class EngineBoot
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        _ = JLang.Lang;         JFonts.Install();         JText.Install();         JStyles.RegisterAll();         JAnim.Install();         Reg.SeedCore();         JLayouts.Install();         JDecor.Install();         ZhLabels.Apply();     }

    private static bool _packsInstalled;

        public static void InstallPacks()
    {
        if (_packsInstalled) return;
        Install();
        _packsInstalled = true;

        P11BgcamB.Install();            P11Decor.Install();             P11DecorB.Install();            P11Enter.Install();             P11EnterB.Install();            P11Exit.Install();              P11ExitB.Install();             P11FxB.Install();               P11Horror1.Install();           P11Horror2.Install();           P11Horror3.Install();           P11Kinetic1.Install();          P11Kinetic2.Install();          P11Kinetic3.Install();          P11LayoutsA.Install();
        P11LayoutsB.Install();
        P11LayoutsC.Install();
        P11LayoutsD.Install();
        P11Looks.Install();             P11Styles.Install();            P11TreatTrans.Install();         P11Typo1.Install();             P11Typo2.Install();             P11Typo3.Install();             P11AudioFx.Install();   
        Omakase.Install();              ZhLabels.Apply();           }
}
