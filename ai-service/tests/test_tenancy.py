"""The AI service holds every user's documents, so these tests are about the boundary between them.

Isolation is physical, not a filter: each tenant has its own Chroma collection and its own keyword index, so a query for one tenant
cannot return another's passage whatever its text. The service key stops anything but the .NET API from naming a tenant at all.
"""

import threading

import pytest
from fastapi.testclient import TestClient

from app.agent import Action, Route
from app.api import create_app
from app.generation import RagResult, Status
from app.service import Service
from app.store import VectorStore
from app.tenants import InvalidTenant, Tenants, default_tenants
from tests.support import SERVICE_KEY, TENANT_A, TENANT_B, headers, make_app, make_client, make_tenants

SECRET = "The launch codename is Zephyrwood and nobody else may learn it."
A_DOC = "# Plans\n\n## Setup\n\nInstall the widget with pip install widget.\n"
B_DOC = f"# Private\n\n## Codename\n\n{SECRET}\n"


def spying_answerer(seen: list):
    def answer(question, hits):
        seen.append([h.source for h in hits] + [h.body for h in hits])
        return RagResult(Status.answered, "stub", hits[:1]) if hits else RagResult(Status.not_found, "", [])

    return answer


def raw_client(**sent):
    """A client with only the headers given, to test what happens when they are missing or wrong."""
    return TestClient(make_app(), headers=sent)


# ----------------------------------------------------------------- the service key

ROUTES = [
    ("GET", "/health", None),
    ("GET", "/documents", None),
    ("POST", "/documents", {"name": "a.md", "content": "# a"}),
    ("DELETE", "/documents/a.md", None),
    ("POST", "/ask", {"question": "anything at all"}),
]


@pytest.mark.parametrize("method,path,body", ROUTES)
@pytest.mark.parametrize(
    "sent",
    [
        {},  # no key at all
        {"X-Service-Key": ""},
        {"X-Service-Key": "wrong-key-0123456789abcdef-----"},
        {"X-Service-Key": SERVICE_KEY + "x"},  # right prefix, wrong length
        {"X-Service-Key": SERVICE_KEY[:-1]},
        {"Authorization": f"Bearer {SERVICE_KEY}"},  # the right secret in the wrong place
    ],
    ids=["missing", "empty", "wrong", "too-long", "too-short", "wrong-header"],
)
def test_every_route_refuses_a_missing_or_wrong_service_key(method, path, body, sent):
    with raw_client(**sent, **{"X-Tenant-ID": TENANT_A}) as client:
        reply = client.request(method, path, json=body)
    assert reply.status_code == 401
    assert reply.headers["X-Request-ID"]  # even a refusal can be traced
    assert SERVICE_KEY not in reply.text


def test_the_key_is_checked_before_the_tenant_so_a_stranger_learns_nothing_about_tenants():
    with raw_client(**{"X-Service-Key": "wrong-key-0123456789abcdef-----"}) as client:  # no tenant header either
        assert client.get("/documents").status_code == 401


def test_the_app_will_not_start_without_a_service_key(monkeypatch):
    monkeypatch.delenv("AI_SERVICE_KEY", raising=False)
    with pytest.raises(RuntimeError, match="AI_SERVICE_KEY"):
        create_app(tenants=make_tenants())


@pytest.mark.parametrize("weak", ["short", "x" * 15])
def test_a_weak_service_key_is_refused_at_startup(weak, monkeypatch):
    monkeypatch.delenv("AI_SERVICE_KEY", raising=False)
    with pytest.raises(RuntimeError, match="at least 16"):
        create_app(tenants=make_tenants(), service_key=weak)


def test_the_key_can_come_from_the_environment(monkeypatch):
    monkeypatch.setenv("AI_SERVICE_KEY", SERVICE_KEY)
    with TestClient(create_app(tenants=make_tenants()), headers=headers()) as client:
        assert client.get("/documents").status_code == 200


# ----------------------------------------------------------------- the tenant header

@pytest.mark.parametrize(
    "tenant",
    [None, "", "A" * 32, "a" * 31, "a" * 33, "g" * 32, "../../etc/passwd", "a" * 31 + "/", "a" * 31 + "\n", "a" * 32 + "\n", "tenant_" + "a" * 25],
    ids=["missing", "empty", "uppercase", "short", "long", "not-hex", "traversal", "slash", "newline-replacing-last", "valid-plus-newline", "prefixed"],
)
def test_a_missing_or_malformed_tenant_is_refused(tenant):
    sent = {"X-Service-Key": SERVICE_KEY}
    if tenant is not None:
        sent["X-Tenant-ID"] = tenant
    try:
        with raw_client(**sent) as client:
            reply = client.get("/documents")
    except Exception:
        pytest.skip("the HTTP client refused to send this header value at all")
    assert reply.status_code == 400


def test_health_needs_a_key_but_no_tenant_and_reveals_no_counts():
    with raw_client(**{"X-Service-Key": SERVICE_KEY}) as client:
        reply = client.get("/health")
    assert reply.status_code == 200 and reply.json() == {"status": "ok"}


# ----------------------------------------------------------------- isolation

def test_the_same_document_name_in_two_tenants_is_two_independent_documents():
    tenants = make_tenants()
    with make_client(tenant=TENANT_A, tenants=tenants) as a, make_client(tenant=TENANT_B, tenants=tenants) as b:
        a.post("/documents", json={"name": "notes.md", "content": A_DOC})
        assert b.get("/documents").json() == {}  # B sees nothing of A's

        b.post("/documents", json={"name": "notes.md", "content": B_DOC})  # same name: not a replacement of A's
        assert list(a.get("/documents").json()) == ["notes.md"] and list(b.get("/documents").json()) == ["notes.md"]

        a.delete("/documents/notes.md")
        assert a.get("/documents").json() == {}
        assert list(b.get("/documents").json()) == ["notes.md"]  # B's copy is untouched


def test_a_question_never_retrieves_another_tenants_passage():
    seen_by_a, seen_by_b = [], []
    tenants = Tenants(lambda t: Service(VectorStore(collection=f"iso{t}"), answerer=spying_answerer(seen_by_a if t == TENANT_A else seen_by_b)))
    with make_client(tenant=TENANT_A, tenants=tenants) as a, make_client(tenant=TENANT_B, tenants=tenants) as b:
        a.post("/documents", json={"name": "plans.md", "content": A_DOC})
        b.post("/documents", json={"name": "private.md", "content": B_DOC})

        # A asks for B's secret in B's own words: the exact text is in the corpus, just not A's part of it.
        reply = a.post("/ask", json={"question": "What is the launch codename Zephyrwood?", "k": 10})
        assert reply.status_code == 200
        assert all("Zephyrwood" not in text and "private.md" not in text for text in seen_by_a[0])

        reply = b.post("/ask", json={"question": "What is the launch codename Zephyrwood?", "k": 10})
        assert any("Zephyrwood" in text for text in seen_by_b[0])  # the same question does find it for its owner
        assert reply.json()["status"] == "answered"


def test_a_tenant_cannot_delete_another_tenants_document():
    tenants = make_tenants()
    with make_client(tenant=TENANT_A, tenants=tenants) as a, make_client(tenant=TENANT_B, tenants=tenants) as b:
        a.post("/documents", json={"name": "plans.md", "content": A_DOC})

        assert b.delete("/documents/plans.md").status_code == 404
        assert list(a.get("/documents").json()) == ["plans.md"]


def test_the_agent_runs_against_the_callers_own_store_and_nobody_elses():
    stores = {}

    def deps_factory(retrieve, store, answer):
        stores.setdefault("seen", []).append(store)
        raise_route = Route(action=Action.clarify, clarifying_question="Which one?")
        from tests.test_agent import Spy

        return Spy(route=raise_route).deps()

    tenants = make_tenants(agent_deps_factory=deps_factory)
    with make_client(tenant=TENANT_A, tenants=tenants) as a, make_client(tenant=TENANT_B, tenants=tenants) as b:
        a.post("/ask", json={"question": "what was the limit?", "mode": "agent"})
        b.post("/ask", json={"question": "what was the limit?", "mode": "agent"})

    assert stores["seen"][0] is tenants.get(TENANT_A)._store
    assert stores["seen"][1] is tenants.get(TENANT_B)._store
    assert stores["seen"][0] is not stores["seen"][1]


def test_tenants_do_not_share_a_write_lock_so_one_slow_upload_does_not_block_another_user():
    tenants = make_tenants()
    service_a, service_b = tenants.get(TENANT_A), tenants.get(TENANT_B)
    finished = threading.Event()

    with service_a._write_lock:  # A is in the middle of a long write
        worker = threading.Thread(target=lambda: (service_b.ingest("b.md", B_DOC), finished.set()))
        worker.start()
        assert finished.wait(timeout=30), "B's upload waited for A's write lock"
    worker.join()


def test_data_and_isolation_survive_a_restart(tmp_path):
    first = default_tenants(str(tmp_path))
    first.get(TENANT_A).ingest("plans.md", A_DOC)
    first.get(TENANT_B).ingest("private.md", B_DOC)

    restarted = default_tenants(str(tmp_path))  # a new process would build the registry the same way
    assert list(restarted.get(TENANT_A).documents()) == ["plans.md"]
    assert list(restarted.get(TENANT_B).documents()) == ["private.md"]


# ----------------------------------------------------------------- the registry

def test_a_tenant_gets_one_service_for_its_lifetime_and_each_tenant_its_own():
    tenants = make_tenants()
    assert tenants.get(TENANT_A) is tenants.get(TENANT_A)
    assert tenants.get(TENANT_A) is not tenants.get(TENANT_B)


def test_concurrent_first_requests_for_a_tenant_build_its_service_once():
    built = []
    tenants = Tenants(lambda t: built.append(t) or object())
    gate = threading.Barrier(8)
    results = []

    def fetch():
        gate.wait()
        results.append(tenants.get(TENANT_A))

    threads = [threading.Thread(target=fetch) for _ in range(8)]
    [t.start() for t in threads]
    [t.join() for t in threads]

    assert built == [TENANT_A] and len({id(r) for r in results}) == 1


# "a" * 32 + newline is the case a $-anchored pattern lets through: 32 valid characters, then a line break.
@pytest.mark.parametrize("bad", ["", "A" * 32, "a" * 31, "../x", "a" * 31 + "\n", "a" * 32 + "\n", "tenant_" + "a" * 25])
def test_the_registry_refuses_a_malformed_tenant_id_so_no_collection_is_named_from_it(bad):
    tenants = Tenants(lambda t: pytest.fail("a service was built for a malformed tenant id"))
    with pytest.raises(InvalidTenant):
        tenants.get(bad)
