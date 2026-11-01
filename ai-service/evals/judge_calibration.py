"""Hand-labelled answers for checking the judge before trusting it.

An LLM judge is a measuring instrument, and an instrument is checked against something known before its readings mean anything. Each source
below has two answers: one the source supports and one that is wrong in the way real generation errors are wrong (a changed number, two
values swapped, a flipped negation, a claim the source never made, a contradiction). The set is balanced, so a judge that approves everything
scores 50%, which is the point: agreeing with the answers is not skill.

It is small (14 cases), so it can only catch a judge that is clearly bad, not certify one that is good.
"""

from dataclasses import dataclass


@dataclass(frozen=True)
class JudgeCase:
    id: str
    sources: str
    answer: str
    supported: bool


_EMBED = "The embedding model truncates its input at 256 tokens."
_TEMP = "At temperature zero the two calls returned different outputs. The cause was floating-point non-associativity."
_CHUNK = "Chunking variant B kept 81% coverage. Chunking variant C kept 88% coverage."
_KEY = "The service refuses to start without a service key."
_HYBRID = "Retrieval uses dense and BM25 keyword search, fused with reciprocal rank fusion."
_STREAM = "The project does not use streaming: an answer appears all at once."
_RECALL = "Approximate search at the lowest setting had a recall of 0.922 against exact search."
_STORE = "Chroma stores the vectors."

CASES = (
    JudgeCase("embed-ok", _EMBED, "It truncates at 256 tokens.", True),
    JudgeCase("embed-number", _EMBED, "It truncates at 512 tokens.", False),
    JudgeCase("temp-ok", _TEMP, "Floating-point non-associativity made identical calls differ.", True),
    JudgeCase("temp-cause", _TEMP, "Network latency made identical calls differ.", False),
    JudgeCase("chunk-ok", _CHUNK, "B kept 81% and C kept 88%.", True),
    JudgeCase("chunk-swap", _CHUNK, "B kept 88% and C kept 81%.", False),
    JudgeCase("key-ok", _KEY, "It will not start without a key.", True),
    JudgeCase("key-contradiction", _KEY, "It starts with a default key if none is set.", False),
    JudgeCase("hybrid-ok", _HYBRID, "Dense and keyword search are combined by reciprocal rank fusion.", True),
    JudgeCase("hybrid-addition", _HYBRID, "Dense and keyword search are combined by reciprocal rank fusion and then reranked by a cross-encoder.", False),
    JudgeCase("stream-ok", _STREAM, "Answers are not streamed.", True),
    JudgeCase("stream-negation", _STREAM, "Answers stream token by token.", False),
    JudgeCase("recall-ok", _RECALL, "The recall was about 92%.", True),
    JudgeCase("store-outside-knowledge", _STORE, "Chroma, which is written in Rust, stores the vectors.", False),
)
