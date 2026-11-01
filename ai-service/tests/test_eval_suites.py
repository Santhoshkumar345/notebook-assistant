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
    assert kinds == ({"attack"} if suite.name == "redteam" else {"answerable", "unanswerable"})


def test_the_fingerprint_is_stable_between_two_builds(suite):
    assert suite.fingerprint() == suites.SUITES[suite.name]().fingerprint()


def test_the_sizes_are_what_the_documentation_says():
    assert [len(suites.SUITES[n]().cases) for n in ("basic", "hard", "redteam")] == [25, 26, 16]


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
