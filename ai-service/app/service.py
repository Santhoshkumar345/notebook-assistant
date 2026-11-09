"""The RAG service core: documents in, grounded answers out. No HTTP in here, so it is testable without a server.

Concurrency: HTTP handlers run in a threadpool, so several requests can be inside this class at once. Readers use
whatever retriever is current. Writers (ingest/delete) are serialized by a lock, build the new keyword index first,
and only then swap it in, so a reader never sees a half-built index.
"""

import logging
import threading
import time
from dataclasses import replace
from typing import Callable, Sequence

from google.genai import errors as genai_errors

from app import tracing
from app.agent import AgentDeps, AgentResult, build_agent, llm_deps
from app.condense import Turn, condense_question
from app.generation import RagResult, answer_question
from app.ingest import ingest_markdown
from app.retrieval import Retriever
from app.store import Hit, VectorStore

logger = logging.getLogger("notebook.service")

Answerer = Callable[[str, list[Hit]], RagResult]
Condenser = Callable[[str, Sequence[Turn]], str]
AgentDepsFactory = Callable[[Callable[[str, int], list[Hit]], VectorStore, Answerer], AgentDeps]
POOL = 20  # candidates each retriever contributes to hybrid fusion


class Service:
    def __init__(
        self,
        store: VectorStore,
        answerer: Answerer = answer_question,
        agent_deps_factory: AgentDepsFactory = llm_deps,
        condenser: Condenser = condense_question,
    ):
        self._store = store
        self._answerer = answerer
        self._condenser = condenser
        self._agent_deps_factory = agent_deps_factory
        self._write_lock = threading.Lock()
        self._retriever = Retriever(store)

    def _refresh(self) -> None:
        self._retriever = Retriever(self._store)  # fully built before it replaces the old one

    def ingest(self, name: str, markdown: str) -> int:
        with tracing.span("rag.ingest") as current, self._write_lock:
            chunks = ingest_markdown(self._store, name, markdown)
            self._refresh()
            tracing.annotate(current, **{"ingest.chunks": chunks})
        return chunks

    def delete(self, name: str) -> int:
        with self._write_lock:
            removed = self._store.delete_source(name)
            self._refresh()
        return removed

    def destroy(self) -> None:
        """Delete everything this tenant has stored. The service is not usable afterwards; the tenants registry forgets it."""
        with self._write_lock:
            self._store.drop()

    def documents(self) -> dict[str, int]:
        return self._store.source_counts()

    def chunk_count(self) -> int:
        return self._store.count()

    def retrieve(self, question: str, k: int) -> list[Hit]:
        return self._retriever.hybrid(question, k, POOL)

    def _standalone(self, question: str, history: Sequence[Turn]) -> str:
        """The question to search for: the one typed, or, for a follow-up in a conversation, that question rewritten to stand on its own.
        Costs a model call only when there is history. A rewrite that fails is not an error: the question as typed is used instead."""
        if not history:
            return question
        with tracing.span("rag.condense", **{"condense.turns": len(history)}) as current:
            try:
                standalone = self._condenser(question, history)
            except genai_errors.APIError as error:
                logger.warning("condense failed (model status %s); using the question as typed", getattr(error, "code", None))  # not the message: it can quote the prompt
                standalone = question
            tracing.annotate(current, **{"condense.rewritten": standalone != question})
        return standalone

    def ask(self, question: str, k: int, history: Sequence[Turn] = ()) -> RagResult:
        return self.ask_with_context(question, k, history)[1]

    def ask_with_context(self, question: str, k: int, history: Sequence[Turn] = ()) -> tuple[list[Hit], RagResult]:
        """The answer together with what the model was shown, which is what an evaluation needs to tell a retrieval failure from a generation one."""
        started = time.perf_counter()
        with tracing.span("rag.ask", **{"rag.k": k}) as current:
            standalone = self._standalone(question, history)
            hits = self.retrieve(standalone, k)
            result = self._answerer(standalone, hits)
            if standalone != question:
                result = replace(result, searched_for=standalone)
            tracing.annotate(current, **{"rag.status": result.status.value, "rag.retrieved": len(hits), "rag.cited": len(result.sources)})
        # Chunk ids and status, not the question text: enough to trace a bad answer back to what the model saw,
        # without writing users' questions into logs.
        logger.info(
            "ask status=%s retrieved=%s cited=%s ms=%.0f",
            result.status.value, [h.id for h in hits], [s.id for s in result.sources], (time.perf_counter() - started) * 1000,
        )
        return hits, result

    def ask_agent(self, question: str, k: int, history: Sequence[Turn] = ()) -> AgentResult:
        """The agent layer: routes the question (retrieve / summarize / clarify), with one bounded retry on not_found."""
        started = time.perf_counter()
        retriever = self._retriever  # one consistent snapshot for the whole run
        deps = self._agent_deps_factory(lambda query, n: retriever.hybrid(query, n, POOL), self._store, self._answerer)
        with tracing.span("rag.ask_agent", **{"rag.k": k}) as current:
            standalone = self._standalone(question, history)
            result = build_agent(deps, k=k)(standalone)
            if standalone != question:
                result = replace(result, searched_for=standalone)
            tracing.annotate(
                current, **{"rag.status": result.status, "rag.action": result.action, "rag.model_calls": result.model_calls, "rag.cited": len(result.sources)}
            )
        logger.info(
            "ask(agent) action=%s status=%s model_calls=%d cited=%s ms=%.0f",
            result.action, result.status, result.model_calls, [s.id for s in result.sources], (time.perf_counter() - started) * 1000,
        )
        return result
