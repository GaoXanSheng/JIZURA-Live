# i18n 播种词库存档

本目录存放从 git 历史取回的旧前端词库，供未来给 WPF 版新增界面语言时播种翻译。当前应用仅 zh-Hans（词库在 `src/App/I18n/Locales/zh-Hans/*.json`，值 = 界面原文）。

## 文件来源（用 `git show <commit>:<path>` 取回）

| 文件 | 来源 commit | 说明 |
|---|---|---|
| `english.gen1.js` | `adca9b5^:wwwroot/app/english.js` | 第一代 web 前端英文词库（注册表手法/风格名覆盖较全；UI chrome 是旧版结构） |
| `zh-Hant.gen1.js` | `adca9b5^:wwwroot/app/chinese.js` | 第一代繁中词库 |
| `zh-Hans.gen1.js` | `adca9b5^:wwwroot/app/chinese_hans.js` | 第一代简中词库（现 ZhLabels.cs 的前身） |
| `ko.gen1.js` | `adca9b5^:wwwroot/app/korean.js` | 第一代韩文词库 |
| `zh-labels.gen2.js` | `0fbdc31:frontend/src/engine/zh-labels.js` | 第二代 Vue 前端引擎标签包（= 现 `src/Engine/ZhLabels.cs` 的源），含 Moods/Themes/SampleLyrics/TR |
| `02b_lang.gen2.js` | `0fbdc31:frontend/src/engine/02b_lang.js` | 歌词语言标签与各语言字体栈 |

## 新增语言的接线步骤

1. 在 `src/App/I18n/Locales/` 下建 `{lang}/` 目录，按面板补 `{panel}.json`（键集与 zh-Hans 一致；缺键运行时自动回退 zh-Hans）。
2. UI 键值可从本目录旧词库播种：第一代词库覆盖引擎手法/风格名较好；WPF 时代的 UI 文案（云音乐/Spout/GPU 等）需补译。
3. `src/App/Views/SettingsTab.xaml.cs` 的 `UiLangs` 数组补一项（语言代码 → 界面自称）。
4. 引擎内容标签（手法/风格/氛围/主题名）：`src/Engine/ZhLabels.cs` 是 zh-Hans 覆盖层；新语言在其 `Apply()` 里按语言分支扩展（曾短暂引入 `LabelPacks` 门面，已按仓库决策移除，恢复多语言时可再引入）。
5. `dotnet test --filter LocalesTests` 会守门：引用键与词库不一致即失败。

## 旧词库 → JSON 的转换

旧文件是 JS 对象字面量（`key: 'value'` 形态），接入时可用一次性脚本转成扁平 JSON（内层 group 展开 `group.key`）。本目录保留原始 JS 以免转换器失真。
