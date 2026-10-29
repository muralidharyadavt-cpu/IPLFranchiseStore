import { ApiError, catalogueApi, customerApi } from './api';
const mockFetch = jest.fn();
beforeEach(() => { globalThis.fetch = mockFetch; mockFetch.mockReset(); });
test('list always uses envelope and safely encodes combined filters and opaque token', async () => {
  const result = { items: [], nextContinuationToken: null, pageSize: 10, totalCount: 0 };
  mockFetch.mockResolvedValue({ ok: true, json: async () => result });
  expect(await catalogueApi.list({ search: 'a&b', merchandiseType: 'Cap', franchise: 'Chennai Super Kings', pageSize: 10, continuationToken: 'a+/=' })).toEqual(result);
  const url = new URL(mockFetch.mock.calls[0][0], 'http://localhost');
  expect(Object.fromEntries(url.searchParams)).toEqual({ paged: 'true', pageSize: '10', includeTotalCount: 'true', search: 'a&b', type: 'Cap', franchise: 'Chennai Super Kings', continuationToken: 'a+/=' });
});
test('details encode the ID', async () => { mockFetch.mockResolvedValue({ ok: true, json: async () => ({ id: 'x' }) }); await catalogueApi.details('a/b'); expect(mockFetch.mock.calls[0][0]).toBe('/api/v1/products/a%2Fb'); });
test('ProblemDetails preserve HTTP status and useful message', async () => { mockFetch.mockResolvedValue({ ok: false, status: 404, json: async () => ({ title: 'Product not found.' }) }); await expect(catalogueApi.details('missing')).rejects.toEqual(new ApiError(404, 'Product not found.')); });
test('network errors have a useful retry message', async () => { mockFetch.mockRejectedValue(new TypeError('fetch failed')); await expect(catalogueApi.details('x')).rejects.toMatchObject({ status: 0, message: expect.stringContaining('retry') }); });
test('non-JSON error has a safe fallback', async () => { mockFetch.mockResolvedValue({ ok: false, status: 502, json: async () => { throw new Error(); } }); await expect(catalogueApi.details('x')).rejects.toMatchObject({ status: 502, message: 'Request failed (502).' }); });
test('cart reads consistently use path identity AND demo header', async () => {
  mockFetch.mockResolvedValue({ ok: true, json: async () => ({ lines: [] }) }); await customerApi.cart('demo-alice');
  expect(mockFetch).toHaveBeenCalledWith('/api/v1/demo-users/demo-alice/cart', expect.objectContaining({ headers: expect.objectContaining({ 'X-Demo-User-Id': 'demo-alice' }) }));
});
test.each([['add', 'POST', '/lines'], ['set', 'PUT', '/lines/cap'], ['remove', 'DELETE', '/lines/cap'], ['acknowledge', 'POST', '/acknowledgements']] as const)('typed %s cart mutation matches actual contract', async (operation, method, suffix) => {
  const command = { requestId: 'uuid', expectedCartVersion: 'version', productId: 'cap', quantity: 2 };
  mockFetch.mockResolvedValue({ ok: true, json: async () => ({ lines: [] }) }); await customerApi.mutate('demo-alice', operation, command);
  expect(mockFetch).toHaveBeenCalledWith('/api/v1/demo-users/demo-alice/cart' + suffix, expect.objectContaining({ method, body: JSON.stringify(command), headers: expect.objectContaining({ 'X-Demo-User-Id': 'demo-alice', 'Content-Type': 'application/json' }) }));
});
test('checkout sends original orderId and expectedCartVersion with demo header', async () => {
  const command = { orderId: 'order-uuid', expectedCartVersion: 'v1' }; mockFetch.mockResolvedValue({ ok: true, json: async () => ({ orderId: 'order-uuid' }) }); await customerApi.checkout('demo-alice', command);
  expect(mockFetch).toHaveBeenCalledWith('/api/v1/orders/checkout', expect.objectContaining({ method: 'POST', body: JSON.stringify(command), headers: expect.objectContaining({ 'X-Demo-User-Id': 'demo-alice' }) }));
});
test('history encodes UTC filters, cursor and identity and details are scoped', async () => {
  mockFetch.mockResolvedValue({ ok: true, json: async () => ({}) }); await customerApi.history('demo-ravi', { pageSize: 10, dateFrom: '2026-10-01T00:00:00Z', dateTo: '2026-10-30T23:59:59Z', continuationToken: '+/=' });
  const params = new URL(mockFetch.mock.calls[0][0], 'http://localhost').searchParams;
  expect(params.get('dateFrom')).toBe('2026-10-01T00:00:00Z'); expect(params.get('continuationToken')).toBe('+/='); expect(mockFetch.mock.calls[0][1].headers['X-Demo-User-Id']).toBe('demo-ravi');
  await customerApi.order('demo-ravi', 'uuid'); expect(mockFetch).toHaveBeenLastCalledWith('/api/v1/orders/uuid', expect.objectContaining({ headers: expect.objectContaining({ 'X-Demo-User-Id': 'demo-ravi' }) }));
});
test('checkout errors retain structured failing lines', async () => {
  const failures = [{ productId: 'cap', code: 'stock', reason: 'Out of stock' }]; mockFetch.mockResolvedValue({ ok: false, status: 409, json: async () => ({ title: 'Rejected', failures }) }); await expect(customerApi.checkout('demo-alice', { orderId: 'id', expectedCartVersion: 'v' })).rejects.toMatchObject({ status: 409, failures });
});
test('unreadable success response is ambiguous, not a successful mutation', async () => { mockFetch.mockResolvedValue({ ok: true, json: async () => { throw new Error(); } }); await expect(customerApi.checkout('demo-alice', { orderId: 'id', expectedCartVersion: 'v' })).rejects.toMatchObject({ status: 0 }); });
test('network timeout is bounded and remains ambiguous', async () => {
  jest.useFakeTimers(); mockFetch.mockImplementation((_url, options) => new Promise((_resolve, reject) => options.signal.addEventListener('abort', () => reject(new Error('timeout')))));
  const result = expect(customerApi.checkout('demo-alice', { orderId: 'id', expectedCartVersion: 'v' })).rejects.toMatchObject({ status: 0 });
  await jest.advanceTimersByTimeAsync(15000); await result; jest.useRealTimers();
});
