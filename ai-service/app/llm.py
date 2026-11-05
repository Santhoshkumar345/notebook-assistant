"""One place for model calls that must survive rate limits and overload (HTTP 429 / 503)."""

import time
from functools import lru_cache

from google.genai import errors, types

from app import tracing
from app.generation import MODEL, finish_name, get_client


@lru_cache(maxsize=1)
def _client():
    return get_client()


def call_model(contents, config: types.GenerateContentConfig, attempts: int = 4, purpose: str = "generate", client=None):
    """generate_content with exponential backoff on 429/503; any other error is raised immediately. `purpose` names the span (llm.route, llm.rewrite, ...)."""
    with tracing.span(f"llm.{purpose}", **{"gen_ai.operation.name": "generate_content", "gen_ai.request.model": MODEL}) as current:
        for attempt in range(1, attempts + 1):
            try:
                response = (client or _client()).models.generate_content(model=MODEL, contents=contents, config=config)
                tracing.annotate(current, **tracing.usage_attributes(response), **{"llm.attempts": attempt, "llm.finish_reason": finish_name(response)})
                return response
            except (errors.ServerError, errors.ClientError) as e:
                if e.code not in (429, 503) or attempt == attempts:
                    tracing.annotate(current, **{"llm.attempts": attempt, "http.status_code": e.code})
                    raise
                tracing.event(current, "retry", **{"http.status_code": e.code, "attempt": attempt})
                time.sleep(3 * 2 ** attempt)
