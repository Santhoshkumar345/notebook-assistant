"""Structure-aware, token-budgeted markdown chunking.

Sections come from markdown headings; sections that are too long are split on paragraph, line,
sentence and word boundaries, in that order. The budget is in TOKENS of the embedding model and
includes the context prefix, so a chunk can never exceed the model's input limit.
"""

import re
from dataclasses import dataclass
from typing import Callable

CountTokens = Callable[[str], int]
SEPARATORS = ("\n\n", "\n", ". ", " ")
MIN_BUDGET = 20


@dataclass(frozen=True)
class Chunk:
    source: str
    heading: str  # heading path, e.g. "Week 4 - Transformers > Tokenization"
    body: str     # the passage shown to users and given to the LLM
    text: str     # what gets embedded: context prefix + body


def split_sections(markdown: str) -> list[tuple[str, str]]:
    """Return (heading_path, body) per section. Headings inside ``` code fences are ignored.

    A section with nothing under its heading (a title directly followed by a subheading) produces no
    section of its own: it would become a near-empty chunk that only adds noise to retrieval. Its heading
    still appears in the path of the sections beneath it.
    """
    sections: list[tuple[str, str]] = []
    path: list[tuple[int, str]] = []
    buf: list[str] = []
    in_fence = False
    has_content = False

    def flush() -> None:
        nonlocal has_content
        body = "\n".join(buf).strip()
        if body and has_content:
            sections.append((" > ".join(h for _, h in path), body))
        buf.clear()
        has_content = False

    for line in markdown.splitlines():
        if line.lstrip().startswith("```"):
            in_fence = not in_fence
        match = None if in_fence else re.match(r"^(#{1,6})\s+(.*)", line)
        if match:
            flush()
            level = len(match.group(1))
            path[:] = [(lv, h) for lv, h in path if lv < level] + [(level, match.group(2).strip())]
        elif line.strip():
            has_content = True
        buf.append(line)
    flush()
    return sections


def split_to_budget(text: str, budget: int, count: CountTokens, seps: tuple[str, ...] = SEPARATORS) -> list[str]:
    """Split text into pieces of at most `budget` tokens, preferring the coarsest boundary available."""
    if count(text) <= budget:
        return [text]

    sep = next((s for s in seps if s in text), None)
    if sep is None:  # no boundary left (e.g. one enormous token): cut by characters
        step = max(1, len(text) * budget // max(count(text), 1) - 1)
        return [p for i in range(0, len(text), step) for p in split_to_budget(text[i:i + step], budget, count, ())]

    finer = seps[seps.index(sep) + 1:]
    parts, join = text.split(sep), sep
    if sep == ". ":  # keep the full stop that split() removed
        parts, join = [p + "." for p in parts[:-1]] + parts[-1:], " "

    pieces: list[str] = []
    current = ""
    for part in parts:
        candidate = f"{current}{join}{part}" if current else part
        if count(candidate) <= budget:
            current = candidate
            continue
        if current:
            pieces.append(current)
            current = ""
        if count(part) > budget:
            pieces.extend(split_to_budget(part, budget, count, finer))
        else:
            current = part
    if current:
        pieces.append(current)
    return pieces


def chunk_markdown(source: str, markdown: str, count: CountTokens, max_tokens: int, margin: int = 4) -> list[Chunk]:
    """Chunk one markdown document. `margin` covers tokenizer effects where prefix and body meet."""
    chunks: list[Chunk] = []
    for heading, section in split_sections(markdown):
        prefix = f"[{source} > {heading}]\n" if heading else f"[{source}]\n"
        budget = max_tokens - count(prefix) - margin
        if budget < MIN_BUDGET:
            raise ValueError(f"heading path too long for a {max_tokens}-token limit: {heading!r}")
        for body in split_to_budget(section, budget, count):
            body = body.strip()
            if body:
                chunks.append(Chunk(source, heading, body, prefix + body))
    return chunks
