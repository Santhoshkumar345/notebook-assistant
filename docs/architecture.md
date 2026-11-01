# Architecture and design decisions

Each decision records what was chosen, why, and how confident we are. Entries marked *unproven*
are choices made on principle that still need to be validated by the evaluation harness.

## Components

| Layer | Technology | Status |
|---|---|---|
| UI | Angular 21 (standalone, signals, zoneless): shell, documents page and chat page built | built |
| Primary backend | ASP.NET Core: edge (validation, error translation, correlation id), persistence (EF Core + SQLite), accounts (Identity, cookie session, CSRF, lock-out, rate limits) and per-user isolation built; streaming planned | built |
| AI service | Python, FastAPI; every route needs the shared service key, and each tenant has its own collection and keyword index | built |
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

### 2026-10-31 - Angular shell (step 3): a client that cannot silently drift from the API

- **Chosen:**
  - **The browser talks to one origin.** The dev server proxies `/api` to the .NET API, so there is no CORS configuration to get wrong. A production deployment should also serve both from one origin.
  - **Types are generated from the API's OpenAPI document** (`openapi-typescript`, no Java), and the document is committed as `api/openapi/v1.json`. A .NET test fails when the live API differs from it, and a web test fails when the browser's file rules differ from the server's (read from the same document). A changed field breaks the build, not the browser.
  - **Signals in services, no store library; plain CSS, no UI library.** Two pages and one list do not justify more; either can be added when that stops being true.
  - **Every failure becomes one `ApiError`** (kind, status, the server's title, request id, retry hint), built from the API's RFC 7807 bodies. A 5xx *without* the API's request id is read as "the API was never reached" (what the dev proxy sends), which keeps "the AI service is down" and "the API is down" apart.
  - **Writes are one at a time, and the list is re-read after every write, success or not**, because a failed upload still leaves a `Failed` row on the server and the screen must show it. Replacing and deleting ask first. Status is shown as text plus colour.
- **Evidence:** 68 web tests and 85 .NET tests. Five deliberate breakages in the web code (no refresh after a failed write, no one-write guard, no proxy-error detection, a browser limit that differs from the server's, replace without asking) were each caught by a specific test, and one more for the header badge. A live run in a real browser against the real API and AI service (local embeddings; no model calls, since this page never asks questions): upload, replace after confirmation, rejected file name, an upload while the AI service was stopped (error with retry hint and request id, a `Failed` row, the header badge turning to "AI service unavailable"), re-index after restarting it, delete after confirmation. Contrast is at least 5.9:1 in light and 6.5:1 in dark mode, no horizontal page scroll at phone width, and the production bundle is 279 kB (78 kB transferred).
- **Found while building it:**
  1. **The OpenAPI document described the API wrongly.** Every route listed only `200`; deletes said 200 instead of 204/400/404; no error shape was documented; health returned anonymous objects. Types generated from that would have misled the client. The controllers now declare every real response, and the snapshot test keeps them honest.
  2. **A class-level `[Produces("application/json")]`, added to tidy that document, silently changed error responses from `application/problem+json` to `application/json`.** Existing tests caught it; the media type is now declared per response instead.
  3. **ASP.NET's default JSON settings accept `"4"` for a number**, so the generated TypeScript typed every integer as `number | string`. Numbers are now strict (a string is a 400), and the document status is an enum, so the page's status handling is a three-value union.
  4. **The header badge said "Ready: 0 documents" for up to 30 seconds after an upload.** It now re-checks after every document write; a test fails without that.
  5. `npm audit` reports 16 moderate advisories (glob, minimatch denial of service) inside the Angular CLI's own build tooling; runtime dependencies have none. The suggested fix downgrades the CLI to version 17, so it is not applied. Revisit when the CLI updates.
- **Limits:**
  - The 5xx-without-a-request-id rule is a heuristic: a production gateway that adds its own request id would defeat it.
  - The browser rules, the .NET rules and the Python rules are three copies of one set of limits; the first is now tested against the second, the second against the third only by a contract mismatch showing up as a 502.
  - No chat page yet, no end-to-end browser tests, one file at a time (no multi-file upload), no authentication.
  - Cold start: the first upload after the AI service starts took 7 s while the embedding model loaded; the page shows "Uploading..." but gives no progress.

### 2026-10-31 - Chat page (step 4), and what to search for when a person answers a clarifying question

- **The decision.** The AI service is stateless: when the agent asks "which limit do you mean?" and the person replies, the reply arrives as a brand-new question. Options: (A) send the reply alone; (B) the API sends the original question together with the reply; (C) send the whole history and have a model rewrite it into one standalone question (an extra model call per follow-up, and a contract change). **Chosen: B, made explicit.** The request carries `answersClarification: true`. The API checks that the conversation really is waiting on a clarifying question (409 if not), joins the question that started the exchange, any earlier answers in the same exchange, and the new answer with spaces (400 if that exceeds 1,000 characters), and sends that. The history stores what the person typed, never the combined text. Nothing is inferred from conversation state, so a new, unrelated question is never silently merged with an old one: the page offers "Ask something else instead".
- **Evidence for B** (`ai-service/evals/clarify_followup_eval.py`, retrieval only, same retriever as the service, top 4): 10 questions, each with a vague form and three plausible replies (full, short, minimal). Ignoring the reply found the gold passage for 2/10. Reply alone: 8, 7 and 6 of 10 (21/30). Question plus reply: 10, 9 and 7 of 10 (26/30). The original, fully specified question: 10/10. Combining lost one case that reply-alone won (a one-word reply). **Caveats:** I wrote the vague questions and replies knowing the answers, and vocabulary overlap with the corpus flatters every strategy that uses the reply; 10 questions, so a one-case difference is noise; a 139-chunk corpus; retrieval only, so whether the generator answers well from a combined string that is not a grammatical question is **not measured**. A labelled form ("Clarification: ...") was not tried; its label words could themselves match documents.
- **What the page does for each outcome:** answered shows the answer and a numbered list of sources (document, heading, the passage behind a disclosure); not found and unsupported answers get an explanation and no text (the AI service sends none for either); a clarifying question is an assistant turn, and the composer switches to "answering" with an opt-out. The status is a typed union in the API contract (`answered | not_found | uncited | clarify`, wire names unchanged), and a `Record` over that union means a new status stops the build until the page says what to show. Text is rendered as text, never markup. A failed or stopped question is not part of the conversation (the API saves only finished exchanges): a failure shows the error, the request id and the unanswered question with Retry, and Stop puts the text back in the box.
- **Evidence:** 100 .NET tests (14 new, for the clarify logic and status wire names), 150 web tests. Thirteen deliberate breakages (five in the clarification logic, eight in the page and store: never marking an answer as an answer, Stop not restoring the text, no guard against a slow answer for a conversation already left, no retry, Enter during IME composition sending, an unsupported answer showing its text, no address for the first answer, Stop not re-reading what may have been saved) were each caught by a specific test. Live, in a real browser against the real services and Gemini: a standard question answered with three sources and the address moving to `/chat/<id>`; a vague question in agent mode asked back "What limit are you referring to?", and the reply "the embedding model's token limit" was answered with the right passage (a self-contained reply, so this shows the mechanism, not the benefit); a not-found card; Stop (text restored, nothing saved); an AI-service outage (error with retry hint and request id, header badge turning to "AI service unavailable") followed by Retry. Logs hold only statuses, chunk ids and timings. Contrast is at least 5.9:1 in light and 6.5:1 in dark mode, no horizontal scroll at phone width, production bundle 290 kB (chat page a separate 20 kB chunk).
- **Found while building it:**
  1. The header badge kept saying "Ready" after a question failed because the AI service had gone away; it now re-checks when a question finishes, failed or stopped.
  2. The mode radio buttons were 13 px targets; the whole label is now the target.
  3. Secondary text on the warning-coloured cards was faded to 4.86:1 in light mode; the fade is gone and size carries the hierarchy.
  4. **Stop abandons the wait but not the work.** The browser cancels, the API cancels its call, nothing is saved, but the AI service has no cancellation, so the model call still runs to completion and is still paid for (visible in its log).
- **Limits:**
  - Only an answer to a clarifying question is combined with earlier turns. A follow-up to a finished answer ("and the second one?") is still understood from the new text alone. Option C would address that and needs its own evaluation.
  - Answers appear all at once, as plain text (no Markdown, no streaming); on the free tier that can be a 5 to 45 second wait.
  - The mode (`pipeline` or `agent`) is a plain string in the OpenAPI document, validated by a pattern on the server and narrowed by hand in TypeScript, so a third mode would not break the build.
  - The page does not show the combined text the AI service searched for, so a surprising answer after a clarification cannot be traced from the screen (the request id leads to the logs, which hold no question text).
  - Still no authentication, no end-to-end browser tests, and one file at a time on the documents page.

### 2026-10-31 - Accounts and per-user isolation (step 5)

- **Chosen.**
  - **Local accounts, ASP.NET Core Identity, an HttpOnly + SameSite=Strict cookie session.** It needs no third-party account or app registration, so it works on the free setup; an external sign-in (Google, Entra ID) could be added later without touching the isolation below. A token in `localStorage` was rejected: injected script could read it. The price of a cookie is CSRF, handled with the Angular/ASP.NET antiforgery pairing (an `XSRF-TOKEN` cookie the page reads and echoes in `X-XSRF-TOKEN`, checked against an HttpOnly cookie it cannot read), with SameSite=Strict as an independent second layer.
  - **Secure by default.** A fallback authorization policy requires a signed-in user on every endpoint; only health, register, login and logout opt out. A test walks every route in the OpenAPI document anonymously and fails if one answers anything but 401.
  - **Isolation in the API, three ways.** Every document, conversation and message row carries a `UserId`. An EF global query filter on each owned type (messages included, so a query straight to the messages table is filtered too) means a query that forgot its `Where` still sees only the caller's rows, and with nobody signed in it matches nothing. A save-time check stamps new rows with the signed-in user and refuses a row for another user, a row moved to another user, or any write with nobody signed in. A test fails if an owned type has no query filter.
  - **Isolation in the AI service is physical.** One Chroma collection and one keyword index per tenant (a tenant is the user's id as 32 hex characters), so a query for one tenant cannot return another's passage whatever it says; a metadata filter on a shared collection would have put one forgotten `where` between users. The tenant id becomes part of a collection name, so it is validated with a full match before use.
  - **The AI service trusts the API, and only the API.** Every route needs a shared service key (compared in constant time), checked before the tenant. Both services refuse to start without it (16+ characters). The API takes the tenant from the signed-in user and removes anything the browser sent; a test sends a forged tenant header and a forged key and checks the AI service receives the real ones. `/health` needs the key but no tenant and reports no counts, and the public health answer carries none either: they would span every user.
  - **Abuse limits.** Account lock-out after five failed sign-ins (15 minutes), rate limits on sign-in and registration per address and on questions per user (a question can cost several model calls), a password rule of length only (12 to 128 characters), and an optional invite code for registering, because open registration on a public deployment hands anyone your model quota. A password is hashed even for an unknown email, and a wrong password and an unknown email get the same words.
  - **Sessions are revoked at once.** The session is re-checked against the account on every request, so deleting an account or changing its security stamp signs it out immediately. Signing out, and a 401 from the API, load the sign-in page afresh instead of routing within the app, because the app's stores are singletons holding the previous person's data and a full load is the one way to be sure none of it survives. A `returnUrl` is followed only if it is a plain path inside the app (an unchecked one is an open redirect).
- **Evidence.** 158 .NET tests (58 new: accounts, cookies, lock-out, CSRF, rate limits, the anonymous walk, two-user isolation over HTTP, in what the AI service is told, and in the database's own filters and write checks), 118 Python tests (62 new), 266 web tests. **Deliberate breakages:** 7 in the Python tenancy and key checks (all caught), 19 in the .NET security properties (18 caught; the one that was not showed my own fix to be redundant, below), 11 in the web auth code (all caught). **Live, in a real browser against the three real services and Gemini:** a signed-out visit to a protected page was sent to sign in; Alice registered (the session cookie is invisible to the page's script), uploaded a private document and got an answer from it; after Alice signed out the page's memory was cleared and every API route answered 401; Bob registered, saw none of her documents, uploaded a file with the *same name* (created, not replaced), asked her exact question and got "not found", got 404 reading *and* deleting her conversation, and was refused (400) a request without the CSRF token; a hostile `returnUrl` was ignored; a session ended behind the page's back showed "Your session ended" and cleared the page; five wrong passwords locked an account and the correct password was then refused. **Directly against the AI service:** no key and a wrong key gave 401, no tenant and a malformed tenant 400, and the two users' tenants held different content under the same filename; a delete by Bob removed only his copy.
- **Found while building it** (each by a test or the live run, not by reading):
  1. **A tenant-id test that could not fail.** My "newline" cases were 31 valid characters plus a newline, which any pattern rejects; the case that matters is 32 valid characters plus a newline (what a `$`-anchored pattern lets through, the same family as the .NET `$` bug from step 1). A mutation exposed it.
  2. **A signed-out page could never obtain a CSRF token.** The 401 from `GET /api/auth/me` short-circuited in authorization before the middleware that issues the token. Issuing now sits between authentication and authorization; checking stays after authorization.
  3. **A route guard called `inject()` after an `await`,** where Angular's injection context is gone: every signed-out visitor would have hit an error instead of the sign-in page.
  4. **A hard-coded 409 message** told someone registering a taken email that "the conversation is no longer waiting"; the client now uses the server's own wording, which differs per route.
  5. **I diagnosed the failing revocation tests wrongly.** I decided Identity's security-stamp validator was unregistered and added a registration with a confident comment. The real cause was my frozen test clock (the validator re-checks only when the session is *older* than the interval). A deliberate breakage of the registration was not caught, which proved it redundant; it and the comment were removed, and the real property (sessions re-checked every request) now has its own mutation.
  6. **The real app returned 500 on sign-in-state checks in Production.** Cookies are Secure outside Development and the antiforgery system refuses to work over plain http. That is the fail-closed design working, but every test ran as Development; tests now check that every cookie is Secure under Production over https, and that plain http is refused.
  7. **A CSRF token goes stale when the identity changes.** After a session ended behind the page's back, the sign-in form was refused ("request token invalid") until a reload. Register, sign-in and sign-out now each hand the page a token for the *new* identity; for sign-out the token has to be issued against an anonymous principal, because `SignOutAsync` leaves the old one in place for the rest of the request.
  8. Two of my tests waited on a task with no timeout and hung the run when the code under test was broken; a mutation runner I wrote once left a file mutated after being killed. Waits now have timeouts, and the runner verifies what it restores.
- **Limits and what is deliberately not here:**
  - **No email verification, password reset, password change or account-deletion endpoint** (nothing here can send email). Deleting an account cascades through the database, which is tested, but nothing in the API exposes it.
  - **Registering a taken email says so (409),** which reveals that the address has an account. It is rate limited, not hidden.
  - **One shared service key.** If it leaks, anyone who can reach the AI service can read any tenant. Rotating it means restarting both services. Deployment must put the AI service on a private network and the key in a secret store.
  - **Deployment decisions this leaves open:** TLS (cookies are Secure outside Development), forwarded headers (behind a proxy, the per-address rate limit sees only the proxy until they are configured), and persisting the ASP.NET Data Protection keys (the cookie is encrypted with them, and the default location is the user profile).
  - **Memory and storage grow with users.** Each active tenant keeps a keyword index in memory and is never evicted; a per-user write gate is kept for every user who has ever written; nothing limits how many documents or conversations a user may create beyond the rate limits. Fine for a portfolio demo, not for a service.
  - Rows from before accounts existed were deleted by the migration (there was only development data), and the AI service's old shared collection is not served to any tenant.
  - Sign-in timing equalisation is by hashing a dummy password, which is not measured.

### 2026-11-01 - End-to-end tests, screenshots and a README for strangers (step 6)

- **Chosen.**
  - **Browser tests of the real stack, with only the language model faked.** Playwright starts the Angular app, the .NET API (throwaway database) and the Python service (real embeddings, real retrieval) on ports of their own and drives a browser already on the machine (Edge or Chrome), so nothing is downloaded and a development stack is never touched. The model is a deterministic stand-in (`ai-service/e2e_server.py`) that cites the top retrieved passage and echoes the question it received (`[asked: ...]`), with keywords for the other outcomes (not found, unsupported, rate-limited once, slow, a clarifying question). The echo is the point: a test can see *which person's document was retrieved* and *what text reached the AI service after a clarifying question*. The stand-in has its own tests, so it cannot quietly drift from what its docstring says. Answer *quality* stays with the evaluation harnesses.
  - **Every test makes its own people** (random email and password, kept only in memory; tracing is off because a trace would record the password fields), so tests are independent and run in parallel.
  - **An automated accessibility scan (axe-core, WCAG 2.2 AA plus best practices) of every screen and state, in light, dark and phone width.**
  - **Screenshots are generated, with the real model** (`npm run screenshots`), so the README shows real answers and the pictures can be regenerated when the interface changes.
  - **A setup script** that makes the service key, puts it in both places and never prints it; and a README rewritten to be followed top to bottom by someone who has never seen the project.
  - **Deliberately not done:** a CI workflow (persistent configuration that spends GitHub Actions minutes on every push, and not verifiable without pushing) and deployment.
- **Evidence.** 28 end-to-end tests (auth and guards, documents, chat, two people side by side, failure and retry, clarifying questions, accessibility). 125 Python, 158 .NET and 266 web tests still pass. **Deliberate breakages through the full stack:** every user sharing one tenant in the AI service, the answer to a clarifying question not being marked as one, and the CSRF token not being checked were each caught by a specific end-to-end test. The first also showed the layers are independent: with the AI service's tenant boundary broken, the test that reads another person's conversation *still passed*, because the API's own query filters held.
- **Found while building it.**
  1. **A contrast failure only a scan could find:** the red "Delete" confirmation button used white text on a light red in dark mode. Its text colour is now a token per scheme.
  2. **A skipped heading level:** "Sources" was an `h3` straight under the page's `h1`; it is an `h2`.
  3. **Two layout flaws only looking at the screenshots revealed**, which no DOM or contrast check had caught: the Re-index and Delete buttons touched, and a few messages left a large empty gap above the composer.
  4. **A configuration mistake caught by reading before running:** in real-model mode the screenshot run would have written into your development index, because nothing read the data-directory setting. `app_from_env` now honours `AI_DATA_DIR`.
  5. **A race in my own test:** the answer renders a moment before the address changes to the conversation's id, and a helper read the address at once. It passed in one run and failed in the next; the helper now waits for the address.
  6. **A selector that matched two things, twice.** The pending question is labelled "Your question, waiting for an answer", which is right for a screen reader. Playwright matches names by *substring*, ignoring case, so it overlapped both the box's label ("Your question") and the answer's ("Answer"). The second overlap only failed when the pending bubble and the answer were on screen in the same instant, so a test passed in one run and failed in the next, and a single green run was misleading. Queries are now exact (or by role), and the suite is run twice in a row before it is trusted.
- **Limits.**
  - The tests run against the Angular dev server, not a production build, and in one browser engine at a time. There is no visual-regression testing, and the Stop test waits a fixed 5.5 seconds for the abandoned model call to finish.
  - The stand-in model cannot reveal anything about retrieval quality, prompt handling or real latency; screenshots contain real model output and so are not reproducible byte for byte.
  - The accessibility scan finds what a machine can find. It cannot judge whether a screen makes sense with a screen reader, so a manual pass is still owed.
  - The setup script is PowerShell only.
  - Nothing runs in CI yet.

### 2026-11-01 - Production readiness (step 7): everything a deployment needs that can be built and proved locally

- **Why this and not "deploy".** The build machine has no Docker, WSL or cloud account, and every real host needs the owner's account (usually a card). I did not write a Dockerfile I could not build. So this step is the part of deployment that is code and can be tested: the application itself is made fit to run behind a proxy on a server, and its production build is tested end to end. [deployment.md](deployment.md) is the guide, with the host options and what is not verified.
- **Chosen.**
  - **One origin.** The API serves the built Angular app (`ASPNETCORE_WEBROOT`): hashed files cached for good, the page itself never cached, deep links answered with the app, an unknown `/api` address or a missing file answered with an honest 404.
  - **Behind a proxy, only a configured one is believed.** `ForwardedHeaders:Mode` is `None` (default), `TrustedProxies` (listed addresses/networks; ASP.NET's own trust of loopback is removed so the list is exactly what was configured) or `TrustAll` (documented as safe only when nothing but the platform proxy can connect). Without this, a TLS-ending proxy makes every request look like plain http (cookies refused) and every visitor share one rate-limit bucket.
  - **Sign-in keys persisted** (default: next to the database), so a restart does not sign everyone out.
  - **Browser hardening on every response** (error responses included): a strict CSP (no inline or foreign script; styles allow inline because Angular writes component styles), nosniff, no framing, no referrer, `no-store` on `/api`, HSTS outside Development (over https only). The Angular production build now sets `inlineCritical: false`, because its default writes an inline `onload` handler the policy would block.
  - **Quotas per person** (50 documents, 2,000,000 characters net of a replacement, 200 conversations, 400 turns per conversation), refused with a 403 that says why, **before** anything is saved or the model is called.
  - **Request-size limits** in both services (2 MiB; the largest allowed document with every character JSON-escaped fits, and a test says so).
  - **Fail-fast configuration** outside Development: no invite code and no explicit `OpenRegistration=true`, or an unwritable database/keys folder, stops the app with a message; risky-but-legitimate settings (no forwarding mode, plain http to a non-loopback AI service) are warnings.
  - **`/healthz`**, anonymous, not in the OpenAPI document, does not call the AI service (a model outage must not restart the API).
  - **An index that heals itself.** Research showed that free hosts sleep and/or lose their disk. The database is the source of truth and the index is derived, but after a restart on such a host every document would still say "Indexed" while questions all answered "not found". A background reconciler (every 5 minutes) re-sends what the index lacks, holds with a different chunk count, or that Failed or was left Pending; it acts **as each person in turn** (an `AsyncLocal` ambient user that the current-user service and the tenant header both honour), only ever adds, and stops its pass if the AI service is down.
- **Evidence.** 225 .NET tests (67 new), 130 Python, 266 web. **The 28 existing browser tests plus 10 new ones pass against the production build**, run outside Development behind a stand-in proxy that overwrites `X-Forwarded-*` and ends nothing but the pretence of TLS; run twice. **31 deliberate breakages** (ambient user ignored by the handler or the current-user service, pending/failed/missing/chunk-mismatch rules, pass-stops-when-down, quota off-by-ones and ordering, CSP missing or loosened, `no-store` dropped, SPA fallback answering missing files / unknown `/api` addresses / any method, caching, body limit, HSTS in development, startup checks, keys in memory, forwarding believed in mode `None`, loopback trusted by default, forwarded headers never applied, health check needing sign-in) were each caught by a test. A further one (CSP `script-src 'none'`) failed the browser tests wholesale, as it should.
- **Found while building it.**
  1. **A missing script got a 401, not a 404.** `MapFallback`'s default pattern skips anything with a file extension, which left `/main-X.js` to the fallback authorization policy: a stranger saw "sign in required" for a broken asset link. The fallback now has an explicit catch-all pattern and decides for itself.
  2. **...which then hid every file.** Static files step aside for any request routing has already matched; the catch-all matched everything, so real files returned 404. It needed an explicit `UseRouting()` after the static-files middleware. A test that had passed before the first fix caught it.
  3. **A survivor.** "Only a GET gets the app" passed under a mutation because the antiforgery check refused the POST first. The test now carries a valid token so the request reaches the fallback itself.
  4. **My stand-in proxy did not forward a disconnect,** so the "Stop" end-to-end test saw an exchange saved that nobody waited for. Real proxies cancel the upstream request, and the stand-in now does; but this is also a **deployment caveat**: a proxy that keeps going after the browser leaves will make Stop save an exchange.
  5. **Self-review:** the reconciler listed the index *before* taking the person's write gate, so an upload in flight could look "missing"; it now lists inside the gate.
  6. Three mistakes of mine in tests: assuming anonymous `/api/auth/me` returns 200 (it is a 401 by design, the point is that it is not the 500 for plain http); a stub reset that also cleared the stub's answers; a mutation whose pattern did not match and so never ran (the runner now reports that instead of passing quietly).
- **Limits.**
  - **Never run on a real host, container, Linux or real https.** HSTS was tested in memory, not in a browser; browsers accept Secure cookies on localhost over http, which is what the end-to-end run relies on.
  - **One instance only.** SQLite, the in-memory write gate and the per-tenant keyword indexes assume a single API and a single AI service.
  - **Chunked request bodies with no declared length** were not tested (the in-memory server does not enforce the framework limit); the Python service checks only the declared length.
  - The quota refusal messages are shown by the web app because it displays the server's own title, but no browser test exercises a quota (it would need lower limits than the other tests allow).
  - No backups or restore procedure, no CI, no Dockerfile, no secret rotation without a restart.
