"""The RAG service core: documents in, grounded answers out. No HTTP in here, so it is testable without a server.

Concurrency: HTTP handlers run in a threadpool, so several requests can be inside this class at once. Readers use
whatever retriever is current. Writers (ingest/delete) are serialized by a lock, build the new keyword index first,
and only then swap it in, so a reader never sees a half-built index.
"""

import logging
import threading
import time
from typing import Callable

from app.agent import AgentDeps, AgentResult, build_agent, llm_deps
from app.generation import RagResult, answer_question
from app.ingest import ingest_markdown
from app.retrieval import Retriever
from app.store import Hit, VectorStore

logger = logging.getLogger("notebook.service")

Answerer = Callable[[str, list[Hit]], RagResult]
AgentDepsFactory = Callable[[Callable[[str, int], list[Hit]], VectorStore, Answerer], AgentDeps]
POOL = 20  # candidates each retriever contributes to hybrid fusion


class Service:
    def __init__(self, store: VectorStore, answerer: Answerer = answer_question, agent_deps_factory: AgentDepsFactory = llm_deps):
        self._store = store
        self._answerer = answerer
        self._agent_deps_factory = agent_deps_factory
        self._write_lock = threading.Lock()
        self._retriever = Retriever(store)

    def _refresh(self) -> None:
        self._retriever = Retriever(self._store)  # fully built before it replaces the old one

    def ingest(self, name: str, markdown: str) -> int:
        with self._write_lock:
            chunks = ingest_markdown(self._store, name, markdown)
            self._refresh()
        return chunks

    def delete(self, name: str) -> int:
        with self._write_lock:
            removed = self._store.delete_source(name)
            self._refresh()
        return removed

    def documents(self) -> dict[str, int]:
        return self._store.source_counts()

    def chunk_count(self) -> int:
        return self._store.count()

    def ask(self, question: str, k: int) -> RagResult:
        started = time.perf_counter()
        hits = self._retriever.hybrid(question, k, POOL)
        result = self._answerer(question, hits)
        # Chunk ids and status, not the question text: enough to trace a bad answer back to what the model saw,
        # without writing users' questions into logs.
        logger.info(
            "ask status=%s retrieved=%s cited=%s ms=%.0f",
            result.status.value, [h.id for h in hits], [s.id for s in result.sources], (time.perf_counter() - started) * 1000,
        )
        return result

    def ask_agent(self, question: str, k: int) -> AgentResult:
        """The agent layer: routes the question (retrieve / summarize / clarify), with one bounded retry on not_found."""
        started = time.perf_counter()
        retriever = self._retriever  # one consistent snapshot for the whole run
        deps = self._agent_deps_factory(lambda query, n: retriever.hybrid(query, n, POOL), self._store, self._answerer)
        result = build_agent(deps, k=k)(question)
        logger.info(
            "ask(agent) action=%s status=%s model_calls=%d cited=%s ms=%.0f",
            result.action, result.status, result.model_calls, [s.id for s in result.sources], (time.perf_counter() - started) * 1000,
        )
        return result
