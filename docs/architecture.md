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
| LLM | Gemini through `google-genai` (exact model version to be pinned) | next |

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
