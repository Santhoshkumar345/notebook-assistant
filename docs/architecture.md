# Architecture and design decisions

Each decision records what was chosen, why, and how confident we are. Entries marked *unproven*
are choices made on principle that still need to be validated by the evaluation harness.

## Components

| Layer | Technology | Status |
|---|---|---|
| UI | Angular | planned |
| Primary backend | ASP.NET Core (users, metadata, chat history, streaming proxy) | planned |
| AI service | Python, FastAPI | in progress |
| Orchestration (agent layer) | LangGraph | planned (Weeks 9-11) |
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
