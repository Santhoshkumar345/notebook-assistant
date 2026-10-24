"""Questions for evaluating the agent layer against the plain pipeline.

Beyond the existing hard set, the agent is only worth its extra model calls where a fixed pipeline fails:
questions that are too vague to search, requests to summarize a whole document, and questions that need facts from
more than one document. SHORT_CLEAR guards the opposite risk: an agent that asks for clarification when it should not.
"""

from dataclasses import dataclass

from evals.hard_set import Question

AMBIGUOUS = [  # refer to something with no way to tell what; the right behavior is to ask
    "What was the limit?",
    "How many did it use?",
    "Why did that fail?",
    "Is it better?",
    "What does it cost?",
    "Which one should I use?",
]

SHORT_CLEAR = [  # short, but answerable: must NOT trigger a clarifying question
    Question("Which vector database does the project use?", ("Chroma",), "short-clear"),
    Question("What is Reciprocal Rank Fusion used for?", ("Reciprocal Rank Fusion",), "short-clear"),
    Question("Which generator model is pinned?", ("gemini-3.5-flash-lite",), "short-clear"),
    Question("How many tokens did the Tamil sentence take?", ("48 tokens",), "short-clear"),
]

MULTI_DOC = [  # every gold phrase must be present in the cited chunks together; the phrases live in different documents
    Question("Which Gemini model does the project pin, and why did the Week 5 notes call a -latest alias risky?", ("gemini-3.5-flash-lite", "survive deprecations"), "multi-doc"),
    Question("What token limit does the architecture give for the embedding model, and what did the Tamil experiment show about token counts?", ("48 tokens", "256-token limit"), "multi-doc"),
    Question("What did the injection experiments show about delimiters, and how does the project stop a document closing the sources tag?", ("held 5/5", "angle brackets escaped"), "multi-doc"),
    Question("What did Week 3 say real training uses instead of one learning rate, and what did Week 4 say about the sqrt(d) scaling?", ("adaptive optimizers", "LARGE-magnitude regime"), "multi-doc"),
    Question("How does the service react to a rate-limited model, and what did Week 5 say about whether streaming helps with rate limits?", ("Retry-After", "Streaming does NOT help"), "multi-doc"),
]


@dataclass(frozen=True)
class SummaryRequest:
    text: str
    document: str
    key_terms: tuple[str, ...]  # a good summary mentions at least two of these


SUMMARIES = [
    SummaryRequest("Summarize week-03.md for me.", "week-03.md", ("gradient descent", "overfitting", "embedding", "cosine")),
    SummaryRequest("Give me an overview of the week 6 notes.", "week-06.md", ("few-shot", "hallucination", "injection", "chain-of-thought")),
    SummaryRequest("What is in week-02.md? Summarize it.", "week-02.md", ("async", "pydantic", "pandas", "numpy")),
    SummaryRequest("Summarize the project architecture document.", "project-architecture.md", ("chunking", "hybrid", "fastapi", "pinned")),
]
