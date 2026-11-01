# Deploying Notebook Assistant

This is the guide for putting the app on a server, and an honest account of what has and has not been checked. Nothing here has been
deployed to a real host: the machine this was built on has no Docker, no WSL and no cloud account, and a real host needs *your* account (and,
for most, a card on file). What has been done is everything that can be built and proved locally; the last section lists what is left.

## What runs

Two processes, one of them public.

```
browser --https--> [ your host's edge: ends TLS ] --http--> API (ASP.NET Core)  --http, private--> AI service (Python/FastAPI)
                                                              |  serves the web app too               |  holds the documents' search index
                                                              |  SQLite file (source of truth)        |  talks to the Gemini API
                                                              +  keys folder (sign-in keys)           +  index folder (rebuildable)
```

- **API** (`api/`): the only thing the internet should reach. It serves the built Angular app itself (one origin: no CORS, no second
  container for the web), the JSON API under `/api`, and `/healthz`.
- **AI service** (`ai-service/`): must **not** be public. It trusts anyone holding the service key to name any person's data, so keep it on a
  private network (or a loopback address, if both run on one machine) and keep the key in the host's secret store.
- **State worth keeping:** the SQLite database and the Data Protection `keys` folder (next to it by default). The AI service's index is
  *derived* and can be lost: see "The index heals itself" below.

## Choosing a host

I looked at free options. **None of them is both free and always on**, and that matters for this app because it holds a database and an index.
This table is from search results, not from reading each provider's pricing page while signed in, and free tiers change often: check the
provider's page before relying on any row.

| Host | Free allowance (as reported) | What it costs you in practice |
| --- | --- | --- |
| Hugging Face Spaces (Docker, CPU Basic) | 2 vCPU, 16 GB; sleeps after about 48 hours idle | Disk is not persistent. One source said compute Spaces now need a paid plan. Simple to try. |
| Azure App Service F1 | 60 CPU-minutes a day, 1 GB storage | No custom domain or TLS binding on F1; the quota is easy to exhaust with embeddings. Needs a card for sign-up. |
| Google Cloud Run | About 180,000 vCPU-seconds and 2 million requests a month | Needs a billing account. Instances scale to zero and the disk is ephemeral, so the index and SQLite file are lost unless you mount storage. |
| Oracle Cloud Always Free (Ampere A1) | Reported as 2 OCPU / 12 GB after a 2026 change (sources disagree) | A full VM you control, so the most realistic "always on" free option. Needs a card; capacity errors are common. |
| Render (free web service) | Free tier exists | Sleeps after 15 minutes idle; disk not persistent. |
| Fly.io | No free tier for new accounts, as reported | Cheap, not free. |

**What I would do:** for something to show an interviewer, a single small VM (Oracle's free one if you can get it, otherwise any
few-dollar one) running both processes under systemd or Docker Compose, with Caddy or nginx in front for TLS. For a quick look that costs
nothing, any sleeping host works *because* of the reconciler below, but expect a slow first request after idle (the embedding model loads).

## Configuration reference

ASP.NET reads settings from environment variables with `__` for a colon (`AiService__BaseUrl` is `AiService:BaseUrl`). Outside
Development the API **refuses to start** when it is configured in a way that would only fail later, and says why.

### API

| Setting | Default | Purpose |
| --- | --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` on a server | Anything but `Development` turns on Secure cookies, HSTS and the startup checks. |
| `ASPNETCORE_URLS` | | Where to listen, for example `http://0.0.0.0:8080` (behind the proxy). |
| `ASPNETCORE_WEBROOT` | `wwwroot` next to the app | The folder holding `web/dist/web/browser` (the built Angular app). With no build there, only the API answers. |
| `AiService__BaseUrl` | | Where the AI service is. Plain http is allowed only on a private network; the app warns otherwise. |
| `AiService__ApiKey` | required | The shared service key, at least 16 characters. Same value as `AI_SERVICE_KEY` below. |
| `Auth__InviteCode` | none | When set (at least 8 characters), registering needs it. **Set this on any public deployment.** |
| `Auth__OpenRegistration` | `false` | Outside Development the app will not start unless there is an invite code *or* this is `true`: open registration must be a decision. |
| `Database__Path` | `App_Data/notebook.db` | The SQLite file. Put it on a volume that survives restarts. |
| `DataProtection__KeysPath` | `keys` next to the database | Where the sign-in and CSRF keys live. Lose them and everyone is signed out; they are not encrypted at rest, so keep the folder private. |
| `ForwardedHeaders__Mode` | `None` | `None` (reached directly), `TrustedProxies` (list them below) or `TrustAll` (only if nothing but the platform's proxy can reach the app). Behind a TLS-ending proxy it must not be `None`, or cookies are refused and every visitor shares one rate-limit bucket. |
| `ForwardedHeaders__KnownProxies__0` | | A proxy's address, for `TrustedProxies`. |
| `ForwardedHeaders__KnownNetworks__0` | | A proxy network in CIDR form such as `10.0.0.0/8`, for `TrustedProxies`. |
| `Quotas__MaxDocuments` | 50 | Documents per person. |
| `Quotas__MaxTotalDocumentChars` | 2,000,000 | Characters across a person's documents (the text is embedded, so this bounds the index too). |
| `Quotas__MaxConversations` | 200 | Conversations per person. |
| `Quotas__MaxMessagesPerConversation` | 400 | Turns in one conversation (a question and its answer are two). |
| `Reconciliation__Enabled` | `true` | The self-healing index pass. |
| `Reconciliation__IntervalMinutes` | 5 | Time between passes. |
| `Reconciliation__StartupDelaySeconds` | 15 | Wait before the first pass, so the AI service can be up. |
| `Limits__MaxRequestBodyBytes` | 2 MiB | Larger requests get a 413 before being read. The biggest allowed document, with every character escaped, fits. |
| `RateLimits__AuthPerMinute`, `RateLimits__AskPerMinute` | 10, 20 | Sign-in attempts per address, questions per person. |

### AI service

| Setting | Purpose |
| --- | --- |
| `AI_SERVICE_KEY` | Required, at least 16 characters; the service will not start without it. |
| `GEMINI_API_KEY` | Your model key. Keep it in the host's secret store. |
| `AI_DATA_DIR` | Where the index is kept (default `ai-service/data/chroma`). |
| `AI_MAX_BODY_BYTES` | Request body limit (default 2 MiB). A second wall behind the API's own. |

Run it with `uvicorn app.api:app_from_env --factory --host <private address> --port 8000`.

Generate keys without printing them: `scripts/setup.ps1` does it for a local setup. On a server, create the service key once
(for example `openssl rand -hex 32`) and put the same value in both places through the host's secret mechanism, never in a committed file.

## Building

```
cd web && npm ci && npx ng build          # output: web/dist/web/browser
cd api && dotnet publish src/NotebookApi -c Release -o publish
```

Copy `web/dist/web/browser` to the folder `ASPNETCORE_WEBROOT` names, run `dotnet publish/NotebookApi.dll`, and start the AI service. There
is no Dockerfile in the repository: I could not build one here, and an untested Dockerfile is worse than none. Writing one is the first
step if you install Docker.

## The index heals itself

The database is the source of truth and the AI service's index is derived from it. On a host with a disk that does not last (most free
ones), the index vanishes on a restart while every document still says "Indexed", and every question would answer "not found" with
nothing to say why. So every few minutes (`Reconciliation__*`) the API compares each person's index with their stored documents and
re-sends what is missing, a different chunk count, anything that Failed, and anything left Pending by an upload that stopped. It acts as each
person in turn, through the same filtered code as their own requests, so one person's text can never go out under another's tenant. It only
*adds*; removing what the database does not know about stays with the explicit **Re-sync** button. If the AI service is down, the pass stops and
tries again at the next tick.

## Before exposing it

- [ ] An invite code is set (or you have decided registration is open).
- [ ] The AI service is not reachable from the internet. The service key is in a secret store, not in a file in the repository.
- [ ] The database and keys folders are on storage that persists, and are backed up (copying the SQLite file while the API is stopped is enough).
- [ ] The proxy ends TLS, and `ForwardedHeaders__Mode` is `TrustedProxies` with the proxy's address (best) or `TrustAll` (only if nothing else can connect).
- [ ] The host's health check is `GET /healthz` (anonymous; it answers while the process is up and does **not** call the AI service, so a model outage does not restart the API).
- [ ] You have read the limits below.

## What has and has not been verified

**Verified here** (on Windows, against a local build; run `dotnet test`, `pytest` and `npm run test:production` in `e2e/` to repeat it):

- The API tests for everything above: the security headers on every kind of response, the SPA fallback (deep links, missing files, unknown API
  addresses, methods, attempts to climb out of the web root), caching headers, forwarded-header trust (off by default; per-visitor rate limits
  behind a trusted proxy; only listed proxies believed; loopback not trusted unless listed), HSTS only outside Development and only over https,
  keys surviving a restart, startup refusal for unsafe configuration, quotas per person, body-size limits, the health check, and the
  reconciler (including that it never acts as the wrong person, even while another request is being served).
- The **browser tests run against the production build** served by the API, outside Development, behind a proxy that ends TLS: the same
  end-to-end tests as the development run, plus tests that the page carries its headers, that a whole session produces no
  content-security-policy violation, that a reload on a deep link works, that hashed files are cached for good and cookies are Secure.
- Mutation checks: each property above was deliberately broken in the code and a test failed.

**Not verified:**

- **No real deployment.** Nothing has run on a host, a container, Linux, or under a real TLS-terminating proxy (the end-to-end run uses a
  small stand-in proxy that overwrites the forwarded headers as a real one does, and the browser reaches it over plain http on localhost, where
  browsers still accept Secure cookies). HSTS over a real https connection was tested in memory, not in a browser.
- **The host table above** is from search summaries, not from each provider's own pages.
- **Multiple instances.** SQLite, the in-memory write gate and the per-tenant keyword indexes assume **one** API instance and one AI-service
  instance. Do not scale out.
- **Chunked request bodies without a length** are limited by the framework's limit on the API (set to the same value) but this was not
  exercised in a test, because the in-memory test server does not enforce it; the Python service checks only the declared length.
- **Backups, restore, log shipping, alerting, rotating the service key** (it needs a restart of both services) and **upgrading the database
  while running** are not set up.
- **No CI.** A GitHub Actions workflow would run the tests on every push but uses your Actions minutes and is configuration I cannot
  verify without pushing; it is deliberately not added until you ask.
