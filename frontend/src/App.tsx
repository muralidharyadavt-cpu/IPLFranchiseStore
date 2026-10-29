import { useEffect, useState } from 'react';
import { Link, Navigate, NavLink, Route, Routes, useParams } from 'react-router-dom';
import { ApiError, catalogueApi } from './api';
import type { Product, ProductPage } from './api';

import { formatPrice } from './formatting';
export { formatPrice } from './formatting';
import { CustomerProvider, CustomerStatus } from './customerSession';
import { AddToCart, CartPage, OrderHistory, OrderDetails } from './CustomerPages';
const stockLabels = { inStock: 'In stock', lowStock: 'Low stock', outOfStock: 'Out of stock' };
function ProductFacts({ product }: { product: Product }) {
  return <><p className="metadata">{product.franchiseName} · {product.merchandiseType}</p>
    <p className="price">{formatPrice(product.price)}</p>
    <p className={`stock ${product.stockStatus}`}>{stockLabels[product.stockStatus]}</p>
    {product.isLimitedEdition && <p className="edition">Limited edition</p>}</>;
}
function Failure({ message, retry }: { message: string; retry: () => void }) {
  return <div className="failure" role="alert" aria-live="assertive"><p>{message}</p><button onClick={retry}>Retry</button></div>;
}
export function Catalogue() {
  const [search, setSearch] = useState('');
  const [type, setType] = useState('');
  const [franchise, setFranchise] = useState('');
  // Query state and cursor stack change together, preventing old tokens being sent with new filters.
  const [query, setQuery] = useState({ search: '', merchandiseType: '', franchise: '', pageSize: 10 });
  const [tokens, setTokens] = useState<(string | undefined)[]>([undefined]);
  const [page, setPage] = useState<ProductPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const timer = setTimeout(() => {
      const next = { search: search.trim(), merchandiseType: type, franchise, pageSize: 10 };
      if (JSON.stringify(next) !== JSON.stringify(query)) {
        setQuery(next); setTokens([undefined]);
      }
    }, search.trim() !== query.search ? 300 : 0);
    return () => clearTimeout(timer);
  }, [search, type, franchise, query]);
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true); setError(''); setPage(null);
    catalogueApi.list({ ...query, continuationToken: tokens[tokens.length - 1] }, controller.signal)
      .then(result => { if (!controller.signal.aborted) setPage(result); })
      .catch(reason => { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Unable to load products.'); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [query, tokens, attempt]);
  const filtersPending = search.trim() !== query.search || type !== query.merchandiseType || franchise !== query.franchise;
  return <>
    <header className="page-heading"><p className="eyebrow">Official merchandise</p><h1>Products</h1><p>Find your franchise. Wear your support.</p></header>
    <form className="filters" role="search" onSubmit={event => event.preventDefault()}>
      <label htmlFor="search">Product name<input id="search" type="search" value={search} maxLength={128} onChange={e => setSearch(e.target.value)} placeholder="Search by name" /></label>
      <label htmlFor="type">Merchandise type<select id="type" value={type} onChange={e => setType(e.target.value)}><option value="">All types</option>{['Jersey', 'Cap', 'Flag', 'Signed Photograph', 'Other'].map(value => <option key={value}>{value}</option>)}</select></label>
      <label htmlFor="franchise">Franchise<select id="franchise" value={franchise} onChange={e => setFranchise(e.target.value)}><option value="">All franchises</option>{['Mumbai Indians', 'Chennai Super Kings', 'Royal Challengers Bengaluru', 'Kolkata Knight Riders'].map(value => <option key={value}>{value}</option>)}</select></label>
    </form>
    <section aria-label="Catalogue results" aria-busy={loading || filtersPending}>
      <p role="status" aria-live="polite">{loading || filtersPending ? 'Loading products…' : page ? `${page.totalCount ?? page.items.length} products found · Page ${tokens.length}` : ''}</p>
      {error && <Failure message={error} retry={() => setAttempt(value => value + 1)} />}
      {!loading && !filtersPending && page && <>
        {page.items.length === 0 && <p>{page.nextContinuationToken ? 'No products on this page. Continue to the next page.' : 'No products found. Try changing your search or filters.'}</p>}
        <ul className="products">{page.items.map(product => <li key={product.id}><article><h2><Link to={`/products/${encodeURIComponent(product.id)}`}>{product.name}</Link></h2><ProductFacts product={product} /><AddToCart product={product} /><Link className="details-link" to={`/products/${encodeURIComponent(product.id)}`}>View details<span className="sr-only"> for {product.name}</span> →</Link></article></li>)}</ul>
      </>}
      <nav className="pagination" aria-label="Product pages">
        <button disabled={loading || filtersPending || tokens.length === 1} onClick={() => setTokens(previous => previous.slice(0, -1))}>Previous</button>
        <span>Page {tokens.length}</span>
        <button disabled={loading || filtersPending || !page?.nextContinuationToken} onClick={() => { if (page?.nextContinuationToken) setTokens(previous => [...previous, page.nextContinuationToken!]); }}>Next</button>
      </nav>
    </section>
  </>;
}
export function ProductDetails() {
  const { id = '' } = useParams();
  const [product, setProduct] = useState<Product | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [notFound, setNotFound] = useState(false);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true); setProduct(null); setError(''); setNotFound(false);
    catalogueApi.details(id, controller.signal).then(result => { if (!controller.signal.aborted) setProduct(result); })
      .catch(reason => { if (!controller.signal.aborted) { setNotFound(reason instanceof ApiError && reason.status === 404); setError(reason instanceof Error ? reason.message : 'Unable to load product.'); } })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [id, attempt]);
  return <><Link to="/products">← Back to products</Link>
    {loading && <p role="status">Loading product…</p>}
    {notFound ? <div role="alert"><h1>Product not found</h1><p>This product is unavailable. Browse the catalogue for other merchandise.</p></div> : error && <Failure message={error} retry={() => setAttempt(value => value + 1)} />}
    {product && <article className="product-detail"><p className="eyebrow">{product.merchandiseType}</p><h1>{product.name}</h1><ProductFacts product={product} /><AddToCart product={product} /><p>{product.description}</p>
      <dl><dt>Available quantity</dt><dd>{product.stock}</dd><dt>Product ID</dt><dd>{product.id}</dd><dt>Franchise ID</dt><dd>{product.franchiseId}</dd></dl><p className="muted">Prices include tax. Stock is subject to change.</p></article>}
  </>;
}
function Pending({ title }: { title: string }) { return <><h1>{title}</h1><p>This page is pending implementation. No actions are available yet.</p><Link to="/products">Browse products</Link></>; }
export default function App() {
  return <CustomerProvider><a className="skip-link" href="#main">Skip to content</a><header className="site-header"><Link className="brand" to="/products">IPL Franchise Store</Link><nav aria-label="Main navigation">{['Products', 'Cart', 'Checkout', 'Orders', 'Admin'].map(name => <NavLink key={name} to={`/${name.toLowerCase()}`}>{name}</NavLink>)}</nav></header>
    <main id="main"><CustomerStatus /><Routes><Route path="/" element={<Navigate to="/products" replace />} /><Route path="/products" element={<Catalogue />} /><Route path="/products/:id" element={<ProductDetails />} /><Route path="/cart" element={<CartPage />} /><Route path="/checkout" element={<CartPage checkout />} /><Route path="/orders" element={<OrderHistory />} /><Route path="/orders/:id" element={<OrderDetails />} />{['Admin'].map(name => <Route key={name} path={`/${name.toLowerCase()}`} element={<Pending title={name} />} />)}<Route path="*" element={<><h1>Page not found</h1><Link to="/products">Browse products</Link></>} /></Routes></main><footer>IPL Franchise Merchandise Store · Prices in INR, inclusive of tax</footer></CustomerProvider>;
}
