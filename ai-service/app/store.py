"""Thin wrapper over Chroma using cosine space."""

from dataclasses import dataclass

import chromadb
import numpy as np

from app.chunking import Chunk

BATCH = 1000  # Chroma caps the batch size (5461 on the pinned version); stay well below it


@dataclass(frozen=True)
class Hit:
    source: str
    heading: str
    body: str
    score: float  # meaning depends on the stage that produced it: cosine, BM25, fused rank score or cross-encoder logit
    id: str = ""  # stable chunk id "<source>#<n>"; lets different retrievers be fused on identity


class VectorStore:
    def __init__(self, path: str | None = None, collection: str = "notebook"):
        """In-memory by default; pass a directory path for a persistent store."""
        self._client = chromadb.PersistentClient(path=path) if path else chromadb.EphemeralClient()
        self._name = collection
        self._col = self._open()

    def _open(self):
        return self._client.get_or_create_collection(self._name, configuration={"hnsw": {"space": "cosine"}})

    def clear(self) -> None:
        self._client.delete_collection(self._name)
        self._col = self._open()

    def count(self) -> int:
        return self._col.count()

    def replace_source(self, source: str, chunks: list[Chunk], embeddings: np.ndarray) -> None:
        """Re-ingesting a document replaces its old chunks, so edited documents never leave stale ones behind."""
        self._col.delete(where={"source": source})
        for start in range(0, len(chunks), BATCH):
            batch = chunks[start:start + BATCH]
            self._col.add(
                ids=[f"{source}#{start + i}" for i in range(len(batch))],
                embeddings=embeddings[start:start + BATCH].tolist(),
                documents=[c.body for c in batch],
                metadatas=[{"source": c.source, "heading": c.heading} for c in batch],
            )

    def query(self, embedding: np.ndarray, k: int) -> list[Hit]:
        n = self._col.count()
        if n == 0:
            return []
        res = self._col.query(
            query_embeddings=[embedding.tolist()],
            n_results=min(k, n),
            include=["documents", "metadatas", "distances"],
        )
        return [
            Hit(meta["source"], meta["heading"], doc, 1.0 - dist, chunk_id)
            for chunk_id, doc, meta, dist in zip(res["ids"][0], res["documents"][0], res["metadatas"][0], res["distances"][0])
        ]

    def all_hits(self) -> list[Hit]:
        """Every stored chunk (score 0.0), e.g. to build a keyword index over the same chunks."""
        res = self._col.get(include=["documents", "metadatas"])
        return [
            Hit(meta["source"], meta["heading"], doc, 0.0, chunk_id)
            for chunk_id, doc, meta in zip(res["ids"], res["documents"], res["metadatas"])
        ]
