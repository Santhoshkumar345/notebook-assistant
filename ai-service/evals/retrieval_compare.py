"""Compare retrieval strategies on the harder question set. Retrieval only: no API key, no LLM calls.

For each strategy: how many questions have ALL their gold phrases in the top-k context, how many results
had to be read to get there, query latency, and - the point of the exercise - whether each of the questions the
dense baseline missed at k=4 is rescued.

Caveat: the corpus is tiny (about 90 chunks), so a pool of 20-30 candidates is a large share of it. Rerankers
look better here than they would on a large corpus. 20 questions: a one-question difference is noise.

Run from the ai-service directory:  python -m evals.retrieval_compare
"""

import sys
import time

from app import embeddings
from app.ingest import ingest_markdown
from app.rerank import rerank
from app.retrieval import Retriever
from app.store import VectorStore
from evals.hard_set import HARD, corpus_docs, norm

sys.stdout.reconfigure(encoding="utf-8")

K = 4
DEPTH = 30  # how far down a ranking we look to find "the smallest k that has everything"


def has_all(hits, phrases) -> bool:
    text = norm(" ".join(h.body for h in hits))
    return all(norm(p) in text for p in phrases)


def k_needed(hits, phrases):
    """Smallest k whose top-k context contains every gold phrase, or None within the ranking we have."""
    return next((k for k in range(1, len(hits) + 1) if has_all(hits[:k], phrases)), None)


def main() -> None:
    docs = corpus_docs()
    store = VectorStore()
    store.clear()
    for name, text in docs.items():
        ingest_markdown(store, name, text)
    retriever = Retriever(store)
    print(f"corpus: {len(docs)} documents -> {store.count()} chunks | {len(HARD)} questions | top-k = {K}\n")

    def two_stage(query: str, pool: int, hybrid: bool):
        """A full-depth ranking in which ONLY the first `pool` results are reordered by the cross-encoder;
        everything after the pool keeps its first-stage order (the reranker never saw it)."""
        base = retriever.hybrid(query, DEPTH, DEPTH) if hybrid else retriever.dense(query, DEPTH)
        return rerank(query, base[:pool]) + base[pool:]

    strategies = {
        "dense (baseline)": lambda q: retriever.dense(q, DEPTH),
        "keyword (BM25) only": lambda q: retriever.keyword(q, DEPTH),
        "hybrid (dense + BM25, RRF)": lambda q: retriever.hybrid(q, DEPTH, DEPTH),
        "dense + rerank, pool 8": lambda q: two_stage(q, 8, hybrid=False),
        "dense + rerank, pool 20": lambda q: two_stage(q, 20, hybrid=False),
        "hybrid + rerank, pool 8": lambda q: two_stage(q, 8, hybrid=True),
        "hybrid + rerank, pool 20": lambda q: two_stage(q, 20, hybrid=True),
    }

    # Warm the models so latency numbers do not include one-off loading.
    retriever.retrieve(HARD[0].text, K, rerank_pool=8)

    results = {}
    print(f"{'strategy':<30}{'top-1':>7}{'top-3':>7}{'top-4':>7}{'mean k':>8}{'ms/query':>10}")
    for name, run in strategies.items():
        t0 = time.perf_counter()
        rankings = {q.text: run(q.text) for q in HARD}
        ms = (time.perf_counter() - t0) / len(HARD) * 1000
        needed = {q.text: k_needed(rankings[q.text], q.gold_all) for q in HARD}
        results[name] = needed
        found = [v for v in needed.values() if v]
        row = [sum(1 for v in needed.values() if v and v <= k) for k in (1, 3, 4)]
        print(f"{name:<30}{row[0]:>7}{row[1]:>7}{row[2]:>7}{sum(found) / max(len(found), 1):>8.1f}{ms:>10.0f}")

    baseline = results["dense (baseline)"]
    print(f"\n=== The questions the dense baseline missed at k={K}: smallest k that contains everything ===")
    header = "".join(f"{n.replace('dense', 'D').replace('hybrid', 'H').replace('keyword (BM25) only', 'BM25').replace(' + rerank, pool ', '+RR')[:14]:>14}" for n in strategies)
    print(f"{'question':<62}{header}")
    for q in HARD:
        if not baseline[q.text] or baseline[q.text] > K:
            cells = "".join(f"{(str(results[n][q.text]) if results[n][q.text] else 'none'):>16}" for n in strategies)
            print(f"{q.text[:60]:<62}{cells}")

    print(f"\n=== Questions where a strategy did WORSE than dense at k={K} (a regression) ===")
    regressed = False
    for name in strategies:
        for q in HARD:
            before, after = baseline[q.text], results[name][q.text]
            if before and before <= K and not (after and after <= K):
                regressed = True
                print(f"  {name}: {q.text[:90]}  (dense needed k={before}, this needs {after or 'none'})")
    if not regressed:
        print("  none")


if __name__ == "__main__":
    main()
