# CLAUDE.md

Windows 屏幕翻译工具，托盘常驻，两个入口：

- **框选翻译**（`Ctrl+Alt+Q`）——框住一块 → 译文弹在旁边的小窗。里面又分两条路，在设置里选：
  **识文翻译**（本机 OCR 认字 + 文字模型）或 **看图翻译**（图直接发给多模态模型）。
- **全屏翻译**（`Ctrl+Alt+W`）——整屏的字翻好后**原地盖在原文位置上**，是一张快照。
  OCR 只负责出坐标，多模态负责出译文。

三条路各有一整套独立设置（地址/Key/模型/超时/截图目录…），互不影响。

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

.\dist\ScreenTranslator.exe --testvision             # 看图直翻连通性 + 挑出会看图的模型，报告见 testvision.txt
.\dist\ScreenTranslator.exe --testvision <url> <model> <key>
```

`--testvision` 会发一张写着 "Good morning. The sky is blue." 的小图过去，并把模型读回来的
内容原样写进报告。**必须看这两行对不对得上**——不会看图的模型照样会答一句像样的话，
只看「状态：Success」会被骗过去。

```powershell
.\dist\ScreenTranslator.exe --testsnapshot --dry   # 整屏覆盖：不联网，只验识别/分段/排版
.\dist\ScreenTranslator.exe --testsnapshot         # 用当前配置真跑一遍（会发一次请求）
.\dist\ScreenTranslator.exe --testsnapshot <url> <model> <key>
```

`--testvision` 现在也会把模型带回来的原文单独打出来（`IncludeOriginal` 开着时），
对不上就是模型没按格式走。

```powershell
.\dist\ScreenTranslator.exe --testclipboard [要复制的内容]   # 复制是否成功 + 耗时，报告见 testclipboard.txt
dotnet test                                          # 纯逻辑单元测试，不联网不花钱
```

`--testclipboard` 单独跑只能测空闲情况；**要复现真实问题，得让另一个进程同时占住剪贴板**
（用 `OpenClipboard` 开着不关）。判断占用是否真的建立，看自己这边 `Set-Clipboard` 会不会失败。

`--testsnapshot` 对**当前屏幕上的内容**跑一遍整屏流程，但不弹窗、不抢鼠标键盘，把结果写成
两张图：`testsnapshot-blocks.png`（每段的框和编号）和 `testsnapshot.png`（覆盖效果），报告见
`testsnapshot.txt`。**排版问题只能看图，报告里的数字看不出来。**
`--dry` 用等长占位文字代替译文，不联网不花钱，是改排版时该先跑的那个。

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
  Translate/           ITranslator + OpenAI 兼容适配器（文字 / 看图两用）+ 图片编码 + 模型列表 + 两个自检
  Overlay/             整屏快照：OCR 出坐标 → 行合并成段 → 分批并行翻译 → 画回原位 → 全屏窗口
tests/ScreenTranslator.Tests/   纯逻辑单元测试（xunit，只测能离线跑的纯函数）
  UI/                  设置窗、结果小窗、历史窗、配色方案、弹窗定位与避让、关闭监视
  History/             最近的翻译记录（内存 + history.json）
  Config/              AppConfig、三条路的 RouteSettings、服务预设、JSON 存取、DPAPI 加密
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

**历史窗口反过来：它是能拿焦点的普通窗口，所以 Ctrl+C 和默认右键菜单直接可用，
故意保留。** 小窗需要「复制选中」按钮，纯粹是因为它拿不到键盘焦点——别把这条限制
照抄到别的窗口上。历史窗口里那个按钮靠 `GotKeyboardFocus` 记住最后聚焦的文本框，
因为点按钮的瞬间焦点就跑到按钮上了，但选区还在。

**小窗那行状态文字开着 `TextTrimming="CharacterEllipsis"`，往里加东西会把尾巴挤掉。**
它一行要放「源语言 → 译文语言　·　耗时　·　token」，而窗口很窄。所以：
译文语言在这里用 `TargetLanguage.ShortName`（中文 / 繁中 / 英文…），
不用设置页那个 `DisplayName`（中文（简体））；单位写全 `token`，不写 `tok`。
**缩写出现在一行会被截断的文字里，看起来就是个 bug** —— 用户正是这么报上来的。

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

**文字翻译和看图直翻是同一个类 `OpenAiCompatibleTranslator`，靠构造函数里的 `_vision` 分流。**
不是为了省事——流式读取、断线抢救（`Salvage`）、HTTP 错误映射这三块要是抄成两份，早晚会
只修好一边。`_vision=false` 时发出去的 JSON 和阶段 4 一模一样（本地桩逐字节比对过）。
两条路的区别只有三处：提示词、user 消息的形状、`IsConfigured` 读哪份设置。

**看图那条路不发 system 消息，全塞进 user 消息里。** 各家对「图片旁边还能不能有 system 角色」
的说法不一致，单条 user 消息 + `[image_url, text]` 是四家都收的形状。

**小窗的「显示原文 / 复制原文」按 `_hasOriginal` 开关。** 看图直翻没有中间的识别文本，
不关掉的话点「显示原文」会展开一个空框。`SetTranslating`（老路）显式置 true，
`SetVisionTranslating`（新路）置 false。

**历史去重原本按「原文相同」删旧的，看图直翻的原文恒为空。** 照原样跑的话每存一条新的
就会把之前所有看图记录全删掉——历史永远只有一条。现在原文为空时改按译文比对
（`HistoryStore.IsDuplicateOf`）。

**图片编码要放到线程池上跑。** 一张全屏图 PNG 编码 + base64 有几百毫秒，卡在 UI 线程上
正好卡在小窗该出现的那一刻。见 `OpenAiCompatibleTranslator.SendVisionAsync`。

**剪贴板：报的错不算数，但也别用「读回来」去验证。** `Clipboard.SetText` 是先
`OleSetClipboard` 再 `OleFlushClipboard` 两步，别的程序（剪贴板历史工具、密码管理器、
Office）短暂占用时 flush 抛 `CLIPBRD_E_CANT_OPEN`——**内容其实已经进去了**。
照着异常报「复制失败」，用户一粘贴发现成功了，从此不知道该信哪条提示。

**第一版修错了，值得记下来：**「写完用 `Clipboard.GetText()` 读回来比对」——读剪贴板同样
要打开剪贴板，被占用时一样失败，而且 .NET 的读路径自己还会重试 10 次 × 100ms。
结果是把「误报失败」变成了「卡四秒之后还是误报失败」，比原来更糟。

正确做法（`ClipboardHelper`）：
- 用 `SetDataObject(text, copy: true, retryTimes: 1, retryDelay: 0)`，**不让它内部重试**——
  那个 10×100ms 的内部循环就是卡顿的来源。重试由我们自己控制。
- 判断有没有写进去用**不需要打开剪贴板**的两个 Win32 调用：`GetClipboardSequenceNumber`
  变了，或者 `GetClipboardOwner` 是本进程。
- 最多重试 8 次 × 60ms（约半秒）——这类占用都是一瞬间的，等一下就过去了。
- 全失败后最后再试一次 `copy: false`：**跳过 flush**，内容由本进程持有。托盘程序一直开着，
  能粘贴到重启为止，比"复制失败"强。

实测三种情况：空闲 13ms 成功；**短暂占用 78ms 成功且内容正确**（用户遇到的就是这种）；
一直占着 460ms 后如实报失败（那种情况内容确实没进去）。
诊断命令：`--testclipboard`，配合一个占住剪贴板的进程用。

**每次保存都会记下改了哪些字段，并留一份 `config.json.bak`。** 重构期间有个中间版本把
`OpenAi` 那一栏的模型和超时悄悄重置成了默认值，翻代码没找出路径——**这类问题不能靠猜**。
`ConfigStore.LogChanges` 把「哪个字段、从什么变成什么」写进日志（key 只记有无、不记内容），
`KeepPreviousVersion` 在内容真的变了时才滚动备份。用户手填的模型名和 key 是几分钟的劳动，
一份 3KB 的副本不值一提。

**预设下拉框「重新选中同一项」不能覆盖地址和模型。** WPF 在重建界面时会让 ComboBox 的
选中项在 null 和原值之间抖一下，照单全收就会把用户手填的模型换成预设默认值。
`RoutePanel._appliedPreset` 记住已经生效的那个预设 id，选中项没真变就什么都不做。

**看图翻译的「原文」是让模型顺手带回来的。** 这条路没有认字环节，本来无原文可显示。
现在提示词要求：先输出译文，另起一行 `===ORIGINAL===`，再照抄原文；`VisionReply` 负责切开。
译文在前是为了流式还能用。**流式显示必须用 `VisionReply.TranslationSoFar` 裁一刀**——
标记是一个分片一个分片到的（`==`、`===ORIG`），直接 IndexOf 会让这些字符在译文尾巴上闪一下。
开关是 `VisionSettings.IncludeOriginal`，默认开，多花一点输出 token。

**整屏是分批并行发的，不是一个大请求。** 每批 12 段、最多 3 批同时在飞，
**每批只裁自己那一片屏幕当图片**（`SnapshotBatching.Split`）。三重收益：省（总像素少于
一整屏）、准（局部图有效分辨率更高）、稳（一次只让模型编号 12 个，格式遵守率高得多）。
顺带根治了一个崩溃：以前把同一张 `frozen` 位图既交给覆盖窗口画、又交给后台线程编码，
GDI+ 不是线程安全的，抛 `Object is currently in use elsewhere`（真实使用中一天崩 4 次）。
现在每批持有自己独占的裁图，**没有共享就没有竞争**。

**批次的 `Dispose` 必须晚于重试。** 重试复用某一批的裁图而不是重新裁，
把 dispose 放在并行循环的 `finally` 里就会让重试去编码一张已经释放的位图——
这个 bug 是 `--testsnapshot` 当场抓出来的。

**会「思考」的模型是整屏慢的真正原因，不是批次大小。** 实测 `qwen3.8-flash`：
一批输出 188 字却烧了 3595 个 completion token，耗时 42 秒；关掉思考后同样的活
**139 秒 → 12 秒，输出 token 少了 9 倍**，而且译文质量和纠错能力没有损失
（`端凵`→`端口`、`资源眚理器`→`资源管理器` 照样纠对）。所以请求里带
`enable_thinking: false`。**当初"拆小批就快"的假设是错的**——思考时间不随输出变短。

**可选字段一律「乐观发送 + 一次性降级」，而且降级要记在静态表里。**
`enable_thinking` 和 `stream_options` 都不是所有服务商都认识，不认识的会直接 400。
做法：带上发 → 收到 400 就去掉重发一次 → **把「这个地址+模型不支持这个字段」记进
`OpenAiCompatibleTranslator.Unsupported`（static）**。
必须是 static：翻译器每次翻译都新建实例，实例级的记忆等于没记，
**每翻一次都要白白多发一次注定失败的请求**。
原则不变：**知道 token 用量、让模型别思考，都不值得让翻译本身失败。**

**payload 用字典拼，不用匿名类型。** 匿名类型里写 `x = cond ? v : null` 会把
`"x": null` 真的发出去；不认识这个字段的服务商看到 null 一样可能出问题。
字典可以「没有就不加这个键」。

**整屏翻译要对「原样退回」的段落自动重试一次。** 模型会把短的外文标签当成专有名词原样吐回来，
表现就是一屏中文里夹着几行没翻的英文/日文——这个功能看起来坏掉的第一大原因。
`SnapshotPipeline.RetryUntranslatedAsync` 挑出「译文和原文一模一样、且原文含两个以上外文字母」
的段落，只把这些重发一次，并在提示词里点名说「上一轮你原样返回了这些」。
**判断里必须排除中文段落**（原样退回是正确答案）和纯数字/符号/网址，否则每次都在为正确答案花钱。
超过 30 段就不重试了——那说明模型是整体没听指令，再发一次也一样。

**快照窗口里「所见即所复制」。** 正在看译文就复制译文，按住空格看原文时复制就是原文，
保存图片同理。这一条让「复制原文」不需要单独的按钮或快捷键。

**快照里的选中是按「段」不是按「字」。** 屏幕上那是一张位图，没有文字对象可以走光标。
左键拖一个框 → 松手复制框到的那几段（`SelectWithin`）；拖动距离小于 5px 才算点击（切原图）。
不设这个阈值，手抖一下就变成了选中。

**很窄的截图（一行字幕）要先放大再发。** 这些模型按 28px 的小块切图，一条 30px 高的图
几乎没有信息量。`ImageEncoder` 把短边不足 224px 的放大到 224（最多 3 倍），同时长边超过
上限的照旧缩小；两个方向撞上时以缩小为准。

**整屏覆盖不能照搬看图直翻。** 多模态不给可靠坐标（你让它报坐标它会编），所以走混合：
OCR **只负责出「字在哪」**，多模态负责出译文，图一起发过去。实测最能说明问题的一条：
OCR 把日文读成 `儆次 0 = 0 矿。今日儆 00 天氦 ℃ 矿。`（纯乱码），模型照样返回
「早上好。今天天气真好。」——因为它看得见图。**所以整屏这条路上，OCR 认错字不要紧，
但 OCR 的框错了就全错。**

**行必须先合并成段落再翻。** OCR 给的是「行」，一句话经常跨三行，逐行翻会得到三个断句。
`TextGrouping` 按行距 / 高度比 / 水平重叠聚类，三个条件缺一不可——只看行距会把并排的两栏
缝成一段。`--testsnapshot` 的 blocks 图就是用来看这一步对不对的：**一段一个框才对，
一行一个框说明合并失效了。**

**译文往下涨会压到下一段。** 译文比原文长时块要往下长，不设上限就会盖住下一段，段间空行
消失、整页糊成一坨。`OverlayRenderer.MeasureCeilings` 给每块算一个「下面那块的顶」当天花板，
撞到就改成缩字号。

**底衬必须完全不透明。** 之前用 alpha 242（95%）想让它融进背景，结果原文以灰影透出来——
浅色字压深色底只要几个百分点就还看得见。

**单行块和多行块的「行高」不是一回事。** 多行块的 `LineHeight` 是行距（约 1.2 倍字号），
单行块的是纯墨迹高度（小于字号）。用同一个系数换算字号，所有单行标签都会明显偏小。

**`SnapshotPipeline` 不许读 `App.Config`。** 诊断命令自己 `ConfigStore.Load()` 一份，
流水线要是偷偷去读运行中的静态实例，就会对着配置好好的机器报「还没配置」——
这个 bug 就是 `--testsnapshot` 抓出来的。配置一路当参数传。

**测试脚本别改用户的正式配置。** 「先改配置 → 跑 → 还原」这种写法，中间被 Ctrl+C 一断就
烂在改完的状态里（已经坑过一次，用户的 key 被换成了假的）。诊断命令支持
`<url> <model> <key>` 临时参数就是为了这个——**走参数，不碰 config.json**。

**三条路各有一整套设置，不共享任何东西。** `RouteSettings`（服务 + 超时 + 图片边长 + 截图目录）
→ `PopupRouteSettings`（+ 流式 / 配色 / 历史开关）→ `OpenAiSettings` / `VisionSettings`；
`SnapshotSettings` 直接继承 `RouteSettings`（没有小窗）。
**加字段只要加在基类上，三条路自动都有**——这是当初三份复制粘贴的替代方案。
界面那边同理：`RoutePanel` 一个类被用三次，**不存在「漏了一栏所以保存后回滚」的可能**。

**`AppConfig` 顶层那几个 `RequestTimeoutSeconds` / `StreamTranslation` / `PopupTheme` /
`KeepHistory` / `SaveCaptures` / `CaptureDirectory` 是遗留字段**，只在 v2→v3 迁移时读一次，
之后没有任何代码看它们。别再往那儿加东西，也别拿它们做判断。

**计算属性会被 System.Text.Json 写进配置文件。** `AppConfig.ActiveRoute` 忘了标
`[JsonIgnore]`，结果 config.json 里多出一份 OpenAi 的完整副本，看着像个没人能改的设置项。

**`SnapshotPipeline` 不许读 `App.Config`。** 诊断命令自己 `ConfigStore.Load()` 一份，
流水线要是偷偷去读运行中的静态实例，就会对着配置好好的机器报「还没配置」——
这个 bug 就是 `--testsnapshot` 抓出来的。配置一路当参数传。

**`Environment.GetFolderPath(ApplicationData)` 不看 `%APPDATA%` 环境变量**（它走
SHGetKnownFolderPath）。想让程序读别处的配置来做沙箱测试，这条路走不通——**结果是它
照样改了用户的真实配置**。要隔离就用诊断命令的 `<url> <model> <key>` 临时参数。

**下拉框的滚轮会误改值。** 鼠标划过「模型」下拉框时滚页面，会静默切换模型，几分钟后才以
一次翻译失败的形式暴露出来，中间没有任何提示。全局 `ComboBox` 样式里拦掉 `PreviewMouseWheel`，
并把事件重新抛给父级 ScrollViewer，否则页面会在指针经过下拉框时卡住不滚。

**有单元测试了，但只覆盖能离线跑的纯函数。** `tests/ScreenTranslator.Tests/`，xunit，
`dotnet test` 几十毫秒跑完，不联网不花钱。覆盖：编号解析、原文切分、行合并成段、
图片缩放决策、语种打分、译文语言映射。
主项目靠 `InternalsVisibleTo` 开放 internal——这些东西对除测试外的任何调用方都是实现细节，
为了测试改成 public 更糟。
**发布产物不受影响**：`publish.ps1` 显式指定主项目，xunit 进不了那个 71MB 的 exe
（验证过 dist 里只有 ScreenTranslator.exe）。所以"零 NuGet 依赖"这句现在特指**发布产物**。
写完第一次跑就抓到一个真 bug：越界的编号（只发了 2 段却回了 `[9]`）会被当成上一段的续写，
把垃圾接到第 1 段后面。**改的是解析器，不是测试。**

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
- 三条路的 key **分开加密存**，切换路线不会互相看到对方的 key。
- 保存配置前会留一份 `config.json.bak`，里面同样只有密文。
- 保存时记的「配置变更」日志**只记 key 的有无，不记内容**。
- `--testkey` / `--testvision` / `--testsnapshot` 支持命令行传 key，**只用于传临时测试值**，
  命令行参数对同机其它进程可见。走参数时不会碰 `config.json`。

## 用户与运行环境

用户不写代码。用中文大白话交流，不堆术语，技术选型自己拍板，只在真需要定夺时问，
且要问得让非技术人员能答。**做外发操作（推送代码、安装东西）前必须先确认。**

实测环境（改动涉及 DPI / OCR / 网络时留意）：

- **单显示器 2560×1600，缩放 150%** —— 多屏路径按正确方式实现了，但用户没有第二块屏，
  无法亲手验证；`--selftest` 里"覆盖全部显示器"那条在单屏下是自动成立的。
- 系统已装 OCR 语言：`en-US`、`ja`（注意不是 `ja-JP`）、`zh-Hans-CN`。
- 三条路各自配的服务：识文翻译 = **DeepSeek**，看图翻译和全屏翻译 = **通义千问 VL**
  （阿里云百炼）。**机器上配了系统代理**，所以非回环地址走代理、回环地址不走。
- 「图片」文件夹被 OneDrive 接管，所以图片默认存 `D:\ScreenTranslator\` 下面，
  不用系统图片目录：框选的原图进 `框选翻译\`，整屏翻好的图进 `全屏翻译\`。

## 需求边界

要做：托盘常驻、两个全局快捷键（框选 / 全屏）、截图、OCR、翻译、框旁弹小窗或原地覆盖、
Esc/点别处关闭。场景是游戏对话、PDF、图片、网页、视频字幕这类**选不中文本**的地方。

**第一版明确不做**（不要自作主张加）：实时叠加翻译（阶段 6 的原位覆盖是**按一下出一张
快照**，不是一直挂着跟随画面变化——这条界线不要越）、划词取文本、OCR 结果手动校正、
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
| 5 | 看图翻译：截图直接喂多模态大模型，跳过 OCR | ✅ 用户验证通过 |
| 6 | 整屏原位覆盖：OCR 只出坐标 + 多模态出译文，把译文画回原文位置（翻译快照） | ✅ 用户验证通过 |
| 6.1 | 三条路设置拆分、快照里框选复制 / 复制原文 / 保存图片、下拉框滚轮修复 | ✅ 用户验证通过 |
| 6.2 | 剪贴板误报失败、看图翻译补上原文、整屏漏翻自动重试 | ✅ 用户验证通过 |
| 6.3 | 整屏分批并行提速（139s→12s）、译文语言可选、token 用量可见、纯逻辑单元测试 | ✅ 用户验证通过 |

**设置窗的五页**（用户定的结构）：

| 页 | 内容 |
|---|---|
| 通用 | 译文语言、框选翻译快捷键、全屏翻译快捷键、开机自启、用量统计、文件位置 |
| 框选翻译 | 顶上「翻译方式」开关；下面**左右两栏**「识文翻译」\|「看图翻译」，每栏一整套；底下是共用的历史查看/清空 |
| 全屏翻译 | 自己一整套服务设置 + 图片保存位置 |
| 文字识别 | 源语言、候选语言、语言包安装（识文翻译和全屏翻译都会用到） |
| 关于 | 两种用法说明、进度、安全说明、文件位置 |

**加一个新设置项的正确姿势**：加在 `RouteSettings` / `PopupRouteSettings` 基类上 → 在
`CopyBaseInto` / `CopyPopupInto` 里带上 → 在 `RoutePanel` 里加控件引用和 Load/Validate/WriteTo。
`AppConfig.Clone` 和 `SettingsWindow.CopyInto` 只处理顶层字段，路由内部的由 `Clone()` 负责。

`TargetLanguage` 是全局一份（三条路共用），选项和提示词里的语言名都在
`Config/TargetLanguages.cs`，**别再往提示词里塞第二份 switch**。
死字段 `ActiveTranslator` 已删——它从来没有被任何代码消费过，改成任何值都毫无效果。

用量统计存 `%APPDATA%\ScreenTranslator\usage.json`，按「日期 + 路线」累加，保留 30 天。
**只记 token，不折算成钱**：单价随模型和服务商变，写死一个价目表迟早给出错误的数字。
诊断命令的花费也算进去——它们同样在真花钱。

截图目录：框选 → `D:\ScreenTranslator\框选翻译`（旧的 `Captures` 会在启动时自动改名，
只在「旧的在、新的不在」时才动手，失败只记日志），全屏 → `D:\ScreenTranslator\全屏翻译`
（**不自动保存**，在快照里点「保存图片」或 Ctrl+S 才存）。

**阶段 5、6 的完整方案见 `docs/PLAN.md`，动手前先看那里。** 两句话概括：
阶段 5 把截图直接发给多模态大模型、跳过 OCR，一次消掉三个痛点——OCR 认错字
（实测「你」被拆成两个假名）、随之而来的语种误判、以及「一张图里好几种语言时只有
一种是对的」（现在是三个引擎打分取一个赢家，输的那种留下的是看着像样的乱码）。
阶段 6 在此之上做全屏原位覆盖，但**它不能照搬阶段 5**：多模态不给可靠坐标，所以
得走混合模式——OCR 只负责出「字在哪」，多模态负责出译文。
不要随便找第二家传统翻译 API 凑数。

**阶段 5、6 当时的硬规矩：现有功能一个都不许动。** 新东西一律做成「设置里多出来的一个
选项」且默认关闭，老快捷键行为不变。老路是用户已经验证通过的，它就是回退方案；
「顺手重构一下」的冲动要压住。

**这条规矩在 6.1 被用户自己解除了一次**——设置窗按三条路重排、`Captures` 改名，都是用户
点名要的。**区别在于「谁提的」**：用户要求的结构调整照做，自己觉得该重构的一律不动。

用户报过、已修的问题（回归测试时覆盖）：托盘对勾不显示、鼠标拖影、Esc 无反应、
小窗不能拖 / 跑出屏幕、连续框选第二次没反应、中文被识别成日语、
托盘「最近的翻译」子菜单弹 .NET 错误框、小窗里的字选不中、四方箭头在小窗内部也出现、
**复制时卡好几秒并误报「剪贴板被占用」（其实已经复制成功）**、
**鼠标滚轮划过下拉框会静默改掉模型**、**看图翻译没有原文可显示 / 可复制**、
**整屏翻译漏翻个别英文和日文段落**、**整屏翻译报「出错了」**（GDI+ 位图跨线程）、
**小窗状态行末尾的 token 单位被截断成半个词**。

完整方案见 `docs/PLAN.md`（原件在 `C:\Users\M\.claude\plans\synthetic-leaping-finch.md`）。

## 最可能返工的地方

1. **OCR 质量**——Windows 内置引擎对白底黑字印刷体尚可，对彩色背景、竖排日文、游戏花体字很差。
2. **自动语种判定**（`Ocr/LanguageScorer.cs`）——纯启发式，短文本和日英混排是弱项。刻意写成
   一个独立小函数，就是为了好换。
3. **小窗的定位与关闭时机**——手感问题，没有标准答案。
4. **全屏独占游戏**——BitBlt 会截到黑屏，置顶小窗也可能被吃掉，需要换 Windows.Graphics.Capture。
5. **整屏覆盖的排版**（`Overlay/OverlayRenderer.cs`）——译文长度、底衬盖不干净、分栏和表格
   还原不了，这三样是问题本身的性质，不是 bug。要调先跑 `--testsnapshot --dry` 看图。
6. **模型是不是「会思考」的那种**——这一条比任何代码优化都重要。同一台机器、同一屏内容，
   开思考 139 秒、关思考 12 秒。换模型后如果整屏突然变慢，先看日志里的 completion token
   是不是远大于译文字数。
7. **行合并成段落**（`Overlay/TextGrouping.cs`）——三个阈值（行距 / 高度比 / 水平重叠）是拍
   出来的，换一种排版可能就要重调。blocks 图里「一段一个框」才对。
