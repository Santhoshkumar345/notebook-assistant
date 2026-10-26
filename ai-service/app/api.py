"""HTTP layer over the service core.

Handlers are plain `def`, not `async def`, on purpose: everything they do is blocking (embedding inference, Chroma,
the Gemini SDK). FastAPI runs `def` handlers in a threadpool; the same blocking work inside `async def` would freeze
the event loop for every other request (see experiments/event_loop_blocking.py).

Not built yet: authentication between callers and this service, and per-user isolation of documents.
"""

import contextvars
import logging
import re
import uuid
from contextlib import asynccontextmanager
from pathlib import Path as FsPath
from typing import Literal

from fastapi import FastAPI, HTTPException, Path, Request
from fastapi.responses import JSONResponse, Response
from google.genai import errors as genai_errors
from pydantic import BaseModel, Field

from app.generation import Status
from app.service import Service
from app.store import VectorStore

logger = logging.getLogger("notebook.api")

NAME_PATTERN = r"^[A-Za-z0-9][A-Za-z0-9._ -]{0,98}\.md$"
MAX_DOC_CHARS = 200_000
DEFAULT_DATA_DIR = str(FsPath(__file__).resolve().parent.parent / "data" / "chroma")


class DocumentIn(BaseModel):
    name: str = Field(pattern=NAME_PATTERN, description="Markdown file name, e.g. notes.md")
    content: str = Field(min_length=1, max_length=MAX_DOC_CHARS)


class DocumentOut(BaseModel):
    name: str
    chunks: int


class AskIn(BaseModel):
    question: str = Field(min_length=3, max_length=1000)
    k: int = Field(default=4, ge=1, le=10, description="How many chunks to give the model")
    mode: Literal["pipeline", "agent"] = Field(default="pipeline", description="pipeline: always retrieve then answer. agent: route, then retrieve, summarize or clarify")


class SourceOut(BaseModel):
    id: str
    source: str
    heading: str
    snippet: str


class AskOut(BaseModel):
    # "not_found" and "uncited" are valid outcomes (no trustworthy answer), so they are HTTP 200.
    # For status "clarify" the answer field holds the clarifying question to put to the user.
    status: Literal["answered", "not_found", "uncited", "clarify"]
    answer: str
    sources: list[SourceOut]
    action: str = "retrieve"  # what the router chose: retrieve, summarize or clarify
    trace: list[str] = []     # the agent's steps, for debugging (empty in pipeline mode)


# One id per user action, sent by the .NET API in X-Request-ID and attached to every log line, so a single request can be
# traced across both services. An incoming value is accepted only if it is short and plain (so it cannot inject into logs).
REQUEST_ID = re.compile(r"[A-Za-z0-9._-]{1,64}")
request_id_var: contextvars.ContextVar[str] = contextvars.ContextVar("request_id", default="-")


def configure_logging() -> None:
    """Python drops INFO records unless a handler and level are set; without this the per-request trace never appears."""
    factory = logging.getLogRecordFactory()
    if not getattr(factory, "adds_request_id", False):
        def with_request_id(*args, **kwargs):
            record = factory(*args, **kwargs)
            record.request_id = request_id_var.get()
            return record

        with_request_id.adds_request_id = True
        logging.setLogRecordFactory(with_request_id)

    root = logging.getLogger("notebook")
    if not root.handlers:
        handler = logging.StreamHandler()
        handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(name)s [%(request_id)s] %(message)s"))
        root.addHandler(handler)
        root.setLevel(logging.INFO)


def create_app(service: Service | None = None, data_dir: str = DEFAULT_DATA_DIR) -> FastAPI:
    configure_logging()

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        # Heavy objects (embedding model, vector store, indexes) are built once at startup, never per request.
        app.state.service = service or Service(VectorStore(path=data_dir))
        yield

    app = FastAPI(title="Notebook Assistant - AI service", version="0.1.0", lifespan=lifespan)

    @app.middleware("http")
    async def request_id_middleware(request: Request, call_next):
        incoming = request.headers.get("x-request-id", "")
        request_id = incoming if REQUEST_ID.fullmatch(incoming) else uuid.uuid4().hex
        token = request_id_var.set(request_id)
        try:
            response = await call_next(request)
        finally:
            request_id_var.reset(token)
        response.headers["X-Request-ID"] = request_id
        return response

    @app.exception_handler(genai_errors.APIError)
    async def upstream_error(request: Request, exc: genai_errors.APIError):
        if exc.code in (429, 503):
            return JSONResponse({"detail": "The language model is busy or rate-limited; retry shortly."}, status_code=503, headers={"Retry-After": "10"})
        logger.error("upstream model error code=%s", exc.code)  # details stay in the log, not in the response
        return JSONResponse({"detail": "The language model returned an error."}, status_code=502)

    def svc(request: Request) -> Service:
        return request.app.state.service

    @app.get("/health")
    def health(request: Request):
        s = svc(request)
        return {"status": "ok", "documents": len(s.documents()), "chunks": s.chunk_count()}

    @app.get("/documents")
    def list_documents(request: Request) -> dict[str, int]:
        return svc(request).documents()

    @app.post("/documents", response_model=DocumentOut, status_code=201)
    def add_document(body: DocumentIn, request: Request):
        return DocumentOut(name=body.name, chunks=svc(request).ingest(body.name, body.content))

    @app.delete("/documents/{name}", status_code=204)
    def delete_document(request: Request, name: str = Path(pattern=NAME_PATTERN)):
        if svc(request).delete(name) == 0:
            raise HTTPException(status_code=404, detail="No such document")
        return Response(status_code=204)

    @app.post("/ask", response_model=AskOut)
    def ask(body: AskIn, request: Request):
        if body.mode == "agent":
            r = svc(request).ask_agent(body.question, body.k)
            status, answer, sources, action, trace, detail = r.status, r.answer, r.sources, r.action, r.trace, "; ".join(r.trace[-1:])
        else:
            r = svc(request).ask(body.question, body.k)
            status, answer, sources, action, trace, detail = r.status.value, r.answer, r.sources, "retrieve", [], r.detail
        if status == "error":
            raise HTTPException(status_code=502, detail=f"The model returned no usable output ({detail}).")
        return AskOut(
            status=status, answer=answer, action=action, trace=trace,
            sources=[SourceOut(id=s.id, source=s.source, heading=s.heading, snippet=s.body[:300]) for s in sources],
        )

    return app


app = create_app()  # uvicorn app.api:app  (nothing heavy happens until startup)
