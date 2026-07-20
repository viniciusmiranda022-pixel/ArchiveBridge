#!/usr/bin/env python3
"""Avaliador automático de PASS/FAIL da PoC Aspose (ADR-0004, item 1).

Cruza resultados dos casos (results/*.json), métricas (metrics/*.csv) e
criteria.json e emite veredito por critério + veredito final.
Exit code 0 = PASS, 1 = FAIL, 2 = erro de uso.

Uso:
    py -3 evaluate.py --results DIR --metrics DIR --criteria criteria.json
    py -3 evaluate.py --selftest

Somente biblioteca padrão. Formatos:

results/<ct*>-<caso>.json:
    {"case": "ct2-500gb", "ct": "ct2", "status": "PASS"|"FAIL",
     "inputGb": 500.0, "elapsedHours": 12.5,
     "originalHashUnchanged": true, "retryDuplicates": 0,
     "crashes": 0, "manualInterventions": 0, "detail": {...}}

metrics/<mesmo-nome>.csv (interno do harness ou do coletor externo):
    utc,workingSetMb,privateMb,handles[,itemsDone]
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path


def parse_iso(ts: str) -> datetime:
    ts = ts.strip()
    if ts.endswith("Z"):
        ts = ts[:-1] + "+00:00"
    return datetime.fromisoformat(ts)


def linear_slope_per_hour(points):
    """Regressão linear simples; retorna slope em unidade/hora."""
    if len(points) < 2:
        return 0.0
    t0 = points[0][0]
    xs = [(t - t0).total_seconds() / 3600.0 for t, _ in points]
    ys = [v for _, v in points]
    n = len(xs)
    mean_x = sum(xs) / n
    mean_y = sum(ys) / n
    denom = sum((x - mean_x) ** 2 for x in xs)
    if denom == 0:
        return 0.0
    return sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys)) / denom


def load_metrics(path: Path):
    rows = []
    with path.open(encoding="utf-8-sig") as fh:
        for row in csv.DictReader(fh):
            try:
                rows.append({
                    "t": parse_iso(row["utc"]),
                    "ws": float(row["workingSetMb"]),
                    "handles": float(row["handles"]),
                    "items": float(row["itemsDone"]) if row.get("itemsDone")
                    else None,
                })
            except (KeyError, ValueError):
                continue
    rows.sort(key=lambda r: r["t"])
    return rows


class Verdict:
    def __init__(self):
        self.checks = []

    def add(self, name, ok, detail):
        self.checks.append((name, ok, detail))

    @property
    def passed(self):
        return all(ok for _, ok, _ in self.checks)

    def print(self):
        for name, ok, detail in self.checks:
            print(f"[{'PASS' if ok else 'FAIL'}] {name}: {detail}")
        print("-" * 60)
        print(f"VEREDITO FINAL: {'PASS' if self.passed else 'FAIL'}")


def evaluate(results_dir: Path, metrics_dir: Path, criteria: dict) -> Verdict:
    v = Verdict()
    results = {}
    for f in sorted(results_dir.glob("*.json")):
        data = json.loads(f.read_text(encoding="utf-8"))
        results[data.get("case", f.stem)] = data

    # --- 1. Casos obrigatórios presentes e PASS -----------------------------
    required = set(criteria["cases"]["required"])
    present = {r.get("ct") for r in results.values()}
    missing = required - present
    v.add("casos obrigatórios presentes", not missing,
          f"faltando: {sorted(missing)}" if missing else
          f"todos presentes ({sorted(required)})")
    failed = [c for c, r in results.items() if r.get("status") != "PASS"]
    v.add("todos os casos com status PASS", not failed,
          f"reprovados: {failed}" if failed else f"{len(results)} caso(s) PASS")

    # --- 2. Integridade ------------------------------------------------------
    if criteria["integrity"]["originalHashMustMatch"]:
        bad = [c for c, r in results.items()
               if r.get("originalHashUnchanged") is not True]
        v.add("original bit a bit intacto", not bad,
              f"alterado/não verificado em: {bad}" if bad else "ok em todos")
    if criteria["integrity"]["retryMustNotDuplicate"]:
        dup = [c for c, r in results.items()
               if r.get("retryDuplicates", 0) != 0]
        v.add("retry sem duplicação lógica", not dup,
              f"duplicatas em: {dup}" if dup else "ok")

    # --- 3. Zero crash/hang/intervenção -------------------------------------
    noisy = [c for c, r in results.items()
             if r.get("crashes", 0) != 0 or r.get("manualInterventions", 0) != 0]
    v.add("zero crash/hang/intervenção manual", not noisy,
          f"ocorrências em: {noisy}" if noisy else "nenhuma ocorrência")

    # --- 4. Memória e handles por série temporal ----------------------------
    mem = criteria["memory"]
    hnd = criteria["handles"]
    peaks = []  # (inputGb, peakMb)
    for case, r in results.items():
        mfile = metrics_dir / f"{case}.csv"
        if not mfile.is_file():
            v.add(f"métricas presentes: {case}", False, "csv ausente")
            continue
        rows = load_metrics(mfile)
        if not rows:
            v.add(f"métricas legíveis: {case}", False, "csv vazio/ilegível")
            continue
        start = rows[0]["t"]
        warm = [x for x in rows
                if (x["t"] - start).total_seconds() >= mem["warmupSeconds"]]
        series = warm if len(warm) >= 2 else rows
        ws_slope = linear_slope_per_hour([(x["t"], x["ws"]) for x in series])
        v.add(f"memória sem crescimento contínuo: {case}",
              ws_slope <= mem["maxSustainedSlopeMbPerHour"],
              f"slope {ws_slope:.1f} MB/h (limite "
              f"{mem['maxSustainedSlopeMbPerHour']})")
        h_slope = linear_slope_per_hour([(x["t"], x["handles"])
                                         for x in series])
        v.add(f"sem leak de handles: {case}",
              h_slope <= hnd["maxSustainedSlopePerHour"],
              f"slope {h_slope:.1f} handles/h (limite "
              f"{hnd['maxSustainedSlopePerHour']})")
        if r.get("inputGb"):
            peaks.append((float(r["inputGb"]), max(x["ws"] for x in rows)))

        # --- Throughput por janela (se itemsDone presente) ------------------
        thr = criteria["throughput"]
        items = [(x["t"], x["items"]) for x in rows if x["items"] is not None]
        if len(items) >= 2:
            window = timedelta(seconds=thr["windowSeconds"])
            windows = []
            w_start, w_items0 = items[0]
            for t, done in items:
                if t - w_start >= window:
                    hours = (t - w_start).total_seconds() / 3600.0
                    windows.append((done - w_items0) / hours if hours else 0)
                    w_start, w_items0 = t, done
            if len(windows) >= thr["minWindowsRequired"]:
                first = windows[0]
                worst_late = min(windows[1:])
                degr = 100.0 * (first - worst_late) / first if first > 0 else 0
                v.add(f"throughput sustentado: {case}",
                      degr <= thr["maxLateDegradationPercent"],
                      f"degradação máxima {degr:.0f}% (limite "
                      f"{thr['maxLateDegradationPercent']}%)")

    # --- 5. Memória proporcional ao tamanho de entrada ----------------------
    big = [(gb, peak) for gb, peak in peaks
           if peak > mem["absolutePeakFloorMb"]]
    offenders = [(gb, peak) for gb, peak in big
                 if peak / gb > mem["maxPeakMbPerInputGb"]]
    v.add("memória sem tendência proporcional ao tamanho do PST",
          not offenders,
          f"pico/GB acima do limite em: {offenders}" if offenders
          else f"{len(peaks)} execução(ões) dentro do limite")

    # --- 6. Janela operacional do 500 GB ------------------------------------
    window_h = criteria.get("operationalWindowHours500Gb")
    big_runs = [(c, r) for c, r in results.items()
                if float(r.get("inputGb") or 0) >= 400]
    if big_runs:
        if window_h is None:
            v.add("janela operacional 500 GB definida", False,
                  "operationalWindowHours500Gb é null — o responsável do "
                  "gate deve defini-la antes da execução full")
        else:
            over = [c for c, r in big_runs
                    if float(r.get("elapsedHours") or math.inf) > window_h]
            v.add("500 GB dentro da janela operacional", not over,
                  f"estouro em: {over} (janela {window_h}h)" if over
                  else f"dentro de {window_h}h")
    return v


# --------------------------------------------------------------------------
def _selftest():
    """Gera cenários sintéticos PASS e FAIL e confere o avaliador."""
    import tempfile

    criteria = json.loads(
        (Path(__file__).parent.parent / "criteria.json")
        .read_text(encoding="utf-8"))
    criteria["operationalWindowHours500Gb"] = 24

    def write_run(root, case, ct, input_gb, elapsed_h, *, ws_slope_mb_h,
                  handle_slope_h, status="PASS", hash_ok=True, dup=0,
                  crashes=0, degrade=False):
        results = root / "results"
        metrics = root / "metrics"
        results.mkdir(exist_ok=True)
        metrics.mkdir(exist_ok=True)
        (results / f"{case}.json").write_text(json.dumps({
            "case": case, "ct": ct, "status": status, "inputGb": input_gb,
            "elapsedHours": elapsed_h, "originalHashUnchanged": hash_ok,
            "retryDuplicates": dup, "crashes": crashes,
            "manualInterventions": 0}), encoding="utf-8")
        t0 = datetime(2026, 7, 20, tzinfo=timezone.utc)
        rows = ["utc,workingSetMb,privateMb,handles,itemsDone"]
        base_rate = 10000.0
        items = 0.0
        for minute in range(0, 121, 5):
            t = t0 + timedelta(minutes=minute)
            ws = 2000 + ws_slope_mb_h * (minute / 60.0)
            handles = 500 + handle_slope_h * (minute / 60.0)
            rate = base_rate * (0.3 if degrade and minute > 60 else 1.0)
            items += rate * (5 / 60.0)
            rows.append(f"{t.isoformat()},{ws:.1f},{ws:.1f},"
                        f"{handles:.0f},{items:.0f}")
        (metrics / f"{case}.csv").write_text("\n".join(rows) + "\n",
                                             encoding="utf-8")

    ok = True
    with tempfile.TemporaryDirectory() as td:
        root = Path(td) / "pass"
        root.mkdir()
        for ct in ("ct1", "ct2", "ct3", "ct4", "ct5"):
            write_run(root, f"{ct}-smoke", ct, 1.0, 0.5,
                      ws_slope_mb_h=5, handle_slope_h=2)
        write_run(root, "ct2-500gb", "ct2", 500.0, 12.0,
                  ws_slope_mb_h=10, handle_slope_h=1)
        v = evaluate(root / "results", root / "metrics", criteria)
        if not v.passed:
            print("SELFTEST FAIL: cenário PASS reprovado indevidamente:")
            v.print()
            ok = False

        scenarios = {
            "memoria-crescente": dict(ws_slope_mb_h=2000, handle_slope_h=1),
            "handle-leak": dict(ws_slope_mb_h=5, handle_slope_h=500),
            "estouro-janela": dict(ws_slope_mb_h=5, handle_slope_h=1),
            "hash-alterado": dict(ws_slope_mb_h=5, handle_slope_h=1,
                                  hash_ok=False),
            "throughput-degradado": dict(ws_slope_mb_h=5, handle_slope_h=1,
                                         degrade=True),
            "caso-fail": dict(ws_slope_mb_h=5, handle_slope_h=1,
                              status="FAIL"),
        }
        for name, kw in scenarios.items():
            root = Path(td) / name
            root.mkdir()
            for ct in ("ct1", "ct2", "ct3", "ct4", "ct5"):
                write_run(root, f"{ct}-smoke", ct, 1.0, 0.5,
                          ws_slope_mb_h=5, handle_slope_h=2)
            elapsed = 30.0 if name == "estouro-janela" else 12.0
            write_run(root, "ct2-500gb", "ct2", 500.0, elapsed, **kw)
            v = evaluate(root / "results", root / "metrics", criteria)
            if v.passed:
                print(f"SELFTEST FAIL: cenário '{name}' deveria reprovar")
                ok = False
    print("SELFTEST", "OK" if ok else "FALHOU")
    return 0 if ok else 1


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--results", type=Path)
    ap.add_argument("--metrics", type=Path)
    ap.add_argument("--criteria", type=Path)
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args(argv)
    if args.selftest:
        return _selftest()
    if not (args.results and args.metrics and args.criteria):
        ap.print_usage()
        return 2
    criteria = json.loads(args.criteria.read_text(encoding="utf-8"))
    verdict = evaluate(args.results, args.metrics, criteria)
    verdict.print()
    return 0 if verdict.passed else 1


if __name__ == "__main__":
    sys.exit(main())
