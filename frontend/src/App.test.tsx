import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import App, { formatPrice } from './App';
import { ApiError, catalogueApi } from './api';
import type { Product, ProductPage } from './api';
jest.mock('./api', () => {
  const actual = jest.requireActual('./api');
  return { ...actual, catalogueApi: { list: jest.fn(), details: jest.fn() } };
});
const list = jest.mocked(catalogueApi.list);
const details = jest.mocked(catalogueApi.details);
const cap: Product = { id: 'csk-cap', storeId: 'ipl-store', type: 'product', merchandiseType: 'Cap', name: 'CSK Cap', franchise: 'CSK', franchiseId: 'CSK', franchiseName: 'Chennai Super Kings', description: 'Official Chennai cap', price: 499, currency: 'INR', stock: 100, stockStatus: 'inStock', isLimitedEdition: false, createdAt: '2026-10-30T00:00:00Z', updatedAt: '2026-10-30T00:00:00Z', _etag: 'v1' };
const page = (items: Product[] = [cap], token: string | null = null): ProductPage => ({ items, nextContinuationToken: token, pageSize: 10, totalCount: items.length });
function open(path = '/products') { return render(<MemoryRouter initialEntries={[path]}><App /></MemoryRouter>); }
beforeEach(() => { list.mockReset().mockResolvedValue(page()); details.mockReset().mockResolvedValue(cap); });
afterEach(() => jest.useRealTimers());
test('lists typed API products with public metadata, price and details link', async () => {
  open(); expect(await screen.findByRole('heading', { name: 'CSK Cap' })).toBeInTheDocument();
  expect(screen.getByText('Chennai Super Kings · Cap')).toBeInTheDocument();
  expect(screen.getByText('₹499.00')).toBeInTheDocument(); expect(screen.getByText('In stock')).toBeInTheDocument();
  expect(screen.getByRole('link', { name: 'CSK Cap' })).toHaveAttribute('href', '/products/csk-cap');
});
test('category and full franchise name filters combine with AND in one typed request', async () => {
  open(); await screen.findByText('₹499.00');
  fireEvent.change(screen.getByLabelText('Merchandise type'), { target: { value: 'Cap' } });
  fireEvent.change(screen.getByLabelText('Franchise', { exact: true }), { target: { value: 'Chennai Super Kings' } });
  await waitFor(() => expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ merchandiseType: 'Cap', franchise: 'Chennai Super Kings', search: '', continuationToken: undefined }), expect.any(AbortSignal)));
});
test('name search is controlled and debounced; combines with other filters', async () => {
  jest.useFakeTimers(); open(); await act(async () => {});
  fireEvent.change(screen.getByLabelText('Merchandise type'), { target: { value: 'Cap' } });
  await act(async () => { jest.advanceTimersByTime(1); });
  const calls = list.mock.calls.length;
  fireEvent.change(screen.getByLabelText('Product name'), { target: { value: 'ca' } });
  fireEvent.change(screen.getByLabelText('Product name'), { target: { value: 'cap' } });
  await act(async () => { jest.advanceTimersByTime(299); }); expect(list).toHaveBeenCalledTimes(calls);
  await act(async () => { jest.advanceTimersByTime(1); });
  expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ search: 'cap', merchandiseType: 'Cap' }), expect.any(AbortSignal));
  expect(screen.getByLabelText('Product name')).toHaveValue('cap');
});
test('next and previous use opaque continuation tokens', async () => {
  list.mockResolvedValueOnce(page([cap], 'opaque-token')).mockResolvedValueOnce(page([{ ...cap, id: 'flag', name: 'RCB Flag' }])).mockResolvedValueOnce(page([cap], 'opaque-token'));
  open(); await screen.findByText('₹499.00'); expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Next' })); await screen.findByRole('heading', { name: 'RCB Flag' });
  expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ continuationToken: 'opaque-token' }), expect.any(AbortSignal));
  fireEvent.click(screen.getByRole('button', { name: 'Previous' })); await screen.findByRole('heading', { name: 'CSK Cap' });
  expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ continuationToken: undefined }), expect.any(AbortSignal));
});
test.each(['Merchandise type', 'Franchise', 'Product name'])('changing %s resets pagination', async label => {
  list.mockResolvedValue(page([cap], 'token')); open(); await screen.findByText('₹499.00');
  fireEvent.click(screen.getByRole('button', { name: 'Next' }));
  await waitFor(() => expect(list).toHaveBeenCalledTimes(2)); await screen.findByText('₹499.00');
  fireEvent.change(screen.getByLabelText(label, { exact: true }), { target: { value: label === 'Merchandise type' ? 'Cap' : label === 'Franchise' ? 'Mumbai Indians' : 'jer' } });
  await waitFor(() => expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ continuationToken: undefined }), expect.any(AbortSignal)));
  expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
});
test('INR formatting uses Indian grouping and two decimals', () => { expect(formatPrice(123456.5)).toBe('₹1,23,456.50'); });
test('details show persisted description, stock, metadata and limited edition', async () => {
  details.mockResolvedValue({ ...cap, merchandiseType: 'Signed Photograph', isLimitedEdition: true, stock: 2, stockStatus: 'lowStock' });
  open('/products/csk-cap'); await screen.findByRole('heading', { name: 'CSK Cap' });
  expect(details).toHaveBeenCalledWith('csk-cap', expect.any(AbortSignal));
  expect(screen.getByText('Official Chennai cap')).toBeInTheDocument(); expect(screen.getByText('Limited edition')).toBeInTheDocument(); expect(screen.getByText('Low stock')).toBeInTheDocument(); expect(screen.getByText('2')).toBeInTheDocument();
});
test('missing product has an announced 404 state', async () => {
  details.mockRejectedValue(new ApiError(404, 'Product not found.')); open('/products/missing');
  expect(await screen.findByRole('heading', { name: 'Product not found' })).toBeInTheDocument(); expect(screen.getByRole('alert')).toHaveTextContent('unavailable');
});
test('empty catalogue explains how to recover', async () => { list.mockResolvedValue(page([])); open(); expect(await screen.findByText(/No products found/)).toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled(); });
test('empty SDK page with a continuation still allows Next', async () => { list.mockResolvedValue(page([], 'next')); open(); await screen.findByText(/No products on this page/); expect(screen.getByRole('button', { name: 'Next' })).toBeEnabled(); });
test('loading state is visible and disables paging', () => { list.mockReturnValue(new Promise(() => {})); open(); expect(screen.getByRole('status')).toHaveTextContent('Loading products'); expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled(); });
test('request failure is announced and retry recovers', async () => {
  list.mockRejectedValueOnce(new ApiError(0, 'Cannot reach the store.')).mockResolvedValueOnce(page()); open();
  const alert = await screen.findByRole('alert'); expect(alert).toHaveAttribute('aria-live', 'assertive'); expect(alert).toHaveTextContent('Cannot reach');
  fireEvent.click(screen.getByRole('button', { name: 'Retry' })); expect(await screen.findByRole('heading', { name: 'CSK Cap' })).toBeInTheDocument();
});
test('details loading, network failure and retry work', async () => {
  details.mockRejectedValueOnce(new ApiError(503, 'Unavailable')).mockResolvedValueOnce(cap); open('/products/csk-cap');
  expect(screen.getByRole('status')).toHaveTextContent('Loading product'); await screen.findByRole('alert');
  fireEvent.click(screen.getByRole('button', { name: 'Retry' })); expect(await screen.findByText('Official Chennai cap')).toBeInTheDocument();
});
test('labels, semantic navigation and pending routes are available', async () => {
  open(); expect(screen.getByRole('search')).toBeInTheDocument(); expect(screen.getByLabelText('Product name')).toBeInTheDocument(); expect(screen.getByLabelText('Merchandise type')).toBeInTheDocument(); expect(screen.getByLabelText('Franchise', { exact: true })).toBeInTheDocument();
  await screen.findByText('₹499.00'); fireEvent.click(screen.getByRole('link', { name: 'Admin' })); expect(screen.getByText(/pending implementation/)).toBeInTheDocument();
});
test('unknown routes show not found', () => { open('/unknown'); expect(screen.getByRole('heading', { name: 'Page not found' })).toBeInTheDocument(); });
test('aborted stale requests cannot replace a newer filter result', async () => {
  let resolveOld!: (page: ProductPage) => void;
  list.mockReturnValueOnce(new Promise(resolve => { resolveOld = resolve; })).mockResolvedValue(page()); open();
  fireEvent.change(screen.getByLabelText('Merchandise type'), { target: { value: 'Cap' } }); await screen.findByText('₹499.00');
  await act(async () => resolveOld(page([{ ...cap, name: 'Stale result' }])));
  expect(screen.queryByText('Stale result')).not.toBeInTheDocument();
});
