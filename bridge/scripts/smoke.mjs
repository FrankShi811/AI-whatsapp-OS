import { spawn } from 'node:child_process'
import path from 'node:path'
import readline from 'node:readline'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const encryptionKey = Buffer.alloc(32, 7).toString('base64')
const packagedExecutable = process.argv[2]
const child = packagedExecutable
  ? spawn(path.resolve(packagedExecutable), [], { stdio: ['pipe', 'pipe', 'inherit'] })
  : spawn(process.execPath, [path.join(here, '..', 'src', 'index.mjs')], { stdio: ['pipe', 'pipe', 'inherit'] })
const output = readline.createInterface({ input: child.stdout })
let ready = false
let ping = false
let initialized = false

const timeout = setTimeout(() => {
  child.kill()
  console.error('FAIL bridge smoke timeout')
  process.exitCode = 1
}, 15000)

output.on('line', line => {
  const message = JSON.parse(line)
  if (message.type === 'event' && message.event === 'ready') {
    ready = true
    child.stdin.write(`${JSON.stringify({ command: 'ping', requestId: 'ping-1' })}\n`)
  } else if (message.type === 'response' && message.requestId === 'ping-1' && message.ok) {
    ping = true
    child.stdin.write(`${JSON.stringify({ command: 'initialize', requestId: 'init-1', accountId: 'smoke', encryptionKey })}\n`)
  } else if (message.type === 'response' && message.requestId === 'init-1' && message.ok) {
    initialized = true
    clearTimeout(timeout)
    child.stdin.end()
  }
})

child.on('exit', () => {
  if (ready && ping && initialized) console.log(`PASS WAFlow WhatsApp bridge ready/ping/initialize${packagedExecutable ? ' (packaged EXE)' : ''}`)
  else {
    console.error(`FAIL bridge smoke ready=${ready} ping=${ping} initialized=${initialized}`)
    process.exitCode = 1
  }
})
