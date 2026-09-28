# Decision services implementation contract

Approved September 23, 2026. Public entry point: `revi.Decide` / `IDecisionService`.
Transport protocol: `SystemOne`. TypeSafe/Jev is the initial configured provider/model,
not the name of the service or protocol. Other transports implement `IDecisionModelClient`.

## Contracts and file-level workplan

1. `Core/Decisions`: typed Choice, Boolean probability (Noul compatibility), and Score
   questions/answers; full distributions; strict response completeness; model/prompt identity;
   token usage and metadata-only observation. HTTP adapter supports cancellation, bounded retries,
   Retry-After, concurrency, and status-only errors that cannot echo source data or credentials.
2. `Core/Decisions`: decision prompt/model registry, declared-name resolution, versioned prompt
   identity, structured runtime state (no string substitution). Provider `.rcfg` files and
   `Models/Decision/*.rcfg` reuse the existing configuration machinery. Human-authored question
   packs retain the RConfig envelope, with YAML only in the raw `[[_decision]]` body via the
   existing YamlDotNet dependency. No available RLON specification or
   interpreter was found; the format parser is isolated for future compatibility work.
3. `Core/Services`, `Analyzers`: explicit reset versus additive assembly loading, dependency-ordered
   registry initialization, matching runtime/analyzer naming rules, decision-file diagnostics.
4. `Core/Agents`: optional context selector, authorized and executable candidates only, required
   tools retained, strict all-answer validation, timeout/failure fallback to authorized catalog.
   Selection never grants execution permission. Skills/resources share the candidate contract.
5. `Core/Documents`: immutable per-collection text index; BM25 plus optional precomputed vectors;
   bounded decision reranking; original spans, source versions and IDs; explicit absent/uncertain
   results; MIME-aware extraction boundary; document-search and search-files integration.
6. `Core/Decisions`, `Refinery`: separately versioned policies, pinned model/prompt fingerprints,
   unknown/review outcomes, threshold evaluation and drift comparison on labelled samples.
7. `Core/Documents`: bounded adaptive retrieval pilot; code owns rounds, calls, cancellation and
   deadlines. Decision models advise sufficiency/contradiction/next-search utility.
8. `WonderBase.Core`: opt-in tenant-scoped decision pilots for authorized search and evidence-bound
   observation/graph proposals; ledger-backed usage; no canonical merge/publication side effects.
9. `Forge`: read/edit/validate decision files; preserve authored text rather than serializing JSON.

## Acceptance checks

- HTTP fixtures for all primitives, structured instructions/criteria, auth, endpoint versioning,
  401/422 no retry, 429/529 retry, cancellation, incomplete/unknown/nonfinite probabilities.
- Parser diagnostics for duplicate keys, unknown fields, missing names/models/questions, invalid
  criteria; multiline prose and nested structured criteria; stable content hashes.
- Additive assemblies retain app resources, providers load before dependent models across all
  assemblies, explicit reload clears state, folder names never alter declared identifiers.
- Classifier enum mapping preserves distributions and rejects ambiguous or undefined mappings.
- Tool filtering cannot expand allowlists; required tools and full-catalog fallback survive errors.
- Search returns exact substrings with document/version/span identity; separate collections do not
  share state; unsupported binary files never become UTF-8 garbage; failure is observable.
- Policies refuse mismatched model/prompt identities. Calibration reports coverage and conditional
  error separately. Adaptive loops remain bounded even when the judge repeatedly asks to continue.
- WonderBase passes only authorized evidence and creates review proposals; metered calls settle
  through existing credits APIs. No live feature becomes default based on published benchmarks.

No production keys or paid model requests are needed for deterministic acceptance tests.

## Delivered scope and verification

The implementation includes the separate decision subsystem and SystemOne transport;
named RConfig packs with embedded YAML decision objects and decision models;
additive registries and matching analyzer names;
opt-in tool selection; exact-span hybrid document search and citation checks; metadata
telemetry and offline calibration; versioned policies and bounded adaptive retrieval;
Forge authoring; and tenant-authorized, ledger-metered WonderBase review pilots.

See [the usage guide](decisions.md) and [editable examples](Examples/Decisions/RConfigs).
Deterministic validation on September 23, 2026: 822 ReviDotNet tests passed, 63 related
WonderBase tests passed (including 8 decision-pilot tests), and WonderBase.sln built.
The September 24 format correction retains the standard RConfig envelope and limits
YAML to `[[_decision]]`. All seven authored packs and the Forge template were migrated.
Verification after that correction: all 840 ReviDotNet tests and 9 WonderBase
decision-pilot tests passed, including loading the host's embedded pack; WonderBase.sln
built with zero warnings/errors. Parser tests cover raw whitespace, literal section
markers, metadata boundaries, and rejection of whole-file YAML.
Forge compiles and its editor service is tested; its page was not visually exercised.
No live API, latency, accuracy, production activation, or calibrated thresholds were
claimed or tested. PDF/OCR extraction remains an explicit host extension, not implicit
binary decoding. Skills/resources use the common selector contract; automated loading
of their contents belongs to the host's existing authorization/execution system.
