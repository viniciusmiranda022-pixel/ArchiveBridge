"""Testes da conversão DOCX -> Markdown do Runbook de Engenharia.

Comando canônico (ambiente-alvo Windows):
    py -3 -m unittest tests.test_runbook_conversion

Linux/macOS:
    python3 -m unittest tests.test_runbook_conversion

Os testes reconvertem o DOCX de ``docs/source`` em um diretório temporário
e validam: contagens de origem via ``conversion-manifest.json`` (nunca por
varredura bruta dos .md, que contêm elementos editoriais gerados),
cobertura por blocos, fences balanceados, numeração contínua, links,
imagens e reprodutibilidade byte a byte contra ``docs/runbook``.
"""

import importlib.util
import json
import re
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
COMMITTED = ROOT / "docs" / "runbook"
SOURCE = (ROOT / "docs" / "source" /
          "Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.docx")

_spec = importlib.util.spec_from_file_location(
    "convert_runbook", ROOT / "tools" / "convert_runbook.py")
convert_runbook = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_spec and convert_runbook)

EXPECTED_FILES = sorted(set(convert_runbook.OUTPUT_ROUTING.values()))
ALLOWED_LANGS = set(convert_runbook.CODE_LANGS.values()) | {
    convert_runbook.CODE_LANG_FALLBACK}

# Contagens de origem esperadas (validadas na análise inicial do DOCX e
# na renderização do PDF oficial).
EXPECTED = {
    "heading1": 13,
    "heading2": 51,
    "heading3": 115,
    "tables": 27,
    "bulletItems": 414,
    "numberItems": 215,
    "codeParagraphs": 41,
    "images": 3,
    "lastListNumber": 215,
}

# Caminhos da máquina de conversão que jamais podem vazar para a saída.
# Específicos o bastante para não colidir com conteúdo legítimo do runbook
# (ex.: a expressão "target/root/hash" na tabela de reconciliação).
FORBIDDEN_PATH_FRAGMENTS = (
    "/home/user/", "/root/.claude", "/tmp/claude", ".claude/uploads",
    "scratchpad")


class ConversionFixture:
    """Executa a conversão uma única vez para toda a suíte."""

    _instance = None

    def __init__(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="runbook-conv-")
        self.out = Path(self.tmp.name)
        convert_runbook.Converter(SOURCE, self.out).convert()
        manifest_path = self.out / convert_runbook.MANIFEST_OUTPUT
        self.manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        self.source_blocks = [b for b in self.manifest["blocks"]
                              if b.get("sourceType") != "generated"]
        self.generated_blocks = [b for b in self.manifest["blocks"]
                                 if b.get("sourceType") == "generated"]
        self.md_files = {name: (self.out / name).read_text(encoding="utf-8")
                         for name in EXPECTED_FILES}
        self.index = (self.out / convert_runbook.INDEX_OUTPUT).read_text(
            encoding="utf-8")

    @classmethod
    def get(cls):
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance


class TestSourceCounts(unittest.TestCase):
    """Contagens derivadas exclusivamente do manifesto de origem."""

    @classmethod
    def setUpClass(cls):
        cls.fx = ConversionFixture.get()

    def test_counts_match_expected(self):
        counts = self.fx.manifest["counts"]
        for key, expected in EXPECTED.items():
            self.assertEqual(counts[key], expected,
                             f"contagem divergente para {key}")

    def test_heading_counts_recomputed_from_blocks(self):
        for style, expected in (("Heading1", 13), ("Heading2", 51),
                                ("Heading3", 115)):
            got = sum(1 for b in self.fx.source_blocks
                      if b.get("style") == style)
            self.assertEqual(got, expected, style)

    def test_generated_blocks_excluded_from_source_counts(self):
        for b in self.fx.generated_blocks:
            self.assertTrue(b.get("excludedFromSourceCounts"),
                            f"bloco gerado sem exclusão: {b}")
            self.assertEqual(b.get("status"), "generated")


class TestBlockCoverage(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fx = ConversionFixture.get()

    def test_every_block_converted_or_justified(self):
        converted = ignored = 0
        for b in self.fx.source_blocks:
            self.assertIn(b["status"], ("converted", "ignored"),
                          f"status inválido no bloco {b['sourceIndex']}")
            if b["status"] == "converted":
                converted += 1
                self.assertTrue(b["outputFile"],
                                f"bloco {b['sourceIndex']} sem destino")
                self.assertIn(b["outputFile"], EXPECTED_FILES)
            else:
                ignored += 1
                self.assertTrue(b.get("justification"),
                                f"bloco {b['sourceIndex']} ignorado sem "
                                "justificativa")
        self.assertEqual(converted + ignored, len(self.fx.source_blocks))

    def test_source_indexes_are_contiguous(self):
        indexes = [b["sourceIndex"] for b in self.fx.source_blocks]
        self.assertEqual(indexes, list(range(len(indexes))),
                         "blocos do corpo fora de ordem ou faltando")

    def test_drawings_are_children_not_blocks(self):
        drawing_children = [
            child
            for b in self.fx.source_blocks
            for child in b.get("children", [])
            if child.get("type") == "drawing"
        ]
        self.assertEqual(len(drawing_children), EXPECTED["images"])
        for child in drawing_children:
            self.assertTrue(child.get("relationshipId"))
            path = self.fx.out / child["outputPath"]
            self.assertTrue(path.is_file(),
                            f"imagem sem destino: {child['outputPath']}")
        kinds = {b["sourceType"] for b in self.fx.source_blocks}
        self.assertEqual(kinds, {"paragraph", "table"},
                         "somente w:p e w:tbl podem ser body blocks")


class TestMarkdownOutput(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fx = ConversionFixture.get()

    def test_no_empty_markdown(self):
        for name, text in self.fx.md_files.items():
            body = text.replace(convert_runbook.GENERATED_NOTICE, "").strip()
            self.assertTrue(body, f"{name} está vazio")

    def test_fences_balanced_and_labelled(self):
        for name, text in self.fx.md_files.items():
            open_lang = None
            fence_len = 0
            for i, line in enumerate(text.splitlines(), 1):
                m = re.match(r"^(`{3,})(\S*)\s*$", line)
                if not m:
                    continue
                if open_lang is None:
                    self.assertTrue(m.group(2),
                                    f"{name}:{i} fence sem linguagem")
                    self.assertIn(m.group(2), ALLOWED_LANGS,
                                  f"{name}:{i} linguagem não permitida")
                    open_lang = m.group(2)
                    fence_len = len(m.group(1))
                elif len(m.group(1)) >= fence_len and not m.group(2):
                    open_lang = None
            self.assertIsNone(open_lang, f"{name}: fence não fechado")

    def test_no_forbidden_codepoints(self):
        for name, text in self.fx.md_files.items():
            self.assertNotIn("￾", text, name)
            self.assertNotIn("￿", text, name)

    def test_no_conversion_machine_paths(self):
        for name, text in {**self.fx.md_files,
                           "README.md": self.fx.index}.items():
            for frag in FORBIDDEN_PATH_FRAGMENTS:
                self.assertNotIn(frag, text,
                                 f"{name} contém caminho da máquina de "
                                 f"conversão: {frag}")

    def test_continuous_list_numbering(self):
        """A sequência ListNumber é contínua (1..215) na ordem de leitura."""
        numbers = []
        in_code = False
        for name in EXPECTED_FILES:
            for line in self.fx.md_files[name].splitlines():
                if re.match(r"^`{3,}", line):
                    in_code = not in_code
                    continue
                if in_code:
                    continue
                m = re.match(r"^(\d+)\. ", line)
                if m:
                    numbers.append(int(m.group(1)))
        self.assertEqual(numbers,
                         list(range(1, EXPECTED["numberItems"] + 1)),
                         "numeração contínua quebrada")

    def test_tables_not_discarded(self):
        gfm = sum(len(re.findall(r"^\|(?: --- \|)+ --- \|$|^\|( --- \|)+$",
                                 text, re.M))
                  for text in self.fx.md_files.values())
        gfm = 0
        html = 0
        for text in self.fx.md_files.values():
            gfm += len(re.findall(r"^\|(?:\s*---\s*\|)+\s*$", text, re.M))
            html += text.count("<table>")
        self.assertEqual(gfm + html, EXPECTED["tables"],
                         "tabelas descartadas ou duplicadas")

    def test_internal_links_resolve(self):
        anchors = {}
        for name, text in self.fx.md_files.items():
            anchors[name] = {
                convert_runbook.github_anchor(m.group(2))
                for m in re.finditer(r"^(#{1,6}) (.+)$", text, re.M)
            }
        link_re = re.compile(r"\]\((?!https?://)([^)#]+)?(?:#([^)]+))?\)")
        for src_name, text in {**self.fx.md_files,
                               "README.md": self.fx.index}.items():
            for m in link_re.finditer(text):
                target, anchor = m.group(1), m.group(2)
                if target in ("conversion-report.md",):
                    continue  # escrito manualmente após a conversão
                if target:
                    self.assertTrue((self.fx.out / target).is_file(),
                                    f"{src_name}: link quebrado -> {target}")
                if anchor:
                    file_anchors = anchors.get(target or src_name, set())
                    self.assertIn(anchor, file_anchors,
                                  f"{src_name}: âncora quebrada -> "
                                  f"{target}#{anchor}")

    def test_images_referenced(self):
        refs = []
        for text in self.fx.md_files.values():
            refs.extend(re.findall(r"!\[[^\]]*\]\((images/[^)]+)\)", text))
        self.assertEqual(len(refs), EXPECTED["images"])
        for ref in refs:
            self.assertTrue((self.fx.out / ref).is_file(),
                            f"imagem referenciada inexistente: {ref}")


class TestReproducibility(unittest.TestCase):
    """A saída commitada em docs/runbook é exatamente a do conversor."""

    @classmethod
    def setUpClass(cls):
        cls.fx = ConversionFixture.get()

    def test_committed_output_matches_fresh_conversion(self):
        fresh = {p.relative_to(self.fx.out).as_posix(): p.read_bytes()
                 for p in sorted(self.fx.out.rglob("*")) if p.is_file()}
        for rel, data in fresh.items():
            committed = COMMITTED / rel
            self.assertTrue(committed.is_file(),
                            f"faltando em docs/runbook: {rel}")
            self.assertEqual(committed.read_bytes(), data,
                             f"docs/runbook/{rel} difere da reconversão — "
                             "rode tools/convert_runbook.py")

    def test_manifest_source_hash_matches_docx(self):
        import hashlib
        self.assertEqual(
            self.fx.manifest["sourceSha256"],
            hashlib.sha256(SOURCE.read_bytes()).hexdigest())


if __name__ == "__main__":
    unittest.main()
