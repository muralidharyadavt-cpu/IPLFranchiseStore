# Engineering design and decision records

Reviewed 30 October 2026 against the supplied assessment and **current working tree**, including uncommitted hardening. Current means implemented; proposed means future work. Alternatives are retrospective unless described as experiments.

Task 4.1 verification: **214 backend / 149 frontend tests passed**, plus owned-process customer/admin emulator flows. The earlier document-only review reported 206/138; focused storage/timezone corrections and their regression evidence are now reflected here. No production benchmarks, cloud SLAs, multi-region validation or independent browser/accessibility verification are claimed.

## Engineering decision summary

| Requirement or risk | Decision | Rejected alternative | Accepted trade-off | Code or test evidence |
| --- | --- | --- | --- | --- |
| One failing line must cancel the whole purchase | Co-locate cart, products, order, audits and receipt under one `storeId`; commit one conditional batch | Independent writes or a saga for the initial store | Hot partition, 20-line checkout cap and eventual tenant migration | [CheckoutService](backend/IPLFranchiseStore.Api/CheckoutService.cs); CheckoutIntegrationTests forced-batch rollback and maximum-order tests |
| Snapshot acceptance can race with inventory changes | Read guard before product; conditionally replace that guard inside every cooperating writer's batch | Preread validation alone, or a process-local lock | Extra writes/RUs and conservative 412 conflicts; external writers can bypass the protocol | [ProductConcurrency](backend/IPLFranchiseStore.Api/ProductConcurrency.cs); CheckoutIntegrationTests admin/acknowledgement race tests |
| Lost HTTP success must not purchase twice | Persist original outcome with business writes; replay original UUID and semantic payload | In-memory deduplication or generating a new ID on timeout | Receipt growth and per-tab client recovery limits; admin mutations lack durable replay | [CheckoutService](backend/IPLFranchiseStore.Api/CheckoutService.cs), [customerSession](frontend/src/customerSession.tsx); commit-then-lost-response test |
| Scarce stock must never go negative | Allocate at checkout, without cart reservations; require explicit snapshot acknowledgement | Holding inventory indefinitely when added to a cart | A limited-edition item in a cart is not guaranteed; losing checkout preserves the cart | [CartService](backend/IPLFranchiseStore.Api/CartService.cs); independent-client final-unit checkout test |
| Large catalogue with existing array clients | SDK continuation pages with stable ties; cap legacy arrays and signal continuation/deprecation | OFFSET paging, unbounded arrays or silent truncation | Legacy clients must follow links; optional COUNT costs extra and is not a shared snapshot | [CatalogueService](backend/IPLFranchiseStore.Api/CatalogueService.cs), [QueryIntegrationTests](tests/IPLFranchiseStore.Api.Tests/QueryIntegrationTests.cs) |
| Demo identities are not authorization | Map customer and admin routes only in Development; leave catalogue/health public | Exposing demo-header shopping/admin APIs in Production | Production currently cannot complete a customer journey; real identity/authorization remains work | [Program](backend/IPLFranchiseStore.Api/Program.cs), [AdminSecurityTests](tests/IPLFranchiseStore.Api.Tests/AdminSecurityTests.cs) |

**Most significant accepted risks:** transaction correctness depends on one logical partition and cooperating writers; account consistency/cross-instance visibility is unverified; demo identities are spoofable; receipts/audits grow without retention; uncertain admin writes have no durable replay. The ADRs explain these boundaries, not a claim of production readiness.

## ADR 1 — Architecture and service boundaries

### Context and invariants

The assessment requires ASP.NET, NoSQL persistence, a permitted frontend, atomic checkout and durable replay. Handlers must not calculate prices or orchestrate independent stock writes.

### Decision and implementation

The application uses .NET 10, React/TypeScript/Vite and stable Cosmos SDK 3.63.3: the mandated backend, a permitted typed frontend, and selected NoSQL storage with same-partition transactions. This is not evidence Cosmos outperforms SQL for commerce.

Boundaries are **logical, not separate assemblies**:

| Boundary | Actual implementation |
| --- | --- |
| Domain/data contracts | [Product](backend/IPLFranchiseStore.Api/Product.cs), [CartModels](backend/IPLFranchiseStore.Api/CartModels.cs), [OrderModels](backend/IPLFranchiseStore.Api/OrderModels.cs); audit records live in InventoryService |
| Application decisions | [InventoryService](backend/IPLFranchiseStore.Api/InventoryService.cs), [CartService](backend/IPLFranchiseStore.Api/CartService.cs), [CheckoutService](backend/IPLFranchiseStore.Api/CheckoutService.cs) validate and construct batches |
| Persistence/query support | [CosmosStore](backend/IPLFranchiseStore.Api/CosmosStore.cs), ProductConcurrency, CosmosPaging, CatalogueService, OrderHistoryService and StoreIndexes |
| HTTP | Query/Admin/Cart/OrderEndpoints map DTOs, identity, statuses and safe failure diagnostics under `/api/v1` |
| Presentation | [App](frontend/src/App.tsx), CustomerPages, AdminPage and role layouts; [api.ts](frontend/src/api.ts) owns transport contracts |

[Program](backend/IPLFranchiseStore.Api/Program.cs) registers one singleton CosmosClient and stateless singleton services. Request state is local. Services depend on CosmosStore/SDK types, without application interfaces or generic repositories. Protected virtual seams interleave writes/fail batches in real-storage tests; this is not infrastructure independence.

React hooks and [CustomerProvider](frontend/src/customerSession.tsx) manage identity, pending UUID intent and UI locking, without Redux/query caching. Search debounce, obsolete-read cancellation and cursor stacks are presentation concerns. The backend calculates decimal money; the browser formats values. Customer order timestamps default to Asia/Kolkata; a labelled history selector offers browser-local time or UTC and retains the selected valid zone in per-tab session storage. Order details reuse it; admin audits retain IST. UTC persistence and date filters are unchanged.

### Alternatives and consequences

| Retrospective alternative | Benefit | Reason not adopted here |
| --- | --- | --- |
| Relational monolith | Constraints, joins and familiar multi-row transactions | Outside the selected NoSQL approach; a reasonable production alternative, not technically inferior |
| Microservices/sagas | Independent deployment and ownership | Adds distributed inventory/order coordination and compensation without a demonstrated scaling need |
| Multiple clean-architecture projects/interfaces | Compile-time dependency boundaries | More scaffolding for this scope; current SDK coupling is accepted, not described as Clean Architecture |

Positive: inspectable transactions and few components. Negative: SDK/serialization coupling, dense JSX/tests and handwritten API types. Mediator/CQRS frameworks, messaging and distributed transactions were not added without a demonstrated need.

A hypothetical purchase cap belongs before service batch construction, with boundary/race tests, explicit DTO evolution, then client/UI validation and component tests, rather than a frontend-only rule. Evidence: [InventoryIntegrationTests](tests/IPLFranchiseStore.Api.Tests/InventoryIntegrationTests.cs), [CustomerPages tests](frontend/src/CustomerPages.test.tsx), [API contract tests](frontend/src/api.test.ts) and [role tests](frontend/src/RoleLayouts.test.tsx).

## ADR 2 — Cosmos data model, partitioning and cost

### Context, decision and implementation

Default database **IPLFranchiseStore**, container **StoreData**, partition key **`/storeId`**, value **`ipl-store`** co-locate the transaction's records. Configuration names are not tenant authorization. [CosmosStore.InitializeAsync](backend/IPLFranchiseStore.Api/CosmosStore.cs) creates resources and inserts four deterministic products; existing IDs yield ignored create conflicts, never inventory resets.

| Document `type` | Identity/content |
| --- | --- |
| `product` | SKU ID, merchandise metadata, INR decimal price, stock, UTC timestamps, Cosmos `_etag` |
| `productGuard` | `product-guard-{productId}`; independent ETag fence |
| `cart` | `cart-{SHA256(userId)}`; lines, accepted snapshots, UUID Version, internal ETag, completion metadata |
| `cartRequest` | User-hash + request UUID; operation, semantic fingerprint, original outcome/status/time |
| `order` | `order-{orderId:N}`; owner, historical lines/total, purchased cart version/time/correlation |
| `checkoutRequest` | `checkout-request-{orderId:N}`; global ID binding, owner, fingerprint, original outcome |
| `inventoryAudit` | Product, actor, operation, old/new values, reason, timestamp, correlation ID |

[ProductTaxonomy](backend/IPLFranchiseStore.Api/ProductTaxonomy.cs) separates merchandise types/franchise aliases from document `type`. Four legacy IDs have read/query fallback classification; other unclassified products become Other without migration. Prices use decimal whole paise and include tax by assumption; shipping is absent.

**Consistency is not explicitly configured:** CosmosClientOptions inherits the account level, not independently verified here. Under Session, SDK-managed tokens are client-local; this code does not forward them across instances. Proposed production choice: provision/assert account Session consistency for read-your-writes, then verify cross-instance visibility. Stronger consistency needs cost justification. Conditional commit checks are not globally strong reads. See [SDK consistency contract](https://learn.microsoft.com/en-us/dotnet/api/microsoft.azure.cosmos.cosmosclientoptions).

Point reads use ID/partition. Product/inventory reads check the discriminator and container existence before returning item 404. Cart/order optional readers now also check container existence on SDK 404 before returning absence; infrastructure failure propagates to sanitized 503 without changing genuine missing-document behavior. CartOrderStorageTests reproduce the previous misclassification and cover both missing infrastructure and missing items.

### Queries, indexes and bounds

[CatalogueService](backend/IPLFranchiseStore.Api/CatalogueService.cs) uses persisted, parameterized Cosmos predicates: partial case-insensitive name, merchandise and franchise filters AND-combine. [OrderHistoryService](backend/IPLFranchiseStore.Api/OrderHistoryService.cs) scopes user and inclusive UTC dates. [StoreIndexes](backend/IPLFranchiseStore.Api/StoreIndexes.cs) adds composites `(name ASC,id ASC)` and `(createdAt DESC,id DESC)`, preserving existing indexing/documents. Polling index transformation shares startup's timeout; no offline migration coordinator exists.

[CosmosPaging](backend/IPLFranchiseStore.Api/CosmosPaging.cs) reads actual SDK pages, default 10/max 50, without OFFSET or in-memory paging. Data Protection wraps cursors bound to query/options/store/SDK and, for history, user. Shared production instances need a compatible shared key ring. Short/empty pages can still have tokens. COUNT is optional, a separate query per page, and not snapshot-consistent with items; current UI requests it. Partial search, broad indexing and each indexed write consume workload-dependent RUs. No RU benchmark was performed. Audit retrieval remains unbounded. Legacy arrays cap at 50 with deprecation/next links: shape compatibility does not preserve unlimited enumeration for clients ignoring links.

### Alternatives and consequences

| Partition model | Atomicity | Scale/query cost | Complexity |
| --- | --- | --- | --- |
| Current shared store | One ordinary TransactionalBatch covers mixed-franchise purchases | Hot key; cheap partition-targeted queries | Simplest invariants |
| Per franchise | Atomic within one franchise; existing mixed cart no longer fits one batch | Distributes stores; cross-store discovery/reporting costs | Tenant migration and explicit cart policy |
| Per product/customer | Product/cart/order naturally separate; current atomic purchase unavailable | More distributed capacity, cross-partition coordination | Reservations/saga or another transaction strategy required |

Standard documented ceilings are 20 GB and 10,000 RU/s per logical partition, not measured application throughput ([limits](https://learn.microsoft.com/en-us/azure/cosmos-db/concepts-limits)). Increasing container RUs cannot remove a hot-key ceiling. The **ordinary TransactionalBatch used here** is same-partition, capped at 100 operations, 2 MB and five seconds ([batch contract](https://learn.microsoft.com/en-us/azure/cosmos-db/transactional-batch)); newer distributed-transaction previews are not used or assumed compatible with this SDK/emulator.

Checkout caps 20 lines, quantity 1–10: `3N+3`, at most 63 operations. Combined serialized documents, including the order repeated in its result, are capped at 1 MiB to leave wire-envelope headroom; oversize returns 413, never split purchases. Receipts/audits have no retention cleanup. Evidence: [QueryIntegrationTests](tests/IPLFranchiseStore.Api.Tests/QueryIntegrationTests.cs) enumerate 124 products, check index upgrades/cursors/history; CheckoutIntegrationTests cover maximum-size success and count/byte rejection.

## ADR 3 — Concurrency, idempotency and transactional integrity

### Context and rejected mechanisms

Required invariants: stock never goes negative through supported writers; no partial purchase; one order per purchased cart version; explicit snapshot acceptance; every successful price/stock change has its audit. In-memory locks cannot coordinate independent API instances. Independent item writes cannot roll back a multi-product purchase.

[ProductConcurrency](backend/IPLFranchiseStore.Api/ProductConcurrency.cs) uses an ETag-conditional **guard replacement**, not a batch read as a fence. Earlier README notes report an emulator probe where batch ReadItem If-Match was ignored; that standalone probe was not repeated here. Current guard tests prove conditional replacements instead of relying on that reported behavior.

### Actual flows and transaction boundaries

1. **Admin stock:** InventoryService validates quoted expectedETag, actor/reason and signed nonzero integer; reads guard **before** product; compares product ETag; computes stock using a long intermediate and rejects underflow/overflow. One batch conditionally replaces product, creates one audit and conditionally replaces guard.
2. **Admin price:** same MutateAsync path, with positive bounded decimal/no fractional paise and unchanged-price rejection. Returns the committed batch ETag, not a later read. Admin has no durable replay key.
3. **Cart mutation:** CartService.ExecuteAsync first checks a durable receipt. A new action checks UUID cart Version and quantity/stock. Add/set read guards before products; add snapshots new lines, while quantity edits preserve acceptance. One batch creates/conditionally replaces cart, creates receipt and replaces applicable guards. Add/set fence all relevant cart products; remove needs no product guard. No inventory is reserved/decremented.
4. **Acknowledgement:** requires every displayed product ID/ETag exactly once; checks existence, stock and versions, then refreshes accepted price/stock/version. Cart, receipt and conditional guards commit together. It is not an automatic refresh or locked future-checkout promise.
5. **Checkout:** CheckoutAsync looks up receipt before current cart validation; validates cart Version, nonempty/unique lines, size, quantity, product existence, price/currency, accepted snapshots and stock. Guards precede product reads. One batch conditionally replaces cart, creates order/result and, for every product, conditionally replaces product, creates audit and conditionally replaces guard.
6. **Ambiguous-response retry:** same owner, ID and original payload replay the stored outcome before inspecting the now-empty or changed cart. No new product ETag is substituted to reapply an intent.

```mermaid
sequenceDiagram
    participant UI as React client
    participant API as CheckoutService
    participant DB as StoreData / ipl-store
    UI->>API: orderId + expectedCartVersion + demo user
    API->>DB: Read checkoutRequest
    alt Matching receipt exists
        DB-->>API: Original outcome
    else New intent
        API->>DB: Read cart; then each guard before its product
        API->>API: Validate every line, snapshots and batch budget
        alt Invalid line
            API->>DB: Persist rejection receipt only
        else Valid
            API->>DB: ONE batch: conditional cart/products/guards + order/result/audits
            DB-->>API: All commit or all roll back
        end
    end
    API-->>UI: Original 201 order, or consistent error/failing lines
```

### Consequences, assumptions and evidence

Batch ACID/snapshot isolation applies within the transaction; prereads are outside it, bridged by ETag conditions. Two checkouts reading the final unit cannot both replace its original product ETag. The winner writes stock zero; the loser rolls back **all** purchase operations. Different order IDs for one cart also compete on the same cart ETag. The same order ID competes on create-only order/result IDs. Guard replacements additionally serialize snapshot acceptance with inventory writers: an intervening cooperating writer changes the guard and invalidates the batch, even when an acknowledgement did not change product content. This can conservatively reject unrelated cart activity with 412; it buys correctness at contention/RU cost.

Server conditions provide correctness; disabled buttons cannot. All existing-product writers must cooperate; account-key edits/older servers can invalidate snapshot/audit assumptions. Seed creation is create-only. Lazy guard creation can precede a failed batch: no purchase effects does not mean no metadata/receipt writes.

### Durable identity and recovery

Cart request IDs are per-user; checkout order IDs bind globally within this store partition. SHA-256 fingerprints cover DTO intent: operation/cart version/product/quantity/sorted observed versions for carts; owner/canonical order ID/cart version for checkout. JSON field ordering is irrelevant. Changed intent with the same key returns 409. Semantic rejections are durable too; invalid identity/UUID can be rejected before receipt processing. Business rejection never creates order/audits or changes inventory/cart.

```mermaid
sequenceDiagram
    participant UI as Client session storage
    participant A as API instance A
    participant DB as Cosmos
    participant B as Recreated API instance B
    UI->>UI: Persist original orderId + cart version before POST
    UI->>A: Checkout original intent
    A->>DB: Atomically commit order, result, stock, audits, cleared cart
    A--xUI: Success response lost
    UI->>UI: Keep pending intent; block new mutations
    UI->>B: Explicit retry, identical ID and payload
    B->>DB: Read persisted checkoutRequest
    DB-->>B: Original successful order
    B-->>UI: Same 201 response; no deduction
```

Unconfirmed results never synthesize success. Duplicate-create races reread the winning receipt; inability to confirm remains retryable. Orders retain purchased names/prices/totals/time; there is no update API. Immutability is application behavior, not tamper resistance. Stock/audit share batch/time; checkout reason includes order ID.

Evidence: [CheckoutIntegrationTests](tests/IPLFranchiseStore.Api.Tests/CheckoutIntegrationTests.cs): IndependentClientsCompetingForFinalUnit_ExactlyOneOrderWins, IndependentClients_CheckoutSameCart_DeductOnce, ForcedLastOperationFailure_RollsBackEveryProductOrderAuditAndCartWrite, CommitThenLostSuccessResponse_RetryReturnsOriginalOrderWithoutSecondDeduction and HistoricalOrderPricesRemainUnchangedAfterAdminPriceUpdate. [CartIntegrationTests](tests/IPLFranchiseStore.Api.Tests/CartIntegrationTests.cs) cover replay/restart/snapshot races. These use real emulator batches; barriers are test interleavings, not production locks. Multi-region/load guarantees remain untested.

## ADR 4 — Franchise multi-tenancy evolution (proposed only)

### Context, invariants and alternatives

Independent franchises require catalogue, inventory, order/report isolation. Current franchise labels are merchandise metadata, not tenants: every record still belongs to `ipl-store`. A partition key alone never authorizes a request.

Proposed target: one transactional partition **per tenant store**, preserving local batch invariants. Mixed-franchise purchases require separate checkouts or explicit reservation/compensation redesign, not silent splitting. Shared-partition tenant filters retain today's hot key and isolation risk. Container/account per tenant improves operational separation but increases provisioning/cost. None is implemented.

### Phased migration and rollback

1. **Identity first:** define tenant/store IDs and OIDC subjects; enforce tenant memberships, admin permissions and customer ownership server-side. Test forged tenant routes, object IDs and cross-tenant paging before moving data.
2. **Define history policy:** route `/api/v1/stores/{storeId}/...`; authorize before resolving the partition. Add tenant/store ownership to every document, guard, receipt, order/audit and fingerprint. Existing mixed-franchise orders remain in a protected legacy store unless a reviewed business policy says otherwise; never rewrite purchased totals/names.
3. **Backfill into a new container:** changing a partition value requires copy/migration, not an in-place replace. Map products by reviewed franchise ownership; copy guards/receipts with explicit mappings. Quarantine ambiguous carts/orders. Inventory migration uses a write fence/maintenance window for the demo; uncontrolled dual writes would destroy atomicity.
4. **Validate:** reconcile document counts, stock, money, immutable snapshots, ownership and idempotency bindings; verify indexes, failed-line rollback and cross-tenant negative tests against target partitions. Replays of pre-cutover IDs need an authorized legacy resolver or preserved routing namespace.
5. **Cut over:** switch routing/config only after reconciliation. Retain the old container read-only. Before target writes, rollback can switch back; after target writes, freeze and reconcile/reverse-migrate under a fence. Blindly switching to a stale source would duplicate purchases or resurrect stock.

Tenant reports target immutable orders; cross-store analytics needs a separate derived pipeline with explicit lag/reconciliation. Positive: isolated local atomicity. Negative: hot franchises, legacy routing, reporting costs and capacity operations. Evidence is **design only**: current [user isolation tests](tests/IPLFranchiseStore.Api.Tests/QueryIntegrationTests.cs) and CosmosStore demonstrate the boundaries to replace, not tenant authorization or migration.

## Failure-mode analysis

"Not directly verified" marks absent evidence. C = [checkout](tests/IPLFranchiseStore.Api.Tests/CheckoutIntegrationTests.cs), K = [cart](tests/IPLFranchiseStore.Api.Tests/CartIntegrationTests.cs), P = [product-read tests](tests/IPLFranchiseStore.Api.Tests/ProductReadIntegrationTests.cs). Method prefixes identify cases.

| Trigger | Invariant | Current behavior | Recovery | Evidence | Residual risk |
| --- | --- | --- | --- | --- | --- |
| Final-unit race | No oversell | One conditional batch wins; tested loser 412 | Review latest cart | C IndependentClientsCompetingForFinalUnit | Cloud contention unmeasured |
| One unavailable line | No partial purchase | Whole purchase rejected, cart retained | Fix/remove line; new intent | C OneUnfulfillableLine | Guard/receipt metadata may exist |
| Duplicate order ID | One deduction | Matching persisted result replayed | Same original payload | C IndependentClients_CheckoutSameCart | Receipt retention required |
| Changed payload/key owner | No intent substitution | 409 | New intentional ID after review | C OrderIdReuseByOtherPayloadOrUser; K ReusingRequestId | Demo owner is spoofable |
| Commit then lost response | No duplicate purchase | Original outcome survives service recreation | Replay saved intent | C CommitThenLostSuccessResponse; frontend lost-response tests | Tab loss loses recovery handle |
| Cosmos timeout before commit | No partial transaction | Transport failure can be ambiguous to caller | Replay identical cart/order command | Not directly verified at network boundary | 15s UI timeout is not proof of rollback |
| Database unavailable | No false purchase success | Transport failures sanitized; product storage failures 503 | Restore dependency, replay uncertain writes | P MissingStorageResource; real transport outage Not directly verified | Cart/order infrastructure classification covered by CartOrderStorageTests |
| Outdated admin ETag | No silent overwrite | 412, no successful mutation audit | Explicit reload/review/resubmit | Inventory StaleETag test | Admin has no durable replay receipt |
| Cart price changes | Explicit acceptance | Warnings, checkout blocked/revalidated | Acknowledge exact displayed versions | K ProductChanges; C PriceIncreaseRequiresAcknowledgement | Another change can race acceptance |
| External writer bypasses guard | Cooperative snapshots/audits | Not supported; product ETags only offer partial protection | Reconcile; restrict writer credentials | Inventory direct-write race tests ETags; full bypass Not directly verified | Arbitrary writes can violate invariants |
| Pending session storage lost | Preserve original purchase intent | Recovery information can be lost | Inspect history; avoid blind new purchase | Corrupt/blocked storage tests; total-loss recovery not directly verified | No cross-device recovery workflow |
| Another user reads history/order | Ownership filtering | Header-scoped history; order mismatch 404 | Correct identity | Query history isolation; C OrderIdReuseByOtherPayloadOrUser | Not authenticated ownership |

## Scaling to 10x

**Current design → instrument/measure → identify bottlenecks → optimize queries/caching → reconsider partitioning/transaction strategy.** There is no measured baseline from which to promise 10x capacity.

Measure RU/latency, partition utilization, 429 waits, product/guard conflicts, batch duration/size, cart length, COUNT/search costs and receipt growth. Popular SKUs/acknowledgements contend on fences; different SKUs still share one partition. Sequential reads add latency. More API instances cannot distribute this key or remove conflicts.

Reduce repeated counts, inspect query/index metrics and cache stable catalogue metadata with bounded staleness. Cached prices/stock never authorize checkout. Test cross-instance cursor keys/session visibility. Tune RUs/indexes, then bound admission/backpressure: retries amplify saturation. Existing conflict/throttling handling is not an application rate limiter.

If the store remains the bottleneck, decide whether per-franchise purchases meet the business need. Distributing products while retaining a mixed-store atomic promise requires a different proven transaction/reservation design, not just changing the key. No illustrative RU/traffic figures are presented as measurements.

## Production readiness and security

### Actual environment and route exposure

[Program](backend/IPLFranchiseStore.Api/Program.cs) invokes the mapping extensions in every environment; each restricted extension checks `IsDevelopment()`, and QueryEndpoints separately guards order history. These are route-registration restrictions, not authentication middleware.

| API routes (all under `/api/v1`) | Development | Production / Staging |
| --- | --- | --- |
| `GET /health`, `GET /health/dependencies` | Mapped | Mapped |
| `GET /products`, `GET /products/{id}` | Mapped | Mapped |
| Cart base `/demo-users/{userId}/cart`: `GET` base, `POST` base + `/lines`, `PUT`/`DELETE` base + `/lines/{productId}`, `POST` base + `/acknowledgements` | Mapped | Not mapped: 404 |
| `POST /orders/checkout`, `GET /orders`, `GET /orders/{orderId}` | Mapped | Not mapped: 404 |
| `POST /admin/products/{id}/stock-adjustments`, `POST /admin/products/{id}/price-updates`, `GET /admin/products/{id}/audits` | Mapped | Not mapped: 404 |

**Local assessment:** caller-selected cart path user, order `X-Demo-User-Id`, and audit actor are not authentication. Any local caller can impersonate another demo user or submit admin operations. [demoRole](frontend/src/demoRole.tsx) stores Shopper/Admin separately from identity; UI guards improve navigation, not authorization.

**Current Production environment:** only catalogue and health APIs remain available; this is **not an authenticated production storefront**. Production frontend builds ignore Admin and omit role switching, but customer UI presence does not enable the absent cart/order APIs. No JWT authentication or production authorization is implemented. [AdminSecurityTests](tests/IPLFranchiseStore.Api.Tests/AdminSecurityTests.cs) verify representative restricted routes return 404 in Production/Staging; the complete mapping inventory above is source-reviewed. Role/AdminPage tests use explicit API mocks.

**Proposed:** OIDC sign-in/JWT signature, issuer, audience and expiry validation; claims-derived actor/subject, server RBAC, tenant memberships and ownership on every operation. Use least-privilege managed identity/RBAC and a secret manager. Current emulator keys use User Secrets/environment, Gateway HTTPS, trusted certificate and no TLS bypass. Validate quantities, decimal precision, explicit versions, bounded text and parameterized filters; SDK/account-key access can bypass all application rules.

**Reliability:** Cosmos RequestTimeout is 10 seconds per configured request, not a whole checkout deadline; startup initialization is bounded at 30 seconds and dependency check at 10. Frontend transport aborts after 15 seconds and treats uncertainty as replayable. SDK 429 retries are configured to **two**, honoring service RetryAfter; retry-wait budget is not overridden (installed SDK documentation defaults to 30 seconds). There is no custom exponential-backoff policy or automatic fresh-ETag conflict retry. Exhausted SDK throttling generally maps to sanitized 503, not an application 429/Retry-After policy. Other SDK internals are not claimed absent. Admin failures require product/audit inspection, never blind replay. [SDK retry contract](https://learn.microsoft.com/en-us/dotnet/api/microsoft.azure.cosmos.cosmosclientoptions.maxretryattemptsonratelimitedrequests).

Liveness stays 200 when storage fails. Dependency health checks container metadata, not seed completeness/index readiness. Initialization failures log safely; there is no background initializer retry. Frontend keeps uncertain customer commands in per-tab sessionStorage and blocks new mutations until replay; browser closure/clearing storage is outside that recovery boundary.

**Observability:** endpoint failure event 1001 records Operation, RequestId, TraceId and ExceptionType, excluding full exception messages, request bodies and credentials. Orders/audits store correlation IDs; admin responses include correlation/ETag headers. TraceId records HTTP Activity.Id when present, otherwise RequestId; no exporter, business metrics, RU dashboards or alerts exist. Add checkout success/failure, conflict frequency, retry count, contention and dependency/partition signals before claiming operational readiness. [EndpointDiagnosticsTests](tests/IPLFranchiseStore.Api.Tests/EndpointDiagnosticsTests.cs) verify safe fields and unchanged business statuses.

## Additional Scenarios, Product Judgment and Scope Decisions

The assessment makes shipping optional and franchise isolation design-only; it does not define reservation, return or fulfilment workflows. These choices therefore need explicit business decisions rather than inferred implementation claims.

| Business scenario and impact | Status and decision | Accepted trade-off / future work |
| --- | --- | --- |
| **Limited editions and abandoned carts:** scarce signed photos can attract speculative carts and disappointed fans | **Partially addressed.** Limited-edition metadata, stock checks, explicit change acknowledgement and atomic final-unit allocation exist. Carts hold no stock, so abandoned carts do not block sales. There is no cart expiry, purchase history cap or anti-scalping policy | A saved cart is not a purchase guarantee; two fans may see availability before only one wins. Make that expectation clear. Agree per-customer limits and abuse controls before a scarce-product launch; the current 10-per-line rule is not a lifetime purchase limit |
| **Reservation versus checkout-time allocation:** fairness must be balanced against inventory being trapped in unpaid sessions | **Implemented choice: checkout-time allocation.** No reservation is created at add/acknowledgement time. **Reservation workflow deferred** | Keeps abandonment from reducing sellable inventory and avoids expiry/release coordination, at the cost of checkout rejection. If payment or measured demand justifies holds, design bounded expiry, abuse limits, atomic consume/release and idempotent recovery; do not equate acknowledgement with a hold |
| **Cancellations, refunds and stock restoration:** duplicate refunds or premature restocking lose money and can sell goods that never return | **Deferred.** Historical orders remain immutable; a positive audited admin adjustment is possible but is not a cancellation/returns workflow. No payment/refund integration exists | Current orders have no cancellation/fulfilment lifecycle. Define eligibility and returned-item inspection before restoration. Future compensating events need their own idempotency, authorization and atomic stock/audit writes; payment refund state must be reconciled separately, never implemented by deleting the original order |
| **Physical inventory reconciliation:** theft, damage or warehouse counting errors break the link between digital availability and fulfilment | **Partially addressed.** Conditional stock adjustments with mandatory actor/reason and atomic audits support manual corrections. No physical count, warehouse integration or reconciliation job exists | Audit explains application changes, not physical truth, and is not tamper-proof. Define count ownership, discrepancy approval and stocktake timing. Reconcile through the same guard protocol; an external system must not overwrite stock directly |
| **Tax and shipping:** an apparently correct item total can still undercharge delivery or produce an inadequate invoice | **Partially addressed.** INR prices include tax as required, with whole-paise validation and historical totals. Shipping, tax breakdowns, jurisdiction rules and invoice calculation are **deferred** | Current total is merchandise only, not a production delivered-price promise. Agree tax presentation and shipping/serviceability rules with business owners; persist accepted charge components and require renewed acceptance if the payable total changes |
| **Franchise-level inventory ownership:** branding alone cannot decide who can edit stock, fulfil an order or receive sales reports | **Design only / deferred.** Today franchise is merchandise metadata in shared `ipl-store`, not an enforced tenant/warehouse ownership boundary. ADR 4 describes the migration | Shared partition preserves mixed-franchise atomic checkout but cannot provide franchise isolation. First establish validated membership and inventory ownership; then migrate to authorized store partitions with explicit mixed-store checkout and legacy-order policies |

These are deliberately narrower than a production commerce platform. The implemented beyond-minimum safeguards concentrate on durable recovery, snapshot races, truthful audit history and safe verification; reservations and financial workflows would add new invariants that the current tests do not establish.

| Additional engineering decision | Business benefit and evidence | Remaining work |
| --- | --- | --- |
| Isolated verification | Avoid damaging demo inventory: owned child, unique CLI-configured database; [harness tests](frontend/src/verificationIsolation.test.js) | Managed cleanup of identified test resources |
| Bounded legacy compatibility | Preserve array shape and signal continuation instead of silently truncating | Version/opt-in migration of legacy `{error}` 503 to ProblemDetails |
| Durable recovery records | Preserve original outcomes across retries/restarts | Define retention and recovery before deleting receipts; no TTL cleanup exists |
| Rate limiting, CI and Docker deferred | Keep optional delivery work behind correctness/security | Abuse controls and repeatable delivery before public operation |

The [verification harness](frontend/scripts/verificationIsolation.cjs) owns child configuration, checks readiness and blocks escaped requests/overrides. It trusts the assembly, stops its child and retains databases; prior reports are not new runs/benchmarks.

## Assumptions, limitations and ranked next steps

Local single-store operation is the target. Immutability is not tamper resistance. Paging is not a concurrent snapshot; key sharing/read visibility need multi-instance validation. Audit/results grow indefinitely; customer display defaults to IST with browser-local/UTC selection; admin audits remain IST. The PDF's stock-two/last-one contradiction is handled with isolated stock-one fixtures, not changed seeds.

Risk order: **P0** authentication/authorization and writer restrictions before exposure; **P1** consistency/cloud recovery, telemetry, retention/reconciliation and pending-intent recovery; **P2** formatting/coupling, browser/accessibility review and delivery automation. Payments, shipping, production deployment and tenant migration are absent. AI-USAGE remains a separate deliverable.

## Requirement traceability and self-audit

This checklist maps the supplied PDF. Task 4.1 regression results are recorded above; no new independent browser check was executed for these corrections.

| PDF requirement / evaluation criterion | Design coverage | Source/test evidence | Remaining gap |
| --- | --- | --- | --- |
| p1 catalogue, INR, signed-photo stock/audits | ADR2/3 | Product, InventoryService; InventoryIntegrationTests | Tax-inclusive assumption; no shipping |
| p1 search, combined filters, capped stable paging | ADR2 | CatalogueService/CosmosPaging; QueryIntegrationTests | Concurrent edits not snapshot-paged |
| p1 cart 1–10, request IDs, accepted changes | ADR3 | CartService; CartIntegrationTests | No reservation/cross-device recovery |
| p1–2 checkout no oversell/partial failure/retry | ADR3, failure matrix | CheckoutIntegrationTests independent-client/rollback/lost-response cases | Cloud load/multi-region unverified |
| p2 immutable orders/history/timezones | ADR2/3 | OrderHistoryService; historical snapshot/date-boundary tests | Customer display preference is per-tab; admin audits remain IST |
| p2 API evolution/errors; p4 API evaluation | ADR2, production readiness | QueryEndpoints; legacy contract/ProductRead/Diagnostics tests | Legacy JSON 503 exception; rate limiting optional/deferred |
| p2 typed accessible frontend; p4 frontend quality | ADR1, readiness | api.ts, CustomerPages/AdminPage/RoleLayouts tests | Browser/screen-reader checks pending |
| p2 clear layers, DI, patterns | ADR1 | Program, services and HTTP modules | Logical boundaries remain SDK-coupled |
| p2 data/consistency/cost; p4 data modelling | ADR2, scaling | CosmosStore, StoreIndexes, CosmosPaging | Account policy/RU benchmarks unverified |
| p2 reliability; p4 failure correctness | ADR3, matrix/readiness | C/K, ProductReadIntegrationTests, recovery/harness tests | Precommit network timeout not directly verified |
| p2 security; p4 security judgement | ADR4, readiness | AdminSecurityTests, demoRole/route guards | Production authentication not implemented |
| p2 observability; p4 operability | Readiness/scaling | EndpointDiagnosticsTests, Program/endpoint logs | Business metrics/exporter/alerts absent |
| p2 tenant evolution incl. migration/first step | ADR4 | Explicit proposal bounded by existing partition tests | No implemented tenancy/migration evidence |
| p3 submission/design/assumptions; p4 trade-offs/critical thinking | Four ADRs, additional scenarios/limitations | Source references and existing verification reports | AI-USAGE separate; final submission commits pending |
| p4 scale 10x and live-change/code-review readiness | Scaling and ADR1 extension path | Independent-client tests and concrete code locations | Interview ability cannot be established from files |
| Optional delivery, CI/containerization/rate limiting | Scope table | Absent by deliberate scope | Proposed, not claimed complete |

Unverified: account consistency, performance, complete network faults, cloud topology, tenancy and browser accessibility. README now documents the complete setup, test suites and isolated verification workflow. CATALOGUE_AUDIT's unbounded-list/absent-details observations are historical and superseded by its closure; it is not the current run guide.
