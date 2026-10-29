import { ApiError, catalogueApi } from './api';
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
