// AECW gate：可执行检查。只读文件、只跑 helper 的 selftest（不注入任何输入）。
import { spawnSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)))
let failed = 0

async function check(name, fn) {
  try {
    await fn()
    console.log(`PASS ${name}`)
  } catch (error) {
    failed += 1
    console.error(`FAIL ${name}: ${error.message}`)
  }
}

function requireFile(relative) {
  const file = path.join(root, relative)
  if (!fs.existsSync(file)) throw new Error(`missing ${relative}`)
  const stat = fs.statSync(file)
  if (!stat.isFile() || stat.size === 0) throw new Error(`${relative} is empty`)
  return file
}

await check('bundle manifest', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'))
  if (manifest.name !== '@local/deskhand') throw new Error('unexpected package name')
  if (manifest.dsh?.bundle?.patch !== './cordis.patch.yml') throw new Error('missing bundle patch declaration')
})

await check('bundle patch', () => {
  const text = fs.readFileSync(requireFile('cordis.patch.yml'), 'utf8')
  if (!/id:\s*deskhand/.test(text)) throw new Error('patch does not insert the deskhand row')
  if (!/@local\/deskhand/.test(text)) throw new Error('patch does not reference the package')
})

await check('locales', () => {
  for (const locale of ['en', 'zh']) {
    const file = requireFile(`locale/${locale}.json`)
    const data = JSON.parse(fs.readFileSync(file, 'utf8'))
    if (typeof data?.meta?.title !== 'string' || data.meta.title === '') throw new Error(`${locale} meta.title missing`)
    if (typeof data?.meta?.description !== 'string' || data.meta.description === '') throw new Error(`${locale} meta.description missing`)
  }
})

await check('icon', () => {
  const file = requireFile('icon.svg')
  const text = fs.readFileSync(file, 'utf8')
  if (!text.includes('<svg')) throw new Error('icon.svg is not SVG')
})

await check('helper executable', () => {
  const file = requireFile(path.join('helper', 'deskhand.exe'))
  if (fs.statSync(file).size < 4096) throw new Error('helper looks truncated')
})

await check('host module imports', async () => {
  const module = await import(pathToFileURL(path.join(root, 'index.js')).href)
  if (typeof module.apply !== 'function') throw new Error('index.js does not export apply()')
  if (!Array.isArray(module.inject) || !module.inject.includes('tools')) throw new Error('index.js does not inject tools')
})

await check('helper selftest', () => {
  const exe = path.join(root, 'helper', 'deskhand.exe')
  // 沙箱内 piped stdio 会 EPERM，改用文件重定向收集 helper 输出。
  const outFile = path.join(root, 'data', `selftest-${process.pid}.log`)
  fs.mkdirSync(path.dirname(outFile), { recursive: true })
  const fd = fs.openSync(outFile, 'w')
  try {
    const run = spawnSync(exe, ['selftest'], { stdio: ['ignore', fd, fd], timeout: 20000, windowsHide: true })
    if (run.error) throw new Error(run.error.message)
    const line = fs.readFileSync(outFile, 'utf8').trim().split(/\r?\n/).filter(Boolean).pop() ?? ''
    let value = null
    try { value = JSON.parse(line) } catch { value = null }
    if (value === null) throw new Error(`selftest returned no JSON (exit ${run.status}): ${line.slice(0, 200)}`)
    if (value.ok !== true) throw new Error(`selftest failed: ${JSON.stringify(value.checks ?? value.error)}`)
  } finally {
    fs.closeSync(fd)
    try { fs.unlinkSync(outFile) } catch { /* ignore */ }
  }
})

if (failed > 0) {
  console.error(`\n${failed} check(s) failed`)
  process.exit(1)
}
console.log('\nall checks passed')
