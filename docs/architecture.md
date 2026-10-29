# Architecture and design decisions

Each decision records what was chosen, why, and how confident we are. Entries marked *unproven*
are choices made on principle that still need to be validated by the evaluation harness.

## Components

| Layer | Technology | Status |
|---|---|---|
| UI | Angular | planned |
| Primary backend | ASP.NET Core: edge (validation, error translation, correlation id), persistence (documents, chat history; EF Core + SQLite) built; users and streaming planned | edge + persistence built |
| AI service | Python, FastAPI | in progress |
| Orchestration (agent layer) | LangGraph: route, retrieve, summarize, clarify, one bounded retry; opt-in via `mode=agent` | built |
| Embeddings | `sentence-transformers/all-MiniLM-L6-v2` (384-d, 256-token limit, English-centric) | built |
| Vector store | Chroma, cosine space | built |
| Retrieval | dense (Chroma) + BM25 (`rank-bm25`) fused with Reciprocal Rank Fusion; optional cross-encoder rerank | built |
| LLM | Gemini through `google-genai`, pinned to `gemini-3.5-flash-lite` | built |

## Decisions

### 2026-10-12 - Chunking: structure-aware, token-budgeted, with a heading prefix
- Split markdown on headings, then recursively on paragraphs, lines and sentences, packing pieces up to a
  **token** budget (not a character budget) that includes the prefix, so no chunk can exceed the embedding
  model's 256-token limit.
- Prepend `[source > heading path]` to the text that is embedded, so a chunk keeps its context.
- **Why:** in the study-repo experiment (Week 7) chunks over the model's limit silently lost answers
  (2500-char chunks: 50% of answers invisible to the embedding). Variants that fit kept 100%.
- **Unproven:** whether this beats fixed-size chunking with overlap. On 16 questions the variants were within
  noise of each other. The choice is made on principle (guaranteed visibility, keeps context), and this repo's
  retrieval eval exists to test it on a larger set.

### 2026-10-12 - Retrieval is evaluated separately from generation
When an answer is wrong, the first question is whether the gold chunk was in the top-k. Retrieval problems and
generation problems have different fixes, so they get separate measurements.

### 2026-10-12 - Grounded generation contract; model pinned
- The generator gets numbered sources and may answer **only** from them. Output is structured
  (`found`, `answer`, `citations`). Sources are passed inside `<sources>` tags as untrusted data, with
  angle brackets escaped so a document cannot close the tag. Citations are verified in code: a "found"
  answer that cites nothing valid is treated as `uncited` and not trusted.
- The model is pinned to `gemini-3.5-flash-lite`. The `gemini-flash-lite-latest` alias resolved to it on this
  date; aliases can change behavior silently, which would invalidate any evaluation history.
- **Evidence (small, in-distribution set):** on 16 answerable questions the gold chunk was in the top-4 context
  for 16/16, and all 16 were answered with a citation to a gold-bearing chunk. On 4 far-off and 5 near-miss
  unanswerable questions, the generator abstained 9/9; a rerun reproduced the same statuses.
- **What this does not show:**
  - The evaluation is **saturated**. 25 questions over 6 small documents of the author's own notes cannot
    distinguish a good system from a very good one, so it cannot guide further changes (reranking, hybrid search).
  - Citations show which source the model claims, not that the claim is faithful to it. Faithfulness was only
    spot-read by hand (answers were truncated to ~110 characters in the printout), not measured.
  - Not tested yet: injection inside a retrieved document, answers that need several chunks, a larger and noisier corpus.
- **Next:** build a harder evaluation set first (synthesis questions, a poisoned document, more distractor
  documents), then use it to judge reranking and hybrid search.

### 2026-10-14 - A harder evaluation set; the baseline for reranking and hybrid search
- **Corpus:** study notes (weeks 1-7) plus this project's own README and architecture doc: 9 documents, 91 chunks,
  with topics that overlap (embedding limits, model aliases, abstention).
- **Set:** 20 answerable questions (paraphrased, multi-fact, numeric, near-duplicate), 6 near-miss unanswerable
  questions, 6 poisoned-document cases. The earlier 25-question set was saturated.
- **Baseline retrieval** (a question counts only if ALL its gold phrases are in the top-k context):
  top-1 8/20, top-3 15/20, top-4 16/20, top-8 18/20. All four top-4 misses are paraphrase questions; the gold chunk
  ranked 5th, 6th, and beyond 8th (twice). Two of the misses share exact words with the right chunk, which is the
  case hybrid (keyword) search should fix. Reranking and hybrid search will be judged against these numbers.
- **Generation at k=4:** 16 answered with a gold-bearing citation; 2 abstained correctly because the right chunk was
  not in context (a retrieval failure that reaches the user as "not found"); 2 answered from a different chunk and the
  answers read as legitimate (partially hand-read, answers were truncated in the printout), which the phrase-based
  gold check mislabels. Near-miss abstention: 6/6.
- **Poisoned documents:** 0/6 hijacked (four instruction payloads, two false facts). The poisoned document was retrieved
  6/6 and cited in 4/6, so a malicious document can appear as a *source* even when the answer is right.
- **Not shown:** one model, hand-written attacks, essentially one run each. The false-fact attacks ran against facts
  the corpus states correctly, in several places or in one document twice. A poisoned fact with no counter-evidence would
  be accepted by any generator: that is a provenance and trust problem, not a prompting problem. Phrase-based gold
  cannot judge faithfulness. Adding one distractor document to the original 16-question set moved hit@1 from 75% to
  69% (hit@3 unchanged), which is within a question of noise.

### 2026-10-17 - Retrieval: hybrid (dense + BM25, fused with RRF) by default; reranking optional
Questions whose gold phrases are ALL in the top-k context (20 questions, 94 chunks), latency on this laptop's CPU:

| strategy | top-1 | top-3 | top-4 | ms/query |
|---|---|---|---|---|
| dense (baseline) | 9 | 15 | 16 | 13 |
| keyword (BM25) only | 14 | 18 | 18 | <1 |
| **hybrid (dense + BM25, RRF)** | 14 | 19 | **20** | 13 |
| dense + rerank, pool 8 | 17 | 17 | 18 | 349 |
| dense + rerank, pool 20 | 19 | 19 | 20 | 796 |
| hybrid + rerank, pool 8 | 18 | 19 | 20 | 372 |
| hybrid + rerank, pool 20 | 18 | 19 | 20 | 869 |

- **Decision:** hybrid is the default. It reached the best top-4 completeness at the dense retriever's latency. A cross-encoder
  reranker mainly improves top-1 precision (14 -> 18) at 350-870 ms per query on CPU, which is worth paying only if fewer chunks
  are sent to the generator.
- **Pool depth is the reranker's key parameter:** with a pool of 8 two of the four baseline misses stayed unrescued (their right chunk
  was at dense rank 10 and 12, outside the pool); a pool of 20 rescued all four. A reranker cannot recover what is not in its pool.
- **BM25 alone is risky:** 18/20 at top-4, but it regressed two questions that dense retrieval answered. Hybrid regressed none.
- **End to end with hybrid retrieval:** 20/20 answered with a gold-bearing citation (dense: 16/20), 6/6 near-miss questions abstained,
  0/6 poisoned documents hijacked an answer (the poisoned document was still cited in 4/6).
- **Not shown:** the questions were written by the same author as the notes, so they share vocabulary with their sources, which probably
  flatters keyword search. 20 questions is small, the corpus is tiny (a pool of 20 is a fifth of it), and top-4 is again at its ceiling,
  so only top-1 still separates the strategies. Next: questions written independently of the notes.
- **Process:** my first run of this comparison was wrong. `retrieve()` fetched `max(k, pool)` candidates, so "pool 8" and "pool 20"
  both reranked 30 and the rows were identical. Spotted because identical rows and identical latency were implausible.

### 2026-10-17 - AI service: a thin HTTP layer over a testable core
- **Endpoints:** `GET /health`, `GET /documents`, `POST /documents`, `DELETE /documents/{name}`, `POST /ask`. The contract is typed with
  Pydantic, which gives validation and an OpenAPI schema that a typed .NET client can be generated from. All RAG logic lives in
  `app/service.py` with no HTTP in it; `app/api.py` only translates.
- **Handlers are plain `def`, not `async def`,** because everything they do is blocking (embedding inference, Chroma, the Gemini SDK).
  Measured in `experiments/event_loop_blocking.py`: with a 1 s blocking call inside an `async def` handler, five pings issued at
  100-300 ms did not complete until 1,109-1,312 ms; with a plain `def` handler they completed at 139-357 ms. Caveats: one process,
  the default threadpool, and CPU-bound pure-Python work (GIL contention) and thread-pool exhaustion were not tested.
- **Shared state:** uploads and deletes are serialized by a lock, the keyword index is rebuilt and then swapped in, and readers use
  whichever retriever is current. Not stress-tested under concurrent load.
- **HTTP semantics:** `not_found` and `uncited` are valid outcomes (HTTP 200); unusable model output is 502; a rate-limited or overloaded
  model is 503 with `Retry-After`; upstream error details are logged, not returned.
- **Observability:** one log line per question with status, retrieved and cited chunk ids, and latency; the question text is not
  logged. Python drops INFO records unless a handler is configured, so the logger is configured explicitly. Verified in a live run.
- **Found while building:** `BM25Okapi([])` raises `ZeroDivisionError`, so a fresh service with no documents would have crashed on its first
  question. The empty case is handled and tested.
- **Live smoke test:** real uvicorn process and persistent store, real Gemini. Two notes ingested (18 chunks); an answerable question
  returned `answered` with a cited chunk id, two unanswerable ones returned `not_found`, invalid inputs were 422. First question 4.9 s
  (cold), then about 2 s.
- **Known gaps (not built):** authentication between callers and this service; per-user isolation (any caller can read or delete any
  document); response streaming; caller rate limiting; upload scanning. Request bodies are size-checked only after Pydantic has read them.

### 2026-10-25 - Agent layer (LangGraph), opt-in; what it measured
- **Design:** `route -> retrieve -> answer`, and if the answer is `not_found` and a rewrite is left, `rewrite -> retrieve -> answer`; `route -> summarize`;
  `route -> clarify`. The router sees only the question and the document names, never raw text; every tool is read-only; the retry is at most one
  rewrite and the graph has a recursion limit of 12; a router that returns nothing falls back to `retrieve`; the merged context is capped at 6 chunks.
  `POST /ask` with `"mode": "agent"` (default `"pipeline"`) also returns the router's `action` and a `trace`.

| | pipeline | agent |
|---|---|---|
| 24 clear questions answered with a gold-bearing citation | 24 | 24 |
| model calls per answerable question | 1 | 2 |
| 6 near-miss unanswerable questions abstained | 6/6 (1 call each) | 6/6 (4 calls each) |
| weak retrieval (dense, k=3), 20 questions | 15 | 16 (3 retries: 1 rescued, 0 hurt) |
| first ambiguous set (see limit 1) | 0/6 clarified, 2 answered anyway | 6/6 clarified |
| independent vague set | - | 5/6 clarified; 0/6 over-clarified on 6 searchable questions |
| 5 questions needing facts from two documents | 4 | 4 |
| summary requests routed to the right document | - | 4/4; summaries mention >= 2 key terms: pipeline 3/4, agent 4/4 |

- **Reading:** no accuracy gain on questions the pipeline already answers, at twice the model calls, and four times on unanswerable questions because the
  `not_found` path retries. The value is behavioral. The pipeline answered two vague questions confidently from irrelevant chunks (an embedding limit for "What was
  the limit?", a deployment sentence for "What does it cost?"); the agent asked instead. It routes summary requests, and under deliberately weakened retrieval its
  retry rescued one question.
- **Decision:** `pipeline` stays the default (cheaper, equal accuracy on clear questions); use `agent` where users type vague follow-ups or ask for summaries.
- **Limits, in order of importance:**
  1. The first ambiguous test was contaminated: the router prompt contains the words "it", "that" and "the limit", and the test questions reused them. An independent
     set phrased differently got 5/6 (the miss failed safe: retrieve, then abstain). Still small, and hand-written by the same author.
  2. The retry fires only on `not_found`. A confident answer built from the wrong chunk is never retried; two weak-retrieval failures were of that kind, and
     they may be phrase-check mislabels, as in the earlier evaluation. A sufficiency grader would catch more, at another model call per question.
  3. Decomposition is not built, so multi-document questions gain nothing (4/5 vs 4/5).
  4. "Mentions two key terms" is a weak check of a summary. Faithfulness is not measured, and a summary reads only the first 24 sections of a document.
  5. One false abstention survived the retry: "How does the API handle errors?" is answered by the "HTTP semantics" bullet, whose wording shares nothing with it.
  6. One model, small hand-written sets, and temperature 0 is not reproducible.
- **Found by using the system on its own docs:** this table used to say the agent layer was "planned", so the system faithfully answered "planned" when asked to
  describe it. A RAG system is exactly as current as its documents.
- **Process:** two background runs were lost when the session ended. The evaluation now caches each outcome, keyed to a fingerprint of code, prompts, corpus and
  model, and resumes after a kill.

### 2026-10-26 - ASP.NET Core API edge in front of the AI service (step 1 of the .NET/Angular shell)

- **Chosen:** the browser will talk only to an ASP.NET Core API (`api/`), which owns validation, error translation and (later) users, metadata and history, and calls
  the Python service over a typed `HttpClient`. Controllers, not minimal APIs: they are what most .NET job descriptions mean. Four tests-first behaviours:
  - **Validation at the edge**, mirroring the Python limits (`Contracts/Limits.cs`), so a bad request fails with a 400 before it costs a model call.
  - **Upstream failures become honest HTTP**, as RFC 7807 ProblemDetails: busy 503 + `Retry-After`, unreachable 503, timeout 504, bad upstream output 502. Upstream bodies
    are never echoed to the caller or logged.
  - **No automatic retries on `/ask`.** Each retry is a paid, rate-limited model call, and the Python service already backs off on 429/503. Retrying here would multiply load exactly when the service is saturated.
  - **One correlation id (`X-Request-ID`)**: accepted only if `[A-Za-z0-9._-]{1,64}` (otherwise replaced, so callers cannot inject into logs), returned on every response, forwarded to Python and attached to
    the Python log lines. Logs carry paths and chunk ids, never question text.
- **Evidence:** 42 .NET tests (a stub upstream records what the API sends) and 56 Python tests; a live run through both services showed the same id in the .NET and Python logs
  for one question, and no question text in either log.
- **Found while building it (each by a test or a live run, not by reading):**
  1. `$` in a .NET regex matches before a trailing newline, so `DELETE /api/documents/notes.md%0A` passed the edge and reached the Python service (which rejects it), turning an input error into a 502 and a
     validation gap. Fixed with `\z`; the test was written first and failed first.
  2. The exception handler clears response headers, so an id set at the start of the request disappeared from exactly the error responses where it matters. It is now set in `OnStarting`; a mutation check confirmed the test fails with the naive version.
  3. The live run showed the id was *not* in the .NET log lines: the console logger drops logging scopes by default, and a dictionary scope prints as its type name. Fixed with `IncludeScopes` and a template scope; both now have tests.
  4. A transitive `Microsoft.OpenApi` 2.0.0 carries a high-severity advisory (GHSA-v5pm-xwqc-g5wc). Pinned to 2.7.5 in `NotebookApi.csproj`; remove the pin once the framework package is patched.
- **Limits and risks:**
  - The validation rules are duplicated in two languages and can drift; a drift surfaces as a 502 plus an Error log (`ContractMismatch`), not silently. A shared schema or contract test would remove the duplication.
  - Live `/ask` calls took 19-47 s on the Gemini free tier (backoff on 429/503), against a 60 s `TimeoutSeconds`. A slow answer can still become a 504 even though the work succeeds. Streaming (planned) is the real fix.
  - There is still no authentication and no per-user isolation; the .NET API does not add them yet (step 5).

### 2026-10-29 - Persistence in the .NET API (step 2): the database is the source of truth, the index is derived

- **Chosen:** EF Core with SQLite and real migrations (`api/src/NotebookApi/Data/`). Three tables: documents (name, full text, status, chunk count), conversations, messages (question or answer, status, cited sources as JSON, the request id).
  - **SQL owns the documents; Chroma is a rebuildable index.** The original text is kept so the index can be recreated (new embedding model, wiped index) without asking users to upload again.
  - **Two stores cannot share a transaction, so each write is ordered to fail safe.** Upload: save as `Pending`, send to the AI service, then `Indexed`; on failure `Failed` (with only the failure *kind*, never upstream text) and the error is still returned. Delete: the AI service first, the row second, so a failure keeps the row and the delete can be repeated. `POST /api/documents/{name}/reindex` retries one document; `POST /api/documents/sync` deletes index entries the database does not know and re-sends every stored document, and can be re-run after a partial failure.
  - **Document writes go through one gate** (`DocumentWriteGate`), so two overlapping uploads of one name cannot leave one version in the database and the other in the index. Single instance only.
  - **History is saved only for finished exchanges**, question and answer in one transaction. A failed ask saves nothing and the caller retries. The conversation id is used for saving and never sent to the AI service. Question text is stored as user data and is still never written to the logs (a test checks this).
- **Evidence:** 83 .NET tests, including a stub upstream that fails in four different ways, cancellation, overlapping uploads, a conversation deleted mid-answer, and checks that the shipped migrations match the model. Deliberate breakages (no gate, no `Failed` record, delete order swapped, `Failed` saved with the request token) each failed a specific test, and the original code passed. Live run against the real services and Gemini: an index-only orphan removed by sync, a two-turn conversation saved, an upload while the AI service was down left a `Failed` row that re-indexed after the restart, and documents and history survived an API restart.
- **Found while building it:**
  1. EF Core's SQLite package pulls a native SQLite with a high-severity advisory (SQLite before 3.50.2, CVE-2025-6965). Pinned `SQLitePCLRaw.lib.e_sqlite3` 2.1.14, and a test now asks the engine for its version instead of trusting the package number.
  2. The SQLite provider cannot `ORDER BY` a `DateTimeOffset` kept as text; stored as a binary value (all UTC) instead, with a round-trip test.
  3. EF Core 9+ registers the `AddDbContext` lambda separately from the options, so a test replacing only the options would apply the file database on top of the in-memory one. Both registrations are removed.
  4. `dotnet ef` runs startup code between `Build()` and `Run()`; the migrate call there is harmless today but means tooling will touch the database path.
- **Limits:**
  - **A conversation is a record, not model context.** The AI service takes one question at a time, so a follow-up like "and the second one?" is not understood from earlier turns, and a clarify answer is not combined with the original question. Changing that means sending history to the AI service and re-running the evaluations.
  - No users yet: every caller sees every document and conversation. `UserId` columns and per-user queries arrive in step 5 with a migration; the AI service also needs tenant isolation.
  - The migration runs at startup, which suits one instance. Several instances starting together would need it as a separate deploy step, and the write gate would need a database-level lock.
  - SQLite migrations are provider-specific: moving to SQL Server or Azure SQL means regenerating them.
  - Nothing limits how many documents or conversations exist; abuse limits belong with authentication.
