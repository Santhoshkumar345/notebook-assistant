"""One independent Service per tenant (a tenant is one user of the .NET API).

Isolation is physical: every tenant has its own Chroma collection and its own keyword index, so a query made for one tenant cannot
return another's passage whatever the question says. A metadata filter on a shared collection would put one forgotten `where` between
a user and everyone else's documents; separate collections leave nothing to forget.

A tenant id is the user's id as 32 lowercase hex characters. It becomes part of a collection name, so it is validated before it is
used for anything.
"""

import re
import threading
from typing import Callable

import chromadb

from app.service import Service
from app.store import VectorStore

TENANT_ID = re.compile(r"[0-9a-f]{32}")  # fullmatch: no trailing newline, no prefix, no path characters


class InvalidTenant(ValueError):
    pass


class Tenants:
    """Builds a tenant's service the first time it is needed and hands back the same one afterwards."""

    def __init__(self, make_service: Callable[[str], Service]):
        self._make = make_service
        self._services: dict[str, Service] = {}
        self._lock = threading.Lock()

    def get(self, tenant_id: str) -> Service:
        if not TENANT_ID.fullmatch(tenant_id):
            raise InvalidTenant("A tenant id is 32 lowercase hex characters.")
        service = self._services.get(tenant_id)
        if service is None:
            with self._lock:
                service = self._services.get(tenant_id)  # another request may have built it while this one waited
                if service is None:
                    service = self._services[tenant_id] = self._make(tenant_id)
        return service


def default_tenants(data_dir: str | None = None) -> Tenants:
    """Tenants backed by one Chroma client: persistent in `data_dir`, or in memory when it is None. Collections are named tenant_<id>."""
    client = chromadb.PersistentClient(path=data_dir) if data_dir else chromadb.EphemeralClient()
    return Tenants(lambda tenant_id: Service(VectorStore(client=client, collection=f"tenant_{tenant_id}")))
