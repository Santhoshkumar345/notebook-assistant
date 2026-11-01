"""One entry point for retrieval: dense, keyword, hybrid (fused), each optionally reranked."""

from dataclasses import replace

from app import embeddings, tracing
from app.hybrid import BM25Index, reciprocal_rank_fusion
from app.rerank import rerank
from app.store import Hit, VectorStore


def index_text(hit: Hit) -> str:
    """The same prefixed text the embedding saw, so the keyword index also sees heading words."""
    return f"[{hit.source} > {hit.heading}]\n{hit.body}"


class Retriever:
    def __init__(self, store: VectorStore):
        self._store = store
        self._chunks = store.all_hits()
        self._by_id = {h.id: h for h in self._chunks}
        # BM25Okapi cannot be built from an empty corpus (ZeroDivisionError), and a fresh service has no documents.
        self._bm25 = BM25Index([index_text(h) for h in self._chunks]) if self._chunks else None

    def dense(self, query: str, n: int) -> list[Hit]:
        with tracing.span("retrieve.dense", **{"retrieve.requested": n}) as current:
            hits = self._store.query(embeddings.embed([query])[0], n)
            tracing.annotate(current, **{"retrieve.returned": len(hits)})
            return hits

    def keyword(self, query: str, n: int) -> list[Hit]:
        with tracing.span("retrieve.keyword", **{"retrieve.requested": n}) as current:
            hits = [] if self._bm25 is None else [self._chunks[i] for i in self._bm25.top(query, n)]
            tracing.annotate(current, **{"retrieve.returned": len(hits)})
            return hits

    def hybrid(self, query: str, n: int, pool: int = 20) -> list[Hit]:
        with tracing.span("retrieve.hybrid", **{"retrieve.requested": n, "retrieve.pool": pool}) as current:
            fused = reciprocal_rank_fusion([[h.id for h in self.dense(query, pool)], [h.id for h in self.keyword(query, pool)]])
            hits = [replace(self._by_id[cid], score=score) for cid, score in fused[:n]]
            tracing.annotate(current, **{"retrieve.returned": len(hits)})
            return hits

    def retrieve(self, query: str, k: int, *, hybrid: bool = False, rerank_pool: int = 0, pool: int = 20) -> list[Hit]:
        """Top-k chunks.

        With rerank_pool > 0 the first stage fetches exactly that many candidates and the cross-encoder reorders them;
        k is then capped at the pool size, because nothing outside the pool is ever scored.
        """
        n = rerank_pool or k
        hits = self.hybrid(query, n, max(pool, n)) if hybrid else self.dense(query, n)
        return rerank(query, hits, min(k, n)) if rerank_pool else hits
