# IPL Franchise Store

Full-stack take-home assignment scaffold with Cosmos DB product storage and create-only seeding.

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

`GET /api/v1/products` reads all product documents in this store partition (no search or public pagination yet). Prices are decimal INR amounts; `type` is the document discriminator `product`. `createdAt` and `updatedAt` are UTC timestamps. `GET /api/v1/health/dependencies` reads container metadata and returns HTTP 200 when available or sanitized HTTP 503 when unavailable. The existing `/api/v1/health` remains a liveness check. Products return sanitized HTTP 503 if storage cannot be read. No credentials or SDK exception details are sent to clients.

One logical partition deliberately co-locates the initial store's products so a future transactional batch can atomically update multiple products within that partition. No checkout or transactions are implemented here. This cannot provide atomic checkout across store partitions. A single hot store cannot scale horizontally across logical partitions; its normal limits are 20 GB and 10,000 RU/s, and actual throughput can be lower. Before growth, revisit partitioning and checkout consistency rather than treating one partition as an unlimited database. See [Cosmos partitioning](https://learn.microsoft.com/en-us/azure/cosmos-db/partitioning-overview).

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

A future checkout service should reuse these invariants: validate positive requested quantities, calculate remaining stock without underflow, condition each product write on the version read, and commit all product writes plus audit records together in one store partition. A conflict must abort the entire checkout for explicit review/retry. Current single-product admin services do not implement checkout; batching multiple independent service calls would not be an atomic checkout. Cosmos transactional batches are limited to 100 operations, 2 MB, and five seconds ([transactional batch documentation](https://learn.microsoft.com/en-us/azure/cosmos-db/transactional-batch)).
