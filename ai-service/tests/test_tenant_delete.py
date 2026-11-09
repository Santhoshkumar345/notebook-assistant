"""Deleting an account deletes the person's index too. The API asks for it with DELETE /tenant, before it removes the account, so that a failure here
leaves the account in place and the deletion can simply be repeated.

What these tests protect: only the named tenant's data goes; it is really gone (not hidden); the call is safe to repeat; it needs the service key and a valid
tenant like every other route; and nothing about who was deleted is logged or recorded.
"""

import logging

from fastapi.testclient import TestClient
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import SimpleSpanProcessor
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter

from app import tracing
from app.api import create_app
from app.tenants import default_tenants
from tests import support
from tests.test_tracing import every_recorded_value

DOC = "# Plans\n\n## Codename\n\nThe launch codename is Zephyrwood."


def two_clients(tenants=None):
    tenants = tenants or support.make_tenants()
    return support.make_client(tenant=support.TENANT_A, tenants=tenants), support.make_client(tenant=support.TENANT_B, tenants=tenants)


def test_deleting_a_tenant_removes_their_documents_and_only_theirs():
    a, b = two_clients()
    with a, b:
        a.post("/documents", json={"name": "plans.md", "content": DOC})
        b.post("/documents", json={"name": "plans.md", "content": DOC})

        reply = a.delete("/tenant")

        assert reply.status_code == 204
        assert a.get("/documents").json() == {}
        assert list(b.get("/documents").json()) == ["plans.md"]  # the other person's document, same name, untouched


def test_the_index_is_really_gone_not_just_hidden():
    tenants = default_tenants()  # an in-memory Chroma shared by its tenants, as the real service shares one persistent one
    name = f"tenant_{support.TENANT_A}"
    with TestClient(create_app(tenants=tenants, service_key=support.SERVICE_KEY), headers=support.headers(support.TENANT_A)) as client:
        client.post("/documents", json={"name": "plans.md", "content": DOC})
        shared = tenants.get(support.TENANT_A)._store._client
        names = lambda: [c if isinstance(c, str) else c.name for c in shared.list_collections()]  # noqa: E731
        assert name in names()

        client.delete("/tenant")

        assert name not in names()


def test_deleting_twice_or_a_tenant_that_never_existed_is_fine():
    a, _ = two_clients()
    with a:
        assert a.delete("/tenant").status_code == 204  # nothing was ever stored
        a.post("/documents", json={"name": "plans.md", "content": DOC})
        assert a.delete("/tenant").status_code == 204
        assert a.delete("/tenant").status_code == 204


def test_a_tenant_can_start_again_after_being_deleted_with_nothing_left_of_before():
    a, _ = two_clients()
    with a:
        a.post("/documents", json={"name": "old.md", "content": DOC})
        a.delete("/tenant")

        a.post("/documents", json={"name": "new.md", "content": DOC})

        assert list(a.get("/documents").json()) == ["new.md"]


def test_it_needs_the_service_key_and_a_valid_tenant_like_every_other_route():
    tenants = support.make_tenants()
    with TestClient(support.make_app(tenants=tenants)) as client:
        assert client.delete("/tenant", headers=support.headers(support.TENANT_A, key=None)).status_code == 401
        assert client.delete("/tenant", headers=support.headers(support.TENANT_A, key="wrong")).status_code == 401
        assert client.delete("/tenant", headers=support.headers(None)).status_code == 400
        assert client.delete("/tenant", headers=support.headers("not-a-tenant")).status_code == 400
        assert client.delete("/tenant", headers=support.headers("a" * 31 + "\n")).status_code == 400  # a newline after 31 characters is not 32 hex characters


def test_a_refused_deletion_deletes_nothing():
    a, _ = two_clients()
    with a:
        a.post("/documents", json={"name": "plans.md", "content": DOC})

        a.delete("/tenant", headers={"X-Service-Key": "wrong", "X-Tenant-ID": support.TENANT_A})

        assert list(a.get("/documents").json()) == ["plans.md"]


def test_who_was_deleted_is_not_written_to_the_log_or_a_trace(caplog):
    memory = InMemorySpanExporter()
    provider = TracerProvider()
    provider.add_span_processor(SimpleSpanProcessor(memory))
    tracing.use_provider(provider)
    a, _ = two_clients()
    try:
        with a, caplog.at_level(logging.INFO, logger="notebook"):
            a.post("/documents", json={"name": "private-plans.md", "content": DOC})
            a.delete("/tenant")
    finally:
        tracing.use_provider(None)

    recorded = " ".join(every_recorded_value(memory)).lower() + caplog.text.lower()
    assert support.TENANT_A not in recorded and "private-plans" not in recorded and "zephyrwood" not in recorded
    assert "tenant.delete" in [s.name for s in memory.get_finished_spans()]
