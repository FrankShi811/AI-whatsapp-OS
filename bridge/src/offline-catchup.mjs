export const DEFAULT_OFFLINE_CATCHUP_TIMEOUT_MS = 20000

export class OfflineCatchupCoordinator {
  constructor({ timeoutMs = DEFAULT_OFFLINE_CATCHUP_TIMEOUT_MS, enqueue, emitStatus, emitIssue, emitSnapshot, getTotals }) {
    this.timeoutMs = timeoutMs
    this.enqueue = enqueue
    this.emitStatus = emitStatus
    this.emitIssue = emitIssue
    this.emitSnapshot = emitSnapshot
    this.getTotals = getTotals
    this.active = null
  }

  cancel() {
    if (this.active?.timer) clearTimeout(this.active.timer)
    this.active = null
  }

  async start({ socket, attempt, source, existingSession }) {
    this.cancel()
    if (!existingSession || !socket) return false

    const active = { socket, attempt, source, received: false, timer: null }
    this.active = active
    this.emitStatus({ state: 'syncing', phase: 'offline_messages', progress: null, source })

    try {
      // A temporary "available" presence makes Baileys send its unified-session
      // request. WhatsApp then flushes messages queued while this desktop was
      // offline. We switch back to "unavailable" after the queue is drained so
      // the phone keeps receiving its own notifications.
      await socket.sendPresenceUpdate('available')
    } catch (error) {
      if (this.active !== active) return false
      this.emitIssue({
        code: 'offline_catchup_presence_failed',
        recoverable: true,
        message: '离线消息补齐请求暂未送达，程序将继续保持实时监听',
        error
      })
    }

    if (this.active !== active) return false
    if (active.received) {
      this.enqueue(() => this.finish(active, false))
      return true
    }

    active.timer = setTimeout(() => {
      this.enqueue(() => this.finish(active, true))
    }, this.timeoutMs)
    return true
  }

  receivePending({ socket, attempt }) {
    const active = this.active
    if (!active || active.socket !== socket || active.attempt !== attempt) return false
    active.received = true
    if (active.timer) {
      clearTimeout(active.timer)
      active.timer = null
    }
    this.enqueue(() => this.finish(active, false))
    return true
  }

  async finish(active, timedOut) {
    if (this.active !== active) return false
    this.active = null
    if (active.timer) clearTimeout(active.timer)

    try { await active.socket.sendPresenceUpdate('unavailable') } catch { }
    const counts = await this.emitSnapshot(`catchup:${active.source}`)
    this.emitStatus({
      state: 'complete',
      phase: timedOut ? 'offline_messages_timeout' : 'offline_messages',
      progress: 100,
      source: active.source,
      pendingNotificationsReceived: !timedOut,
      ...counts,
      ...this.getTotals()
    })
    return true
  }
}
