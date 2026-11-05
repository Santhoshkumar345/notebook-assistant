"""Run a suite, record the run, and compare it with the baseline.

  python -m evals.run --suite hard                      run it; compare with evals/baselines/hard.json if there is one
  python -m evals.run --suite hard --replay             answer only from the cache: no model, no key, free; fails if a call is missing
  python -m evals.run --suite hard --retrieval-only     retrieval metrics only, no model at all
  python -m evals.run --suite hard --judge              also judge faithfulness (after the judge passes its calibration)
  python -m evals.run --suite redteam --no-guard        what the attacks do when the output guard is switched off
  python -m evals.run --suite followup --no-condense    answer follow-ups as typed, without rewriting them: the baseline the rewrite is measured against
  python -m evals.run --suite hard --update-baseline    accept this run as the new baseline (commit the file, with the reason in the message)

Exit codes: 0 fine (or nothing to compare with), 1 a metric regressed, 2 the dataset changed so the comparison is meaningless,
3 the run could not be completed (a cache miss in replay mode, an uncalibrated judge, a broken dataset), 64 wrong usage.

Model answers are cached in ai-service/data/eval_answers.jsonl by exact request, so a rerun costs nothing unless the prompt, the model or
the retrieved context changed. Runs are saved in ai-service/data/eval_runs/.
"""

import argparse
import sys
from datetime import datetime
from pathlib import Path
from typing import Callable

from app import generation
from evals import framework as fw
from evals.suites import SUITES

HERE = Path(__file__).resolve().parent
DATA = HERE.parent / "data"
_REAL = object()  # "build the real Gemini client", as opposed to None ("there is no model")


def _percent(value: float) -> str:
    return f"{value:.0%}"


def _describe(name: str, m: fw.MetricValue, base: fw.MetricValue | None, verdict: str) -> str:
    count = f"{m.successes}/{m.n}" if m.successes is not None else f"n={m.n}"
    shown = _percent(m.value) if m.successes is not None else f"{m.value:.3f}"
    interval = f"{_percent(m.ci[0])}-{_percent(m.ci[1])}" if m.ci else "-"
    was = (_percent(base.value) if base.successes is not None else f"{base.value:.3f}") if base else "-"
    return f"  {name:<32}{count:>7} {shown:>5}   95% CI {interval:<9}  baseline {was:>5}   {verdict}"


def _print_report(report: fw.Report, comparison: fw.Comparison | None, base: fw.Report | None, out: Callable[[str], None]) -> None:
    meta, usage = report.meta, report.usage
    out(f"suite {meta['suite']} ({meta['n_cases']} cases, k={meta['k']}, {meta['mode']}) dataset {meta['fingerprint']} model {meta['model']}"
        f"{'' if meta.get('guard', True) else '  [OUTPUT GUARD OFF]'}{'' if meta.get('condense', True) else '  [FOLLOW-UP REWRITE OFF]'}")
    out(f"  model calls: {usage['live_calls']} live, {usage['cached_calls']} cached | tokens in/out: {usage['input_tokens']}/{usage['output_tokens']}"
        f" | latency p50/p95 over {usage['latency_measured_on']} live cases: {usage['latency_ms_p50']}/{usage['latency_ms_p95']} ms")
    verdicts = {r.metric: r.verdict for r in comparison.rows} if comparison else {}
    for name, value in report.metrics.items():
        out(_describe(name, value, base.metrics.get(name) if base else None, verdicts.get(name, "")))
    out("  (95% CI = Wilson interval: with a few dozen cases it is wide, and that width is the honest size of the uncertainty)")


def _print_failures(report: fw.Report, metrics: list[str], out: Callable[[str], None]) -> None:
    for name in metrics:
        bad = name in fw.METRICS and fw.METRICS[name].direction == "lower"
        failing = [c for c in report.cases if name in c["flags"] and c["flags"][name] is bad]
        if failing:
            out(f"  cases failing {name}: " + ", ".join(c["id"] for c in failing))
            for c in failing[:5]:
                out(f"    - {c['id']}: status={c['status']} answer={(c['answer'] or '')[:120]!r}")


def main(argv: list[str] | None = None, *, suites=None, client=_REAL, judge_client=None, out: Callable[[str], None] = print) -> int:
    suites = SUITES if suites is None else suites
    parser = argparse.ArgumentParser(prog="evals.run", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.error = lambda message: (out(f"usage error: {message}"), sys.exit(64))[1]  # 2 is taken: it means "the dataset changed"
    parser.add_argument("--suite", required=True, choices=sorted(suites))
    parser.add_argument("--baseline", type=Path, help="default: evals/baselines/<suite>.json")
    parser.add_argument("--update-baseline", action="store_true")
    parser.add_argument("--replay", action="store_true", help="answer only from the cache")
    parser.add_argument("--retrieval-only", action="store_true")
    parser.add_argument("--judge", action="store_true")
    parser.add_argument("--no-guard", action="store_true", help="switch the output guard off, to see what the model does on its own")
    parser.add_argument("--no-condense", action="store_true", help="do not rewrite follow-up questions before searching: the baseline the rewrite is measured against")
    parser.add_argument("--tolerance", type=float, default=0.10, help="how much worse (as a share of cases) a metric may get before the run fails")
    parser.add_argument("--pause", type=float, default=2.5, help="seconds between live model calls (the free tier's rate)")
    parser.add_argument("--cache", type=Path, default=DATA / "eval_answers.jsonl")
    parser.add_argument("--runs-dir", type=Path, default=DATA / "eval_runs")
    args = parser.parse_args(argv)

    suite = suites[args.suite]()
    baseline_path = args.baseline or HERE / "baselines" / f"{args.suite}.json"
    try:
        suite.validate()
        model = None
        if not args.retrieval_only:
            live = None if args.replay else (client if client is not _REAL else generation.get_client())
            model = fw.CachingClient(live, args.cache)

        judge = None
        if args.judge and model is not None:
            judge_live = None if args.replay else (judge_client or (client if client is not _REAL else None) or generation.get_client())
            judge = fw.LlmJudge(fw.CachingClient(judge_live, args.cache))
            calibration = fw.calibrate(judge)
            out(f"judge calibration: agrees with the hand labels on {calibration.total - len(calibration.errors)}/{calibration.total}")
            fw.require_calibrated(calibration)

        original_guard = generation.sanitize_answer
        if args.no_guard:
            generation.sanitize_answer = lambda text, sources: type("Unguarded", (), {"text": text, "changed": False, "links_removed": 0, "images_removed": 0})()
        try:
            report = fw.run_suite(suite, client=model, judge=judge, pause_s=0 if args.replay else args.pause, progress=None, condense=not args.no_condense)
        finally:
            generation.sanitize_answer = original_guard
    except (fw.CacheMiss, fw.JudgeNotCalibrated, fw.DatasetError) as problem:
        out(f"cannot complete the run: {problem}" + ("  (the cache lacks an answer: run once without --replay)" if isinstance(problem, fw.CacheMiss) else ""))
        return 3

    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    report.save(args.runs_dir / f"{stamp}-{args.suite}.json")

    if args.update_baseline:
        report.save(baseline_path)
        _print_report(report, None, None, out)
        out(f"baseline written: {baseline_path}")
        return 0

    base = fw.Report.load(baseline_path) if baseline_path.exists() else None
    comparison = fw.compare(base, report, args.tolerance) if base else None
    _print_report(report, comparison, base, out)

    if comparison is None:
        out(f"no baseline at {baseline_path}; nothing to compare with (--update-baseline records this run as one)")
        return 0
    if comparison.status == "incomparable":
        out(f"INCOMPARABLE: {comparison.reason}. Review the change, then --update-baseline.")
        return 2
    if comparison.status == "regressed":
        regressed = [r.metric for r in comparison.rows if r.verdict in ("regressed", "missing")]
        out("REGRESSED: " + ", ".join(regressed) + f" (tolerance {args.tolerance:.0%})")
        _print_failures(report, regressed, out)
        for flip in comparison.flips:
            out(f"  flipped: {flip.case} {flip.metric} {flip.was} -> {flip.now}")
        return 1
    out(f"result: OK against {baseline_path.name} (tolerance {args.tolerance:.0%})")
    for flip in comparison.flips:
        out(f"  flipped: {flip.case} {flip.metric} {flip.was} -> {flip.now}")
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
