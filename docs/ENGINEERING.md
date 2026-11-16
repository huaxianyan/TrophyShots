# 工程细则

`AGENTS.md` 只留硬约束、技术基线与命令，这份放实现细节。改截图、续做、奖杯识别或视频宿主前先读对应小节。

## 截图规则

- 启动时读 mpv 配置：先 `portable_config/mpv.conf`，再 `%APPDATA%\mpv\mpv.conf`，只取三个 screenshot 键，遇到 `[section]` 就停（profile 不算默认值）。跑 mpv 时仍用 `--no-config`，用户的脚本与滤镜不进来。
- 截图给 mpv 自己做：`set_property screenshot-directory <绝对目录>` + `screenshot video`，落盘后核对真实文件名。模板含 `%p` / `%n` 这类运行期占位符时预计算不了，退回「目录里最新的写入」。
- `%f` = 带扩展名的文件名，`%F` = 去掉扩展名，`%%` = 字面百分号。**这两个语义是拿真实 mpv 实测的**，别照记忆改。
- 列表的「已完成」用同一份规则算目标路径，算不出来（运行期占位符）就不做完成标记；没读到 mpv 配置时退回 `<录像目录>/screenshots/%F`。

## 断点续做与非常帧率

- `.state.json` 记「上次的文件 + 位置」，打开/定位/截图/切换/退出都写；只在记录就是当前文件且位置在片内时才跳回去（`Core/ResumePolicy`），片尾附近不续。
- **别用 `r_frame_rate != avg_frame_rate` 判 VFR**：mkv 里真变帧率文件也会两者都报 60/60（2026-11-10 实测）。现在是采样开头 5 秒的包时间戳（`ffprobe -read_intervals %+5`），排序后看相邻间隔相对中位数的偏离，容差 `max(2.5ms, 25%)` —— 时间基 1/1000 时 1/60 会在 0.016/0.017 跳，容差去掉就会把 CFR 全判成 VFR。
- 判为 VFR 后逐帧按 `1/fps` 秒定位，界面提示「非常帧率：按时间步进」。
- mpv 退出与 IPC 断开都会触发自动重启并回到原位置；退出流程里要压住这个重启（`_shuttingDown` / `_disposing`）。

## 奖杯片段识别

- 判据是**平台固定文案**（`得奖杯`/`トロフィー`/`Trophy`），不按游戏做模板；角落裁剪右上角 `x≥48%W, y≤20%H`。`赢` 会被 OCR 认成 贏/羸/嬴，所以中文只匹配后三字。用 Windows 自带 `Windows.Media.Ocr`，引擎按 `zh-Hans-CN → ja → en-US` 顺序试、命中即停；已覆盖中文（影之刃0、恶魔城、共鸣）、日文（FF14），以及带玩家 ID 与带成就名的两种横幅布局。
- 先扫片尾 30 秒关键帧（0.5s）+ 最后 10 秒 2fps 逐帧（1.3s，堵住结尾落在最后一个 GOP 里的漏洞），命中即收；没命中才全片关键帧粗扫（开 `-hwaccel auto`）→ 命中点 ±5s 按 5fps 精定位；两级都没命中才退到片尾 60 秒 2fps 兜底。GPU 只用在关键帧粗扫（93 帧 7.8s → 3.5s），逐帧抽样开硬解反而更慢（每帧要回读显存）；mpv 播放侧固定 `--hwdec=auto-safe`，实测 `d3d11va`、丢帧 0。取帧时间用 ffmpeg `showinfo` 的 `pts_time`（`-v info` + 带 seek 时加 `-copyts`），**不要**用关键帧序号对齐 ffprobe 时间表，会错位。
- 切段：按文案分组 → <1.5 秒的组并邻居 → `MergeDuplicates`（时间重叠，或标题字符集 Jaccard ≥0.55 且间隔 ≤5 秒）→ **`KeepLastTrophy` 只保留最后一枚**（用户 2026-11-10 定：多条也只跳最后那个），并把与它间隔 ≤1.5 秒的段并进来兜住半段。
- 缓存 `%LOCALAPPDATA%\TrophyShots\trophies.json`，键 = 路径 + 大小 + 修改时间，带 `DetectorVersion`（改算法就升号，旧的「未检出」自动失效）。**不写录像目录。**
- 性能：5 分钟 141 Mbps 素材 ≈3 秒/条；`恶魔城` 27 条 48 秒、`影之刃0` 36 条 1 分 49 秒，均 100% 识别。

## 目录选择

- 默认用系统文件夹对话框。`--picker builtin` 切到自带的 Avalonia 选择器（`Views/DirectoryPickerWindow`）。
- 本机 MacType 的 GDI hook 会让系统文件夹对话框把进程崩在 `gdi32full.dll`（BEX64 / `c0000409`）。判断方式是看进程里有没有 `MacType64.dll`，但**注入是随对话框一起进来的，点按钮前查不到**，所以不能拿它做预判；只能靠 MacType 的排除列表或 `--picker builtin`。

## 已踩过的坑

- **Avalonia 的 `NativeControlHost` 要求 `app.manifest` 里有 `<compatibility>` 的 `supportedOS`**，否则创建子窗口时直接抛异常。DPI 声明也写在同一份清单里。
- `NativeControlHost.CreateNativeControlCore` 的 parent 是 Avalonia 自己的 wrapper 窗口；宿主子窗口的尺寸要乘 `TopLevel.RenderScaling` 换算成设备像素，否则高缩放下视频只占一角。
- **点视频区会丢按键**：mpv 的嵌入窗口是 disabled + `WS_EX_NOACTIVATE`，焦点落进我们的宿主窗口。`VideoHostWindow` 在 `WM_SETFOCUS` 里把焦点弹回 `GetAncestor(GA_ROOT)`。
- **交给 mpv 的路径必须绝对**：mpv 的工作目录是它自己所在目录，相对路径会写到 mpv 目录下并失败。
- **连发 `frame-back-step` 会丢请求**（10 条只退 1 帧）。每步等 `time-pos` 真的变了再发；批量回退将来若要提速，用一次精确 seek 代替循环。
- **首帧之前 `width` / `estimated-frame-number` 是 `unavailable`**（读到就是 null），读取前先等首帧。另外 `estimated-frame-number` 是时间推算的估算值，判断「是不是同一帧」要用 mpv 截图哈希比对。
- 判「画面真的画出来了」用 `PrintWindow(PW_RENDERFULLCONTENT)`；GDI 抓屏读不到 D3D 内容。真实夜间素材非黑比例只有几个百分点，阈值按 0.5% 量级取。
- **Avalonia 的 `NativeControlHost` 在可见性/布局变化时会重建宿主窗口**，mpv 会继续往已销毁的旧窗口画，表现为「有声音没画面」。必须在重建时把播放器重挂到新句柄；排查时把 mpv 的 `warn`/`error` 日志经 `request_log_messages` 转发进应用日志。
- 列表缩略图用 ffmpeg 在片头 10% 截一帧、176 宽、缓存到 `%LOCALAPPDATA%\TrophyShots\thumbnails`（键含大小与修改时间），并发 3。
- `.NET 10` 的 `dotnet new sln` 生成 `TrophyShots.slnx`，后续命令都用这个名字。

## 自检模式

实现：`src/TrophyShots.App/Services/SelfTestRunner.cs`。

`basic`（列表/缩略图/步进/截图/汇总/键盘/焦点）、`empty`（空状态不是黑屏）、`playback`（播放中画面与丢帧）、`trophies`（奖杯识别与跳转）、`resume-a` + `resume-b`（续做两段）、`vfr`、`broken`、`restart`、`probe`（真实素材只读）、`hold`。

调用方式与夹具生成命令见 `AGENTS.md` 的「常用命令」。
