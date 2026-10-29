# IPL Franchise Store

Full-stack take-home assignment with Cosmos DB products, audited inventory, persistent carts and atomic demo checkout.

- `backend/IPLFranchiseStore.Api`: ASP.NET Core Web API targeting .NET 10.
- `frontend`: React, TypeScript, and Vite.
- `tests/IPLFranchiseStore.Api.Tests`: xUnit health endpoint integration test.

## Prerequisites

.NET SDK 10.0.403 (pinned in `global.json`), Node.js 24.15.0, npm 11.12.1.
Frontend dependencies are pinned; commit `frontend/package-lock.json` for reproducible installs.
On Windows PowerShell, use `npm.cmd` if execution policy blocks `npm.ps1`.

## Validate

From the repository root:

```sh
dotnet restore
dotnet build
dotnet test
cd frontend
npm install
npm run build
```

## Run locally

```sh
dotnet run --project backend/IPLFranchiseStore.Api --urls http://localhost:5000
```

`GET http://localhost:5000/api/v1/health` returns HTTP 200 and `{"status":"healthy"}`.

In a second terminal:

```sh
cd frontend
npm run dev
```

Open the URL printed by Vite to see **IPL Franchise Store**.

## Cosmos DB local setup (Gate 1A)

Run the Windows Cosmos DB Emulator at `https://localhost:8081` and trust its installed development certificate. TLS validation remains enabled; fix certificate trust if HTTPS fails rather than bypassing validation.

The API uses the stable Microsoft.Azure.Cosmos SDK with one DI-managed singleton CosmosClient, Gateway HTTPS transport and System.Text.Json camel-case serialization. EF Core is not used.

Configure `Cosmos:Key` using .NET User Secrets for the API project (UserSecretsId `IPLFranchiseStore-local-development`), or set the `Cosmos__Key` environment variable in the API/test terminal. Obtain the local development key from the emulator's Quickstart page. Never put it in appsettings, a script, or a tracked environment file. User Secrets are local development storage, not encrypted production storage.

Optional overrides are `Cosmos__Endpoint`, `Cosmos__DatabaseName`, and `Cosmos__ContainerName`. Defaults are `https://localhost:8081`, `IPLFranchiseStore`, and `StoreData`. The partition key is `/storeId`, and initial products all use `ipl-store`.

Startup creates the database/container if missing and inserts four products with stable IDs. HTTP 409 conflicts are ignored: existing stock, descriptions, prices and timestamps are never overwritten. Missing individual seed records are inserted again. Other initialization failures produce a sanitized warning; liveness remains available. Fix configuration/availability and restart to retry initialization.

Bare `GET /api/v1/products` retains the legacy product array; Gate 3 below adds opt-in search and pagination. Prices are decimal INR amounts; `type` is the document discriminator `product`. `createdAt` and `updatedAt` are UTC timestamps. `GET /api/v1/health/dependencies` reads container metadata and returns HTTP 200 when available or sanitized HTTP 503 when unavailable. The existing `/api/v1/health` remains a liveness check. Products return sanitized HTTP 503 if storage cannot be read. No credentials or SDK exception details are sent to clients.

One logical partition deliberately co-locates the initial store's products so a transactional batch can atomically update multiple products within that partition. Checkout now uses this in Gate 2B below. This cannot provide atomic checkout across store partitions. A single hot store cannot scale horizontally across logical partitions; its normal limits are 20 GB and 10,000 RU/s, and actual throughput can be lower. Before growth, revisit partitioning and checkout consistency rather than treating one partition as an unlimited database. See [Cosmos partitioning](https://learn.microsoft.com/en-us/azure/cosmos-db/partitioning-overview).

Run `dotnet restore`, `dotnet build`, and `dotnet test` after configuring the key. The emulator-backed test is mandatory (fails with an actionable message if the key is missing), uses a unique `IPLFranchiseStoreTests-*` database, verifies `/storeId`, HTTP retrieval and inventory preservation across two API hosts, and deletes only its own test database. It never resets the application's inventory.

Inspect `https://localhost:8081/_explorer/index.html` to see `IPLFranchiseStore` → `StoreData` and the `/storeId` partition key. The API default run URL above remains `http://localhost:5000`.

## Gate 1B: development inventory administration

Products retain their existing fields and now include Cosmos `_etag` (a quoted, opaque concurrency token). Obtain it from `GET /api/v1/products`. Each mutation requires that exact token in `expectedETag`; success returns the committed product, its new `_etag`, an `ETag` HTTP header, and an `X-Correlation-ID` header. Always JSON-encode the token rather than stripping its embedded quotes.

These routes are mapped **only when ASPNETCORE_ENVIRONMENT is Development**:

| Method | Route | Body |
| --- | --- | --- |
| POST | `/api/v1/admin/products/{id}/stock-adjustments` | `quantity` (signed integer delta), `expectedETag`, `actor`, `reason` |
| POST | `/api/v1/admin/products/{id}/price-updates` | `price` (decimal INR), `expectedETag`, `actor`, `reason` |
| GET | `/api/v1/admin/products/{id}/audits` | none |

Example PowerShell (run mutations only against disposable development data):

```powershell
$products = Invoke-RestMethod http://localhost:5000/api/v1/products
$product = $products | Where-Object id -eq 'mi-jersey'
$body = @{ quantity = -1; expectedETag = $product._etag; actor = 'local-developer'; reason = 'Manual stock recount' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/api/v1/admin/products/$($product.id)/stock-adjustments" -ContentType 'application/json' -Body $body
# Fetch the product again and explicitly review before a subsequent mutation.
$product = (Invoke-RestMethod http://localhost:5000/api/v1/products) | Where-Object id -eq 'mi-jersey'
$body = @{ price = 1899.50; expectedETag = $product._etag; actor = 'local-developer'; reason = 'Price correction' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "http://localhost:5000/api/v1/admin/products/$($product.id)/price-updates" -ContentType 'application/json' -Body $body
Invoke-RestMethod "http://localhost:5000/api/v1/admin/products/$($product.id)/audits"
```

The InventoryService owns validation, product reads, business rules and batch persistence. Zero/missing/non-integer adjustments, missing/wildcard ETags, blank actor/reason, and invalid prices return HTTP 400. Reasons are limited to 500 characters and actor to 200. Prices must be positive, at most INR 999,999,999.99, and exactly representable in whole paise; all application money calculations use decimal. Stock is bounded from zero to Int32.MaxValue. Insufficient stock, stock overflow or unchanged price returns HTTP 409. Missing products return 404. A stale ETag returns 412 ProblemDetails; neither pre-read staleness nor a conditional-write conflict is automatically retried with a new ETag. Storage failures return sanitized 503 ProblemDetails.

The service uses one transactional batch containing an ETag-conditional product replacement and one create-only audit document in the same `StoreData` container and `/storeId` partition. The audit includes product ID, actor, UTC timestamp, reason, operation type, decimal old/new values and a server-generated request correlation ID. If either operation fails, neither change commits. Audit IDs are server-generated GUIDs and are independent of correlation IDs. The audit-ID generation method is overridable for tests to force a collision; there is no HTTP failure-injection switch. SDK rate-limit retries retain the original conditional write. Ambiguous transport failures can leave the caller uncertain about commit success: inspect product/audits before resubmitting; correlation IDs are diagnostic, not an idempotency-key contract. No exactly-once network delivery guarantee is claimed.

**Security limitation:** Development admin endpoints are unauthenticated and accept a caller-supplied actor. They are for a trusted local machine only; keep the API bound to localhost. They return 404 in Production and Staging. Before production enablement, require authenticated Microsoft Entra ID/OIDC JWTs, an inventory-admin authorization policy, and derive the actor from verified identity claims. Cosmos access should use managed identity and least-privilege data-plane RBAC. Audit documents are append-only through this API, but an account-key holder can modify data directly; this is not a tamper-proof compliance ledger.

The emulator suite uses unique disposable test databases and never adjusts the application's seed inventory. It checks HTTP status mappings, exact audit values, simultaneous stock/price writers, an interleaving between point read and conditional batch, and rollback when audit creation fails. Host setup/teardown is serialized to avoid competing emulator provisioning; the race tests still issue simultaneous requests explicitly.

The Gate 2B checkout service reuses these invariants: validate positive requested quantities, calculate remaining stock without underflow, condition each product write on the version read, and commit all product writes plus audit records together in one store partition. A conflict aborts the entire checkout for explicit review/retry. Single-product admin services remain separate; batching multiple independent service calls would not be an atomic checkout. Cosmos transactional batches are limited to 100 operations, 2 MB, and five seconds ([transactional batch documentation](https://learn.microsoft.com/en-us/azure/cosmos-db/transactional-batch)).

## Gate 2A: persistent demo carts and durable idempotency

Cart routes are available only in Development, under `/api/v1/demo-users/{userId}/cart`. The path's explicit demo identity selects the cart; it is **not authentication**. User IDs are case-sensitive, 1–64 ASCII letters/digits/underscores/hyphens. Anyone able to access the development API can impersonate a demo user. Keep it local. Production and Staging do not map these routes; production must use verified identity claims and ownership authorization instead of a caller-selected user ID.

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET | (none) | Read cart, accepted snapshots, current prices/stock and warnings |
| POST | `/lines` | Add a quantity, incrementing an existing line |
| PUT | `/lines/{productId}` | Set an existing line's quantity |
| DELETE | `/lines/{productId}` | Remove a line (JSON body required) |
| POST | `/acknowledgements` | Accept reviewed current product versions for every line |

Every command requires a new client-generated non-zero UUID `requestId` and `expectedCartVersion` copied from GET cart. An absent cart is returned as `version: "empty"`, `lines: []`, `currentTotal: 0`, `requiresAcknowledgement: false`, `canCheckout: false`; the first successful command creates its document. The cart remains persisted even after removing its final line. Cart version is an application UUID, separate from the internal Cosmos ETag used for conditional writes. Quantities must be 1–10; zero is never a removal shortcut. The demo has a limit of 20 distinct lines so all snapshot guards fit within one Cosmos batch.

Example requests (replace UUIDs and versions with actual values):

```http
GET /api/v1/demo-users/demo-alice/cart

POST /api/v1/demo-users/demo-alice/cart/lines
Content-Type: application/json

{"requestId":"5cbce871-1140-4f1a-b052-6e2915b9ae17","expectedCartVersion":"empty","productId":"mi-jersey","quantity":1}
```

Successful responses return the cart directly, for example:

```json
{
  "userId": "demo-alice",
  "version": "<cart-version-uuid>",
  "lines": [{
    "productId": "mi-jersey", "name": "MI Jersey", "quantity": 1,
    "acceptedPrice": 1999, "acceptedStock": 50, "acceptedProductVersion": "\"<product-etag>\"",
    "currentPrice": 1999, "currentStock": 50, "currentProductVersion": "\"<product-etag>\"",
    "priceChanged": false, "stockChanged": false, "versionChanged": false,
    "insufficientStock": false, "unavailable": false, "requiresAcknowledgement": false,
    "currentSubtotal": 1999
  }],
  "currency": "INR", "currentTotal": 1999,
  "requiresAcknowledgement": false, "canCheckout": true
}
```

Set/remove examples use the latest returned cart version and a fresh request ID:

```http
PUT /api/v1/demo-users/demo-alice/cart/lines/mi-jersey
Content-Type: application/json

{"requestId":"c0c471eb-ab7c-4cde-86fc-f64a779ce9b9","expectedCartVersion":"<cart-version-uuid>","quantity":2}

DELETE /api/v1/demo-users/demo-alice/cart/lines/mi-jersey
Content-Type: application/json

{"requestId":"39202eeb-5d79-4786-871c-f8899328a641","expectedCartVersion":"<cart-version-uuid>"}
```

On GET, each line is compared with its current product. Any product version, price or stock change requires explicit acknowledgement; stock is flagged even if sufficient units remain. The total uses current prices with decimal INR arithmetic, while accepted prices/stock/version remain visible. Quantity edits preserve existing accepted snapshots and do not implicitly accept changes. Missing products are marked unavailable and total becomes null. Insufficient stock blocks eligibility and cannot be acknowledged until quantity is reduced or the line is removed. `canCheckout` is a point-in-time eligibility indicator only; carts do not reserve inventory. Gate 2B checkout revalidates everything atomically rather than trusting this flag.

To acknowledge, first GET and review the current cart and all product versions, then send the current cart version and the quoted `currentProductVersion` for **every** line:

```http
POST /api/v1/demo-users/demo-alice/cart/acknowledgements
Content-Type: application/json

{"requestId":"637c73a9-c0a5-4bef-a898-c3e7acf41ab1","expectedCartVersion":"<cart-version-uuid>","products":[{"productId":"mi-jersey","expectedETag":"\"<current-product-etag>\""}]}
```

The service refreshes accepted price, stock and product version snapshots only if those reviewed versions and the cart are still current at commit. A concurrent application inventory write causes HTTP 412 and rolls back acknowledgement. HTTP 400 ProblemDetails covers malformed/missing input or invalid quantities; 404 covers missing products/lines; 409 covers stock/line-limit violations or request-ID payload reuse; 412 covers stale cart/product versions or a guard race. After reviewing a 412, submit a new request ID with the refreshed versions. A storage/transport failure returns sanitized 503: retry the **identical** command with the same request ID because its commit outcome may be unknown.

Idempotency is durable Cosmos data, not an in-memory cache. Request IDs are scoped per demo user: a deterministic `cart-request-{user-hash}-{uuid}` ID, user ID, operation, SHA-256 semantic payload fingerprint, result/status and UTC creation time are stored in `StoreData`, in the same `storeId = ipl-store` partition as the cart. Fingerprints include operation, cart version, product ID, quantity and reviewed product versions (sorted by product ID); JSON field order is irrelevant. Matching duplicates replay the stored original status/result **before** current cart validation, even after restart, later cart edits or product changes. This is a historical result, not the latest cart; GET for current state. Different payload/operation with the same user/request ID returns 409. Different users have separate request-ID namespaces. Records have no TTL or automatic pruning in this gate; retention/storage costs must be addressed before growth.

Successful commands create the immutable request result and create/ETag-conditionally replace the cart in one transactional batch. A duplicate result-ID conflict aborts the entire batch. Concurrent duplicate callers resolve to the persisted winning result. Validly identified semantic rejections are also persisted and replayed; their rejection-only batch writes no cart changes. Malformed HTTP bodies, invalid user IDs or invalid/missing UUIDs are rejected before durable processing. No success response is synthesized if the batch/result cannot be confirmed, and no new ETag is silently substituted to reapply a conflicting command.

**Product guard protocol:** An emulator-backed probe demonstrated that batch `ReadItem` ignores `IfMatchEtag`; it is not used as a concurrency guarantee. A separate `productGuard` document per product is lazily created. Snapshot-accepting cart commands and InventoryService read this guard **before** the product, and conditionally replace the guard in the same batch as their own writes. InventoryService now commits product + audit + guard atomically. Cart batches commit cart + request result + guards; product documents, product ETags and inventory are untouched by carts. These short-lived write fences reserve no stock and may cause a conservative 412 during contention even when the product value did not change. All future application product writers must participate in this protocol. Direct account-key edits or other external writers bypassing it are outside the guarantee, just as they bypass application validation/auditing. Do not run an older API instance that writes products without guards alongside this version.

GET cart is read-only and does not provide a locked checkout snapshot; changes may happen immediately after any response. The isolated emulator tests prove single application of concurrent duplicate commands, replay after recreating the API, snapshot warning/acknowledgement state, and rollback of cart/result writes for forced product-guard, cart-ETag and request-record conflicts. They never alter the application's seed inventory.

## Gate 2B: atomic checkout and immutable orders

Development-only endpoints:

| Method | Route | Identity |
| --- | --- | --- |
| POST | `/api/v1/orders/checkout` | `X-Demo-User-Id` header matching the cart's demo user |
| GET | `/api/v1/orders/{orderId}` | Same header; another user's order returns 404 |

The explicit demo identity follows the cart's user naming rules. The header is caller-supplied and is **not authentication**. Both routes are unmapped in Production/Staging until verified identity claims and ownership authorization are implemented. Do not expose the development API beyond a trusted local environment. Payment processing is not implemented.

GET the cart, review/acknowledge any product warnings, and submit its exact `version` with a client-generated non-zero UUID `orderId`. Checkout uses the persisted cart; the client does not send or control order prices, totals or line snapshots.

```http
POST /api/v1/orders/checkout
X-Demo-User-Id: demo-alice
Content-Type: application/json

{"orderId":"c4f8f5ab-fda2-4ac2-b79b-5aec758d7108","expectedCartVersion":"<version-from-GET-cart>"}
```

Successful checkout and matching retries return HTTP **201 Created** with `Location: /api/v1/orders/{orderId}` and the original persisted order, for example:

```json
{
  "id": "order-c4f8f5abfda24ac2b79b5aec758d7108",
  "storeId": "ipl-store", "orderId": "c4f8f5ab-fda2-4ac2-b79b-5aec758d7108",
  "userId": "demo-alice", "createdAt": "2026-10-29T14:00:00+00:00",
  "currency": "INR", "total": 1999,
  "lines": [{"productId":"mi-jersey","name":"MI Jersey","quantity":1,"unitPrice":1999,"lineTotal":1999,"productVersion":"\"<purchased-product-etag>\""}],
  "purchasedCartVersion": "<version-from-GET-cart>", "correlationId": "<server-request-correlation>", "type": "order"
}
```

```http
GET /api/v1/orders/c4f8f5ab-fda2-4ac2-b79b-5aec758d7108
X-Demo-User-Id: demo-alice
```

GET returns HTTP 200 and the stored historical order; it never joins current product prices into an order. Orders are create-only through this API, and no order update/delete routes exist. A database account-key holder can still edit documents directly; immutable here means application behavior, not a tamper-proof ledger. Gate 3 below adds paged order history.

CheckoutService rejects empty carts, invalid/duplicate lines, missing products, quantities outside 1–10, insufficient stock, and any mismatch between accepted and current price/stock/product ETag. Even a sufficient but changed stock count requires explicit acknowledgement. It collects failing product IDs, codes and reasons in a ProblemDetails `failures` extension. All line failures are discovered before business writes: zero stock is deducted, no order/audit is created and the cart remains intact. A rejection may persist an idempotency result and lazily create guard metadata; neither is a purchase.

| Status | Meaning |
| --- | --- |
| 201 | Checkout committed, or original committed result replayed |
| 400 | Invalid UUID/identity/body, missing expected cart version, or invalid stored line/product data |
| 404 | Missing product; missing order or wrong user on GET |
| 409 | Empty cart, insufficient stock, conflicting order ID/user/payload, or a batch create conflict |
| 412 | Cart changed, product acknowledgement required, or concurrent product/guard/cart change |
| 413 | More than 20 distinct lines or the supported document-size budget exceeded |
| 503 | Storage/transport result uncertain; retry the identical order ID, user and original payload |

Example error:

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Checkout rejected; no products were purchased.","status":409,"failures":[{"productId":"kkr-signed-photo","code":"insufficientStock","reason":"Insufficient stock for this quantity."}]}
```

For **N** products, exactly one successful Cosmos TransactionalBatch in `StoreData` / `storeId = ipl-store` performs **3N + 3** operations:

1. ETag-conditionally replace the purchased cart with empty lines, a new version, `lastCompletedOrderId` and UTC `lastCompletedAt`.
2. Create the order and its durable checkout result (two create-only documents).
3. For every line: ETag-conditionally replace the product with deducted stock, create one `checkout` stock audit, and ETag-conditionally replace that product's guard.

All operations commit or none do. Audit actor is the demo user, timestamp matches the order/product update timestamp, reason includes the order ID, and correlation ID is generated by the HTTP request. Monetary validation and total arithmetic use decimal INR with whole paise, never binary floating-point calculations. Emptying/versioning the cart prevents a second order ID from purchasing the same cart; adding new lines later is a separate cart version and can be purchased normally.

**Maximum order size is 20 distinct products, each quantity 1–10 (63 batch operations).** Before constructing/executing the batch, the service measures the serialized UTF-8 bytes of every document, including the order duplicated inside the stored result, product replacements, audits, guards and completed cart. Their combined budget is capped at **1 MiB**, deliberately leaving a further MiB below Cosmos's 2 MB wire limit for operation envelopes and metadata. Oversized requests return 413 before any purchase writes; they are never split into multiple transactions. The 20-line success case and both count/byte rejection cases run against the emulator.

Order IDs are globally unique in this store, unlike user-scoped cart request IDs. `checkout-request-{orderId}` stores the user, operation, SHA-256 fingerprint of canonical order ID + user + original expected cart version, UTC timestamp, result and status. Its creation is in the same transaction as stock, order, audit and cart completion. A matching retry checks this durable record **before** checking the current cart and returns the original order/status, even after process/service restart, a later cart, or an admin price update. Reusing the ID with another expected cart version or another user returns 409 without exposing the original order. Validly identified rejections are durable too. After reviewing a rejection, submit a new order ID; after a 503/lost response, retry the identical original request with the same ID. There is no automatic application retry with refreshed product/cart ETags and no automatic price acceptance. Record retention has no TTL in this gate.

**Guard review and proof:** InventoryService reads a guard before its product, then includes its conditional guard replacement alongside the product and audit in one batch. Cart additions/quantity edits/acknowledgements read guards before products and conditionally replace them alongside cart/request writes. Checkout follows the same ordering and includes both conditional product and guard replacements in its purchase batch. Thus an acknowledgement or admin write between checkout validation and commit invalidates checkout, and checkout invalidates an already-validated acknowledgement. An acknowledgement can also invalidate an admin write even though the product ETag itself has not changed. Guard conflicts cause conservative 412 responses, never silent retries. Batch reads with ignored If-Match headers are not used as a correctness mechanism.

Tests use independently created Cosmos clients for final-unit competition, same-ID duplicates and different-ID same-cart races; a test-only barrier forces both independent services past validation before either commits. Additional independent-client interleavings prove admin↔cart-acknowledgement↔checkout guard participation in both directions. Tests append a deliberately conflicting final operation to a real emulator batch and confirm rollback of every product/order/audit/cart write. Another test commits successfully, then injects an IOException before delivering the success response (HTTP 503); a normal retry returns the persisted original order without another deduction. Test hooks are protected service overrides only; no HTTP failure switches are exposed.

The concurrency guarantee assumes all application writers use the protocol and a single supported store partition. Direct account-key writers, old API versions bypassing guards, external payment effects, cross-partition purchases, multi-region production behavior and tamper-proof storage are outside this verified gate. Seed initialization remains create-only and never overwrites inventory. All automated/live verification mutations use dedicated isolated emulator databases.

## Gate 3: catalogue queries, history and compatible API evolution

Bare `GET /api/v1/products` (or `?paged=false` without catalogue parameters) retains its **array** response, full field set and legacy error shape, capped at 50 items in name ASC / id ASC order. It sets Deprecation: true, X-Page-Size: 50 and a Link rel="alternate" to paged mode. When more results exist, Link rel="next" contains the complete opaque continuation URL (the next response is a paged envelope). Even a short/empty SDK page may have a next link: always follow it. This deliberately bounds old callers; callers that ignored headers must migrate to avoid incomplete catalogues. Use `?paged=true` to obtain a page. Supplying any catalogue filter, `pageSize`, `continuationToken` or `includeTotalCount` also selects the paged envelope automatically. Combining `paged=false` with these new parameters returns 400. Existing clients can continue deserializing the bare array; new clients should always use paging.

| Catalogue parameter | Accepted values / default | Example |
| --- | --- | --- |
| `paged` | Boolean; absent preserves the bare legacy array unless another supported parameter selects paging. `false` cannot accompany filters/paging options. | `paged=true` |
| `search` | Optional case-insensitive partial product name, trimmed length 1–128 | `search=jer` |
| `type` | Optional merchandise type: `Jersey`, `Cap`, `Flag`, `Signed Photograph`, `Other`, case-insensitive. `Signed Photo` is an alias. Deprecated `product` retains the old all-product discriminator filter. | `type=Cap` |
| `merchandiseType` | Explicit alias for the five merchandise types above; `product` is not a merchandise type. If both parameters appear, their restrictions combine with AND. | `merchandiseType=Signed%20Photograph` |
| `franchise` | Optional trimmed 1–64 characters; known franchise IDs/full names below are interchangeable and case-insensitive. Other nonempty values use an exact case-insensitive lookup, returning no matches when absent. | `franchise=Chennai%20Super%20Kings` or `franchise=CSK` |
| `pageSize` | Integer 1–50; default **10** in paged mode | `pageSize=2` |
| `continuationToken` | Absent for first page; otherwise an opaque nonempty token, URL-encode and return unchanged | `continuationToken=<next-token>` |
| `includeTotalCount` | Boolean, default false (`totalCount: null`); true executes an additional COUNT query | `includeTotalCount=true` |

All supplied filters combine with **AND**, using parameterized Cosmos SQL against persisted documents in partition `ipl-store`. **The public `type` filter now implements the assessment's merchandise-type requirement.** `merchandiseType` is its unambiguous alternative. The response/document field `type: "product"` remains the legacy Cosmos discriminator and must not be used as a merchandise label. New clients should display `merchandiseType`. `type=product` is retained only for old clients, not as the recommended merchandise example.

| Franchise ID | Full display name accepted by `franchise` |
| --- | --- |
| `MI` | `Mumbai Indians` |
| `CSK` | `Chennai Super Kings` |
| `RCB` | `Royal Challengers Bengaluru` (also accepts historical `Royal Challengers Bangalore`) |
| `KKR` | `Kolkata Knight Riders` |

Unknown nonempty merchandise labels return HTTP 200 with an empty page, consistently with a franchise/name lookup having no matches. `Other` is an explicit supported category, not a synonym for an unsupported filter value. Empty/oversized values, duplicate parameter keys, unsupported parameter names (including `category`, `q`, `name`, `franchiseId`, `offset`, `limit`, `sort`) and invalid paging values return 400 ProblemDetails. Unsupported parameters were previously silently ignored; this was corrected to prevent accidentally unfiltered responses. Parameter names are case-insensitive; examples use the canonical spellings. Each parameter is single-valued. `type=Jersey&merchandiseType=Cap` is an empty intersection; `type=product&merchandiseType=Cap` returns caps.

New seed documents persist canonical `merchandiseType` metadata. Existing seed JSON is **not rewritten**: the four stable seed IDs map explicitly to Jersey/Cap/Flag/Signed Photograph when the stored field is missing/null. The fallback is included in the Cosmos SQL predicate, not a post-query in-memory filter. Explicit stored metadata takes precedence over the fallback and over names; arbitrary legacy products without metadata classify as `Other`, with no guessed classification from their names. Add appropriate canonical metadata through a future reviewed migration if those older custom products need more specific categories. Existing stock, prices, timestamps and product ETags are untouched by catalogue reads or initialization conflicts. Stored discriminator names are not renamed, and cart/checkout/audit/idempotency behavior is unchanged.

```http
GET /api/v1/products?paged=true&pageSize=2&includeTotalCount=true
GET /api/v1/products?search=jEr&type=Jersey&franchise=mi&pageSize=10&includeTotalCount=true
GET /api/v1/products?type=Cap&franchise=Chennai%20Super%20Kings&includeTotalCount=true
GET /api/v1/products?type=cap&franchise=CSK&includeTotalCount=true
GET /api/v1/products?merchandiseType=Signed%20Photograph&franchise=Kolkata%20Knight%20Riders
GET /api/v1/products?pageSize=2&includeTotalCount=true&continuationToken=<URL-encoded-next-token>
```

Paged responses have this shape:

```json
{"items":[],"nextContinuationToken":null,"pageSize":10,"totalCount":0}
```

`items` contains the existing complete Product representation: ID, ETag, name, type, franchise, description, decimal INR price/currency, stock and timestamps. The concrete backwards-compatible addition is **`stockStatus`**: `outOfStock` for stock <= 0, `lowStock` for 1–5, and `inStock` above 5. It is computed from current stock, requires no old-document migration or client input, and is additive to both legacy and paged product results. Old fields are neither renamed nor removed. A contract test checks the full old field set and deserializes the array with a legacy client DTO that ignores the new field.

The audited correction also adds `merchandiseType`, `franchiseId`, `franchiseName` and `isLimitedEdition` to both legacy and paged product items. For example, CSK Cap retains `type: "product"` and `franchise: "CSK"` while exposing `merchandiseType: "Cap"`, `franchiseId: "CSK"`, `franchiseName: "Chennai Super Kings"`, `isLimitedEdition: false`. KKR Signed Photo exposes `merchandiseType: "Signed Photograph"` and `isLimitedEdition: true`. Prices remain decimal INR amounts **including tax**, with no added tax calculation; no shipping enhancement is implemented here. Old clients ignore these additional fields, and new clients can distinguish merchandise classification from document identity without changing old stored documents.

Catalogue pages use `ORDER BY c.name ASC, c.id ASC` with Cosmos's case-sensitive ordinal string ordering; search matching remains case-insensitive. Unique IDs break duplicate-name ties. `MaxItemCount` and the SDK continuation token determine each page—there is no `OFFSET LIMIT`, in-memory slicing, or preloading of all matches. Cosmos can return fewer items than requested, including an empty page with a continuation token; keep following `nextContinuationToken` until null.

`totalCount` is null unless `includeTotalCount=true`. When enabled, it is an exact `COUNT(1)` query with the same filters and partition, executed **in addition to** the page query on every request. This consumes extra RUs and latency; count cost depends on data/filter cardinality, and partial `CONTAINS` searches can require more index work. Leave it disabled when not needed. Count and page are separate reads, not a transactionally consistent snapshot during concurrent writes.

Order history uses the established explicit development identity:

```http
GET /api/v1/orders?pageSize=10&includeTotalCount=true
X-Demo-User-Id: demo-alice

GET /api/v1/orders?dateFrom=2026-10-01T00%3A00%3A00Z&dateTo=2026-10-31T23%3A59%3A59.9999999Z&pageSize=10
X-Demo-User-Id: demo-alice
```

`GET /api/v1/orders` returns the same paged envelope with original OrderDocument snapshots as items. Only the supplied user's persisted orders are queried; counts also include only that user. The endpoint remains Development-only and does not treat the caller-controlled header as production authentication. It uses `createdAt DESC, id DESC`, so newest orders come first and tied timestamps remain deterministic. Product names, unit prices, line totals, order totals, correlation IDs and original timestamps are read from immutable orders, never joined to the current catalogue.

History accepts `dateFrom`, `dateTo`, `pageSize`, `continuationToken` and `includeTotalCount`. Date bounds are **inclusive**. Dates must be full ISO 8601 UTC timestamps with `Z` or `+00:00` and up to seven fractional-second digits; local timestamps, nonzero offsets, invalid calendar dates and reversed ranges return 400 ProblemDetails. URL-encode `+` if using `+00:00`. Query parameters are normalized to the existing persisted DateTimeOffset UTC representation (`+00:00`, with serializer-trimmed fractional seconds), preserving old orders without rewriting them. The query assumes orders were stored by the established UTC serializer; external writers using inconsistent date-string formats are outside this contract. The future React UI will display local time, defaulting to **Asia/Kolkata (IST)**; no frontend change is part of this gate.

Continuation tokens wrap the **actual Cosmos SDK token** with ASP.NET Core Data Protection authenticated encryption. A fingerprint binds each token to endpoint/query version, Cosmos endpoint/database/container/partition, SDK assembly version, filters, sort, page size and count option; history also binds the demo user. Tokens are opaque query state, not authentication. Tampered, oversized, malformed, cross-query, cross-user and mismatched-option tokens return sanitized **400 ProblemDetails**. Keep the filters/options/user unchanged between pages. Trimming text filters and equivalent UTC timestamp spellings are normalized, but clients should otherwise repeat the same values. Invalid sizes/filters/dates also return 400; unavailable storage/indexes return sanitized 503. The legacy bare products endpoint preserves its prior 503 JSON error for old clients.

The catalogue query-version scope was deliberately advanced for the merchandise correction: older catalogue continuation tokens return 400 and require restarting at the first page. This invalidates only query cursors, not product ETags, cart requests, checkout request IDs, orders or order-history tokens. Equivalent known franchise aliases normalize to the same restriction; changing to a different franchise or merchandise category cannot silently continue an earlier query.

Data Protection uses the local Windows user-profile key ring outside the repository with application name `IPLFranchiseStore`. Tokens survive API recreation with that same key ring, as verified by an emulator test. Losing keys, changing SDK/query/container versions or moving between unshared key rings can invalidate tokens: restart from the first page. A future multi-instance deployment must share and protect its key ring. No continuation signing/encryption keys are committed or hard-coded.

Provisioning adds these required composite indexes:

```text
(/name ASC, /id ASC)
(/createdAt DESC, /id DESC)
```

New containers receive them at creation. Existing containers receive missing composites through an ETag-conditional **metadata-only** indexing-policy update; existing index paths/composites and all document values/ETags are preserved. After an update, provisioning polls index transformation progress within the existing startup timeout. Large-container transformations can outlast that timeout; queries may return 503 until index provisioning is ready. Index creation/rebuilding and larger indexes add RU/storage/write costs. The normal consistent all-path indexing policy remains the baseline; custom policies excluding query fields require separate review.

Fixed-dataset tests enumerate 124 persisted products and both users' order histories across pages without duplicates/omissions, cover all eight filter-presence combinations, duplicate-name/timestamp ordering, counts, UTC subsecond boundaries, token misuse and historical snapshots. They also prove additive/idempotent index upgrades preserve an existing product's price, inventory and ETag. Shared isolated emulator test fixtures provision 4,000 RU/s to keep indexed bulk setup and large checkout verification from becoming default-400-RU throttling tests; application/production-demo throughput and retry semantics are unchanged.

Continuation paging is not a frozen cross-request snapshot. Concurrent inserts/deletes or product-name changes can affect subsequent pages; the no-duplicates/no-omissions verification applies to a fixed dataset. Single-store partition scalability limits, development authentication limitations, and previous checkout/cart/audit invariants remain unchanged.

### Product details and bounded compatibility

GET /api/v1/products/{id} point-reads the persisted product in the store partition and returns the same complete, enriched Product representation as catalogue items. Unknown IDs or non-product documents return 404 ProblemDetails; dependency failures return sanitized 503 ProblemDetails. No document is rewritten. Example: GET /api/v1/products/csk-cap.

The legacy list cap is a deliberate compatibility correction required by the assessment, not a promise that legacy callers will still receive the entire catalogue. Use paged=true for all new clients. Follow nextContinuationToken until null, including after empty pages.
