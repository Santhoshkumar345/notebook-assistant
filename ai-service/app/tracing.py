"""Tracing: what a request did, how long each step took, which model it used and what it cost, so a slow or wrong answer can be explained after the fact.

The rest of the code only calls `span(...)`. That goes through OpenTelemetry's API, which does nothing at all until a provider is configured, so
tracing costs nothing when it is off and the code never knows where spans end up. Where they go is decided once, at startup, from the environment:
  NOTEBOOK_TRACE_FILE          append one JSON line per finished span to this file
  OTEL_EXPORTER_OTLP_ENDPOINT  send spans to an OpenTelemetry collector or any backend that accepts OTLP (Jaeger, Tempo, Langfuse, ...)

What a span may hold: names, counts, ids, token numbers, durations, outcomes. Never a question, an answer, a document, a document's name or a tenant's id:
those are the user's. Every request's root span carries the request id the logs already carry, so a log line and its trace can be matched either way. A string that is longer than MAX_ATTRIBUTE_CHARS is cut, as a safety net against a slip, not as a
substitute for leaving text out.
"""

import json
import os
import threading
from contextlib import contextmanager
from pathlib import Path
from typing import Iterator, Sequence

from opentelemetry import trace
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import ReadableSpan, TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor, SimpleSpanProcessor, SpanExporter, SpanExportResult
from opentelemetry.trace import Span, Status, StatusCode

MAX_ATTRIBUTE_CHARS = 200
SERVICE_NAME = "notebook-ai-service"

_provider: TracerProvider | None = None


def use_provider(provider: TracerProvider | None) -> None:
    """Send spans to this provider (None turns tracing off). Kept here instead of in OpenTelemetry's global so tests, and a second app in the same process, cannot fight over it."""
    global _provider
    _provider = provider


def _tracer() -> trace.Tracer:
    return (_provider or trace.NoOpTracerProvider()).get_tracer("notebook")


def clip(value):
    return value if not isinstance(value, str) or len(value) <= MAX_ATTRIBUTE_CHARS else value[: MAX_ATTRIBUTE_CHARS - 1] + "…"


class SafeSpan:
    """What `span()` hands out: the real span, except that a long string is cut on the way in, whichever way the attribute is set."""

    def __init__(self, inner: Span):
        self._inner = inner

    def set_attribute(self, key: str, value) -> None:
        self._inner.set_attribute(key, clip(value))

    def add_event(self, name: str, attributes=None) -> None:
        self._inner.add_event(name, {k: clip(v) for k, v in (attributes or {}).items() if v is not None})

    def __getattr__(self, name: str):
        return getattr(self._inner, name)


def annotate(current: Span | SafeSpan, **attributes) -> None:
    """Set attributes on a span, skipping None and clipping any long string."""
    for key, value in attributes.items():
        if value is not None:
            current.set_attribute(key, clip(value))


@contextmanager
def span(name: str, **attributes) -> Iterator[SafeSpan]:
    """A child of whatever span is current, ended on exit. An exception marks it with the kind of error and passes on; its message and stack are NOT recorded."""
    with _tracer().start_as_current_span(name, record_exception=False, set_status_on_exception=False) as raw:
        current = SafeSpan(raw)
        annotate(current, **attributes)
        try:
            yield current
        except BaseException as error:
            current.set_attribute("error.type", type(error).__name__)
            current.set_status(Status(StatusCode.ERROR))  # no description: an error message can quote what the user wrote
            raise


def current_span() -> SafeSpan:
    """The span the caller is running inside (a do-nothing one if there is none)."""
    return SafeSpan(trace.get_current_span())


def event(current: Span | SafeSpan, name: str, **attributes) -> None:
    current.add_event(name, attributes)


class JsonLinesExporter(SpanExporter):
    """One compact JSON object per finished span, appended to a file. Readable with any tool that reads lines; the format is this module's own, not OTLP."""

    def __init__(self, path: str | Path):
        self._path = Path(path)
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()

    def export(self, spans: Sequence[ReadableSpan]) -> SpanExportResult:
        lines = "".join(json.dumps(self._record(s), ensure_ascii=False, default=str) + "\n" for s in spans)
        with self._lock, self._path.open("a", encoding="utf-8") as file:  # one write per batch: lines from different threads never interleave
            file.write(lines)
        return SpanExportResult.SUCCESS

    @staticmethod
    def _record(s: ReadableSpan) -> dict:
        return {
            "name": s.name,
            "trace_id": f"{s.context.trace_id:032x}",
            "span_id": f"{s.context.span_id:016x}",
            "parent_id": f"{s.parent.span_id:016x}" if s.parent else None,
            "start": s.start_time / 1e9,
            "duration_ms": round((s.end_time - s.start_time) / 1e6, 3),
            "status": s.status.status_code.name,
            "attributes": dict(s.attributes or {}),
            "events": [{"name": e.name, "attributes": dict(e.attributes or {})} for e in s.events],
        }

    def shutdown(self) -> None:
        pass


def configure_from_env() -> bool:
    """Turn tracing on if the environment asks for it. Returns whether it did."""
    trace_file = os.environ.get("NOTEBOOK_TRACE_FILE")
    endpoint = os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT")
    if not trace_file and not endpoint:
        return False

    provider = TracerProvider(resource=Resource.create({"service.name": SERVICE_NAME}))
    if trace_file:
        provider.add_span_processor(SimpleSpanProcessor(JsonLinesExporter(trace_file)))
    if endpoint:
        from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter  # imported only when asked for: it pulls in gRPC

        provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))  # reads the endpoint and headers from the standard OTEL_* variables
    use_provider(provider)
    return True


def usage_attributes(response) -> dict:
    """Token counts from a Gemini response, under the OpenTelemetry GenAI names. Missing numbers are left out, not guessed."""
    meta = getattr(response, "usage_metadata", None)
    if meta is None:
        return {}
    return {
        "gen_ai.usage.input_tokens": getattr(meta, "prompt_token_count", None),
        "gen_ai.usage.output_tokens": getattr(meta, "candidates_token_count", None),
        "gen_ai.usage.reasoning_tokens": getattr(meta, "thoughts_token_count", None),
    }
