"""Ingestion: markdown -> chunks -> embeddings -> vector store."""

from pathlib import Path

import numpy as np

from app import embeddings
from app.chunking import chunk_markdown
from app.store import VectorStore


def ingest_markdown(store: VectorStore, source: str, markdown: str) -> int:
    chunks = chunk_markdown(source, markdown, embeddings.count_tokens, embeddings.max_tokens())
    vectors = embeddings.embed([c.text for c in chunks]) if chunks else np.empty((0, 0))
    store.replace_source(source, chunks, vectors)
    return len(chunks)


def ingest_directory(store: VectorStore, directory: Path) -> dict[str, int]:
    """Ingest every .md file in a directory; returns chunks created per file."""
    return {p.name: ingest_markdown(store, p.name, p.read_text(encoding="utf-8")) for p in sorted(directory.glob("*.md"))}
