#!/usr/bin/env python3
"""DEVELOPMENT-SET scoring of the rule-provenance audit on the VAC-002-P3 corpus. Run with `python3 -I`.

  score_dev.py [--analyzer lexical|ast]... [--cbe DIR] [--work DIR] [--summary FILE]

This is NOT a confirmatory measurement. The P3 corpus was used to develop the `ast` analyzer (EXP-3 phase 1 showed
its misses and false questions), so numbers on it are biased upward. A held-out corpus is still required.

The script reuses the EXP-3 harness read-only: it imports `harness/exp3.py` from the cohort-based-engineering
checkout for tree materialisation, handoff construction and labels, and never writes inside that checkout (no
bytecode, no results). Every output goes under `dev/out/` (git-ignored) except the optional `--summary` file.
Scoring follows EXP-3 PREREGISTRATION.md section 6: a finding is a TP when its symbol resolves to a PLANTED
declaration of that scenario, an FQ when it resolves to SUPPORTED, TECHNICAL or nothing (UNLABELLED).
"""
import importlib.util
import json
import os
import subprocess
import sys
from pathlib import Path

sys.dont_write_bytecode = True

HERE = Path(__file__).resolve().parent
EXPERIMENT = HERE.parent
CLI = EXPERIMENT / "src/RuleProvenance.Cli/bin/Debug/net10.0/rule-provenance.dll"
MECHANISMS = ("superseded-value", "exemption", "silent-default")


def arg_values(name):
    return [sys.argv[i + 1] for i, a in enumerate(sys.argv[:-1]) if a == name]


def load_exp3(cbe: Path):
    path = cbe / "research/experiments/EXP-3/harness/exp3.py"
    spec = importlib.util.spec_from_file_location("exp3_readonly", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run_audit(exp3, analyzer, tree, handoff, files, out, sdk):
    args = ["dotnet", str(CLI), "audit", "--analyzer", analyzer, "--root", str(tree), "--handoff", str(handoff),
            "--sdk-version", sdk, "--out", str(out)]
    for f in files:
        args += ["--legacy", str(f)]
    r = subprocess.run(args, capture_output=True, text=True, env=exp3.ENV)
    if r.returncode not in (0, 4):
        raise SystemExit(f"audit failed ({r.returncode}): {r.stderr}")
    return json.loads(out.read_text())


def score(exp3, sid, dom, report):
    lab = exp3.labels_for(sid, dom)
    mech = exp3.LABELS["plantedRules"].get(sid)
    scored = []
    for f in report["findings"]:
        sym = f.get("location", {}).get("symbol")
        key, l = exp3.resolve_label(lab, sym)
        cat = l[0] if l else "UNLABELLED"
        outcome = {"PLANTED": "TP", "SUPPORTED": "FQ", "TECHNICAL": "FQ", "UNLABELLED": "FQ",
                   "OTHER-UNAUTHORIZED": "OTHER"}[cat]
        scored.append({"symbol": sym, "class": f["class"], "label": cat, "outcome": outcome, "message": f["message"]})
    tps = [s for s in scored if s["outcome"] == "TP"]
    mech_ok = None
    if mech and tps:
        if mech == "superseded-value":
            mech_ok = any(s["class"] == "superseded-authority" for s in tps)
        elif mech == "silent-default":
            mech_ok = any("wildcard default" in s["message"] for s in tps)
        else:
            mech_ok = any("exemption" in s["message"] for s in tps)
    return {"scenario": sid, "domain": dom, "mechanism": mech, "findings": len(scored), "tp": len(tps),
            "fq": sum(s["outcome"] == "FQ" for s in scored), "detected": bool(tps) if mech else None,
            "mechanismCorrect": mech_ok, "scored": scored}


def agg(rows):
    n = sum(r["findings"] for r in rows)
    fq = sum(r["fq"] for r in rows)
    planted = [r for r in rows if r["mechanism"]]
    det = sum(1 for r in planted if r["detected"])
    return {"scenarios": len(rows), "ownerQuestions": n, "tp": sum(r["tp"] for r in rows), "fq": fq,
            "plantedRules": len(planted), "detected": det,
            "recall": round(det / len(planted), 4) if planted else None,
            "falseQuestionRate": round(fq / n, 4) if n else None,
            "mechanismCorrect": sum(1 for r in planted if r["mechanismCorrect"]),
            "missed": [r["scenario"] for r in planted if not r["detected"]]}


def main():
    cbe = Path((arg_values("--cbe") or ["/home/user/cohort-based-engineering"])[-1])
    analyzers = arg_values("--analyzer") or ["lexical", "ast"]
    work = Path((arg_values("--work") or [str(HERE / "out/work")])[-1])
    exp3 = load_exp3(cbe)
    sdk = subprocess.run(["dotnet", "--version"], capture_output=True, text=True, env=exp3.ENV,
                         check=True).stdout.strip()
    rev = exp3.git_rev()
    scens = sorted(p.name for p in (exp3.P3 / "scenarios").iterdir() if p.name[:1] in "CD" and len(p.name) == 3)
    results = {a: [] for a in analyzers}
    for sid in scens:
        scen = json.loads((exp3.P3 / "scenarios" / sid / "scenario.json").read_text())
        dom = scen["domain"]
        tree = work / sid / "tree"
        exp3.materialize(scen, tree)
        handoff = exp3.build_handoff(sid, tree, rev)
        exp3.validate("echelon-authority-handoff-0.1.schema.json", handoff)
        hp = work / sid / "handoff.json"
        hp.write_text(json.dumps(handoff, indent=2) + "\n")
        files = exp3.audit_files(dom, tree, "rules")
        all_src = exp3.audit_files(dom, tree, "all")
        for a in analyzers:
            out = HERE / "out" / a / f"{sid}.audit.json"
            out.parent.mkdir(parents=True, exist_ok=True)
            report = run_audit(exp3, a, tree, hp, files, out, sdk)
            exp3.validate("echelon-provenance-audit-0.1.schema.json", report)
            row = score(exp3, sid, dom, report)
            # Exploratory and unscored, as in EXP-3 section 2: owner questions over all src/**/*.fs.
            ex = run_audit(exp3, a, tree, hp, all_src, HERE / "out" / a / f"{sid}.all-src.audit.json", sdk)
            row["exploratoryAllSrcOwnerQuestions"] = len(ex["findings"])
            results[a].append(row)
    summary = {"schema": "dokimos.rule-provenance.dev-scores/1",
               "note": "DEVELOPMENT SET (VAC-002-P3, 27 scenarios). Not a confirmatory result: the ast analyzer "
                       "was developed against this corpus, so its numbers are biased upward. Scoring rules: EXP-3 "
                       "PREREGISTRATION.md section 6.",
               "corpusRevision": rev, "sdk": sdk, "analyzers": {}}
    for a, rows in results.items():
        summary["analyzers"][a] = {
            "aggregate": agg(rows),
            "byMechanism": {m: agg([r for r in rows if r["mechanism"] == m]) for m in MECHANISMS},
            "plantedScenariosOnly": agg([r for r in rows if r["mechanism"]]),
            "exploratoryAllSrcOwnerQuestions": sum(r["exploratoryAllSrcOwnerQuestions"] for r in rows),
            "falseQuestions": sorted({(s["symbol"] or "", s["label"]) for r in rows for s in r["scored"]
                                      if s["outcome"] == "FQ"}),
            "scenarios": [{k: r[k] for k in ("scenario", "mechanism", "findings", "tp", "fq", "detected",
                                             "mechanismCorrect", "exploratoryAllSrcOwnerQuestions")}
                          | {"findingsDetail": r["scored"]} for r in rows]}
    text = json.dumps(summary, indent=2) + "\n"
    (HERE / "out" / "dev-scores.json").write_text(text)
    for path in arg_values("--summary"):
        Path(path).write_text(text)
    for a in analyzers:
        g = summary["analyzers"][a]["aggregate"]
        print(f"[DEVELOPMENT SET] {a:8} recall {g['detected']}/{g['plantedRules']} = {g['recall']}; "
              f"false questions {g['fq']}/{g['ownerQuestions']} = {g['falseQuestionRate']}; missed {g['missed']}; "
              f"exploratory all-src owner questions {summary['analyzers'][a]['exploratoryAllSrcOwnerQuestions']}")
        for m in MECHANISMS:
            x = summary["analyzers"][a]["byMechanism"][m]
            print(f"    {m:17} recall {x['detected']}/{x['plantedRules']}; questions {x['ownerQuestions']}, "
                  f"FQ {x['fq']}; mechanism correct {x['mechanismCorrect']}")


if __name__ == "__main__":
    main()
