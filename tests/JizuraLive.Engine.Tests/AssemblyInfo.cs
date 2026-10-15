// 引擎（JText/JLang/JStyles 等）有大量**进程级静态缓存**，且渲染本身由 EngineRenderGate 串行化。
// 测试并行跑会并发改写这些静态缓存，导致 golden 比对抖动（画面出现结构性差异）。
// 因此本测试程序集整体禁用并行——串行执行，稳定优先。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
