import logging
import re

import pytest
from fastapi.testclient import TestClient
from google.genai import errors as genai_errors

from app.api import MAX_DOC_CHARS, create_app
from app.generation import RagResult, Status
from app.retrieval import Retriever
from app.service import Service
from app.store import VectorStore

GUIDE = "# Guide\n\n## Setup\n\nInstall the widget with pip install widget.\n\n## Usage\n\nRun widget --help for options."


def answer_from_first_hit(question, hits):
    if not hits:
        return RagResult(Status.not_found, "", [])
    return RagResult(Status.answered, "stub answer", hits[:1])


def make_client(answerer=answer_from_first_hit):
    store = VectorStore(collection="api_tests")
    store.clear()
    return TestClient(create_app(service=Service(store, answerer)))


@pytest.fixture()
def client():
    with make_client() as c:
        yield c


def test_an_empty_retriever_does_not_crash():
    store = VectorStore(collection="empty_retriever")
    store.clear()
    assert Retriever(store).hybrid("anything", 4, 20) == []


def test_health_on_an_empty_service(client):
    assert client.get("/health").json() == {"status": "ok", "documents": 0, "chunks": 0}


def test_ingest_then_ask_returns_the_cited_source(client):
    created = client.post("/documents", json={"name": "guide.md", "content": GUIDE})
    assert created.status_code == 201 and created.json()["chunks"] >= 1
    assert "guide.md" in client.get("/documents").json()

    reply = client.post("/ask", json={"question": "How do I install the widget?"})
    body = reply.json()
    assert reply.status_code == 200 and body["status"] == "answered"
    assert body["sources"][0]["source"] == "guide.md" and body["sources"][0]["id"].startswith("guide.md#")


def test_asking_with_no_documents_is_not_found_not_an_error(client):
    reply = client.post("/ask", json={"question": "What is the capital of France?"})
    assert reply.status_code == 200
    assert reply.json() == {"status": "not_found", "answer": "", "sources": [], "action": "retrieve", "trace": []}


def test_reingesting_replaces_the_old_chunks(client):
    first = client.post("/documents", json={"name": "guide.md", "content": GUIDE}).json()["chunks"]
    client.post("/documents", json={"name": "guide.md", "content": GUIDE})
    assert client.get("/documents").json() == {"guide.md": first}


@pytest.mark.parametrize("name", ["../etc/passwd", "notes.txt", "a/b.md", ".md", ""])
def test_bad_document_names_are_rejected(client, name):
    assert client.post("/documents", json={"name": name, "content": "# x\n\ntext"}).status_code == 422


def test_oversized_and_empty_documents_are_rejected(client):
    assert client.post("/documents", json={"name": "big.md", "content": "a" * (MAX_DOC_CHARS + 1)}).status_code == 422
    assert client.post("/documents", json={"name": "empty.md", "content": ""}).status_code == 422


def test_delete_removes_the_document_and_a_second_delete_is_404(client):
    client.post("/documents", json={"name": "guide.md", "content": GUIDE})
    assert client.delete("/documents/guide.md").status_code == 204
    assert client.get("/documents").json() == {}
    assert client.delete("/documents/guide.md").status_code == 404


def test_ask_input_is_validated(client):
    assert client.post("/ask", json={"question": "ab"}).status_code == 422
    assert client.post("/ask", json={"question": "a fine question", "k": 99}).status_code == 422


def test_an_unusable_model_output_is_a_502():
    with make_client(lambda q, h: RagResult(Status.error, "", [], "finish_reason=MAX_TOKENS")) as c:
        reply = c.post("/ask", json={"question": "anything at all"})
    assert reply.status_code == 502 and "MAX_TOKENS" in reply.json()["detail"]


def test_a_rate_limited_model_is_a_503_with_retry_after():
    def rate_limited(question, hits):
        raise genai_errors.ClientError(429, {"error": {"message": "slow down"}})

    with make_client(rate_limited) as c:
        reply = c.post("/ask", json={"question": "anything at all"})
    assert reply.status_code == 503 and reply.headers["Retry-After"] == "10"
    assert "slow down" not in reply.text  # upstream details are not leaked to the caller


def test_a_valid_request_id_is_echoed_and_appears_in_the_service_log(caplog):
    with make_client() as c:
        c.post("/documents", json={"name": "guide.md", "content": GUIDE})
        with caplog.at_level(logging.INFO, logger="notebook"):
            reply = c.post("/ask", json={"question": "How do I install the widget?"}, headers={"X-Request-ID": "trace-123.abc_X"})
    assert reply.headers["X-Request-ID"] == "trace-123.abc_X"
    ask_lines = [r for r in caplog.records if r.name == "notebook.service" and r.getMessage().startswith("ask ")]
    assert ask_lines and all(r.request_id == "trace-123.abc_X" for r in ask_lines)  # the sync handler runs in a threadpool: the id must survive that


def test_a_request_id_is_generated_when_none_is_sent(client):
    reply = client.get("/health")
    assert re.fullmatch(r"[0-9a-f]{32}", reply.headers["X-Request-ID"])


@pytest.mark.parametrize("unsafe", ["bad id with spaces", "{injected}", "a" * 65, "tab\there"])
def test_an_unsafe_request_id_is_replaced_not_trusted(client, unsafe):
    try:
        reply = client.get("/health", headers={"X-Request-ID": unsafe})
    except Exception:
        pytest.skip("the HTTP client refused to send this header value at all")
    assert re.fullmatch(r"[0-9a-f]{32}", reply.headers["X-Request-ID"])


def test_the_request_id_is_not_left_set_after_the_request(client):
    client.get("/health", headers={"X-Request-ID": "first"})
    from app.api import request_id_var

    assert request_id_var.get() == "-"
