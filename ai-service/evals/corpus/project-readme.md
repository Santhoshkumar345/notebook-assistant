# Notebook Assistant

A document Q&A system in the style of NotebookLM: upload documents, ask questions, get answers
with citations to the exact passages - and abstention when the documents don't contain the answer.

> Status: **early build.** Ingestion, retrieval, grounded generation with verified citations, hybrid retrieval,
> evaluation harnesses, a FastAPI service, an opt-in LangGraph agent layer and the ASP.NET Core API (validation,
> error mapping, document and chat-history persistence, accounts with per-user isolation) and the Angular app with its
> sign-in, documents and chat pages exist. Deployment does not yet. This README only claims what is built.

## Planned architecture

```
Angular UI          built: documents page (upload, status, re-index, delete, sync) and chat page (ask, citations, clarifying questions)
   |
ASP.NET Core API    built: validation, errors, correlation id, documents + chat history in SQLite; users, streaming planned
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
| FastAPI service: `/health`, `/documents`, `/ask`, typed contract, error mapping | `ai-service/app/api.py`, `service.py` | built, tested, smoke-tested live |
| Agent layer (LangGraph): route, retrieve, summarize, clarify, one bounded retry; `"mode": "agent"` on `/ask` | `ai-service/app/agent.py` | built, opt-in, tested offline |
| Agent vs. pipeline evaluation (resumable) and an independent router check | `ai-service/evals/agent_eval.py`, `router_check.py` | built; results and limits in `docs/architecture.md` |
| ASP.NET Core API: validation, typed client, ProblemDetails error mapping, correlation id, SQLite persistence (documents, conversations), index sync, clarification follow-ups, accounts (ASP.NET Core Identity, cookie session, CSRF, lock-out, rate limits) and per-user isolation; 158 tests | `api/` | built; no auth yet |
| Angular app: shell with a live health indicator, documents page, chat page (standard and agent modes, cited sources, clarifying-question flow, stop and retry), sign-in and registration with route guards, typed client generated from the API's OpenAPI document; 266 tests | `web/` | built |
| Retrieval comparison for answering a clarifying question (reply alone vs question plus reply) | `ai-service/evals/clarify_followup_eval.py` | built; results and caveats in `docs/architecture.md` |
| Blocking-in-`async def` measurement | `ai-service/experiments/event_loop_blocking.py` | built |

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

## Secrets (once)

The AI service and the .NET API share a **service key**, and both refuse to start without it: every document the AI service holds would
otherwise be open to anyone who can reach it. Make one and put the same value in both places:

```bash
python -c "import secrets; print(secrets.token_hex(24))"
```

- AI service: add `AI_SERVICE_KEY=<the key>` to `ai-service/.env` (gitignored), next to `GEMINI_API_KEY`.
- API: `cd api/src/NotebookApi`, then `dotnet user-secrets set "AiService:ApiKey" "<the key>"`.
- Optional, and wanted before the API is reachable by strangers: `dotnet user-secrets set "Auth:InviteCode" "<a code>"` makes registering require it. Open registration hands anyone your model quota.

## Run the AI service

```bash
cd ai-service
.venv\Scripts\python -m uvicorn app.api:app_from_env --factory --port 8000
```

Needs `GEMINI_API_KEY` and `AI_SERVICE_KEY` in `ai-service/.env`. Every route needs the service key, and every route that touches documents needs a
tenant (a user's id as 32 hex characters): each tenant gets its own collection and keyword index. The .NET API supplies both; to call the service by hand:

```bash
curl -X POST http://127.0.0.1:8000/documents -H "X-Service-Key: <the key>" -H "X-Tenant-ID: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" -H "Content-Type: application/json" -d "{\"name\": \"notes.md\", \"content\": \"# Notes\n\nChroma is the vector store.\"}"
curl -X POST http://127.0.0.1:8000/ask -H "X-Service-Key: <the key>" -H "X-Tenant-ID: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" -H "Content-Type: application/json" -d "{\"question\": \"Which vector store is used?\"}"
```

## Run the .NET API

Start the AI service first (above), then:

```bash
cd api
dotnet test
dotnet run --project src/NotebookApi --urls http://127.0.0.1:5247
```

The API listens on `http://127.0.0.1:5247` and forwards to `AiService:BaseUrl` (default `http://127.0.0.1:8000`, set in
`api/src/NotebookApi/appsettings.json`), presenting the service key and the signed-in user's id as the tenant. Every route except
`GET /api/health` and the account routes needs a signed-in user. Accounts: `POST /api/auth/register`, `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/auth/me`
(an HttpOnly, SameSite=Strict session cookie; unsafe requests also need the `X-XSRF-TOKEN` header, which the web app sends). Other routes: `GET|POST /api/documents`, `DELETE /api/documents/{name}`, `POST /api/documents/{name}/reindex`, `POST /api/documents/sync`;
`POST /api/ask` (optional `conversationId`, and `answersClarification` when answering a clarifying question); `GET /api/conversations`, `GET|DELETE /api/conversations/{id}`.
Documents and history live in a SQLite file (`Database:Path`, default `api/src/NotebookApi/App_Data/notebook.db`, created and migrated on first start, not committed).
The database is the source of truth; the AI service's index is derived from it, and `POST /api/documents/sync` makes the index match.
Send `X-Request-ID` to trace one request through both services' logs. OpenAPI is served at `/openapi/v1.json` in Development.

## Run the web app

With the AI service and the .NET API running (above):

```bash
cd web
npm install
npm start
```

Open `http://localhost:4200`. The dev server proxies `/api` to the .NET API. See [web/README.md](web/README.md) for tests,
how the API types are generated, and how they are kept in step with the API.

**What protects it:** every route needs the shared service key, and each user's documents live in a collection and keyword index of their own
(see [docs/architecture.md](docs/architecture.md)). **Known gaps:** no streaming; no email verification, password reset or account deletion;
and nothing here is ready to face the internet yet: TLS, forwarded headers, persisted Data Protection keys, a private network between the API and
this service, and the key in a secret store are all deployment work. Keep it on localhost until then.

## Background

Built while following a structured AI-engineering study plan; the learning log with the experiments
behind these design choices is in the companion repository `ai-engineer-journey`.
