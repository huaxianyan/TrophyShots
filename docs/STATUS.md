# 进度记录

## 当前主线

M0（mpv 嵌入 + IPC）已完成：WPF/.NET 8 原生窗口用 `--wid` 嵌入 mpv，JSON IPC 打通逐帧、截图、缩放跟随、键盘与退出清理，真实 1440p60 素材与合成测试片各 14/14 自检通过。框架选型定稿为 WPF，不再考虑 JavaFX / Qt。

下一步 M1：目录扫描 + 列表 + 时间轴 + ±1 帧 + 截图落盘 + 自动下一条。开工前先把 `--selftest` 固化成可回归的验收命令（见 `AGENTS.md` 文档索引）。

## 2026-11-10 M0：用 WPF 嵌 mpv、打通 IPC，验证渲染、键盘与退出

**目标**：验证 `docs/PLAN.md` §5 的架构假设——原生窗口嵌 mpv、控制走 IPC——是否成立，再决定 UI 框架。

**定稿结论**

- UI 框架：**C# / WPF / `net8.0-windows`**（本机 SDK 8.0.414 可用，零 NuGet 依赖）。嵌窗用 `HwndHost` 自建宿主窗口，再把它交给 mpv 的 `--wid`。
- 视频内核：mpv 0.32.0（Scoop）。控制全部走 `--input-ipc-server` 的命名管道 JSON IPC，mpv 侧关掉自带键位与 OSC。
- 代码在 `src/TrophyShots/`：`Mpv/MpvProcess`（拉起与退出）、`Mpv/MpvIpc`（管道协议）、`Mpv/MpvHost`（HwndHost）、`Capture/WindowCapture`（抓屏取证）。

**验证方式**：`--offscreen --selftest basic|focus|hold` 在进程内驱动真实代码路径，窗口放屏幕外，不抢前台窗口、不动鼠标。合成素材用烧帧号的 `testsrc2` 60fps 片（命令见 `AGENTS.md`），真实素材用 `F:\Video\FF共鸣` 里的 2560×1440 60fps mkv。

**实测证据（2026-11-10，两个素材各一轮，均为 14/14 PASS）**

- 解码元数据：合成片 640×360 / 60fps，真实片 2560×1440 / 60fps，`container-fps` 都是 60.000004。
- 渲染：`PrintWindow(PW_RENDERFULLCONTENT)` 同时抓整窗与 mpv 子窗口，视频区非黑比例 96.6%（合成片）/ 9.9%（真实夜间素材），画面确实在图。
- 帧身份用 **mpv 自己截图的 SHA256** 比对，不依赖估算帧号：真实片 frame 1800 → +1 → 1801（哈希变）→ −1 → 1800（哈希完全一致）；+10 → 1810 → −10 → 1800（哈希一致）。
- 单步延迟：命令发出到 `time-pos` 变化 2.7–4.4 ms（PLAN §9.7 要求 < 100 ms）。
- 截图：`screenshot-to-file <绝对路径> video` 落原分辨率 PNG，合成片 640×360 / 66 KB，真实片 2560×1440 / 1.17 MB。
- 缩放：窗口 1306×722 → 986×602 DIP 时 `osd-dimensions` 从 1632×903 跟到 1232×753，画面继续正常渲染。
- 键盘：activate 后给主窗口投递 `WM_KEYDOWN`，WPF `PreviewKeyDown` 接住并走 IPC，帧 11 → 12（29–41 ms 含确认帧号）；focus 模式合计 18/18 PASS。
- 退出：IPC `quit` 后 mpv 88–162 ms 退出；`Stop-Process -Force` 强杀主进程后 mpv 立即归零（Job object `KILL_ON_JOB_CLOSE` 兜底）。两种路径都没有孤儿进程。

**本轮踩到并已修掉的三个坑**

1. **连发 `frame-back-step` 会丢请求**：一次发 10 条只退了 1 帧，`estimated-frame-number` 停在 1809。改成每步确认 `time-pos` 真的变了再发下一条，10 步往返即精确。代价是 10 步约 1.26 s，M2 应改成一次精确 seek 而不是循环回退。
2. **mpv 的工作目录是它自己所在目录**：传相对路径截图会写到 mpv 目录下且失败。所有交给 mpv 的路径必须先 `Path.GetFullPath`。
3. **首帧之前属性是 `unavailable`**：`width`、`estimated-frame-number` 在首帧渲染前读不到，必须先轮询等首帧，否则整条链路读到 NaN。

另外记两条与窗口有关的事实：mpv 0.32 的嵌入窗口是 **disabled + `WS_EX_NOACTIVATE`**，点击视频区的焦点其实落在我们的宿主窗口上（所以要在 `WM_KILLFOCUS` 时把焦点抢回主窗口，否则按键全丢）；`HwndHost` 必须重写 `MeasureOverride` 才会随窗口伸缩。

**边界（本轮没验证）**

- 全程 `--hwdec=no`，硬解路径未测（软件解码在 1440p60 上单步只要 3 ms，是否要开硬解留到 M1 定）。
- 只测了单个文件的加载与步进；45 分钟级连续处理、帧缓冲释放、内存增长未测。
- PLAN §9.1 的"烧帧号人工目视核对"没做（自动化里不读图）。本轮用截图哈希证明了帧身份，视觉级验收留到 M1 交付时一起做。
- 未做打包（M3）、未测 VFR 退化路径、未接 ffprobe。
- 未建 GitHub 远端，只有本地提交。

**下一步**

1. M1：目录扫描 + 列表 + 打开文件 + 时间轴 + ±1 帧 + 截图 + 自动下一条。
2. 把 `--selftest` 变成固定回归命令，M1 起每次改动都跑一遍（`--offscreen` 保证不打扰）。
3. 决定 mpv 版本策略：现在用 Scoop 的 0.32.0，M3 自带时固定版本并写进 `AGENTS.md`。

## 2026-11-10 规划完成，等待开工

用户已确认需求与约束，规划文档写入 `docs/PLAN.md`，本仓库尚无代码。

**已确定的事实**

- 素材：`F:\Video\FF共鸣`，40 个 `.mkv`，合计 58 GB，2560×1440 H.264，**CFR 60fps**，时长 296–300 秒、每文件约 17894 帧，单帧 PNG 约 1.4 MB。
- 用户明确**拒绝 Electron / WebView**，UI 必须原生框架；FFmpeg 系（ffmpeg / libmpv / libvlc）可接受。
- 现有工作流是手动 mpv 逐帧截图，新工具的帧行为必须与之一致。

**实测性能（同机同文件）**

- ffmpeg 精确 seek 取单帧：1.94 s（→ 不能每次步进 spawn 进程）
- ffprobe 全量列举 PTS：72 s（→ 不能逐文件建全量帧索引）
- ffprobe 读元数据：毫秒级

**技术方向**（M0 已验证，见上一条）

- 视频内核 mpv，`--wid` 嵌入原生窗口，JSON IPC 控制逐帧与截图。
- UI 框架定为 WPF / .NET 8。
