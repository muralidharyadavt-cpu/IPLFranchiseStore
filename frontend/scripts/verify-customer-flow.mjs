// Owns a fresh isolated API process/database; cannot attach to an existing API.
// Uses the real TypeScript API client and real HTTP/Cosmos, with no mocked responses.
import fs from 'node:fs/promises';
import assert from 'node:assert/strict';
import { transformSync } from '@babel/core';
import isolation from './verificationIsolation.cjs';
const originalFetch = globalThis.fetch;
await isolation.withIsolatedApi(async ({ base, databaseName, fetch: ownedFetch }) => {
  const requests = [];
  globalThis.fetch = async (url, options) => {
    const response = await ownedFetch(url, options);
    requests.push({ method: options?.method ?? 'GET', path: String(url), status: response.status });
    return response;
  };
  try {
    const source = await fs.readFile(new URL('../src/api.ts', import.meta.url), 'utf8');
    const { code } = transformSync(source, { filename: 'api.ts', configFile: false, babelrc: false, presets: ['@babel/preset-typescript'] });
    const { customerApi, catalogueApi } = await import('data:text/javascript;base64,' + Buffer.from(code).toString('base64'));
    const normalBefore = await originalFetch('http://localhost:5000/api/v1/products?paged=true').then(r => { assert.equal(r.status, 200); return r.json(); });
    const user = 'gate4b-' + crypto.randomUUID();
    const miBefore = await catalogueApi.details('mi-jersey');
    const capBefore = await catalogueApi.details('csk-cap');
    assert.equal(miBefore.stock, 50); assert.equal(capBefore.stock, 100);
    let cart = await customerApi.cart(user); assert.equal(cart.lines.length, 0);
    cart = await customerApi.mutate(user, 'add', { requestId: crypto.randomUUID(), expectedCartVersion: cart.version, productId: 'mi-jersey', quantity: 2 });
    cart = await customerApi.mutate(user, 'add', { requestId: crypto.randomUUID(), expectedCartVersion: cart.version, productId: 'csk-cap', quantity: 3 });
    assert.equal(cart.currentTotal, 5495); assert.equal(cart.canCheckout, true);
    const command = { orderId: crypto.randomUUID(), expectedCartVersion: cart.version };
    const order = await customerApi.checkout(user, command);
    assert.equal(order.total, 5495); assert.equal(order.lines.length, 2);
    assert.deepEqual(order.lines.map(l => [l.productId, l.quantity, l.unitPrice, l.lineTotal]).sort(), [['csk-cap', 3, 499, 1497], ['mi-jersey', 2, 1999, 3998]]);
    const stockAfterCommit = [(await catalogueApi.details('mi-jersey')).stock, (await catalogueApi.details('csk-cap')).stock];
    assert.deepEqual(stockAfterCommit, [48, 97]);
    const cleared = await customerApi.cart(user); assert.equal(cleared.lines.length, 0); assert.equal(cleared.currentTotal, 0);
    // Treat the first response as lost: resolve the original logical checkout by exact replay.
    const replay = await customerApi.checkout(user, command); assert.deepEqual(replay, order);
    const stockAfterReplay = [(await catalogueApi.details('mi-jersey')).stock, (await catalogueApi.details('csk-cap')).stock];
    assert.deepEqual(stockAfterReplay, stockAfterCommit);
    const history = await customerApi.history(user, { pageSize: 10 }); assert.equal(history.items.length, 1); assert.equal(history.items[0].orderId, order.orderId);
    assert.deepEqual(await customerApi.order(user, order.orderId), order);
    const other = await customerApi.history(user + '-other', { pageSize: 10 }); assert.equal(other.items.length, 0);
    const normalAfter = await originalFetch('http://localhost:5000/api/v1/products?paged=true').then(r => r.json());
    assert.deepEqual(normalAfter, normalBefore, 'Normal seed documents, prices, stock and ETags must not change.');
    const report = { databaseName, base, user, orderId: order.orderId, total: cart.currentTotal, stockBefore: [50, 100], stockAfterCommit, stockAfterReplay, cartLinesAfter: cleared.lines.length, historyCount: history.items.length, normalSeedUnchanged: true, requests };
    await fs.mkdir(new URL('../../TestResults/', import.meta.url), { recursive: true });
    await fs.writeFile(new URL('../../TestResults/gate4b-live.json', import.meta.url), JSON.stringify(report, null, 2));
    console.log(JSON.stringify(report, null, 2));
  } finally { globalThis.fetch = originalFetch; }
});
