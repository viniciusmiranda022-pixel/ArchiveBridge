<!-- Gerado por tools/convert_runbook.py a partir de docs/source/Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.docx. Não editar manualmente: alterações devem ser feitas no DOCX e reconvertidas. -->

# Runbook de Engenharia — Plataforma de Migração EV/PST → M365

Conversão fiel do documento original (`docs/source/Runbook_Engenharia_Plataforma_Migracao_EV_PST_M365.docx`, Confidencial — engenharia e segurança) para Markdown. Gerado por `tools/convert_runbook.py`; ver `conversion-report.md` e `conversion-manifest.json`.

## Sumário

- **[Mapa do documento](00-mapa-do-documento.md)**
  - [Como o desenvolvedor deve usar este runbook](00-mapa-do-documento.md#como-o-desenvolvedor-deve-usar-este-runbook)
- **[Parte I - Decisão de produto e limites reais da plataforma](01-parte-i-decisao-de-produto.md)**
  - [1. Mandato, resultado e regra de honestidade técnica](01-parte-i-decisao-de-produto.md#1-mandato-resultado-e-regra-de-honestidade-técnica)
  - [2. Correções obrigatórias ao documento-base](01-parte-i-decisao-de-produto.md#2-correções-obrigatórias-ao-documento-base)
  - [3. Matriz de capacidades do produto](01-parte-i-decisao-de-produto.md#3-matriz-de-capacidades-do-produto)
  - [4. Como superar Archive Shuttle sem propaganda vazia](01-parte-i-decisao-de-produto.md#4-como-superar-archive-shuttle-sem-propaganda-vazia)
  - [5. Requisitos funcionais e não funcionais](01-parte-i-decisao-de-produto.md#5-requisitos-funcionais-e-não-funcionais)
- **[Parte II - Arquitetura e organização do software](02-parte-ii-arquitetura.md)**
  - [6. Princípios arquiteturais](02-parte-ii-arquitetura.md#6-princípios-arquiteturais)
  - [7. Componentes e responsabilidades](02-parte-ii-arquitetura.md#7-componentes-e-responsabilidades)
  - [8. Estrutura do repositório](02-parte-ii-arquitetura.md#8-estrutura-do-repositório)
  - [9. Registros de decisão arquitetural antes do código](02-parte-ii-arquitetura.md#9-registros-de-decisão-arquitetural-antes-do-código)
  - [10. Preparação da estação do desenvolvedor](02-parte-ii-arquitetura.md#10-preparação-da-estação-do-desenvolvedor)
  - [11. Modelo de domínio e invariantes](02-parte-ii-arquitetura.md#11-modelo-de-domínio-e-invariantes)
  - [12. Persistência, concorrência e esquema de banco](02-parte-ii-arquitetura.md#12-persistência-concorrência-e-esquema-de-banco)
  - [13. Contratos HTTP](02-parte-ii-arquitetura.md#13-contratos-http)
  - [14. Mensageria e execução durável](02-parte-ii-arquitetura.md#14-mensageria-e-execução-durável)
- **[Parte III - Conectores de origem e engine PST](03-parte-iii-conectores-e-engine-pst.md)**
  - [15. Conector de origem: desenho seguro](03-parte-iii-conectores-e-engine-pst.md#15-conector-de-origem-desenho-seguro)
  - [16. Inventário e exportação do Enterprise Vault](03-parte-iii-conectores-e-engine-pst.md#16-inventário-e-exportação-do-enterprise-vault)
  - [17. Ingestão de PST já existente](03-parte-iii-conectores-e-engine-pst.md#17-ingestão-de-pst-já-existente)
  - [18. Seleção da engine PST](03-parte-iii-conectores-e-engine-pst.md#18-seleção-da-engine-pst)
  - [19. Inspeção estrutural](03-parte-iii-conectores-e-engine-pst.md#19-inspeção-estrutural)
  - [20. Planejamento e particionamento](03-parte-iii-conectores-e-engine-pst.md#20-planejamento-e-particionamento)
  - [21. Fingerprint e cadeia de custódia por item](03-parte-iii-conectores-e-engine-pst.md#21-fingerprint-e-cadeia-de-custódia-por-item)
  - [22. Corrupção, reparo e quarentena](03-parte-iii-conectores-e-engine-pst.md#22-corrupção-reparo-e-quarentena)
  - [23. Validação independente](03-parte-iii-conectores-e-engine-pst.md#23-validação-independente)
- **[Parte IV - Destinos Microsoft 365](04-parte-iv-destinos-m365.md)**
  - [24. Strategy e capability gates](04-parte-iv-destinos-m365.md#24-strategy-e-capability-gates)
  - [25. Adapter Purview Network Upload - caminho GA](04-parte-iv-destinos-m365.md#25-adapter-purview-network-upload---caminho-ga)
  - [26. Reconciliador do Purview](04-parte-iv-destinos-m365.md#26-reconciliador-do-purview)
  - [27. Cenários acima de 100 GB no mesmo archive](04-parte-iv-destinos-m365.md#27-cenários-acima-de-100-gb-no-mesmo-archive)
  - [28. Adapter Graph Mailbox Import/Export - trilha estratégica bloqueada](04-parte-iv-destinos-m365.md#28-adapter-graph-mailbox-importexport---trilha-estratégica-bloqueada)
  - [29. Adapter contratual de ingestão rápida](04-parte-iv-destinos-m365.md#29-adapter-contratual-de-ingestão-rápida)
- **[Parte V - Segurança, infraestrutura e operação](05-parte-v-seguranca-infra-operacao.md)**
  - [30. Threat model e ativos](05-parte-v-seguranca-infra-operacao.md#30-threat-model-e-ativos)
  - [31. Identidade e segregação de funções](05-parte-v-seguranca-infra-operacao.md#31-identidade-e-segregação-de-funções)
  - [32. Segredos e material criptográfico](05-parte-v-seguranca-infra-operacao.md#32-segredos-e-material-criptográfico)
  - [33. Storage e ciclo de vida](05-parte-v-seguranca-infra-operacao.md#33-storage-e-ciclo-de-vida)
  - [34. Hardening dos workers Windows](05-parte-v-seguranca-infra-operacao.md#34-hardening-dos-workers-windows)
  - [35. Malware e conteúdo hostil](05-parte-v-seguranca-infra-operacao.md#35-malware-e-conteúdo-hostil)
  - [36. Infraestrutura como código](05-parte-v-seguranca-infra-operacao.md#36-infraestrutura-como-código)
  - [37. CI/CD e supply chain](05-parte-v-seguranca-infra-operacao.md#37-cicd-e-supply-chain)
  - [38. Configuração de aplicação](05-parte-v-seguranca-infra-operacao.md#38-configuração-de-aplicação)
  - [39. Observabilidade](05-parte-v-seguranca-infra-operacao.md#39-observabilidade)
  - [40. SLO, RTO e RPO](05-parte-v-seguranca-infra-operacao.md#40-slo-rto-e-rpo)
  - [41. Backup e disaster recovery](05-parte-v-seguranca-infra-operacao.md#41-backup-e-disaster-recovery)
  - [42. Runbooks operacionais](05-parte-v-seguranca-infra-operacao.md#42-runbooks-operacionais)
- **[Parte VI - Plano de desenvolvimento e aceitação de produção](06-parte-vi-plano-desenvolvimento.md)**
  - [43. Estratégia de entrega sem protótipo descartável](06-parte-vi-plano-desenvolvimento.md#43-estratégia-de-entrega-sem-protótipo-descartável)
  - [44. Backlog por épico](06-parte-vi-plano-desenvolvimento.md#44-backlog-por-épico)
  - [45. Test strategy](06-parte-vi-plano-desenvolvimento.md#45-test-strategy)
  - [46. Performance e dimensionamento](06-parte-vi-plano-desenvolvimento.md#46-performance-e-dimensionamento)
  - [47. Production readiness review](06-parte-vi-plano-desenvolvimento.md#47-production-readiness-review)
  - [48. Canário de produção](06-parte-vi-plano-desenvolvimento.md#48-canário-de-produção)
  - [49. Critérios de encerramento de uma migração](06-parte-vi-plano-desenvolvimento.md#49-critérios-de-encerramento-de-uma-migração)
  - [50. Decisão final para o caso de 500 GB](06-parte-vi-plano-desenvolvimento.md#50-decisão-final-para-o-caso-de-500-gb)
- **[Apêndice A - DDL de referência / Apêndice B - Manifesto de partição / Apêndice C - Códigos de erro / Apêndice D - Checklist diário do operador / Apêndice E - Pacote de evidência final / Apêndice F - Referências oficiais e source of truth](07-apendices.md)**

Relatório e auditoria da conversão: [conversion-report.md](conversion-report.md) · [conversion-manifest.json](conversion-manifest.json)
