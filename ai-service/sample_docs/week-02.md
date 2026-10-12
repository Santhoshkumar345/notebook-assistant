# Week 2 — async/await, JSON, httpx, pydantic, Pandas/NumPy

## Day 1 — async/await
- **What:** Python coroutines are 100% lazy — calling an `async def` function without `await`ing it runs *zero* lines of the body, not even code before the first `await`. This is a real difference from C#, where calling an `async Task` method executes synchronously up to the first `await`, and only the continuation after that is deferred. Forgetting `await` in Python = "nothing happened" (safer to notice); the equivalent mistake in C# = "silent partial execution + lost continuation" (side effects already ran, more dangerous).
- **Why:** `asyncio.run()` is required because Python has no implicit event loop the way C#'s compiler wraps `async Task Main()`.
- **Trade-off:** async only helps I/O-bound work. CPU-heavy work inside `async def` with no `await` blocks the entire event loop — nothing else runs concurrently, because asyncio is single-threaded cooperative multitasking; a coroutine only yields control at an `await` point.
- **How to debug:** a blocking sync call (e.g. `requests.get()`) inside an `async def` freezes the whole event loop for its duration. In a FastAPI service (Phase 4), this doesn't just slow one request — it stalls every concurrent request the server is handling, since they all share one event loop.

## Day 2 — JSON, httpx, pydantic
- **What:** `GitHubRepo(**raw_dict)` reuses Week 1's `**kwargs` in reverse — expanding a dict into keyword arguments instead of collecting them.
- **Why:** pydantic ignores undeclared fields by default (validates only what you declare). To reject unexpected fields: `model_config = ConfigDict(extra="forbid")` (Pydantic v2 syntax — the old `class Config: extra = "forbid"` still works but is v1-style).
- **Trade-off:** skipping `response.raise_for_status()` means a 404's JSON error body (`{"message": "Not Found"}`) gets parsed as if it were valid data — the bug then surfaces downstream, far from the actual cause, instead of failing fast at the HTTP boundary.
- **How to debug:** `description: str | None = None` silently defaults to `None` if the key is missing — safe but can hide a real problem. `description: str` (no default) raises `ValidationError` immediately if the key is missing — a stricter, safer failure mode that forces explicit handling instead of silently assuming.

## Day 3 — Pandas/NumPy
- **What:** `df.groupby("model")["score"].mean()` — groups rows (`GROUP BY model`), narrows to just the `score` column, then averages per group. Returns a `Series` indexed by `model`, NOT a two-column DataFrame — need `.reset_index(name=...)` to get that shape back. Easy to assume it looks like SQL output when it doesn't.
- **Why:** NumPy is faster than a Python list comprehension doing the same element-wise math because NumPy's operations run in compiled C, not through the Python interpreter once per element.
- **Trade-off:** repeatedly mutating a DataFrame in place (`df["col"] = ...`) across a long script is fine for a short script but gets hard to reason about at scale — safer to write transformations as functions that return a new DataFrame (`df.copy()` + modify + return).
- **Deferred to Week 3:** cosine similarity depth (what it means for two unrelated texts to score high similarity, and whether that points to a bad embedding model vs. a misinterpreted score) — correctly flagged as needing the full embeddings/vector-space treatment rather than a rushed answer now.

## What I built this week
- `scripts/week2_day1_async.py` — async/await, sequential vs. `asyncio.gather` concurrency, forgot-await gotcha
- `scripts/week2_day2_json_httpx_pydantic.py` — JSON module, sync + async httpx calls against the GitHub API, pydantic validation
- `scripts/week2_day3_pandas_numpy.py` — DataFrame filter/groupby/sort, NumPy vectorization, dot product/cosine similarity preview

**Week 2: complete. Python-fluency phase (Weeks 1-2) done.**
