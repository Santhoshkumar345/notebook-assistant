# Week 7 - Vector search and chunking (start of the RAG core)

## Day 1 - Vector databases, HNSW, embedding limits
- **What:** a vector DB adds an ANN index, persistence, metadata filtering and updates/serving on top of "store vector + text + metadata, return the nearest". HNSW is a layered graph: sparse top layers with long hops, dense bottom layer with local links; a search enters at the top, descends greedily, and refines. It is approximate, and `ef_search` is the recall-vs-latency dial.
- **Verified live (the first run was misleading, twice):**
  - My exact baseline used a full `argsort`, which inflated HNSW's apparent speedup. With a fair `argpartition` baseline: exact 0.25 ms/query at recall 1.000; HNSW `ef_search=10` 0.22 ms at recall 0.922; `ef_search>=50` recall 1.000 with latency rising 0.28 -> 0.65 ms. Recall saturated at 50 (the knee); higher only cost latency.
  - `collection.modify(ef_search=...)` updated the reported config but NOT the behavior in chromadb 1.5.9 (config said 200, results matched 10). Setting it at creation works. Verify behavior, not config. (Not tested: whether a restart would apply it.)
  - At 20k synthetic vectors exact search was as fast and perfectly accurate. ANN earns its keep at much larger N, or for filtering, persistence, updates and serving.
- **Top-k always returns k results:** a `topic='hours'` filter still returned two irrelevant docs (distance ~0.92). A relevance floor must be calibrated on labeled data and is model-relative: here relevant results had cosine similarity 0.596 and 0.376 while irrelevant ones were ~0.07-0.10, so a guessed 0.75 would have rejected the correct answer. Pair it with abstention (Week 6).
- **Embedding length trap:** `all-MiniLM-L6-v2` truncates at 256 tokens, silently. A 401-token text with the password at the end had cosine -0.063 with the query vs 0.789 for the tail alone, and its embedding was identical (1.0000) to the filler-only text. Chunk size must stay under the limit, counted in that model's own tokens (not characters, and not 512-1024 for this model). A longer-context model is a secondary fix: one vector for a long passage is still a blurry average.

## Day 2 - Chunking, measured on the study notes
- **Why chunk:** the model's token limit, precision (one vector = one idea), the LLM's budget and lost-in-the-middle, and citation granularity. Too small loses context ("orphan" chunks); too large blurs embeddings and truncates. Overlap protects answers split across a boundary.
- **Strategies:** fixed-size, recursive, structure-aware (headings/sections), semantic (embedding-similarity splits, a different thing from structure-aware), plus the cheap trick of prefixing document/section titles.
- **Experiment:** 7 variants, 16 questions with gold phrases, scored only on the part of each chunk the embedding model can see. Chunks over the 256-token limit lost answers: 2500-char chunks had 50% coverage, whole heading sections 81%, 1000-char chunks 88%. Variants that fit (500 chars, structural split <=700 chars, +prefix) kept 100%. Fixed 1000 + overlap, structural split and structural + prefix were within a question or two of each other (hit@3 88-94%) on 16 questions, so I cannot rank them; the prefix moved hit@1 up and hit@3 down, which is noise. Prediction missed: whole heading sections were not among the best once the metric stopped flattering them.
- **Costs:** small chunks lose context; overlap adds storage, embedding work, duplicate top-k slots and duplicate prompt tokens (mitigate with dedup / MMR).
- **Process lessons:** (1) a hack to silence a tokenizer warning (`model_max_length = 10**6`) also disabled truncation inside `encode()`, invalidating the first run; the header showing a 1,000,000-token limit and a crash exposed it. (2) My metric credited answers in a chunk's invisible tail; fixed by scoring only the visible text. Habit: validate the measuring instrument, compare config to behavior, read the headers, and do error analysis on the misses (not done yet; Week 8).

## What I built this week
- `scripts/week7_day1_vector_search.py` - Chroma basics, HNSW vs exact search, metadata filter, embedding truncation trap
- `scripts/week7_day2_chunking.py` - 7 chunking variants evaluated on retrieval over the study notes

**Week 7 complete. Next: Week 8 - end-to-end RAG, reranking, hybrid search, FastAPI, reassessment checkpoint.**
