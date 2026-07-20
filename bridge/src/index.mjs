import readline from 'node:readline'
import path from 'node:path'
import fs from 'node:fs/promises'
import crypto from 'node:crypto'
import process from 'node:process'
import QRCode from 'qrcode'
import pino from 'pino'
import makeWASocket, {
  ALL_WA_PATCH_NAMES,
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
  authKey: null,
  existingSession: false,
  contacts: new Map(),
  chats: new Map(),
  historyTotals: { contacts: 0, chats: 0, messages: 0 },
  syncQueue: Promise.resolve()
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

function isUnreadableAuthState(error) {
  const message = error instanceof Error ? error.message : String(error ?? '')
  return message.startsWith('auth_state_decrypt_failed:') || message.startsWith('legacy_auth_state_migration_failed:')
}

async function recoverUnreadableAuthState(error) {
  const reason = safeError(error)
  const suffix = new Date().toISOString().replace(/[:.]/g, '-').replace('T', '_').replace('Z', '')
  const backupDir = `${state.sessionDir}.unreadable-${suffix}`
  await fs.rename(state.sessionDir, backupDir)
  await fs.mkdir(state.sessionDir, { recursive: true })
  state.existingSession = false
  const data = {
    reason: 'local_session_unreadable',
    detail: reason,
    backupName: path.basename(backupDir),
    requiresQr: true
  }
  emit({ type: 'event', event: 'auth_recovery', accountId: state.accountId, data })
  return data
}

async function loadAuthStateWithRecovery() {
  try {
    return { ...(await useEncryptedAuthState(state.sessionDir, state.authKey)), recovered: false, recovery: null }
  } catch (error) {
    if (!isUnreadableAuthState(error)) throw error
    const recovery = await recoverUnreadableAuthState(error)
    return { ...(await useEncryptedAuthState(state.sessionDir, state.authKey)), recovered: true, recovery }
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

function enqueueSync(action, phase) {
  state.syncQueue = state.syncQueue
    .then(action)
    .catch(error => emit({ type: 'event', event: 'sync_status', accountId: state.accountId, data: { state: 'failed', phase, error: safeError(error) } }))
  return state.syncQueue
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
  if (!Number.isFinite(seconds)) return new Date().toISOString()
  return new Date(Math.abs(seconds) >= 1_000_000_000_000 ? seconds : seconds * 1000).toISOString()
}

function phoneFromJid(jid) {
  return String(jid ?? '').endsWith('@s.whatsapp.net') ? String(jid).split('@')[0].split(':')[0].replace(/\D/g, '') : ''
}

function firstNonEmpty(...values) {
  return values.map(value => String(value ?? '').trim()).find(Boolean) ?? ''
}

function syncTypeName(value) {
  const names = ['initial_bootstrap', 'initial_status', 'full', 'recent', 'push_name', 'non_blocking_data', 'on_demand']
  const numeric = Number(value)
  return Number.isInteger(numeric) && names[numeric] ? names[numeric] : String(value ?? 'unknown')
}

function emitItems(event, items, source = 'live', extra = {}, chunkSize = 100) {
  for (let offset = 0; offset < items.length; offset += chunkSize) {
    emit({
      type: 'event', event, accountId: state.accountId,
      data: { items: items.slice(offset, offset + chunkSize), source, ...extra }
    })
  }
}

function messageContent(message) {
  if (!message) return null
  if (message.ephemeralMessage?.message) return messageContent(message.ephemeralMessage.message)
  if (message.viewOnceMessage?.message) return messageContent(message.viewOnceMessage.message)
  if (message.viewOnceMessageV2?.message) return messageContent(message.viewOnceMessageV2.message)
  if (message.documentWithCaptionMessage?.message) return messageContent(message.documentWithCaptionMessage.message)
  return message
}

function messageText(message) {
  message = messageContent(message)
  if (!message) return ''
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
  message = messageContent(message)
  if (!message) return 'unknown'
  if (message.imageMessage) return 'image'
  if (message.videoMessage) return 'video'
  if (message.audioMessage) return 'audio'
  if (message.documentMessage) return 'document'
  if (message.stickerMessage) return 'sticker'
  return 'text'
}

function messageFileName(message) {
  message = messageContent(message)
  return firstNonEmpty(
    message?.documentMessage?.fileName,
    message?.imageMessage?.fileName,
    message?.videoMessage?.fileName,
    message?.audioMessage?.fileName
  )
}

function messageMimeType(message) {
  message = messageContent(message)
  return firstNonEmpty(
    message?.documentMessage?.mimetype,
    message?.imageMessage?.mimetype,
    message?.videoMessage?.mimetype,
    message?.audioMessage?.mimetype,
    message?.stickerMessage?.mimetype
  )
}

const mediaTypes = new Map(Object.entries({
  '.jpg': ['image', 'image/jpeg'], '.jpeg': ['image', 'image/jpeg'], '.png': ['image', 'image/png'], '.webp': ['image', 'image/webp'], '.gif': ['video', 'image/gif'],
  '.mp4': ['video', 'video/mp4'], '.3gp': ['video', 'video/3gpp'], '.mov': ['video', 'video/quicktime'],
  '.mp3': ['audio', 'audio/mpeg'], '.m4a': ['audio', 'audio/mp4'], '.ogg': ['audio', 'audio/ogg'], '.opus': ['audio', 'audio/ogg; codecs=opus'], '.wav': ['audio', 'audio/wav'], '.aac': ['audio', 'audio/aac'],
  '.pdf': ['document', 'application/pdf'], '.txt': ['document', 'text/plain'], '.csv': ['document', 'text/csv'], '.json': ['document', 'application/json'],
  '.doc': ['document', 'application/msword'], '.docx': ['document', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'],
  '.xls': ['document', 'application/vnd.ms-excel'], '.xlsx': ['document', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'],
  '.ppt': ['document', 'application/vnd.ms-powerpoint'], '.pptx': ['document', 'application/vnd.openxmlformats-officedocument.presentationml.presentation'],
  '.zip': ['document', 'application/zip'], '.rar': ['document', 'application/vnd.rar'], '.7z': ['document', 'application/x-7z-compressed']
}))

async function buildMediaMessage(filePath, caption) {
  const resolved = path.resolve(String(filePath ?? ''))
  const info = await fs.stat(resolved)
  if (!info.isFile()) throw new Error('attachment_is_not_a_file')
  if (info.size <= 0 || info.size > 100 * 1024 * 1024) throw new Error('attachment_size_must_be_between_1_byte_and_100mb')
  const fileName = path.basename(resolved)
  const extension = path.extname(fileName).toLowerCase()
  const mediaType = mediaTypes.get(extension)
  if (!mediaType) throw new Error('unsupported_attachment_type')
  const [kind, mimeType] = mediaType
  const data = await fs.readFile(resolved)
  const safeCaption = String(caption ?? '').trim().slice(0, 1024)
  if (kind === 'image') return { payload: { image: data, mimetype: mimeType, caption: safeCaption }, kind, mimeType, fileName }
  if (kind === 'video') return { payload: { video: data, mimetype: mimeType, caption: safeCaption }, kind, mimeType, fileName }
  if (kind === 'audio') return { payload: { audio: data, mimetype: mimeType, ptt: false }, kind, mimeType, fileName }
  return { payload: { document: data, mimetype: mimeType, fileName, caption: safeCaption }, kind, mimeType, fileName }
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

async function resolveUserJid(...values) {
  const candidates = values.flat().filter(Boolean).map(value => String(value))
  const phoneJid = candidates.find(value => value.endsWith('@s.whatsapp.net'))
  if (phoneJid) return phoneJid
  const lid = candidates.find(value => value.endsWith('@lid'))
  if (!lid) return ''
  try { return await state.socket?.signalRepository?.lidMapping?.getPNForLID(lid) ?? lid }
  catch { return lid }
}

async function normalizeContact(contact, source = 'live') {
  const sourceJid = String(contact?.id ?? contact?.lid ?? contact?.phoneNumber ?? '')
  const jid = await resolveUserJid(contact?.phoneNumber, contact?.id, contact?.lid)
  if (!shouldForward(jid || sourceJid)) return null
  const phone = phoneFromJid(jid)
  const displayName = firstNonEmpty(contact?.name, contact?.notify, contact?.verifiedName, contact?.username, phone ? `+${phone}` : sourceJid)
  return {
    jid: jid || sourceJid,
    sourceJid,
    phone,
    displayName,
    savedName: String(contact?.name ?? ''),
    notifyName: String(contact?.notify ?? ''),
    verifiedName: String(contact?.verifiedName ?? ''),
    username: String(contact?.username ?? ''),
    source
  }
}

async function normalizeChat(chat, source = 'live') {
  const sourceJid = String(chat?.id ?? chat?.lidJid ?? chat?.pnJid ?? '')
  const jid = await resolveUserJid(chat?.pnJid, chat?.id, chat?.lidJid)
  if (!shouldForward(jid || sourceJid)) return null
  const phone = phoneFromJid(jid)
  if (!phone) return null
  const cachedContact = [...state.contacts.values()].find(item => item.phone === phone || item.jid === jid || item.sourceJid === sourceJid)
  const embedded = chat?.messages?.[0]?.message
  const timestamp = chat?.conversationTimestamp ?? chat?.lastMsgTimestamp ?? chat?.lastMessageRecvTimestamp
  return {
    jid,
    sourceJid,
    phone,
    displayName: firstNonEmpty(cachedContact?.savedName, cachedContact?.displayName, chat?.name, chat?.displayName, `+${phone}`),
    lastMessage: embedded ? messageText(embedded.message) : '',
    lastMessageAt: timestamp == null ? '' : timestampToIso(timestamp),
    unreadCount: Number.isFinite(Number(chat?.unreadCount)) ? Number(chat.unreadCount) : null,
    archived: Boolean(chat?.archived),
    ...(chat?.pinned !== undefined && chat?.pinned !== null ? {
      pinned: Number(chat.pinned) > 0,
      pinnedAt: Number(chat.pinned) > 0 ? timestampToIso(chat.pinned) : ''
    } : {}),
    source
  }
}

async function normalizeMessage(message, source) {
  const sourceJid = message?.key?.remoteJid ?? ''
  const jid = await resolveDirectJid(message?.key)
  if (!shouldForward(jid)) return null
  return {
    id: message.key.id ?? '',
    jid,
    sourceJid,
    phone: phoneFromJid(jid),
    fromMe: Boolean(message.key.fromMe),
    participant: message.key.participant ?? '',
    pushName: message.pushName ?? '',
    timestamp: timestampToIso(message.messageTimestamp),
    text: messageText(message.message),
    kind: messageKind(message.message),
    fileName: messageFileName(message.message),
    mimeType: messageMimeType(message.message),
    status: message.status ?? null,
    deliveredAt: latestReceiptTime(message.userReceipt, 'receiptTimestamp'),
    readAt: latestReceiptTime(message.userReceipt, 'readTimestamp', 'playedTimestamp'),
    source
  }
}

function latestReceiptTime(receipts, ...fields) {
  let latest = null
  for (const receipt of receipts ?? []) for (const field of fields) {
    const value = receipt?.[field]
    if (value == null) continue
    const numeric = Number(value?.toString?.() ?? value)
    if (Number.isFinite(numeric) && (latest == null || numeric > latest)) latest = numeric
  }
  return latest == null ? '' : timestampToIso(latest)
}

function rememberContact(contact) {
  if (!contact) return
  const key = contact.sourceJid || contact.jid || contact.phone
  const existing = state.contacts.get(key) ?? {}
  const merged = { ...existing }
  for (const [name, value] of Object.entries(contact)) if (value !== '' && value != null) merged[name] = value
  merged.displayName = firstNonEmpty(merged.savedName, merged.notifyName, merged.verifiedName, merged.username, merged.displayName, merged.phone ? `+${merged.phone}` : key)
  state.contacts.set(key, merged)
}

function rememberChat(chat) {
  if (!chat) return
  const key = chat.phone || chat.jid
  const existing = state.chats.get(key) ?? {}
  state.chats.set(key, {
    ...existing,
    ...chat,
    displayName: chat.displayName || existing.displayName || `+${chat.phone}`,
    lastMessage: chat.lastMessage || existing.lastMessage || '',
    lastMessageAt: chat.lastMessageAt || existing.lastMessageAt || ''
  })
}

async function normalizeContacts(contacts, source) {
  const items = (await Promise.all((contacts ?? []).map(contact => normalizeContact(contact, source)))).filter(Boolean)
  for (const item of items) rememberContact(item)
  return items
}

async function normalizeChats(chats, source) {
  const items = (await Promise.all((chats ?? []).map(chat => normalizeChat(chat, source)))).filter(Boolean)
  for (const item of items) rememberChat(item)
  return items
}

async function normalizeMessages(messages, source) {
  const items = (await Promise.all((messages ?? []).map(message => normalizeMessage(message, source)))).filter(item => item?.phone && item?.id)
  for (const item of items) {
    const contact = [...state.contacts.values()].find(value => value.phone === item.phone)
    if (!item.fromMe && item.pushName) rememberContact({ jid: item.jid, sourceJid: item.sourceJid, phone: item.phone, displayName: item.pushName, notifyName: item.pushName, source })
    rememberChat({ jid: item.jid, sourceJid: item.sourceJid, phone: item.phone, displayName: contact?.displayName || item.pushName || `+${item.phone}`, lastMessage: item.text || `[${item.kind}]`, lastMessageAt: item.timestamp, unreadCount: null, source })
  }
  return items
}

async function forwardMessage(message, source) {
  const data = await normalizeMessage(message, source)
  if (!data?.phone || !data.id) return
  if (!data.fromMe && data.pushName) rememberContact({ jid: data.jid, sourceJid: data.sourceJid, phone: data.phone, displayName: data.pushName, notifyName: data.pushName, source })
  const contact = [...state.contacts.values()].find(value => value.phone === data.phone)
  rememberChat({ jid: data.jid, sourceJid: data.sourceJid, phone: data.phone, displayName: contact?.displayName || data.pushName || `+${data.phone}`, lastMessage: data.text || `[${data.kind}]`, lastMessageAt: data.timestamp, unreadCount: null, source })
  emit({
    type: 'event',
    event: 'message',
    accountId: state.accountId,
    data
  })
}

async function handleHistorySync(update) {
  const phase = syncTypeName(update?.syncType)
  emit({ type: 'event', event: 'sync_status', accountId: state.accountId, data: { state: 'syncing', phase, progress: update?.progress ?? null } })
  const contacts = await normalizeContacts(update?.contacts, `history:${phase}`)
  const chats = await normalizeChats(update?.chats, `history:${phase}`)
  const messages = await normalizeMessages(update?.messages, `history:${phase}`)
  state.historyTotals.contacts += contacts.length
  state.historyTotals.chats += chats.length
  state.historyTotals.messages += messages.length
  emitItems('contacts_upsert', contacts, `history:${phase}`)
  emitItems('chats_upsert', chats, `history:${phase}`)
  emitItems('messages_history', messages, `history:${phase}`)
  emit({
    type: 'event', event: 'sync_status', accountId: state.accountId,
    data: {
      state: update?.isLatest ? 'complete' : 'syncing', phase, progress: update?.progress ?? null,
      contacts: state.contacts.size, chats: state.chats.size, messages: state.historyTotals.messages,
      isLatest: Boolean(update?.isLatest)
    }
  })
}

async function emitCachedSnapshot(source = 'manual') {
  const contacts = [...state.contacts.values()]
  const chats = [...state.chats.values()]
  emitItems('contacts_upsert', contacts, source)
  emitItems('chats_upsert', chats, source)
  return { contacts: contacts.length, chats: chats.length }
}

async function manualSync() {
  emit({ type: 'event', event: 'sync_status', accountId: state.accountId, data: { state: 'syncing', phase: 'app_state', progress: null } })
  try {
    await state.socket?.resyncAppState?.(ALL_WA_PATCH_NAMES, false)
    const counts = await emitCachedSnapshot('manual')
    emit({
      type: 'event', event: 'sync_status', accountId: state.accountId,
      data: { state: 'complete', phase: 'app_state', progress: 100, ...counts, messages: state.historyTotals.messages, existingSession: state.existingSession }
    })
  } catch (error) {
    emit({ type: 'event', event: 'sync_status', accountId: state.accountId, data: { state: 'failed', phase: 'app_state', error: safeError(error), existingSession: state.existingSession } })
  }
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
  const { state: auth, saveCreds } = await loadAuthStateWithRecovery()
  state.existingSession = Boolean(auth.creds.registered && (auth.creds.accountSyncCounter ?? 0) > 0)
  state.contacts.clear()
  state.chats.clear()
  state.historyTotals = { contacts: 0, chats: 0, messages: 0 }
  const { version } = await fetchLatestBaileysVersion()
  const socket = makeWASocket({
    auth,
    version,
    browser: Browsers.windows('WAFlow'),
    logger,
    printQRInTerminal: false,
    markOnlineOnConnect: false,
    syncFullHistory: true,
    shouldSyncHistoryMessage: () => true,
    generateHighQualityLinkPreview: false
  })
  state.socket = socket
  state.connection = 'connecting'
  emit({ type: 'event', event: 'connection', accountId: state.accountId, data: { state: 'connecting' } })

  socket.ev.on('creds.update', saveCreds)
  socket.ev.on('messages.upsert', update => {
    enqueueSync(async () => {
      for (const message of update.messages ?? []) await forwardMessage(message, update.type ?? 'notify')
    }, 'messages')
  })
  socket.ev.on('messaging-history.set', update => {
    enqueueSync(() => handleHistorySync(update), 'history')
  })
  socket.ev.on('messaging-history.status', update => {
    enqueueSync(async () => {
      emit({ type: 'event', event: 'sync_status', accountId: state.accountId, data: { state: update.status === 'complete' ? 'complete' : 'paused', phase: syncTypeName(update.syncType), progress: update.explicit ? 100 : null, explicit: update.explicit, contacts: state.contacts.size, chats: state.chats.size, messages: state.historyTotals.messages } })
    }, 'history_status')
  })
  socket.ev.on('contacts.upsert', contacts => {
    enqueueSync(async () => {
      const items = await normalizeContacts(contacts, 'live')
      emitItems('contacts_upsert', items, 'live')
    }, 'contacts')
  })
  socket.ev.on('contacts.update', contacts => {
    enqueueSync(async () => {
      const items = await normalizeContacts(contacts, 'live_update')
      emitItems('contacts_upsert', items, 'live_update')
    }, 'contacts')
  })
  socket.ev.on('chats.upsert', chats => {
    enqueueSync(async () => {
      const items = await normalizeChats(chats, 'live')
      emitItems('chats_upsert', items, 'live')
    }, 'chats')
  })
  socket.ev.on('chats.update', chats => {
    enqueueSync(async () => {
      const items = await normalizeChats(chats, 'live_update')
      emitItems('chats_upsert', items, 'live_update')
    }, 'chats')
  })
  socket.ev.on('lid-mapping.update', mapping => {
    enqueueSync(async () => {
      const lid = String(mapping?.lid ?? '')
      const jid = String(mapping?.pn ?? '')
      const phone = phoneFromJid(jid)
      if (!lid || !phone) return
      const contacts = [...state.contacts.values()].filter(item => item.jid === lid || item.sourceJid === lid)
      for (const item of contacts) rememberContact({ ...item, jid, phone, source: 'lid_mapping' })
      const chats = [...state.chats.values()].filter(item => item.jid === lid || item.sourceJid === lid)
      for (const item of chats) rememberChat({ ...item, jid, phone, source: 'lid_mapping' })
      emitItems('contacts_upsert', contacts.map(item => ({ ...item, jid, phone, source: 'lid_mapping' })), 'lid_mapping')
      emitItems('chats_upsert', chats.map(item => ({ ...item, jid, phone, source: 'lid_mapping' })), 'lid_mapping')
    }, 'lid_mapping')
  })
  socket.ev.on('messages.update', async updates => {
    for (const update of updates ?? []) {
      const jid = await resolveDirectJid(update.key)
      if (!shouldForward(jid)) continue
      const numericStatus = update.update?.status ?? null
      emit({
        type: 'event', event: 'message_status', accountId: state.accountId,
        data: {
          id: update.key.id ?? '', jid, status: numericStatus,
          statusAt: new Date().toISOString(),
          deliveredAt: Number(numericStatus) >= 3 ? new Date().toISOString() : '',
          readAt: Number(numericStatus) >= 4 ? new Date().toISOString() : '',
          failureReason: Number(numericStatus) === 0 ? 'WhatsApp 返回发送错误' : ''
        }
      })
    }
  })
  socket.ev.on('message-receipt.update', async updates => {
    for (const update of updates ?? []) {
      const jid = await resolveDirectJid(update.key)
      if (!shouldForward(jid)) continue
      const deliveredAt = update.receipt?.receiptTimestamp == null ? '' : timestampToIso(update.receipt.receiptTimestamp)
      const readValue = update.receipt?.readTimestamp ?? update.receipt?.playedTimestamp
      const readAt = readValue == null ? '' : timestampToIso(readValue)
      const status = readAt ? 4 : deliveredAt ? 3 : null
      if (status == null) continue
      emit({
        type: 'event', event: 'message_status', accountId: state.accountId,
        data: { id: update.key.id ?? '', jid, status, statusAt: new Date().toISOString(), deliveredAt, readAt, failureReason: '' }
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
        data: { state: 'connected', user: socket.user?.id ?? '', name: socket.user?.name ?? '', existingSession: state.existingSession }
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
        reply(requestId, true, { bridge: 'WAFlow.WhatsApp.Bridge', version: '0.4.0', connection: state.connection })
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
      case 'validate_session': {
        if (!state.sessionDir || !state.authKey) throw new Error('bridge_not_initialized')
        const result = await loadAuthStateWithRecovery()
        reply(requestId, true, { recovered: result.recovered, recovery: result.recovery })
        return
      }
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
        reply(requestId, true, { id: result?.key?.id ?? '', jid, timestamp: new Date().toISOString(), status: result?.status ?? 2 })
        return
      }
      case 'send_media': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        const jid = command.jid ? String(command.jid) : jidFromPhone(command.phone)
        if (!shouldForward(jid)) throw new Error('only_individual_contacts_supported')
        const media = await buildMediaMessage(command.path, command.caption)
        const result = await state.socket.sendMessage(jid, media.payload)
        reply(requestId, true, { id: result?.key?.id ?? '', jid, timestamp: new Date().toISOString(), status: result?.status ?? 2, kind: media.kind, mimeType: media.mimeType, fileName: media.fileName })
        return
      }
      case 'set_chat_pin': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        const jid = command.jid ? String(command.jid) : jidFromPhone(command.phone)
        if (!shouldForward(jid)) throw new Error('only_individual_contacts_supported')
        const pinned = Boolean(command.pinned)
        await state.socket.chatModify({ pin: pinned }, jid)
        const chat = state.chats.get(phoneFromJid(jid))
        if (chat) rememberChat({ ...chat, pinned, pinnedAt: pinned ? new Date().toISOString() : '' })
        reply(requestId, true, { jid, pinned })
        return
      }
      case 'sync_now': {
        if (!state.socket || state.connection !== 'connected') throw new Error('whatsapp_not_connected')
        enqueueSync(manualSync, 'manual')
        reply(requestId, true, { state: 'started', existingSession: state.existingSession, contacts: state.contacts.size, chats: state.chats.size })
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

emit({ type: 'event', event: 'ready', data: { bridge: 'WAFlow.WhatsApp.Bridge', version: '0.4.0' } })
