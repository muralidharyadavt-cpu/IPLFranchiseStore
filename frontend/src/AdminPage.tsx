import { useEffect, useRef, useState } from 'react';
import { useLocation } from 'react-router-dom';
import { adminApi, ApiError, catalogueApi } from './api';
import type { InventoryAudit, Product, ProductPage } from './api';
import { isAdminUiEnabled } from './adminEnvironment';
import { useCustomer } from './customerSession';
import { formatDate, formatPrice } from './formatting';

function errorMessage(reason: unknown): string {
  if (reason instanceof ApiError) return `HTTP ${reason.status || 'network'}: ${reason.message}`;
  return reason instanceof Error ? reason.message : 'Request failed.';
}
function auditValue(operation: string, value: number): string | number {
  if (operation === 'priceUpdate') return formatPrice(value);
  if (operation === 'stockAdjustment' || operation === 'checkout') return value;
  return `${value} (units unspecified)`;
}
function ProductEditor({ product, onUpdate, onBusy }: { product: Product; onUpdate: (product: Product) => void; onBusy: (busy: boolean) => void }) {
  const { user } = useCustomer();
  const [actor, setActor] = useState(user);
  const [quantity, setQuantity] = useState('');
  const [price, setPrice] = useState(product.price.toFixed(2));
  const [stockReason, setStockReason] = useState('');
  const [priceReason, setPriceReason] = useState('');
  const [audits, setAudits] = useState<InventoryAudit[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [blocked, setBlocked] = useState(false);
  const [unavailable, setUnavailable] = useState(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [attempt, setAttempt] = useState(0);
  const lock = useRef(false);
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setError(''); setAudits(null);
    adminApi.audits(product.id, controller.signal).then(result => { if (!controller.signal.aborted) { setAudits(result); setUnavailable(false); } })
      .catch(async reason => {
        if (controller.signal.aborted) return;
        if (reason instanceof ApiError && [401, 403].includes(reason.status)) setUnavailable(true);
        else if (reason instanceof ApiError && reason.status === 404) {
          // A public product exists but its admin route does not: no mutation probe is needed.
          try { await catalogueApi.details(product.id, controller.signal); if (!controller.signal.aborted) setUnavailable(true); }
          catch (missing) { if (!controller.signal.aborted) setError(errorMessage(missing)); }
        } else setError(errorMessage(reason));
      }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [product.id, attempt]);

  const reload = async () => {
    if (lock.current) return;
    lock.current = true; setBusy(true); onBusy(true); setError(''); setMessage('');
    let latest: Product | undefined;
    try {
      latest = await catalogueApi.details(product.id);
      const history = await adminApi.audits(product.id);
      if (!mounted.current) return;
      onUpdate(latest); setAudits(history); setBlocked(false); setUnavailable(false);
      setMessage('Latest product and audit history loaded. Review the values and preserved form inputs before explicitly submitting.');
    } catch (reason) {
      if (mounted.current) {
        setBlocked(true);
        if (reason instanceof ApiError && ([401, 403].includes(reason.status) || (latest && reason.status === 404))) setUnavailable(true);
        setError(errorMessage(reason));
      }
    }
    finally { lock.current = false; if (mounted.current) { setBusy(false); onBusy(false); } }
  };
  const submit = async (operation: 'stock' | 'price') => {
    if (lock.current || loading || blocked || unavailable || audits === null) return;
    setError(''); setMessage('');
    const reason = (operation === 'stock' ? stockReason : priceReason).trim();
    if (!actor.trim() || actor.trim().length > 200) { setError('Actor is required (maximum 200 characters).'); return; }
    if (!reason || reason.length > 500) { setError('Audit reason is required (maximum 500 characters).'); return; }
    const adjustment = Number(quantity);
    if (operation === 'stock' && (!/^[+-]?\d+$/.test(quantity) || !Number.isSafeInteger(adjustment) || adjustment === 0 || adjustment < -2147483648 || adjustment > 2147483647)) { setError('Stock adjustment must be a non-zero 32-bit integer, positive or negative.'); return; }
    // Validate decimal text, not a rounded floating-point calculation. The API does decimal arithmetic.
    if (operation === 'price' && (!/^\d{1,9}(\.\d{1,2})?$/.test(price) || Number(price) <= 0 || Number(price) > 999999999.99)) { setError('Price must be positive INR, at most 999999999.99, with at most two decimal places.'); return; }
    lock.current = true; setBusy(true); onBusy(true);
    const command = { expectedETag: product._etag, actor: actor.trim(), reason };
    let updated: Product;
    try {
      updated = operation === 'stock' ? await adminApi.stock(product.id, { ...command, quantity: adjustment }) : await adminApi.price(product.id, { ...command, price: Number(price) });
    } catch (failure) {
      if (mounted.current) {
        const status = failure instanceof ApiError ? failure.status : 0;
        if (status === 412) { setBlocked(true); setError('Stale data conflict (HTTP 412). No overwrite was applied. Reload the latest product, review it, then explicitly submit again.'); }
        else if (status === 0 || status >= 500 || status === 408 || status === 429) { setBlocked(true); setError(`${errorMessage(failure)} The outcome may be unknown. Reload and inspect the product and audit history before any new adjustment. No write will be retried automatically.`); }
        else { if ([401, 403].includes(status)) setUnavailable(true); if (status === 404) setBlocked(true); setError(errorMessage(failure)); }
      }
      lock.current = false; if (mounted.current) { setBusy(false); onBusy(false); } return;
    }
    if (!mounted.current) { lock.current = false; return; }
    // Older persisted products may return null classification on admin writes. Preserve read enrichment.
    onUpdate({ ...updated, merchandiseType: updated.merchandiseType ?? product.merchandiseType });
    if (operation === 'stock') { setQuantity(''); setStockReason(''); } else { setPrice(updated.price.toFixed(2)); setPriceReason(''); }
    setMessage(`${operation === 'stock' ? 'Stock' : 'Price'} updated successfully. New ETag: ${updated._etag}`);
    try { const history = await adminApi.audits(product.id); if (mounted.current) setAudits(history); }
    catch (failure) { if (mounted.current) {
      if (failure instanceof ApiError && [401, 403, 404].includes(failure.status)) setUnavailable(true);
      setError(`Update succeeded, but audit history could not be loaded. ${errorMessage(failure)}`);
    } }
    finally { lock.current = false; if (mounted.current) { setBusy(false); onBusy(false); } }
  };
  const disabled = busy || loading || blocked || unavailable || audits === null || !product._etag;
  return <section aria-label={`Edit ${product.name}`} className="admin-editor">
    <h2>Edit {product.name}</h2><p>Current price: {formatPrice(product.price)} · Stock: {product.stock}</p><p className="version">Expected ETag: {product._etag}</p>
    {unavailable && <p role="alert">Admin unavailable. The backend must run in Development on a trusted local machine. Production and Staging do not expose these routes. Editing is disabled; no further mutations will be sent.</p>}
    {error && <p role="alert" aria-live="assertive">{error}</p>}
    {(loading || busy || message) && <p role="status" aria-live="polite">{loading ? 'Loading audit history…' : busy ? 'Saving or reloading…' : message}</p>}
    <button disabled={busy || loading} onClick={() => void reload()}>Reload latest product and audits</button>
    {audits === null && !loading && !unavailable && <button disabled={busy} onClick={() => setAttempt(n => n + 1)}>Retry audit history</button>}
    {!unavailable && <><label htmlFor="admin-actor">Actor (caller-supplied demo identity)<input id="admin-actor" value={actor} maxLength={200} disabled={busy} onChange={e => setActor(e.target.value)} /></label>
      <div className="admin-forms"><form noValidate id="stock-adjustments" aria-label="Stock adjustment" onSubmit={e => { e.preventDefault(); void submit('stock'); }}><h3>Adjust stock</h3>
        <label htmlFor="stock-adjustment">Stock adjustment (+ or −)<input id="stock-adjustment" type="number" step="1" value={quantity} disabled={busy} onChange={e => setQuantity(e.target.value)} /></label>
        <label htmlFor="stock-reason">Stock adjustment reason<input id="stock-reason" value={stockReason} maxLength={500} required disabled={busy} onChange={e => setStockReason(e.target.value)} /></label><button disabled={disabled}>Submit stock adjustment</button></form>
      <form noValidate id="price-updates" aria-label="Price update" onSubmit={e => { e.preventDefault(); void submit('price'); }}><h3>Update price</h3>
        <label htmlFor="new-price">New price (INR)<input id="new-price" type="text" inputMode="decimal" value={price} disabled={busy} onChange={e => setPrice(e.target.value)} /></label>
        <label htmlFor="price-reason">Price update reason<input id="price-reason" value={priceReason} maxLength={500} required disabled={busy} onChange={e => setPriceReason(e.target.value)} /></label><button disabled={disabled}>Submit price update</button></form></div></>}
    <h3 id="audit-history">Audit history</h3>{audits && (audits.length === 0 ? <p>No audit entries.</p> : <div className="table-scroll"><table><caption>Audit history for {product.name} (IST)</caption><thead><tr>{['Actor', 'Operation', 'Old value', 'New value', 'Reason', 'Time (IST)', 'Correlation ID'].map(label => <th key={label} scope="col">{label}</th>)}</tr></thead><tbody>{audits.map(audit => <tr key={audit.id}><td>{audit.actor}</td><td>{audit.operationType}</td><td>{auditValue(audit.operationType, audit.oldValue)}</td><td>{auditValue(audit.operationType, audit.newValue)}</td><td>{audit.reason}</td><td><time dateTime={audit.timestamp} title={audit.timestamp}>{formatDate(audit.timestamp)}</time></td><td>{audit.correlationId || 'Unavailable'}</td></tr>)}</tbody></table></div>)}
  </section>;
}
export default function AdminPage() {
  const { hash } = useLocation();
  const enabled = isAdminUiEnabled();
  const [page, setPage] = useState<ProductPage | null>(null);
  const [tokens, setTokens] = useState<(string | undefined)[]>([undefined]);
  const [selected, setSelected] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  const [editingBusy, setEditingBusy] = useState(false);
  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController(); setLoading(true); setError(''); setSelected(''); setPage(null);
    catalogueApi.list({ search: '', merchandiseType: '', franchise: '', pageSize: 50, continuationToken: tokens[tokens.length - 1] }, controller.signal)
      .then(result => { if (!controller.signal.aborted) { setPage(result); setSelected(result.items[0]?.id ?? ''); } }).catch(reason => { if (!controller.signal.aborted) setError(errorMessage(reason)); }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [enabled, tokens, attempt]);
  const product = page?.items.find(p => p.id === selected);
  useEffect(() => {
    if (hash) document.getElementById(hash.slice(1))?.scrollIntoView?.({ block: 'start' });
  }, [hash, product?.id, loading]);
  return <><h1 id="product-management">Development Demo Admin</h1><p>Unauthenticated local development tools. Actor values are caller supplied. Production requires real authentication and authorization.</p>
    {!enabled ? <p role="alert">Admin unavailable in this frontend build. Use the development server with a Development backend. Production builds do not enable administrative editing.</p> : <>
      {loading && <p role="status">Loading products…</p>}{error && <div role="alert" aria-live="assertive"><p>{error}</p><button onClick={() => setAttempt(n => n + 1)}>Retry products</button></div>}
      {page && <><div className="table-scroll"><table><caption>Persisted products — page {tokens.length}</caption><thead><tr>{['Product', 'Franchise', 'Merchandise type', 'Price (INR)', 'Stock', 'ETag', 'Edit'].map(label => <th key={label} scope="col">{label}</th>)}</tr></thead><tbody>{page.items.map(item => <tr key={item.id}><th scope="row">{item.name}</th><td>{item.franchiseName}</td><td>{item.merchandiseType}</td><td>{formatPrice(item.price)}</td><td>{item.stock}</td><td className="version">{item._etag}</td><td><button disabled={editingBusy} onClick={() => setSelected(item.id)}>Edit {item.name}</button></td></tr>)}</tbody></table></div>
        {page.items.length === 0 && <p>No products on this page.</p>}
        <nav className="pagination" aria-label="Admin product pages"><button disabled={editingBusy || loading || tokens.length === 1} onClick={() => setTokens(previous => previous.slice(0, -1))}>Previous</button><span>Page {tokens.length}</span><button disabled={editingBusy || loading || !page.nextContinuationToken} onClick={() => { if (page.nextContinuationToken) setTokens(previous => [...previous, page.nextContinuationToken!]); }}>Next</button></nav>
        <label htmlFor="admin-product">Product to edit<select id="admin-product" disabled={editingBusy} value={selected} onChange={e => setSelected(e.target.value)}>{page.items.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
        {product && <ProductEditor key={product.id} product={product} onBusy={setEditingBusy} onUpdate={updated => setPage(current => current && ({ ...current, items: current.items.map(item => item.id === updated.id ? updated : item) }))} />}
      </>}
    </>}
  </>;
}
