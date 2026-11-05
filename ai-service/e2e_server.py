"""The AI service with a deterministic stand-in for the language model, for the end-to-end browser tests (see ../e2e).

Everything except the model is real: the service key, the tenants, the embeddings, Chroma, the hybrid retrieval, the HTTP layer. Only the
two places that would call Gemini are replaced, so the browser tests are free, fast and repeatable, and can prove what the service actually
received. How good real answers are is measured by the evaluation harnesses in evals/, not by UI tests.

How the stand-in behaves, chosen by words in the question:
  "unanswerable"  the documents do not contain an answer (status not_found)
  "unsupported"   an answer that cites nothing valid (status uncited)
  "flaky"         the model is rate limited the first time this exact question is asked, and fine afterwards
  "slow"          takes four seconds to answer (so a test can press Stop)
  ends in "vague?" the agent asks a clarifying question instead of searching
  starts with "and " in a conversation: a follow-up, rewritten into the last thing the person asked followed by the follow-up (see fake_condenser)
  anything else   an answer quoting the start of the top retrieved passage, and the question it was asked ("[asked: ...]"), which is how a test
                  sees both which tenant's document was retrieved and what text reached the service after a clarifying question

Run:  AI_SERVICE_KEY=<16+ characters> python e2e_server.py <data directory>     (PORT, default 8010)
"""

import os
import re
import sys
import threading
import time

import chromadb
import uvicorn
from google.genai import errors as genai_errors

from app.agent import Action, AgentDeps, Route
from app.condense import Turn
from app.api import create_app
from app.generation import RagResult, Status
from app.service import Service
from app.store import VectorStore
from app.tenants import Tenants

SLOW_SECONDS = 4

_seen_flaky: set[str] = set()
_seen_lock = threading.Lock()


def _opening(text: str, limit: int = 140) -> str:
    return re.sub(r"\s+", " ", text).strip()[:limit]


def fake_answerer(question: str, hits: list) -> RagResult:
    lowered = question.lower()
    if "flaky" in lowered:
        with _seen_lock:
            first_time = question not in _seen_flaky
            _seen_flaky.add(question)
        if first_time:
            raise genai_errors.ClientError(429, {"error": {"message": "the model is rate limited"}})
    if "slow" in lowered:
        time.sleep(SLOW_SECONDS)
    if "unanswerable" in lowered or not hits:
        return RagResult(Status.not_found, "", [])
    if "unsupported" in lowered:
        return RagResult(Status.uncited, "", [], "found=true without a valid citation")
    top = hits[0]
    return RagResult(Status.answered, f"From {top.source}: {_opening(top.body)} [asked: {question}]", [top])


def fake_condenser(question: str, history: list[Turn]) -> str:
    """A follow-up (it starts with \"and \") becomes the last thing the person asked followed by the follow-up; anything else is left as typed, which is what a
    real rewriter does with a question that already stands on its own. Joined the way the clarification flow joins questions, so a test can read it back."""
    if not question.strip().lower().startswith("and "):
        return question
    last = next((turn.text for turn in reversed(history) if turn.role == "user"), None)
    return f"{last} {question}" if last else question


def fake_route(question: str, documents: list[str]) -> Route:
    lowered = question.strip().lower()
    if lowered.endswith("vague?"):
        return Route(action=Action.clarify, clarifying_question="Which topic do you mean?")
    if lowered.startswith("summarize") and documents:
        return Route(action=Action.summarize, document=documents[0])
    return Route(action=Action.retrieve)


def fake_agent_deps(retrieve, store, answer) -> AgentDeps:
    return AgentDeps(
        route=fake_route,
        rewrite=lambda question: question,
        retrieve=retrieve,
        answer=answer,
        summarize=lambda document, chunks: RagResult(Status.answered, f"A summary of {document}.", chunks[:1]),
        chunks_of=store.chunks_of,
        documents=lambda: list(store.source_counts()),
    )


def build_tenants(data_dir: str) -> Tenants:
    client = chromadb.PersistentClient(path=data_dir)
    return Tenants(lambda tenant: Service(VectorStore(client=client, collection=f"tenant_{tenant}"), answerer=fake_answerer, agent_deps_factory=fake_agent_deps, condenser=fake_condenser))


def main() -> None:
    data_dir = sys.argv[1]
    uvicorn.run(create_app(tenants=build_tenants(data_dir)), host="127.0.0.1", port=int(os.environ.get("PORT", "8010")), log_level="warning")


if __name__ == "__main__":
    main()
