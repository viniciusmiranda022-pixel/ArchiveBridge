"""Testes do avaliador automático da PoC Aspose (poc/aspose-email).

O avaliador é a única parte executável do pacote fora da VM descartável
(stdlib pura); seu selftest gera cenários sintéticos PASS e FAIL e confere
os vereditos. Rodar no CI garante que o critério automático não regrida.

Comando canônico (Windows): py -3 -m unittest tests.test_poc_evaluator
Linux/macOS:                python3 -m unittest tests.test_poc_evaluator
"""

import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EVALUATOR = ROOT / "poc" / "aspose-email" / "scripts" / "evaluate.py"


class TestPocEvaluator(unittest.TestCase):
    def test_selftest_passes(self):
        proc = subprocess.run(
            [sys.executable, str(EVALUATOR), "--selftest"],
            capture_output=True, text=True, timeout=120)
        self.assertEqual(
            proc.returncode, 0,
            f"selftest falhou:\n{proc.stdout}\n{proc.stderr}")
        self.assertIn("SELFTEST OK", proc.stdout)

    def test_usage_error_without_args(self):
        proc = subprocess.run(
            [sys.executable, str(EVALUATOR)],
            capture_output=True, text=True, timeout=30)
        self.assertEqual(proc.returncode, 2)


if __name__ == "__main__":
    unittest.main()
