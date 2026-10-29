export interface Product {
  id: string; storeId: string; type: string; merchandiseType: string;
  name: string; franchise: string; franchiseId: string; franchiseName: string;
  description: string; price: number; currency: 'INR'; stock: number;
  stockStatus: 'inStock' | 'lowStock' | 'outOfStock'; isLimitedEdition: boolean;
  createdAt: string; updatedAt: string; _etag: string;
}
export interface ProductPage {
  items: Product[]; nextContinuationToken: string | null; pageSize: number; totalCount: number | null;
}
export interface CatalogueQuery {
  search: string; merchandiseType: string; franchise: string;
  continuationToken?: string; pageSize: number;
}
export interface ProblemDetails { title?: string; detail?: string; status?: number }
export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); this.name = 'ApiError'; }
}
async function get<T>(url: string, signal?: AbortSignal): Promise<T> {
  let response: Response;
  try { response = await fetch(url, { signal, headers: { Accept: 'application/json' } }); }
  catch (error) {
    if (signal?.aborted) throw error;
    throw new ApiError(0, 'Cannot reach the store. Check your connection and retry.');
  }
  if (!response.ok) {
    const problem: ProblemDetails = await response.json().catch(() => ({}));
    throw new ApiError(response.status, problem.detail || problem.title || `Request failed (${response.status}).`);
  }
  return response.json() as Promise<T>;
}
export const catalogueApi = {
  list(query: CatalogueQuery, signal?: AbortSignal): Promise<ProductPage> {
    const params = new URLSearchParams({ paged: 'true', pageSize: String(query.pageSize), includeTotalCount: 'true' });
    if (query.search) params.set('search', query.search);
    if (query.merchandiseType) params.set('type', query.merchandiseType);
    if (query.franchise) params.set('franchise', query.franchise);
    if (query.continuationToken) params.set('continuationToken', query.continuationToken);
    return get(`/api/v1/products?${params}`, signal);
  },
  details(id: string, signal?: AbortSignal): Promise<Product> {
    return get(`/api/v1/products/${encodeURIComponent(id)}`, signal);
  },
};
