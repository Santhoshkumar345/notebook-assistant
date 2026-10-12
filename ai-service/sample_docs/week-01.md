# Week 1 — Python Syntax & Core Data Structures

## Topics
- Syntax, list/dict/set/tuple comprehensions
- Functions, `*args`/`**kwargs`
- Classes
- Decorators
- Context managers (`with`)
- Exception handling

## Day 1 — Comprehensions

### Topic: List/dict/set comprehensions
- **What:** Not just syntactic sugar for a `for` + `.append()` loop — CPython compiles a comprehension into its own scoped mini-function with a dedicated `LIST_APPEND` bytecode, so it's both faster and doesn't leak its loop variable into the enclosing scope (a plain `for` loop's variable survives after the loop; a comprehension's doesn't).
- **Why:** Concise transform/filter over a collection — Python's rough equivalent of C#'s LINQ `.Select()`/`.Where()`, but built into syntax rather than a library. Key difference from LINQ: comprehensions are eager (build the whole result immediately), LINQ is usually lazy (`IEnumerable`, deferred). The lazy equivalent in Python is a generator expression `(x for x in ...)`.
- **Trade-offs:** Nesting more than one `for` clause in a single comprehension collapses multiple loop headers onto one line in outer-to-inner order, which doesn't read the way nested loops naturally would, and loses the indentation cue that shows nesting depth. Readability drops fast past 2 clauses.
- **How to debug:** `AttributeError: 'dict' object has no attribute 'value'` inside a comprehension over `dict` items usually means treating a dict like a typed object — need `x["value"]` (key access), not `x.value` (attribute access). Shows up constantly with raw JSON API responses, which is exactly what `pydantic` (Week 2) fixes by giving you a typed, dot-accessible model.

## Day 1b — Raw syntax primer (added after pacing check-in)

Went back to cover fundamentals explicitly instead of assuming comprehension fluency implied loop fluency: operators (`//`, `%`, `and`/`or`/`not`), conditionals, `for`/`while` with `break`/`continue`, indexing & slicing (`s[::-1]`, negative indices), rewriting comprehensions as explicit loops, string methods (`strip`, `split`, `join`, `replace`), list methods (`append`, `sort` in-place vs `sorted()` returning new), built-ins (`enumerate`, `zip`, `min`/`max`/`sum`).

**Why this mattered:** solving Day 1 exercises via C#/LINQ pattern-matching intuition isn't the same as knowing raw Python loop syntax — and DSA interview rounds test the raw manual-loop version, not the pythonic shortcut.

## DSA drills (two-pointer + hashmap optimization)

- `reverse_string`: two-pointer swap on a list (strings are immutable, so this is O(n) time but O(n) *extra* space — true O(1) space in-place swap only works on a mutable sequence like a list of ints).
- `two_sum`: wrote brute-force O(n²) nested-loop version first, found and fixed a dead-code bug (`return None` placed inside the `if` block right after `return (i,j)` — unreachable, since a `return` exits immediately; it happened to still "work" only because both test cases had a match). Then optimized to O(n) time / O(n) space using a single-pass hashmap (`value -> index`), checking `target - num` against the map before inserting — the standard time-for-space trade-off.

## Day 2 — Functions, *args, **kwargs
- **What/why:** `*args` collects extra positional args as a tuple, `**kwargs` collects extra keyword args as a dict — kept separate so order-sensitive values and named options don't collapse into one ambiguous bucket.
- **Trade-off:** overusing them hides a function's real contract — no autocomplete, no obvious required params from the signature alone. Explicit named parameters are self-documenting; `**kwargs` is not.
- **How to debug:** `foo(1, 2, 3, x=4, b=5)` for `def foo(a, b, *args, **kwargs)` raises `TypeError: got multiple values for argument 'b'` — `b` is bound positionally to `2` first, then the keyword `b=5` tries to bind it again. Positional binding happens before keyword binding is checked for conflicts.

## Day 3 — Classes
- **What:** class attributes (shared, like C# `static`) vs. instance attributes (per-object). `e1.company = "X"` doesn't mutate the class attribute — it creates a new instance attribute that *shadows* it just for `e1`. But mutating a shared *mutable* class attribute (e.g. `e1.company.append(...)` if it were a list) affects every instance — same category of gotcha as Day 1b/DSA's mutable-default trap.
- **Why:** `self` is explicit (not hidden like C#'s `this`) because `instance.method(args)` is literally sugar for `ClassName.method(instance, args)` — no separate calling convention for methods vs. functions.
- **Trade-off:** Python allows multiple inheritance (unlike C#), resolved via MRO — powerful but requires understanding resolution order to avoid diamond-problem bugs. C# avoids the ambiguity entirely by disallowing it.
- **How to debug:** mutable default arguments (`def __init__(self, salary=[])`) are evaluated once at definition time and shared across every instance — classic Python gotcha. Fix: default to `None`, initialize inside the method.

## Day 4 — Decorators
- **What:** `@timer` above `def slow_add` is exactly `slow_add = timer(slow_add)` — a decorator is a plain function that takes a function and returns a new one.
- **Why:** `wrapper(*args, **kwargs)` (not fixed params) so the decorator works on any function signature, not just one shape.
- **Trade-off:** skipping `functools.wraps` breaks `__name__`/`__doc__`/introspection — every decorated function's stack trace and `help()` shows `wrapper` instead of its real name, which gets genuinely painful once a codebase has many decorated functions.
- **How to debug:** a `retry` decorator that only catches `ValueError` won't retry a `ConnectionError` — it bubbles up immediately. Catching bare `Exception` in a retry loop is usually a mistake, not a fix: it would silently "retry" real bugs (like a `TypeError` from wrong usage) instead of surfacing them, masking programming errors as if they were transient network blips. Only retry exceptions that represent genuinely transient/recoverable failures.

## Day 5 — Context managers & exception handling
- **What:** execution order on an exception is try → except → finally → return; on success it's try → else → finally → return. `finally` always runs, exception or not.
- **Why:** `__exit__(exc_type, exc_value, traceback)` exists because cleanup often needs to know whether an error occurred — e.g. roll back a DB transaction on failure vs. commit on success.
- **Trade-off:** returning `True` from `__exit__` swallows the exception — fine for expected/harmless cases (`contextlib.suppress(FileNotFoundError)` when deleting an optional temp file), dangerous when it hides real failures like data corruption.
- **How to debug:** `except Exception: pass` (or literal bare `except:`, which is worse — it also catches `KeyboardInterrupt`/`SystemExit`) silently swallows real bugs. Fix: log with `logging.exception(...)` (captures the full traceback, unlike `print`) and re-raise unless you have a specific, intentional reason to suppress that exact exception type.

## What I built this week
- `scripts/week1_day1_basics.py` — syntax, data structures, comprehensions
- `scripts/week1_day1b_syntax_primer.py` — operators, conditionals, loops, indexing/slicing, string/list methods, built-ins
- `scripts/week1_dsa_drill1.py` — reverse_string (two-pointer), two_sum (brute force, then O(n) hashmap)
- `scripts/week1_day2_functions.py` — default args, `*args`, `**kwargs`, unpacking
- `scripts/week1_day3_classes.py` — class/instance attributes, inheritance, `super()`, dunder `__str__`
- `scripts/week1_day4_decorators.py` — custom decorators, `@property`, `@staticmethod`/`@classmethod`, retry pattern
- `scripts/week1_day5_context_exceptions.py` — try/except/else/finally, custom exceptions, `with`, class-based and `@contextmanager`-based context managers

**Week 1: complete.**
