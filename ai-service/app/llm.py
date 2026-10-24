"""One place for model calls that must survive rate limits and overload (HTTP 429 / 503)."""

import time
from functools import lru_cache

from google.genai import errors, types

from app.generation import MODEL, get_client


@lru_cache(maxsize=1)
def _client():
    return get_client()


def call_model(contents, config: types.GenerateContentConfig, attempts: int = 4):
    """generate_content with exponential backoff on 429/503; any other error is raised immediately."""
    for attempt in range(1, attempts + 1):
        try:
            return _client().models.generate_content(model=MODEL, contents=contents, config=config)
        except (errors.ServerError, errors.ClientError) as e:
            if e.code not in (429, 503) or attempt == attempts:
                raise
            time.sleep(3 * 2 ** attempt)
