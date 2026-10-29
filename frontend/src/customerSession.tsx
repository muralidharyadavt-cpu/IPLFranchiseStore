import { createContext, useContext, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import { ApiError, customerApi } from './api';
import type { Cart, CartCommand, CartOperation, CheckoutCommand, Order } from './api';

type Pending = { user: string; kind: 'cart'; operation: CartOperation; command: CartCommand } |
  { user: string; kind: 'checkout'; command: CheckoutCommand };
const storageKey = 'ipl.pending-command.v1';
function readPending(): Pending | null {
  const raw = sessionStorage.getItem(storageKey);
  if (!raw) return null;
  const value = JSON.parse(raw) as Pending;
  if (!value || !['cart', 'checkout'].includes(value.kind) || typeof value.user !== 'string' || !value.command) throw new Error('Invalid saved command.');
  return value;
}
interface CustomerContextValue {
  user: string; changeUser: (user: string) => void; cart: Cart | null; busy: boolean; pending: Pending | null;
  error: string; message: string; storageError: boolean;
  refresh: () => Promise<Cart>; mutate: (operation: CartOperation, values?: Pick<CartCommand, 'productId' | 'quantity' | 'products'>, observed?: Cart) => Promise<void>;
  checkout: (cart: Cart) => Promise<void>; retry: () => Promise<void>;
}
const CustomerContext = createContext<CustomerContextValue | null>(null);
export function useCustomer() {
  const context = useContext(CustomerContext);
  if (!context) throw new Error('CustomerProvider is required.');
  return context;
}
export function CustomerProvider({ children }: { children: ReactNode }) {
  const [initial] = useState(() => {
    try { const pending = readPending(); return { pending, user: pending?.user ?? sessionStorage.getItem('ipl.demo-user') ?? 'demo-alice', failed: false }; }
    catch { return { pending: null, user: 'demo-alice', failed: true }; }
  });
  const [user, setUser] = useState(initial.user);
  const userRef = useRef(user);
  const [cart, setCart] = useState<Cart | null>(null);
  const [pending, setPending] = useState<Pending | null>(initial.pending);
  const pendingRef = useRef(pending);
  const lock = useRef(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [storageError, setStorageError] = useState(initial.failed);
  const navigate = useNavigate();
  const refresh = async () => {
    const currentUser = userRef.current;
    const result = await customerApi.cart(currentUser);
    if (currentUser === userRef.current) setCart(result);
    return result;
  };
  const save = (value: Pending | null) => {
    // Persist intent BEFORE sending. A blocked/unavailable storage write must prevent the mutation.
    if (value) sessionStorage.setItem(storageKey, JSON.stringify(value)); else sessionStorage.removeItem(storageKey);
    pendingRef.current = value; setPending(value);
  };
  const send = async (intent: Pending) => {
    let order: Order | undefined;
    try {
      if (intent.kind === 'cart') setCart(await customerApi.mutate(intent.user, intent.operation, intent.command));
      else order = await customerApi.checkout(intent.user, intent.command);
    } catch (reason) {
      const definite = reason instanceof ApiError && reason.status >= 400 && reason.status < 500 && ![408, 429].includes(reason.status);
      if (definite) save(null); // Rejected intent is finished. Refresh/review before making a NEW command.
      const details = reason instanceof ApiError ? reason.failures.map(f => `${f.productId}: ${f.reason}`).join(' ') : '';
      setError(`${reason instanceof ApiError ? `HTTP ${reason.status || 'network'}: ` : ''}${reason instanceof Error ? reason.message : 'Request failed.'} ${details}${definite ? ' Refresh and review the cart before a new action.' : ' Outcome uncertain. Retry the saved request; do not start another purchase.'}`);
      return;
    }
    try { save(null); }
    catch { setStorageError(true); setError('The successful result could not be cleared from browser storage. Reload and replay the saved request safely.'); return; }
    setMessage(order ? `Order ${order.orderId} confirmed.` : 'Cart updated successfully.');
    if (order) navigate(`/orders/${order.orderId}?confirmed=true`, { state: { order } });
    // A durable replay can return a historical result; explicitly GET the current cart afterward.
    try { await refresh(); } catch { setError('The action succeeded, but the current cart could not be loaded. Refresh the cart before another action.'); setCart(null); }
  };
  const run = async (create: () => Promise<Pending>) => {
    if (lock.current || pendingRef.current || storageError) return;
    lock.current = true; setBusy(true); setError(''); setMessage('');
    try { const intent = await create(); save(intent); await send(intent); }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to prepare request.'); }
    finally { lock.current = false; setBusy(false); }
  };
  const retry = async () => {
    if (lock.current || !pendingRef.current || storageError) return;
    lock.current = true; setBusy(true); setError('');
    try { await send(pendingRef.current); } catch { setError('Cannot update saved intent. Reload before continuing.'); setStorageError(true); }
    finally { lock.current = false; setBusy(false); }
  };
  const mutate: CustomerContextValue['mutate'] = (operation, values = {}, observed) => run(async () => {
    const current = observed ?? await refresh();
    return { user, kind: 'cart', operation, command: { ...values, requestId: crypto.randomUUID(), expectedCartVersion: current.version } };
  });
  const checkout = (current: Cart) => run(async () => ({ user, kind: 'checkout', command: { orderId: crypto.randomUUID(), expectedCartVersion: current.version } }));
  const changeUser = (value: string) => {
    if (lock.current || pendingRef.current || storageError || !/^[A-Za-z0-9_-]{1,64}$/.test(value)) return;
    try { sessionStorage.setItem('ipl.demo-user', value); } catch { setError('Browser storage is required to change identity safely.'); return; }
    userRef.current = value; setUser(value); setCart(null); setError(''); setMessage('');
  };
  return <CustomerContext.Provider value={{ user, changeUser, cart, busy, pending, error, message, storageError, refresh, mutate, checkout, retry }}>{children}</CustomerContext.Provider>;
}
export function CustomerStatus() {
  const customer = useCustomer();
  const [draft, setDraft] = useState(customer.user);
  return <section className="customer-status" aria-label="Demo session">
    <form onSubmit={e => { e.preventDefault(); customer.changeUser(draft); }}><label htmlFor="demo-user">Demo user (not authentication)<input id="demo-user" value={draft} pattern="[A-Za-z0-9_-]{1,64}" maxLength={64} required disabled={customer.busy || !!customer.pending || customer.storageError} onChange={e => setDraft(e.target.value)} /></label><button disabled={customer.busy || !!customer.pending || customer.storageError}>Switch user</button><span>Current user: {customer.user}</span></form>
    {customer.storageError && <p role="alert">Saved request storage is unavailable or invalid. Mutations are blocked to protect retry safety. Restore this tab's session storage before continuing.</p>}
    {customer.error && <p role="alert" aria-live="assertive">{customer.error}</p>}
    {customer.pending && <div className="failure"><p>A saved {customer.pending.kind} request needs resolution. Original ID: {customer.pending.kind === 'checkout' ? customer.pending.command.orderId : customer.pending.command.requestId}. New actions and user switching are blocked.</p><button disabled={customer.busy || customer.storageError} onClick={() => void customer.retry()}>Retry saved request</button></div>}
    {(customer.busy || customer.message) && <p role="status" aria-live="polite">{customer.busy ? 'Saving request…' : customer.message}</p>}
  </section>;
}
