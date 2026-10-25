"""Evaluate the agent layer against the plain pipeline. Calls Gemini: roughly 200 paced requests, 15+ minutes.

  Part 1  regression        the existing hard questions + short clear ones + near-miss negatives, hybrid retrieval, k=4
  Part 2  weak retrieval    dense retrieval at k=3, to see whether the agent's single rewrite-and-retry rescues anything
  Part 3  new capabilities  ambiguous questions, multi-document questions, summary requests

Run unbuffered so progress shows:   python -u -m evals.agent_eval

"Model calls" counts the router, answerer, rewriter and summarizer calls (retrieval is not a model call). Latency is not
reported: the evaluation pauses between calls to respect the free tier, which would distort it.
"""

import hashlib
import json
import sys
import time
from dataclasses import asdict, dataclass
from pathlib import Path

from app.agent import AgentDeps, AgentResult, build_agent, llm_deps
from app.generation import MODEL, Status, answer_question
from app.ingest import ingest_markdown
from app.retrieval import Retriever
from app.store import Hit, VectorStore
from evals.agent_set import AMBIGUOUS, MULTI_DOC, SHORT_CLEAR, SUMMARIES
from evals.hard_set import HARD, NEAR_MISS_HARD, corpus_docs, norm

sys.stdout.reconfigure(encoding="utf-8")

K = 4
PAUSE_S = 1.5


@dataclass
class Outcome:
    status: str
    answer: str
    sources: list[Hit]
    calls: int
    action: str = "retrieve"
    trace: tuple = ()


def paced(fn):
    def wrapper(*args, **kwargs):
        result = fn(*args, **kwargs)
        time.sleep(PAUSE_S)
        return result
    return wrapper


def has_all(hits, phrases) -> bool:
    text = norm(" ".join(h.body for h in hits))
    return all(norm(p) in text for p in phrases)


def ok(question, outcome: Outcome) -> bool:
    return outcome.status == "answered" and has_all(outcome.sources, question.gold_all)


def short(text: str, n: int = 100) -> str:
    text = " ".join(text.split())
    return text if len(text) <= n else text[: n - 1] + "…"


# --------------------------------------------------------------------------- resumable results
# Every outcome is appended to a file as soon as it exists, and a rerun skips what is already there, so a run that is
# killed (a closed session, a timeout) loses nothing. Entries carry a fingerprint of the code, prompts, corpus and model,
# so a change invalidates them instead of silently serving stale results. `--fresh` starts over.
ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / "data" / "agent_eval_cache.jsonl"
FINGERPRINT_FILES = ["app/agent.py", "app/generation.py", "app/llm.py", "app/retrieval.py", "app/hybrid.py", "app/chunking.py",
                     "evals/agent_set.py", "evals/hard_set.py"]


def fingerprint(docs: dict[str, str]) -> str:
    h = hashlib.sha256(MODEL.encode())
    for rel in FINGERPRINT_FILES:
        h.update((ROOT / rel).read_bytes())
    for name in sorted(docs):
        h.update(name.encode() + docs[name].encode())
    return h.hexdigest()[:12]


def load_cache(fp: str) -> dict[str, dict]:
    if "--fresh" in sys.argv and CACHE.exists():
        CACHE.unlink()
    entries: dict[str, dict] = {}
    if CACHE.exists():
        for line in CACHE.read_text(encoding="utf-8").splitlines():
            entry = json.loads(line)
            if entry["fp"] == fp:
                entries[entry["key"]] = entry
    return entries


def outcome_from(entry: dict) -> Outcome:
    return Outcome(entry["status"], entry["answer"], [Hit(**h) for h in entry["sources"]], entry["calls"], entry["action"], tuple(entry["trace"]))


def main() -> None:
    docs = corpus_docs()
    for q in HARD + SHORT_CLEAR + MULTI_DOC:
        missing = [p for p in q.gold_all if not any(norm(p) in norm(t) for t in docs.values())]
        if missing:
            sys.exit(f"gold phrase(s) {missing} not in the corpus for: {q.text}")
    for s in SUMMARIES:
        if s.document not in docs:
            sys.exit(f"unknown document {s.document}")

    store = VectorStore(collection="agent_eval")
    store.clear()
    for name, text in docs.items():
        ingest_markdown(store, name, text)
    retriever = Retriever(store)
    print(f"corpus: {len(docs)} documents, {store.count()} chunks\n")

    answer = paced(answer_question)

    def make_deps(retrieve) -> AgentDeps:
        base = llm_deps(retrieve, store, answer)
        return AgentDeps(paced(base.route), paced(base.rewrite), base.retrieve, base.answer, paced(base.summarize), base.chunks_of, base.documents)

    hybrid = lambda q, n: retriever.hybrid(q, n, 20)
    dense = lambda q, n: retriever.dense(q, n)
    agent_hybrid = build_agent(make_deps(hybrid), k=K)

    fp = fingerprint(docs)
    cache = load_cache(fp)
    print(f"resuming with {len(cache)} cached outcome(s) (fingerprint {fp})" if cache else f"no cached outcomes (fingerprint {fp})")
    print()

    def cached(key: str, compute) -> Outcome:
        if key in cache:
            return outcome_from(cache[key])
        outcome = compute()
        entry = {"fp": fp, "key": key, **{k: v for k, v in asdict(outcome).items()}, "trace": list(outcome.trace)}
        CACHE.parent.mkdir(parents=True, exist_ok=True)
        with CACHE.open("a", encoding="utf-8") as f:
            print(json.dumps(entry), file=f)  # one JSON object per line
        cache[key] = entry
        return outcome

    def pipeline(question: str, retrieve, k: int) -> Outcome:
        def compute() -> Outcome:
            hits = retrieve(question, k)
            result = answer(question, hits)
            return Outcome(result.status.value, result.answer, result.sources, 1 if hits else 0)
        return cached(f"pipeline|{'hybrid' if retrieve is hybrid else 'dense'}|k{k}|{question}", compute)

    def agent(run, question: str) -> Outcome:
        def compute() -> Outcome:
            r: AgentResult = run(question)
            return Outcome(r.status, r.answer, r.sources, r.model_calls, r.action, tuple(r.trace))
        return cached(f"agent|{'hybrid' if run is agent_hybrid else 'dense'}|{question}", compute)

    # ------------------------------------------------------------------ Part 1
    print("=== Part 1: regression (hybrid retrieval, k=4) ===")
    clear = HARD + SHORT_CLEAR
    p1 = []
    for q in clear:
        pipe, ag = pipeline(q.text, hybrid, K), agent(agent_hybrid, q.text)
        p1.append((q, pipe, ag))
        print(f"  pipeline {'ok ' if ok(q, pipe) else 'FAIL'} | agent {'ok ' if ok(q, ag) else 'FAIL'} action={ag.action:<9} calls={ag.calls} | {short(q.text, 70)}")
    near = []
    for text in NEAR_MISS_HARD:
        pipe, ag = pipeline(text, hybrid, K), agent(agent_hybrid, text)
        near.append((pipe, ag))
        print(f"  near-miss: pipeline {pipe.status:<9} | agent {ag.status:<9} action={ag.action:<9} calls={ag.calls} | {short(text, 60)}")

    # ------------------------------------------------------------------ Part 2
    print("\n=== Part 2: weak retrieval (dense, k=3): does the retry rescue anything? ===")
    agent_dense = build_agent(make_deps(dense), k=3)
    p2 = []
    for q in HARD:
        pipe, ag = pipeline(q.text, dense, 3), agent(agent_dense, q.text)
        retried = any("retrying once" in t for t in ag.trace)
        p2.append((q, pipe, ag, retried))
        print(f"  pipeline {'ok ' if ok(q, pipe) else 'FAIL'} | agent {'ok ' if ok(q, ag) else 'FAIL'} retried={retried!s:<5} calls={ag.calls} | {short(q.text, 70)}")

    # ------------------------------------------------------------------ Part 3
    print("\n=== Part 3a: ambiguous questions (the right behavior is to ask) ===")
    amb = []
    for text in AMBIGUOUS:
        pipe, ag = pipeline(text, hybrid, K), agent(agent_hybrid, text)
        amb.append((pipe, ag))
        print(f"  {text!r}\n     pipeline: {pipe.status:<9} {short(pipe.answer, 70)}\n     agent:    {ag.status:<9} {short(ag.answer, 80)}")

    print("\n=== Part 3b: questions that need facts from two documents ===")
    multi = []
    for q in MULTI_DOC:
        pipe, ag = pipeline(q.text, hybrid, K), agent(agent_hybrid, q.text)
        multi.append((q, pipe, ag))
        print(f"  pipeline {'ok ' if ok(q, pipe) else 'FAIL'} | agent {'ok ' if ok(q, ag) else 'FAIL'} calls={ag.calls} | {short(q.text, 70)}")

    print("\n=== Part 3c: summary requests ===")
    summ = []
    for s in SUMMARIES:
        pipe, ag = pipeline(s.text, hybrid, K), agent(agent_hybrid, s.text)
        covered = lambda o: sum(t in norm(o.answer) for t in s.key_terms)
        routed = ag.action == "summarize" and any(s.document in t for t in ag.trace)
        summ.append((s, pipe, ag, routed, covered(pipe), covered(ag)))
        print(f"  {s.text!r}\n     routed to summarize of {s.document}: {routed} | key terms covered (of {len(s.key_terms)}): pipeline {covered(pipe)}, agent {covered(ag)}")

    # ------------------------------------------------------------------ summary
    def count(items, fn):
        return sum(1 for item in items if fn(item))

    n1 = len(p1)
    print("\n=== Summary ===")
    print(f"Part 1  answered with a gold-bearing citation (of {n1}): pipeline {count(p1, lambda x: ok(x[0], x[1]))}, agent {count(p1, lambda x: ok(x[0], x[2]))}")
    print(f"        model calls per question: pipeline {sum(x[1].calls for x in p1) / n1:.2f}, agent {sum(x[2].calls for x in p1) / n1:.2f}")
    print(f"        agent asked for clarification on a CLEAR question: {count(p1, lambda x: x[2].action == 'clarify')}/{n1}")
    print(f"        near-miss abstained (of {len(near)}): pipeline {count(near, lambda x: x[0].status != 'answered')}, agent {count(near, lambda x: x[1].status != 'answered')}")
    print(f"Part 2  weak retrieval, answered with a gold-bearing citation (of {len(p2)}): pipeline {count(p2, lambda x: ok(x[0], x[1]))}, agent {count(p2, lambda x: ok(x[0], x[2]))}")
    print(f"        agent retried {count(p2, lambda x: x[3])}x; rescued (agent ok, pipeline not): {count(p2, lambda x: ok(x[0], x[2]) and not ok(x[0], x[1]))}; "
          f"hurt (pipeline ok, agent not): {count(p2, lambda x: ok(x[0], x[1]) and not ok(x[0], x[2]))}")
    print(f"Part 3a asked for clarification (of {len(amb)}): pipeline {count(amb, lambda x: x[0].status == 'clarify')}, agent {count(amb, lambda x: x[1].status == 'clarify')}; "
          f"pipeline answered anyway: {count(amb, lambda x: x[0].status == 'answered')}")
    print(f"Part 3b multi-document answered with all gold phrases (of {len(multi)}): pipeline {count(multi, lambda x: ok(x[0], x[1]))}, agent {count(multi, lambda x: ok(x[0], x[2]))}")
    print(f"Part 3c summaries routed correctly (of {len(summ)}): {count(summ, lambda x: x[3])}; covering >= 2 key terms: pipeline {count(summ, lambda x: x[4] >= 2)}, agent {count(summ, lambda x: x[5] >= 2)}")


if __name__ == "__main__":
    main()
