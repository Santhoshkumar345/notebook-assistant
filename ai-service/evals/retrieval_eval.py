"""Retrieval-only evaluation with error analysis. No LLM and no API key involved.

Answerable questions carry a "gold phrase" that must appear in a retrieved chunk. A hit means the gold
phrase is in the top-k chunks. Unanswerable questions are not covered by the documents at all; they
exist to look at how similarity scores separate answerable from unanswerable queries (relevance floor).

Caveats: 16 answerable and 4 unanswerable questions is a small set. Treat differences of a question or two
as noise, and treat the score-separation numbers as an illustration, not a calibrated threshold.

Run from the ai-service directory:  python -m evals.retrieval_eval
"""

import re
import statistics
import sys
from pathlib import Path

from app import embeddings
from app.ingest import ingest_directory
from app.store import VectorStore

sys.stdout.reconfigure(encoding="utf-8")

SAMPLE_DOCS = Path(__file__).resolve().parent.parent / "sample_docs"

ANSWERABLE = [
    ("How many tokens did the Tamil greeting use compared to English?", "48 tokens"),
    ("Why is attention scaled by the square root of the dimension?", "LARGE-magnitude regime"),
    ("Was temperature zero deterministic across identical API calls?", "Sprocket"),
    ("Why did a reasoning model return nothing with a tiny output limit?", "thoughts_token_count"),
    ("What does a tool whitelist fail to protect against?", "argument-level injection"),
    ("What happened when the tool-calling mode was forced?", "get_weather('Paris')"),
    ("What value did the model put for a customer name that was not in the email?", "'Unknown'"),
    ("What number did the model wrongly give for days until Christmas?", "266"),
    ("How many more tokens does chain-of-thought use than a direct answer?", "44x"),
    ("What went wrong with the vague zero-shot name-format prompt?", "echoed the names"),
    ("Why is a mutable default argument shared between instances?", "evaluated once at definition time"),
    ("Why does groupby plus mean not give a two-column table?", "reset_index"),
    ("What happened to the loss when the learning rate was set to 1.0?", "10^35"),
    ("How similar were the cat and kitten sentences?", "0.6253"),
    ("Why did escaping the closing tag not help in the injection test?", "escaping tags gave no measurable gain"),
    ("What signals that a structured output was cut off?", "MAX_TOKENS"),
]

UNANSWERABLE = [
    "What is the capital of France?",
    "How do I configure a Kubernetes ingress controller?",
    "Who won the 2018 football World Cup?",
    "What does Azure Cosmos DB cost per month?",
]


# Sound answerable from the notes but are not: the notes never give these facts (verified against the corpus).
# Abstaining on far-off questions proves little; these are the informative abstention tests.
NEAR_MISS = [
    "How many parameters does the all-MiniLM-L6-v2 embedding model have?",
    "What recall did HNSW reach with ef_search set to 500?",
    "What name did the model give at temperature 3.0?",
    "What was the p95 latency of the Gemini API calls?",
    "How did the one-shot prompt perform on the name-format task?",
]


def norm(s: str) -> str:
    return re.sub(r"\s+", " ", s.lower())


def main() -> None:
    store = VectorStore()
    store.clear()
    counts = ingest_directory(store, SAMPLE_DOCS)
    total = store.count()
    print(f"ingested {len(counts)} files -> {total} chunks | embedding limit {embeddings.max_tokens()} tokens\n")

    q_vecs = embeddings.embed([q for q, _ in ANSWERABLE])
    ranks, top1_scores, misses = [], [], []
    for (question, gold), vec in zip(ANSWERABLE, q_vecs):
        hits = store.query(vec, k=total)  # the FULL ranking, to see where the right chunk landed
        g = norm(gold)
        rank = next((i for i, h in enumerate(hits, 1) if g in norm(h.body)), None)
        ranks.append(rank)
        top1_scores.append(hits[0].score)
        if rank is None or rank > 3:
            misses.append((question, gold, rank, hits))

    n = len(ANSWERABLE)
    found = [r for r in ranks if r]
    print(f"{'coverage (gold phrase exists in some chunk)':<46}{len(found) / n:>6.0%}")
    for k in (1, 3, 5):
        print(f"{f'hit@{k}':<46}{sum(1 for r in found if r <= k) / n:>6.0%}")
    print(f"{'MRR':<46}{sum(1 / r for r in found) / n:>6.2f}\n")

    print(f"=== Error analysis: {len(misses)} question(s) where the gold chunk was not in the top 3 ===")
    for question, gold, rank, hits in misses:
        print(f"\nQ: {question}\n   gold phrase: {gold!r}")
        if rank is None:
            print("   -> gold phrase is in NO chunk (a chunking/coverage problem, not a ranking problem)")
        else:
            gold_hit = hits[rank - 1]
            print(f"   -> gold chunk ranked #{rank} (score {gold_hit.score:.3f}): {gold_hit.source} > {gold_hit.heading}")
        for i, h in enumerate(hits[:3], 1):
            print(f"   top{i} (score {h.score:.3f}): {h.source} > {h.heading}")

    print("\n=== Do scores separate answerable from unanswerable queries? (top-1 cosine similarity) ===")
    unans_scores = [store.query(v, k=1)[0].score for v in embeddings.embed(UNANSWERABLE)]
    for label, scores in (("answerable", top1_scores), ("unanswerable", unans_scores)):
        print(f"{label:<13} n={len(scores):<3} min {min(scores):.3f}  median {statistics.median(scores):.3f}  max {max(scores):.3f}")
    best = max(
        ((t / 100, sum(s >= t / 100 for s in top1_scores) + sum(s < t / 100 for s in unans_scores)) for t in range(0, 100)),
        key=lambda x: x[1],
    )
    print(f"best single threshold on this tiny set: {best[0]:.2f} (classifies {best[1]} of {n + len(UNANSWERABLE)} correctly)")


if __name__ == "__main__":
    main()
