const { spawn } = require('node:child_process');
const { randomUUID } = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

function verificationConfiguration(env = process.env) {
  if (env.LIVE_API_URL) throw new Error('LIVE_API_URL is not accepted. Verification must own its API process and database.');
  const databaseName = 'IPLFranchiseStoreVerification-' + randomUUID().replaceAll('-', '');
  const apiDirectory = path.resolve(__dirname, '../../backend/IPLFranchiseStore.Api');
  const dll = path.join(apiDirectory, 'bin/Debug/net10.0/IPLFranchiseStore.Api.dll');
  if (!fs.existsSync(dll)) throw new Error('Build the backend with dotnet build before verification.');
  return {
    databaseName,
    args: [dll, '--urls', 'http://127.0.0.1:0', '--environment', 'Development',
      '--Cosmos:Endpoint', 'https://localhost:8081', '--Cosmos:DatabaseName', databaseName,
      '--Cosmos:ContainerName', 'StoreData', '--Logging:LogLevel:Microsoft.Hosting.Lifetime', 'Information'],
    options: { cwd: apiDirectory, env: { ...env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development' }, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] },
  };
}

async function withIsolatedApi(action, { spawnProcess = spawn, fetchImpl = globalThis.fetch, env = process.env, timeoutMs = 45000 } = {}) {
  const config = verificationConfiguration(env);
  // Start the built application directly (no launch profile). Command-line Cosmos settings
  // override inherited environment/User Secrets. Port 0 cannot attach to an existing API.
  const child = spawnProcess('dotnet', config.args, config.options);
  let exited = false;
  child.on('exit', () => { exited = true; });
  try {
    const base = await new Promise((resolve, reject) => {
      let text = '';
      const timer = setTimeout(() => reject(new Error('Owned verification API did not become ready. No verification writes were sent.')), timeoutMs);
      const fail = () => { clearTimeout(timer); reject(new Error('Owned verification API exited before becoming ready.')); };
      child.once('exit', fail);
      child.once('error', fail);
      child.stdout.on('data', data => {
        text = (text + data.toString()).slice(-16000);
        const match = text.match(/Now listening on: (http:\/\/127\.0\.0\.1:([1-9]\d*))\r?\n/);
        if (match && Number(match[2]) <= 65535) {
          clearTimeout(timer); child.removeListener('exit', fail);
          resolve(match[1]);
        }
      });
      // Consume stderr without echoing arbitrary SDK diagnostics/credentials into the report.
      child.stderr.on('data', () => {});
    });
    const health = await fetchImpl(base + '/api/v1/health/dependencies', { signal: AbortSignal.timeout(10000) });
    if (!health.ok || exited) throw new Error('Owned test database is not available. No verification writes were sent.');
    const ownedFetch = (url, options = {}) => {
      const target = new URL(url, base);
      if (exited || target.origin !== base || !target.pathname.startsWith('/api/v1/'))
        throw new Error('Verification request escaped the owned API. Request blocked.');
      return fetchImpl(target.href, options);
    };
    console.log('Test-owned database: ' + config.databaseName + '; API: ' + base);
    return await action({ base, databaseName: config.databaseName, fetch: ownedFetch });
  } finally {
    // Only our child is stopped. No database is deleted (including on setup failure).
    if (!exited) child.kill();
  }
}
module.exports = { verificationConfiguration, withIsolatedApi };
