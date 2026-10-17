"""Cross-encoder reranking: score (query, chunk) pairs jointly, then reorder a candidate pool.

A bi-encoder (our embedding model) encodes query and chunk separately, so chunk vectors can be precomputed. A
cross-encoder reads both together with attention across them: more accurate, but one forward pass per candidate and
nothing precomputable - so it is only run on a small pool. It cannot recover a chunk that is not in the pool.
"""

from dataclasses import replace
from functools import lru_cache

from sentence_transformers import CrossEncoder

from app.store import Hit

# Trained on MS MARCO web-search passages; whether that transfers to technical notes is measured, not assumed.
MODEL_NAME = "cross-encoder/ms-marco-MiniLM-L-6-v2"


@lru_cache(maxsize=1)
def _model() -> CrossEncoder:
    return CrossEncoder(MODEL_NAME)


def rerank(query: str, hits: list[Hit], top_k: int | None = None) -> list[Hit]:
    """Reorder `hits` by cross-encoder relevance (score = raw logit, higher is better)."""
    if not hits:
        return []
    scores = _model().predict([(query, f"{h.heading}\n{h.body}") for h in hits], show_progress_bar=False)
    ranked = sorted(zip(hits, scores), key=lambda pair: -pair[1])
    return [replace(h, score=float(s)) for h, s in ranked[:top_k]]
