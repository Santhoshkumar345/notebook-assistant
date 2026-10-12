import re
from pathlib import Path

from app.chunking import chunk_markdown, split_sections, split_to_budget

words = lambda s: len(s.split())  # a cheap stand-in tokenizer for unit tests

DOC = """# Guide

Intro text.

## Setup

Install it.

### Details

Fine print here.

## Usage

Run it.
"""


def test_sections_track_the_heading_path():
    headings = [h for h, _ in split_sections(DOC)]
    assert headings == ["Guide", "Guide > Setup", "Guide > Setup > Details", "Guide > Usage"]


def test_heading_with_nothing_under_it_makes_no_chunk_but_stays_in_the_path():
    md = "# Title\n\n## Part\n\nReal content.\n"
    assert split_sections(md) == [("Title > Part", "## Part\n\nReal content.")]


def test_headings_inside_code_fences_are_ignored():
    md = "# Title\n\n```python\n# just a comment\nx = 1\n```\n\nafter\n"
    sections = split_sections(md)
    assert [h for h, _ in sections] == ["Title"]
    assert "# just a comment" in sections[0][1]


def test_split_to_budget_respects_the_budget():
    text = ("Alpha beta gamma delta. " * 40) + "\n\n" + ("one two three four five. " * 30)
    pieces = split_to_budget(text, 25, words)
    assert all(words(p) <= 25 for p in pieces)


def test_split_to_budget_loses_no_words():
    text = "First sentence here. Second one follows.\n\nNew paragraph with more words in it. " * 12
    pieces = split_to_budget(text, 10, words)
    assert re.findall(r"\w+", " ".join(pieces)) == re.findall(r"\w+", text)


def test_a_single_unbreakable_token_is_still_cut_to_fit():
    pieces = split_to_budget("x" * 500, 8, lambda s: max(1, len(s) // 10))
    assert len(pieces) > 1 and all(len(p) // 10 <= 8 for p in pieces)


def test_chunks_carry_a_context_prefix_and_fit_the_budget():
    long_doc = "# Notes\n\n## Topic\n\n" + ("A fairly ordinary sentence about the topic. " * 60)
    chunks = chunk_markdown("notes.md", long_doc, words, max_tokens=50)
    assert len(chunks) > 1
    assert all(c.text.startswith("[notes.md > Notes > Topic]\n") for c in chunks)
    assert all(words(c.text) <= 50 for c in chunks)


def test_every_sample_doc_chunk_fits_the_real_embedding_limit():
    """Integration test with the real tokenizer: no chunk may exceed the model's input limit."""
    from app import embeddings

    docs = sorted((Path(__file__).resolve().parent.parent / "sample_docs").glob("*.md"))
    assert docs, "sample docs missing"
    for doc in docs:
        for chunk in chunk_markdown(doc.name, doc.read_text(encoding="utf-8"), embeddings.count_tokens, embeddings.max_tokens()):
            assert embeddings.count_tokens(chunk.text) <= embeddings.max_tokens(), chunk.heading
