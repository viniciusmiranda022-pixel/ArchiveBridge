<!-- Gerado por tools/convert_runbook.py a partir de docs/source/Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.docx. Não editar manualmente: alterações devem ser feitas no DOCX e reconvertidas. -->

**PLATAFORMA DE MIGRAÇÃO**  
**EV / PST → ONLINE ARCHIVE**

Runbook de arquitetura, desenvolvimento, segurança, implantação e operação de produção

| **Versão** | 1.0 - baseline de engenharia |
| --- | --- |
| **Data** | 20 de julho de 2026 |
| **Classificação** | Confidencial - engenharia e segurança |
| **Status** | Aprovado para execução do backlog, sujeito aos gates externos |
| **Plataforma alvo** | Microsoft Azure + Microsoft 365 / Exchange Online |
| **Runtime** | .NET 10 LTS; workers Windows isolados |
| **Documento-base** | Sistema de migração de PSTs legados para o Online Archive do Microsoft 365 |

**Princípio de projeto**

**Nenhum item é considerado migrado sem origem identificada, hash verificável, destino autorizado, resultado persistido e reconciliação concluída.**

# Mapa do documento

- Parte I - decisão de produto, limites da Microsoft e critérios para superar Archive Shuttle.
- Parte II - arquitetura, repositório, contratos, estados, banco e mensageria.
- Parte III - conectores Enterprise Vault/PST, engine PST e cadeia de custódia.
- Parte IV - destinos Microsoft 365: Purview GA, Graph FTS e adapters contratuais.
- Parte V - segurança, infraestrutura como código, CI/CD, observabilidade e recuperação.
- Parte VI - plano de execução, backlog, testes, critérios de produção e runbooks.
- Apêndices - comandos, DDL, payloads, manifestos, CSVs e referências oficiais.

> [!CAUTION]
> **BLOQUEIO / DECISÃO CRÍTICA**
> Este documento substitui a hipótese de que um PST de 500 GB pode ser importado ao mesmo archive por cinco ondas Purview de 100 GB. A documentação oficial atual limita o caminho GA e exige avaliação da Microsoft acima de 100 GB. O produto bloqueará esse cenário até existir um conector de ingestão suportado.

## Como o desenvolvedor deve usar este runbook

1. Ler primeiro os capítulos 1 a 5 e registrar dúvidas como ADR; não criar projeto ou tabela antes das decisões de domínio.
2. Executar a sequência de scaffolding exatamente uma vez em repositório limpo; toda alteração posterior passa por pull request.
3. Implementar por vertical slice e liberar somente quando o Definition of Done da etapa estiver integralmente verde.
4. Tratar comandos com placeholders entre sinais \< \> como modelos; nunca copiar segredos ou identificadores reais para o repositório.
5. Quando a documentação da Microsoft divergir do documento, bloquear a feature, registrar ADR e atualizar a matriz de capacidades.
