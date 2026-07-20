import { spawn } from 'node:child_process'
import path from 'node:path'
import readline from 'node:readline'
import fs from 'node:fs/promises'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const accountId = 'smoke-qr'
const encryptionKey = Buffer.alloc(32, 11).toString('base64')
const sessionDir = path.join(process.env.LOCALAPPDATA, 'WAFlow', 'whatsapp-sessions', accountId)
await fs.rm(sessionDir, { recursive: true, force: true })
const packagedExecutable = process.argv[2]
const child = packagedExecutable
  ? spawn(path.resolve(packagedExecutable), [], { stdio: ['pipe', 'pipe', 'inherit'] })
  : spawn(process.execPath, [path.join(here, '..', 'src', 'index.mjs')], { stdio: ['pipe', 'pipe', 'inherit'] })
const output = readline.createInterface({ input: child.stdout })
let qrReceived = false
let encryptedSession = false

const timeout = setTimeout(() => {
  child.kill()
  console.error('FAIL WhatsApp QR was not produced within 30 seconds')
  process.exitCode = 1
}, 30000)

output.on('line', line => {
  const message = JSON.parse(line)
  if (message.type === 'event' && message.event === 'ready')
    child.stdin.write(`${JSON.stringify({ command: 'initialize', requestId: 'init', accountId, encryptionKey })}\n`)
  else if (message.type === 'response' && message.requestId === 'init' && message.ok)
    child.stdin.write(`${JSON.stringify({ command: 'connect', requestId: 'connect' })}\n`)
  else if (message.type === 'response' && message.requestId === 'connect' && !message.ok)
    console.error(`Bridge connect error: ${message.error?.message ?? JSON.stringify(message.error)}`)
  else if (message.type === 'event' && message.event === 'bridge_error')
    console.error(`Bridge event error: ${message.data?.error ?? JSON.stringify(message.data)}`)
  else if (message.type === 'event' && message.event === 'connection' && message.data?.state === 'disconnected')
    console.error(`Bridge disconnected: ${message.data?.error ?? 'unknown'}`)
  else if (message.type === 'event' && message.event === 'qr') {
    qrReceived = message.data?.dataUrl?.startsWith('data:image/png;base64,') === true
    child.stdin.write(`${JSON.stringify({ command: 'disconnect', requestId: 'disconnect' })}\n`)
  } else if (message.type === 'response' && message.requestId === 'disconnect') {
    clearTimeout(timeout)
    child.stdin.end()
  }
})

await new Promise(resolve => child.on('exit', resolve))
const sessionFiles = await fs.readdir(sessionDir).catch(() => [])
encryptedSession = sessionFiles.some(file => file.endsWith('.enc')) && !sessionFiles.some(file => file.endsWith('.json'))
await fs.rm(sessionDir, { recursive: true, force: true })
if (qrReceived && encryptedSession) console.log(`PASS WhatsApp multi-device QR event produced with encrypted session state${packagedExecutable ? ' (packaged EXE)' : ''}`)
else {
  console.error(`FAIL WhatsApp QR/session check qr=${qrReceived} encrypted=${encryptedSession}`)
  process.exitCode = 1
}
