# M5 余项交接（代理配额 2026-10-11 19:56 重置后执行）

## 已落地（勿重做）
layoutsA-D(123) / enter(40) / exit+exitB(107) / decor+decorB / treattrans /
kline / ascii / audiofx(4) / omakase / zh-labels — 全部在 EngineBoot.InstallPacks 接线。

## 待移植（JS 源 → 目标文件）
| 源 | 目标 | 组 |
|---|---|---|
| 11p_bgcamB.js | src/Engine/Packs/P11BgcamB.cs | bg/cam |
| 11p_fxB.js | src/Engine/Packs/P11FxB.cs | fx |
| 11p_looks.js | src/Engine/Packs/P11Looks.cs | （按源） |
| 11p_styles.js | src/Engine/Packs/P11Styles.cs | Reg.Styles |
| 11p_typo1/2/3.js | src/Engine/Packs/P11Typo1/2/3.cs | （按源） |
| 11p_kinetic1/2/3.js | src/Engine/Packs/P11Kinetic1/2/3.cs | （按源） |
| 11p_horror1/2/3.js | src/Engine/Packs/P11Horror1/2/3.cs | （按源） |

## 每个代理的固定提示词模板
见 git log 里「M5 波次二」各代理 prompt（要点：README 契约 → 对应组样板文件
（JLayouts/JAnim/JDecor/JStyles）→ Pack 文件 + `[Collection("EngineRegistry")]`
测试 → Install() 幂等/无 static ctor/不改共享文件 → RNG 顺序保真 → Name 断言只查非空
→ 全量测试保持全绿）。完成后在 EngineBoot.InstallPacks 按 index.ts 导入序补
Install()（bgcamB 最先，kline/ascii 在 sets 之后，zh-labels 重放已在末尾）。

## 已知挂账
- Renderer 非内置 FXE 插件 Draw 路径未接（fxB 代理任务含接线）
- TransDef.Draw 调用路径未接（treattrans 的转场本体已注册，渲染端接入待做）
- Env.Beat 缺 {since,len,index} 对象（horror/kinetic 的 beat 驱动条目走了回退式）
- P11AudioFx 的 fx「ASCII化」未注册（同 fx 插件路径）
- decor/decorB 无专属冒烟测试文件（PackDecorTests 已覆盖两者）
