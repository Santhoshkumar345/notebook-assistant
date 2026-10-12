# Week 6 - Prompt Engineering, Hallucination, Prompt Injection

## Day 1 - Prompting techniques (measured, not eyeballed)
- **What:** prompting shapes the context so the most likely continuation is what you want; nothing about the model changes. Few-shot = in-context learning: examples shape output within ONE request, no weight update, gone on the next request (the API is stateless). Chain-of-thought (CoT) gives the model scratch space, since each generated token can only condition on what's already written.
- **Verified live:**
  - Name-format task: vague zero-shot 0/6 (it just echoed the names), few-shot 6/6, explicit-rule zero-shot 6/6. The examples carried information the vague prompt lacked; when you CAN state the rule, an instruction is cheaper than examples.
  - Multi-digit multiplication: direct 7/8 vs CoT 8/8, at ~44x the output tokens (7 vs 310 on average). The one direct miss was off by exactly 2000 (a dropped carry).
- **Trade-offs:** few-shot costs tokens on every request and can bias output (label balance, example order). CoT costs tokens and latency, its written reasoning isn't guaranteed to be the real cause of the answer, and it's redundant on reasoning models (they already think internally).
- **How to debug / evidence:** 7/8 vs 8/8 on 8 items run once is consistent with theory, not proof; `temperature=0` isn't reproducible (Week 5), so repeated trials on a larger set are needed. The "4 positive / 1 negative few-shot examples -> everything labeled positive" idea is a hypothesis: test it with a confusion matrix on a labeled set, then rebalance or reorder the examples.

## Day 2 - Hallucination
- **What/why:** fluent output not grounded in fact or in the provided source. The training objective is plausible next tokens, not truth; knowledge is stored lossily in weights (rare facts, numbers, dates are fragile); the model has a cutoff and no clock; with missing context, a plausible fill-in is the easy continuation.
- **Verified live (several predictions missed):**
  - Grounded Q&A: the naive prompt already abstained on all 3 unanswerable questions, and so did the guarded one. The guard's real value is a parseable `NOT_FOUND` token (exact-match measurable, routable in code), not accuracy.
  - Nonexistent paper: both prompts hedged; the textbook fabricated-citation demo did NOT reproduce on this model.
  - Dates: asked directly for today's date, the model abstained; asked "days until Dec 25?" it answered `266` (truth: 81), silently assuming a "today" of roughly early April. The hallucination was a hidden premise, not a visible wrong fact, which is why it's hard to spot. The abstention didn't generalize to the derived question. With the date supplied it answered 81, but model arithmetic isn't guaranteed.
- **How to debug dates:** collect failing examples and classify first. Relative dates -> inject the current date. Dates from documents -> check retrieval and that chunking didn't split them. Date arithmetic -> do it in code or a tool. Format/timezone ambiguity -> ISO-8601 via structured output and a pydantic `date`. Training-data facts -> ground with retrieval. Add a range sanity validator and monitor.
- **Measuring hallucination:** a labeled set with answerable AND unanswerable items plus subtle cases, repeated runs, exact-match scoring where possible and a judge/human for free text, re-run when the model changes. Two crude traps passing is not evidence of safety.

## Day 3 - Prompt injection
- **What/why:** attacker-controlled text treated as instructions. Instructions and data share one token channel; roles are trained behaviors, not enforced isolation. There is no equivalent of SQL's parameterized queries. Direct (user types it) vs indirect (hidden in retrieved/fetched content, the dangerous kind for RAG and agents). Impact scales with the tools the model can use.
- **Verified live:** 5 payloads x 3 prompt variants. Naive prompt injected 4/5 (I confirmed the "polite note" case was real compliance, not the model mentioning the attack). Delimiters plus a "treat as data" rule held 5/5, including a `</email>` breakout; escaping tags gave no measurable gain here (my prediction missed), though it is still the right design. Caveat: 5 handwritten payloads, one model, one run is not a security guarantee. In the tool scenario the model never emitted the exfiltration call, so the policy never fired; I tested the guard directly instead.
- **Defense layering:** least privilege and code-level gates are strongest (they hold even when the model is fooled) but bound damage rather than eliminate it (misuse within allowed actions remains); delimiters help but aren't reliable; validate outputs and tool arguments in code; add filters, guardrails and red-team evals later (Weeks 12-13). Allow-lists need exact domain matching: `"company.com" in addr` and even `endswith("company.com")` both pass `evilcompany.com`.
- **Incident response order:** contain (disable the tool or enable the allow-list), trace (needs logged chunk IDs and tool calls per request), remove the poisoned source and check for copies, add ingestion-time controls, monitor.

## Process notes
- Predictions that did not hold this week: that the naive RAG prompt would fabricate, that the fake-paper demo would fabricate, that the delimiter breakout would defeat plain delimiters. Writing the prediction down first and then comparing is worth keeping as a habit; the surprises were the most useful part.
- Direction update (2026-10-06): job search targets .NET full-stack and .NET full-stack + AI first, with .NET as the core backend over a Python AI service; DSA moves to C# later.

## What I built this week
- `scripts/week6_day1_prompting_techniques.py` - few-shot vs zero-shot vs explicit rule; direct vs chain-of-thought
- `scripts/week6_day2_hallucination.py` - grounded Q&A, missing-clock dates, nonexistent paper
- `scripts/week6_day3_prompt_injection.py` - 5 payloads x 3 defenses, tool exfiltration scenario, code-level policy check

**Week 6 complete. LLM Engineering Core phase (Weeks 5-6) done.**
