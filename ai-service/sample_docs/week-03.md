# Week 3 — ML/DL Conceptual Foundations

## Neural networks
A stack of weighted sums + nonlinear activation, repeated across layers — millions of tunable numbers shaped via training to approximate a mapping from input to output. Purely linear layers stacked with no nonlinear activation between them mathematically collapse into a single linear function, no matter how many layers — nonlinearity between layers is the entire reason depth adds expressive power (without it, a 100-layer network is no more powerful than 1 layer, just 100x the wasted compute).

## Loss functions
A single number quantifying how wrong the model currently is (`np.mean((y_pred - y) ** 2)` for MSE) — turns "make the model better" into a concrete number to minimize. Different tasks need different loss functions (MSE for regression, cross-entropy for classification/next-token prediction); picking the wrong one means optimizing for the wrong objective even with bug-free training code.

## Gradient descent — verified hands-on (`scripts/week3_day1_gradient_descent.py`)
Iteratively nudge parameters opposite to the loss gradient, scaled by the learning rate. Watched loss drop 550 -> 2.3 over 200 steps fitting `y = 3x + 7`. Observed real, unscripted asymmetric convergence: `w` converged faster than `b` because gradient magnitude for `w` scales with `x` (up to 10) while `b`'s doesn't — a live demonstration of why real training uses adaptive optimizers (Adam) instead of one shared learning rate for every parameter.
- **Trade-off confirmed empirically:** tested `learning_rate=1.0` directly — loss exploded from 550 to 5x10^35 in 10 steps (verified, not just predicted), oscillating sign each step. Confirmed via math too: convergence requires `lr < 2/lambda_max` (largest eigenvalue of the loss Hessian, ~70.8 for this dataset) — 1.0 is far past that threshold.

## Overfitting — verified hands-on (`scripts/week3_day1_overfitting.py`)
Fit polynomials of increasing degree to noisy data with a held-out test set. Degree-14 hit **exactly zero** train MSE (memorized every point including noise) but test MSE of 37.86 — worse than a degree-1 fit. The train/test gap opening up IS overfitting, numerically. Degree 5 was the sweet spot (low on both).
- **Forward connection:** the same train-vs-held-out-test gap is why RAG eval setups (Phase 6) must measure on data the pipeline wasn't tuned against — confirmed later (Week 4) via the four-angle answer mapping this directly onto over-tuning a RAG pipeline's chunk size/thresholds to a fixed benchmark question set.

## Embeddings — verified hands-on with real model (`scripts/week3_day2_embeddings.py`)
Dense vectors (384-dim, `all-MiniLM-L6-v2`) where proximity encodes meaning, not string overlap. Real result: "The cat sat on the mat" vs. "A kitten was resting on the rug" (zero shared words) scored 0.6253 cosine similarity; unrelated topic pairs scored near zero or slightly negative.

## Cosine similarity — the deferred Week 2 question, answered
Measures angle between vectors only (`dot(a,b)/(|a|*|b|)`), ignoring magnitude — direction is where an embedding model actually encodes meaning, magnitude often doesn't carry reliable signal. A high similarity score on genuinely unrelated texts usually traces to a specific, diagnosable cause: domain mismatch (general-purpose model on specialized vocabulary), bad chunking (a chunk blending two topics), or **anisotropy** (embedding vectors cluster in a narrow cone rather than spreading evenly, inflating baseline similarity even between unrelated pairs — part of why reranking exists, Week 8). Scores are relative to one model, not an absolute "% similar."

## Vector spaces
Each dimension is a learned latent feature, not human-labeled. Search becomes "find nearest points" (what a vector DB does, Week 7). Trade-off: curse of dimensionality — distances between random points converge as dimensions increase, making "nearest" less discriminative, part of why approximate nearest-neighbor methods like HNSW (Week 7) trade a little accuracy for a lot of speed.

## Process note
For this week, watched YouTube videos first to learn the actual formulas/derivations, then used these sessions to map that understanding onto working code — a workflow worth continuing for future conceptual/theory-heavy weeks. See [[feedback_answer_verification.md]] for the related adjustment (submit unaided first-draft answers here, verify separately if needed, not the reverse).

## What I built this week
- `scripts/week3_day1_gradient_descent.py` — gradient descent fitting a line from scratch, learning-rate divergence tested empirically
- `scripts/week3_day1_overfitting.py` — polynomial degree vs. train/test MSE, overfitting made numeric
- `scripts/week3_day2_embeddings.py` — real sentence-transformers embeddings + cosine similarity on semantically related vs. unrelated sentence pairs

**Week 3 complete.**
