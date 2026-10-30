import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import App from './App';
import { ApiError, catalogueApi, customerApi } from './api';
import type { Cart, Order, Product } from './api';
import { formatDate, formatPrice } from './formatting';
jest.mock('./api', () => ({ ...jest.requireActual('./api'), catalogueApi: { list: jest.fn(), details: jest.fn() }, customerApi: { cart: jest.fn(), mutate: jest.fn(), checkout: jest.fn(), order: jest.fn(), history: jest.fn() } }));
const api = jest.mocked(customerApi);
const products = jest.mocked(catalogueApi);
const cap: Product = { id: 'csk-cap', name: 'CSK Cap', storeId: 'ipl-store', type: 'product', merchandiseType: 'Cap', franchise: 'CSK', franchiseId: 'CSK', franchiseName: 'Chennai Super Kings', description: 'Official cap', price: 499, currency: 'INR', stock: 100, stockStatus: 'inStock', isLimitedEdition: false, _etag: 'product-v1', createdAt: '2026-10-30T00:00:00Z', updatedAt: '2026-10-30T00:00:00Z' };
const empty: Cart = { userId: 'demo-alice', version: 'empty', lines: [], currency: 'INR', currentTotal: 0, requiresAcknowledgement: false, canCheckout: false };
const cart: Cart = { ...empty, version: 'cart-v1', currentTotal: 998, canCheckout: true, lines: [{ productId: cap.id, name: cap.name, quantity: 2, acceptedPrice: 499, acceptedStock: 100, acceptedProductVersion: 'product-v1', currentPrice: 499, currentStock: 100, currentProductVersion: 'product-v1', priceChanged: false, stockChanged: false, versionChanged: false, insufficientStock: false, unavailable: false, requiresAcknowledgement: false, currentSubtotal: 998 }] };
const order: Order = { id: 'order-1', storeId: 'ipl-store', type: 'order', orderId: '11111111-1111-4111-8111-111111111111', userId: 'demo-alice', createdAt: '2026-10-30T00:00:00Z', currency: 'INR', total: 998, purchasedCartVersion: cart.version, correlationId: 'correlation', lines: [{ productId: cap.id, name: cap.name, quantity: 2, unitPrice: 499, lineTotal: 998, productVersion: 'product-v1' }] };
function open(path = '/cart') { return render(<MemoryRouter initialEntries={[path]}><App /></MemoryRouter>); }
beforeEach(() => {
  sessionStorage.clear(); jest.resetAllMocks();
  api.cart.mockResolvedValue(cart); api.mutate.mockResolvedValue(cart); api.checkout.mockResolvedValue(order); api.order.mockResolvedValue(order);
  api.history.mockResolvedValue({ items: [order], nextContinuationToken: null, pageSize: 10, totalCount: 1 });
  products.details.mockResolvedValue(cap); products.list.mockResolvedValue({ items: [cap], nextContinuationToken: null, pageSize: 10, totalCount: 1 });
});
afterEach(() => { jest.restoreAllMocks(); jest.useRealTimers(); });
test('add from catalogue sends UUID, expected version, identity and selected quantity', async () => {
  api.cart.mockResolvedValue(empty); open('/products'); await screen.findByRole('heading', { name: 'CSK Cap' });
  fireEvent.change(screen.getByLabelText('Quantity for CSK Cap'), { target: { value: '3' } });
  fireEvent.click(screen.getByRole('button', { name: 'Add to cart: CSK Cap' }));
  await waitFor(() => expect(api.mutate).toHaveBeenCalledTimes(1));
  expect(api.mutate).toHaveBeenCalledWith('demo-alice', 'add', { productId: 'csk-cap', quantity: 3, expectedCartVersion: 'empty', requestId: expect.stringMatching(/^[0-9a-f-]{36}$/) });
  await screen.findByText('Cart updated successfully.');
});
test('details add action works', async () => { open('/products/csk-cap'); await screen.findByText('Official cap'); fireEvent.click(screen.getByRole('button', { name: 'Add to cart: CSK Cap' })); await waitFor(() => expect(api.mutate).toHaveBeenCalledWith('demo-alice', 'add', expect.objectContaining({ quantity: 1, productId: 'csk-cap' }))); });
test('display time zone is labelled, saved and used in order details without changing UTC', async () => {
  open('/orders'); await screen.findByText(/30 Oct 2026/);
  expect(screen.getByLabelText('Display time zone')).toHaveValue('Asia/Kolkata');
  fireEvent.change(screen.getByLabelText('Display time zone'), { target: { value: 'UTC' } });
  expect(sessionStorage.getItem('ipl.display-time-zone.v1')).toBe('UTC');
  expect(screen.getByText(/30 Oct 2026/)).toHaveTextContent('UTC');
  fireEvent.click(screen.getByRole('link', { name: `View order ${order.orderId}` }));
  await screen.findByRole('heading', { name: 'Order details' });
  expect(await screen.findByText(/30 Oct 2026/)).toHaveAttribute('datetime', order.createdAt);
  expect(screen.getByText(/30 Oct 2026/)).toHaveTextContent('UTC');
});
test('saved non-IST timezone is restored on history refresh', async () => {
  sessionStorage.setItem('ipl.display-time-zone.v1', 'America/New_York');
  const view = open('/orders'); await screen.findByText(/29 Oct 2026/); view.unmount();
  open('/orders'); await screen.findByText(/29 Oct 2026/);
  expect(screen.getByLabelText('Display time zone')).toHaveValue('America/New_York');
});
test('browser-local option changes only display, not UTC query filters', async () => {
  const resolved = new Intl.DateTimeFormat().resolvedOptions();
  jest.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ ...resolved, timeZone: 'America/New_York' });
  open('/orders'); await screen.findByText(/30 Oct 2026/);
  expect(screen.getByRole('option', { name: 'Browser local (America/New_York)' })).toBeInTheDocument();
  fireEvent.change(screen.getByLabelText('Display time zone'), { target: { value: 'America/New_York' } });
  expect(screen.getByText(/29 Oct 2026/)).toHaveAttribute('datetime', order.createdAt);
  expect(api.history).toHaveBeenCalledTimes(1);
  expect(api.history).toHaveBeenCalledWith('demo-alice', expect.objectContaining({ dateFrom: undefined, dateTo: undefined }), expect.any(AbortSignal));
});
test.each(['0', '-1', '11', '1.5', ''])('reject invalid add quantity %s', async quantity => {
  open('/products'); await screen.findByRole('heading', { name: 'CSK Cap' }); fireEvent.change(screen.getByLabelText('Quantity for CSK Cap'), { target: { value: quantity } }); fireEvent.click(screen.getByRole('button', { name: 'Add to cart: CSK Cap' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('Quantity must be 1–10'); expect(api.mutate).not.toHaveBeenCalled();
});
test('add cannot exceed displayed stock and out-of-stock is disabled', async () => {
  products.details.mockResolvedValue({ ...cap, stock: 1 }); open('/products/csk-cap'); await screen.findByText('Official cap'); fireEvent.change(screen.getByLabelText('Quantity for CSK Cap'), { target: { value: '2' } }); fireEvent.click(screen.getByRole('button', { name: 'Add to cart: CSK Cap' })); expect(await screen.findByRole('alert')).toHaveTextContent('available stock'); expect(api.mutate).not.toHaveBeenCalled();
});
test('cart displays server unit/subtotal/total and stock, sets quantity and removes', async () => {
  open(); await screen.findByText(/Current total: ₹998.00/); expect(screen.getByText(/Available stock: 100/)).toBeInTheDocument();
  fireEvent.change(screen.getByLabelText('Quantity for CSK Cap'), { target: { value: '3' } }); fireEvent.click(screen.getByRole('button', { name: 'Update quantity' }));
  await screen.findByText('Cart updated successfully.'); expect(api.mutate).toHaveBeenNthCalledWith(1, 'demo-alice', 'set', expect.objectContaining({ quantity: 3, expectedCartVersion: 'cart-v1' }));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Remove CSK Cap' })).toBeEnabled()); fireEvent.click(screen.getByRole('button', { name: 'Remove CSK Cap' }));
  await waitFor(() => expect(api.mutate).toHaveBeenCalledTimes(2)); expect(api.mutate.mock.calls[1][1]).toBe('remove'); expect(api.mutate.mock.calls[0][2].requestId).not.toBe(api.mutate.mock.calls[1][2].requestId);
});
test('cart quantity validation prevents invalid update', async () => { open(); await screen.findByText(/Current total/); fireEvent.change(screen.getByLabelText('Quantity for CSK Cap'), { target: { value: '0' } }); fireEvent.click(screen.getByRole('button', { name: 'Update quantity' })); expect(await screen.findByRole('alert')).toHaveTextContent('Quantity'); expect(api.mutate).not.toHaveBeenCalled(); });
test('repeated click cannot create another command and saves intent before sending', async () => {
  api.mutate.mockImplementation(() => { expect(sessionStorage.getItem('ipl.pending-command.v1')).toContain('requestId'); return new Promise(() => {}); });
  open(); await screen.findByText(/Current total/); const button = screen.getByRole('button', { name: 'Update quantity' }); fireEvent.click(button); fireEvent.click(button);
  await waitFor(() => expect(api.mutate).toHaveBeenCalledTimes(1)); expect(button).toBeDisabled(); expect(screen.getByRole('button', { name: 'Switch user' })).toBeDisabled();
});
test.each([0, 503, 408, 429])('ambiguous cart HTTP %s retry reuses exact ID and payload', async status => {
  api.mutate.mockRejectedValueOnce(new ApiError(status, 'Lost response')).mockResolvedValueOnce(cart); open(); await screen.findByText(/Current total/); fireEvent.click(screen.getByRole('button', { name: 'Update quantity' }));
  await screen.findByText(/Outcome uncertain/); const original = api.mutate.mock.calls[0]; fireEvent.click(screen.getByRole('button', { name: 'Retry saved request' })); await screen.findByText('Cart updated successfully.'); expect(api.mutate.mock.calls[1]).toEqual(original); expect(sessionStorage.getItem('ipl.pending-command.v1')).toBeNull();
});
test('saved ambiguous mutation survives component recreation and locks other operations', async () => {
  api.mutate.mockRejectedValueOnce(new ApiError(0, 'Lost response')).mockResolvedValueOnce(cart); const view = open(); await screen.findByText(/Current total/); fireEvent.click(screen.getByRole('button', { name: 'Remove CSK Cap' })); await screen.findByText(/Outcome uncertain/); const original = api.mutate.mock.calls[0]; view.unmount();
  open('/products'); await screen.findByRole('heading', { name: 'CSK Cap' }); expect(screen.getByRole('button', { name: 'Add to cart: CSK Cap' })).toBeDisabled(); fireEvent.click(screen.getByRole('button', { name: 'Retry saved request' })); await screen.findByText('Cart updated successfully.'); expect(api.mutate.mock.calls[1]).toEqual(original);
});
test('price/stock/version warnings require explicit acknowledgement of displayed ETags', async () => {
  api.cart.mockResolvedValue({ ...cart, requiresAcknowledgement: true, canCheckout: false, lines: [{ ...cart.lines[0], priceChanged: true, stockChanged: true, versionChanged: true, requiresAcknowledgement: true, currentPrice: 549, currentStock: 99, currentProductVersion: 'product-v2' }] });
  open('/checkout'); await screen.findByText(/Price changed from/); expect(screen.getByText('Stock changed from 100 to 99.')).toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Place order' })).toBeDisabled(); expect(api.mutate).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('button', { name: 'Accept reviewed changes' })); await waitFor(() => expect(api.mutate).toHaveBeenCalledWith('demo-alice', 'acknowledge', expect.objectContaining({ expectedCartVersion: 'cart-v1', products: [{ productId: 'csk-cap', expectedETag: 'product-v2' }] })));
});
test('insufficient stock blocks acknowledgement and checkout', async () => { api.cart.mockResolvedValue({ ...cart, canCheckout: false, requiresAcknowledgement: true, lines: [{ ...cart.lines[0], insufficientStock: true, currentStock: 1 }] }); open('/checkout'); await screen.findByText(/Insufficient stock/); expect(screen.getByRole('button', { name: 'Accept reviewed changes' })).toBeDisabled(); expect(screen.getByRole('button', { name: 'Place order' })).toBeDisabled(); });
test('checkout confirmation shows historical lines and stable UUID request', async () => {
  open('/checkout'); await screen.findByRole('button', { name: 'Place order' }); fireEvent.click(screen.getByRole('button', { name: 'Place order' }));
  await screen.findByRole('heading', { name: 'Order confirmed' }); expect(api.checkout).toHaveBeenCalledWith('demo-alice', { orderId: expect.stringMatching(/^[0-9a-f-]{36}$/), expectedCartVersion: 'cart-v1' }); expect(screen.getByText('Order total: ₹998.00')).toBeInTheDocument(); expect(screen.getByText(/2 × ₹499.00/)).toBeInTheDocument();
});
test.each([409, 412])('checkout HTTP %s identifies line failure and leaves cart for review', async status => {
  api.checkout.mockRejectedValue(new ApiError(status, 'Cart changed', [{ productId: 'csk-cap', code: 'stock', reason: 'Insufficient stock' }])); open('/checkout'); await screen.findByRole('button', { name: 'Place order' }); fireEvent.click(screen.getByRole('button', { name: 'Place order' }));
  const alert = await screen.findByRole('alert'); expect(alert).toHaveTextContent(`HTTP ${status}`); expect(alert).toHaveTextContent('csk-cap: Insufficient stock'); expect(screen.getByText(/Current total/)).toBeInTheDocument(); expect(sessionStorage.getItem('ipl.pending-command.v1')).toBeNull();
});
test('lost checkout response reload and replay uses original orderId and cart version', async () => {
  api.checkout.mockRejectedValueOnce(new ApiError(0, 'Response lost')).mockResolvedValueOnce(order); const view = open('/checkout'); await screen.findByRole('button', { name: 'Place order' }); fireEvent.click(screen.getByRole('button', { name: 'Place order' })); await screen.findByText(/Outcome uncertain/);
  const original = api.checkout.mock.calls[0]; view.unmount(); api.cart.mockResolvedValue(empty); open('/checkout'); await screen.findByText(/Your cart is empty/);
  fireEvent.click(screen.getByRole('button', { name: 'Retry saved request' })); await screen.findByRole('heading', { name: 'Order confirmed' }); expect(api.checkout.mock.calls[1]).toEqual(original);
});
test('two rapid checkout clicks submit only once', async () => { api.checkout.mockReturnValue(new Promise(() => {})); open('/checkout'); await screen.findByRole('button', { name: 'Place order' }); const button = screen.getByRole('button', { name: 'Place order' }); fireEvent.click(button); fireEvent.click(button); await waitFor(() => expect(api.checkout).toHaveBeenCalledTimes(1)); });
test('empty cart and empty history are clear', async () => {
  api.cart.mockResolvedValue(empty); api.history.mockResolvedValue({ items: [], nextContinuationToken: null, pageSize: 10, totalCount: 0 }); open(); await screen.findByText(/Your cart is empty/); fireEvent.click(screen.getByRole('link', { name: 'Orders' })); await screen.findByText('No orders found.');
});
test('history next/previous tokens, date filters and reset are server-based', async () => {
  api.history.mockResolvedValue({ items: [order], nextContinuationToken: 'cursor', pageSize: 10, totalCount: 2 }); open('/orders'); await screen.findByText('Order total: ₹998.00'); fireEvent.click(screen.getByRole('button', { name: 'Next' })); await waitFor(() => expect(api.history).toHaveBeenCalledWith('demo-alice', expect.objectContaining({ continuationToken: 'cursor' }), expect.any(AbortSignal)));
  await screen.findByText('Order total: ₹998.00'); fireEvent.click(screen.getByRole('button', { name: 'Previous' })); await waitFor(() => expect(api.history.mock.calls.at(-1)![1].continuationToken).toBeUndefined());
  fireEvent.change(screen.getByLabelText('From (UTC, inclusive)'), { target: { value: '2026-10-01T00:00:00' } }); fireEvent.change(screen.getByLabelText('To (UTC, inclusive)'), { target: { value: '2026-10-30T23:59:59' } }); fireEvent.click(screen.getByRole('button', { name: 'Apply date filters' }));
  await waitFor(() => expect(api.history).toHaveBeenLastCalledWith('demo-alice', { dateFrom: '2026-10-01T00:00:00.000Z', dateTo: '2026-10-30T23:59:59.000Z', continuationToken: undefined, pageSize: 10 }, expect.any(AbortSignal)));
});
test('invalid date range is announced without request', async () => { open('/orders'); await screen.findByText('Order total: ₹998.00'); fireEvent.change(screen.getByLabelText('From (UTC, inclusive)'), { target: { value: '2026-11-01T00:00' } }); fireEvent.change(screen.getByLabelText('To (UTC, inclusive)'), { target: { value: '2026-10-01T00:00' } }); fireEvent.click(screen.getByRole('button', { name: 'Apply date filters' })); expect(await screen.findByRole('alert')).toHaveTextContent('UTC range'); expect(api.history).toHaveBeenCalledTimes(1); });
test('IST dates and INR historical prices format consistently', async () => { expect(formatPrice(5495)).toBe('₹5,495.00'); expect(formatDate('2026-10-30T00:00:00Z')).toContain('5:30'); expect(formatDate('2026-10-30T00:00:00Z')).toContain('IST'); open('/orders'); await screen.findByText(/30 Oct 2026/); fireEvent.click(screen.getByRole('link', { name: `View order ${order.orderId}` })); await screen.findByRole('heading', { name: 'Order details' }); expect(api.order).toHaveBeenCalledWith('demo-alice', order.orderId, expect.any(AbortSignal)); });
test('identity switching scopes cart requests and is explicitly demo-only', async () => { open(); await screen.findByText(/Current total/); fireEvent.change(screen.getByLabelText('Demo user (not authentication)'), { target: { value: 'demo-ravi' } }); fireEvent.click(screen.getByRole('button', { name: 'Switch user' })); await waitFor(() => expect(api.cart).toHaveBeenLastCalledWith('demo-ravi')); });
test('cart read failure has accessible retry', async () => { api.cart.mockRejectedValueOnce(new ApiError(0, 'Offline')).mockResolvedValueOnce(cart); open(); const alert = await screen.findByRole('alert'); expect(alert).toHaveAttribute('aria-live', 'assertive'); fireEvent.click(within(alert).getByRole('button', { name: 'Retry cart' })); await screen.findByText(/Current total/); });
test('history failure retry works', async () => { api.history.mockRejectedValueOnce(new ApiError(503, 'Unavailable')).mockResolvedValueOnce({ items: [], nextContinuationToken: null, totalCount: 0, pageSize: 10 }); open('/orders'); await screen.findByRole('alert'); fireEvent.click(screen.getByRole('button', { name: 'Retry history' })); await screen.findByText('No orders found.'); });
test('other-user order 404 is handled', async () => { api.order.mockRejectedValue(new ApiError(404, 'Missing')); open('/orders/' + order.orderId); expect(await screen.findByRole('alert')).toHaveTextContent('not found for this demo user'); });
test('session storage failure prevents mutation', async () => { jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Storage unavailable'); }); open(); await screen.findByText(/Current total/); fireEvent.click(screen.getByRole('button', { name: 'Update quantity' })); await screen.findByRole('alert'); expect(api.mutate).not.toHaveBeenCalled(); });
test('concurrent acknowledgement 412 never silently accepts new versions', async () => {
  api.cart.mockResolvedValue({ ...cart, requiresAcknowledgement: true, canCheckout: false, lines: [{ ...cart.lines[0], requiresAcknowledgement: true, currentProductVersion: 'reviewed-v2', versionChanged: true }] }); api.mutate.mockRejectedValue(new ApiError(412, 'Product changed again'));
  open('/checkout'); await screen.findByRole('button', { name: 'Accept reviewed changes' }); fireEvent.click(screen.getByRole('button', { name: 'Accept reviewed changes' })); await screen.findByText(/HTTP 412/);
  expect(api.mutate).toHaveBeenCalledTimes(1); expect(api.mutate.mock.calls[0][2].products![0].expectedETag).toBe('reviewed-v2'); expect(screen.getByRole('button', { name: 'Place order' })).toBeDisabled();
});
test('out-of-stock product cannot be added', async () => { products.details.mockResolvedValue({ ...cap, stock: 0, stockStatus: 'outOfStock' }); open('/products/csk-cap'); await screen.findByText('Official cap'); expect(screen.getByRole('button', { name: 'Add to cart: CSK Cap' })).toBeDisabled(); expect(screen.getByText('Out of stock')).toBeInTheDocument(); });
test('corrupt recovery storage blocks new mutations', async () => { sessionStorage.setItem('ipl.pending-command.v1', '{invalid'); open('/products'); await screen.findByRole('heading', { name: 'CSK Cap' }); expect(screen.getByRole('button', { name: 'Add to cart: CSK Cap' })).toBeDisabled(); expect(screen.getByRole('alert')).toHaveTextContent('storage'); });
test('changing demo user resets history cursor before requesting their orders', async () => {
  api.history.mockResolvedValue({ items: [order], nextContinuationToken: 'alice-cursor', totalCount: 2, pageSize: 10 }); open('/orders'); await screen.findByText('Order total: ₹998.00'); fireEvent.click(screen.getByRole('button', { name: 'Next' })); await waitFor(() => expect(api.history.mock.calls.at(-1)![1].continuationToken).toBe('alice-cursor'));
  await screen.findByText('Order total: ₹998.00'); fireEvent.change(screen.getByLabelText('Demo user (not authentication)'), { target: { value: 'demo-ravi' } }); fireEvent.click(screen.getByRole('button', { name: 'Switch user' }));
  await waitFor(() => expect(api.history).toHaveBeenLastCalledWith('demo-ravi', expect.objectContaining({ continuationToken: undefined }), expect.any(AbortSignal)));
  expect(api.history.mock.calls.filter(call => call[0] === 'demo-ravi').every(call => call[1].continuationToken === undefined)).toBe(true);
});
