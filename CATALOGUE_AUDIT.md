# Catalogue contract audit

Audit date: 29 October 2026 (Asia/Kolkata). No frontend, Docker, checkout enhancement, commit or push is part of this audit.

## Source authority

Read all four pages of the employer assessment, `Senior Software Engineer Assessment 4 - IPL Franchise (1) (1).pdf`, plus `Park_Place_Execution_Blueprint_v2.md` and `Park_Place_Codex_Prompts_v2.md` supplied by the user. The assessment is authoritative. Page 1 defines merchandise type (jersey/cap/flag/signed photograph/other); page 3 explicitly requires Cap + Chennai Super Kings to return CSK Cap. The blueprint recommends a separate document discriminator and merchandise type. Naming/layout choices in that blueprint are recommendations, not grounds to rename the working repository/container/partition or rebuild the app.

## Investigation before code changes

Actual point reads from the normal emulator database IPLFranchiseStore / StoreData confirmed all four documents have `type: "product"`, franchise abbreviations and no `merchandiseType` field. The catalogue query incorrectly compared the public type filter with that document discriminator. Its franchise comparison accepted only the stored abbreviation; full names returned no matches. A request with `category=Cap` was silently ignored and returned the full legacy array.

| Item | Before audit | Correction |
| --- | --- | --- |
| Case-insensitive partial name | Working | Preserved |
| Merchandise filter | FAIL: Cap compared against product | Correct merchandise predicate in Cosmos SQL |
| Full franchise names | FAIL: literal comparison against CSK/MI/etc. | Canonical name/ID aliases |
| AND semantics | Mechanically AND, but wrong type field | Actual merchandise/name/franchise AND tested |
| Cursor paging / ordering | Working for fixed data | Preserved; catalogue cursor scope advanced |
| INR price / stock status | Correct values | Preserved, tax-inclusive assumption documented |
| Unknown query names | Silently ignored | 400 ProblemDetails |
| Domain metadata | No separate merchandise type | Additive merchandiseType, franchiseId/franchiseName, limited-edition indicator |

The previous 129 tests were green but the type oracle used `product` in every product fixture. This did not validate the employer's type requirement. Corrected combination tests now use varied persisted Jersey/Cap values.

## Precise source/contracts corrected

- Product.cs: retain Type as the legacy discriminator; add optional MerchandiseType and additive franchise/limited-edition response metadata.
- CosmosStore.cs: classify newly created seed documents and enrich legacy catalogue responses without overwriting existing documents.
- CatalogueService.cs: public type now filters merchandise; explicit merchandiseType alias; franchise alias expansion; SQL-side legacy metadata fallback; new catalogue cursor scope.
- QueryEndpoints.cs: recognize merchandiseType and reject unknown/repeated catalogue keys instead of silently ignoring them.
- ProductTaxonomy.cs (new): centralized public vocabulary and the four explicit legacy seed ID classifications.
- QueryTestDatabase.cs / QueryIntegrationTests.cs: realistic persisted categories, corrected former oracle and new audit cases.
- README.md: replace the misleading discriminator-as-merchandise contract with precise parameters, examples and compatibility behavior.

No cart, checkout, admin, audit or order-history business logic was changed by this correction.

## Final contract

The complete supported parameter table and franchise aliases are in README, Gate 3. Parameters are `paged`, `search`, `type`, `merchandiseType`, `franchise`, `pageSize`, `continuationToken`, `includeTotalCount`.

- `type=Cap` meets the assessment's type requirement. `merchandiseType=Cap` is the explicit equivalent.
- Valid categories: Jersey, Cap, Flag, Signed Photograph, Other, case-insensitive. Signed Photo is an alias.
- Deprecated `type=product` keeps existing all-product filtering functional; it is not a merchandise category. Response `type` remains product for compatibility.
- Both type parameters are AND-combined; conflicting categories yield an empty intersection. Unsupported nonempty category values also yield HTTP 200 with an empty page. Empty/oversized values, duplicate keys and unsupported parameter names yield 400 ProblemDetails.
- Known franchise IDs/full names are interchangeable: MI/Mumbai Indians; CSK/Chennai Super Kings; RCB/Royal Challengers Bengaluru (also Bangalore); KKR/Kolkata Knight Riders. Unknown nonempty names use exact case-insensitive lookup and return no matches if absent.
- Bare GET products preserves its array, existing fields, ID ordering and error shape. Any supported filter/paging option selects the envelope. Paged defaults are size 10, maximum 50, count disabled/null; includeTotalCount=true adds a separate RU-consuming COUNT query.
- name ASC/id ASC and real SDK continuation paging are unchanged. Catalogue tokens from the former discriminator-only query scope now return 400, not silently different results. Cart/checkout IDs, product ETags and order-history tokens are unaffected.

## Existing data and compatibility proof

New seed documents persist canonical merchandiseType. Existing four seed documents are not patched: explicit stable ID metadata is applied in the SQL predicate when the field is missing/null, then added to the response. Other unclassified legacy products fall into Other rather than guessing from names. Explicit stored classification wins. This is enrichment, not filtering an in-memory seed array or paginating in application memory.

All original public seed fields, including price, stock, createdAt, updatedAt and ETag, matched the pre-audit capture. A second round of actual Cosmos point reads confirmed the merchandiseType field is still absent in all four normal documents. Their unchanged prices/stocks are MI 1999/50, CSK 499/100, RCB 299/200, KKR 4999/2. Null/missing-field compatibility and ETag preservation were also tested on isolated databases. No normal price or stock write was executed.

## Verification record

- Independently executed baseline: dotnet test --no-build: 129 passed, 0 failed, 0 skipped.
- Initial audit-specific run: 78 passed / 1 failed. The failing new assertion incorrectly assumed only one matching jersey in a 124-product fixture; it was corrected to enumerate the expected full filtered dataset.
- First corrected complete run: restore/build passed (0 warnings/errors), 159 tests passed / 0 failed / 0 skipped.
- Final verification after adding a positive Other/legacy classification case: dotnet restore passed; dotnet build passed with 0 warnings/errors; dotnet test: **160 passed, 0 failed, 0 skipped**, duration 1 minute 44 seconds. There are 31 added audit cases, plus corrected former discriminator-only filter expectations. Existing cart/checkout/admin/order-history regressions pass.
- Filtered Cosmos tests enumerate 21 Cap/CSK records across pages of two, with no duplicates or omissions. General paging still enumerates 124 persisted products. Unknown filters, aliases, token mismatch, unchanged old-client array and all cart/checkout/admin/history regressions are covered.
- Live page enumeration returned csk-cap, kkr-signed-photo, mi-jersey, rcb-flag exactly once; final token null. Changing a token's type restriction returned HTTP 400; a real pre-audit cursor returned HTTP 400.

## Remaining attention before Gate 4A

- Future UI/API client must display merchandiseType and franchiseName, not the legacy type discriminator. It must request paged mode and follow tokens; the bare legacy array remains intentionally unbounded and is an explicit compatibility exception to the assessment's generally paged-list rule.
- Product-details UI is required by the assessment. A dedicated GET /products/{id} endpoint is recommended in the supplied blueprint but is absent in the inspected route definitions; the live GET /api/v1/products/mi-jersey returned HTTP 404. Plan its contract separately rather than scraping the legacy full array. No details feature is implemented here.
- Catalogue tokens require stable Data Protection keys. Paging is not a frozen snapshot during concurrent writes; COUNT is a separate read. Demo identities/admin remain non-production authentication.
- The original PDF starts with two signed photos but later narrates a last-one showdown ending at zero. Keep the normal stock at two and use the existing isolated stock-one concurrency fixtures; do not alter inventory to force the contradictory example.
- DESIGN.md/ADRs, assumptions and AI-USAGE.md remain submission deliverables not present in the inspected repository. The single-project layout and current decimal-INR/partition naming differ from blueprint recommendations but are not changed by this narrowly scoped audit.

## Actual live HTTP responses

Base URL: http://localhost:5000. Requests below were executed against the existing normal emulator. JSON bodies are pretty-printed; values are unchanged. These catalogue calls are read-only.

### GET /api/v1/products?search=jer&includeTotalCount=true

HTTP 200

```json
{
  "items": [
    {
      "_etag": "\"00000000-0000-0000-67a8-b395d00501dd\"",
      "id": "mi-jersey",
      "storeId": "ipl-store",
      "type": "product",
      "merchandiseType": "Jersey",
      "name": "MI Jersey",
      "franchise": "MI",
      "franchiseId": "MI",
      "franchiseName": "Mumbai Indians",
      "isLimitedEdition": false,
      "description": "Mumbai Indians jersey",
      "price": 1999,
      "currency": "INR",
      "stock": 50,
      "stockStatus": "inStock",
      "createdAt": "2026-10-29T13:23:35.568701+00:00",
      "updatedAt": "2026-10-29T13:23:35.568701+00:00"
    }
  ],
  "nextContinuationToken": null,
  "pageSize": 10,
  "totalCount": 1
}
```

### GET /api/v1/products?type=Cap&franchise=Chennai%20Super%20Kings&includeTotalCount=true

HTTP 200

```json
{
  "items": [
    {
      "_etag": "\"00000000-0000-0000-67a8-b398c80501dd\"",
      "id": "csk-cap",
      "storeId": "ipl-store",
      "type": "product",
      "merchandiseType": "Cap",
      "name": "CSK Cap",
      "franchise": "CSK",
      "franchiseId": "CSK",
      "franchiseName": "Chennai Super Kings",
      "isLimitedEdition": false,
      "description": "Chennai Super Kings cap",
      "price": 499,
      "currency": "INR",
      "stock": 100,
      "stockStatus": "inStock",
      "createdAt": "2026-10-29T13:23:35.5603177+00:00",
      "updatedAt": "2026-10-29T13:23:35.5603177+00:00"
    }
  ],
  "nextContinuationToken": null,
  "pageSize": 10,
  "totalCount": 1
}
```

### GET /api/v1/products?type=Cap&franchise=CSK&includeTotalCount=true

HTTP 200

```json
{
  "items": [
    {
      "_etag": "\"00000000-0000-0000-67a8-b398c80501dd\"",
      "id": "csk-cap",
      "storeId": "ipl-store",
      "type": "product",
      "merchandiseType": "Cap",
      "name": "CSK Cap",
      "franchise": "CSK",
      "franchiseId": "CSK",
      "franchiseName": "Chennai Super Kings",
      "isLimitedEdition": false,
      "description": "Chennai Super Kings cap",
      "price": 499,
      "currency": "INR",
      "stock": 100,
      "stockStatus": "inStock",
      "createdAt": "2026-10-29T13:23:35.5603177+00:00",
      "updatedAt": "2026-10-29T13:23:35.5603177+00:00"
    }
  ],
  "nextContinuationToken": null,
  "pageSize": 10,
  "totalCount": 1
}
```

### GET /api/v1/products?type=cAP&franchise=chennai%20super%20kings&includeTotalCount=true

HTTP 200

```json
{
  "items": [
    {
      "_etag": "\"00000000-0000-0000-67a8-b398c80501dd\"",
      "id": "csk-cap",
      "storeId": "ipl-store",
      "type": "product",
      "merchandiseType": "Cap",
      "name": "CSK Cap",
      "franchise": "CSK",
      "franchiseId": "CSK",
      "franchiseName": "Chennai Super Kings",
      "isLimitedEdition": false,
      "description": "Chennai Super Kings cap",
      "price": 499,
      "currency": "INR",
      "stock": 100,
      "stockStatus": "inStock",
      "createdAt": "2026-10-29T13:23:35.5603177+00:00",
      "updatedAt": "2026-10-29T13:23:35.5603177+00:00"
    }
  ],
  "nextContinuationToken": null,
  "pageSize": 10,
  "totalCount": 1
}
```

### GET /api/v1/products?search=ca&type=Cap&franchise=CSK&includeTotalCount=true

HTTP 200

```json
{
  "items": [
    {
      "_etag": "\"00000000-0000-0000-67a8-b398c80501dd\"",
      "id": "csk-cap",
      "storeId": "ipl-store",
      "type": "product",
      "merchandiseType": "Cap",
      "name": "CSK Cap",
      "franchise": "CSK",
      "franchiseId": "CSK",
      "franchiseName": "Chennai Super Kings",
      "isLimitedEdition": false,
      "description": "Chennai Super Kings cap",
      "price": 499,
      "currency": "INR",
      "stock": 100,
      "stockStatus": "inStock",
      "createdAt": "2026-10-29T13:23:35.5603177+00:00",
      "updatedAt": "2026-10-29T13:23:35.5603177+00:00"
    }
  ],
  "nextContinuationToken": null,
  "pageSize": 10,
  "totalCount": 1
}
```

### GET /api/v1/products?search=jer&type=Cap&franchise=CSK&includeTotalCount=true

HTTP 200

```json
{
  "items": [],
  "nextContinuationToken": null,
  "pageSize": 10,
  "totalCount": 0
}
```

### GET /api/v1/products?merchandiseType=Signed%20Photo&franchise=KKR&includeTotalCount=true

HTTP 200

```json
{
  "items": [
    {
      "_etag": "\"00000000-0000-0000-67a8-b39cfc0501dd\"",
      "id": "kkr-signed-photo",
      "storeId": "ipl-store",
      "type": "product",
      "merchandiseType": "Signed Photograph",
      "name": "KKR Signed Photo",
      "franchise": "KKR",
      "franchiseId": "KKR",
      "franchiseName": "Kolkata Knight Riders",
      "isLimitedEdition": true,
      "description": "Kolkata Knight Riders signed photo",
      "price": 4999,
      "currency": "INR",
      "stock": 2,
      "stockStatus": "lowStock",
      "createdAt": "2026-10-29T13:23:35.568701+00:00",
      "updatedAt": "2026-10-29T13:23:35.568701+00:00"
    }
  ],
  "nextContinuationToken": null,
  "pageSize": 10,
  "totalCount": 1
}
```

### GET /api/v1/products?type=Unsupported&includeTotalCount=true

HTTP 200

```json
{
  "items": [],
  "nextContinuationToken": null,
  "pageSize": 10,
  "totalCount": 0
}
```

### GET /api/v1/products?franchise=Chennai%20Super%20Kings&includeTotalCount=true

HTTP 200

```json
{
  "items": [
    {
      "_etag": "\"00000000-0000-0000-67a8-b398c80501dd\"",
      "id": "csk-cap",
      "storeId": "ipl-store",
      "type": "product",
      "merchandiseType": "Cap",
      "name": "CSK Cap",
      "franchise": "CSK",
      "franchiseId": "CSK",
      "franchiseName": "Chennai Super Kings",
      "isLimitedEdition": false,
      "description": "Chennai Super Kings cap",
      "price": 499,
      "currency": "INR",
      "stock": 100,
      "stockStatus": "inStock",
      "createdAt": "2026-10-29T13:23:35.5603177+00:00",
      "updatedAt": "2026-10-29T13:23:35.5603177+00:00"
    }
  ],
  "nextContinuationToken": null,
  "pageSize": 10,
  "totalCount": 1
}
```

### GET /api/v1/products?category=Cap

HTTP 400

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Unsupported catalogue parameter 'category'. Use search, type/merchandiseType, franchise or paging parameters.",
  "status": 400,
  "traceId": "00-74cd0c4df35cd28de1b0311bc1ffa4a8-360f10991b5c8fca-00"
}
```

### GET /api/v1/products?pageSize=0

HTTP 400

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "pageSize must be an integer between 1 and 50 (default 10).",
  "status": 400,
  "traceId": "00-59caf58ec4f05f12d5b434d492991005-1021041203fd7ea4-00"
}
```

### GET /api/v1/products?type=Cap&type=Jersey

HTTP 400

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Catalogue parameter 'type' must appear exactly once.",
  "status": 400,
  "traceId": "00-e3b433cebd38784aaa9632a30275fc85-bb06a1d27eea05af-00"
}
```

### GET /api/v1/products

HTTP 200

```json
[
  {
    "_etag": "\"00000000-0000-0000-67a8-b398c80501dd\"",
    "id": "csk-cap",
    "storeId": "ipl-store",
    "type": "product",
    "merchandiseType": "Cap",
    "name": "CSK Cap",
    "franchise": "CSK",
    "franchiseId": "CSK",
    "franchiseName": "Chennai Super Kings",
    "isLimitedEdition": false,
    "description": "Chennai Super Kings cap",
    "price": 499,
    "currency": "INR",
    "stock": 100,
    "stockStatus": "inStock",
    "createdAt": "2026-10-29T13:23:35.5603177+00:00",
    "updatedAt": "2026-10-29T13:23:35.5603177+00:00"
  },
  {
    "_etag": "\"00000000-0000-0000-67a8-b39cfc0501dd\"",
    "id": "kkr-signed-photo",
    "storeId": "ipl-store",
    "type": "product",
    "merchandiseType": "Signed Photograph",
    "name": "KKR Signed Photo",
    "franchise": "KKR",
    "franchiseId": "KKR",
    "franchiseName": "Kolkata Knight Riders",
    "isLimitedEdition": true,
    "description": "Kolkata Knight Riders signed photo",
    "price": 4999,
    "currency": "INR",
    "stock": 2,
    "stockStatus": "lowStock",
    "createdAt": "2026-10-29T13:23:35.568701+00:00",
    "updatedAt": "2026-10-29T13:23:35.568701+00:00"
  },
  {
    "_etag": "\"00000000-0000-0000-67a8-b395d00501dd\"",
    "id": "mi-jersey",
    "storeId": "ipl-store",
    "type": "product",
    "merchandiseType": "Jersey",
    "name": "MI Jersey",
    "franchise": "MI",
    "franchiseId": "MI",
    "franchiseName": "Mumbai Indians",
    "isLimitedEdition": false,
    "description": "Mumbai Indians jersey",
    "price": 1999,
    "currency": "INR",
    "stock": 50,
    "stockStatus": "inStock",
    "createdAt": "2026-10-29T13:23:35.568701+00:00",
    "updatedAt": "2026-10-29T13:23:35.568701+00:00"
  },
  {
    "_etag": "\"00000000-0000-0000-67a8-b39a880501dd\"",
    "id": "rcb-flag",
    "storeId": "ipl-store",
    "type": "product",
    "merchandiseType": "Flag",
    "name": "RCB Flag",
    "franchise": "RCB",
    "franchiseId": "RCB",
    "franchiseName": "Royal Challengers Bengaluru",
    "isLimitedEdition": false,
    "description": "Royal Challengers Bengaluru flag",
    "price": 299,
    "currency": "INR",
    "stock": 200,
    "stockStatus": "inStock",
    "createdAt": "2026-10-29T13:23:35.5603177+00:00",
    "updatedAt": "2026-10-29T13:23:35.5603177+00:00"
  }
]
```


## Backend closure (30 October 2026)

The historical observations above describe Gate 3.1. Product details is now implemented as a persisted point read with enriched metadata and 404 ProblemDetails. The unbounded legacy list exception is removed: array shape retained, capped at 50, name/id ordering, explicit Deprecation and Link alternate/next headers. Following next links transitions into the envelope without duplicates or omissions. New clients must use paged=true. Regression tests cover both changes; no inventory, cart, checkout or order semantics were changed.

Closure verification: dotnet restore/build succeeded (0 warnings/errors); real-emulator dotnet test passed 162/162, 0 failed/skipped. The first attempt found the emulator stopped (connection refused); starting the existing emulator resolved the environment failure. No normal product writes were performed.
