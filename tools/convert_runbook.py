#!/usr/bin/env python3
"""Conversor determinístico do Runbook de Engenharia (DOCX) para Markdown.

Uso:
    python3 tools/convert_runbook.py [--source DOCX] [--out DIR]

Somente biblioteca padrão. Lê ``word/document.xml`` na ordem do corpo
(``w:p`` e ``w:tbl``), roteia cada bloco para um arquivo Markdown via
``OUTPUT_ROUTING`` e registra todos os blocos em
``conversion-manifest.json`` (body blocks = w:p + w:tbl; desenhos e
hyperlinks entram como ``children`` do bloco pai, nunca como blocos
independentes).

A numeração das listas reproduz a numeração real do documento: o estilo
``ListNumber`` referencia um único ``numId`` sem restart, portanto todos
os itens numerados formam uma sequência contínua que atravessa capítulos
(verificado contra a renderização do PDF: 1-5, ..., 36-45, ...).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

W = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
R = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}"
A = "{http://schemas.openxmlformats.org/drawingml/2006/main}"
WP = "{http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing}"
PKG_REL = "{http://schemas.openxmlformats.org/package/2006/relationships}"

# Roteamento determinístico dos 13 Heading1 do DOCX. O conversor falha se
# encontrar um Heading1 fora deste mapa ou se algum título obrigatório não
# aparecer no documento.
OUTPUT_ROUTING = {
    "Mapa do documento": "00-mapa-do-documento.md",
    "Parte I - Decisão de produto e limites reais da plataforma":
        "01-parte-i-decisao-de-produto.md",
    "Parte II - Arquitetura e organização do software":
        "02-parte-ii-arquitetura.md",
    "Parte III - Conectores de origem e engine PST":
        "03-parte-iii-conectores-e-engine-pst.md",
    "Parte IV - Destinos Microsoft 365":
        "04-parte-iv-destinos-m365.md",
    "Parte V - Segurança, infraestrutura e operação":
        "05-parte-v-seguranca-infra-operacao.md",
    "Parte VI - Plano de desenvolvimento e aceitação de produção":
        "06-parte-vi-plano-desenvolvimento.md",
    "Apêndice A - DDL de referência": "07-apendices.md",
    "Apêndice B - Manifesto de partição": "07-apendices.md",
    "Apêndice C - Códigos de erro": "07-apendices.md",
    "Apêndice D - Checklist diário do operador": "07-apendices.md",
    "Apêndice E - Pacote de evidência final": "07-apendices.md",
    "Apêndice F - Referências oficiais e source of truth": "07-apendices.md",
}

# Blocos anteriores ao primeiro Heading1 (capa: título, versão, data,
# classificação, status, plataforma, runtime, princípio de projeto).
PREAMBLE_OUTPUT = "00-mapa-do-documento.md"

INDEX_OUTPUT = "README.md"
MANIFEST_OUTPUT = "conversion-manifest.json"
IMAGES_DIR = "images"

# Sombreado dos parágrafos de código no tema do documento.
CODE_FILL = "F5F7FA"

# Sombreados de caixas de destaque -> GitHub alerts.
CALLOUT_FILLS = {
    "FDECEC": "CAUTION",   # vermelho: bloqueio / decisão crítica
    "FFF4D6": "WARNING",   # amarelo: atenção
    "E8F1F8": "NOTE",      # azul: informação
    "EAF6ED": "TIP",       # verde: prática recomendada
    "F2F4F7": "IMPORTANT", # cinza: destaque neutro
}

# Rótulo em negrito na primeira linha do bloco de código -> linguagem do fence.
CODE_LANGS = {
    "TEXT": "text",
    "POWERSHELL": "powershell",
    "SQL": "sql",
    "HTTP": "http",
    "JSON": "json",
    "CSHARP": "csharp",
    "CSV": "csv",
    "BICEP": "bicep",
    "BASH": "bash",
    "YAML": "yaml",
    "XML": "xml",
    "KQL": "kql",
}
CODE_LANG_FALLBACK = "text"

MONO_FONTS = {"Cascadia Mono", "Cascadia Code", "Consolas", "Courier New"}

GENERATED_NOTICE = (
    "<!-- Gerado por tools/convert_runbook.py a partir de "
    "docs/source/Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.docx. "
    "Não editar manualmente: alterações devem ser feitas no DOCX e "
    "reconvertidas. -->"
)


def norm_text(text: str) -> str:
    """Normaliza texto para hashing: colapsa espaços em branco."""
    return re.sub(r"\s+", " ", text).strip()


def sha256_hex(data) -> str:
    if isinstance(data, str):
        data = data.encode("utf-8")
    return hashlib.sha256(data).hexdigest()


def escape_md(text: str) -> str:
    """Escapa caracteres com significado em Markdown no texto corrido.

    ``<`` e ``>`` são obrigatórios: o runbook usa placeholders como
    ``<TenantId>`` que o GFM interpretaria como tag HTML e ocultaria.
    """
    return re.sub(r"([\\`*_\[\]<>])", r"\\\1", text)


def escape_table_cell(text: str) -> str:
    return escape_md(text).replace("|", "\\|")


def github_anchor(title: str) -> str:
    """Anchor de heading no estilo GitHub."""
    anchor = title.strip().lower()
    anchor = re.sub(r"[^\w\sÀ-ɏ-]", "", anchor, flags=re.UNICODE)
    anchor = anchor.replace(" ", "-")
    return anchor


class Run:
    __slots__ = ("text", "bold", "italic", "mono", "href")

    def __init__(self, text, bold=False, italic=False, mono=False, href=None):
        self.text = text
        self.bold = bold
        self.italic = italic
        self.mono = mono
        self.href = href


class Converter:
    def __init__(self, source: Path, out_dir: Path):
        self.source = source
        self.out_dir = out_dir
        self.zipf = zipfile.ZipFile(source)
        self.doc = ET.fromstring(self.zipf.read("word/document.xml"))
        self.body = self.doc.find(W + "body")
        self.relmap = self._load_rels()
        # Um único contador contínuo para o estilo ListNumber (numId único,
        # sem startOverride) — reproduz a numeração real do documento.
        self.list_number_counter = 0
        self.image_counter = 0
        self.image_files: dict[str, str] = {}  # rId -> caminho relativo salvo
        self.manifest_blocks: list[dict] = []
        # outputFile -> lista de linhas markdown
        self.outputs: dict[str, list[str]] = {}
        # outputFile -> [(nível, título)] para o índice
        self.headings_by_file: dict[str, list[tuple[int, str]]] = {}
        self.current_output = PREAMBLE_OUTPUT
        self.seen_h1: list[str] = []
        self.errors: list[str] = []

    # ------------------------------------------------------------------
    def _load_rels(self):
        rels = ET.fromstring(self.zipf.read("word/_rels/document.xml.rels"))
        out = {}
        for rel in rels.findall(PKG_REL + "Relationship"):
            out[rel.get("Id")] = {
                "type": rel.get("Type").rsplit("/", 1)[-1],
                "target": rel.get("Target"),
            }
        return out

    # ------------------------------------------------------------------
    def convert(self):
        blocks = [el for el in self.body
                  if el.tag in (W + "p", W + "tbl")]
        idx = 0
        while idx < len(blocks):
            el = blocks[idx]
            if el.tag == W + "tbl":
                self._emit_table(el, idx)
                idx += 1
                continue
            kind = self._classify(el)
            if kind == "code":
                idx = self._emit_code_group(blocks, idx)
                continue
            if kind == "heading":
                self._emit_heading(el, idx)
            elif kind == "callout":
                self._emit_callout(el, idx)
            elif kind == "list":
                self._emit_list_item(el, idx)
            elif kind == "empty":
                self._register(idx, "paragraph", self._style(el), "",
                               status="ignored",
                               justification="parágrafo vazio (espaçamento visual)")
            else:
                self._emit_prose(el, idx)
            idx += 1

        self._check_routing()
        self._write_outputs()
        self._write_index()
        self._write_manifest()
        if self.errors:
            for err in self.errors:
                print("ERRO:", err, file=sys.stderr)
            raise SystemExit(1)

    # ------------------------------------------------------------------
    def _style(self, p) -> str:
        pPr = p.find(W + "pPr")
        if pPr is None:
            return ""
        s = pPr.find(W + "pStyle")
        return s.get(W + "val") if s is not None else ""

    def _fill(self, p) -> str:
        pPr = p.find(W + "pPr")
        if pPr is None:
            return ""
        shd = pPr.find(W + "shd")
        return (shd.get(W + "fill") or "") if shd is not None else ""

    def _plain_text(self, el) -> str:
        return "".join(t.text or "" for t in el.iter(W + "t"))

    def _has_drawing(self, p) -> bool:
        return p.find(f".//{A}blip") is not None

    def _classify(self, p) -> str:
        style = self._style(p)
        if style in ("Heading1", "Heading2", "Heading3"):
            return "heading"
        fill = self._fill(p)
        if fill == CODE_FILL:
            return "code"
        if fill in CALLOUT_FILLS:
            return "callout"
        if style in ("ListBullet", "ListNumber"):
            return "list"
        if not self._plain_text(p).strip() and not self._has_drawing(p):
            return "empty"
        return "prose"

    # ------------------------------------------------------------------
    def _register(self, idx, source_type, style, text, *, status="converted",
                  justification=None, children=None, block_kind=None,
                  output_file=None):
        entry = {
            "sourceIndex": idx,
            "sourceType": source_type,
            "style": style or None,
            "normalizedTextSha256": sha256_hex(norm_text(text)),
            "textPreview": norm_text(text)[:100],
            "outputFile": output_file
            if output_file is not None else self.current_output,
            "status": status,
        }
        if block_kind:
            entry["blockKind"] = block_kind
        if justification:
            entry["justification"] = justification
        if children:
            entry["children"] = children
        self.manifest_blocks.append(entry)

    def _register_generated(self, output_file, description):
        self.manifest_blocks.append({
            "sourceType": "generated",
            "status": "generated",
            "excludedFromSourceCounts": True,
            "outputFile": output_file,
            "description": description,
        })

    def _lines(self, output_file=None) -> list[str]:
        name = output_file or self.current_output
        if name not in self.outputs:
            self.outputs[name] = [GENERATED_NOTICE, ""]
            self._register_generated(
                name, "aviso de arquivo gerado (cabeçalho do conversor)")
        return self.outputs[name]

    # ------------------------------------------------------------------
    def _runs(self, p) -> list[Run]:
        """Extrai runs com formatação, resolvendo hyperlinks e quebras."""
        runs: list[Run] = []

        def walk(node, href):
            for child in node:
                tag = child.tag
                if tag == W + "r":
                    rPr = child.find(W + "rPr")
                    bold = italic = mono = False
                    if rPr is not None:
                        bold = rPr.find(W + "b") is not None
                        italic = rPr.find(W + "i") is not None
                        fonts = rPr.find(W + "rFonts")
                        if fonts is not None:
                            mono = fonts.get(W + "ascii") in MONO_FONTS
                    for piece in child:
                        ptag = piece.tag
                        if ptag == W + "t":
                            runs.append(Run(piece.text or "", bold, italic,
                                            mono, href))
                        elif ptag == W + "br":
                            runs.append(Run("\n", bold, italic, mono, href))
                        elif ptag == W + "tab":
                            runs.append(Run("\t", bold, italic, mono, href))
                elif tag == W + "hyperlink":
                    rid = child.get(R + "id")
                    target = None
                    if rid and rid in self.relmap:
                        target = self.relmap[rid]["target"]
                    walk(child, target)

        walk(p, None)
        return runs

    def _hyperlink_children(self, p) -> list[dict]:
        children = []
        for h in p.iter(W + "hyperlink"):
            rid = h.get(R + "id")
            if rid and rid in self.relmap:
                children.append({
                    "type": "hyperlink",
                    "relationshipId": rid,
                    "target": self.relmap[rid]["target"],
                })
        return children

    def _render_inline(self, runs: list[Run], escape=True,
                       table_cell=False) -> str:
        """Renderiza runs como Markdown inline, agrupando formatação igual."""
        out: list[str] = []
        i = 0
        esc = escape_table_cell if table_cell else escape_md
        while i < len(runs):
            run = runs[i]
            if run.text == "\n":
                out.append("<br>" if table_cell else "  \n")
                i += 1
                continue
            j = i
            group = []
            while (j < len(runs) and runs[j].text != "\n"
                   and runs[j].bold == run.bold
                   and runs[j].italic == run.italic
                   and runs[j].mono == run.mono
                   and runs[j].href == run.href):
                group.append(runs[j].text)
                j += 1
            text = "".join(group)
            if not text:
                i = j
                continue
            lead = text[: len(text) - len(text.lstrip())]
            trail = text[len(text.rstrip()):]
            core = text.strip()
            if not core:
                out.append(text)
                i = j
                continue
            if run.mono:
                rendered = f"`{core}`"
            else:
                rendered = esc(core) if escape else core
                if run.bold:
                    rendered = f"**{rendered}**"
                if run.italic:
                    rendered = f"*{rendered}*"
            if run.href:
                rendered = f"[{rendered}]({run.href})"
            out.append(lead + rendered + trail)
            i = j
        return "".join(out)

    # ------------------------------------------------------------------
    def _emit_heading(self, p, idx):
        style = self._style(p)
        level = int(style[-1])
        title = norm_text(self._plain_text(p))
        if style == "Heading1":
            if title not in OUTPUT_ROUTING:
                self.errors.append(
                    f"Heading1 não mapeado em OUTPUT_ROUTING: {title!r}")
                self._register(idx, "paragraph", style, title,
                               status="ignored",
                               justification="Heading1 sem roteamento",
                               block_kind="heading1")
                return
            self.seen_h1.append(title)
            self.current_output = OUTPUT_ROUTING[title]
        lines = self._lines()
        if lines and lines[-1] != "":
            lines.append("")
        lines.append("#" * level + " " + title)
        lines.append("")
        self.headings_by_file.setdefault(self.current_output, []).append(
            (level, title))
        self._register(idx, "paragraph", style, title,
                       block_kind=f"heading{level}")

    # ------------------------------------------------------------------
    def _emit_code_group(self, blocks, idx) -> int:
        """Agrupa parágrafos de código consecutivos em um único fence."""
        content_lines: list[str] = []
        lang = None
        first_idx = idx
        indices = []
        while idx < len(blocks):
            el = blocks[idx]
            if el.tag != W + "p" or self._classify(el) != "code":
                break
            runs = self._runs(el)
            text = "".join(r.text for r in runs)
            para_lines = text.split("\n")
            # Rótulo de linguagem: primeira linha, run inicial em negrito,
            # só maiúsculas/dígitos (ex.: POWERSHELL, CSHARP, JSON).
            label = None
            if runs and runs[0].bold:
                candidate = para_lines[0].strip()
                if re.fullmatch(r"[A-Z][A-Z0-9#+]{1,15}", candidate):
                    label = candidate
            if label is not None:
                if content_lines:
                    # Novo rótulo abre um novo bloco: fecha o atual antes.
                    break
                lang = CODE_LANGS.get(label, CODE_LANG_FALLBACK)
                para_lines = para_lines[1:]
            content_lines.extend(para_lines)
            indices.append(idx)
            idx += 1
        if lang is None:
            lang = CODE_LANG_FALLBACK
        while content_lines and not content_lines[-1].strip():
            content_lines.pop()
        while content_lines and not content_lines[0].strip():
            content_lines.pop(0)
        content = "\n".join(content_lines)
        longest_ticks = max(
            (len(m.group(0)) for m in re.finditer(r"`+", content)), default=0)
        fence = "`" * max(3, longest_ticks + 1)
        lines = self._lines()
        if lines and lines[-1] != "":
            lines.append("")
        lines.append(f"{fence}{lang}")
        lines.extend(content_lines)
        lines.append(fence)
        lines.append("")
        for k, block_index in enumerate(indices):
            self._register(
                block_index, "paragraph", self._style(blocks[block_index]),
                self._plain_text(blocks[block_index]),
                block_kind="code",
                justification=None if k == 0 else
                "continuação do bloco de código iniciado no parágrafo "
                f"{indices[0]}")
        return idx

    # ------------------------------------------------------------------
    def _emit_callout(self, p, idx):
        fill = self._fill(p)
        alert = CALLOUT_FILLS[fill]
        rendered = self._render_inline(self._runs(p))
        lines = self._lines()
        if lines and lines[-1] != "":
            lines.append("")
        lines.append(f"> [!{alert}]")
        for row in rendered.split("\n"):
            lines.append(("> " + row.strip()).rstrip())
        lines.append("")
        self._register(idx, "paragraph", self._style(p), self._plain_text(p),
                       block_kind="callout",
                       children=self._hyperlink_children(p) or None)

    # ------------------------------------------------------------------
    def _emit_list_item(self, p, idx):
        style = self._style(p)
        rendered = self._render_inline(self._runs(p)).replace("  \n", " ")
        if style == "ListNumber":
            self.list_number_counter += 1
            prefix = f"{self.list_number_counter}. "
            kind = "number-item"
        else:
            prefix = "- "
            kind = "bullet-item"
        self._lines().append(prefix + rendered.strip())
        self._register(idx, "paragraph", style, self._plain_text(p),
                       block_kind=kind,
                       children=self._hyperlink_children(p) or None)

    # ------------------------------------------------------------------
    def _emit_prose(self, p, idx):
        children = []
        lines = self._lines()
        if lines and lines[-1] != "":
            lines.append("")
        if self._has_drawing(p):
            for blip in p.iter(A + "blip"):
                rid = blip.get(R + "embed")
                path = self._save_image(rid, idx)
                alt = self._drawing_alt(p) or f"Diagrama {self.image_counter}"
                lines.append(f"![{alt}]({path})")
                children.append({
                    "type": "drawing",
                    "relationshipId": rid,
                    "outputPath": path,
                })
        rendered = self._render_inline(self._runs(p)).strip()
        if rendered:
            # Um número no início de linha viraria item de lista em GFM.
            rendered = re.sub(r"^(\d+)\.", r"\1\\.", rendered)
            lines.append(rendered)
        lines.append("")
        children.extend(self._hyperlink_children(p))
        self._register(idx, "paragraph", self._style(p), self._plain_text(p),
                       block_kind="image-anchor" if children and
                       children[0].get("type") == "drawing" else "prose",
                       children=children or None)

    def _drawing_alt(self, p):
        for docpr in p.iter(WP + "docPr"):
            descr = docpr.get("descr") or docpr.get("name")
            if descr:
                return descr
        return None

    def _save_image(self, rid, idx) -> str:
        if rid in self.image_files:
            return self.image_files[rid]
        rel = self.relmap.get(rid)
        if rel is None or rel["type"] != "image":
            self.errors.append(
                f"Bloco {idx}: relacionamento de imagem sem destino: {rid}")
            return ""
        self.image_counter += 1
        ext = Path(rel["target"]).suffix or ".png"
        rel_path = f"{IMAGES_DIR}/diagram-{self.image_counter}{ext}"
        target = self.out_dir / rel_path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(self.zipf.read("word/" + rel["target"]))
        self.image_files[rid] = rel_path
        return rel_path

    # ------------------------------------------------------------------
    def _emit_table(self, tbl, idx):
        rows = []
        complex_table = False
        if tbl.findall(f".//{W}gridSpan") or tbl.findall(f".//{W}vMerge"):
            complex_table = True
        for tr in tbl.findall(W + "tr"):
            row = []
            for tc in tr.findall(W + "tc"):
                paras = tc.findall(W + "p")
                if len(paras) > 1:
                    complex_table = True
                cell_runs = []
                for k, cp in enumerate(paras):
                    if k:
                        cell_runs.append(Run("\n"))
                    cell_runs.extend(self._runs(cp))
                row.append(cell_runs)
            rows.append(row)
        lines = self._lines()
        if lines and lines[-1] != "":
            lines.append("")
        if complex_table:
            self._emit_html_table(tbl, rows, lines)
        else:
            header = [self._render_inline(c, table_cell=True).strip()
                      for c in rows[0]]
            lines.append("| " + " | ".join(header) + " |")
            lines.append("|" + "|".join(" --- " for _ in header) + "|")
            for row in rows[1:]:
                cells = [self._render_inline(c, table_cell=True).strip()
                         for c in row]
                while len(cells) < len(header):
                    cells.append("")
                lines.append("| " + " | ".join(cells) + " |")
        lines.append("")
        self._register(idx, "table", None, self._plain_text(tbl),
                       block_kind="table")

    def _emit_html_table(self, tbl, rows, lines):
        def html_escape(s):
            return (s.replace("&", "&amp;").replace("<", "&lt;")
                    .replace(">", "&gt;"))

        lines.append("<table>")
        for r_i, tr in enumerate(tbl.findall(W + "tr")):
            lines.append("  <tr>")
            for tc in tr.findall(W + "tc"):
                tcPr = tc.find(W + "tcPr")
                colspan = 1
                if tcPr is not None:
                    gs = tcPr.find(W + "gridSpan")
                    if gs is not None:
                        colspan = int(gs.get(W + "val"))
                    vm = tcPr.find(W + "vMerge")
                    if vm is not None and vm.get(W + "val") != "restart":
                        continue  # célula de continuação de merge vertical
                text = "<br>".join(
                    html_escape(self._plain_text(cp))
                    for cp in tc.findall(W + "p"))
                tag = "th" if r_i == 0 else "td"
                span = f' colspan="{colspan}"' if colspan > 1 else ""
                lines.append(f"    <{tag}{span}>{text}</{tag}>")
            lines.append("  </tr>")
        lines.append("</table>")

    # ------------------------------------------------------------------
    def _check_routing(self):
        expected = set(OUTPUT_ROUTING)
        seen = set(self.seen_h1)
        for missing in sorted(expected - seen):
            self.errors.append(f"Heading1 obrigatório ausente: {missing!r}")
        if len(self.seen_h1) != len(seen):
            dupes = sorted({t for t in self.seen_h1
                            if self.seen_h1.count(t) > 1})
            self.errors.append(f"Heading1 duplicado no documento: {dupes}")
        for entry in self.manifest_blocks:
            if entry.get("status") == "converted" and not entry["outputFile"]:
                self.errors.append(
                    f"Bloco {entry['sourceIndex']} sem arquivo de destino")

    # ------------------------------------------------------------------
    def _write_outputs(self):
        self.out_dir.mkdir(parents=True, exist_ok=True)
        for name, lines in self.outputs.items():
            while lines and lines[-1] == "":
                lines.pop()
            content = "\n".join(lines) + "\n"
            (self.out_dir / name).write_text(content, encoding="utf-8")

    def _write_index(self):
        ordered = sorted(set(OUTPUT_ROUTING.values()),
                         key=lambda n: n)
        lines = [
            GENERATED_NOTICE,
            "",
            "# Runbook de Engenharia — Plataforma de Migração EV/PST → M365",
            "",
            "Conversão fiel do documento original "
            "(`docs/source/Runbook_Engenharia_Plataforma_Migracao_EV_PST_"
            "M365.docx`, Confidencial — engenharia e segurança) para "
            "Markdown. Gerado por `tools/convert_runbook.py`; ver "
            "`conversion-report.md` e `conversion-manifest.json`.",
            "",
            "## Sumário",
            "",
        ]
        for name in ordered:
            headings = self.headings_by_file.get(name, [])
            h1s = [t for lvl, t in headings if lvl == 1]
            title = " / ".join(h1s) if h1s else name
            lines.append(f"- **[{title}]({name})**")
            for lvl, t in headings:
                if lvl == 2:
                    lines.append(f"  - [{t}]({name}#{github_anchor(t)})")
        lines += [
            "",
            "Relatório e auditoria da conversão: "
            "[conversion-report.md](conversion-report.md) · "
            "[conversion-manifest.json](conversion-manifest.json)",
        ]
        (self.out_dir / INDEX_OUTPUT).write_text(
            "\n".join(lines) + "\n", encoding="utf-8")
        self._register_generated(INDEX_OUTPUT,
                                 "índice do runbook gerado pelo conversor")

    def _write_manifest(self):
        source_blocks = [b for b in self.manifest_blocks
                         if b.get("sourceType") != "generated"]
        converted = [b for b in source_blocks if b["status"] == "converted"]
        ignored = [b for b in source_blocks if b["status"] == "ignored"]
        counts = {
            "bodyBlocks": len(source_blocks),
            "converted": len(converted),
            "ignored": len(ignored),
            "paragraphs": sum(1 for b in source_blocks
                              if b["sourceType"] == "paragraph"),
            "tables": sum(1 for b in source_blocks
                          if b["sourceType"] == "table"),
            "heading1": sum(1 for b in source_blocks
                            if b.get("style") == "Heading1"),
            "heading2": sum(1 for b in source_blocks
                            if b.get("style") == "Heading2"),
            "heading3": sum(1 for b in source_blocks
                            if b.get("style") == "Heading3"),
            "bulletItems": sum(1 for b in source_blocks
                               if b.get("blockKind") == "bullet-item"),
            "numberItems": sum(1 for b in source_blocks
                               if b.get("blockKind") == "number-item"),
            "codeParagraphs": sum(1 for b in source_blocks
                                  if b.get("blockKind") == "code"),
            "callouts": sum(1 for b in source_blocks
                            if b.get("blockKind") == "callout"),
            "images": len(self.image_files),
            "lastListNumber": self.list_number_counter,
        }
        manifest = {
            "generatedBy": "tools/convert_runbook.py",
            "source": "docs/source/"
                      "Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365"
                      ".docx",
            "sourceSha256": sha256_hex(self.source.read_bytes()),
            "counts": counts,
            "blocks": self.manifest_blocks,
        }
        (self.out_dir / MANIFEST_OUTPUT).write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    repo_root = Path(__file__).resolve().parent.parent
    parser.add_argument(
        "--source",
        default=repo_root / "docs" / "source" /
        "Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.docx",
        type=Path)
    parser.add_argument(
        "--out", default=repo_root / "docs" / "runbook", type=Path)
    args = parser.parse_args(argv)
    Converter(args.source, args.out).convert()
    print(f"Conversão concluída em {args.out}")


if __name__ == "__main__":
    main()
