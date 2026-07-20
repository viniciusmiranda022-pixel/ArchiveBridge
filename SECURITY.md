# Política de Segurança

## Classificação

O conteúdo de engenharia deste repositório deriva do Runbook de Engenharia,
classificado como **Confidencial — engenharia e segurança**. A publicação
neste repositório foi autorizada formalmente pelo owner em 2026-07-20
(registro em `docs/runbook/conversion-report.md`).

## Regras invioláveis

- **Nenhum segredo no repositório**: senhas, SAS URLs, connection strings,
  chaves, certificados, tokens e identificadores reais de tenant/cliente
  são proibidos em código, docs, issues e histórico. Os comandos do runbook
  usam placeholders entre `< >` — mantenha-os assim.
- **Nenhum dado de migração**: arquivos PST/OST, exports do Enterprise
  Vault, CSVs de mapping reais e evidências de clientes jamais entram no
  repositório (ver `.gitignore`).
- Dependências e supply chain seguem a Parte V do runbook (seções 30–37).

## Reporte de vulnerabilidades

Reporte vulnerabilidades de forma privada via **GitHub Security Advisories**
(aba *Security* → *Report a vulnerability*) ou diretamente ao owner
(@viniciusmiranda022-pixel). Não abra issue pública com detalhes
exploráveis.

Se você suspeitar de segredo commitado (mesmo em histórico), trate como
incidente: siga o runbook operacional 42.6 ("Suspeita de segredo em log") —
revogar/rotacionar primeiro, limpar depois.
