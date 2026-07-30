const DEFAULT_TIMEOUT_MS = 8000
export const BUNDLED_VALIDATED_VERSION = [2, 3000, 1043857760]

function validVersion(value) {
  return Array.isArray(value)
    && value.length === 3
    && value.every(part => Number.isInteger(part) && part >= 0)
}

export async function resolveBaileysVersion(fetchVersion, options = {}) {
  const disabled = options.disabled === true
    || process.env.WAFLOW_BAILEYS_VERSION_LOOKUP_DISABLED === '1'
  if (disabled) {
    return {
      version: BUNDLED_VALIDATED_VERSION,
      source: 'bundled',
      warning: 'online_version_lookup_disabled'
    }
  }

  const timeoutMs = Number.isFinite(options.timeoutMs)
    ? Math.max(50, Number(options.timeoutMs))
    : DEFAULT_TIMEOUT_MS
  let timer
  try {
    const result = await Promise.race([
      Promise.resolve().then(fetchVersion),
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error('version_lookup_timeout')), timeoutMs)
      })
    ])
    if (!validVersion(result?.version)) throw new Error('invalid_version_response')
    return { version: result.version, source: 'remote', warning: '' }
  } catch (error) {
    return {
      version: BUNDLED_VALIDATED_VERSION,
      source: 'bundled',
      warning: String(error?.message ?? error ?? 'version_lookup_failed')
    }
  } finally {
    if (timer) clearTimeout(timer)
  }
}
