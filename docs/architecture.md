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
