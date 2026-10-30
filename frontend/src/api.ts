export interface Product {
  id: string; storeId: string; type: string; merchandiseType: string | null;
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
export interface CheckoutFailure { productId: string; code: string; reason: string }
export interface ProblemDetails { title?: string; detail?: string; status?: number; failures?: CheckoutFailure[] }
export class ApiError extends Error {
  constructor(public status: number, message: string, public failures: CheckoutFailure[] = []) { super(message); this.name = 'ApiError'; }
}
async function request<T>(url: string, options: RequestInit = {}): Promise<T> {
  let response: Response;
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 15000);
  const abort = () => controller.abort();
  options.signal?.addEventListener('abort', abort, { once: true });
  if (options.signal?.aborted) controller.abort();
  try {
    response = await fetch(url, { ...options, signal: controller.signal, headers: { Accept: 'application/json', ...options.headers } });
    if (!response.ok) {
      const problem: ProblemDetails = await response.json().catch(() => ({}));
      throw new ApiError(response.status, problem.detail || problem.title || `Request failed (${response.status}).`, problem.failures);
    }
    try { return await response.json() as T; }
    catch { throw new ApiError(0, 'The store response could not be read. Retry the original request.'); }
  }
  catch (error) {
    if (options.signal?.aborted) throw error;
    if (error instanceof ApiError) throw error;
    throw new ApiError(0, 'Cannot reach the store. Check your connection and retry.');
  } finally {
    clearTimeout(timer); options.signal?.removeEventListener('abort', abort);
  }
}
const get = <T,>(url: string, signal?: AbortSignal) => request<T>(url, { signal });
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

export interface CartLine {
  productId: string; name: string | null; quantity: number; acceptedPrice: number; acceptedStock: number;
  acceptedProductVersion: string; currentPrice: number | null; currentStock: number | null;
  currentProductVersion: string | null; priceChanged: boolean; stockChanged: boolean; versionChanged: boolean;
  insufficientStock: boolean; unavailable: boolean; requiresAcknowledgement: boolean; currentSubtotal: number | null;
}
export interface Cart {
  userId: string; version: string; lines: CartLine[]; currency: 'INR'; currentTotal: number | null;
  requiresAcknowledgement: boolean; canCheckout: boolean;
}
export interface CartCommand {
  requestId: string; expectedCartVersion: string; productId?: string; quantity?: number;
  products?: { productId: string; expectedETag: string }[];
}
export type CartOperation = 'add' | 'set' | 'remove' | 'acknowledge';
export interface CheckoutCommand { orderId: string; expectedCartVersion: string }
export interface OrderLine {
  productId: string; name: string; quantity: number; unitPrice: number; lineTotal: number; productVersion: string;
}
export interface Order {
  id: string; storeId: string; type: 'order'; orderId: string; userId: string; createdAt: string; currency: 'INR';
  total: number; lines: OrderLine[]; purchasedCartVersion: string; correlationId: string;
}
export interface OrderPage { items: Order[]; nextContinuationToken: string | null; pageSize: number; totalCount: number | null }
export interface HistoryQuery { dateFrom?: string; dateTo?: string; continuationToken?: string; pageSize: number }
const identity = (user: string) => ({ 'X-Demo-User-Id': user });
const cartUrl = (user: string) => `/api/v1/demo-users/${encodeURIComponent(user)}/cart`;
export const customerApi = {
  cart(user: string, signal?: AbortSignal): Promise<Cart> { return request(cartUrl(user), { signal, headers: identity(user) }); },
  mutate(user: string, operation: CartOperation, command: CartCommand): Promise<Cart> {
    const suffix = operation === 'acknowledge' ? '/acknowledgements' : operation === 'add' ? '/lines' : `/lines/${encodeURIComponent(command.productId!)}`;
    return request(cartUrl(user) + suffix, { method: operation === 'set' ? 'PUT' : operation === 'remove' ? 'DELETE' : 'POST', headers: { ...identity(user), 'Content-Type': 'application/json' }, body: JSON.stringify(command) });
  },
  checkout(user: string, command: CheckoutCommand): Promise<Order> {
    return request('/api/v1/orders/checkout', { method: 'POST', headers: { ...identity(user), 'Content-Type': 'application/json' }, body: JSON.stringify(command) });
  },
  order(user: string, id: string, signal?: AbortSignal): Promise<Order> {
    return request(`/api/v1/orders/${encodeURIComponent(id)}`, { signal, headers: identity(user) });
  },
  history(user: string, query: HistoryQuery, signal?: AbortSignal): Promise<OrderPage> {
    const params = new URLSearchParams({ pageSize: String(query.pageSize), includeTotalCount: 'true' });
    if (query.dateFrom) params.set('dateFrom', query.dateFrom);
    if (query.dateTo) params.set('dateTo', query.dateTo);
    if (query.continuationToken) params.set('continuationToken', query.continuationToken);
    return request(`/api/v1/orders?${params}`, { signal, headers: identity(user) });
  },
};

export interface InventoryAudit {
  id: string; storeId: string; type: 'inventoryAudit'; productId: string; actor: string;
  timestamp: string; reason: string; operationType: 'priceUpdate' | 'stockAdjustment' | 'checkout';
  oldValue: number; newValue: number; correlationId: string;
}
export interface InventoryChange { expectedETag: string; actor: string; reason: string }
export interface StockAdjustment extends InventoryChange { quantity: number }
export interface PriceUpdate extends InventoryChange { price: number }
export const adminApi = {
  stock(id: string, command: StockAdjustment): Promise<Product> {
    return request(`/api/v1/admin/products/${encodeURIComponent(id)}/stock-adjustments`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(command) });
  },
  price(id: string, command: PriceUpdate): Promise<Product> {
    return request(`/api/v1/admin/products/${encodeURIComponent(id)}/price-updates`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(command) });
  },
  audits(id: string, signal?: AbortSignal): Promise<InventoryAudit[]> {
    return request(`/api/v1/admin/products/${encodeURIComponent(id)}/audits`, { signal });
  },
};
