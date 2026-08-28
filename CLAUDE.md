# CLAUDE.md

Windows 屏幕划取翻译工具。托盘常驻 → 全局快捷键框选 → 截图 → OCR → 翻译 → 框旁弹小窗。

用户不写代码，交流一律用中文大白话，不堆术语。技术决策由 Claude 拍板，只在真正需要用户
定夺时才问，且要问得让非技术人员能答。

## 常用命令

```powershell
dotnet build src\ScreenTranslator\ScreenTranslator.csproj -c Debug   # 构建
.\publish.ps1                                                        # 打包成 dist\ScreenTranslator.exe
.\tools\make-icon.ps1                                                # 重新生成图标

.\dist\ScreenTranslator.exe --selftest                # 框选对齐自检，报告见 %APPDATA%\ScreenTranslator\selftest.txt
.\dist\ScreenTranslator.exe --testkey                 # 翻译连通性 + 可用模型 + 流式分段数，报告见 testkey.txt
.\dist\ScreenTranslator.exe --testkey <url> <model> <key>   # 用临时参数测（验证错误提示用）
```

**构建前一定要先杀掉正在运行的 ScreenTranslator**，否则 exe 被占用会报 MSB4018 文件锁错误：

```powershell
Get-Process ScreenTranslator -EA SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 1200
```

改完代码后的标准收尾：构建 → `.\publish.ps1` → 启动 dist 版本。**启动 dist 版本很重要**，
因为开机自启注册表项会跟着当前运行的 exe 路径走，跑调试版会把它指到 bin\Debug 去。

## 技术栈

C# / .NET 9 / `net9.0-windows10.0.19041.0`（这个 TFM 才能直接调 WinRT 的 `Windows.Media.Ocr`）。
WPF 做窗口 + WinForms 只用 `NotifyIcon` 和框选遮罩。自包含单文件发布，约 71MB，无 NuGet 依赖。

## 核心不变量：坐标 1:1

整个截图设计立在一句话上：

```
遮罩客户区 (x,y)  ==  冻屏位图 (x,y)  ==  屏幕 (虚拟桌面原点 + x, y)
```

按快捷键瞬间把整个虚拟桌面 BitBlt 成一张位图，遮罩窗口用 `SetWindowPos` 精确铺在虚拟桌面
矩形上显示这张图，用户在冻住的画面上框选，松手后直接从这张位图裁。**全程没有任何 DPI 换算，
所以不可能出现"框的位置和截到的区域对不上"**，无论几块屏、缩放多少。

支撑它的两件事，改动时不能破坏：

- `app.manifest` 里的 `PerMonitorV2`。必须由清单声明，任何托管代码（包括
  `Application.SetHighDpiMode`）都太晚。因此项目里关掉了 WinForms 的 `WFO0003` 警告。
- `SelectionOverlay` 吞掉 `WM_DPICHANGED`，且用 `SetWindowPos` 而不是 WinForms 的 Bounds 定位。

`--selftest` 就是在验证这条不变量（7 项检查，含逐像素比对）。**换显示器或改缩放后要重跑。**

## 目录结构

```
src/ScreenTranslator/
  App.xaml.cs          应用根：单实例、托盘、快捷键、诊断入口、OCR→翻译流程编排
  Capture/             冻屏、遮罩、框选、裁剪、对齐自检
  Ocr/                 IOcrProvider + Windows OCR + 语言包检测 + 自动语种打分
  Translate/           ITranslator + OpenAI 兼容适配器 + 模型列表 + 连通性自检
  UI/                  设置窗、结果小窗、历史窗、配色方案、弹窗定位与避让、关闭监视
  History/             最近的翻译记录（内存 + history.json）
  Config/              AppConfig、JSON 存取、DPAPI 加密
  Infrastructure/      路径、日志、单实例、开机自启
```

## 踩过的坑（改代码前先看这里）

**WinForms + WPF 同时启用，隐式 using 会打架。** 需要显式别名的类型：
`Application`、`Button`、`Brush`、`KeyEventArgs`、`MouseEventArgs`、`MessageBox`、`Cursors`、
`Color`、`ColorConverter`。
另外 **WinForms 项目的隐式 using 不含 `System.IO`**，用到 `Path`/`File`/`Directory` 要手动 using。

**`ProcessCmdKey` / `OnKeyDown` 在这个程序里不触发。** 消息循环是 WPF 的 Dispatcher，
不会跑 WinForms 的键盘预处理。遮罩的 Esc 必须在 `WndProc` 里直接接 `WM_KEYDOWN`。
鼠标消息不受影响（不需要焦点），所以曾出现"右键能取消、Esc 没反应"。

**结果小窗是 `WS_EX_NOACTIVATE`，永远拿不到键盘焦点。** 这是硬需求（不能抢走游戏/编辑器的
焦点）。因此它的 Esc 和"点别处关闭"靠 `DismissWatcher` 轮询 `GetAsyncKeyState` 实现——
故意不用低级键盘钩子（钩子卡住会拖慢全系统输入），也不用 `RegisterHotKey`（会吞掉 Esc）。
同理它不能用 `Window.DragMove()`，拖动是手写的。

**GDI+ 画细线会偏移。** `PixelOffsetMode.Half` 下 1 像素画笔的 `DrawLine`/`DrawRectangle`
会落在相邻一行，跑到重绘区域之外，于是留下永久拖影。**所有细线一律用 `FillRectangle`**，
并且所有 Invalidate 区域留 2px 余量（抗锯齿会溢出）。

**`ToolStripItem.Dispose()` 会把自己从所属集合里删掉。** 所以
`foreach (var i in items) i.Dispose()` 必定抛
「Collection was modified」——它在重建「最近的翻译」子菜单时直接弹了 .NET 错误框。
必须**先 `CopyTo` 到数组、再 `Clear()`、最后才 `Dispose`**。

**译文和原文用的是只读 `TextBox`，不是 `TextBlock`。** 因为 WPF 的 `TextBlock` 根本不能
用鼠标选中。因此必须连带着开 `IsInactiveSelectionHighlightEnabled`（窗口永远不激活，
不开就看不到选中底色）、关 `IsUndoEnabled`（流式输出每秒重写十几次）、并把
`ContextMenu` 置空（默认右键菜单里的「粘贴」在这无意义，而且那个弹出窗会被
`DismissWatcher` 当成「点了别处」）。副作用：`TextBox` 不支持 `LineHeight`，行距比以前紧一点。

**小窗配色全部走 `DynamicResource`。** `StaticResource` 在加载时就把颜色定死了，
换不掉；主题是在构造函数里把 `Resources[key]` 整批替掉的（`ApplyTheme`）。
新增一套配色 = 在 `PopupThemes.All` 里多写一条。

**小窗的拖动和拉伸按「离边缘多远」分，不按命中了哪个控件（`HitTest`）。**
最初把拉伸做成一个 18px 的小拓手，结果差 1 个像素就抓成了拖动——而且没有任何报错，
窗口只是默默地挪了。现在：右下角 22 DIP = 拉伸（斜线图标只是画着看的，
`IsHitTestVisible=False`），外圈 16 DIP 的边框 = 拖动，**中间什么都不是**。
中间不能拖是硬性要求：不然它会和鼠标选中文字打架。鼠标指针必须和这三个区一致
（四方箭头 / 斜双箭头 / 普通箭头），否则就是在骗人。
拉伸一旦开始就得关掉 `SizeToContent`，否则 WPF 会按内容重算尺寸、把拖动抵消掉。

**托盘菜单的对勾画在图标栏里。** `ShowImageMargin = false` 会让打勾的菜单项看起来没打勾，
必须开 `ShowCheckMargin`。

**HttpClient 默认走系统代理，连 127.0.0.1 也走。** 用户机器普遍配了代理，本地模型
（Ollama / LM Studio）的请求会被代理吞掉并返回 502。所以回环地址走独立的 `UseProxy=false`
客户端。

**OCR 语言标签在不同机器上不一致**（有的报 `ja`，有的报 `ja-JP`）。一律用
`OcrLanguages.TagsMatch` 做前缀双向匹配，创建引擎时用**系统实际报告的标签**，不要用配置里的。

**已装但没进候选列表的语言 = 静默的错误答案。** 中文被日文引擎读会得到一堆日文汉字
（"识别质量" → "沢別貭量"），看起来像模像样。所以候选语言默认等于系统已装的全部语言
（SchemaVersion 2 的迁移做的就是这件事）。

**流式输出：看响应的 Content-Type，不看自己发了什么。** 有的兼容服务接受
`stream:true` 却还是回一整块 JSON。按请求内容判断就会把这些人留在空白小窗前，所以用
`text/event-stream` 决定走哪个读取器，不是流就自动当整段处理。

**流式的超时是「多久没有新内容」，不是「总共多久」。** 每收到一行就
`CancelAfter` 重置一次。否则按一句字幕调好的超时会把一整段翻译从中间切断。
断在半路时（`Salvage`）把已收到的文字当「可能不完整」返回，而不是用错误提示把
用户已经看到的字擦掉。干净地断开但没收到 `[DONE]` **不**标警告——有的服务就不发
结束标记，标了会变成每次都报假警。

**`IProgress` 是异步投递的，最后一段可能晚于最终结果到达。** 小窗里用
`_settled` 拦住，否则迟到的那一段会盖掉已经写好的结果（连同「可能不完整」的黄字）。

**钉住的小窗在截图前必须先藏起来。** 它是置顶窗口，不藏就会被拍进冻屏位图、并浮在
遮罩上方——就是当初「连续框选第二次没反应」那个毛病。用 `SW_HIDE`/`SW_SHOWNA`，
不用 WPF 的 `Hide()`/`Show()`，因为 `Show()` 有可能抢焦点。另外新小窗要避开已钉住的（
`PopupPlacement.StepAside`），否则两次相邻的框选会把新窗盖在钉住窗上，钉住就白钉了。

**测试用的本地 HTTP 桩要用 `TcpListener`，不能用 `HttpListener`** —— 后者在 Windows 上
需要 URL ACL 预留（即管理员），不提权时静默失败。

**脚本合成鼠标操作时，每个 `mouse_event` 之间必须留时间（100ms 上下）。** 按下和移动
挤在一起发，WPF 还没处理完按下就收到了移动，拖动根本不成立——**表现是"功能坏了"，
但坏的是测试脚本**。这个坑已经骗过两次了。同理，驱动小窗的脚本要先
`SetProcessDpiAwarenessContext(-4)`，否则 `GetWindowRect` 返回的是被虚拟化的逻辑坐标，
和 `mouse_event` 要的物理坐标对不上。

## 安全

- **程序不内置任何 API key。**
- key 用 Windows DPAPI 按当前用户加密后存 `%APPDATA%\ScreenTranslator\config.json` 的
  `ApiKeyProtected` 字段，文件里没有明文，复制到别的机器/账户解不开。
- 配置、日志、截图、历史**全部在项目目录之外**，所以仓库里不可能混进密钥。
- 翻译历史存 `history.json`，里面是当时屏幕上的内容。因此它**封顶 30 条**、能在设置里
  关掉、也能一键清空；关掉开关只是不再新增，不会自动删已有的。
- `--testkey` 支持命令行传 key，**只用于传临时测试值**，命令行参数对同机其它进程可见。

## 用户与运行环境

用户不写代码。用中文大白话交流，不堆术语，技术选型自己拍板，只在真需要定夺时问，
且要问得让非技术人员能答。**做外发操作（推送代码、安装东西）前必须先确认。**

实测环境（改动涉及 DPI / OCR / 网络时留意）：

- **单显示器 2560×1600，缩放 150%** —— 多屏路径按正确方式实现了，但用户没有第二块屏，
  无法亲手验证；`--selftest` 里"覆盖全部显示器"那条在单屏下是自动成立的。
- 系统已装 OCR 语言：`en-US`、`ja`（注意不是 `ja-JP`）、`zh-Hans-CN`。
- 翻译用 **DeepSeek**（OpenAI 兼容接口）。**机器上配了系统代理。**
- 「图片」文件夹被 OneDrive 接管，所以截图默认存 `D:\ScreenTranslator\Captures`，不用系统图片目录。

## 需求边界

要做：托盘常驻、全局快捷键框选、截图、OCR、翻译、框旁弹小窗、Esc/点别处关闭。
场景是游戏对话、PDF、图片、网页、视频字幕这类**选不中文本**的地方。

**第一版明确不做**（不要自作主张加）：实时叠加翻译、划词取文本、OCR 结果手动校正、
账号系统、自动更新。

用户点名要求必须处理好的坑，都已实现，别改坏了：多屏+各屏不同缩放的框选对齐、遮罩覆盖
全部显示器、译文窗不抢焦点、OCR 语言包缺失要检测并引导安装、单实例。

## 进度

| 阶段 | 内容 | 状态 |
|---|---|---|
| 0 | 托盘、全局快捷键、单实例、设置窗、配置 DPAPI 加密 | ✅ 用户验证通过 |
| 1 | 冻屏、全虚拟桌面遮罩、框选、裁剪、保存 | ✅ 用户验证通过 |
| 2 | Windows OCR、语言包检测与一键安装、自动语种判定 | ✅ 用户验证通过 |
| 3 | OpenAI 兼容翻译、译文窗、错误提示、模型列表拉取 | ✅ 用户已填 key 验证跑通 |
| 4 | 流式输出、复制译文/原文、鼠标选中、原文对照、重译、钉住、历史、拖动与拉伸、四套配色 | ✅ 用户验证通过 |
| 5 | 第二个引擎 | ⬜ 未开始 |

阶段 4 加出来的设置项：`StreamTranslation`（边翻译边显示）、`KeepHistory`（记翻译历史）、
`PopupTheme`（小窗配色）。都在 `AppConfig` 里，改一处要同步改 `Clone`、`ConfigStore.Normalize`、
`SettingsWindow` 的 `LoadFromConfig`／`TryBuildConfig`／`CopyInto` —— **漏掉 `CopyInto` 的后果
是保存一次之后设置窗里的值会回滚**，而且不报错。

**阶段 5 的建议方向**：接「截图直接喂多模态大模型」，跳过 OCR。这能同时消掉两个已知痛点
——Windows OCR 认错字（用户实测遇到过「你」被拆成两个假名），以及随之而来的语种误判。
不要随便找第二家传统翻译 API 凑数。

用户报过、已修的问题（回归测试时覆盖）：托盘对勾不显示、鼠标拖影、Esc 无反应、
小窗不能拖 / 跑出屏幕、连续框选第二次没反应、中文被识别成日语、
托盘「最近的翻译」子菜单弹 .NET 错误框、小窗里的字选不中、四方箭头在小窗内部也出现。

完整方案见 `docs/PLAN.md`（原件在 `C:\Users\M\.claude\plans\synthetic-leaping-finch.md`）。

## 最可能返工的地方

1. **OCR 质量**——Windows 内置引擎对白底黑字印刷体尚可，对彩色背景、竖排日文、游戏花体字很差。
2. **自动语种判定**（`Ocr/LanguageScorer.cs`）——纯启发式，短文本和日英混排是弱项。刻意写成
   一个独立小函数，就是为了好换。
3. **小窗的定位与关闭时机**——手感问题，没有标准答案。
4. **全屏独占游戏**——BitBlt 会截到黑屏，置顶小窗也可能被吃掉，需要换 Windows.Graphics.Capture。
