# Week 5 - LLM API Engineering (Gemini free tier)

## Day 1 - API basics: roles, sampling parameters, streaming
- **What:** an LLM API call is an HTTP request/response; the SDK is a convenience wrapper. Roles (system/user/assistant) aren't an API nicety, they're baked into how chat models were trained (Week 4: base vs. instruct vs. chat). `temperature` reshapes the next-token probability distribution before sampling; `top_p` is a different knob that truncates the candidate pool to the smallest set of tokens whose cumulative probability reaches p; `max_output_tokens` is a hard stop on the generation loop, not a hint. Streaming sends tokens as produced - same total time, better perceived latency.
- **Verified live (not assumed):**
  - `temperature=0` was NOT reproducible: two identical calls gave `Sprocket` then `Circuit`. "Deterministic" means greedy over the *computed* probabilities, which can shift between calls (floating-point non-associativity across batched GPU execution, load balancing).
  - Reasoning models (`gemini-3.8-flash`) spend `max_output_tokens` on hidden thinking tokens first (`thoughts_token_count`). With a 15-token limit it burned the budget thinking and returned `None`. Non-reasoning `gemini-flash-lite-latest` returned a clean truncated sentence. Thinking tokens are typically billed too.
  - Free tier hit `429` at 5 requests/minute on the newest model; `503` overloads were transient; `gemini-2.5-flash` returned 404 for a new key (deprecated for new users).
- **Trade-off:** `-latest` aliases survive deprecations but can change behavior silently; pinned versions are stable but eventually retire.
- **How to debug rate limits:** exponential-backoff retry (Week 4's retry decorator, applied to 429) and reduced concurrency. Streaming does NOT help - it changes how one response is delivered, not how many requests you make.

## Day 2 - Function/tool calling
- **What:** the model never executes anything. It returns a structured `function_call`; *our* process runs the function and sends the result back as a `function_response` (role `user`) alongside the full prior history.
- **Why the full history is re-sent:** LLM APIs are stateless - the second request is brand new, so without the history the model has lost the original question and its own call. Consequence: tokens are re-sent and re-billed every turn.
- **Security (the part I got wrong twice before getting it):** a `TOOL_REGISTRY` whitelist controls *which functions* can run, NOT *what arguments* they receive. Argument-level injection into allowed tools remains (`run_sql(query)` can still receive `DROP TABLE`), including indirect injection via retrieved RAG documents. Mitigations: schema/pydantic validation of args, least-privilege credentials, parameterized queries, human confirmation for destructive actions. Treat model output as untrusted input.
- **How to debug "model won't call my tool":** AFC (automatic function calling) is a red herring - it only decides who runs the loop. The real control is `tool_config` mode (AUTO / ANY / NONE). Tested: `NONE` with the tool declared reproduced "I don't have real-time weather data"; `ANY` forced `get_weather('Paris')` for "capital of France". Also check the tool is actually in that request's config and that name/description are clear (the second not tested).

## Day 3 - Structured outputs
- **What:** pass a pydantic schema; the server constrains decoding so only schema-valid tokens can be emitted (masking the vocabulary, like `top_p` but grammar-driven). Guarantees **shape**, never **truth**.
- **Verified live:**
  - Prompt-only "give me JSON" returned markdown-fenced JSON -> `json.loads` failed, and the category drifted outside our enum.
  - Schema-constrained returned a validated `Ticket` object with `category` inside the enum.
  - Required `customer_name: str` with no name in the email -> model wrote `'Unknown'` (my prediction of a fabricated name was wrong). Worse in a quieter way: a sentinel string passes validation and is indistinguishable from real data. Fix: `str | None` so "not stated" is representable.
  - `max_output_tokens=20` -> `finish_reason=MAX_TOKENS`, truncated JSON, `parsed=None` with NO exception.
- **Trade-off / how to debug:** check `parsed is None` and `finish_reason` before use; retry according to the cause (bigger budget only for truncation), cap retries, escalate to manual review. A suddenly skewed field (e.g. urgency=5 everywhere) passes validation - schemas can't detect judgment drift. Check the input data first, then model/alias changes, then prompt changes; catch it with distribution checks and a human-labeled eval set (Week 12).

## Process notes
- Concept-first ordering now applied (see feedback memory): plain-language explanation before each demo.
- Check-in answers submitted unaided; two refinements made it into the notes (statelessness, whitelist != argument safety).

## What I built this week
- `scripts/week5_day1_llm_api_basics.py` - system instruction, temperature, max tokens, streaming, reasoning-model token budget
- `scripts/week5_day2_function_calling.py` - manual tool-calling round trip with registry whitelist and backoff
- `scripts/week5_day3_structured_outputs.py` - schema-constrained extraction, shape vs. truth, truncation failure

**Week 5 complete.**
