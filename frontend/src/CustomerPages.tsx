import { useEffect, useState } from 'react';
import { Link, useLocation, useParams } from 'react-router-dom';
import { ApiError, customerApi } from './api';
import type { Cart, CartLine, Order, OrderPage, Product } from './api';
import { useCustomer } from './customerSession';
import { browserTimeZone, defaultTimeZone, formatDate, formatPrice, readTimeZone, timeZoneStorageKey } from './formatting';

const money = (value: number | null) => value === null ? 'Unavailable' : formatPrice(value);
export function AddToCart({ product }: { product: Product }) {
  const customer = useCustomer();
  const [quantity, setQuantity] = useState('1');
  const [validation, setValidation] = useState('');
  const blocked = customer.busy || !!customer.pending || customer.storageError;
  return <form className="add-to-cart" onSubmit={e => {
    e.preventDefault(); const value = Number(quantity);
    if (!Number.isInteger(value) || value < 1 || value > 10 || value > product.stock) { setValidation('Quantity must be 1–10 and cannot exceed available stock.'); return; }
    setValidation(''); void customer.mutate('add', { productId: product.id, quantity: value });
  }} noValidate>
    <label htmlFor={`add-${product.id}`}>Quantity for {product.name}<input id={`add-${product.id}`} type="number" min="1" max={Math.min(10, product.stock)} step="1" value={quantity} disabled={blocked || product.stock === 0} onChange={e => setQuantity(e.target.value)} /></label>
    <button aria-label={`Add to cart: ${product.name}`} disabled={blocked || product.stock === 0}>Add to cart</button>
    {validation && <p role="alert">{validation}</p>}
  </form>;
}
function LineEditor({ line, cart }: { line: CartLine; cart: Cart }) {
  const customer = useCustomer();
  const [quantity, setQuantity] = useState(String(line.quantity));
  const [validation, setValidation] = useState('');
  const blocked = customer.busy || !!customer.pending || customer.storageError;
  useEffect(() => setQuantity(String(line.quantity)), [line.quantity]);
  return <article className="cart-line">
    <h2><Link to={`/products/${encodeURIComponent(line.productId)}`}>{line.name ?? line.productId}</Link></h2>
    <p>Unit price: {money(line.currentPrice)} · Line total: {money(line.currentSubtotal)}</p>
    <p>Available stock: {line.currentStock ?? 'Unavailable'} · Quantity: {line.quantity}</p>
    {line.requiresAcknowledgement && <div className="warning"><p>Review product changes before checkout.</p>
      {line.priceChanged && <p>Price changed from {formatPrice(line.acceptedPrice)} to {money(line.currentPrice)}.</p>}
      {line.stockChanged && <p>Stock changed from {line.acceptedStock} to {line.currentStock ?? 'unavailable'}.</p>}
      {line.versionChanged && <p>Product version changed.</p>}</div>}
    {line.insufficientStock && <p role="alert">Insufficient stock. Reduce quantity or remove this line.</p>}
    {line.unavailable && <p role="alert">Product unavailable. Remove it before checkout.</p>}
    <form className="line-actions" noValidate onSubmit={e => {
      e.preventDefault(); const value = Number(quantity);
      if (!Number.isInteger(value) || value < 1 || value > 10 || value > (line.currentStock ?? 0)) { setValidation('Quantity must be 1–10 and cannot exceed available stock.'); return; }
      setValidation(''); void customer.mutate('set', { productId: line.productId, quantity: value }, cart);
    }}><label htmlFor={`quantity-${line.productId}`}>Quantity for {line.name ?? line.productId}<input id={`quantity-${line.productId}`} type="number" min="1" max={Math.min(10, line.currentStock ?? 0)} step="1" value={quantity} disabled={blocked} onChange={e => setQuantity(e.target.value)} /></label><button disabled={blocked || line.unavailable}>Update quantity</button><button type="button" disabled={blocked} onClick={() => void customer.mutate('remove', { productId: line.productId }, cart)}>Remove<span className="sr-only"> {line.name ?? line.productId}</span></button></form>
    {validation && <p role="alert">{validation}</p>}
  </article>;
}
export function CartPage({ checkout = false }: { checkout?: boolean }) {
  const customer = useCustomer();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let active = true; setLoading(true); setError('');
    customer.refresh().catch(reason => { if (active) setError(reason instanceof Error ? reason.message : 'Unable to load cart.'); }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [customer.user, attempt]);
  const cart = customer.cart;
  const blocked = loading || customer.busy || !!customer.pending || customer.storageError;
  return <><h1>{checkout ? 'Checkout' : 'Your cart'}</h1><p>Demo user: {customer.user}. Carts do not reserve stock.</p>
    <button disabled={customer.busy || loading} onClick={() => setAttempt(n => n + 1)}>Refresh cart</button>
    {loading && <p role="status">Loading cart…</p>}
    {error && <div role="alert" aria-live="assertive"><p>{error}</p><button onClick={() => setAttempt(n => n + 1)}>Retry cart</button></div>}
    {!loading && !error && cart && <>
      {cart.lines.length === 0 ? <p>Your cart is empty. <Link to="/products">Browse products</Link>.</p> : <>
        {cart.lines.map(line => <LineEditor key={`${cart.userId}-${line.productId}`} line={line} cart={cart} />)}
        <p className="price">Current total: {money(cart.currentTotal)}</p>
        {cart.requiresAcknowledgement && <section className="warning" aria-label="Review cart changes"><h2>Accept updated prices and stock</h2><p>Review the changes above and the current total. Acceptance is explicit and applies to these displayed product versions only.</p>
          <button disabled={blocked || cart.lines.some(line => line.unavailable || line.insufficientStock || !line.currentProductVersion)} onClick={() => void customer.mutate('acknowledge', { products: cart.lines.map(line => ({ productId: line.productId, expectedETag: line.currentProductVersion! })) }, cart)}>Accept reviewed changes</button></section>}
        {!cart.canCheckout && <p>Checkout is blocked until product changes are accepted and every line is available in the requested quantity.</p>}
        {checkout ? <><p>Place this order for the displayed total. No payment is collected in this demo.</p><button disabled={blocked || !cart.canCheckout} onClick={() => void customer.checkout(cart)}>Place order</button></> : cart.canCheckout && !blocked ? <Link className="button-link" to="/checkout">Proceed to checkout</Link> : <span>Proceed to checkout unavailable</span>}
      </>}
    </>}
  </>;
}
export function OrderSummary({ order, timeZone = readTimeZone() }: { order: Order; timeZone?: string }) {
  return <article className="order-summary"><h2>Order {order.orderId}</h2><p><time dateTime={order.createdAt}>{formatDate(order.createdAt, timeZone)}</time></p>
    <ul>{order.lines.map(line => <li key={line.productId}><Link to={`/products/${encodeURIComponent(line.productId)}`}>{line.name}</Link> — {line.quantity} × {formatPrice(line.unitPrice)} = {formatPrice(line.lineTotal)}</li>)}</ul><p className="price">Order total: {formatPrice(order.total)}</p><p className="muted">Historical prices at purchase, inclusive of tax.</p></article>;
}
export function OrderDetails() {
  const { id = '' } = useParams();
  const customer = useCustomer();
  const location = useLocation();
  const initialOrder = (location.state as { order?: Order } | null)?.order;
  const [order, setOrder] = useState<Order | null>(initialOrder?.userId === customer.user ? initialOrder : null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setError(''); setOrder(null);
    customerApi.order(customer.user, id, controller.signal).then(value => { if (!controller.signal.aborted) setOrder(value); }).catch(reason => { if (!controller.signal.aborted) setError(reason instanceof ApiError && reason.status === 404 ? 'Order not found for this demo user.' : reason instanceof Error ? reason.message : 'Unable to load order.'); }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [customer.user, id, attempt]);
  return <><h1>{new URLSearchParams(location.search).get('confirmed') === 'true' && order ? 'Order confirmed' : 'Order details'}</h1>
    {loading && <p role="status">Loading order…</p>}{error && <div role="alert" aria-live="assertive"><p>{error}</p><button onClick={() => setAttempt(n => n + 1)}>Retry order</button></div>}{order?.userId === customer.user && <OrderSummary order={order} />}<Link to="/orders">Order history</Link> · <Link to="/products">Continue shopping</Link></>;
}
export function OrderHistory() {
  const { user } = useCustomer();
  const [timeZone, setTimeZone] = useState(readTimeZone);
  const [zoneError, setZoneError] = useState('');
  const localZone = browserTimeZone();
  const [from, setFrom] = useState(''); const [to, setTo] = useState('');
  const [filters, setFilters] = useState({ dateFrom: '', dateTo: '' });
  const [tokens, setTokens] = useState<(string | undefined)[]>([undefined]);
  const [page, setPage] = useState<OrderPage | null>(null);
  const [error, setError] = useState(''); const [validation, setValidation] = useState('');
  const [loading, setLoading] = useState(true); const [attempt, setAttempt] = useState(0);
  // Include user in paging state so identity changes cannot reuse another user's token.
  const [pageUser, setPageUser] = useState(user);
  useEffect(() => { if (pageUser !== user) { setPageUser(user); setTokens([undefined]); } }, [user, pageUser]);
  useEffect(() => {
    if (pageUser !== user) return;
    const controller = new AbortController(); setLoading(true); setError(''); setPage(null);
    customerApi.history(user, { dateFrom: filters.dateFrom || undefined, dateTo: filters.dateTo || undefined, pageSize: 10, continuationToken: tokens[tokens.length - 1] }, controller.signal)
      .then(value => { if (!controller.signal.aborted) setPage(value); }).catch(reason => { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Unable to load orders.'); }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [user, pageUser, filters, tokens, attempt]);
  return <><h1>Order history</h1><p>Newest first. Date filters remain UTC; display time zone: {timeZone}.</p>
    <label htmlFor="display-time-zone">Display time zone<select id="display-time-zone" value={timeZone} onChange={e => {
      const next = e.target.value; setTimeZone(next); setZoneError('');
      try { sessionStorage.setItem(timeZoneStorageKey, next); } catch { setZoneError('Time zone applied for this page but could not be saved for navigation or refresh.'); }
    }}>{Array.from(new Set([defaultTimeZone, 'UTC', localZone, timeZone])).map(zone => <option key={zone} value={zone}>{zone === defaultTimeZone ? 'IST (default)' : zone === localZone && zone !== 'UTC' ? `Browser local (${zone})` : zone}</option>)}</select></label>
    {zoneError && <p role="alert" aria-live="polite">{zoneError}</p>}
    <form className="filters" onSubmit={e => { e.preventDefault();
      const dateFrom = from ? new Date(from + 'Z') : null; const dateTo = to ? new Date(to + 'Z') : null;
      if ((dateFrom && Number.isNaN(dateFrom.getTime())) || (dateTo && Number.isNaN(dateTo.getTime())) || (dateFrom && dateTo && dateFrom > dateTo)) { setValidation('Enter a valid UTC range with From before To.'); return; }
      setValidation(''); setFilters({ dateFrom: dateFrom?.toISOString() ?? '', dateTo: dateTo?.toISOString() ?? '' }); setTokens([undefined]);
    }}><label htmlFor="from">From (UTC, inclusive)<input id="from" type="datetime-local" step="1" value={from} onChange={e => setFrom(e.target.value)} /></label><label htmlFor="to">To (UTC, inclusive)<input id="to" type="datetime-local" step="1" value={to} onChange={e => setTo(e.target.value)} /></label><button>Apply date filters</button></form>
    {validation && <p role="alert">{validation}</p>}
    {loading || pageUser !== user ? <p role="status">Loading orders…</p> : error ? <div role="alert" aria-live="assertive"><p>{error}</p><button onClick={() => setAttempt(n => n + 1)}>Retry history</button></div> : page && <>
      <p role="status">{page.totalCount ?? page.items.length} orders · Page {tokens.length}</p>
      {page.items.length === 0 && <p>{page.nextContinuationToken ? 'No orders on this page. Continue to the next page.' : 'No orders found.'}</p>}
      {page.items.map(order => <div key={order.orderId}><OrderSummary order={order} timeZone={timeZone} /><Link to={`/orders/${order.orderId}`}>View order {order.orderId}</Link></div>)}
    </>}
    <nav className="pagination" aria-label="Order pages"><button disabled={loading || pageUser !== user || tokens.length === 1} onClick={() => setTokens(previous => previous.slice(0, -1))}>Previous</button><span>Page {tokens.length}</span><button disabled={loading || pageUser !== user || !page?.nextContinuationToken} onClick={() => { if (page?.nextContinuationToken) setTokens(previous => [...previous, page.nextContinuationToken!]); }}>Next</button></nav>
  </>;
}
