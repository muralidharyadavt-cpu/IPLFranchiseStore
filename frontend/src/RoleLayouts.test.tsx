import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import App from './App';
import { adminApi, catalogueApi, customerApi } from './api';
import type { Product } from './api';
import { isAdminUiEnabled } from './adminEnvironment';
import { demoRoleStorageKey } from './demoRole';
jest.mock('./adminEnvironment', () => ({ isAdminUiEnabled: jest.fn() }));
jest.mock('./api', () => ({ ...jest.requireActual('./api'), catalogueApi: { list: jest.fn(), details: jest.fn() }, adminApi: { stock: jest.fn(), price: jest.fn(), audits: jest.fn() }, customerApi: { cart: jest.fn(), mutate: jest.fn(), history: jest.fn() } }));
const enabled = jest.mocked(isAdminUiEnabled);
const catalogue = jest.mocked(catalogueApi);
const admin = jest.mocked(adminApi);
const customer = jest.mocked(customerApi);
const product: Product = { id: 'csk-cap', storeId: 'ipl-store', type: 'product', merchandiseType: 'Cap', name: 'CSK Cap', franchise: 'CSK', franchiseId: 'CSK', franchiseName: 'Chennai Super Kings', description: 'Cap', price: 499, currency: 'INR', stock: 100, stockStatus: 'inStock', isLimitedEdition: false, _etag: '"v1"', createdAt: '2026-10-30T00:00:00Z', updatedAt: '2026-10-30T00:00:00Z' };
function Location() { const location = useLocation(); return <output aria-label="Current route">{location.pathname}{location.hash}</output>; }
function open(path = '/') { return render(<MemoryRouter initialEntries={[path]}><App /><Location /></MemoryRouter>); }
function switchRole(role: 'Shopper' | 'Admin') { fireEvent.change(screen.getByLabelText('Development-only demo role'), { target: { value: role } }); }
beforeEach(() => {
  sessionStorage.clear(); jest.resetAllMocks(); enabled.mockReturnValue(true);
  catalogue.list.mockResolvedValue({ items: [product], nextContinuationToken: null, totalCount: 1, pageSize: 10 }); catalogue.details.mockResolvedValue(product); admin.audits.mockResolvedValue([]);
  customer.cart.mockResolvedValue({ userId: 'demo-alice', version: 'empty', lines: [], currency: 'INR', currentTotal: 0, canCheckout: false, requiresAcknowledgement: false });
  customer.history.mockResolvedValue({ items: [], nextContinuationToken: null, totalCount: 0, pageSize: 10 });
});
afterEach(() => jest.restoreAllMocks());
test('Shopper defaults to products and exposes only shopper navigation', async () => {
  open(); await screen.findByRole('heading', { name: 'Products' }); expect(screen.getByLabelText('Current route')).toHaveTextContent('/products');
  const nav = screen.getByRole('navigation', { name: 'Shopper navigation' }); expect(within(nav).getAllByRole('link').map(link => link.textContent)).toEqual(['Products', 'Cart', 'Orders']);
  expect(screen.queryByRole('navigation', { name: 'Admin navigation' })).not.toBeInTheDocument(); expect(screen.queryByRole('link', { name: 'Admin' })).not.toBeInTheDocument();
  await screen.findByRole('heading', { name: 'CSK Cap' });
});
test('saved Admin role redirects root to inventory management', async () => {
  sessionStorage.setItem(demoRoleStorageKey, 'Admin'); open(); await screen.findByRole('heading', { name: 'Development Demo Admin' }); expect(screen.getByLabelText('Current route')).toHaveTextContent('/admin');
  const nav = screen.getByRole('navigation', { name: 'Admin navigation' }); expect(within(nav).getByRole('link', { name: 'Inventory management' })).toHaveAttribute('href', '/admin');
  expect(within(nav).getByRole('link', { name: 'Stock adjustments' })).toHaveAttribute('href', '/admin#stock-adjustments'); expect(within(nav).getByRole('link', { name: 'Price updates' })).toBeInTheDocument(); expect(within(nav).getByRole('link', { name: 'Audit history' })).toBeInTheDocument();
  expect(screen.queryByRole('navigation', { name: 'Shopper navigation' })).not.toBeInTheDocument(); await screen.findByText('No audit entries.');
});
test('switching roles navigates to the correct landing page and changes layouts', async () => {
  open(); await screen.findByRole('heading', { name: 'CSK Cap' }); switchRole('Admin'); await screen.findByRole('heading', { name: 'Development Demo Admin' }); expect(screen.getByRole('navigation', { name: 'Admin navigation' })).toBeInTheDocument(); expect(screen.queryByRole('navigation', { name: 'Shopper navigation' })).not.toBeInTheDocument();
  switchRole('Shopper'); await screen.findByRole('heading', { name: 'Products' }); expect(screen.getByLabelText('Current route')).toHaveTextContent('/products'); expect(screen.queryByRole('navigation', { name: 'Admin navigation' })).not.toBeInTheDocument(); await screen.findByRole('heading', { name: 'CSK Cap' });
});
test('Shopper direct admin access is denied before any admin or product request', () => {
  open('/admin'); expect(screen.getByRole('heading', { name: 'Access Denied' })).toBeInTheDocument(); expect(admin.audits).not.toHaveBeenCalled(); expect(catalogue.list).not.toHaveBeenCalled(); expect(admin.stock).not.toHaveBeenCalled(); expect(admin.price).not.toHaveBeenCalled();
});
test('role survives navigation and remount/refresh without changing demo user identity', async () => {
  sessionStorage.setItem('ipl.demo-user', 'demo-ravi'); const view = open(); await screen.findByRole('heading', { name: 'CSK Cap' }); switchRole('Admin'); await screen.findByRole('heading', { name: 'Development Demo Admin' });
  fireEvent.click(screen.getByRole('link', { name: 'Audit history' })); await waitFor(() => expect(screen.getByLabelText('Current route')).toHaveTextContent('/admin#audit-history')); expect(screen.getByLabelText('Development-only demo role')).toHaveValue('Admin'); expect(sessionStorage.getItem('ipl.demo-user')).toBe('demo-ravi'); view.unmount();
  open('/'); await screen.findByRole('heading', { name: 'Development Demo Admin' }); expect(screen.getByLabelText('Development-only demo role')).toHaveValue('Admin'); await screen.findByText('No audit entries.');
});
test('admin-like demo user ID does not grant an Admin role', () => {
  sessionStorage.setItem('ipl.demo-user', 'Admin'); open('/admin'); expect(screen.getByRole('heading', { name: 'Access Denied' })).toBeInTheDocument(); expect(screen.getByLabelText('Development-only demo role')).toHaveValue('Shopper');
});
test.each(['/', '/admin'])('production ignores saved Admin at %s and has no demo admin controls', async path => {
  enabled.mockReturnValue(false); sessionStorage.setItem(demoRoleStorageKey, 'Admin'); open(path);
  expect(screen.queryByLabelText('Development-only demo role')).not.toBeInTheDocument(); expect(screen.queryByRole('navigation', { name: 'Admin navigation' })).not.toBeInTheDocument(); expect(admin.audits).not.toHaveBeenCalled(); expect(admin.stock).not.toHaveBeenCalled();
  if (path === '/') { await screen.findByRole('heading', { name: 'Products' }); expect(screen.getByLabelText('Current route')).toHaveTextContent('/products'); await screen.findByRole('heading', { name: 'CSK Cap' }); }
  else expect(screen.getByRole('alert')).toHaveTextContent('Production builds do not enable');
});
test('invalid saved role falls back to Shopper', async () => { sessionStorage.setItem(demoRoleStorageKey, 'SuperAdmin'); open(); await screen.findByRole('heading', { name: 'Products' }); expect(screen.getByLabelText('Development-only demo role')).toHaveValue('Shopper'); await screen.findByRole('heading', { name: 'CSK Cap' }); });
test('failed role storage write keeps Shopper and announces failure', async () => {
  jest.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Storage blocked'); }); open('/products'); await screen.findByRole('heading', { name: 'CSK Cap' }); switchRole('Admin'); expect(screen.getByRole('alert')).toHaveTextContent('Cannot save the demo role'); expect(screen.getByLabelText('Current route')).toHaveTextContent('/products'); expect(admin.audits).not.toHaveBeenCalled();
});
test('selector describes simulation and does not claim authentication', async () => { open('/products'); expect(screen.getByText(/NOT real authentication or server authorization/)).toBeInTheDocument(); await screen.findByRole('heading', { name: 'CSK Cap' }); });
test.each(['/products/csk-cap', '/cart', '/checkout', '/orders'])('customer deep link %s stays usable', async path => {
  open(path); expect(screen.getByLabelText('Current route')).toHaveTextContent(path); expect(screen.getByRole('navigation', { name: 'Shopper navigation' })).toBeInTheDocument();
  if (path === '/products/csk-cap') await screen.findByRole('heading', { name: 'CSK Cap' }); else if (path === '/orders') await screen.findByText('No orders found.'); else await screen.findByText(/Your cart is empty/);
});
test('switching layouts preserves a saved uncertain customer request for safe recovery', async () => {
  sessionStorage.setItem('ipl.pending-command.v1', JSON.stringify({ user: 'demo-alice', kind: 'cart', operation: 'remove', command: { requestId: '11111111-1111-4111-8111-111111111111', expectedCartVersion: 'v1', productId: 'csk-cap' } }));
  open('/products'); await screen.findByRole('heading', { name: 'CSK Cap' }); switchRole('Admin'); await screen.findByRole('heading', { name: 'Development Demo Admin' }); expect(screen.getByRole('button', { name: 'Retry saved request' })).toBeInTheDocument(); expect(sessionStorage.getItem('ipl.pending-command.v1')).toContain('11111111-1111-4111-8111-111111111111'); expect(customer.mutate).not.toHaveBeenCalled(); await screen.findByText('No audit entries.');
});
