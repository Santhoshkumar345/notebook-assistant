"""Shared helpers for the API tests: an app whose tenants each get their own in-memory store, and clients that send the headers the
.NET API sends (the service key and the tenant id)."""

import uuid

from fastapi.testclient import TestClient

from app.api import create_app
from app.service import Service
from app.store import VectorStore
from app.tenants import Tenants

SERVICE_KEY = "test-service-key-0123456789abcdef"
TENANT_A = "a" * 32
TENANT_B = "b" * 32


def headers(tenant: str | None = TENANT_A, key: str | None = SERVICE_KEY) -> dict[str, str]:
    sent = {}
    if key is not None:
        sent["X-Service-Key"] = key
    if tenant is not None:
        sent["X-Tenant-ID"] = tenant
    return sent


def make_tenants(answerer=None, agent_deps_factory=None) -> Tenants:
    """Each tenant gets a collection of its own. Names carry a per-call prefix because the in-memory Chroma is shared by the whole test process."""
    run = uuid.uuid4().hex[:12]
    options = {}
    if answerer is not None:
        options["answerer"] = answerer
    if agent_deps_factory is not None:
        options["agent_deps_factory"] = agent_deps_factory
    return Tenants(lambda tenant: Service(VectorStore(collection=f"t{run}{tenant}"), **options))


def make_app(answerer=None, agent_deps_factory=None, tenants: Tenants | None = None):
    return create_app(tenants=tenants or make_tenants(answerer, agent_deps_factory), service_key=SERVICE_KEY)


def make_client(answerer=None, agent_deps_factory=None, tenant: str | None = TENANT_A, tenants: Tenants | None = None) -> TestClient:
    """A client that behaves like the .NET API: it sends the service key and the given tenant on every request."""
    return TestClient(make_app(answerer, agent_deps_factory, tenants), headers=headers(tenant))
