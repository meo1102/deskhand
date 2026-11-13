# AGENTS.md

## 项目
- 目的与范围：给 DSH 加「眼睛和手」——截屏让模型看见桌面、用鼠标键盘操作桌面。眼睛走 DSH 附件服务（图片块进模型）；手走普通用户权限的 broker（DSH 插件直接 spawn 的子进程会被沙箱降到 Low，无法注入输入）。W0.5 已交付（截屏、坐标点击、剪贴板输入、按轮审批、黑名单、右下角角标）；W1 已交付：UIA 控件树与按 ref 操作、窗口列表与前置、拖拽、剪贴板读写、按轮审批、耳朵（麦克风 + WAV）；多屏/DPI 按人类决定不做；已实机验证的项见 README「验收状态」，未实测项（拖拽/剪贴板破坏性验证、真实语音）不得宣称通过。
- 主要产物 / 关键入口：
  - `index.js`：Host 插件——注册 `desk_*` 工具（含 `desk_uia` / `desk_uia_act`）、按轮审批、审计与截图保留。
  - `helper/deskhand.cs` → `helper/deskhand.exe`：唯一碰 Windows API 的地方——GDI 截屏、SendInput、UIA 控件树、broker（`serve`）、watcher（`watch`）、右下角「使用中」角标、黑名单。
  - `scripts/install-startup.ps1`：汇编 helper（含 UIA 程序集引用）、把 exe 完整性标签抬到 Medium、写登录启动项、启动 watcher。
  - `scripts/aecw-selfcheck.mjs`：可执行门禁；`README.md`：结构、启动、审批、黑名单、UIA 与边界。

## 约束
- 不变式：
  - 手必须由 **Medium 完整性** 的 broker 执行；broker 只监听 `127.0.0.1` 随机端口，插件凭 `data/broker.json` 的 token 连接。
  - helper.exe 在 DSH 沙箱（Low）里编译会带 Low 完整性标签，Windows 会按标签把进程压在 Low；**每次重编译后必须跑一次 `install-startup.cmd`**（Medium 编译 + `icacls /setintegritylevel Medium`）。
  - 输入动作按轮审批：`desk_begin(task)` 一次获批（默认 10 分钟，`roundMinutes` 可配），轮内点击/输入/按键/滚轮不再逐次弹卡；`desk_end` 主动结束；未开始轮次时输入动作直接报错。审批文案写任务与前台窗口名，不写坐标。
  - 前台窗口的进程名 / 标题 / 窗口类命中 `data/blacklist.json` 即拒绝输入；默认含 `RVLSession` 与「独享桌面」。
  - UIA 动作（`desk_uia_act`）属于输入：同样需要先 `desk_begin` 过审批轮，并受黑名单限制；`ref` 在界面变化后失效，需重新 `desk_uia`（也可改用 `name` 子串定位；解析失败会自动重试一次）；远程桌面窗口内部控件不可见。
  - 拖拽（`desk_drag`）、窗口切换（`desk_activate`）、写剪贴板（`desk_clipboard set`）都属于输入动作：需先 `desk_begin`；`desk_activate` 的目标窗口若在黑名单中直接拒绝；`desk_windows` 会标出每个窗口是否黑名单。
  - 审计写 `data/audit.jsonl`，审批写 `data/approval.log`，截图落 `data/shots/` 并滚动保留，`data/STOP` 冻结所有输入。
  - watcher 常驻，随 DSH 启停 broker；broker 不在时眼睛（截屏）走直接 helper 兜底。
- 禁止与边界：不操作「独享桌面」远程桌面；不碰安全桌面（UAC/锁屏）与提权窗口；不做无人值守；不引入外部依赖或联网；不读取其他插件 DOM；不改 profile 的 `package.json` / `cordis.patch.yml`；不把 token 写进日志或文档。
- 耳朵（已实现麦克风 + WAV 文件）：`desk_audio_info`（识别器/麦克风）、`desk_hear`（麦克风听写 1–30 秒）、`desk_transcribe`（WAV 转文字），走 Windows 自带 `System.Speech` 中文识别器（本机 zh-CN），不联网、不引第三方模型；采集/转写属敏感动作，需先 `desk_begin` 过审批轮；WASAPI loopback（系统正在播放的声音）暂未接入，非 WAV 格式需先转 WAV。嘴巴（规划）：用 `System.Speech.Synthesis` 朗读文本（本机 Microsoft Huihui），同样走按轮审批、写审计；图标与中文名「观行」已按「看·动·听·说」设计（取自「听其言而观其行」）。

## 验证
- 可执行检查：`node scripts/aecw-selfcheck.mjs`（包清单、bundle 补丁、locale、图标、helper 可执行位、Host 模块可导入、helper selftest）；交付前 `aecw verify` 必须通过。
- 人工判断：Medium broker 下实机点击/键入/回看是否正常；审批卡文案是否可读；「使用中」角标是否可见且不挡操作；黑名单是否符合预期；`desk_uia` 的控件树对本地窗口是否可读、`desk_uia_act` 是否按 ref 生效；多屏/DPI 与 UIA 之外的 W1 项未覆盖不得宣称。

## 外部参考
- `README.md`：结构、启动、审批、黑名单、完整性标签与已知边界。
- 同工作区插件样例：`../xingzang/`（行藏）、`../已归档会话删除-方案与影响-v0.1.md`。
- DSH Cordis 插件开发 skill 与 `cordis_inspect_query`：Tool/Service/approval 契约。

## AECW
- 本项目受 AECW 治理：宣称完成前先调用 `aecw verify`；发布、阶段与规则变更由人类通过 `aecw decide` 判定；不要手写状态或证据。

## 规则
- 当前无 active 规则；由 AECW 学习闭环维护。
