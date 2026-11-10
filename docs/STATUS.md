# 进度记录

## 当前主线

M2 完成：断点续做、非常帧率退化步进、损坏文件跳过与末尾汇总都落地，播放器挂掉会自动重启并回到原位置。界面仍是 Avalonia 12.1.1 + CommunityToolkit.Mvvm，mpv 走 `NativeControlHost` 嵌入，截图落点跟随用户 mpv 配置（`F:\Video\截图` / `%f`）。

自检：`basic` 15/15、`resume-a` 1/1 + `resume-b` 2/2、`vfr` 3/3、`broken` 2/2、`restart` 3/3；模块测试 42（Core 36 + Player 6）。

下一步 M3：打包免安装 exe（自带 mpv 与 ffprobe）、干净机器验证；M2 余下的真机手工验收（40 条真实素材跑一遍、快捷键手感、45 分钟连续处理的内存曲线）。

## 2026-11-10 应用图标

- 新增 `src/TrophyShots.App/Assets/app.png`（1024，窗口图标）与 `app.ico`（16/24/32/48/64/128/256 多尺寸，exe 图标）。设计：深色圆角底 + 金色奖杯 + 白色取景框角标。生成脚本在 `work/make_icon.py`（PIL 绘制，4 倍超采样后缩）。
- `TrophyShots.App.csproj` 加 `ApplicationIcon=Assets\app.ico` 与 `AvaloniaResource Include="Assets\**"`；`MainWindow` 与 `DirectoryPickerWindow` 用 `Icon="/Assets/app.png"`。
- 验收：`ExtractAssociatedIcon` 从 exe 取到 32×32 图标（内容正确）；`--selftest empty` 3/3、`--selftest basic` 16/16 仍全绿（资源缺失时 Avalonia 建窗会直接抛异常，两轮都正常建窗即证明资源已打进程序集）。

## 2026-11-10 反馈修复：侧栏与状态栏重叠、列表缩略图、播放黑屏排查

- **重叠**：侧栏 Border 原来跨了两行（`RowSpan=2`），把底部状态栏盖在自己身上，侧栏最后一行「已处理 x / n」正好落在状态栏底下。改成侧栏只占第一行、状态栏独占第二行。
- **缩略图**：新增 `ThumbnailService`，用 ffmpeg 在片头 10% 处截一帧、缩到 176 宽，缓存到 `%LOCALAPPDATA%\TrophyShots	humbnails`（缓存键含文件大小与修改时间，换片自动重生成），并发 3 后台生成；列表项左侧 84×48 预览，没生成时显示「预览」占位。`--selftest basic` 增加 `thumbnail-generated` 检查。
- **黑屏**：用反馈里的目录（`F:\Video\影之刃0`，20 条 H.264 High 1440p60、约 141 Mbps）在三个层面都没复现：只读探针暂停帧 85.2% 非黑、播放中抓屏 6 个采样都是 84.8%、播放中改窗口尺寸 77.6%；再用屏幕 BitBlt 抓取确认画面是活的（亮度 108–188 在变）。
- 没复现但确实存在的合理隐患：Avalonia 的 `NativeControlHost` 在可见性/布局变化时会重建宿主窗口，而 mpv 还在往已销毁的旧窗口画——表现正是「有声音没画面」。现在宿主窗口重建会自动把播放器重挂到新句柄，日志里记 `宿主窗口 0x…（上一个 0x…，重接=）`。
- 本轮补了两处可观测手段：mpv 自己的 warn/error 日志转发进我们的日志（`mpv: …`），以及 `--selftest playback`（播放 3 秒 + 播放中改尺寸，记录非黑比例、`frame-drop-count`、`decoder-frame-drop-count`、`vf-fps`、`hwdec-current`）。
- 若新版本仍然黑屏，日志里会有 mpv 的具体报错与丢帧统计；另外需要知道当时窗口是否最大化。

## 2026-11-10 UI 对齐：跟随系统主题、卡片式布局、空状态不再是一块黑

- 问题：初版把主题硬写成 Dark、还自造了一套不透明黑底，开机右侧是一整块纯黑，和 `FFXIVSubmarineLedger` 的观感完全不同。
- 改法：`RequestedThemeVariant="Default"`（跟随系统，本机是浅色）；去掉写死的深色底，改用半透明色块（侧栏 `#0E808080`、卡片 `#0A808080`、边框 `#40808080`、主按钮 `#285B8DEF`）和它的 nav / card / section-title 样式词汇；左侧改成「品牌 + 操作 + 列表 + 进度」的侧栏，空状态给占位文案。
- mpv 的原生窗口改成「有录像列表时才创建」（`IsPlayerVisible`）——原生窗口永远盖在 Avalonia 之上，留着它占位文案就会被吃掉。播放器也改成宿主窗口就绪后再拉起，打开录像会等它就绪（`--directory` 的打开动作挪到窗口显示之后）。
- 验收：`--selftest empty` 3/3（整窗平均亮度 240.7，旧版是暗底）；`basic` 15/15、`resume` 1/1 + 2/2、`vfr` 3/3、`restart` 3/3 全部照旧通过。
- 边界：没做应用图标（ledger 有 `Assets/app.png`）；配色对齐的是它的做法，没有逐像素比对。

## 2026-11-10 M2：断点续做、非常帧率退化、错误处理与末尾汇总

**目标**：PLAN §10 的 M2——快捷键完整、断点续做、错误处理、进度显示。

**断点续做**

- `.state.json` 记「上次处理到哪个文件 + 位置」，打开、定位、截图、切换、退出都会写（原子替换，写坏也不会读崩）。
- 打开时只有「记录的就是这条录像、位置还在片内」才跳回去（`ResumePolicy`，尾部和接近 0 的位置都从头开始）。
- 验收拆成两段跑：`--selftest resume-a` 定位到 2.5 s 并退出，`resume-b` 重新启动后读回 `00:02.500`。

**非常帧率（VFR）**

- **PLAN 里「`r_frame_rate` ≠ `avg_frame_rate` 即 VFR」这条判据在 mkv 上不成立**：实测造了一个真变帧率文件（PTS 间隔 0.017/0.033/0.083/0.100 跳动），ffprobe 依然同时报 60/60。改用采样：读开头 5 秒的包时间戳（`-read_intervals %+5`，单个 58 GB 文件约 0.15 s），排序后看相邻间隔偏离中位数的比例。
- 容差必须留出时间戳量化余量：时间基 1/1000 时 1/60 会在 0.016/0.017 之间跳，按 5% 相对误差判会把所有 CFR 素材误判成 VFR（踩过）。现在用 `max(2.5 ms, 25% × 中位数)`。
- 判为 VFR 后，逐帧改成按 `1/fps` 秒定位，界面标「非常帧率：按时间步进」。

**错误处理与汇总**

- 损坏文件（ffprobe 读不出、或首帧 20 秒不出来）标红、不自动选中、`截图并下一条` 跳过，不静默卡死；磁盘写入失败照旧显式报错并写明目录。
- mpv 异常退出：进程退出与 IPC 连接关闭两条信号都会触发自动重启，回到原录像原位置（`restart` 自检：kill 后 0.9 s 内换新 pid，帧号回到 2.5 s 处）。顶栏另有「重启播放器」按钮。
- 没有待处理条目时弹出汇总窗口（完成 / 出错 / 未处理三类计数加名单，自带 Avalonia 窗口）；顶栏「查看汇总」可随时看。

**顺手修掉**：探测原来会用播放器的取消令牌（切换录像就打断，结果变空）→ 扫描用独立令牌；`--directory` 被扫两遍 → 等自动打开完成；探测晚于打开时选中项的步进方式没跟上 → 探测完成时补正。

**边界**：`--hwdec` 仍固定 `no`；45 分钟连续处理与内存曲线、40 条真实素材整批跑、快捷键手感仍是手工项。

## 2026-11-10 M1 收尾：截图规则跟随 mpv 配置，目录选择与 MacType 冲突解决

**背景**：用户实测 M1 后提了两点——截图文件名要按他自己 mpv 的设置来；另外点「选择录像目录…」会直接崩。

**截图规则改为读 mpv 配置**

- 启动时读 `portable_config/mpv.conf`（其次 `%APPDATA%\mpv\mpv.conf`），只取 `screenshot-directory` / `screenshot-template` / `screenshot-format`，其他设置不碰（工具跑 mpv 仍用 `--no-config`，用户的滤镜与脚本不会混进来）。实测读到：`F:\Video\截图`、`%f`、`png`。
- `%f` / `%F` 的语义由真实 mpv 实测确认（不是猜文档）：`%f` = 带扩展名的文件名，`%F` = 去掉扩展名。用户的 `%f` 产出 `Replay 2026-10-22 00-01-37.mkv.png`。
- 截图走 mpv 自己的 `screenshot video`（模板、格式、目录都给 mpv），落盘后再核对实际文件名；模板里出现 `%p`/`%n` 这类运行期占位符时无法预计算，改用"目录里最新的写入"定位。
- 列表的「已完成」用同一份规则判定，所以现在指向 `F:\Video\截图`；没读到 mpv 配置时仍退回 PLAN 里的默认 `<录像目录>/screenshots/<主文件名>.png`。
- 副作用（用户已知）：所有游戏的截图集中在一个文件夹，同名录像会互相覆盖。

**选目录崩溃：MacType 的 GDI hook**

- 现象：点「选择录像目录…」后进程直接消失，Windows 事件日志 `BEX64`，故障模块 `gdi32full.dll`，异常 `c0000409`，异常偏移 `0x1f211`。
- 取证：WER 报告的模块列表里有 `MacType64.dll`、`Easyhk64.dll`、`MacType64.Core.dll` —— 与本机 2026-11-01 记的那批 MacType 崩溃同一签名。崩的是 shell 文件夹对话框（用 GDI 画字）；工具自己的界面走 Skia，不受影响。
- 另一条实测事实：窗口显示后、点按钮前，进程里查不到任何 MacType DLL，它是**跟着对话框一起进来**的。所以"启动时检测注入"不可靠，不能用作预判。
- 处理：默认仍用系统对话框（用户要求），另加 `--picker system|builtin|auto`；`builtin` 是自带的 Avalonia 目录选择器（回车/双击进目录、可直接粘路径），不经过 shell 对话框。用户把 `TrophyShots.exe` 加进 MacType 排除列表后，系统对话框恢复正常（实测：弹出→取消→无崩溃）。
- 附带产出：`src/TrophyShots.Core/FolderBrowsing.cs`（列子目录/上一级/磁盘，自带选择器与测试共用）。

**验收**：`dotnet test` 28/28；`--selftest basic` 14/14（截图落盘名 `Replay 2026-10-22 00-01-37.mkv.png`，与 mpv 手动截一致）。

**没做的**：`--hwdec` 仍固定 `no`（用户配置是 `auto-safe`，未跟随）；45 分钟连续处理与内存曲线仍未测。

## 2026-11-10 M1：Avalonia 界面、目录扫描、列表、时间轴、截图与自动下一条

**本轮目标**：把 M0 验证过的架构落成能用的界面，前端按用户要求"直接用框架"——对齐 `FFXIVSubmarineLedger` 的技术基线（Avalonia、MVVM、分层、模块测试）。

**结构**（`TrophyShots.slnx`，全部 `net10.0`）

- `src/TrophyShots.Core`：目录扫描、自然排序、`screenshots/` 与 `.state.json` 的路径约定、状态读写。纯逻辑，无 UI、无进程。
- `src/TrophyShots.Player`：mpv 子进程与 Job object、命名管道 JSON IPC、逐帧（含"每步确认 time-pos"）、ffprobe 元数据、`PrintWindow` 抓屏；另有 `VideoHostWindow`（交给 `--wid` 的宿主子窗口，负责跟随尺寸与焦点回弹）。
- `src/TrophyShots.App`：Avalonia 12.1.1 + CommunityToolkit.Mvvm，`MainWindowViewModel` 持有列表与状态，`MpvHostControl : NativeControlHost` 把 mpv 画面塞进布局。
- `tests/TrophyShots.Core.Tests`：xunit.v3，17 个用例（自然排序、扩展名过滤、截图路径、状态读写与坏文件自愈）。

**功能**：左侧列表（文件名 / 时长 / 完成状态）、顶部 `已处理 x / n` 与目录、时间轴滑块（松手才定位）、±1/±10 帧、±1 秒、播放暂停、截图并下一条、重截当前、状态栏提示；快捷键 ←/→、Shift+←/→、`,`/`.`、空格、`S`/`Enter`、`Backspace`、↑/↓。

**验收证据（2026-11-10）**

- `--selftest basic`（`--offscreen`，窗口在屏幕外）：14/14 通过。含列表条数与自然序、首帧、视频区抓屏非黑 86.7%、+1 帧、1 秒处帧 60、截图落盘 640×360、列表标记完成、进度文本、自动切下一条、状态文件、快捷键映射、焦点回弹。
- `--selftest probe`（真实素材 `F:\Video\FF共鸣\Replay 2026-10-22 00-01-37.mkv`，只读）：5/5 通过。首帧 0.7–2.1 s，定位到 150 秒后帧号正好 9000（60 fps），+1 帧生效，回到 00:00.000。
- `dotnet test`：17/17。真实 1440p60 视频区抓屏非黑 8.8%（夜间素材，正常）。
- 退出后无残留 `mpv` 进程（Job object 兜底仍在）。

**新踩的坑**

1. **Avalonia 的 `NativeControlHost` 需要 `app.manifest` 里声明 `supportedOS`**，缺了会直接抛「Unable to create child window for native control host」。加 `<compatibility>` 段即可，和 DPI 声明写在同一个清单里。
2. `NativeControlHost.CreateNativeControlCore` 拿到的 parent 是 Avalonia 自己建的 wrapper 窗口，我们的宿主窗口要作为它的子窗口创建，尺寸按 `RenderScaling` 换算成设备像素，否则 125% 缩放下视频只占一角。
3. 点视频区会让我们自己的宿主窗口拿到焦点（mpv 的窗口是 disabled + `WS_EX_NOACTIVATE`），所以在 `WM_SETFOCUS` 里立刻 `SetFocus(GetAncestor(GA_ROOT))` 弹回主窗口，否则按键全丢。
4. 自检不能靠"投递 WM_KEYDOWN"验证快捷键：自检窗口不激活，框架按未激活丢掉按键。改成直接调用 `MainWindow.HandleShortcut`（真实映射代码）+ 单独验证焦点回弹；操作系统到 Avalonia 的那一跳留给真机人工确认。
5. `xunit.v3` 没有隐式 using，测试文件要自己 `using Xunit;`；.NET 10 的 `dotnet new sln` 生成的是 `TrophyShots.slnx`。

**边界（本轮未验证）**

- 没做 45 分钟连续处理与长时间内存曲线（PLAN §9.6）。
- 没有 VFR 的实际退化步进，只在列表里标出"非常数帧率"。
- 状态文件只写不读回位置（选择文件用的 `LastFileName` 已生效），续做位置留 M2。
- 没测损坏文件跳过与末尾汇总报告（M2 的错误处理项）。
- 未打包，未做干净机器验证（M3）。

**下一步**

1. M2：读回 `LastPositionSeconds` → 打开即定位到断点；VFR 按时间戳步进；错误文件标红跳过 + 末尾汇总。
2. 真机上手工过一遍快捷键手感（自检只覆盖到映射层）。
3. 决定 mpv 与 ffprobe 的随包分发形态（M3 前把版本固定下来）。

## 2026-11-10 M0：用 WPF 嵌 mpv、打通 IPC，验证渲染、键盘与退出

**目标**：验证 `docs/PLAN.md` §5 的架构假设——原生窗口嵌 mpv、控制走 IPC——是否成立，再决定 UI 框架。

**当时的结论（框架部分已被 M1 取代，架构结论仍有效）**

- 视频内核确认：mpv 0.32.0（Scoop），`--wid` 嵌入，控制全走 `--input-ipc-server` 的命名管道 JSON IPC，mpv 侧关掉自带键位与 OSC。
- 当时用 C#/WPF 验证通过；M1 按用户要求换成 Avalonia，`--wid` 与 IPC 的做法原样沿用，`MpvProcess` / `MpvIpc` 代码直接搬到 `src/TrophyShots.Player`。
- M0 的验证方式：`--selftest` 在进程内驱动真实代码路径，窗口放屏幕外，不抢前台窗口、不动鼠标。

**M0 实测证据（当时的 WPF 宿主，两个素材各 14/14）**

- 解码元数据：合成片 640×360 / 60fps，真实片 2560×1440 / 60fps。
- 帧身份用 **mpv 自己截图的 SHA256** 比对：真实片 frame 1800 → +1 → 1801（哈希变）→ −1 → 1800（哈希完全一致）；+10 → 1810 → −10 → 1800（一致）。
- 单步延迟：命令发出到 `time-pos` 变化 2.7–4.4 ms（PLAN §9.7 要求 < 100 ms）。
- 缩放：窗口 1306×722 → 986×602 DIP 时 `osd-dimensions` 1632×903 → 1232×753 跟随。
- 退出：IPC `quit` 后 88–162 ms 退出；`Stop-Process -Force` 强杀主进程后 mpv 立即归零。

**M0 修掉的三个坑（现在仍在用）**

1. 连发 `frame-back-step` 会丢请求（10 条只退 1 帧）→ 改逐步确认 `time-pos`。
2. mpv 的工作目录是它自己所在目录，相对路径会写错地方 → 交给 mpv 的路径一律转绝对。
3. 首帧之前 `width` / `estimated-frame-number` 是 `unavailable` → 先等首帧再读。

**当时未覆盖**：硬解（全程 `--hwdec=no`）、多文件连续处理、打包、烧帧号人工目视核对、GitHub 远端。

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
