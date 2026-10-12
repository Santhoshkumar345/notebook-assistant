"""End-to-end generation evaluation: retrieve top-k, generate a grounded answer, verify citations.

For every answerable question it separates RETRIEVAL failures from GENERATION failures:
  * gold phrase not in the retrieved context  -> a retrieval problem (nothing the generator could do)
  * gold phrase in the context but not answered, or answered citing the wrong source -> a generation problem
Unanswerable questions should be abstained on; any answer to one is an unsupported answer.

What this does NOT measure: faithfulness (whether an answer is actually supported by the sources it cites).
That needs a judge model or a human - read the printed answers. ~20 Gemini calls; needs GEMINI_API_KEY in ai-service/.env.

Run from the ai-service directory:  python -m evals.generation_eval
"""

import sys
import time

from app import embeddings
from app.generation import MODEL, Status, answer_question, get_client
from app.ingest import ingest_directory
from app.store import VectorStore
from evals.retrieval_eval import ANSWERABLE, NEAR_MISS, SAMPLE_DOCS, UNANSWERABLE, norm

sys.stdout.reconfigure(encoding="utf-8")

K = 4          # chunks given to the generator
PAUSE_S = 2.5  # stay under the free tier's request rate


def short(text: str, n: int = 110) -> str:
    text = " ".join(text.split())
    return text if len(text) <= n else text[: n - 1] + "…"


def main() -> None:
    store = VectorStore()
    store.clear()
    ingest_directory(store, SAMPLE_DOCS)
    client = get_client()
    print(f"model: {MODEL} | k={K} | chunks: {store.count()}\n")

    rows = []
    print("=== Answerable questions ===")
    for question, gold in ANSWERABLE:
        hits = store.query(embeddings.embed([question])[0], K)
        g = norm(gold)
        in_context = any(g in norm(h.body) for h in hits)
        result = answer_question(question, hits, client)
        cited_gold = any(g in norm(s.body) for s in result.sources)
        rows.append((question, in_context, result, cited_gold))
        cites = ", ".join(f"{s.source.replace('.md', '')} > {s.heading.split(' > ')[-1][:28]}" for s in result.sources) or "-"
        print(f"\nQ: {question}\n   context has gold: {'yes' if in_context else 'NO '} | status: {result.status.value:<9} | cites gold: {'yes' if cited_gold else 'no'}"
              f"\n   cited: {cites}\n   answer: {short(result.answer) if result.answer else result.detail or '(none)'}")
        time.sleep(PAUSE_S)

    unans, near = [], []
    for title, questions, bucket in (
        ("far-off, easy to refuse", UNANSWERABLE, unans),
        ("near-miss: sounds answerable from the notes, is not", NEAR_MISS, near),
    ):
        print(f"\n=== Unanswerable questions: {title} (should abstain) ===")
        for question in questions:
            hits = store.query(embeddings.embed([question])[0], K)
            result = answer_question(question, hits, client)
            bucket.append(result)
            print(f"Q: {question}\n   status: {result.status.value}" + (f" | ANSWERED: {short(result.answer, 160)}" if result.answer else ""))
            time.sleep(PAUSE_S)

    n = len(rows)
    in_ctx = [r for r in rows if r[1]]
    answered = [r for r in rows if r[2].status is Status.answered]
    good = [r for r in rows if r[2].status is Status.answered and r[3]]
    gen_fail = [r for r in in_ctx if not (r[2].status is Status.answered and r[3])]
    print("\n=== Summary ===")
    print(f"answerable: gold in retrieved context        {len(in_ctx)}/{n}   (retrieval-side ceiling)")
    print(f"answerable: answered (status=answered)       {len(answered)}/{n}")
    print(f"answerable: answered AND cited a gold chunk  {len(good)}/{n}")
    print(f"generation failures (gold in context, not answered+cited correctly): {len(gen_fail)}")
    for q, _, res, _ in gen_fail:
        print(f"   - {q} -> {res.status.value}")
    print(f"retrieval failures (gold not in context): {n - len(in_ctx)}")
    print(f"unanswerable (far-off): abstained {sum(r.status is Status.not_found for r in unans)}/{len(unans)}")
    print(f"unanswerable (near-miss): abstained {sum(r.status is Status.not_found for r in near)}/{len(near)}")
    print(f"statuses: {sorted({r[2].status.value for r in rows} | {r.status.value for r in unans + near})}")


if __name__ == "__main__":
    main()
