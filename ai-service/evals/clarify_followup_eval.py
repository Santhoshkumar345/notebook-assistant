"""What should the service search for after a person answers a clarifying question? Retrieval only: no API key, no LLM calls.

The AI service is stateless. When the agent asks "which limit do you mean?" and the person replies "the embedding model's",
that reply reaches the service as a brand-new question. Options compared, per question, on the same retriever as the service
(hybrid, k=4, pool 20), judged by whether every gold phrase is in the top-4 context:

  vague only      the original vague question, reply ignored           (what happens if nothing is done)
  reply only      just the person's reply                              (the stateless behaviour today)
  combined        "<vague question> <reply>"                           (what the .NET API could send)
  clear question  the fully specified question from the hard set       (an upper bound: the retrieval a perfect rewrite would get)

Each of the 10 questions has three replies a person might plausibly type: full (names the topic completely), short (the key
noun phrase) and minimal (a word or two that only make sense given the question before it).

Caveats, in order of importance:
  1. The vague questions and replies were written by the same person who knew the gold answers. They are phrased from what a
     clarifying question would ask, not from the gold phrases, but vocabulary overlap with the corpus is likely and flatters
     every strategy that uses the reply.
  2. 10 questions x 3 reply styles. A one-case difference is noise.
  3. A tiny corpus (about 90 chunks): wrong answers have fewer places to hide than in a real one.
  4. Retrieval only. Whether the generator then answers well is a separate question.

Run from the ai-service directory:  python -m evals.clarify_followup_eval
"""

import sys
from dataclasses import dataclass

from app.ingest import ingest_markdown
from app.retrieval import Retriever
from app.store import VectorStore
from evals.hard_set import HARD, corpus_docs, norm

sys.stdout.reconfigure(encoding="utf-8")

K = 4
POOL = 20


@dataclass(frozen=True)
class Case:
    clear: str  # start of the matching question in HARD (its gold phrases are the answer key)
    vague: str
    full: str
    short: str
    minimal: str


CASES = [
    Case("Which exact generator model", "Which exact one is it pinned to?", "The language model used for generation", "the generator model", "the generator"),
    Case("Why did silencing the tokenizer warning", "Why did silencing it break the experiment?", "The tokenizer warning", "the warning", "tokenizer"),
    Case("Which two mistakes invalidated", "Which two mistakes invalidated the first runs?", "The first chunking experiments", "chunking", "the chunks"),
    Case("What caused two identical temperature-zero", "What caused the outputs to differ?", "Two calls made at temperature zero", "temperature zero", "temperature 0"),
    Case("Why would a fixed similarity cutoff", "Why would that cutoff have been a mistake?", "The fixed similarity cutoff of 0.75", "the 0.75 cutoff", "0.75"),
    Case("Does running the retrieval evaluation", "Does running it need a key?", "The retrieval evaluation", "the retrieval eval", "evaluation"),
    Case("Why is writing 'Unknown'", "Why is that worse than inventing one?", "Writing the word Unknown for a missing name", "writing Unknown", "Unknown"),
    Case("What does the project do when the model claims", "What does the project do in that case?", "When the model claims an answer but cites no valid source", "an answer with no valid citation", "no citation"),
    Case("What did the call that was supposed", "What did that call really change?", "The call meant to change the HNSW search breadth", "the HNSW setting call", "HNSW"),
    Case("How should a question about a relative date", "How should that be handled?", "A question with a relative date such as next Friday", "relative dates", "next Friday"),
]

STYLES = ("full", "short", "minimal")


def gold_for(prefix: str) -> tuple[str, str]:
    matches = [q for q in HARD if q.text.startswith(prefix)]
    assert len(matches) == 1, f"{prefix!r} matches {len(matches)} hard-set questions"
    return matches[0].text, matches[0].gold_all


def main() -> None:
    docs = corpus_docs()
    store = VectorStore()
    store.clear()
    for name, text in docs.items():
        ingest_markdown(store, name, text)
    retriever = Retriever(store)

    def found(query: str, gold: tuple[str, ...]) -> bool:
        text = norm(" ".join(h.body for h in retriever.hybrid(query, K, POOL)))
        return all(norm(phrase) in text for phrase in gold)

    totals = {"vague only": 0, "clear question": 0}
    for style in STYLES:
        totals[f"reply only ({style})"] = 0
        totals[f"combined ({style})"] = 0

    print(f"corpus: {len(docs)} documents -> {store.count()} chunks | {len(CASES)} questions x {len(STYLES)} reply styles | top-{K}\n")
    print(f"{'question (vague form)':<48}{'vague':>6}{'clear':>6}  " + "  ".join(f"{s[:5]}:rep/comb" for s in STYLES))
    for case in CASES:
        clear_text, gold = gold_for(case.clear)
        vague_hit = found(case.vague, gold)
        clear_hit = found(clear_text, gold)
        totals["vague only"] += vague_hit
        totals["clear question"] += clear_hit
        cells = []
        for style in STYLES:
            reply = getattr(case, style)
            reply_hit = found(reply, gold)
            combined_hit = found(f"{case.vague} {reply}", gold)
            totals[f"reply only ({style})"] += reply_hit
            totals[f"combined ({style})"] += combined_hit
            cells.append(f"{'Y' if reply_hit else '-'}/{'Y' if combined_hit else '-'}".center(15))
        print(f"{case.vague[:46]:<48}{'Y' if vague_hit else '-':>6}{'Y' if clear_hit else '-':>6}  " + "  ".join(cells))

    n = len(CASES)
    print(f"\nfound all gold phrases in the top-{K} (out of {n}):")
    for name, count in totals.items():
        print(f"  {name:<24}{count:>3}/{n}")


if __name__ == "__main__":
    main()
