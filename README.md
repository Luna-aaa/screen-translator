# 屏幕划取翻译（ScreenTranslator）

常驻托盘的 Windows 小工具：按快捷键框选屏幕上任意一块区域，自动截图 → 识别文字 → 翻译 →
在框旁弹出译文。用于游戏对话、PDF、图片、网页、视频字幕这类选不中文本的地方。

- 目标系统：Windows 10 (1903+) / Windows 11
- 技术栈：C# / .NET 9 / WPF + WinForms(NotifyIcon)
- 发布形态：单个自包含 exe，目标机不需要安装任何运行环境

---

## 开发进度

| 阶段 | 内容 | 状态 |
|---|---|---|
| 0 | 托盘、全局快捷键、单实例、设置窗口、配置加密存储 | ✅ 完成 |
| 1 | 冻屏 + 全虚拟桌面遮罩 + 框选 + 截图 | ✅ 完成 |
| 2 | Windows OCR、语言包检测与引导、自动语言判定 | ✅ 完成 |
| 3 | OpenAI 兼容翻译接口、译文窗（不抢焦点） | ✅ 完成 |
| 4 | 流式输出、复制、原文对照、重译、钉住、历史 | ⬜ |
| 5 | 第二个引擎（多模态大模型直译），设置里可切换 | ⬜ |

完整方案见 `C:\Users\M\.claude\plans\synthetic-leaping-finch.md`。

---

## 构建与运行

```powershell
# 开发时直接跑
dotnet run --project src\ScreenTranslator\ScreenTranslator.csproj

# 打包成单个 exe（输出到 .\dist\ScreenTranslator.exe）
.\publish.ps1
```

图标是脚本生成的，改了样式重新跑一次即可：

```powershell
.\tools\make-icon.ps1
```

### 对齐自检

```powershell
.\dist\ScreenTranslator.exe --selftest
```

不打开界面，只跑一遍几何检查后退出（0 = 全过，1 = 有失败），报告写在
`%APPDATA%\ScreenTranslator\selftest.txt`。它验证的是：进程真的是 Per-Monitor-V2、
虚拟桌面矩形覆盖了全部显示器、遮罩窗口占据的物理像素矩形与之完全相等、
以及从冻屏位图裁出来的区域与直接截取屏幕同一区域**逐像素一致**。

**换显示器或改了缩放比例之后值得重跑一次** —— 那正是这类工具最容易悄悄错位的时候。

### 翻译连通性检查

```powershell
.\dist\ScreenTranslator.exe --testkey                       # 用已保存的配置测
.\dist\ScreenTranslator.exe --testkey <地址> <模型> <key>   # 用临时参数测
```

不开界面，真发一次翻译请求后退出（0 = 通过），报告写在
`%APPDATA%\ScreenTranslator\testkey.txt`。带参数的形式用于验证各种错误提示；
**只传临时值**，命令行参数对同机其它进程可见。

### 代理

翻译请求默认走系统代理（访问境外服务通常需要）。但**回环地址（127.0.0.1 / localhost）
强制不走代理** —— 接本地模型（Ollama、LM Studio 等）时，请求被代理吞掉会得到一个
毫无意义的 502，极难排查。

---

## 文件位置

| 用途 | 路径 |
|---|---|
| 配置 | `%APPDATA%\ScreenTranslator\config.json` |
| 日志 | `%APPDATA%\ScreenTranslator\logs\yyyy-MM-dd.log`（保留 7 天） |
| 截图 | 默认第一个非系统盘下的 `\ScreenTranslator\Captures\`（本机是 `D:\ScreenTranslator\Captures`），可在设置里改 |
| 开机自启 | 注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `ScreenTranslator` |

**API key 不会明文落盘。** 它用 Windows DPAPI 按当前用户账户加密后，以 base64 存在
`config.json` 的 `ApiKeyProtected` 字段里。把 config.json 复制到别的电脑或别的用户账户下
是解不开的——这是有意为之。程序本身不内置任何 key。

---

## 关键实现说明

### 冻屏截图法

按下快捷键的瞬间先把整个虚拟桌面 `BitBlt` 成一张位图，再把这张位图铺在一个覆盖虚拟桌面
矩形的置顶窗口上供用户框选，松手后直接从这张位图里裁。选区坐标和位图坐标是同一套物理像素，
因此**不存在 DPI 换算，也就不可能出现"框的位置和截到的区域对不上"**，无论几块屏、缩放多少。

进程在 `app.manifest` 里声明 `PerMonitorV2`。这必须由清单完成——它要在原生启动的第一时间生效，
任何托管代码（包括 `Application.SetHighDpiMode`）都太晚了。因此项目里关掉了 WinForms 的
`WFO0003` 警告。

### 单实例

命名互斥体 `Local\ScreenTranslator.SingleInstance.v1` 决定谁是主实例；后启动的实例通过命名管道
`ScreenTranslator.Ipc.v1` 把意图（`show-settings`）交给主实例后自行退出。所以重复双击 exe
会唤起已有实例的设置窗口，而不是开出第二个托盘图标。

### 可换引擎

翻译和 OCR 都按接口设计（`ITranslator` / `IOcrProvider`，阶段 2、3 落地）。
`ServicePresets` 里列的 DeepSeek / 通义 / 智谱 / Kimi / OpenAI 共用同一个
OpenAI 兼容适配器——换服务商只是换 base URL + 模型 + key，不涉及代码。
