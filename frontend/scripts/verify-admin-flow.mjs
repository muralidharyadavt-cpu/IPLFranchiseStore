// Owns a fresh isolated API process/database; cannot attach to an existing API.
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
    const { customerApi, catalogueApi, adminApi } = await import('data:text/javascript;base64,' + Buffer.from(code).toString('base64'));
    const normal = () => originalFetch('http://localhost:5000/api/v1/products?paged=true').then(async r => { assert.equal(r.status, 200); return r.json(); });
    const normalBefore = await normal();
    const buyer = 'gate5-buyer-' + crypto.randomUUID();
    const watcher = 'gate5-cart-' + crypto.randomUUID();
    const before = await catalogueApi.details('csk-cap'); assert.equal(before.price, 499); assert.equal(before.stock, 100);
    let buyerCart = await customerApi.cart(buyer);
    buyerCart = await customerApi.mutate(buyer, 'add', { requestId: crypto.randomUUID(), expectedCartVersion: buyerCart.version, productId: 'csk-cap', quantity: 1 });
    const order = await customerApi.checkout(buyer, { orderId: crypto.randomUUID(), expectedCartVersion: buyerCart.version }); assert.equal(order.total, 499);
    let cart = await customerApi.cart(watcher);
    cart = await customerApi.mutate(watcher, 'add', { requestId: crypto.randomUUID(), expectedCartVersion: cart.version, productId: 'csk-cap', quantity: 2 });
    let current = await catalogueApi.details('csk-cap'); assert.equal(current.stock, 99);
    const actor = 'gate5-demo-admin';
    const up = await adminApi.stock('csk-cap', { quantity: 5, expectedETag: current._etag, actor, reason: 'Gate 5 isolated stock increase' }); assert.equal(up.stock, 104); assert.notEqual(up._etag, current._etag);
    const down = await adminApi.stock('csk-cap', { quantity: -2, expectedETag: up._etag, actor, reason: 'Gate 5 isolated stock decrease' }); assert.equal(down.stock, 102);
    const updated = await adminApi.price('csk-cap', { price: 549.25, expectedETag: down._etag, actor, reason: 'Gate 5 isolated price update' }); assert.equal(updated.price, 549.25); assert.notEqual(updated._etag, down._etag);
    await assert.rejects(adminApi.price('csk-cap', { price: 599, expectedETag: down._etag, actor, reason: 'Gate 5 stale ETag rejection' }), error => error.status === 412);
    await assert.rejects(adminApi.stock('csk-cap', { quantity: -1000, expectedETag: updated._etag, actor, reason: 'Gate 5 underflow rejection' }), error => error.status === 409);
    await assert.rejects(adminApi.price('csk-cap', { price: 1.234, expectedETag: updated._etag, actor, reason: 'Gate 5 fractional paise rejection' }), error => error.status === 400);
    const listing = await catalogueApi.list({ search: '', merchandiseType: 'Cap', franchise: 'Chennai Super Kings', pageSize: 10 }); assert.equal(listing.items.length, 1); assert.equal(listing.items[0].price, 549.25); assert.equal(listing.items[0]._etag, updated._etag);
    cart = await customerApi.cart(watcher); assert.equal(cart.requiresAcknowledgement, true); assert.equal(cart.canCheckout, false); assert.equal(cart.lines[0].priceChanged, true); assert.equal(cart.lines[0].stockChanged, true); assert.equal(cart.lines[0].acceptedPrice, 499); assert.equal(cart.currentTotal, 1098.50);
    const historical = await customerApi.order(buyer, order.orderId); assert.deepEqual(historical, order); assert.equal(historical.lines[0].unitPrice, 499); assert.equal(historical.total, 499);
    const history = await customerApi.history(buyer, { pageSize: 10 }); assert.deepEqual(history.items[0], order);
    const audits = await adminApi.audits('csk-cap'); const changes = audits.filter(a => a.actor === actor); assert.equal(changes.length, 3);
    assert.deepEqual(changes.map(a => [a.operationType, a.oldValue, a.newValue]), [['stockAdjustment', 99, 104], ['stockAdjustment', 104, 102], ['priceUpdate', 499, 549.25]]);
    assert.ok(changes.every(a => a.reason.startsWith('Gate 5 isolated') && a.correlationId && new Date(a.timestamp).toISOString()));
    cart = await customerApi.mutate(watcher, 'acknowledge', { requestId: crypto.randomUUID(), expectedCartVersion: cart.version, products: cart.lines.map(l => ({ productId: l.productId, expectedETag: l.currentProductVersion })) }); assert.equal(cart.canCheckout, true); assert.equal(cart.requiresAcknowledgement, false);
    assert.equal((await catalogueApi.details('csk-cap')).stock, 102);
    assert.deepEqual(await normal(), normalBefore, 'Normal seed prices, inventory, timestamps and ETags must remain unchanged.');
    const report = { databaseName, base, buyer, watcher, orderId: order.orderId, stockChanges: [99, 104, 102], oldPrice: 499, newPrice: updated.price, cartWarningVerified: true, acknowledgedTotal: cart.currentTotal, historicalOrderUnchanged: true, auditCount: changes.length, staleETagStatus: 412, insufficientStockStatus: 409, invalidPriceStatus: 400, normalSeedUnchanged: true, requests };
    await fs.mkdir(new URL('../../TestResults/', import.meta.url), { recursive: true });
    await fs.writeFile(new URL('../../TestResults/gate5-live.json', import.meta.url), JSON.stringify(report, null, 2));
    console.log(JSON.stringify(report, null, 2));
  } finally { globalThis.fetch = originalFetch; }
});
