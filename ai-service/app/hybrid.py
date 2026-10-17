"""Keyword (BM25) search and Reciprocal Rank Fusion.

BM25 scores exact term overlap weighted by how rare each term is. It finds identifiers, rare words and numbers that
embeddings blur, and it misses paraphrases - the opposite weakness of vector search, which is why the two are combined.
"""

import re

from rank_bm25 import BM25Okapi

RRF_K = 60  # the conventional constant; larger values flatten the advantage of top ranks


def tokenize(text: str) -> list[str]:
    """Lowercased word tokens; underscores stay inside a token so identifiers like ef_search survive."""
    return re.findall(r"\w+", text.lower())


class BM25Index:
    def __init__(self, texts: list[str]):
        self._bm25 = BM25Okapi([tokenize(t) for t in texts])

    def top(self, query: str, n: int) -> list[int]:
        """Indexes of the n best-matching texts, best first. Texts with no term overlap are never returned."""
        scores = self._bm25.get_scores(tokenize(query))
        ranked = sorted(range(len(scores)), key=lambda i: -scores[i])
        return [i for i in ranked[:n] if scores[i] > 0]


def reciprocal_rank_fusion(rankings: list[list[str]], k: int = RRF_K) -> list[tuple[str, float]]:
    """Fuse several best-first rankings of ids: score(id) = sum over rankings of 1 / (k + rank). Rank-based, so
    the rankings' own score scales (cosine vs BM25) never need to be normalized."""
    scores: dict[str, float] = {}
    for ranking in rankings:
        for rank, item in enumerate(ranking, 1):
            scores[item] = scores.get(item, 0.0) + 1.0 / (k + rank)
    return sorted(scores.items(), key=lambda kv: -kv[1])
