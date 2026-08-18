# ArchiveBridge — Roteiro de Demonstração ao Cliente (10–15 min)

> Executar em **Modo de Demonstração** (`PresentationMode:Enabled=true`, `Development`). Todos os dados são
> **sintéticos** (Contoso Demo). O banner âmbar no topo deixa isso explícito o tempo todo.
>
> **Regra de ouro:** não afirmar que Exportação, Staging, Importação Microsoft 365 ou Reconciliação já estão
> implementadas. Elas aparecem claramente como **etapas futuras**.

---

## 0. Antes de começar (30s)

Abrir o portal já logado como **Vinicius Miranda (Administrator)**, na tela **Dashboard**, resolução 1440×900.
Apontar o **banner de demonstração**: "os dados desta sessão são simulados". Isso protege a conversa: nada aqui
é um cliente ou mailbox real.

## 1. Login (30s)

- **Falar:** "Este é o portal operacional do ArchiveBridge — a plataforma que controla toda a migração de
  arquivos do Enterprise Vault para o Microsoft 365."
- **Destacar:** identidade de produto (marca, subtítulo "Enterprise Archive Migration Platform"), acesso
  restrito por usuário/senha.
- **Não prometer:** nada ainda.

## 2. Dashboard / Visão Geral (2–3 min) — tela mais importante

- **Falar:** "Em uma tela, o gestor vê o estado da migração." Ler os indicadores (Projetos, Ondas, Ambientes,
  Jobs, Evidências, Validações).
- **Destacar o Pipeline de Migração** (8 etapas): Descoberta/Planejamento/Mapping **concluídos**, Validação
  **em andamento**; e — muito importante — **Exportação/Staging/Importação M365/Reconciliação** aparecem como
  **"Planejado"** e **"Não disponível nesta versão"**. "A plataforma é honesta sobre o que já faz e o que vem
  a seguir."
- **Destacar Status da Plataforma e Atividade recente** (rastreabilidade em tempo quase real).
- **Não prometer:** que as 4 etapas finais já executam. Elas são o roadmap.

## 3. Projeto (1 min)

- **Falar:** "Cada projeto reúne o tenant de destino, a política de arquivamento, as ondas e a cadeia de
  custódia."
- **Destacar:** as **abas** (Visão Geral, Ondas, Enterprise Vault, Jobs, Governança) e o badge de status.
- **Não prometer:** edição/execução — esta rodada é de leitura/observabilidade.

## 4. Onda de Migração (1 min)

- **Falar:** "Uma onda é um lote planejado — quais arquivos, qual volume, para onde."
- **Destacar:** volume legível (ex.: 1.8 TB), itens, PSTs, **linha do tempo do ciclo de vida**
  (Draft → Validação → Aprovação → Frozen → Execução) e onde ela está.
- **Não prometer:** que a execução (Frozen → Execução real) já roda.

## 5. Enterprise Vault (2 min) — tela central

- **Falar:** "Antes de migrar, a plataforma **descobre** as capacidades do ambiente de origem."
- **Destacar:** cards por ambiente com **Ready / Blocked** bem visíveis; capacidade detectada; e a mensagem
  **"operação somente leitura — nenhum dado é exportado do Enterprise Vault"**.
- **Não prometer:** exportação. Deixar claro: "Descoberta lê o ambiente; **não** move nem exporta nada."

## 6. Mapping (1–2 min)

- **Falar:** "O mapping define a correspondência entre os PSTs de origem e o arquivo online correspondente no
  Microsoft 365."
- **Destacar:** mapping atual (versão, linhas, hash, status **Congelado**), histórico de versões e o painel de
  **Validação de CSV** — que existe visualmente, com o botão **desabilitado** e a nota
  *"Validação pelo Portal estará disponível em uma próxima etapa."*
- **Não prometer:** upload/validação via Portal agora.

## 7. Evidências (1–2 min)

- **Falar:** "Tudo que a plataforma produz vira **evidência imutável** — uma cadeia de custódia."
- **Destacar:** origem, versão, resultado, **SHA-256 abreviado com copiar**, download **verificado** (a
  plataforma confere hash/tamanho antes de entregar). Nenhum caminho físico é exposto.
- **Não prometer:** nada além do que está listado.

## 8. Jobs (1 min)

- **Falar:** "A operação é assíncrona e resiliente — uma fila durável de trabalho."
- **Destacar:** cards por estado (Pending/Processing/Retry/Completed/Failed) e, em **Job Details**, a **linha do
  tempo** das transições. Detalhes internos ficam recolhidos.
- **Não prometer:** capacidades de Slice futuro.

## 9. Auditoria (1 min)

- **Falar:** "Cada ação e cada login ficam auditados, por tenant e por projeto."
- **Destacar:** abas Operacional / Autenticação, badges de resultado (Sucesso/Negado/Falha).

## 10. Roadmap / Fechamento (1 min)

- **Falar:** "O que vem a seguir — exportação controlada, staging, importação para o Microsoft 365 e
  reconciliação — está no roadmap e aparece no pipeline como **planejado**."
- **Destacar:** o rodapé "ArchiveBridge **Preview**" e a postura honesta da plataforma.
- **Fechar com:** "Hoje a plataforma já **descobre, planeja, mapeia e valida** com governança e custódia
  completas; as etapas de execução são as próximas."

---

## Perguntas prováveis — respostas seguras

- *"Já migra para o M365?"* → "A importação para o Microsoft 365 é uma **etapa planejada**; nesta versão a
  plataforma cobre descoberta, planejamento, mapping e validação com custódia."
- *"Esses dados são de um cliente?"* → "Não — é um **dataset de demonstração** (Contoso Demo), sinalizado pelo
  banner. Nenhum dado real é usado."
- *"É seguro?"* → "Sim: autenticação, papéis (RBAC), auditoria, isolamento por tenant/projeto e evidência
  verificada. A descoberta é somente leitura."
