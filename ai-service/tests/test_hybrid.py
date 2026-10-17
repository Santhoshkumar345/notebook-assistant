from app.hybrid import BM25Index, reciprocal_rank_fusion, tokenize


def test_tokenize_keeps_identifiers_whole():
    assert tokenize("Set ef_search=50, then max_output_tokens!") == ["set", "ef_search", "50", "then", "max_output_tokens"]


def test_bm25_finds_an_exact_rare_term():
    texts = ["the cat sat on the mat", "set ef_search to 50 for better recall", "dogs and cats are pets"]
    assert BM25Index(texts).top("ef_search", 3) == [1]


def test_bm25_never_returns_texts_with_no_overlap():
    assert BM25Index(["alpha beta", "gamma delta"]).top("zzz", 5) == []


def test_rrf_puts_an_item_found_by_both_retrievers_above_items_found_by_one():
    fused = reciprocal_rank_fusion([["a", "b"], ["b", "c"]])
    assert [item for item, _ in fused][0] == "b"  # 1/62 + 1/61 beats the 1/61 of "a" or "c"
    assert {item for item, _ in fused} == {"a", "b", "c"}


def test_rrf_scores_are_rank_based_not_score_based():
    # an item that is top of one ranking and absent from the other still gets 1/(k+1)
    fused = dict(reciprocal_rank_fusion([["x"], ["y"]], k=60))
    assert fused["x"] == fused["y"] == 1 / 61
