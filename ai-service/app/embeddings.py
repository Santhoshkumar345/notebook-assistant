"""Local embeddings. One shared model; vectors are L2-normalized so dot product == cosine similarity."""

from functools import lru_cache

import numpy as np
from sentence_transformers import SentenceTransformer
from transformers.utils import logging as hf_logging

# count_tokens() tokenizes WITHOUT truncation on purpose, which makes the tokenizer warn.
# Silence it through logging. Never change tokenizer.model_max_length to do so: that also
# disables the truncation inside encode().
hf_logging.set_verbosity_error()

MODEL_NAME = "all-MiniLM-L6-v2"


@lru_cache(maxsize=1)
def get_model() -> SentenceTransformer:
    return SentenceTransformer(MODEL_NAME)


def max_tokens() -> int:
    """The model's input limit. Anything longer is silently truncated by encode()."""
    return get_model().max_seq_length


def count_tokens(text: str) -> int:
    """Token count in the model's own tokenizer, including its special tokens."""
    return len(get_model().tokenizer(text)["input_ids"])


def embed(texts: list[str]) -> np.ndarray:
    return get_model().encode(texts, normalize_embeddings=True, batch_size=32, show_progress_bar=False)
