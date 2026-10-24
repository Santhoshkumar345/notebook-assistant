from fastapi.testclient import TestClient

from app.agent import Action, AgentDeps, Route, build_agent
from app.api import create_app
from app.generation import RagResult, Status
from app.service import Service
from app.store import Hit, VectorStore

DOCS = ["alpha.md", "beta.md"]


def hit(i, source="alpha.md", body="text"):
    return Hit(source, f"Section {i}", body, 0.5, f"{source}#{i}")


class Spy:
    """Fake dependencies that record how they were called."""

    def __init__(self, route=None, answers=None, retrieved=None, rewrite="better query"):
        self.route_result = route if route is not None else Route(action=Action.retrieve)
        self.answers = list(answers or [RagResult(Status.answered, "ok", [hit(0)])])
        self.retrieved = list(retrieved or [[hit(0), hit(1)]])
        self.rewrite_result = rewrite
        self.calls = {"route": [], "retrieve": [], "answer": [], "rewrite": [], "summarize": [], "chunks_of": []}

    def deps(self) -> AgentDeps:
        def route(question, documents):
            self.calls["route"].append((question, documents))
            return self.route_result

        def retrieve(query, k):
            self.calls["retrieve"].append(query)
            return self.retrieved.pop(0) if len(self.retrieved) > 1 else self.retrieved[0]

        def answer(question, hits):
            self.calls["answer"].append([h.id for h in hits])
            return self.answers.pop(0) if len(self.answers) > 1 else self.answers[0]

        def rewrite(question):
            self.calls["rewrite"].append(question)
            return self.rewrite_result

        def summarize(document, chunks):
            self.calls["summarize"].append(document)
            return RagResult(Status.answered, "a summary", chunks)

        def chunks_of(document):
            self.calls["chunks_of"].append(document)
            return [hit(0, document), hit(1, document)]

        return AgentDeps(route, rewrite, retrieve, answer, summarize, chunks_of, lambda: DOCS)


def test_retrieve_route_runs_retrieve_then_answer_with_two_model_calls():
    spy = Spy()
    r = build_agent(spy.deps())("what is x?")
    assert (r.status, r.action, r.model_calls) == ("answered", "retrieve", 2)
    assert len(spy.calls["retrieve"]) == 1 and len(spy.calls["answer"]) == 1 and spy.calls["rewrite"] == []


def test_the_router_sees_only_the_question_and_document_names():
    spy = Spy()
    build_agent(spy.deps())("what is x?")
    assert spy.calls["route"] == [("what is x?", DOCS)]  # never any chunk text


def test_clarify_asks_the_user_and_never_retrieves_or_answers():
    spy = Spy(route=Route(action=Action.clarify, clarifying_question="Which limit do you mean?"))
    r = build_agent(spy.deps())("what was the limit?")
    assert (r.status, r.action, r.answer, r.model_calls) == ("clarify", "clarify", "Which limit do you mean?", 1)
    assert spy.calls["retrieve"] == [] and spy.calls["answer"] == []


def test_summarize_reads_the_named_document_and_skips_retrieval():
    spy = Spy(route=Route(action=Action.summarize, document="beta.md"))
    r = build_agent(spy.deps())("summarize beta")
    assert (r.status, r.action, r.answer) == ("answered", "summarize", "a summary")
    assert spy.calls["chunks_of"] == ["beta.md"] and spy.calls["retrieve"] == []
    assert [s.source for s in r.sources] == ["beta.md", "beta.md"]


def test_summarizing_an_unknown_document_falls_back_to_retrieve():
    spy = Spy(route=Route(action=Action.summarize, document="nope.md"))
    r = build_agent(spy.deps())("summarize nope")
    assert r.action == "retrieve" and spy.calls["chunks_of"] == [] and len(spy.calls["retrieve"]) == 1
    assert any("unknown document" in line for line in r.trace)


def test_clarify_without_a_question_falls_back_to_retrieve():
    spy = Spy(route=Route(action=Action.clarify, clarifying_question="  "))
    assert build_agent(spy.deps())("vague").action == "retrieve"


def test_a_router_that_returns_nothing_defaults_to_retrieve():
    class NoRoute(Spy):
        def deps(self):
            d = super().deps()
            return AgentDeps(lambda q, docs: None, d.rewrite, d.retrieve, d.answer, d.summarize, d.chunks_of, d.documents)

    r = build_agent(NoRoute().deps())("anything")
    assert r.action == "retrieve" and r.status == "answered" and any("defaulting to retrieve" in t for t in r.trace)


def test_not_found_triggers_exactly_one_rewrite_and_a_merged_second_retrieval():
    spy = Spy(answers=[RagResult(Status.not_found, "", []), RagResult(Status.answered, "found it", [hit(5)])],
              retrieved=[[hit(0), hit(1)], [hit(1), hit(5)]], rewrite="synonym query")
    r = build_agent(spy.deps())("original question")
    assert r.status == "answered" and r.answer == "found it"
    assert spy.calls["rewrite"] == ["original question"] and spy.calls["retrieve"] == ["original question", "synonym query"]
    assert spy.calls["answer"][1] == ["alpha.md#0", "alpha.md#1", "alpha.md#5"]  # union, no duplicate of #1
    assert r.model_calls == 4  # router + first answer + rewrite + second answer; retrieval is not a model call


def test_the_retry_is_bounded_it_never_loops():
    spy = Spy(answers=[RagResult(Status.not_found, "", [])])  # always not_found
    r = build_agent(spy.deps())("unanswerable")
    assert r.status == "not_found" and len(spy.calls["rewrite"]) == 1 and len(spy.calls["answer"]) == 2


def test_an_error_result_is_not_retried():
    spy = Spy(answers=[RagResult(Status.error, "", [], "finish_reason=MAX_TOKENS")])
    r = build_agent(spy.deps())("q")
    assert r.status == "error" and spy.calls["rewrite"] == []


def test_the_merged_context_is_capped():
    many = [hit(i) for i in range(10)]
    spy = Spy(answers=[RagResult(Status.not_found, "", []), RagResult(Status.answered, "ok", [hit(0)])], retrieved=[many[:5], many[5:]])
    build_agent(spy.deps(), max_context=6)("q")
    assert len(spy.calls["answer"][1]) == 6


def test_an_empty_context_costs_no_answer_model_call():
    spy = Spy(answers=[RagResult(Status.not_found, "", [])], retrieved=[[]])
    r = build_agent(spy.deps(), max_rewrites=0)("q")
    assert r.model_calls == 1  # only the router; with no chunks the answerer is not called as a model


def test_the_api_exposes_the_agent_with_action_and_trace():
    spy = Spy(route=Route(action=Action.clarify, clarifying_question="Which one?"))
    store = VectorStore(collection="agent_api_tests")
    store.clear()
    service = Service(store, agent_deps_factory=lambda retrieve, st, answer: spy.deps())
    with TestClient(create_app(service=service)) as client:
        reply = client.post("/ask", json={"question": "what was the limit?", "mode": "agent"})
        plain = client.post("/ask", json={"question": "what was the limit?"})
    body = reply.json()
    assert reply.status_code == 200 and body["status"] == "clarify" and body["answer"] == "Which one?"
    assert body["action"] == "clarify" and body["trace"] == ["route: clarify"]
    assert plain.json()["action"] == "retrieve" and plain.json()["trace"] == []  # pipeline mode is unchanged
