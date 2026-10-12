# Week 4 — Transformer Architecture

## The big picture (covered after initially jumping straight to components — good catch, fixed the ordering)
A transformer ("Attention Is All You Need," 2017) processes a whole sequence in parallel via stacked blocks of self-attention + feedforward, replacing RNNs because RNNs are sequential (can't parallelize training, step N needs step N-1) and lose long-range information over long sequences (vanishing gradients, compounded).

Pipeline: text -> **tokenization** -> **embedding lookup** -> **positional encoding** (attention has no inherent sense of order, unlike an RNN — position must be explicitly injected) -> stack of transformer blocks (**multi-head self-attention** + **feedforward network** + **residual connections/layer norm**, repeated dozens of times) -> output layer (linear + softmax over vocabulary, predicting one next token at a time).

Encoder-only (BERT, understanding tasks) vs. decoder-only (GPT/Gemini/Llama-style chat models, generation) vs. original encoder-decoder (translation). Modern LLMs are decoder-only.

## Attention — built from scratch on real embeddings
- **What/why:** self-attention lets each token's representation become a weighted blend of every other token's representation, weighted by similarity — built and verified with real `sentence-transformers` embeddings from Week 3 (`scripts/week4_day1_attention.py`).
- **Real bug caught and fixed:** first version applied `/sqrt(d)` scaling to already unit-normalized (cosine-similarity-scale) vectors, which over-compressed a meaningful signal (0.788 vs 0.253 similarity) into near-uniform attention weights (0.255 vs 0.246). Root cause: `/sqrt(d)` scaling is calibrated for the LARGE-magnitude regime real learned Q/K projections produce (~unit variance per dimension, dot products ~O(d)), not for small, pre-normalized cosine-similarity-scale vectors. Fixed by simulating the realistic regime (`X * sqrt(d)`) to correctly demonstrate the actual saturation problem `/sqrt(d)` solves — verify against real numbers, don't trust a formula's applicability without checking the scale it assumes.
- **Self-attention-to-self:** in the simplified demo (Q=K=V=same embedding), a token attending most to itself is mathematically guaranteed (Cauchy-Schwarz — nothing beats a unit vector's similarity with itself). In a real transformer with separate learned Q/K projections, this is only an empirical tendency, not a guarantee — an important distinction between the simplified demo and the real mechanism.
- **Multi-head attention:** real transformers run several attention computations in parallel with independent learned Q/K/V projections per head, so different heads can specialize (nearby-word relationships vs. long-range references), then concatenate and combine — our demo showed one simplified head only.

## Tokenization — real BPE via tiktoken
- **What/why:** BPE is statistical, not linguistic — it merges whatever byte/character sequences appeared frequently enough in training data, not morphemes/prefixes/suffixes. `"unbelievability"` splitting into `unbelie`/`v`/`ability` isn't a bug, it's literally how the merge vocabulary was built — optimized for compression of frequent patterns, not matching human language structure.
- **Real, striking finding:** multilingual token bloat — Tamil (4 words) took **48 tokens** (~12/word), Hindi (4 words) took **17 tokens** (~4/word), vs. English's ~1.2 tokens/word. Root cause: BPE operates on raw UTF-8 *bytes*; Tamil/Hindi characters need 3 bytes each, and since the tokenizer's merge rules were learned overwhelmingly from English text, those multi-byte sequences rarely got merged into single clean tokens — visible directly in the decoded output as `<0xef><0xbf><0xbd>`-style fragments (partial character byte sequences, not valid standalone characters).
- **Practical implication:** real cost and context-budget impact for multilingual users — same meaning costs meaningfully more tokens in non-English languages with most current tokenizers.

## Context window
- Cost scales quadratically with token count (attention compares every token to every other token).
- Why not just use a giant context instead of RAG: (1) cost/latency from quadratic scaling, (2) "lost in the middle" — models empirically underuse information buried mid-context even when technically within the window, (3) refreshing/re-sending a huge context on every query is wasteful vs. indexing once and retrieving cheaply per query.
- Confirmed via four-angle answer: a bigger context window is not a guarantee of better RAG retrieval — a well-curated 4k context can outperform a sloppy 128k dump. Retrieval quality depends more on chunking/ranking/filtering than raw window size.

## Parameters
Every tunable `w`/`b` in the network, at massive scale (Week 3's 2-parameter toy example, scaled to billions). Directly drives compute/memory cost — the reason a 3B local model was the right choice for an 8GB-RAM machine, and why frontier models require API access rather than local hardware.

## Base vs. instruction-tuned vs. chat models
- Base: next-token prediction over raw text, will continue a prompt in whatever statistically likely direction, not necessarily "answer the question."
- Instruction-tuned: further trained on (instruction, ideal response) pairs to reliably follow instructions.
- Chat: instruction-tuned + multi-turn conversation format (system/user/assistant roles) + typically RLHF/preference tuning. Everything used so far (Gemini, `llama3.2:3b`) is a chat model.

## Fine-tuning vs. prompting vs. RAG
- Fine-tuning: updates weights, shapes durable style/behavior/format — NOT a reliable way to inject verifiable, changeable facts.
- Prompting: no weight changes, just better instructions at inference time — fast, free, limited by context window and existing knowledge.
- RAG: no weight changes either — changes what's retrieved and inserted into the prompt at query time. Cheap to update (re-index, no retraining), traceable (can point to which retrieved chunk backed an answer).
- Confirmed via four-angle answer: the classic mistake is treating fine-tuning as a knowledge-base update mechanism — conflating "shaping how the model talks" with "keeping what it knows fresh." Fix: keep facts in a live retrieval store, fine-tune (if at all) only for tone/format/behavior.

## What I built this week
- `scripts/week4_day1_attention.py` — self-attention from scratch on real embeddings, sqrt(d) scaling demonstrated correctly after catching and fixing an initial scaling error
- `scripts/week4_day2_tokenization.py` — BPE tokenization via tiktoken: subword splits, code tokenization, multilingual token-cost comparison (English/Tamil/Hindi)

**Week 4 complete. ML/DL Conceptual Foundations phase (Weeks 3-4) done.**
