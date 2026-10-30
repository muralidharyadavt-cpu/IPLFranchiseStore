# AI usage disclosure

## 1. Overview of the AI-assisted workflow

ChatGPT and Codex were used substantially throughout this assessment. Work was organized around the supplied requirements, scoped milestones and explicit acceptance criteria rather than treating a generated application as a finished submission. Instructions constrained each stage, requested evidence from the real application and identified areas that needed investigation before further implementation. Reported results and subsequent review informed targeted corrections. This document describes that assistance; it does not imply that implementation or validation was performed entirely without AI.

The original four-page assessment remained the requirements authority. Repository source, actual request/response contracts and emulator behavior were used to check interpretations. Proposed production capabilities were kept separate from local demonstration features.

## 2. Tools used and scope of assistance

**ChatGPT** supported requirement interpretation and prioritization, architecture and trade-off discussions, implementation milestone planning, failure-scenario and acceptance-criteria design, review of reported results and identified gaps, and documentation/submission planning. Iterative instructions narrowed scope, challenged assumptions and requested concrete verification rather than relying on completion summaries alone.

**Codex** contributed project scaffolding, substantial backend and frontend implementation, Cosmos DB integration, test generation, build/test execution, debugging, refactoring and local integration verification. It also inspected existing code and assessment material, reviewed documentation accuracy and helped draft README and DESIGN content, including this disclosure. Its role extended well beyond boilerplate. Shell and repository tools were used to inspect files, run commands and compare outcomes; those activities are not equivalent to an independent human review of every line.

## 3. AI usage across planning, implementation and review

One example was the Cosmos checkout design. Assistance explored the trade-off between a single store partition, atomic multi-product writes and future franchise isolation. Implementation combined conditional product/cart writes, audits, immutable orders and durable results in a transactional batch. Review then examined the shared product-guard protocol and required independent-client race and rollback tests. The design records the hot-partition and cooperating-writer limitations rather than presenting local correctness tests as evidence of unlimited scalability.

A second example was the catalogue contract audit. A requirement-driven review questioned whether `type=product` represented merchandise or an internal document discriminator. Source and persisted-record inspection informed corrections to public merchandise filtering, franchise aliases, pagination and compatibility behavior. Follow-up review addressed product details and bounded legacy arrays. These changes were refined through feedback, with contract tests and explicit documentation of preserved fields and remaining compatibility costs.

A third example was focused reliability and reviewer hardening. Review requests identified gaps in product-read error handling, verification isolation and diagnostics. Codex helped implement discriminator-aware reads, distinguish missing storage from missing products, isolate live mutations in owned API processes/databases and test sanitized diagnostic fields. Role-separated navigation was also reviewed against backend environment restrictions: a development selector improves demonstration flow but does not implement authentication. Each correction had a narrower scope than redesigning the application.

## 4. Testing, validation and corrective iterations

Codex generated and executed backend xUnit/emulator tests and frontend Jest/React Testing Library tests, and assisted with real local customer/admin API flows. Test coverage included conflicting ETags, final-unit races, forced batch failures, durable replay, lost responses, paging, warnings and frontend retry behavior. Failures and review findings led to corrective iterations rather than being omitted from the process.

An earlier verified milestone reported 206 backend and 138 frontend tests passing, with isolated live flows also passing. Those are prior local results, not a fresh run for this documentation task or a clean-machine reproducibility guarantee. Component tests use mocks/jsdom; live API checks do not establish visual browser behavior or screen-reader accessibility. No production deployment, production load benchmark, cloud multi-region validation or completed accessibility audit is claimed.

## 5. Limitations and accountability

AI output can misunderstand requirements, overstate guarantees or miss failure paths. Documentation therefore distinguishes implemented behavior, source-reviewed claims, executed checks and deferred proposals. Authentication, payment/refund workflows and franchise tenancy remain future work, not generated features assumed complete. Reported tests require the documented emulator, credentials and environment.

AI assistance does not replace the assessment's expectation that the submitter can explain, defend and modify the solution. This disclosure makes no claim that all code was manually written, manually reviewed line by line, or independently tested by the candidate. Remaining uncertainties and operational risks are documented in DESIGN.md for review.
