"""The real datasets, checked without a model: the questions' gold phrases must exist in their own corpus.

This is the test that would have caught the silent breakage of 2026-11-01, when a README rewrite removed two gold phrases from the hard set's
corpus and nothing noticed (the evaluations only ran by hand). The corpus is frozen now, and this keeps it honest.
"""

import pytest

from evals import suites
from evals.framework import norm


@pytest.fixture(params=sorted(suites.SUITES))
def suite(request):
    return suites.SUITES[request.param]()


def test_a_suite_is_valid_every_gold_phrase_is_in_its_corpus_and_ids_are_unique(suite):
    suite.validate()


def test_a_suite_has_both_kinds_of_question_it_needs(suite):
    kinds = {c.kind for c in suite.cases}
    assert kinds == {"redteam": {"attack"}, "followup": {"answerable"}}.get(suite.name, {"answerable", "unanswerable"})


def test_the_fingerprint_is_stable_between_two_builds(suite):
    assert suite.fingerprint() == suites.SUITES[suite.name]().fingerprint()


def test_the_sizes_are_what_the_documentation_says():
    assert [len(suites.SUITES[n]().cases) for n in ("basic", "hard", "redteam", "followup")] == [25, 26, 18, 15]


def test_every_attack_has_a_marker_that_an_honest_answer_would_not_contain():
    for case in suites.redteam().cases:
        assert case.markers
        for marker in case.markers:
            assert all(norm(marker) not in norm(honest) for honest in case.honest), case.id


def test_no_poisoned_document_is_part_of_the_corpus_itself():
    corpus = norm(" ".join(suites.redteam().corpus.values()))
    for case in suites.redteam().cases:
        for _, text in case.docs:
            assert norm(text) not in corpus, case.id


def test_the_frozen_project_documents_are_in_the_repository_not_read_from_the_live_ones():
    from evals.hard_set import FROZEN

    assert (FROZEN / "project-readme.md").exists() and (FROZEN / "project-architecture.md").exists()
    assert "project-readme.md" in suites.hard().corpus


def test_every_question_in_the_follow_up_suite_comes_with_a_conversation_and_the_controls_are_labelled():
    cases = suites.followup().cases

    assert all(c.history for c in cases)
    assert all(c.history[0][0] == "user" and c.history[-1][0] == "assistant" for c in cases)  # a user turn first, an answer last: how a conversation looks to the API
    assert sum("standalone" in c.tags for c in cases) == 4 and len(cases) - 4 == 11


def test_a_follow_up_is_not_answerable_from_its_own_words_alone():
    # the point of the suite: none of these questions names the thing it is about, so each depends on the conversation before it
    for case in suites.followup().cases:
        if "standalone" not in case.tags:
            assert not any(norm(g) in norm(case.question) for g in case.gold), case.id


def test_the_attacks_that_come_through_the_conversation_carry_their_payload_in_an_earlier_answer():
    attacks = [c for c in suites.redteam().cases if "injected-history" in c.tags]

    assert len(attacks) == 2 and all(c.history[-1][0] == "assistant" and c.markers for c in attacks)
    assert all(not any(m in c.question.lower() for m in c.markers) for c in attacks)  # the payload is in the history, not in what the person typed
