"""An independent check of the router, because the first agent evaluation had a leak.

The router prompt contains the examples 'it', 'that' and 'the limit', and the first ambiguous test questions reused exactly
those words, so a good score there proves little. This check uses:
  * vague questions that do NOT echo the prompt's wording                       -> should be answered with a clarifying question
  * broad or short questions that are still searchable                          -> must NOT trigger a clarifying question
Only the router's choice is judged here. ~25 Gemini calls, paced.

Run from the ai-service directory:  python -u -m evals.router_check
"""

import sys

from app.agent import AgentDeps, build_agent, llm_deps
from app.generation import answer_question
from app.ingest import ingest_markdown
from app.retrieval import Retriever
from app.store import VectorStore
from evals.agent_eval import paced
from evals.hard_set import corpus_docs

sys.stdout.reconfigure(encoding="utf-8")

VAGUE = [
    "Can you tell me more about the second approach?",
    "How does this compare?",
    "And what about the other one?",
    "Why was the number so high?",
    "Was this worth the effort?",
    "What came next?",
]
SEARCHABLE = [
    "Tell me about chunking.",
    "What are the known gaps?",
    "Explain the retrieval strategy.",
    "What did the experiments find about prompt injection?",
    "Describe the agent design.",
    "How does the API handle errors?",
]


def main() -> None:
    docs = corpus_docs()
    store = VectorStore(collection="router_check")
    store.clear()
    for name, text in docs.items():
        ingest_markdown(store, name, text)
    retriever = Retriever(store)
    answer = paced(answer_question)
    base = llm_deps(lambda q, n: retriever.hybrid(q, n, 20), store, answer)
    run = build_agent(AgentDeps(paced(base.route), paced(base.rewrite), base.retrieve, base.answer, paced(base.summarize), base.chunks_of, base.documents), k=4)

    print("=== vague questions that do not echo the router prompt (should ask) ===")
    asked = 0
    for q in VAGUE:
        r = run(q)
        asked += r.action == "clarify"
        print(f"  {r.action:<9} {r.status:<9} {q!r} -> {' '.join(r.answer.split())[:90]}")
    print("\n=== short or broad but searchable questions (must NOT ask) ===")
    over = 0
    for q in SEARCHABLE:
        r = run(q)
        over += r.action == "clarify"
        print(f"  {r.action:<9} {r.status:<9} {q!r} -> {' '.join(r.answer.split())[:90]}")
    print(f"\nasked a clarifying question on vague input: {asked}/{len(VAGUE)} | over-clarified on searchable input: {over}/{len(SEARCHABLE)}")


if __name__ == "__main__":
    main()
