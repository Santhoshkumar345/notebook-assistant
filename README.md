# Notebook Assistant

A document Q&A system in the style of NotebookLM: upload documents, ask questions, get answers
with citations to the exact passages - and abstention when the documents don't contain the answer.

> Status: **early build (Week 8 of a 16-week plan).** Ingestion, retrieval, grounded generation with
> verified citations, and evaluation harnesses exist. The API, the agent layer, the .NET backend and
> the UI do not yet. This README only claims what is built.

## Planned architecture

```
Angular UI
   |
ASP.NET Core API    users, document metadata, chat history, streaming          (planned)
   |
FastAPI AI service  ingest -> chunk -> embed -> Chroma -> retrieve -> Gemini   (in progress)
   |                + LangGraph agent: retrieve / summarize / ask a clarifying question (planned)
Gemini API + local embeddings + Chroma
```

Everything is designed to run on free tiers and a laptop (8 GB RAM). See [docs/architecture.md](docs/architecture.md)
for the design decisions, the evidence behind each, and what that evidence does *not* show.

## What exists today

| Piece | Where | State |
|---|---|---|
| Structure-aware chunking under the embedding model's token limit | `ai-service/app/chunking.py` | built, tested |
| Local embeddings (`all-MiniLM-L6-v2`) | `ai-service/app/embeddings.py` | built |
| Chroma vector store | `ai-service/app/store.py` | built |
| Grounded generation: numbered sources, citations verified in code, abstention | `ai-service/app/generation.py` | built, tested |
| Retrieval evaluation with error analysis (16 questions) | `ai-service/evals/retrieval_eval.py` | built |
| First end-to-end evaluation (25 questions) | `ai-service/evals/generation_eval.py` | built; **saturated** |
| Harder evaluation: 20 questions, near-miss negatives, poisoned documents | `ai-service/evals/hard_eval.py` | built; baseline for the next step |
| Hybrid search (dense + BM25, RRF) and optional cross-encoder reranking | `ai-service/app/hybrid.py`, `rerank.py`, `retrieval.py` | built; hybrid is the default |
| Retrieval strategy comparison on the harder set | `ai-service/evals/retrieval_compare.py` | built |
| FastAPI service | - | next |

## Run

```bash
cd ai-service
python -m venv .venv
.venv\Scripts\pip install -r requirements.txt
.venv\Scripts\python -m pytest
.venv\Scripts\python -m evals.hard_eval --retrieval-only
```

The tests and the `--retrieval-only` evaluations need no API key. The generation evaluations call Gemini
(roughly 25-40 requests, paced for the free tier): copy `.env.example` to `ai-service/.env`, set `GEMINI_API_KEY`,
then run `.venv\Scripts\python -m evals.hard_eval`.

## Background

Built while following a structured AI-engineering study plan; the learning log with the experiments
behind these design choices is in the companion repository `ai-engineer-journey`.
