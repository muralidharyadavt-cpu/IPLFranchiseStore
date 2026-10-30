/** @jest-environment node */
const { EventEmitter } = require('node:events');
const { PassThrough } = require('node:stream');
const fs = require('node:fs');
const { verificationConfiguration, withIsolatedApi } = require('../scripts/verificationIsolation.cjs');

beforeEach(() => jest.spyOn(fs, 'existsSync').mockReturnValue(true));
afterEach(() => jest.restoreAllMocks());
function fakeChild() {
  const child = new EventEmitter(); child.stdout = new PassThrough(); child.stderr = new PassThrough(); child.kill = jest.fn(); return child;
}
function start(child, address = 'http://127.0.0.1:54321') {
  return jest.fn(() => { setImmediate(() => child.stdout.write(`Now listening on: ${address}\n`)); return child; });
}
test('each run owns a unique database with highest-priority CLI configuration', () => {
  const one = verificationConfiguration({ Cosmos__DatabaseName: 'IPLFranchiseStore', ASPNETCORE_ENVIRONMENT: 'Production' }); const two = verificationConfiguration({});
  expect(one.databaseName).toMatch(/^IPLFranchiseStoreVerification-[0-9a-f]{32}$/); expect(two.databaseName).not.toBe(one.databaseName);
  expect(one.args).toEqual(expect.arrayContaining(['--Cosmos:DatabaseName', one.databaseName, '--Cosmos:Endpoint', 'https://localhost:8081', '--urls', 'http://127.0.0.1:0', '--environment', 'Development']));
  expect(one.options.env.ASPNETCORE_ENVIRONMENT).toBe('Development'); expect(one.options.windowsHide).toBe(true);
});
test('external endpoint overrides abort before any child or mutation', async () => {
  const spawnProcess = jest.fn(); const action = jest.fn(); await expect(withIsolatedApi(action, { env: { LIVE_API_URL: 'http://localhost:5001' }, spawnProcess })).rejects.toThrow('LIVE_API_URL'); expect(spawnProcess).not.toHaveBeenCalled(); expect(action).not.toHaveBeenCalled();
});
test('missing build aborts before starting an API', async () => {
  fs.existsSync.mockReturnValue(false); const spawnProcess = jest.fn(); await expect(withIsolatedApi(jest.fn(), { env: {}, spawnProcess })).rejects.toThrow('Build the backend'); expect(spawnProcess).not.toHaveBeenCalled();
});
test('does not attach to a pre-existing port 5001 API', async () => {
  const child = fakeChild(); const fetchImpl = jest.fn().mockResolvedValue({ ok: true });
  await withIsolatedApi(async session => { expect(session.base).toBe('http://127.0.0.1:54321'); await session.fetch('/api/v1/products'); }, { env: {}, spawnProcess: start(child), fetchImpl });
  expect(fetchImpl.mock.calls.every(call => call[0].startsWith('http://127.0.0.1:54321/'))).toBe(true); expect(child.kill).toHaveBeenCalledTimes(1);
});
test('startup failure aborts action before writes and stops only the owned child', async () => {
  const child = fakeChild(); const action = jest.fn(); const spawnProcess = jest.fn(() => { setImmediate(() => child.emit('exit', 1)); return child; }); const fetchImpl = jest.fn();
  await expect(withIsolatedApi(action, { env: {}, spawnProcess, fetchImpl })).rejects.toThrow('exited before'); expect(action).not.toHaveBeenCalled(); expect(fetchImpl).not.toHaveBeenCalled();
});
test('unhealthy owned database aborts before invoking the mutation flow', async () => {
  const child = fakeChild(); const action = jest.fn(); const fetchImpl = jest.fn().mockResolvedValue({ ok: false }); await expect(withIsolatedApi(action, { env: {}, spawnProcess: start(child), fetchImpl })).rejects.toThrow('not available'); expect(action).not.toHaveBeenCalled(); expect(fetchImpl).toHaveBeenCalledTimes(1); expect(child.kill).toHaveBeenCalledTimes(1);
});
test('unknown listening address fails closed before any request', async () => {
  const child = fakeChild(); const action = jest.fn(); const fetchImpl = jest.fn(); await expect(withIsolatedApi(action, { env: {}, spawnProcess: start(child, 'http://localhost:5001'), fetchImpl, timeoutMs: 10 })).rejects.toThrow('did not become ready'); expect(action).not.toHaveBeenCalled(); expect(fetchImpl).not.toHaveBeenCalled(); expect(child.kill).toHaveBeenCalledTimes(1);
});
test('foreign mutation URL and stopped child are blocked before fetch', async () => {
  const child = fakeChild(); const fetchImpl = jest.fn().mockResolvedValue({ ok: true });
  await withIsolatedApi(async session => {
    expect(() => session.fetch('http://localhost:5000/api/v1/orders/checkout', { method: 'POST' })).toThrow('escaped');
    expect(() => session.fetch('/outside-api', { method: 'POST' })).toThrow('escaped');
    child.emit('exit', 1); expect(() => session.fetch('/api/v1/orders/checkout', { method: 'POST' })).toThrow('escaped');
  }, { env: {}, spawnProcess: start(child), fetchImpl }); expect(fetchImpl).toHaveBeenCalledTimes(1);
});
test('flow failures stop the owned process without deleting any database', async () => {
  const child = fakeChild(); await expect(withIsolatedApi(async () => { throw new Error('Flow failed'); }, { env: {}, spawnProcess: start(child), fetchImpl: jest.fn().mockResolvedValue({ ok: true }) })).rejects.toThrow('Flow failed'); expect(child.kill).toHaveBeenCalledTimes(1);
});
