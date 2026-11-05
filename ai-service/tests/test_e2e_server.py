"""The stand-in model used by the end-to-end tests must behave exactly as its docstring says, or those tests prove nothing."""

import pytest
from google.genai import errors as genai_errors

import e2e_server
from app.agent import Action
from app.generation import Status
from app.store import Hit
from tests import support

HIT = Hit(source="plans.md", heading="Plans", body="The launch codename is   Zephyrwood.\n\nNobody else may learn it.", score=0.9, id="plans.md#0")


def test_an_ordinary_question_gets_an_answer_quoting_the_top_passage_and_the_question():
    result = e2e_server.fake_answerer("What is the codename?", [HIT])

    assert result.status is Status.answered
    assert result.sources == [HIT]
    assert result.answer == "From plans.md: The launch codename is Zephyrwood. Nobody else may learn it. [asked: What is the codename?]"


def test_nothing_retrieved_is_not_found():
    assert e2e_server.fake_answerer("What is the codename?", []).status is Status.not_found


def test_the_keywords_select_the_other_outcomes():
    assert e2e_server.fake_answerer("an unanswerable question", [HIT]).status is Status.not_found
    uncited = e2e_server.fake_answerer("an unsupported claim", [HIT])
    assert uncited.status is Status.uncited and uncited.sources == []


def test_a_flaky_question_is_rate_limited_once_per_distinct_text_then_fine():
    e2e_server._seen_flaky.clear()

    with pytest.raises(genai_errors.ClientError) as error:
        e2e_server.fake_answerer("a flaky question", [HIT])
    assert error.value.code == 429

    assert e2e_server.fake_answerer("a flaky question", [HIT]).status is Status.answered  # the retry works
    with pytest.raises(genai_errors.ClientError):
        e2e_server.fake_answerer("another flaky question", [HIT])  # a different text fails once too


def test_a_slow_question_takes_its_time(monkeypatch):
    slept = []
    monkeypatch.setattr(e2e_server.time, "sleep", slept.append)

    e2e_server.fake_answerer("a slow question", [HIT])

    assert slept == [e2e_server.SLOW_SECONDS]


def test_the_router_clarifies_only_a_question_that_ends_in_vague():
    assert e2e_server.fake_route("What about that vague?", ["a.md"]).action is Action.clarify
    assert e2e_server.fake_route("What about that vague? the budget", ["a.md"]).action is Action.retrieve  # once answered, the combined question searches
    assert e2e_server.fake_route("Summarize the plans", ["plans.md"]).document == "plans.md"
    assert e2e_server.fake_route("Summarize the plans", []).action is Action.retrieve


def test_the_fake_runs_inside_the_real_service_with_real_retrieval_and_isolated_tenants():
    tenants = support.make_tenants(answerer=e2e_server.fake_answerer, agent_deps_factory=e2e_server.fake_agent_deps)
    with support.make_client(tenant=support.TENANT_A, tenants=tenants) as alice, support.make_client(tenant=support.TENANT_B, tenants=tenants) as bob:
        alice.post("/documents", json={"name": "plans.md", "content": "# Plans\n\n## Codename\n\nThe launch codename is Zephyrwood."})
        bob.post("/documents", json={"name": "plans.md", "content": "# Plans\n\n## Codename\n\nBob only plans a picnic."})

        a = alice.post("/ask", json={"question": "What is the launch codename?"}).json()
        b = bob.post("/ask", json={"question": "What is the launch codename?"}).json()
        clarify = alice.post("/ask", json={"question": "What about that vague?", "mode": "agent"}).json()

    assert a["status"] == "answered" and "Zephyrwood" in a["answer"]
    assert b["status"] == "answered" and "picnic" in b["answer"] and "Zephyrwood" not in b["answer"]
    assert clarify["status"] == "clarify" and clarify["answer"] == "Which topic do you mean?"


# ----------------------------------------------------------------- follow-up questions

def test_a_follow_up_starting_with_and_is_joined_to_the_last_thing_the_person_asked():
    from app.condense import Turn

    history = [Turn("user", "What is the launch codename?"), Turn("assistant", "From plans.md: ..."), Turn("user", "Who made it?"), Turn("assistant", "From plans.md: ...")]

    assert e2e_server.fake_condenser("and who may learn it?", history) == "Who made it? and who may learn it?"


def test_any_other_question_is_left_exactly_as_typed_even_in_a_conversation():
    from app.condense import Turn

    history = [Turn("user", "What is the launch codename?"), Turn("assistant", "From plans.md: ...")]

    assert e2e_server.fake_condenser("What is the launch budget?", history) == "What is the launch budget?"


def test_the_stand_in_service_uses_it_so_a_browser_test_sees_what_reached_the_service(tmp_path):
    tenants = e2e_server.build_tenants(str(tmp_path))
    with support.make_client(tenants=tenants) as client:
        client.post("/documents", json={"name": "plans.md", "content": "# Plans\n\n## Codename\n\nThe launch codename is Zephyrwood and only Alice may learn it."})

        reply = client.post("/ask", json={"question": "and who may learn it?", "history": [{"role": "user", "text": "What is the launch codename?"}, {"role": "assistant", "text": "Zephyrwood."}]})

    body = reply.json()
    assert body["searched_for"] == "What is the launch codename? and who may learn it?"
    assert "[asked: What is the launch codename? and who may learn it?]" in body["answer"]
