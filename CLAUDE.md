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
.\dist\ScreenTranslator.exe --testkey                 # 翻译连通性 + 可用模型，报告见 testkey.txt
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

`--selftest` 就是在验证这条不变量（8 项检查，含逐像素比对）。**换显示器或改缩放后要重跑。**

## 目录结构

```
src/ScreenTranslator/
  App.xaml.cs          应用根：单实例、托盘、快捷键、诊断入口、OCR→翻译流程编排
  Capture/             冻屏、遮罩、框选、裁剪、对齐自检
  Ocr/                 IOcrProvider + Windows OCR + 语言包检测 + 自动语种打分
  Translate/           ITranslator + OpenAI 兼容适配器 + 模型列表 + 连通性自检
  UI/                  设置窗、结果小窗、弹窗定位、关闭监视
  Config/              AppConfig、JSON 存取、DPAPI 加密
  Infrastructure/      路径、日志、单实例、开机自启
```

## 踩过的坑（改代码前先看这里）

**WinForms + WPF 同时启用，隐式 using 会打架。** 需要显式别名的类型：
`Application`、`Button`、`Brush`、`KeyEventArgs`、`MouseEventArgs`、`MessageBox`。
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

**测试用的本地 HTTP 桩要用 `TcpListener`，不能用 `HttpListener`** —— 后者在 Windows 上
需要 URL ACL 预留（即管理员），不提权时静默失败。

## 安全

- **程序不内置任何 API key。**
- key 用 Windows DPAPI 按当前用户加密后存 `%APPDATA%\ScreenTranslator\config.json` 的
  `ApiKeyProtected` 字段，文件里没有明文，复制到别的机器/账户解不开。
- 配置、日志、截图**全部在项目目录之外**，所以仓库里不可能混进密钥。
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
| 4 | 流式输出、复制、原文对照、重译、钉住、历史、快捷键自定义 | ⬜ 未开始 |
| 5 | 第二个引擎 | ⬜ 未开始 |

**阶段 5 的建议方向**：接「截图直接喂多模态大模型」，跳过 OCR。这能同时消掉两个已知痛点
——Windows OCR 认错字（用户实测遇到过「你」被拆成两个假名），以及随之而来的语种误判。
不要随便找第二家传统翻译 API 凑数。

用户报过、已修的问题（回归测试时覆盖）：托盘对勾不显示、鼠标拖影、Esc 无反应、
小窗不能拖 / 跑出屏幕、连续框选第二次没反应、中文被识别成日语。

完整方案见 `docs/PLAN.md`（原件在 `C:\Users\M\.claude\plans\synthetic-leaping-finch.md`）。

## 最可能返工的地方

1. **OCR 质量**——Windows 内置引擎对白底黑字印刷体尚可，对彩色背景、竖排日文、游戏花体字很差。
2. **自动语种判定**（`Ocr/LanguageScorer.cs`）——纯启发式，短文本和日英混排是弱项。刻意写成
   一个独立小函数，就是为了好换。
3. **小窗的定位与关闭时机**——手感问题，没有标准答案。
4. **全屏独占游戏**——BitBlt 会截到黑屏，置顶小窗也可能被吃掉，需要换 Windows.Graphics.Capture。
