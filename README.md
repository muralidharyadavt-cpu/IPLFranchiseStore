# IPL Franchise Store

Minimal full-stack take-home assignment scaffold. No database or business features are implemented.

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
