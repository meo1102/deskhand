// index.js — deskhand Host 半区。
// 眼睛：截屏交给附件服务，作为图片内容返回模型（broker 或直接 helper 都行）。
// 手：点击 / 键入 / 按键 / 滚轮，走 deskhand broker（普通用户权限，回环 TCP），
//     因为 DSH 从插件里直接 spawn 的进程会被沙箱降到 Low 完整性，无法注入输入。
import { spawn } from 'node:child_process'
import fs from 'node:fs'
import net from 'node:net'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const PKG_DIR = path.dirname(fileURLToPath(import.meta.url))

export const inject = ['tools', 'attachments', 'approval']

const DEFAULT_CONFIG = {
  dataDir: 'data',
  helperPath: path.join(PKG_DIR, 'helper', 'deskhand.exe'),
  timeoutMs: 20000,
  maxShots: 60,
  requireApproval: true,
  roundMinutes: 10,
}

function normalizeConfig(config) {
  const raw = config !== null && typeof config === 'object' ? config : {}
  return {
    dataDir: typeof raw.dataDir === 'string' && raw.dataDir.trim() !== '' ? raw.dataDir.trim() : DEFAULT_CONFIG.dataDir,
    helperPath:
      typeof raw.helperPath === 'string' && raw.helperPath.trim() !== ''
        ? raw.helperPath.trim()
        : DEFAULT_CONFIG.helperPath,
    timeoutMs: Number.isInteger(raw.timeoutMs) && raw.timeoutMs >= 1000 ? Math.min(raw.timeoutMs, 120000) : DEFAULT_CONFIG.timeoutMs,
    maxShots: Number.isInteger(raw.maxShots) && raw.maxShots >= 1 ? Math.min(raw.maxShots, 500) : DEFAULT_CONFIG.maxShots,
    requireApproval: raw.requireApproval !== false,
    roundMinutes: Number.isInteger(raw.roundMinutes) && raw.roundMinutes >= 1 ? Math.min(raw.roundMinutes, 60) : DEFAULT_CONFIG.roundMinutes,
  }
}

const BROKER_HINT = '桌面手 broker 未运行：双击 deskhand\\scripts\\start-broker.cmd 启动（登录后 watcher 会自动跟随 DSH 拉起）'

function readBrokerInfo(infoFile) {
  try {
    const info = JSON.parse(fs.readFileSync(infoFile, 'utf8'))
    if (!info || !Number.isInteger(info.port) || typeof info.token !== 'string') return null
    return info
  } catch {
    return null
  }
}

/** 一次 broker 请求：token + tab 分隔命令，回一行 JSON。 */
function brokerCall(infoFile, args, timeoutMs) {
  return new Promise((resolve, reject) => {
    const info = readBrokerInfo(infoFile)
    if (info === null) {
      reject(new Error(BROKER_HINT))
      return
    }
    const socket = net.connect(info.port, '127.0.0.1')
    let buffer = ''
    let settled = false
    const finish = (error, value) => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      try { socket.destroy() } catch { /* ignore */ }
      if (error) reject(error)
      else resolve(value)
    }
    const timer = setTimeout(() => finish(new Error('broker 响应超时')), timeoutMs)
    socket.setEncoding('utf8')
    socket.on('connect', () => {
      socket.write([info.token, ...args].join('\t') + '\n')
    })
    socket.on('data', (chunk) => {
      buffer += chunk
      const newline = buffer.indexOf('\n')
      if (newline < 0) return
      const line = buffer.slice(0, newline)
      let value = null
      try { value = JSON.parse(line) } catch { value = null }
      if (value === null) finish(new Error('broker 返回了非 JSON 内容'))
      else finish(null, value)
    })
    socket.on('error', (error) => {
      if (error && (error.code === 'ENOENT' || error.code === 'ECONNREFUSED' || error.code === 'EACCES')) {
        finish(new Error(BROKER_HINT))
      } else {
        finish(error)
      }
    })
  })
}

/** 直接跑 helper（只用于 state/capture 的兜底，不用于输入）。 */
function runHelper(helperPath, args, timeoutMs) {
  return new Promise((resolve, reject) => {
    const child = spawn(helperPath, args, { windowsHide: true })
    let stdout = ''
    let stderr = ''
    let settled = false
    const timer = setTimeout(() => {
      if (settled) return
      settled = true
      try { child.kill() } catch { /* ignore */ }
      reject(new Error(`helper timed out after ${timeoutMs}ms`))
    }, timeoutMs)
    child.stdout.on('data', (chunk) => { stdout += chunk })
    child.stderr.on('data', (chunk) => { stderr += chunk })
    child.on('error', (error) => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      reject(new Error(`cannot start helper ${helperPath}: ${error.message}`))
    })
    child.on('close', (code) => {
      if (settled) return
      settled = true
      clearTimeout(timer)
      const lastLine = stdout.trim().split(/\r?\n/).filter((line) => line.trim() !== '').pop() ?? ''
      let value = null
      try { value = JSON.parse(lastLine) } catch { value = null }
      if (value === null) {
        reject(new Error(`helper returned no JSON (exit ${code}): ${(stderr || stdout).trim().slice(0, 300)}`))
        return
      }
      resolve(value)
    })
  })
}

const TEXT_OUTPUT = Object.freeze({
  schema: { type: 'object' },
  render: (_args, value) => [{ type: 'text', text: JSON.stringify(value, null, 2) }],
})

const CAPTURE_OUTPUT = Object.freeze({
  schema: {
    type: 'object',
    additionalProperties: false,
    properties: {
      path: { type: 'string' },
      image: { type: 'object' },
      screen: { type: 'object' },
      broker: { type: 'boolean' },
    },
  },
  render: (_args, value) => {
    const image = value.image ?? {}
    const screen = value.screen ?? {}
    const width = Number(image.width) || 0
    const height = Number(image.height) || 0
    const scale = image.originalDimensions && image.originalDimensions.width && width
      ? image.originalDimensions.width / width
      : 1
    const note = scale > 1.001
      ? `图片被缩放到 ${width}x${height}，点击坐标要乘以 ${scale.toFixed(2)}（屏幕坐标以原始 ${screen.width}x${screen.height} 为准）`
      : `图片与屏幕同尺寸（${width}x${height}），点击坐标直接用图片坐标`
    return [
      { type: 'text', text: `<path>${value.path}</path>\n<type>image</type>\n<content>\n窗口截图：${note}\n</content>` },
      { type: 'image', attachment: image },
    ]
  },
})

function audit(logFile, entry) {
  try {
    fs.appendFileSync(logFile, JSON.stringify(entry) + '\n', 'utf8')
  } catch {
    // 审计写失败不阻断动作；W1 升级为强制。
  }
}

function pruneShots(shotsDir, maxShots) {
  try {
    const files = fs.readdirSync(shotsDir)
      .filter((name) => name.endsWith('.png'))
      .map((name) => ({ name, mtime: fs.statSync(path.join(shotsDir, name)).mtimeMs }))
      .sort((a, b) => b.mtime - a.mtime)
    for (const stale of files.slice(maxShots)) {
      try { fs.unlinkSync(path.join(shotsDir, stale.name)) } catch { /* ignore */ }
    }
  } catch {
    // ignore
  }
}

function ensureStopped(dataDir) {
  if (fs.existsSync(path.join(dataDir, 'STOP'))) {
    throw new Error('deskhand 已停止：删除数据目录下的 STOP 文件后再操作')
  }
}

export function apply(ctx, config) {
  const cfg = normalizeConfig(config)
  const dataDir = path.resolve(PKG_DIR, cfg.dataDir)
  const shotsDir = path.join(dataDir, 'shots')
  const brokerInfoFile = path.join(dataDir, 'broker.json')
  const logFile = path.join(dataDir, 'audit.jsonl')
  fs.mkdirSync(shotsDir, { recursive: true })

  const direct = (args) => runHelper(cfg.helperPath, args, cfg.timeoutMs)
  const broker = (args) => brokerCall(brokerInfoFile, args, cfg.timeoutMs)

  const approvalLog = path.join(dataDir, 'approval.log')
  let round = { until: 0, task: '' }
  const roundActive = () => Date.now() < round.until

  async function ensureRound(exec, action) {
    if (!cfg.requireApproval) return
    if (roundActive()) return
    throw new Error(`尚未获批本轮桌面操作（本次动作：${action}）。请先调用 desk_begin 描述本轮任务并获得用户批准。`)
  }

  async function beginRound(exec, task) {
    if (roundActive()) {
      return { granted: true, until: new Date(round.until).toISOString(), task: round.task, note: '本轮仍在有效期内' }
    }
    const approval = ctx.get('approval')
    if (!approval) throw new Error('审批服务不可用，无法开始本轮操作')
    let foreground = ''
    try {
      const state = await broker(['state'])
      foreground = state && state.foreground && state.foreground.title ? state.foreground.title : ''
    } catch {
      // broker 不在时不影响审批，只是少了前台窗口提示
    }
    try { fs.appendFileSync(approvalLog, new Date().toISOString() + ' round-ask ' + task + '\n') } catch { /* ignore */ }
    const outcome = await approval.request({
      agent: exec.agent,
      toolName: 'desk_begin',
      reason: `允许 agent 在你桌面上执行本轮操作：${task}${foreground ? '（当前前台窗口：' + foreground + '）' : ''}`,
      signal: exec.signal,
    })
    try { fs.appendFileSync(approvalLog, new Date().toISOString() + ' round-outcome ' + outcome + '\n') } catch { /* ignore */ }
    if (outcome !== 'allowed-once') {
      throw new Error(`本轮操作未获批准（${outcome}）`)
    }
    round = { until: Date.now() + cfg.roundMinutes * 60 * 1000, task }
    return { granted: true, until: new Date(round.until).toISOString(), task }
  }

  ctx.effect(() => {
    const disposers = []

    disposers.push(ctx.tools.register({
      name: 'desk_state',
      description: '读取桌面状态：屏幕物理分辨率、鼠标坐标、前台窗口（标题/类名/进程）、当前执行者完整性。任何操作前先看它。',
      parameters: { type: 'object', properties: {}, additionalProperties: false },
      output: TEXT_OUTPUT,
      timeoutMs: 10000,
      isConcurrencySafe: () => true,
      execute: async () => {
        try {
          const state = await broker(['state'])
          return { ...state, broker: true }
        } catch {
          const state = await direct(['state'])
          return { ...state, broker: false }
        }
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_begin',
      description: '开始一轮桌面操作并请求用户批准。批准后本轮内（默认 10 分钟）的点击/输入/按键不再逐次审批；任务完成请调用 desk_end。task 用一句人话写清要做什么，例如「把浏览器里的表格整理到 Excel」。',
      parameters: {
        type: 'object',
        properties: {
          task: { type: 'string', description: '本轮任务的一句话描述，会显示在审批卡上' },
        },
        required: ['task'],
        additionalProperties: false,
      },
      output: TEXT_OUTPUT,
      timeoutMs: 300000,
      execute: async (args, exec) => {
        const task = typeof args.task === 'string' ? args.task.trim() : ''
        if (task === '') throw new Error('task 不能为空')
        return beginRound(exec, task)
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_end',
      description: '结束本轮桌面操作，撤销本轮免审批状态。',
      parameters: { type: 'object', properties: {}, additionalProperties: false },
      output: TEXT_OUTPUT,
      timeoutMs: 10000,
      execute: async () => {
        const ended = roundActive() ? round.task : ''
        round = { until: 0, task: '' }
        return { ended, note: ended ? '本轮已结束' : '当前没有进行中的轮次' }
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_capture',
      description: '截取当前主屏并作为图片返回模型。返回的图片可能被缩放，按返回文本里的说明换算坐标。',
      parameters: {
        type: 'object',
        properties: {
          region: {
            type: 'string',
            description: '可选裁剪，格式 "x,y,width,height"，使用屏幕物理坐标；省略则截全屏',
          },
        },
        additionalProperties: false,
      },
      output: CAPTURE_OUTPUT,
      timeoutMs: 20000,
      isConcurrencySafe: () => true,
      execute: async (args) => {
        const started = Date.now()
        const file = path.join(shotsDir, `shot-${Date.now()}.png`)
        const shotArgs = ['shot', file]
        if (typeof args.region === 'string' && args.region.trim() !== '') {
          const parts = args.region.split(',').map((part) => part.trim())
          if (parts.length !== 4 || parts.some((part) => !/^-?\d+$/.test(part))) {
            throw new Error('region 需要 "x,y,width,height" 四个整数')
          }
          const [x, y, width, height] = parts
          if (Number(width) <= 0 || Number(height) <= 0) throw new Error('region 的宽高必须为正')
          shotArgs.push(x, y, width, height)
        }
        let usedBroker = true
        let shot
        try {
          shot = await broker(shotArgs)
        } catch {
          usedBroker = false
          shot = await direct(shotArgs)
        }
        if (shot.ok !== true) throw new Error(shot.error ?? 'capture failed')
        const data = fs.readFileSync(file)
        const image = await ctx.attachments.saveImage({
          data,
          mediaType: 'image/png',
          name: path.basename(file),
        })
        const screen = await direct(['state']).then((state) => state.screen ?? null, () => null)
        audit(logFile, { ts: new Date().toISOString(), tool: 'capture', file, bytes: data.length, ms: Date.now() - started, broker: usedBroker })
        pruneShots(shotsDir, cfg.maxShots)
        return { path: file, image, screen, broker: usedBroker }
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_click',
      description: '把鼠标移到屏幕坐标 (x,y) 并点击。x/y 用屏幕物理坐标（desk_state 的 screen 与 desk_capture 换算说明）。需先 desk_begin 开始一轮并获批。危险区域先看再点。',
      parameters: {
        type: 'object',
        properties: {
          x: { type: 'integer', description: '屏幕物理 x 坐标' },
          y: { type: 'integer', description: '屏幕物理 y 坐标' },
          button: { type: 'string', enum: ['left', 'right', 'middle'], description: '按键，默认 left' },
          clicks: { type: 'integer', description: '点击次数 1-3，默认 1' },
        },
        required: ['x', 'y'],
        additionalProperties: false,
      },
      output: TEXT_OUTPUT,
      timeoutMs: 15000,
      execute: async (args, exec) => {
        ensureStopped(dataDir)
        const x = Number(args.x)
        const y = Number(args.y)
        if (!Number.isInteger(x) || !Number.isInteger(y)) throw new Error('x/y 必须是整数')
        const button = typeof args.button === 'string' ? args.button : 'left'
        const clicks = Number.isInteger(args.clicks) ? Math.max(1, Math.min(3, args.clicks)) : 1
        await ensureRound(exec, '点击')
        const result = await broker(['click', String(x), String(y), button, String(clicks)])
        audit(logFile, { ts: new Date().toISOString(), tool: 'click', x, y, button, clicks, ok: result.ok === true })
        if (result.ok !== true) throw new Error(result.error ?? 'click failed')
        return result
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_type',
      description: '向当前焦点窗口输入文本（走剪贴板 + Ctrl+V，支持中文与任意 Unicode）。会覆盖用户剪贴板。需先 desk_begin 开始一轮并获批；先确认焦点在正确的输入框。',
      parameters: {
        type: 'object',
        properties: {
          text: { type: 'string', description: '要输入的文本，非空' },
        },
        required: ['text'],
        additionalProperties: false,
      },
      output: TEXT_OUTPUT,
      timeoutMs: 15000,
      execute: async (args, exec) => {
        ensureStopped(dataDir)
        const text = typeof args.text === 'string' ? args.text : ''
        if (text.length === 0) throw new Error('text 不能为空')
        await ensureRound(exec, '输入文本')
        const encoded = Buffer.from(text, 'utf8').toString('base64')
        const result = await broker(['type', encoded])
        audit(logFile, { ts: new Date().toISOString(), tool: 'type', chars: text.length, ok: result.ok === true })
        if (result.ok !== true) throw new Error(result.error ?? 'type failed')
        return result
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_key',
      description: '发送组合键，如 "enter"、"ctrl+c"、"alt+tab"、"ctrl+shift+s"。需先 desk_begin 开始一轮并获批；当前焦点窗口生效。',
      parameters: {
        type: 'object',
        properties: {
          keys: { type: 'string', description: '组合键，用 + 连接，例如 ctrl+shift+esc' },
        },
        required: ['keys'],
        additionalProperties: false,
      },
      output: TEXT_OUTPUT,
      timeoutMs: 15000,
      execute: async (args, exec) => {
        ensureStopped(dataDir)
        const keys = typeof args.keys === 'string' ? args.keys.trim() : ''
        if (keys === '') throw new Error('keys 不能为空')
        await ensureRound(exec, '按键')
        const result = await broker(['key', keys])
        audit(logFile, { ts: new Date().toISOString(), tool: 'key', keys, ok: result.ok === true })
        if (result.ok !== true) throw new Error(result.error ?? 'key failed')
        return result
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_scroll',
      description: '在当前焦点位置滚动滚轮。delta 正值向上，负值向下（单格通常 120）。需先 desk_begin 开始一轮并获批。',
      parameters: {
        type: 'object',
        properties: {
          delta: { type: 'integer', description: '滚动量，正上负下' },
        },
        required: ['delta'],
        additionalProperties: false,
      },
      output: TEXT_OUTPUT,
      timeoutMs: 15000,
      execute: async (args, exec) => {
        ensureStopped(dataDir)
        const delta = Number(args.delta)
        if (!Number.isInteger(delta) || delta === 0) throw new Error('delta 必须是非零整数')
        await ensureRound(exec, '滚轮')
        const result = await broker(['scroll', String(delta)])
        audit(logFile, { ts: new Date().toISOString(), tool: 'scroll', delta, ok: result.ok === true })
        if (result.ok !== true) throw new Error(result.error ?? 'scroll failed')
        return result
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_uia',
      description: '读取前台窗口的 UIA 控件树（ref/名称/类型/坐标/可用动作）。ref 用于 desk_uia_act；界面变化后 ref 会失效，需重新读取。远程桌面窗口内部控件不可见。',
      parameters: { type: 'object', properties: {}, additionalProperties: false },
      output: TEXT_OUTPUT,
      timeoutMs: 20000,
      isConcurrencySafe: () => true,
      execute: async () => broker(['uia']),
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_uia_act',
      description: '按 UIA ref 操作前台窗口元素：invoke / setvalue / toggle / select / expand / collapse / focus。属于输入动作，需先 desk_begin 并受黑名单限制。',
      parameters: {
        type: 'object',
        properties: {
          action: { type: 'string', enum: ['invoke', 'setvalue', 'toggle', 'select', 'expand', 'collapse', 'focus'], description: '动作类型' },
          ref: { type: 'string', description: 'desk_uia 返回的 ref，如 0.2.1；改用 name 时可留空' },
          name: { type: 'string', description: '按控件名定位（子串匹配、只匹配可用且支持该动作的元素），ref 失效时更稳' },
          value: { type: 'string', description: 'setvalue 时要写入的文本' },
        },
        required: ['action'],
        additionalProperties: false,
      },
      output: TEXT_OUTPUT,
      timeoutMs: 20000,
      execute: async (args, exec) => {
        ensureStopped(dataDir)
        const action = typeof args.action === 'string' ? args.action : ''
        const ref = typeof args.ref === 'string' ? args.ref : ''
        const name = typeof args.name === 'string' ? args.name.trim() : ''
        if (action === '' || (ref === '' && name === '')) throw new Error('需要 ref 或 name')
        await ensureRound(exec, 'UIA ' + action)
        const value = typeof args.value === 'string' ? Buffer.from(args.value, 'utf8').toString('base64') : ''
        const nameB64 = name !== '' ? Buffer.from(name, 'utf8').toString('base64') : ''
        const result = await broker(['uia_act', action, ref === '' ? '-' : ref, value, nameB64])
        audit(logFile, { ts: new Date().toISOString(), tool: 'uia_act', action, ref, name, ok: result.ok === true })
        if (result.ok !== true) throw new Error(result.error ?? 'uia_act failed')
        return result
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_windows',
      description: '列出当前可见的顶层窗口（标题/进程/坐标/是否黑名单）。选目标窗口用标题或 pid，再用 desk_activate 前置。',
      parameters: { type: 'object', properties: {}, additionalProperties: false },
      output: TEXT_OUTPUT,
      timeoutMs: 15000,
      isConcurrencySafe: () => true,
      execute: async () => broker(['windows']),
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_browser_tabs',
      description: '列出暴露了 CDP 调试端口的 Chromium 浏览器标签页（需浏览器以 --remote-debugging-port=9222 启动）。读不到时返回提示，不报错。',
      parameters: {
        type: 'object',
        properties: {
          port: { type: 'integer', description: 'CDP 端口，默认 9222' },
        },
        additionalProperties: false,
      },
      output: TEXT_OUTPUT,
      timeoutMs: 12000,
      isConcurrencySafe: () => true,
      execute: async (args) => {
        const ports = Number.isInteger(args.port) ? [args.port] : [9222, 9223]
        const attempts = []
        for (const port of ports) {
          try {
            const response = await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(3000) })
            if (!response.ok) {
              attempts.push({ port, error: 'http ' + response.status })
              continue
            }
            const list = await response.json()
            const tabs = (Array.isArray(list) ? list : [])
              .filter((tab) => tab && tab.type === 'page')
              .map((tab) => ({ title: tab.title, url: tab.url, id: tab.id }))
            return { port, count: tabs.length, tabs }
          } catch (error) {
            attempts.push({ port, error: error && error.message ? error.message : 'unreachable' })
          }
        }
        return {
          ok: false,
          attempts,
          hint: '没有发现 CDP 端口。用 --remote-debugging-port=9222 重启浏览器后再试；或改用 desk_uia 走本地控件树。',
        }
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_activate',
      description: '把匹配标题（或 pid）的窗口带到前台。属于输入动作，需先 desk_begin；目标在黑名单中会被拒绝。',
      parameters: {
        type: 'object',
        properties: {
          title: { type: 'string', description: '窗口标题片段' },
          pid: { type: 'integer', description: '进程 id（与 title 二选一）' },
        },
        additionalProperties: false,
      },
      output: TEXT_OUTPUT,
      timeoutMs: 20000,
      execute: async (args, exec) => {
        ensureStopped(dataDir)
        const needle = Number.isInteger(args.pid) ? String(args.pid) : (typeof args.title === 'string' ? args.title.trim() : '')
        if (needle === '') throw new Error('需要 title 或 pid')
        await ensureRound(exec, '切换窗口')
        const result = await broker(['activate', needle])
        audit(logFile, { ts: new Date().toISOString(), tool: 'activate', needle, ok: result.ok === true })
        if (result.ok !== true) throw new Error(result.error ?? 'activate failed')
        return result
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_drag',
      description: '从 (from_x,from_y) 拖到 (to_x,to_y)。属于输入动作，需先 desk_begin。',
      parameters: {
        type: 'object',
        properties: {
          from_x: { type: 'integer', description: '起点屏幕 x' },
          from_y: { type: 'integer', description: '起点屏幕 y' },
          to_x: { type: 'integer', description: '终点屏幕 x' },
          to_y: { type: 'integer', description: '终点屏幕 y' },
          button: { type: 'string', enum: ['left', 'right', 'middle'], description: '按键，默认 left' },
          steps: { type: 'integer', description: '插值步数 2-100，默认 24' },
        },
        required: ['from_x', 'from_y', 'to_x', 'to_y'],
        additionalProperties: false,
      },
      output: TEXT_OUTPUT,
      timeoutMs: 30000,
      execute: async (args, exec) => {
        ensureStopped(dataDir)
        const coords = [args.from_x, args.from_y, args.to_x, args.to_y].map(Number)
        if (coords.some((value) => !Number.isInteger(value))) throw new Error('坐标必须是整数')
        await ensureRound(exec, '拖拽')
        const button = typeof args.button === 'string' ? args.button : 'left'
        const steps = Number.isInteger(args.steps) ? Math.max(2, Math.min(100, args.steps)) : 24
        const result = await broker(['drag', String(coords[0]), String(coords[1]), String(coords[2]), String(coords[3]), button, String(steps)])
        audit(logFile, { ts: new Date().toISOString(), tool: 'drag', coords, button, ok: result.ok === true })
        if (result.ok !== true) throw new Error(result.error ?? 'drag failed')
        return result
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_clipboard',
      description: '读写/备份/恢复/校验剪贴板文本。get 与 backup/verify 只读；set 与 restore 属于输入动作，需先 desk_begin。',
      parameters: {
        type: 'object',
        properties: {
          action: { type: 'string', enum: ['get', 'set', 'backup', 'restore', 'verify'], description: '操作类型' },
          text: { type: 'string', description: 'set 时要写入的文本' },
          path: { type: 'string', description: 'backup/restore/verify 的备份文件路径' },
        },
        required: ['action'],
        additionalProperties: false,
      },
      output: TEXT_OUTPUT,
      timeoutMs: 15000,
      execute: async (args, exec) => {
        ensureStopped(dataDir)
        const action = args.action
        if (action === 'get') return broker(['clipboard', 'get'])
        if (action === 'backup') {
          const file = typeof args.path === 'string' ? args.path : ''
          if (file === '') throw new Error('backup 需要 path')
          return broker(['clipboard', 'backup', file])
        }
        if (action === 'verify') {
          const file = typeof args.path === 'string' ? args.path : ''
          if (file === '') throw new Error('verify 需要 path')
          let expected
          try {
            expected = fs.readFileSync(file, 'utf8')
          } catch {
            throw new Error('备份文件不存在: ' + file)
          }
          const current = await broker(['clipboard', 'get'])
          if (current.ok !== true) throw new Error(current.error ?? 'read clipboard failed')
          const text = typeof current.text === 'string' ? current.text : ''
          return { match: text === expected, chars: text.length }
        }
        if (action === 'set' || action === 'restore') {
          await ensureRound(exec, action === 'set' ? '写剪贴板' : '恢复剪贴板')
          if (action === 'set') {
            const text = typeof args.text === 'string' ? args.text : ''
            if (text === '') throw new Error('set 需要非空 text')
            const encoded = Buffer.from(text, 'utf8').toString('base64')
            return broker(['clipboard', 'set', encoded])
          }
          const file = typeof args.path === 'string' ? args.path : ''
          if (file === '') throw new Error('restore 需要 path')
          return broker(['clipboard', 'restore', file])
        }
        throw new Error('action 只支持 get / set / backup / restore / verify')
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_audio_info',
      description: '耳朵诊断：列出可用的语音识别器与麦克风是否可用。',
      parameters: { type: 'object', properties: {}, additionalProperties: false },
      output: TEXT_OUTPUT,
      timeoutMs: 15000,
      isConcurrencySafe: () => true,
      execute: async () => broker(['audio_info']),
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_hear',
      description: '用麦克风听写最近 N 秒（默认 5 秒，最多 30 秒），返回识别文本。属于敏感采集，需先 desk_begin 开始一轮并获批。',
      parameters: {
        type: 'object',
        properties: {
          seconds: { type: 'integer', description: '听写秒数 1-30，默认 5' },
        },
        additionalProperties: false,
      },
      output: TEXT_OUTPUT,
      timeoutMs: 60000,
      execute: async (args, exec) => {
        ensureStopped(dataDir)
        const seconds = Number.isInteger(args.seconds) ? Math.max(1, Math.min(30, args.seconds)) : 5
        await ensureRound(exec, '麦克风听写')
        const result = await broker(['hear', String(seconds)])
        audit(logFile, { ts: new Date().toISOString(), tool: 'hear', seconds, ok: result.ok === true })
        if (result.ok !== true) throw new Error(result.error ?? 'hear failed')
        return result
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_transcribe',
      description: '把 WAV 音频文件转成文字（中文识别器）。属于敏感读取，需先 desk_begin 开始一轮并获批。',
      parameters: {
        type: 'object',
        properties: {
          path: { type: 'string', description: 'WAV 文件绝对路径' },
        },
        required: ['path'],
        additionalProperties: false,
      },
      output: TEXT_OUTPUT,
      timeoutMs: 120000,
      execute: async (args, exec) => {
        ensureStopped(dataDir)
        const file = typeof args.path === 'string' ? args.path : ''
        if (file === '') throw new Error('path 不能为空')
        await ensureRound(exec, '音频文件转写')
        const result = await broker(['transcribe', file])
        audit(logFile, { ts: new Date().toISOString(), tool: 'transcribe', file, ok: result.ok === true })
        if (result.ok !== true) throw new Error(result.error ?? 'transcribe failed')
        return result
      },
    }))

    disposers.push(ctx.tools.register({
      name: 'desk_probe',
      description: '诊断（临时）：broker 是否可达、当前登录器 TCP 是否可用。',
      parameters: { type: 'object', properties: {}, additionalProperties: false },
      output: TEXT_OUTPUT,
      timeoutMs: 12000,
      execute: async (args, exec) => {
        const result = { brokerFile: fs.existsSync(brokerInfoFile), requireApproval: cfg.requireApproval }
        const approval = ctx.get('approval')
        result.approvalAvailable = !!(approval && typeof approval.request === 'function')
        try {
          const session = exec && exec.agent ? exec.agent.session : undefined
          result.approvalPolicy = approval && typeof approval.overrideOf === 'function' ? approval.overrideOf(session) : 'n/a'
        } catch (error) {
          result.approvalPolicy = 'error:' + error.message
        }
        try {
          result.broker = await broker(['version'])
          result.broker.ok = true
        } catch (error) {
          result.broker = 'error:' + error.message
        }
        return result
      },
    }))

    return () => {
      for (const dispose of disposers) {
        try { dispose() } catch { /* ignore */ }
      }
    }
  })
}
