# deskhand（观行）· W0.5

给 DSH 加眼睛和手的最小闭环：**截屏 → 模型看图 → 点击/键入 → 再截屏回看**。
W0.5 只服务本机、单屏（主屏）、前台窗口；不做 UIA、窗口管理、多屏拼接与审批弹窗。

## 为什么需要 broker

DSH 从插件里直接 spawn 的子进程会被 DSH 沙箱降到 **Low 完整性**（实测父进程 Medium、子进程 Low），
Low 进程的 `SetCursorPos` 会被拒绝（Win32 1150），`SendInput` 被静默丢弃。
所以「手」改由一个**普通用户权限的 broker** 执行：watcher 跟随 DSH 启停，broker 监听 127.0.0.1 随机端口，
插件通过 `data/broker.json` 里的端口 + token 连接。眼睛（截屏）不受完整性限制，broker 不在时也能直接用 helper。

## 能力

| 工具 | 作用 |
|---|---|
| `desk_state` | 屏幕物理分辨率、鼠标坐标、前台窗口（标题/类名/进程）、执行者完整性 |
| `desk_capture` | 截主屏（可带 `region` 裁剪），返回图片 + 缩放换算说明 |
| `desk_click` | 绝对坐标点击（左/右/中，1–3 次） |
| `desk_type` | 向焦点窗口输入文本（剪贴板 + Ctrl+V，支持中文） |
| `desk_key` | 组合键，如 `ctrl+c`、`alt+tab` |
| `desk_scroll` | 滚轮 |
| `desk_uia` | 前台窗口的 UIA 控件树（ref/名称/类型/坐标/可用动作） |
| `desk_uia_act` | 按 ref 做 invoke / setvalue / toggle / select / expand / collapse / focus |
| `desk_windows` | 列出可见顶层窗口（标题/进程/坐标/是否黑名单） |
| `desk_activate` | 按标题或 pid 把窗口带到前台（黑名单目标拒绝） |
| `desk_drag` | 从 (x1,y1) 拖到 (x2,y2)，支持左/右/中键与插值步数 |
| `desk_clipboard` | 读写/备份/恢复/校验剪贴板文本（get 只读；set/restore 属输入动作） |
| `desk_browser_tabs` | 列出暴露 CDP 端口的浏览器标签页（需 `--remote-debugging-port=9222`） |
| `desk_probe` | 诊断：broker 是否可达 |

## 启动（跟随 DSH）

一次性安装启动项（当前用户，普通权限）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-startup.ps1
```

之后每次登录：`deskhand.exe watch` 随启动项运行 → 发现 DSH 在跑就拉起 `serve` 即 broker → DSH 退出就关掉 broker。
临时手动启动：双击 `scripts\start-broker.cmd`。

## 使用中提示

broker 每次执行截屏/点击/输入/按键/滚轮时，会在**主屏右下角**点亮一个 always-on-top、鼠标穿透的小角标「● agent 正在使用桌面」，空闲 5 秒后自动隐藏。用于避免人和 agent 同时抢用桌面。角标不抢焦点、不进 Alt-Tab、不挡点击。

## 审批（按轮）

输入类动作（`desk_click` / `desk_type` / `desk_key` / `desk_scroll`）不再是逐次审批，而是**按一轮操作**审批：

1. 调 `desk_begin`，用一句人话写清本轮任务（例如「把浏览器里的表格整理到 Excel」）；
2. 审批卡显示**任务描述 + 当前前台窗口名**（不写坐标），批准一次即可；
3. 批准后本轮内（默认 10 分钟，`roundMinutes` 可配）的所有输入动作不再弹卡；
4. 任务结束调 `desk_end` 撤销本轮；超时也会自动失效。

未开始轮次时，输入动作会直接报错提示先 `desk_begin`。截屏只读，不弹审批。审批记录写在 `data/approval.log`。

## 黑名单

`data/blacklist.json`（broker 启动时自动生成默认值）：

```json
{
  "processes": ["RVLSession"],
  "titles": ["独享桌面"],
  "classes": []
}
```

前台窗口的**进程名 / 标题 / 窗口类**任一命中即拒绝所有输入动作（点击/输入/按键/滚轮），错误里会写明命中的应用名。默认已把「独享桌面」远程桌面（进程 `RVLSession`）列为不可操作；该文件修改后约 5 秒内生效，不需要重启。

## UIA（W1 进行中）

- `desk_uia` 导出前台窗口的控件树：`ref`（路径，如 `0.2.1`）、名称、控件类型、坐标、是否可用、可用动作（invoke/value/toggle/select/expandcollapse/scrollitem）。
- `desk_uia_act` 按 `ref` 执行语义动作，属于**输入动作**：需要先 `desk_begin` 过审批轮，并受黑名单限制。支持改用 `name`（子串匹配、只匹配可用且支持该动作的元素）定位，`ref` 失效时更稳；解析失败会自动重试一次（300ms）。
- 限制：只看**本地**窗口；「独享桌面」远程桌面内部控件不可见（它只是一个窗口）；界面变化后 `ref` 失效，需重新 `desk_uia`。控件树只做控制视图（`ControlViewWalker`），最多 400 节点/深度 12/2.5 秒，超出会标记 `truncated`。

## 耳朵（麦克风 + WAV 文件）

| 工具 | 作用 |
|---|---|
| `desk_audio_info` | 列出可用语音识别器、麦克风是否可用 |
| `desk_hear` | 麦克风听写最近 N 秒（1–30，默认 5），返回中文识别文本；需先 `desk_begin` 过审批轮 |
| `desk_transcribe` | WAV 音频文件转文字（中文识别器）；需先 `desk_begin` 过审批轮 |

实现：Windows 自带 `System.Speech` 中文识别器（本机 `MS-2052-80-DESK`，zh-CN），不联网、不引第三方模型。限制：只支持 WAV（PCM 16k 单声道最佳），其他格式需先转 WAV；系统正在播放的声音（WASAPI loopback）暂未接入；实时流式转写不做。

### 嘴巴（规划）

用 Windows 自带 SAPI 朗读文本（`System.Speech.Synthesis`，本机有 Microsoft Huihui 中文语音），不联网；与听写一样走按轮审批、写审计。图标与中文名「观行」已按「看·动·听·说」四件事设计（取自「听其言而观其行」）。

## W1 输入补齐

- `desk_windows` 列窗口（含 `blacklisted` 标记），`desk_activate` 前置目标窗口（黑名单目标直接拒绝）；
- `desk_drag` 拖拽（起点/终点/按键/插值步数，起点会校验光标是否真的移动）；
- `desk_clipboard` 读写剪贴板文本（`get` 只读；`set` 属输入动作，走按轮审批）；另支持 `backup`/`restore`/`verify`（备份到文件、恢复、只比对的校验，用于测试时不动用户内容）。
- 以上输入类动作与点击一致：需 `desk_begin` 过审批轮、受黑名单限制、写审计。

## 验收状态（2026-11-12）

已实机验证：

- 截屏、坐标点击、键入、按键、按轮审批、黑名单（「独享桌面」被拒）、右下角「使用中」角标；
- `desk_windows` 列表与黑名单标记；`desk_uia` 控件树（261 节点合法 JSON）；`desk_uia_act` 展开「应用」菜单（截图确认）；
- 耳朵链路：`desk_audio_info`（zh-CN 识别器 + 麦克风）、`desk_transcribe` 返回文本。

未实测 / 明确挂起：

- `desk_drag`、`desk_clipboard`（代码已装，未做破坏性验证）；
- 麦克风真实语音、音频文件真实语音验收（人类挂起）；
- 多屏与不同 DPI（人类决定不做）。

已知坑：UIA 的 `ref` 在界面变化或审批卡开合后会失效，需要重新 `desk_uia` 再操作；远程桌面窗口内部控件不可见。

## 结构

- `index.js`：Host 插件，只注册工具；截图经 `ctx.attachments.saveImage` 变成可回传的图片块。
- `helper/deskhand.cs` → `helper/deskhand.exe`：唯一碰 Windows API 的地方（GDI 截屏、SendInput 注入、剪贴板）。
  - `serve`：broker，监听 127.0.0.1 随机端口，token 鉴权，命令用 tab 分隔。
  - `watch`：跟随 DSH 启停 broker。
- `data/`：`broker.json`（端口 + token）、`audit.jsonl` 审计、`shots/` 截图（保留 `maxShots` 张）、`STOP` 文件可冻结所有输入动作。

## 构建与检查

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
node scripts\aecw-selfcheck.mjs
aecw verify
```

### 完整性标签（重要）

helper 是在 DSH 沙箱（Low 进程）里编译的，产物 `deskhand.exe` 会带 **Low 完整性标签**；Windows 会按镜像文件的标签把进程限制在 Low——这时 broker 的 `SetCursorPos`/`SendInput` 会被静默拒绝，即使启动它的父进程是 Medium。

所以：**每次重新编译 helper 之后，必须再跑一次 `scripts\install-startup.cmd`**（它以 Medium 运行，会执行 `icacls ... /setintegritylevel Medium`，再重启 watcher）。核对：

```powershell
icacls helper\deskhand.exe   # 应显示 Mandatory Label\Medium Mandatory Level
```

另外不要用 WMI `Win32_Process.Create` 拉起 watcher——实测它会把子进程降成 Low。用 `Start-Process` 或启动项。


## W0.5 已知边界

- 只有主屏；多屏拼接、窗口精确裁剪在 W1。
- 无 UIA：点按钮靠坐标，不做控件树定位。
- 无审批闸门：工具一装即可用，靠会话权限与 STOP 文件兜底；W1 接 approval。
- broker 的 token 存在用户可读文件里，同用户进程均可读；W1 收紧。
- 截图是全屏物理像素（本机 3120×2080），附件服务会再归一化，返回文本里给出换算系数。
- 提权窗口（UIPI）、安全桌面（UAC/锁屏）不可操作，这是 OS 边界，不是实现缺口。
