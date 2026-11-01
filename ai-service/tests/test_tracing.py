"""Tracing: what a request did, how long each step took, which model it used and how many tokens it cost, without ever recording what anyone asked or wrote.

Spans go through OpenTelemetry's API, so the code under test never knows where they end up. These tests give it an in-memory exporter and read the spans back.
"""

import json
import threading
import types as pytypes

import pytest
from google.genai import errors as genai_errors
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import SimpleSpanProcessor
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter
from opentelemetry.trace import StatusCode

from app import tracing
from app.agent import AgentDeps, Route
from app.generation import RagResult, Status, answer_question
from app.store import Hit
from tests import support

GUIDE = "# Guide\n\n## Setup\n\nInstall the widget with pip install widget.\n\n## Usage\n\nRun widget --help for options."
QUESTION = "How do I install the zebra-crossing widget?"
SECRET_DOC = "# Merger plans\n\n## Codename\n\nProject Nightingale will acquire Initech for forty million."


@pytest.fixture()
def exporter():
    memory = InMemorySpanExporter()
    provider = TracerProvider()
    provider.add_span_processor(SimpleSpanProcessor(memory))
    tracing.use_provider(provider)
    yield memory
    tracing.use_provider(None)


def by_name(exporter, name):
    return [s for s in exporter.get_finished_spans() if s.name == name]


def one(exporter, name):
    spans = by_name(exporter, name)
    assert len(spans) == 1, f"expected one {name!r}, found {[s.name for s in exporter.get_finished_spans()]}"
    return spans[0]


# ----------------------------------------------------------------- the helper itself

def test_without_a_provider_spans_cost_nothing_and_break_nothing():
    tracing.use_provider(None)
    with tracing.span("anything", answer=42) as s:
        s.set_attribute("more", "fine")  # a no-op span accepts everything


def test_spans_nest_and_share_a_trace(exporter):
    with tracing.span("outer"):
        with tracing.span("inner", size=3):
            pass

    inner, outer = one(exporter, "inner"), one(exporter, "outer")
    assert inner.parent.span_id == outer.context.span_id
    assert inner.context.trace_id == outer.context.trace_id
    assert inner.attributes["size"] == 3


def test_a_failure_marks_the_span_with_the_kind_of_error_and_never_its_message(exporter):
    with pytest.raises(RuntimeError):
        with tracing.span("risky"):
            raise RuntimeError("the user's document said: launch code 0000")

    span = one(exporter, "risky")
    assert span.status.status_code is StatusCode.ERROR
    assert span.attributes["error.type"] == "RuntimeError"
    assert "launch code" not in json.dumps([span.status.description, [e.attributes for e in span.events], dict(span.attributes)], default=str)
    assert span.events == ()  # record_exception would have stored the message and the stack


def test_a_long_string_is_cut_so_that_a_slip_cannot_record_a_whole_document(exporter):
    with tracing.span("slip", text="x" * 5000) as s:
        s.set_attribute("later", "y" * 5000)

    span = one(exporter, "slip")
    assert len(span.attributes["text"]) <= tracing.MAX_ATTRIBUTE_CHARS
    assert len(span.attributes["later"]) <= tracing.MAX_ATTRIBUTE_CHARS


def test_none_attributes_are_skipped(exporter):
    with tracing.span("sparse", present=1, absent=None):
        pass
    assert "absent" not in one(exporter, "sparse").attributes


# ----------------------------------------------------------------- the file exporter

def test_the_json_lines_exporter_writes_one_valid_line_per_span(tmp_path):
    path = tmp_path / "spans.jsonl"
    provider = TracerProvider()
    provider.add_span_processor(SimpleSpanProcessor(tracing.JsonLinesExporter(path)))
    tracing.use_provider(provider)
    try:
        with tracing.span("parent", kind="demo"):
            with tracing.span("child", n=2):
                pass
    finally:
        tracing.use_provider(None)

    lines = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
    child, parent = lines  # a span is written when it ends: the child first
    assert (child["name"], parent["name"]) == ("child", "parent")
    assert child["parent_id"] == parent["span_id"] and child["trace_id"] == parent["trace_id"]
    assert child["attributes"] == {"n": 2} and parent["attributes"] == {"kind": "demo"}
    assert child["duration_ms"] >= 0 and child["status"] == "UNSET"


def test_concurrent_writers_never_interleave_a_line(tmp_path):
    path = tmp_path / "spans.jsonl"
    provider = TracerProvider()
    provider.add_span_processor(SimpleSpanProcessor(tracing.JsonLinesExporter(path)))
    tracing.use_provider(provider)

    def work():
        for i in range(50):
            with tracing.span("w", i=i):
                pass

    try:
        threads = [threading.Thread(target=work) for _ in range(8)]
        [t.start() for t in threads]
        [t.join() for t in threads]
    finally:
        tracing.use_provider(None)

    assert len([json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]) == 400


def test_the_service_traces_to_a_file_only_when_asked_to(tmp_path, monkeypatch):
    monkeypatch.delenv("NOTEBOOK_TRACE_FILE", raising=False)
    monkeypatch.delenv("OTEL_EXPORTER_OTLP_ENDPOINT", raising=False)
    assert tracing.configure_from_env() is False  # nothing configured: tracing stays off

    path = tmp_path / "t.jsonl"
    monkeypatch.setenv("NOTEBOOK_TRACE_FILE", str(path))
    try:
        assert tracing.configure_from_env() is True
        with tracing.span("hello"):
            pass
    finally:
        tracing.use_provider(None)
    assert json.loads(path.read_text(encoding="utf-8").splitlines()[0])["name"] == "hello"


# ----------------------------------------------------------------- one request, end to end

def answer_from_first_hit(question, hits):
    return RagResult(Status.answered, "stub answer", hits[:1]) if hits else RagResult(Status.not_found, "", [])


def every_recorded_value(exporter):
    for s in exporter.get_finished_spans():
        yield from (str(v) for v in s.attributes.values())
        yield s.name
        for e in s.events:
            yield e.name
            yield from (str(v) for v in (e.attributes or {}).values())


def test_a_question_produces_one_connected_trace_with_the_request_id_and_the_outcome(exporter):
    with support.make_client(answer_from_first_hit) as client:
        client.post("/documents", json={"name": "guide.md", "content": GUIDE})
        exporter.clear()
        reply = client.post("/ask", json={"question": QUESTION}, headers={"X-Request-ID": "req-abc-123"})

    assert reply.status_code == 200
    http, ask = one(exporter, "http.request"), one(exporter, "rag.ask")
    hybrid, dense, keyword = one(exporter, "retrieve.hybrid"), one(exporter, "retrieve.dense"), one(exporter, "retrieve.keyword")

    assert ask.parent.span_id == http.context.span_id
    assert hybrid.parent.span_id == ask.context.span_id
    assert dense.parent.span_id == hybrid.context.span_id and keyword.parent.span_id == hybrid.context.span_id
    assert len({s.context.trace_id for s in exporter.get_finished_spans()}) == 1

    assert http.attributes["request_id"] == "req-abc-123" == reply.headers["x-request-id"]
    assert http.attributes["http.route"] == "/ask" and http.attributes["http.status_code"] == 200
    assert ask.attributes["rag.status"] == "answered" and ask.attributes["rag.k"] == 4
    assert ask.attributes["rag.retrieved"] >= 1 and ask.attributes["rag.cited"] == 1
    assert hybrid.attributes["retrieve.returned"] >= 1


def test_nothing_anyone_asked_or_wrote_is_recorded(exporter):
    with support.make_client(answer_from_first_hit) as client:
        client.post("/documents", json={"name": "merger-plans.md", "content": SECRET_DOC})
        client.post("/ask", json={"question": "What is the Nightingale codename?"})
        client.get("/documents")
        client.delete("/documents/merger-plans.md")

    recorded = " | ".join(every_recorded_value(exporter)).lower()
    for private in ("nightingale", "initech", "forty million", "merger-plans", "codename", support.TENANT_A, support.SERVICE_KEY.lower()):
        assert private not in recorded, f"{private!r} leaked into a span"


def test_a_route_is_recorded_as_its_template_not_with_the_document_name_in_it(exporter):
    with support.make_client(answer_from_first_hit) as client:
        client.delete("/documents/merger-plans.md")

    assert one(exporter, "http.request").attributes["http.route"] == "/documents/{name}"


def test_a_tenant_is_recorded_only_as_a_short_fingerprint(exporter):
    with support.make_client(answer_from_first_hit) as client:
        client.get("/documents")

    fingerprint = one(exporter, "http.request").attributes["tenant.fingerprint"]
    assert len(fingerprint) == 8 and fingerprint != support.TENANT_A[:8]


def test_two_tenants_have_different_fingerprints(exporter):
    tenants = support.make_tenants(answer_from_first_hit)
    with support.make_client(tenant=support.TENANT_A, tenants=tenants) as a, support.make_client(tenant=support.TENANT_B, tenants=tenants) as b:
        a.get("/documents")
        b.get("/documents")

    prints = {s.attributes["tenant.fingerprint"] for s in by_name(exporter, "http.request")}
    assert len(prints) == 2


def test_a_refused_request_is_still_traced_with_its_status(exporter):
    with support.make_client() as client:
        reply = client.get("/documents", headers={"X-Service-Key": "wrong"})

    assert reply.status_code == 401
    assert one(exporter, "http.request").attributes["http.status_code"] == 401


def test_ingesting_records_how_many_chunks_not_what_they_said(exporter):
    with support.make_client(answer_from_first_hit) as client:
        client.post("/documents", json={"name": "guide.md", "content": GUIDE})

    ingest = one(exporter, "rag.ingest")
    assert ingest.attributes["ingest.chunks"] >= 1


# ----------------------------------------------------------------- the model call

def fake_client(*outcomes):
    """A stand-in for the Gemini client. Each outcome is a response or an exception to raise, used in order."""
    remaining = list(outcomes)

    def generate_content(model, contents, config):
        outcome = remaining.pop(0)
        if isinstance(outcome, Exception):
            raise outcome
        return outcome

    return pytypes.SimpleNamespace(models=pytypes.SimpleNamespace(generate_content=generate_content))


def response(parsed, prompt_tokens=120, output_tokens=15, thought_tokens=None, finish="STOP"):
    return pytypes.SimpleNamespace(
        parsed=parsed,
        candidates=[pytypes.SimpleNamespace(finish_reason=finish)],
        usage_metadata=pytypes.SimpleNamespace(prompt_token_count=prompt_tokens, candidates_token_count=output_tokens, thoughts_token_count=thought_tokens),
    )


HITS = [Hit("a.md", "Alpha", "The widget needs pip.", 0.5)]


def test_a_model_call_records_the_model_tokens_latency_and_outcome(exporter):
    from app.generation import MODEL, GeneratedAnswer

    result = answer_question("How is it installed?", HITS, fake_client(response(GeneratedAnswer(found=True, answer="With pip.", citations=[1]), 321, 17, 40)))

    assert result.status is Status.answered
    call = one(exporter, "llm.generate")
    assert call.attributes["gen_ai.operation.name"] == "generate_content"
    assert call.attributes["gen_ai.request.model"] == MODEL
    assert call.attributes["gen_ai.usage.input_tokens"] == 321
    assert call.attributes["gen_ai.usage.output_tokens"] == 17
    assert call.attributes["gen_ai.usage.reasoning_tokens"] == 40
    assert call.attributes["llm.attempts"] == 1 and call.attributes["llm.finish_reason"] == "STOP"
    assert call.attributes["rag.status"] == "answered"
    assert call.end_time >= call.start_time  # a fake call is faster than the clock ticks


def test_a_retried_call_records_each_retry_and_how_many_attempts_it_took(exporter, monkeypatch):
    from app import generation
    from app.generation import GeneratedAnswer

    monkeypatch.setattr(generation.time, "sleep", lambda s: None)
    busy = genai_errors.ClientError(429, {"error": {"message": "slow down, secret-question-text"}})

    answer_question("q", HITS, fake_client(busy, busy, response(GeneratedAnswer(found=False, answer="", citations=[]))))

    call = one(exporter, "llm.generate")
    assert call.attributes["llm.attempts"] == 3
    retries = [e for e in call.events if e.name == "retry"]
    assert [e.attributes["http.status_code"] for e in retries] == [429, 429]
    assert "secret-question-text" not in " ".join(every_recorded_value(exporter))


def test_a_call_that_finally_fails_is_an_error_span_with_the_status_code(exporter, monkeypatch):
    from app import generation

    monkeypatch.setattr(generation.time, "sleep", lambda s: None)
    broken = genai_errors.ClientError(400, {"error": {"message": "bad"}})

    with pytest.raises(genai_errors.ClientError):
        answer_question("q", HITS, fake_client(broken))

    call = one(exporter, "llm.generate")
    assert call.status.status_code is StatusCode.ERROR and call.attributes["error.type"] == "ClientError"


def test_nothing_retrieved_means_no_model_call_and_no_llm_span(exporter):
    answer_question("q", [], fake_client())
    assert by_name(exporter, "llm.generate") == []


def test_the_prompt_is_not_recorded(exporter):
    from app.generation import GeneratedAnswer

    answer_question("What is the secret-question-text?", [Hit("a.md", "A", "secret-passage-text", 0.5)], fake_client(response(GeneratedAnswer(found=False, answer="", citations=[]))))

    recorded = " ".join(every_recorded_value(exporter))
    assert "secret-question-text" not in recorded and "secret-passage-text" not in recorded


def test_the_other_model_calls_are_traced_with_their_purpose(exporter, monkeypatch):
    from app import llm

    monkeypatch.setattr(llm, "_client", lambda: fake_client(response(None, 50, 5)))
    llm.call_model("contents", config=None, purpose="route")

    call = one(exporter, "llm.route")
    assert call.attributes["gen_ai.usage.input_tokens"] == 50 and call.attributes["llm.attempts"] == 1


# ----------------------------------------------------------------- the agent

def fake_agent_deps(retrieve, store, answer):
    return AgentDeps(
        route=lambda q, docs: Route(action="retrieve"), rewrite=lambda q: q, retrieve=retrieve, answer=answer,
        summarize=lambda doc, chunks: RagResult(Status.answered, "summary", chunks), chunks_of=store.chunks_of, documents=lambda: list(store.source_counts()),
    )


def test_an_agent_run_traces_each_step_it_took_under_one_parent(exporter):
    with support.make_client(answer_from_first_hit, fake_agent_deps) as client:
        client.post("/documents", json={"name": "guide.md", "content": GUIDE})
        exporter.clear()
        reply = client.post("/ask", json={"question": QUESTION, "mode": "agent"})

    assert reply.status_code == 200
    parent = one(exporter, "rag.ask_agent")
    steps = {s.name: s for s in exporter.get_finished_spans() if s.name.startswith("agent.")}
    assert set(steps) == {"agent.route", "agent.retrieve", "agent.answer"}
    assert all(s.parent.span_id == parent.context.span_id for s in steps.values())
    assert parent.attributes["rag.action"] == "retrieve" and parent.attributes["rag.status"] == "answered"
    assert steps["agent.answer"].attributes["rag.status"] == "answered"
    assert len({s.context.trace_id for s in exporter.get_finished_spans()}) == 1
