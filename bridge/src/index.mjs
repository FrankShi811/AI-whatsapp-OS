import readline from 'node:readline'
import path from 'node:path'
import fs from 'node:fs/promises'
import crypto from 'node:crypto'
import process from 'node:process'
import QRCode from 'qrcode'
import pino from 'pino'
import makeWASocket, {
  Browsers,
  BufferJSON,
  DisconnectReason,
  fetchLatestBaileysVersion,
  initAuthCreds,
  proto
} from '@whiskeysockets/baileys'

const logger = pino({ level: 'silent' })
const state = {
  accountId: 'default',
  sessionDir: '',
  socket: null,
  connection: 'idle',
  reconnectTimer: null,
  manualDisconnect: false,
  authKey: null
}

const authFileLocks = new Map()

function parseEncryptionKey(value) {
  const key = Buffer.from(String(value ?? ''), 'base64')
  if (key.length !== 32) throw new Error('invalid_session_encryption_key')
  return key
}

function fixAuthFileName(file) {
  return String(file ?? '').replace(/\//g, '__').replace(/:/g, '-')
}

async function withAuthFileLock(file, action) {
  const previous = authFileLocks.get(file) ?? Promise.resolve()
  let release
  const next = new Promise(resolve => { release = resolve })
  const tail = previous.then(() => next)
  authFileLocks.set(file, tail)
  await previous
  try { return await action() }
  finally {
    release()
    if (authFileLocks.get(file) === tail) authFileLocks.delete(file)
  }
}

function encryptAuthData(data, key) {
  const iv = crypto.randomBytes(12)
  const cipher = crypto.createCipheriv('aes-256-gcm', key, iv)
  const plaintext = Buffer.from(JSON.stringify(data, BufferJSON.replacer), 'utf8')
  const ciphertext = Buffer.concat([cipher.update(plaintext), cipher.final()])
  return JSON.stringify({ version: 1, algorithm: 'aes-256-gcm', iv: iv.toString('base64'), tag: cipher.getAuthTag().toString('base64'), data: ciphertext.toString('base64') })
}

function decryptAuthData(envelope, key) {
  const parsed = JSON.parse(envelope)
  if (parsed?.version !== 1 || parsed?.algorithm !== 'aes-256-gcm') throw new Error('unsupported_auth_state_format')
  const decipher = crypto.createDecipheriv('aes-256-gcm', key, Buffer.from(parsed.iv, 'base64'))
  decipher.setAuthTag(Buffer.from(parsed.tag, 'base64'))
  const plaintext = Buffer.concat([decipher.update(Buffer.from(parsed.data, 'base64')), decipher.final()]).toString('utf8')
  return JSON.parse(plaintext, BufferJSON.reviver)
}

async function useEncryptedAuthState(folder, key) {
  await fs.mkdir(folder, { recursive: true })

  const writeDataUnlocked = async (data, file) => {
    const encryptedPath = path.join(folder, `${fixAuthFileName(file)}.enc`)
    const temporaryPath = `${encryptedPath}.${process.pid}.tmp`
    await fs.writeFile(temporaryPath, encryptAuthData(data, key), { encoding: 'utf8', mode: 0o600 })
    await fs.rm(encryptedPath, { force: true })
    await fs.rename(temporaryPath, encryptedPath)
  }
  const writeData = async (data, file) => withAuthFileLock(file, () => writeDataUnlocked(data, file))

  const readData = async file => withAuthFileLock(file, async () => {
    const encryptedPath = path.join(folder, `${fixAuthFileName(file)}.enc`)
    try {
      return decryptAuthData(await fs.readFile(encryptedPath, 'utf8'), key)
    } catch (error) {
      if (error?.code !== 'ENOENT') throw new Error(`auth_state_decrypt_failed:${fixAuthFileName(file)}`)
    }

    const legacyPath = path.join(folder, fixAuthFileName(file))
    try {
      const legacy = JSON.parse(await fs.readFile(legacyPath, 'utf8'), BufferJSON.reviver)
      await writeDataUnlocked(legacy, file)
      await fs.rm(legacyPath, { force: true })
      return legacy
    } catch (error) {
      if (error?.code === 'ENOENT') return null
      throw new Error(`legacy_auth_state_migration_failed:${fixAuthFileName(file)}`)
    }
  })

  const removeData = async file => withAuthFileLock(file, async () => {
    await Promise.all([
      fs.rm(path.join(folder, `${fixAuthFileName(file)}.enc`), { force: true }),
      fs.rm(path.join(folder, fixAuthFileName(file)), { force: true })
    ])
  })

  const storedCreds = await readData('creds.json')
  const creds = storedCreds || initAuthCreds()
  if (!storedCreds) await writeData(creds, 'creds.json')
  return {
    state: {
      creds,
      keys: {
        get: async (type, ids) => {
          const data = {}
          await Promise.all(ids.map(async id => {
            let value = await readData(`${type}-${id}.json`)
            if (type === 'app-state-sync-key' && value) value = proto.Message.AppStateSyncKeyData.create(value)
            data[id] = value
          }))
          return data
        },
        set: async data => {
          const tasks = []
          for (const category in data) for (const id in data[category]) {
            const value = data[category][id]
            const file = `${category}-${id}.json`
            tasks.push(value ? writeData(value, file) : removeData(file))
          }
          await Promise.all(tasks)
        }
      }
    },
    saveCreds: async () => writeData(creds, 'creds.json')
  }
}

function emit(payload) {
  process.stdout.write(`${JSON.stringify(payload)}\n`)
}

function reply(requestId, ok, result = null, error = null) {
  emit({ type: 'response', requestId, ok, result, error })
}

function safeError(error) {
  const message = error instanceof Error ? error.message : String(error ?? 'unknown_error')
  return message.replace(/Bearer\s+[^\s]+/gi, 'Bearer [REDACTED]').slice(0, 1000)
}

function validateAccountId(value) {
  const normalized = String(value ?? '').trim()
  if (!/^[a-zA-Z0-9_-]{1,64}$/.test(normalized)) throw new Error('invalid_account_id')
  return normalized
}

function resolveSessionDir(accountId) {
  const localAppData = process.env.LOCALAPPDATA
  if (!localAppData) throw new Error('LOCALAPPDATA_not_available')
  return path.join(localAppData, 'WAFlow', 'whatsapp-sessions', accountId)
}

function jidFromPhone(phone) {
  const digits = String(phone ?? '').replace(/\D/g, '')
  if (digits.length < 8 || digits.length > 15) throw new Error('invalid_whatsapp_number')
  return `${digits}@s.whatsapp.net`
}

function timestampToIso(value) {
  if (value == null) return new Date().toISOString()
  const seconds = typeof value === 'number' ? value : Number(value?.toString?.() ?? value)
  return Number.isFinite(seconds) ? new Date(seconds * 1000).toISOString() : new Date().toISOString()
}

function messageText(message) {
  if (!message) return ''
  if (message.ephemeralMessage?.message) return messageText(message.ephemeralMessage.message)
  if (message.viewOnceMessage?.message) return messageText(message.viewOnceMessage.message)
  return message.conversation
    ?? message.extendedTextMessage?.text
    ?? message.imageMessage?.caption
    ?? message.videoMessage?.caption
    ?? message.documentMessage?.caption
    ?? message.buttonsResponseMessage?.selectedDisplayText
    ?? message.listResponseMessage?.title
    ?? message.templateButtonReplyMessage?.selectedDisplayText
    ?? ''
}

function messageKind(message) {
  if (!message) return 'unknown'
  if (message.imageMessage) return 'image'
  if (message.videoMessage) return 'video'
  if (message.audioMessage) return 'audio'
  if (message.documentMessage) return 'document'
  if (message.stickerMessage) return 'sticker'
  return 'text'
}

function shouldForward(jid) {
  return Boolean(jid)
    && (jid.endsWith('@s.whatsapp.net') || jid.endsWith('@lid'))
    && jid !== 'status@broadcast'
}

async function resolveDirectJid(key) {
  const alternate = key?.remoteJidAlt ?? ''
  const remote = key?.remoteJid ?? ''
  if (alternate.endsWith('@s.whatsapp.net')) return alternate
  if (remote.endsWith('@s.whatsapp.net')) return remote
  const lid = alternate.endsWith('@lid') ? alternate : remote.endsWith('@lid') ? remote : ''
  if (!lid) return remote || alternate
  try { return await state.socket?.signalRepository?.lidMapping?.getPNForLID(lid) ?? lid }
  catch { return lid }
}

async function forwardMessage(message, source) {
  const sourceJid = message?.key?.remoteJid ?? ''
  const jid = await resolveDirectJid(message?.key)
  if (!shouldForward(jid)) return
  emit({
    type: 'event',
    event: 'message',
    accountId: state.accountId,
    data: {
      id: message.key.id ?? '',
      jid,
      sourceJid,
      phone: jid.endsWith('@s.whatsapp.net') ? jid.split('@')[0] : '',
      fromMe: Boolean(message.key.fromMe),
      participant: message.key.participant ?? '',
      pushName: message.pushName ?? '',
      timestamp: timestampToIso(message.messageTimestamp),
      text: messageText(message.message),
      kind: messageKind(message.message),
      source
    }
  })
}

async function closeSocket() {
  if (state.reconnectTimer) clearTimeout(state.reconnectTimer)
  state.reconnectTimer = null
  const socket = state.socket
  state.socket = null
  if (socket) {
    try { socket.end(new Error('waflow_disconnect')) } catch { }
  }
  state.connection = 'disconnected'
}

async function connect() {
  if (!state.sessionDir) throw new Error('bridge_not_initialized')
  await closeSocket()
  state.manualDisconnect = false
  await fs.mkdir(state.sessionDir, { recursive: true })
  if (!state.authKey) throw new Error('session_encryption_key_missing')
  const { state: auth, saveCreds } = await useEncryptedAuthState(state.sessionDir, state.authKey)
  const { version } = await fetchLatestBaileysVersion()
  const socket = makeWASocket({
    auth,
    version,
    browser: Browsers.windows('WAFlow'),
    logger,
    printQRInTerminal: false,
    markOnlineOnConnect: false,
    syncFullHistory: true,
    generateHighQualityLinkPreview: false
  })
  state.socket = socket
  state.connection = 'connecting'
  emit({ type: 'event', event: 'connection', accountId: state.accountId, data: { state: 'connecting' } })

  socket.ev.on('creds.update', saveCreds)
  socket.ev.on('messages.upsert', async update => {
    for (const message of update.messages ?? []) await forwardMessage(message, update.type ?? 'notify')
  })
  socket.ev.on('messages.update', async updates => {
    for (const update of updates ?? []) {
      const jid = await resolveDirectJid(update.key)
      if (!shouldForward(jid)) continue
      emit({
        type: 'event', event: 'message_status', accountId: state.accountId,
        data: { id: update.key.id ?? '', jid, status: update.update?.status ?? null }
      })
    }
  })
  socket.ev.on('connection.update', async update => {
    if (update.qr) {
      const dataUrl = await QRCode.toDataURL(update.qr, { width: 320, margin: 2, errorCorrectionLevel: 'M' })
      emit({ type: 'event', event: 'qr', accountId: state.accountId, data: { dataUrl } })
    }
    if (update.connection === 'open') {
      state.connection = 'connected'
      emit({
        type: 'event', event: 'connection', accountId: state.accountId,
        data: { state: 'connected', user: socket.user?.id ?? '', name: socket.user?.name ?? '' }
      })
      return
    }
    if (update.connection !== 'close') return
    state.connection = 'disconnected'
    const statusCode = update.lastDisconnect?.error?.output?.statusCode
      ?? update.lastDisconnect?.error?.statusCode
      ?? null
    const loggedOut = statusCode === DisconnectReason.loggedOut
    emit({
      type: 'event', event: 'connection', accountId: state.accountId,
      data: { state: loggedOut ? 'logged_out' : 'disconnected', statusCode, error: safeError(update.lastDisconnect?.error) }
    })
    if (!loggedOut && !state.manualDisconnect) {
      state.reconnectTimer = setTimeout(() => connect().catch(error => emit({ type: 'event', event: 'bridge_error', data: { error: safeError(error) } })), 5000)
    }
  })
}

async function handle(command) {
  const requestId = command.requestId ?? ''
  try {
    switch (command.command) {
      case 'ping':
        reply(requestId, true, { bridge: 'WAFlow.WhatsApp.Bridge', version: '0.1.0', connection: state.connection })
        return
      case 'initialize': {
        state.accountId = validateAccountId(command.accountId ?? 'default')
        state.authKey = parseEncryptionKey(command.encryptionKey)
        state.sessionDir = resolveSessionDir(state.accountId)
        await fs.mkdir(state.sessionDir, { recursive: true })
        reply(requestId, true, { accountId: state.accountId, sessionDir: state.sessionDir })
        return
      }
      case 'connect':
        await connect()
        reply(requestId, true, { state: state.connection })
        return
      case 'disconnect':
        state.manualDisconnect = true
        await closeSocket()
        emit({ type: 'event', event: 'connection', accountId: state.accountId, data: { state: 'disconnected', manual: true } })
        reply(requestId, true, { state: state.connection })
        return
      case 'logout':
        state.manualDisconnect = true
        if (state.socket) await state.socket.logout()
        await closeSocket()
        if (state.sessionDir) await fs.rm(state.sessionDir, { recursive: true, force: true })
        emit({ type: 'event', event: 'connection', accountId: state.accountId, data: { state: 'logged_out', manual: true } })
        reply(requestId, true, { state: 'logged_out' })
        return
      case 'send_text': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        const text = String(command.text ?? '').trim()
        if (!text || text.length > 4096) throw new Error('invalid_message_text')
        const jid = command.jid ? String(command.jid) : jidFromPhone(command.phone)
        if (!shouldForward(jid)) throw new Error('only_individual_contacts_supported')
        const result = await state.socket.sendMessage(jid, { text })
        reply(requestId, true, { id: result?.key?.id ?? '', jid, timestamp: new Date().toISOString() })
        return
      }
      default:
        throw new Error('unknown_command')
    }
  } catch (error) {
    reply(requestId, false, null, { code: safeError(error).split(':')[0], message: safeError(error) })
  }
}

const lines = readline.createInterface({ input: process.stdin, crlfDelay: Infinity })
lines.on('line', line => {
  if (!line.trim()) return
  try { handle(JSON.parse(line)) }
  catch (error) { emit({ type: 'event', event: 'bridge_error', data: { error: safeError(error) } }) }
})
lines.on('close', async () => {
  state.manualDisconnect = true
  await closeSocket()
  process.exit(0)
})

emit({ type: 'event', event: 'ready', data: { bridge: 'WAFlow.WhatsApp.Bridge', version: '0.1.0' } })
