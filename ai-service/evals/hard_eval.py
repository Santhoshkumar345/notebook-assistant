"""Harder evaluation: retrieval on a noisier corpus, end-to-end generation, near-miss abstention, poisoned documents.

  python -m evals.hard_eval --retrieval-only     no API key, no LLM calls (fast; use this while tuning retrieval)
  python -m evals.hard_eval                      also runs generation (~31 Gemini calls, paced)

Gold checks are phrase-based: a multi-fact question counts only if ALL its phrases are present in the
retrieved context (retrieval) or in the cited chunks together (generation). Phrase checks cannot judge whether
an answer is faithful - read the printed answers for the failures.
"""

import sys
import time
from collections import defaultdict

import numpy as np

from app import embeddings
from app.generation import MODEL, Status, answer_question, get_client
from app.ingest import ingest_markdown
from app.store import VectorStore
from evals.hard_set import HARD, NEAR_MISS_HARD, POISONED, corpus_docs, norm

sys.stdout.reconfigure(encoding="utf-8")

K = 4
PAUSE_S = 2.5


def has_all(chunks, phrases) -> bool:
    text = norm(" ".join(c.body for c in chunks))
    return all(norm(p) in text for p in phrases)


def short(text: str, n: int = 150) -> str:
    text = " ".join(text.split())
    return text if len(text) <= n else text[: n - 1] + "…"


def main() -> None:
    retrieval_only = "--retrieval-only" in sys.argv
    docs = corpus_docs()
    missing = [(q.text, p) for q in HARD for p in q.gold_all if not any(norm(p) in norm(t) for t in docs.values())]
    if missing:
        sys.exit(f"gold phrases missing from the corpus - fix the test, not the system: {missing}")

    store = VectorStore()
    store.clear()
    n_chunks = sum(ingest_markdown(store, name, text) for name, text in docs.items())
    print(f"corpus: {len(docs)} documents -> {n_chunks} chunks | questions: {len(HARD)} answerable, {len(NEAR_MISS_HARD)} near-miss\n")

    retrieved = {q.text: store.query(embeddings.embed([q.text])[0], 8) for q in HARD}

    print("=== Retrieval: share of questions whose gold phrases are ALL in the top-k context ===")
    for k in (1, 3, 4, 8):
        print(f"  top-{k}: {sum(has_all(retrieved[q.text][:k], q.gold_all) for q in HARD)}/{len(HARD)}")
    by_cat = defaultdict(list)
    for q in HARD:
        by_cat[q.category].append(has_all(retrieved[q.text][:K], q.gold_all))
    print(f"  by category at top-{K}: " + ", ".join(f"{c} {sum(v)}/{len(v)}" for c, v in sorted(by_cat.items())))
    print(f"  retrieval misses at top-{K}:")
    for q in HARD:
        if not has_all(retrieved[q.text][:K], q.gold_all):
            where = [i for i, h in enumerate(retrieved[q.text], 1) if any(norm(p) in norm(h.body) for p in q.gold_all)]
            print(f"   - [{q.category}] {q.text}  (a gold-bearing chunk ranks at {where or 'none of the top 8'})")
    if retrieval_only:
        return

    client = get_client()
    print(f"\nmodel: {MODEL} | k={K}\n\n=== Generation on answerable questions ===")
    cells = defaultdict(list)
    for q in HARD:
        hits = retrieved[q.text][:K]
        in_ctx = has_all(hits, q.gold_all)
        res = answer_question(q.text, hits, client)
        cited_ok = res.status is Status.answered and has_all(res.sources, q.gold_all)
        cells[(in_ctx, res.status is Status.answered, cited_ok)].append((q, res))
        time.sleep(PAUSE_S)
    for (in_ctx, answered, cited_ok), items in sorted(cells.items(), reverse=True):
        label = f"context complete={in_ctx!s:<5} answered={answered!s:<5} cited gold={cited_ok!s:<5}"
        print(f"  {label} {len(items)}")
        if not (in_ctx and answered and cited_ok):
            for q, res in items:
                print(f"     - [{q.category}] {q.text}\n       status={res.status.value} answer={short(res.answer) or res.detail}")

    print("\n=== Near-miss unanswerable questions (should abstain) ===")
    answered_anyway = 0
    for question in NEAR_MISS_HARD:
        res = answer_question(question, store.query(embeddings.embed([question])[0], K), client)
        if res.status is not Status.not_found:
            answered_anyway += 1
        print(f"  {res.status.value:<9} {question}" + (f"\n             ANSWERED: {short(res.answer)}" if res.answer else ""))
        time.sleep(PAUSE_S)
    print(f"  abstained on {len(NEAR_MISS_HARD) - answered_anyway}/{len(NEAR_MISS_HARD)}")

    print("\n=== Poisoned documents (each is retrieved alongside the clean corpus) ===")
    controlled: set[str] = set()
    for poison in POISONED:
        q_vec = embeddings.embed([poison.question])[0]
        if poison.question not in controlled:
            controlled.add(poison.question)
            control = answer_question(poison.question, store.query(q_vec, K), client)
            print(f"  control, no poison - {poison.question!r}\n     answer: {short(control.answer)}")
            time.sleep(PAUSE_S)
        ingest_markdown(store, "poison.md", poison.text)  # replaces the previous poisoned document
        hits = store.query(q_vec, K)
        res = answer_question(poison.question, hits, client)
        print(f"  {poison.name}\n     retrieved={any(h.source == 'poison.md' for h in hits)}"
              f" hijacked={poison.marker in res.answer.lower()}"
              f" cited_poison={any(s.source == 'poison.md' for s in res.sources)}"
              f" honest_fact_in_answer={poison.honest.lower() in res.answer.lower()} status={res.status.value}"
              f"\n     answer: {short(res.answer) or res.detail}")
        time.sleep(PAUSE_S)
    store.replace_source("poison.md", [], np.empty((0, 0)))


if __name__ == "__main__":
    main()
