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
