#!/usr/bin/env python3
"""Gate de SCA fail-closed sobre a saída JSON de `dotnet list package --vulnerable`.

Falha (exit != 0) quando:
  - o arquivo JSON não pode ser interpretado          -> exit 2
  - o formato esperado está ausente/divergente        -> exit 3
  - existe qualquer pacote vulnerável                  -> exit 1

Sucesso (exit 0) apenas quando o JSON é válido, tem o formato esperado
(`--vulnerable` foi efetivamente executado) e nenhum pacote vulnerável é
reportado. Não depende de texto localizado do console.

Uso: python3 tools/sca_gate.py <caminho-do-json>
O comando `dotnet list ... --format json --output-version 1` deve ter sido
executado com sucesso antes; o erro de comando é verificado no CI (exit code).
"""
import json
import sys

EXIT_OK = 0
EXIT_VULNERABLE = 1
EXIT_UNPARSEABLE = 2
EXIT_FORMAT = 3


def fail(code: int, message: str) -> "int":
    print(f"[sca-gate] ERRO: {message}", file=sys.stderr)
    return code


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        return fail(EXIT_FORMAT, "uso: sca_gate.py <arquivo.json>")

    path = argv[1]
    try:
        with open(path, encoding="utf-8") as handle:
            data = json.load(handle)
    except FileNotFoundError:
        return fail(EXIT_FORMAT, f"arquivo não encontrado: {path}")
    except (json.JSONDecodeError, UnicodeDecodeError) as exc:
        return fail(EXIT_UNPARSEABLE, f"JSON não interpretável: {exc}")

    # Formato esperado, fail-closed se divergente.
    if not isinstance(data, dict):
        return fail(EXIT_FORMAT, "raiz do JSON não é um objeto")
    if data.get("version") != 1:
        return fail(EXIT_FORMAT, f"versão de saída inesperada: {data.get('version')!r} (esperado 1)")
    parameters = data.get("parameters")
    if not isinstance(parameters, str) or "--vulnerable" not in parameters:
        return fail(EXIT_FORMAT, "a varredura --vulnerable não consta nos parâmetros da saída")
    projects = data.get("projects")
    if not isinstance(projects, list) or not projects:
        return fail(EXIT_FORMAT, "lista de projetos ausente ou vazia")

    findings: list[str] = []
    for project in projects:
        if not isinstance(project, dict) or "path" not in project:
            return fail(EXIT_FORMAT, f"entrada de projeto sem 'path': {project!r}")
        project_path = project["path"]
        for framework in project.get("frameworks", []) or []:
            fw = framework.get("framework", "?")
            for bucket in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(bucket, []) or []:
                    vulns = package.get("vulnerabilities") or []
                    for vuln in vulns:
                        findings.append(
                            f"{project_path} [{fw}] {package.get('id', '?')} "
                            f"{package.get('resolvedVersion', '?')} "
                            f"severidade={vuln.get('severity', '?')} {vuln.get('advisoryurl', '')}"
                        )

    if findings:
        print("[sca-gate] pacotes vulneráveis detectados:")
        for finding in findings:
            print(f"  - {finding}")
        return fail(EXIT_VULNERABLE, f"{len(findings)} vulnerabilidade(s) — bloqueado")

    print(f"[sca-gate] OK: nenhum pacote vulnerável em {len(projects)} projetos.")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main(sys.argv))
