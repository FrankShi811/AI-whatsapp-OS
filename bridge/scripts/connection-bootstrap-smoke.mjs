import assert from 'node:assert/strict'
import { BUNDLED_VALIDATED_VERSION, resolveBaileysVersion } from '../src/connection-bootstrap.mjs'

const remote = await resolveBaileysVersion(
  async () => ({ version: [2, 3000, 123456789] }),
  { timeoutMs: 100 }
)
assert.deepEqual(remote.version, [2, 3000, 123456789])
assert.equal(remote.source, 'remote')

const startedAt = Date.now()
const timeout = await resolveBaileysVersion(
  () => new Promise(() => {}),
  { timeoutMs: 60 }
)
assert.deepEqual(timeout.version, BUNDLED_VALIDATED_VERSION)
assert.equal(timeout.source, 'bundled')
assert.match(timeout.warning, /version_lookup_timeout/)
assert.ok(Date.now() - startedAt < 500)

const rejected = await resolveBaileysVersion(
  async () => { throw new Error('network_blocked') },
  { timeoutMs: 100 }
)
assert.equal(rejected.source, 'bundled')
assert.deepEqual(rejected.version, BUNDLED_VALIDATED_VERSION)
assert.match(rejected.warning, /network_blocked/)

const invalid = await resolveBaileysVersion(
  async () => ({ version: [] }),
  { timeoutMs: 100 }
)
assert.equal(invalid.source, 'bundled')
assert.deepEqual(invalid.version, BUNDLED_VALIDATED_VERSION)
assert.match(invalid.warning, /invalid_version_response/)

const disabled = await resolveBaileysVersion(
  async () => { throw new Error('must_not_run') },
  { timeoutMs: 100, disabled: true }
)
assert.equal(disabled.source, 'bundled')
assert.deepEqual(disabled.version, BUNDLED_VALIDATED_VERSION)
assert.equal(disabled.warning, 'online_version_lookup_disabled')

console.log('PASS WhatsApp connection bootstrap timeout and bundled-version fallback')
