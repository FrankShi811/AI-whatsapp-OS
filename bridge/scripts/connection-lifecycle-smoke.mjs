import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
import { spawn } from 'node:child_process'
import readline from 'node:readline'

const dataRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'waflow-bridge-lifecycle-'))
const child = spawn(process.execPath, ['src/index.mjs'], {
  cwd: path.resolve(import.meta.dirname, '..'),
  env: {
    ...process.env,
    WAFLOW_DATA_ROOT: dataRoot,
    WAFLOW_BAILEYS_VERSION_LOOKUP_DISABLED: '1'
  },
  stdio: ['pipe', 'pipe', 'pipe']
})

const messages = []
const waiters = []
const output = readline.createInterface({ input: child.stdout, crlfDelay: Infinity })
output.on('line', line => {
  let message
  try { message = JSON.parse(line) } catch { return }
  messages.push(message)
  for (const waiter of [...waiters]) {
    if (!waiter.predicate(message)) continue
    clearTimeout(waiter.timer)
    waiters.splice(waiters.indexOf(waiter), 1)
    waiter.resolve(message)
  }
})

function waitFor(predicate, timeoutMs = 15000) {
  const existing = messages.find(predicate)
  if (existing) return Promise.resolve(existing)
  return new Promise((resolve, reject) => {
    const waiter = {
      predicate,
      resolve,
      timer: setTimeout(() => {
        waiters.splice(waiters.indexOf(waiter), 1)
        reject(new Error(`bridge_event_timeout_${timeoutMs}`))
      }, timeoutMs)
    }
    waiters.push(waiter)
  })
}

let requestSequence = 0
async function command(name, payload = {}, timeoutMs = 15000) {
  const requestId = `smoke-${++requestSequence}`
  child.stdin.write(`${JSON.stringify({ command: name, requestId, ...payload })}\n`)
  const response = await waitFor(message => message.type === 'response' && message.requestId === requestId, timeoutMs)
  assert.equal(response.ok, true, `${name} failed: ${JSON.stringify(response.error ?? {})}`)
  return response.result ?? {}
}

try {
  await waitFor(message => message.type === 'event' && message.event === 'ready')
  await command('initialize', {
    accountId: 'lifecycle_smoke',
    encryptionKey: crypto.randomBytes(32).toString('base64')
  })

  await command('connect', {
    proxyUrl: 'http://127.0.0.1:1',
    proxySource: 'lifecycle-smoke',
    allowDirectFallback: true
  })
  await waitFor(message => message.type === 'event'
    && message.event === 'connection_issue'
    && message.data?.code === 'proxy_route_failed', 30000)

  const disconnectedAt = messages.length
  const disconnected = await command('disconnect')
  assert.equal(disconnected.state, 'disconnected')
  await waitFor(message => message.type === 'event'
    && message.event === 'connection'
    && message.data?.state === 'disconnected'
    && message.data?.manual === true)
  await new Promise(resolve => setTimeout(resolve, 6500))
  assert.equal(messages.slice(disconnectedAt).some(message =>
    message.type === 'event'
      && message.event === 'connection'
      && ['connecting', 'retrying'].includes(message.data?.state)), false,
  'automatic reconnect resumed after manual disconnect')

  const sessionDirectory = path.join(dataRoot, 'whatsapp-sessions', 'lifecycle_smoke')
  const markerPath = path.join(sessionDirectory, 'stale-session-marker')
  await fs.writeFile(markerPath, 'stale')
  const loggedOut = await command('logout')
  assert.equal(loggedOut.state, 'logged_out')
  await assert.rejects(fs.access(markerPath))
  await fs.access(sessionDirectory)

  console.log('PASS WhatsApp proxy fallback, manual disconnect cancellation, and local logout reset')
} finally {
  output.close()
  child.stdin.end()
  if (!child.killed) child.kill()
  await fs.rm(dataRoot, { recursive: true, force: true })
}
