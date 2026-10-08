# PLANO.md — Sistema de Gestão de Estoque e Doações (Creche Fraternidade Menino Jesus)

> **Plan-to-code para o Claude Code.** Versão 0.1 — 06/10/2026
> Fontes: `Documento_Unificado_-_Trunna_Tech_-_24_09.pdf` (requisitos RF01–RF13, RNF01–RNF10, protótipos, BPMN, planilha manual) e `Cria_Banco.sql` (modelagem T-SQL).

---

## 0. Como usar este documento

1. Coloque na raiz do repositório: `PLANO.md` (este arquivo), `CLAUDE.md` (seção 13), e em `docs/referencia/` o `Cria_Banco.sql` original e o PDF do projeto.
2. Execute **uma fase por vez** (seção 8 e 9). Cada tarefa tem **Entregáveis** e **Critério de aceite**. Não avance de fase com critério de aceite falhando.
3. Pontos marcados **🚦 GATE** exigem validação humana antes de continuar.
4. Qualquer ambiguidade que não esteja resolvida na seção 12 deve ser registrada em `docs/DECISOES.md` (decisão + motivo) e, se bloquear, perguntada ao responsável.
5. Faça um commit por tarefa (`feat:`, `fix:`, `chore:`, `docs:`), com a aplicação sempre subindo via `docker compose up`.

### Decisões já tomadas (vindas do responsável pelo projeto)

| Tema | Decisão |
|---|---|
| Framework | ASP.NET Core, arquitetura **MVC** (RNF03) |
| Banco | **SQL Server Express 2025** em container (RNF02) |
| Entrega | **Tudo conteinerizado** (app, banco, migração) — deploy com um comando |
| Multi-tenant | Sim; cada tenant tem **matriz e filial(is)** (RF10) |
| Estratégia | **MVP** para deploy de teste (já com dependências e banco criado) → depois versão completa com boas práticas de segurança |
| Ambientes | (1) **Dev** na máquina do desenvolvedor; (2) **Ensaio** no **servidor caseiro Ubuntu** do desenvolvedor, por `localhost`/LAN, em HTTP, com dados fictícios e Portainer — **é o 1º deploy de teste**; (3) **Teste na creche** na **VPS Linux x86-64** (Ubuntu/Debian), com domínio e HTTPS, que depois vira produção. Ver seção 6.4 |
| Gestão visual | **Portainer CE** como serviço opcional do compose (profile `tools`; seção 6.5) |
| Escala | Hoje: **1 matriz + 1 filial** (um tenant), **sem plano de escalar**. A estrutura multi-tenant é mantida (custo baixo), mas os controles de capacidade são proporcionais a esse porte (seção 2.1) |

---

## 1. Contexto resumido

**Cliente:** Creche Fraternidade Menino Jesus (Santo André–SP), ONG que hoje controla estoque e doações em **cadernos e planilhas** (Anexo A do PDF: planilhas mensais de entrada/saída, produtos de alimentação em sua maioria, com unidades como Kg, g, ml, sachê, cx, lata).

**Objetivo:** sistema web para cadastro de itens, entradas (doação/compra), saídas (uso interno/doação/avaria), controle de estoque por unidade (matriz/filial), alertas de estoque mínimo, dashboard, relatórios (PDF/CSV), usuários/permissões e log de atividades.

**Fora do escopo (declarado no PDF):** gestão financeira/contábil, notas fiscais, integrações externas, RH, app mobile, pagamentos, IA para previsão.

**Telas do protótipo de alta fidelidade** (menu lateral escuro + cabeçalho com seletor de unidade, sino de alertas, logout e avatar):

| Menu | Conteúdo |
|---|---|
| Login | e-mail + senha |
| Dashboard | cards (doações recebidas, itens em estoque, beneficiários, doações realizadas), gráfico "Doações por mês", rosca "Doações por tipo", "Alertas de estoque baixo", "Últimas movimentações" |
| Estoque | abas **Estoque Atual** e **Movimentações**; botões **Nova Entrada** / **Nova Saída** (modais) |
| Itens | catálogo + cadastro/edição/exclusão (inativação) |
| Fornecedores | cadastro de parceiros |
| Relatórios | filtros + visualizar/imprimir/PDF/CSV |
| Usuários | administração |
| Sobre a ONG | página institucional |
| Cabeçalho | seletor **Matriz / Filial** |

**Formulários do protótipo**
- *Registrar Entrada:* Origem (Doação/Compra), Item, Quantidade, Unidade (vem do item), Valor estimado, Data, Observação, Responsável (usuário logado, somente leitura).
- *Registrar Saída:* Item, Quantidade, Unidade, Tipo de saída (Uso interno/Doação/Avaria), Data, Observação, Responsável.

---

## 2. Decisões de arquitetura

| Tema | Escolha | Justificativa |
|---|---|---|
| Runtime | **.NET 10 (LTS)**, ASP.NET Core MVC + Razor | .NET 10 é o LTS atual; .NET 8 sai de suporte em nov/2026 |
| Acesso a dados | **EF Core 10** (provider SQL Server) mapeando o schema existente (*database-first manual*, **sem EF Migrations**) + SQL puro/Dapper apenas em relatórios pesados | O banco é a fonte da verdade (triggers, CHECKs, computed columns já escritos). Migrações ficam em `.sql` versionados |
| Migrações | Scripts `db/migrations/V###__nome.sql` aplicados por um **migrator próprio** (modo `--migrate` do mesmo binário) | Os scripts usam `GO`, que o ADO.NET não entende; o migrator divide por `GO`. Evita depender de `sqlcmd` na imagem |
| Multi-tenant | **Banco único, schema único, coluna `tenant_id`** em todas as tabelas de negócio | Express tem limites de instância (1 socket/4 cores, ~1,4 GB de buffer pool); um banco só é o mais simples de operar e fazer backup |
| Isolamento | 3 camadas: (1) filtros globais do EF por `tenant_id`; (2) **FKs compostas** `(id, tenant_id)` no banco; (3) **Row-Level Security** via `SESSION_CONTEXT` (Fase 6) | Defesa em profundidade |
| Tenant atual | Vem **sempre** do usuário autenticado (claim `tenant_id`), nunca de URL/formulário | Evita IDOR/troca de tenant |
| Unidade atual | Claim `unidade_id` escolhida no seletor do cabeçalho, validada contra `usuarios_unidades` | RF10 |
| Autenticação | **Cookie auth** próprio sobre a tabela `usuarios` + `PasswordHasher<T>` (PBKDF2) | Mantém o schema do projeto; ASP.NET Identity completo traria tabelas extras sem necessidade |
| Autorização | Policies por papel (`admin`, `operacional`, `visualizador`) | RF08 |
| UI | Razor + **Bootstrap 5** (arquivos locais, sem CDN) + Chart.js (local) | Funciona offline na rede da creche; replicar o layout do protótipo |
| PDF / CSV | **QuestPDF** (licença Community — conferir elegibilidade) / **CsvHelper** | RNF10 |
| Logs | Serilog (console + arquivo em volume) | |
| Testes | xUnit; integração com **Testcontainers.MsSql** (Fase 7) | |
| Idioma | UI pt-BR; entidades de domínio em português (`Item`, `Movimentacao`, `Parceiro`), sufixos técnicos em inglês (`Service`, `Interceptor`) | Alinha com o banco e com a equipe |
| Fuso | Banco grava **UTC**; exibição em `America/Sao_Paulo` | Container usa UTC por padrão |

**Limites do SQL Server Express 2025 a respeitar:** banco até 50 GB, 1 socket/4 cores, ~1.410 MB de buffer pool. Sem SQL Server Agent (backup agendado precisa de sidecar — Fase 8). A imagem de container do SQL Server só roda em **x86-64**: **servidor com CPU ARM (plano de VPS ARM ou Raspberry Pi) não serve** (conferir com `uname -m`) e quem desenvolver em Mac Apple Silicon precisará de emulação.

### 2.1 Dimensionamento, retenção, compressão e monitoramento

**Cenário:** 1 matriz + 1 filial, sem plano de escala. Estimativa (não medida — revalidar com dados reais após ~3 meses de uso):

| Item | Estimativa |
|---|---|
| Movimentações por dia (as duas unidades) | 20 a 100 |
| Tamanho por movimentação (linha + índices) | ~0,5 KB |
| Auditoria gerada por movimentação (JSON antigo/novo de `movimentacoes` + `estoque_unidade`) | ~1,5 a 2,5 KB |
| Crescimento do banco | ~20 a 110 MB por ano |
| Tamanho após 10 anos, no pior caso | ~1 GB |

**Conclusão:** o limite de 50 GB e o buffer pool de ~1,4 GB do Express **não são restrição** neste porte; os dados "quentes" cabem inteiros na memória. A tabela `logs_auditoria` é a que mais cresce (tipicamente mais da metade do banco). Os controles abaixo são **seguro barato**, não necessidade urgente. O risco operacional mais provável é **disco do host cheio** ou **ausência de backup**, não o limite do Express — por isso o monitoramento inclui espaço do volume.

**Política de retenção (valores iniciais — ver Q13):**

| Dado | Retenção online | Ao expirar |
|---|---|---|
| `movimentacoes`, `estoque_unidade`, `itens`, `parceiros`, `categorias_itens`, `usuarios` | indefinida | **nunca purgar** (registro de prestação de contas; volume pequeno) |
| `logs_auditoria` — alterações de dados (`INSERT`/`UPDATE`/`DELETE`) | 24 meses | exportar para arquivo comprimido e apagar |
| `logs_auditoria` — ações de aplicação (`LOGIN`, `LOGIN_FALHA`, `LOGOUT`, `ALTERNAR_UNIDADE`, `EXPORTAR_RELATORIO`, `ALTERAR_SENHA`) | 12 meses | exportar para arquivo comprimido e apagar |
| Arquivos exportados da auditoria (volume `archive`) | a definir com a creche (sugestão: 5 anos) | entram no backup |
| Logs de aplicação (Serilog, arquivos) | 30 dias | rotação automática |

**Compressão:** `DATA_COMPRESSION = PAGE` em `logs_auditoria` e `movimentacoes` (tabela e índices), aplicado já no `V001` (A19). Tabelas pequenas (tenants, unidades, lookups, usuários, estoque) ficam sem compressão. Custo de CPU irrelevante neste volume.

**Monitoramento:** view de tamanho no banco (T1.5) + página de saúde para o admin + alertas por limiar (E8.2), com limite configurável de 50 GB.

---

## 3. Análise do `Cria_Banco.sql` e ajustes necessários

**O que está bom (manter):** lookup tables para papéis e unidades de medida; CHECKs de negócio (`quantidade > 0`, status, tipo, parceiro coerente, beneficiário obrigatório em doação); coluna `abaixo_do_minimo` persistida; `valor_total` calculado; trigger de estoque com estorno; auditoria com `SESSION_CONTEXT`; índices em FKs (SQL Server não os cria sozinho).

**Ajustes (A01–A21).** A recomendação é **reescrever como `V001` consolidado** (ainda não há dados em produção, então é mais seguro que ALTERs em cima do script original). Guardar o original em `docs/referencia/`.

| # | Ajuste | Motivo |
|---|---|---|
| A01 | Remover `CREATE DATABASE` e `USE` do script; o migrator cria o banco (collation `Latin1_General_100_CI_AI`, para busca sem distinção de acento — "acucar" acha "Açúcar") | O script precisa rodar em qualquer banco alvo |
| A02 | Criar `tenants`, `unidades` (`tipo` = `matriz`/`filial`), `usuarios_unidades`; adicionar `tenant_id` em usuarios, categorias_itens, itens, parceiros, movimentacoes, logs_auditoria | Multi-tenant + RF10 |
| A03 | **Estoque por unidade:** mover `quantidade_atual`, `quantidade_minima`, `valor_total_estoque` e `abaixo_do_minimo` de `itens` para nova tabela `estoque_unidade (item_id, unidade_id)`; `itens` vira catálogo do tenant | RF10 exige estoques individualizados; hoje o saldo é único por item |
| A04 | **FKs compostas com `tenant_id`**: `itens UNIQUE (id, tenant_id)` e `movimentacoes (item_id, tenant_id) → itens (id, tenant_id)`, idem parceiros, unidades, usuarios | O banco passa a **impedir** referência cruzada entre tenants, mesmo com bug na aplicação |
| A05 | UNIQUEs globais viram por tenant: `(tenant_id, codigo_interno)`, `(tenant_id, nome)` em categorias, `(tenant_id, numero_movimentacao)`. `usuarios.email` continua único **global** (login) | Dois tenants podem ter o mesmo código |
| A06 | **Bug:** `UNIQUE (cpf_cnpj)` aceita só **uma** linha com `NULL` no SQL Server. Pessoa física sem CPF e fornecedor anônimo (RF06) quebram na segunda inserção. Trocar por índice único **filtrado**: `UNIQUE (tenant_id, cpf_cnpj) WHERE cpf_cnpj IS NOT NULL` | RF06 |
| A07 | Adicionar tipo de movimentação **`avaria`** (saída). Ajustar `CK_mov_tipo`, `CK_mov_parceiro_coerente` (avaria → sem parceiro) e a regra do trigger (saída). | RF03 e RF11 pedem "descartados por avaria"; o SQL só tem `doacao_realizada` e `consumo_interno` |
| A08 | `itens`: adicionar `preco_estimado DECIMAL(10,2) NULL` e `origem_padrao VARCHAR(10) NULL` (`doacao`/`compra`). "Tipo do item" = `categorias_itens`; "observação" = `descricao` | RF01 |
| A09 | `unidades_medida`: acrescentar `lata` e `sache` (aparecem na planilha da creche). `g`/`ml` só se quiserem controlar por peso/volume (hoje o estoque é contado em embalagens) | Anexo A |
| A10 | **Trigger de estoque — reescrever:** (a) **Bug:** o `UPDATE ... FROM itens JOIN recem_processadas` aplica só **uma** linha quando duas movimentações do mesmo item entram no mesmo `INSERT`; agregar com `GROUP BY (tenant, item, unidade)` e `SUM`. (b) Fazer *upsert* da linha de `estoque_unidade`. (c) **Saída reduz o valor pelo custo médio** (`valor_total_estoque / quantidade_atual`) e não pelo `valor_unitario` informado — senão uma saída com preço estimado alto estoura `CK_valor_total_estoque >= 0`. O `valor_unitario` da saída fica só informativo (valor da doação para prestação de contas). (d) Trocar `RAISERROR + ROLLBACK` por `THROW 50001, 'Estoque insuficiente', 1`. (e) Validar só as linhas afetadas | Corretude e mensagem amigável |
| A11 | **Imutabilidade:** movimentação `processada` não pode ter `quantidade`, `valor_unitario`, `item_id`, `unidade_id`, `tipo_movimentacao` alterados (hoje é só "limitação documentada"). Transições válidas: `pendente→processada`, `pendente→cancelada`, `processada→cancelada`. **Sem DELETE** em `movimentacoes`/`estoque_unidade`/`logs_auditoria` (`INSTEAD OF DELETE` ou `DENY` ao login da aplicação). Excluir no protótipo (ícone de lixeira) = **cancelar** | O trigger só trata INSERT/UPDATE; um DELETE de movimentação processada deixaria o saldo errado |
| A12 | Defaults `SYSDATETIME()` → **`SYSUTCDATETIME()`**; triggers de `atualizado_em` idem | Container em UTC; conversão na exibição |
| A13 | `numero_movimentacao` gerado por `SEQUENCE` + `DEFAULT` (ex.: `MOV-2026-000123`) | Hoje ninguém gera o número |
| A14 | `logs_auditoria`: `id BIGINT`; adicionar `tenant_id`, `unidade_id`; gravar `ip_usuario` a partir do `SESSION_CONTEXT`; estender triggers para `estoque_unidade`, `parceiros`, `categorias_itens`, `usuarios` — **excluindo a coluna `senha` do JSON** | RF13; hoje o hash de senha iria para o log; `ip_usuario` nunca é preenchido |
| A15 | `usuarios`: acrescentar `ultimo_login_em`, `tentativas_falhas_login`, `bloqueado_ate`, `trocar_senha` (BIT) | Lockout (Fase 6) e troca obrigatória da senha semeada |
| A16 | `logs_auditoria.acao`: além de `INSERT/UPDATE/DELETE`, aceitar ações de aplicação (`LOGIN`, `LOGIN_FALHA`, `LOGOUT`, `ALTERNAR_UNIDADE`, `EXPORTAR_RELATORIO`, `ALTERAR_SENHA`, `PURGA_AUDITORIA`) — gravadas pela aplicação | RF13 pede "ação realizada" por usuário; triggers não enxergam login |
| A17 | Índices para as consultas reais: `movimentacoes (tenant_id, unidade_id, data_movimentacao DESC) INCLUDE (...)`, `estoque_unidade (tenant_id, unidade_id) WHERE abaixo_do_minimo = 1`, `logs_auditoria (tenant_id, criado_em DESC)` | Dashboard/alertas/relatórios |
| A18 | Criar login/usuário de banco **`estoque_app`** (menor privilégio) para a aplicação: `db_datareader`, `db_datawriter`, `DENY DELETE` em `movimentacoes`/`estoque_unidade`/`logs_auditoria`, `DENY UPDATE` em `logs_auditoria`, sem DDL. O migrator usa uma conexão administrativa separada | Segurança desde o MVP |
| A19 | **Compressão de dados:** `WITH (DATA_COMPRESSION = PAGE)` em `logs_auditoria` e `movimentacoes` (tabela e índices não clusterizados). Nota: valores `NVARCHAR(MAX)` que não cabem na linha (~8 KB) ficam fora da linha e **não** são comprimidos por PAGE; o JSON de auditoria típico cabe na linha | Reduz o espaço da tabela que mais cresce. Confirmar o ganho com `sp_estimate_data_compression_savings` e registrar em `docs/DECISOES.md` |
| A20 | Novas tabelas `manutencao_log` (tarefa, início, fim, sucesso, linhas afetadas, detalhes JSON) e `saude_banco_historico` (data, tamanho total MB, tamanho de `logs_auditoria` MB); views `vw_saude_banco` e `vw_tamanho_tabelas`; índice `logs_auditoria (criado_em)` para purga em lotes | Retenção e monitoramento (seção 2.1) |
| A21 | Login de manutenção **`estoque_maint`** (separado de `estoque_app`): `SELECT` nas tabelas de log, `DELETE` **somente** em `logs_auditoria`, `INSERT` em `manutencao_log`/`saude_banco_historico`. Usado apenas pelo modo `--maintenance` | `estoque_app` tem `DENY DELETE` em `logs_auditoria` (S14); a purga não pode rodar com ele |

---

## 4. Modelo multi-tenant alvo (resumo)

```
tenants 1──N unidades (matriz | filial)         [no máximo 1 matriz por tenant]
tenants 1──N usuarios N──N unidades             (usuarios_unidades)
tenants 1──N categorias_itens 1──N itens        (catálogo compartilhado entre as unidades do tenant)
itens N──N unidades  =  estoque_unidade         (saldo, mínimo, valor — por unidade)
tenants 1──N parceiros
movimentacoes → (tenant, unidade, item, parceiro?, responsável)
logs_auditoria → (tenant, unidade?, usuario?)
```

Esboço DDL das tabelas novas (Claude Code completa no `V001`):

```sql
CREATE TABLE tenants (
  id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_tenants PRIMARY KEY,
  nome VARCHAR(255) NOT NULL,
  cnpj VARCHAR(20) NULL,
  ativo BIT NOT NULL CONSTRAINT DF_tenants_ativo DEFAULT 1,
  criado_em DATETIME2 NOT NULL CONSTRAINT DF_tenants_criado DEFAULT SYSUTCDATETIME()
);

CREATE TABLE unidades (
  id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_unidades PRIMARY KEY,
  tenant_id INT NOT NULL CONSTRAINT FK_unidades_tenant REFERENCES tenants(id),
  nome VARCHAR(100) NOT NULL,
  tipo VARCHAR(10) NOT NULL CONSTRAINT CK_unidades_tipo CHECK (tipo IN ('matriz','filial')),
  ativo BIT NOT NULL CONSTRAINT DF_unidades_ativo DEFAULT 1,
  criado_em DATETIME2 NOT NULL CONSTRAINT DF_unidades_criado DEFAULT SYSUTCDATETIME(),
  CONSTRAINT UQ_unidades_tenant_nome UNIQUE (tenant_id, nome),
  CONSTRAINT UQ_unidades_id_tenant UNIQUE (id, tenant_id)
);
CREATE UNIQUE INDEX UX_unidades_uma_matriz ON unidades(tenant_id) WHERE tipo = 'matriz';

CREATE TABLE estoque_unidade (
  tenant_id INT NOT NULL,
  unidade_id INT NOT NULL,
  item_id INT NOT NULL,
  quantidade_atual INT NOT NULL CONSTRAINT DF_est_qtd DEFAULT 0,
  quantidade_minima INT NOT NULL CONSTRAINT DF_est_min DEFAULT 0,
  valor_total_estoque DECIMAL(12,2) NOT NULL CONSTRAINT DF_est_valor DEFAULT 0,
  abaixo_do_minimo AS (CASE WHEN quantidade_atual <= quantidade_minima THEN 1 ELSE 0 END) PERSISTED,
  atualizado_em DATETIME2 NULL,
  CONSTRAINT PK_estoque_unidade PRIMARY KEY (unidade_id, item_id),
  CONSTRAINT FK_est_unidade FOREIGN KEY (unidade_id, tenant_id) REFERENCES unidades(id, tenant_id),
  CONSTRAINT FK_est_item    FOREIGN KEY (item_id, tenant_id)    REFERENCES itens(id, tenant_id),
  CONSTRAINT CK_est_qtd   CHECK (quantidade_atual >= 0),
  CONSTRAINT CK_est_min   CHECK (quantidade_minima >= 0),
  CONSTRAINT CK_est_valor CHECK (valor_total_estoque >= 0)
);
```

> Observação: `itens` precisa de `UNIQUE (id, tenant_id)` **antes** de `estoque_unidade` e `movimentacoes` referenciarem. Ordem de criação das tabelas importa.

---

## 5. Estrutura do repositório

```
estoque-creche/
├─ PLANO.md  CLAUDE.md  README.md
├─ compose.yaml            # produção/teste: sqlserver + migrator + web
├─ compose.dev.yaml        # override dev: expõe 1433, volume de código, logs verbosos
├─ Dockerfile
├─ .env.example  .gitignore  .dockerignore  .editorconfig
├─ global.json  Directory.Build.props
├─ docs/
│  ├─ referencia/  (Cria_Banco.sql original, PDF do projeto)
│  ├─ DECISOES.md
│  └─ DEPLOY.md          # runbook: servidor de teste (caseiro) e VPS
├─ deploy/Caddyfile        # proxy reverso (HTTPS automático) — profile https
├─ secrets/                # gitignored: senha do admin do Portainer
├─ db/
│  ├─ migrations/  V001__schema.sql  V002__lookups.sql  ...
│  └─ tests/       *.sql de verificação das regras (ou xUnit na Fase 7)
├─ scripts/        smoke.sh  backup.sh
├─ src/Estoque.Web/
│  ├─ Program.cs                    # modos: web | --migrate | --healthcheck | --maintenance (Fase 8)
│  ├─ Controllers/  Models/{Entities,ViewModels}/  Views/  wwwroot/
│  ├─ Data/        EstoqueDbContext, Configurations/, Interceptors/, Migrator/, Seed/
│  ├─ Services/    EstoqueService, MovimentacaoService, DashboardService, ...
│  └─ Infrastructure/  Tenancy/, Security/
└─ tests/Estoque.Tests/
```

Projeto único com pastas (MVC clássico) é suficiente. Separar em camadas só se a Fase 5+ justificar.

---

## 6. Docker

### 6.1 `compose.yaml` (esqueleto a validar na T0.4)

```yaml
name: estoque-creche

x-logging: &logging
  driver: json-file
  options: { max-size: "10m", max-file: "5" }     # sem isso os logs do Docker enchem o disco da VPS

x-app: &app
  build: .
  image: estoque-web:local
  logging: *logging
  networks: [backend]

services:
  sqlserver:
    image: mcr.microsoft.com/mssql/server:2025-latest   # fixar tag exata após validar (T0.4)
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_PID: Express
      MSSQL_SA_PASSWORD: ${MSSQL_SA_PASSWORD:?defina no .env}
      MSSQL_MEMORY_LIMIT_MB: "1536"          # valores iniciais; ajustar após medir
    volumes:
      - mssql_data:/var/opt/mssql
    healthcheck:
      # validar o caminho do sqlcmd na imagem; se ausente, remover o healthcheck e
      # deixar o migrator tentar reconectar (retry com backoff)
      test: ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P \"$$MSSQL_SA_PASSWORD\" -Q 'SELECT 1' -b -o /dev/null || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 12
      start_period: 30s
    mem_limit: 2560m
    logging: *logging
    restart: unless-stopped
    networks: [backend]          # SEM "ports:" — o banco nunca é publicado

  migrator:
    <<: *app
    command: ["--migrate"]
    environment:
      ConnectionStrings__Admin: "Server=sqlserver,1433;Database=master;User Id=sa;Password=${MSSQL_SA_PASSWORD};Encrypt=True;TrustServerCertificate=True"
      APP_DB_PASSWORD: ${APP_DB_PASSWORD:?defina no .env}
      MAINT_DB_PASSWORD: ${MAINT_DB_PASSWORD:?defina no .env}
      SEED_ADMIN_EMAIL: ${SEED_ADMIN_EMAIL:?}
      SEED_ADMIN_PASSWORD: ${SEED_ADMIN_PASSWORD:?}
      SEED_DEMO_DATA: ${SEED_DEMO_DATA:-false}
    depends_on:
      sqlserver: { condition: service_healthy }
    restart: "no"

  web:
    <<: *app
    environment:
      ASPNETCORE_ENVIRONMENT: ${ASPNETCORE_ENVIRONMENT:-Production}
      Cookie__SecurePolicy: ${COOKIE_SECURE_POLICY:-SameAsRequest}   # Always quando usar HTTPS
      ConnectionStrings__Default: "Server=sqlserver,1433;Database=EstoqueDoacoes;User Id=estoque_app;Password=${APP_DB_PASSWORD};Encrypt=True;TrustServerCertificate=True"
    ports:
      - "${WEB_BIND:-127.0.0.1}:${WEB_PORT:-8080}:8080"   # padrão: só loopback
    volumes:
      - dataprotection:/home/app/.aspnet/DataProtection-Keys
      - applogs:/app/logs
    depends_on:
      migrator: { condition: service_completed_successfully }
    healthcheck:
      test: ["CMD", "dotnet", "Estoque.Web.dll", "--healthcheck"]
      interval: 15s
      timeout: 5s
      retries: 5
    read_only: true
    tmpfs: [/tmp]
    security_opt: ["no-new-privileges:true"]
    cap_drop: [ALL]
    restart: unless-stopped
    networks: [frontend, backend]

  proxy:
    image: caddy:2                        # fixar tag exata na T0.4
    profiles: ["https"]                   # só sobe na VPS com domínio (modo A)
    environment:
      SITE_ADDRESS: ${SITE_ADDRESS:-localhost}
      ACME_EMAIL: ${ACME_EMAIL:-admin@example.com}   # no modo A usar um e-mail real
      ACME_CA: ${ACME_CA:-https://acme-v02.api.letsencrypt.org/directory}
    ports:
      - "80:80"
      - "443:443"
      - "443:443/udp"
    volumes:
      - ./deploy/Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy_data:/data
      - caddy_config:/config
    depends_on:
      web: { condition: service_healthy }
    security_opt: ["no-new-privileges:true"]
    logging: *logging
    restart: unless-stopped
    networks: [frontend]

  portainer:
    image: portainer/portainer-ce:lts     # fixar tag exata na T0.4
    profiles: ["tools"]
    command: ["--admin-password-file=/run/secrets/portainer_admin_password"]
    secrets: [portainer_admin_password]
    ports:
      - "${PORTAINER_BIND:-127.0.0.1}:9443:9443"   # UI HTTPS. NÃO publicar 9000 (HTTP) nem 8000 (agente Edge)
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock  # ≈ acesso root ao host: ver seção 6.5
      - portainer_data:/data
    security_opt: ["no-new-privileges:true"]
    logging: *logging
    restart: unless-stopped
    networks: [tools]

networks:
  frontend: {}
  backend: {}
  tools: {}

secrets:
  portainer_admin_password:
    file: ./secrets/portainer_admin_password.txt   # gitignored; senha com 12+ caracteres, sem quebra de linha

volumes:
  mssql_data:
  dataprotection:
  applogs:
  caddy_data:
  caddy_config:
  portainer_data:
```

`deploy/Caddyfile`:

```
{
    email {$ACME_EMAIL}
    acme_ca {$ACME_CA:https://acme-v02.api.letsencrypt.org/directory}
}

{$SITE_ADDRESS} {
    encode zstd gzip
    reverse_proxy web:8080
}
```

Pontos de atenção:
- O `proxy` só sobe com o profile `https` (`COMPOSE_PROFILES=tools,https`). Com um **domínio público** apontando para a VPS, o Caddy obtém e renova o certificado (Let's Encrypt) sozinho e redireciona HTTP→HTTPS. Com `SITE_ADDRESS=localhost`, usa certificado interno (para testes locais do próprio proxy).
- Por padrão o `web` e o Portainer publicam só em `127.0.0.1` (`WEB_BIND`, `PORTAINER_BIND`). Para acessar de **outro computador da LAN** no teste local, usar o IP da LAN do servidor no bind — **só HTTP, sem dados reais e sem redirecionamento de portas no roteador**. Em produção, quem escuta em 80/443 é o `proxy`.
- `Cookie__SecurePolicy`: `SameAsRequest` no teste local (HTTP); `Always` quando usar o profile `https`. Cookie `Secure` não funciona em HTTP por IP da LAN.
- `TrustServerCertificate=True` é aceitável **dentro da rede privada do compose** no MVP (o driver exige `Encrypt=True` por padrão e o container usa certificado autoassinado). Fase 6: certificado próprio.
- Senhas no `.env`: **evitar** `; ' " $` (quebram connection string/interpolação do compose). SA exige senha forte (mín. 8 caracteres, 3 de 4 classes).
- **Volume `dataprotection` é obrigatório**: sem ele, cada restart invalida cookies e antiforgery tokens.
- `--healthcheck` é um modo do binário que faz `GET http://localhost:8080/healthz` e devolve exit code (a imagem `aspnet` não tem `curl`/`wget`).
- Limites de memória (`mem_limit`, `MSSQL_MEMORY_LIMIT_MB`) são valores iniciais; medir com `docker stats` e ajustar.
- Dev: `compose.dev.yaml` publica `127.0.0.1:1433:1433` (apenas loopback), útil para SSMS/Azure Data Studio.

### 6.2 `Dockerfile` (multi-stage)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Estoque.Web/Estoque.Web.csproj src/Estoque.Web/
RUN dotnet restore src/Estoque.Web/Estoque.Web.csproj
COPY . .
RUN dotnet publish src/Estoque.Web/Estoque.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
COPY db ./db
USER $APP_UID
ENTRYPOINT ["dotnet", "Estoque.Web.dll"]
```

### 6.3 `.env.example`

| Variável | Uso |
|---|---|
| `MSSQL_SA_PASSWORD` | senha do `sa` (só migrator e container do banco) |
| `APP_DB_PASSWORD` | senha do login `estoque_app` |
| `MAINT_DB_PASSWORD` | senha do login `estoque_maint` (purga de auditoria e manutenção, Fase 8) |
| `Retencao__AuditoriaDadosMeses` / `Retencao__AuditoriaAcoesMeses` | retenção online da auditoria (24 / 12; mínimo 12) |
| `Monitoramento__LimiteBancoGB` | limite usado nos alertas de tamanho (50) |
| `SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD` | primeiro administrador (mín. 12 caracteres; app recusa subir em Production com valor vazio/padrão) |
| `SEED_DEMO_DATA` | `true` carrega tenant demo, itens da planilha e usuários de exemplo |
| `COMPOSE_PROFILES` | `tools` (ensaio local: com Portainer) · `tools,https` (VPS com Portainer) · `https` (VPS sem Portainer) |
| `SITE_ADDRESS` | domínio público do sistema (ex.: `estoque.exemplo.org`); só com profile `https` |
| `ACME_EMAIL` | e-mail **real** para o Let's Encrypt (avisos de expiração) |
| `ACME_CA` | autoridade de certificação do Caddy. Padrão: Let's Encrypt de produção. Nas primeiras tentativas usar a de **testes**: `https://acme-staging-v02.api.letsencrypt.org/directory` (o navegador mostra aviso de certificado; evita bloqueio por limite de emissões) |
| `WEB_BIND` / `WEB_PORT` | endereço e porta do `web` (padrão `127.0.0.1` / 8080). IP da LAN só no teste local |
| `PORTAINER_BIND` | endereço da UI do Portainer (padrão `127.0.0.1`, porta 9443) |
| `COOKIE_SECURE_POLICY` | `SameAsRequest` (ensaio local, HTTP) ou `Always` (HTTPS) |
| `secrets/portainer_admin_password.txt` | **arquivo** (gitignored, `chmod 600`) com a senha do admin do Portainer, ≥ 12 caracteres, sem quebra de linha |
| `ASPNETCORE_ENVIRONMENT` | `Production` no deploy de teste |

### 6.4 Ambientes e servidores: ensaio (servidor caseiro) e VPS (teste da creche)

**Ambientes**

| Ambiente | Onde | Acesso | Dados | Observações |
|---|---|---|---|---|
| Dev | máquina do desenvolvedor | `localhost` | fictícios | `compose.dev.yaml`; banco em `127.0.0.1:1433` |
| **Ensaio** (1º deploy) | **servidor caseiro Ubuntu** do desenvolvedor | `localhost` no servidor, túnel SSH ou IP da LAN (**modo C**) | **fictícios** (`SEED_DEMO_DATA=true`) | HTTP, Portainer ligado (`COMPOSE_PROFILES=tools`), sem proxy |
| **Teste na creche** → produção | **VPS Linux x86-64** (Ubuntu/Debian) | `https://<dominio>` (**modo A**) | fictícios no teste; reais só após a checklist da T4.5 | `COMPOSE_PROFILES=https` (Portainer desligado ou só por túnel SSH) |

O servidor caseiro serve como **ensaio**: tudo que for validado nele (compose, migrações, seed, roteiro da Fase 3, backup/restauração) é repetido na VPS apenas trocando o `.env`. Os usuários da creche acessam **somente a VPS**, por HTTPS.

**Servidor caseiro (modo C) — cuidados**
- IP **reservado** na LAN (DHCP estático no roteador) para o endereço não mudar.
- **Nenhum redirecionamento de porta, DMZ ou UPnP** no roteador para este servidor. Se for preciso acessar de fora de casa, usar VPN (WireGuard/Tailscale) ou túnel SSH.
- Quem controla o acesso é o **endereço de bind** (`127.0.0.1` × IP da LAN), não o `ufw` (portas publicadas pelo Docker contornam o `ufw`).
- Verificar `uname -m` (precisa ser `x86_64`) e `free -h` antes de começar; um mini-PC/desktop antigo x86-64 com 4 GB serve; **Raspberry Pi (ARM) não**.


**Requisitos mínimos do servidor (caseiro ou VPS)**

| Item | Recomendação |
|---|---|
| SO | Ubuntu LTS (22.04/24.04) ou Debian estável, 64 bits |
| CPU | **x86-64** (`uname -m` → `x86_64`). ARM (plano de VPS ARM, Raspberry Pi) **não** roda a imagem do SQL Server |
| vCPU / RAM | 2 vCPU / **4 GB** (SQL Server ~2,5 GB com folga + app + SO) + 1–2 GB de swap como rede de segurança |
| Disco | ≥ 40 GB SSD (banco pequeno; o resto é imagens Docker, logs, backups e arquivos de auditoria) |
| Rede | Servidor caseiro: IP reservado na LAN. VPS: IPv4 público e **domínio** com registro A apontando para ela |
| Docker | Docker Engine + plugin Compose v2, instalados pelo repositório oficial do Docker (não Docker Desktop e **não** a versão snap do Ubuntu) |

**Preparação do servidor (T0.5 → `docs/DEPLOY.md`)**
1. Usuário não-root (`deploy`) com chave SSH; `PermitRootLogin no` e `PasswordAuthentication no` no `sshd` — **testar o login por chave em outra sessão antes de fechar a atual**.
2. Firewall: no painel do provedor e no `ufw`, liberar só **22, 80 e 443**. Atenção: portas publicadas pelo Docker **contornam o `ufw`**; por isso o compose só publica 80/443 (proxy) e `127.0.0.1` (web). O banco nunca é publicado.
3. `fail2ban` para SSH; `unattended-upgrades` para atualizações de segurança do SO.
4. Relógio sincronizado (`systemd-timesyncd` ou `chrony`), fuso do servidor em UTC.
5. `systemctl enable docker` (sobe após reboot; os containers usam `restart: unless-stopped`).
6. Swap pequeno e ajuste de rotação de logs do Docker (já no compose).
7. Clonar o repositório com *deploy key* somente leitura; `.env` com `chmod 600`, fora do Git.

**Modos de acesso**

| Modo | Quando | Como |
|---|---|---|
| **A — HTTPS público** (padrão na VPS) | teste da creche e produção; **exige domínio/subdomínio** | `SITE_ADDRESS=estoque.exemplo.org`, portas 80/443 abertas, Caddy emite o certificado |
| **B — somente túnel SSH** | só para o desenvolvedor/admin enquanto não há domínio; **não serve para os usuários da creche** | 80/443 **fechadas** no firewall; acesso por `ssh -L 8080:localhost:8080 deploy@IP` e `http://localhost:8080` |
| **C — localhost/LAN** (teste local) | servidor caseiro, 1º deploy de teste | `WEB_BIND=127.0.0.1` (ou IP da LAN), sem proxy, HTTP, dados fictícios, Portainer ligado; **sem** redirecionamento de portas no roteador |

Nunca expor o login por **HTTP puro** na internet. No modo C o HTTP é aceito porque o tráfego fica no servidor/LAN doméstica e os dados são fictícios.

**Deploy e atualização (MVP)**
- Primeira vez: `git clone` → `cp .env.example .env` → editar senhas (e o domínio, no modo A) → criar `secrets/portainer_admin_password.txt` → `docker compose up -d --build`. O migrator cria o banco e aplica as migrações sozinho.
- Atualizar: **backup** → `git pull` → `docker compose up -d --build` (o migrator roda de novo) → conferir `/healthz`. *Rollback:* voltar o `git checkout` da tag anterior e restaurar o backup se houve migração de banco.
- Evolução (Fase 7): CI constrói a imagem, publica em um registry (ex.: GHCR) e a VPS apenas faz `docker compose pull && up -d`.

### 6.5 Portainer (gestão visual dos containers)

Serviço opcional do compose (profile `tools`, imagem `portainer/portainer-ce:lts`, tag exata fixada na T0.4). Interface em `https://localhost:9443` (certificado autoassinado: o aviso do navegador é esperado).

**Para que usar:** ver o estado e a saúde de `sqlserver`, `migrator`, `web` (e `proxy`); acompanhar **logs** em tempo real; reiniciar um container; ver consumo de CPU/RAM (base para ajustar `mem_limit`); abrir **console** no container (ex.: `sqlcmd` no `sqlserver`); inspecionar volumes, redes e imagens.

**Configuração**
- Publicar **apenas a 9443**. As portas 9000 (HTTP legado) e 8000 (agente Edge) não são necessárias e ficam fechadas.
- O admin é **pré-configurado** por `--admin-password-file` (arquivo em `secrets/`). Isso evita a janela de ~5 minutos após o primeiro start em que quem alcançar a porta primeiro define a senha. Se a janela expirar mesmo assim: `docker compose restart portainer`.
- Atualizar: `docker compose pull portainer && docker compose up -d portainer`.

**Regras**
- **A fonte da verdade é o Git + `compose.yaml`.** Não editar nem refazer a stack `estoque-creche` pela interface do Portainer (ela tende a aparecer como stack criada fora dele, com controle limitado). Ele é para **observar e operar**, não para configurar.
- Não alterar variáveis de ambiente nem volumes pela interface.

**Riscos (aceitos no teste local, mitigados em produção)**
- Montar `/var/run/docker.sock` equivale a **acesso root ao host**.
- O admin do Portainer enxerga as **variáveis de ambiente** dos containers, inclusive as senhas do banco.
- Por isso: bind em `127.0.0.1` (ou IP da LAN só no teste), senha forte, nenhum usuário extra, **nunca exposto à internet**. Na VPS, deixar o profile `tools` desligado ou acessar só por túnel SSH (`ssh -L 9443:localhost:9443 deploy@IP`).

### 6.6 Domínio e DNS (registro do zero)

Os usuários da creche acessam a VPS por HTTPS, então é preciso um domínio com um **subdomínio** (ex.: `estoque.<dominio>`) apontando para o IP da VPS. O ensaio no servidor caseiro **não** depende disso, por isso o registro pode (e deve) andar em paralelo, já que a validação de alguns domínios leva dias.

**Escolha da extensão e do titular**

| Opção | Requisito | Custo (referência) | Observação |
|---|---|---|---|
| **`.org.br`** (recomendada) | **CNPJ** da creche e comprovação de que é entidade sem fins lucrativos | ~R$ 40/ano no Registro.br | Combina com uma ONG, mas exige documentação e a liberação pode levar dias |
| `.com.br` | CPF ou CNPJ | ~R$ 40/ano no Registro.br | Mais rápido; sem comprovação de finalidade |
| `.org` / `.com` (internacional) | nenhum | ~R$ 50–150/ano, varia com o câmbio | Registrado em registradora credenciada, sem exigir CPF/CNPJ |

Valores de referência de fontes secundárias; **conferir no Registro.br no momento do registro**.

**Titular:** registrar **no nome da creche** (CNPJ), não no do desenvolvedor, para o domínio não ficar dependente de uma pessoa. O CPF/CNPJ informado fica vinculado ao domínio. O contato técnico pode ser o desenvolvedor.

**Procedimento (T0.6)**
1. Escolher o nome (curto, sem hífen, fácil de ditar por telefone) e conferir a disponibilidade no Registro.br.
2. Registrar com os dados da creche; ativar **renovação automática** e a autenticação em dois fatores da conta, se disponível; cadastrar **dois e-mails de contato** (creche + desenvolvedor) para avisos de vencimento.
3. Usar o DNS do próprio Registro.br (ou outro de confiança) e criar o registro **A**: `estoque` → IP público da VPS (e **AAAA** só se a VPS tiver IPv6 funcionando). TTL baixo (ex.: 300 s) durante a configuração.
4. Deixar o domínio raiz livre para um futuro site institucional; o sistema fica no subdomínio.
5. Conferir a propagação antes de subir o proxy: `dig +short estoque.<dominio>` deve devolver o IP da VPS.
6. Liberar **80 e 443** no firewall da VPS. A porta 80 é necessária para a emissão do certificado, mesmo com redirecionamento para HTTPS.
7. Subir com `ACME_CA` de **testes** → confirmar nos logs do `proxy` que o certificado foi emitido → trocar para o de produção e reiniciar o `proxy`.
8. Registrar domínio, titular, data de vencimento e contatos em `docs/DEPLOY.md`.

**Cuidados**
- O titular do domínio pode aparecer em consultas públicas (WHOIS); se o titular for pessoa física, verificar o que é exibido (LGPD).
- Sem o domínio resolvendo corretamente, o Caddy não emite certificado e o `proxy` fica em erro; o ensaio local (modo C) não é afetado.
- Se o domínio expirar, o sistema fica fora do ar para os usuários: a renovação é responsabilidade formal definida entre a creche e o desenvolvedor (Q19).

---

## 7. Escopo do MVP

### 7.1 Dentro do MVP

| Funcionalidade | Requisito | Observações |
|---|---|---|
| `docker compose up -d --build` sobe banco + migração + app (+ Portainer) no **servidor caseiro** (modo C); o proxy HTTPS entra ao promover para a VPS | RNF02/03 | critério central do MVP |
| Portainer para gestão visual dos containers | — | seção 6.5 |
| Servidor preparado (SSH por chave, firewall 22/80/443, fail2ban, atualizações automáticas) | RNF04 | T0.5 |
| Banco criado com schema multi-tenant, triggers, lookups, login `estoque_app`, compressão PAGE e views de tamanho | RNF06 | Fase 1 |
| Seed: tenant, **Matriz** e **Filial**, admin; opcional dados demo | — | senha vem do `.env`, hash gerado em C# |
| Login/logout por e-mail e senha, papéis aplicados nas ações | RF08, RF09, RNF04 | sem auto-cadastro |
| Seletor de unidade (Matriz/Filial) no cabeçalho, filtrando tudo | RF10 | |
| Itens (CRUD + inativar), categorias | RF01 | |
| Fornecedores/parceiros (CRUD), fornecedor anônimo | RF06 | |
| **Nova Entrada** (doação/compra) | RF02, RF05 | |
| **Nova Saída** (uso interno/doação/avaria) | RF03, RF05 | doação exige beneficiário |
| Estoque atual + lista de movimentações + cancelar movimentação | RF04 | cancelar = estorno |
| Alertas de estoque mínimo (sino + dashboard) | RF12 | |
| Dashboard básico (4 cards, alertas, últimas movimentações) | RF11 (parcial) | gráficos ficam para a versão completa |
| `/healthz`, logs no console, tratamento de erro amigável | RNF08/09 | |
| Auditoria automática por triggers (itens, movimentações) | RF13 (parcial) | tela de log na versão completa |

### 7.2 Fora do MVP (versão completa)

Relatórios com filtros/PDF/CSV/impressão; dashboard com gráficos e indicadores completos; CRUD de usuários e perfis; tela de log de atividades; RLS; HTTPS/TLS; rate limiting e lockout; backups agendados; CI/CD; testes de ponta a ponta; cadastro de novos tenants por interface.

### 7.3 Matriz de permissões (suposição — ver Q05)

| Ação | admin | operacional | visualizador |
|---|:-:|:-:|:-:|
| Ver dashboard, estoque, movimentações, relatórios | ✔ | ✔ | ✔ |
| Cadastrar/editar itens e fornecedores | ✔ | ✔ | — |
| Registrar entrada e saída | ✔ | ✔ | — |
| Cancelar movimentação | ✔ | — | — |
| Gerir usuários, ver log de atividades | ✔ | — | — |

### 7.4 Regras de negócio que a aplicação deve respeitar (já no banco)

- Entrada **compra** → fornecedor obrigatório. Entrada **doação** → fornecedor opcional (anônimo permitido).
- Saída **doação** → beneficiário obrigatório. Saídas **uso interno** e **avaria** → sem fornecedor.
- Quantidade > 0; estoque nunca negativo (o protótipo mostra "-5" no Arroz — o sistema real deve impedir e exibir a mensagem "Estoque insuficiente").
- Responsável = usuário logado (não editável).
- Movimentação processada não se edita: cancela-se e lança-se outra.
- Toda consulta e escrita é limitada ao **tenant** e à **unidade** atuais.

---

## 8. Plano de execução — MVP

### Fase 0 — Fundação (repositório + Docker)

**T0.1 Esqueleto do repositório**
- [ ] Estrutura da seção 5, `git init`, `.gitignore` (inclui `.env`, `secrets/*` exceto `.gitkeep`, `bin/`, `obj/`, `logs/`), `.dockerignore`, `.editorconfig`, `global.json` (SDK 10), `Directory.Build.props` (`Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`).
- [ ] Copiar `Cria_Banco.sql` e o PDF para `docs/referencia/`.
- *Aceite:* `dotnet build` limpo.

**T0.2 Projeto MVC `Estoque.Web`**
- [ ] ASP.NET Core MVC (.NET 10), cultura `pt-BR`, endpoint `/healthz`, Serilog.
- [ ] `Program.cs` com três modos: web (padrão), `--migrate`, `--healthcheck`.
- *Aceite:* `dotnet run` abre a página inicial; `dotnet run -- --healthcheck` retorna exit code 0 com o app no ar.

**T0.3 Dockerfile** (seção 6.2)
- *Aceite:* `docker build` conclui; container roda como usuário não-root (`docker exec ... id`).

**T0.4 `compose.yaml`, `compose.dev.yaml`, `.env.example`** (seção 6)
- [ ] Validar a tag exata da imagem `mssql/server:2025-*` e **se o `sqlcmd` existe** em `/opt/mssql-tools18/bin` na imagem. Se não existir, remover o healthcheck do SQL e usar retry no migrator.
- [ ] `deploy/Caddyfile` e serviço `proxy` (profile `https`); serviço `portainer` (profile `tools`, seção 6.5); pasta `secrets/` com `.gitkeep`; rotação de logs do Docker.
- [ ] Validar `docker compose config` **com e sem** profiles (se reclamar do arquivo de secret ausente com o profile desativado, criar um placeholder no bootstrap).
- *Aceite:* `docker compose config` sem erros; com `COMPOSE_PROFILES=tools`, `docker compose up -d --build` deixa `sqlserver` e `web` saudáveis e o `portainer` no ar (o `proxy` **não** sobe); `curl localhost:8080/healthz` → 200; `https://localhost:9443` abre o Portainer direto na tela de login (sem tela de criação de admin); com `COMPOSE_PROFILES=tools,https` e `SITE_ADDRESS=localhost` o `proxy` também sobe.

**T0.5 Preparação do servidor (caseiro e VPS) e runbook**
- [ ] Escrever `docs/DEPLOY.md` com os passos da seção 6.4 (comandos exatos para Ubuntu e Debian, instalação do Docker pelo repositório oficial, usuário `deploy`, `ufw`, `fail2ban`, `unattended-upgrades`, swap, DNS). Incluir uma seção própria para o **servidor caseiro** (modo C): IP reservado na LAN, bind do `web`/Portainer, sem redirecionamento de portas, acesso remoto só por VPN ou túnel SSH.
- [ ] Script **opcional** `scripts/server-bootstrap.sh` para os passos idempotentes (pacotes, `ufw`, swap, Docker). Ele **não** altera o `sshd_config`: esse passo é manual e documentado, para não bloquear o acesso por engano.
- *Aceite (no servidor):* `uname -m` = `x86_64`; `docker compose version` ok (Docker pelo repositório oficial, não snap); `free -h` ≥ 3,5 GB; `ss -tlnp` mostra escuta apenas nas portas e endereços previstos (modo C: 8080/9443 em `127.0.0.1` ou no IP da LAN; modo A: 22/80/443); porta 1433 inacessível de fora; login SSH por senha recusado.

**T0.6 Domínio e DNS** (tarefa **não-código**, em paralelo às fases 0–3; seção 6.6)
- [ ] Definir extensão e titular (Q19), registrar o domínio, criar o registro A do subdomínio apontando para a VPS, configurar `ACME_CA` de testes.
- [ ] Documentar em `docs/DEPLOY.md`: domínio, titular, vencimento, contatos, onde está o DNS.
- *Aceite:* `dig +short estoque.<dominio>` devolve o IP da VPS; após subir o profile `https` na VPS, o certificado é emitido (primeiro de testes, depois de produção) e `https://estoque.<dominio>/healthz` responde 200.

### Fase 1 — Banco de dados

**T1.1 `V001__schema.sql`** (consolidado, aplica A01–A17 e A19 da seção 3)
- [ ] Tabelas, constraints, índices, computed columns, sequence, lookups (`papeis_usuario`, `unidades_medida` + `lata`/`sache`).
- *Aceite:* script roda em banco vazio sem erro; `INFORMATION_SCHEMA` mostra todas as tabelas esperadas.

**T1.2 Triggers (`V002__triggers.sql`)** — A10, A11, A12, A14
- [ ] `trg_mov_atualiza_estoque` (agregado por item/unidade, upsert, custo médio, `THROW 50001`, estorno no cancelamento).
- [ ] Trigger de imutabilidade/transição de status; bloqueio de DELETE.
- [ ] `atualizado_em` em UTC; auditoria por triggers com `tenant_id`, `unidade_id`, `ip_usuario`, sem coluna `senha`.
- *Aceite:* testes da T1.4.

**T1.3 Migrator (`Data/Migrator`)**
- [ ] Cria o banco se não existir (conexão administrativa em `master`).
- [ ] Aplica `db/migrations/V*.sql` em ordem natural, divide por linha `GO`, transação por arquivo, registra em `schema_migrations (versao, nome, checksum_sha256, aplicado_em)`.
- [ ] **Falha** se o checksum de uma migração já aplicada mudou. Usa `sp_getapplock` para evitar execução concorrente.
- [ ] Retry com backoff (até ~90 s) enquanto o SQL Server inicializa.
- [ ] Passo idempotente de **login `estoque_app`** (A18): senha vinda de `APP_DB_PASSWORD`, escapada corretamente (nada de concatenação ingênua de string).
- [ ] Seed (T3.1) roda ao final, só se o tenant ainda não existir.
- *Aceite:* rodar `--migrate` duas vezes seguidas não altera nada na segunda; alterar um `V001` já aplicado faz o migrator falhar.

**T1.4 Testes das regras do banco** (`db/tests` ou xUnit + Testcontainers)
- [ ] Entrada processada soma saldo e valor; saída subtrai saldo e valor (custo médio).
- [ ] Saída maior que o saldo → erro 50001 e nada é alterado.
- [ ] Cancelar movimentação processada estorna.
- [ ] **Dois INSERTs do mesmo item na mesma instrução** somam corretamente (regressão do bug da A10).
- [ ] Saída `doacao_realizada` sem beneficiário → falha; `compra` sem parceiro → falha; `doacao_recebida` sem parceiro → ok.
- [ ] Dois parceiros pessoa física sem CPF → ok (regressão da A06); mesmo CNPJ duplicado → falha.
- [ ] Segunda matriz no mesmo tenant → falha.
- [ ] Movimentação do tenant A referenciando item do tenant B → falha (FK composta).
- [ ] UPDATE de `quantidade` em movimentação processada → falha; DELETE → falha.
- [ ] Auditoria não contém hash de senha.
- *Aceite:* todos os testes passam.

**T1.5 Manutenção e monitoramento no banco (`V003__manutencao.sql`)** — A20, A21
- [ ] Tabelas `manutencao_log` e `saude_banco_historico`; views `vw_saude_banco` (tamanho de dados e de log em MB, por arquivo) e `vw_tamanho_tabelas` (linhas e MB por tabela, via `sys.dm_db_partition_stats`); espaço livre do volume via `sys.dm_os_volume_stats`.
- [ ] Índice `logs_auditoria (criado_em)`; login `estoque_maint` com os privilégios mínimos da A21 (criado pelo migrator, senha em `MAINT_DB_PASSWORD`).
- [ ] Compressão PAGE confirmada: rodar `sp_estimate_data_compression_savings` com a carga demo e anotar o resultado em `docs/DECISOES.md`.
- *Aceite:* as views retornam valores coerentes; `estoque_maint` consegue `DELETE` em `logs_auditoria` e **não** em `movimentacoes`; `estoque_app` continua sem `DELETE` em `logs_auditoria`; `sys.partitions.data_compression_desc` mostra `PAGE` nas tabelas previstas.

**🚦 GATE 1:** revisar `V001`/`V002` com o responsável antes de construir a aplicação sobre elas.

### Fase 2 — Núcleo da aplicação

**T2.1 `EstoqueDbContext` e entidades**
- [ ] Mapear o schema existente (nomes de tabela/coluna em snake_case via configurações explícitas). Colunas calculadas (`valor_total`, `abaixo_do_minimo`) como `ValueGeneratedOnAddOrUpdate`.
- [ ] Declarar **`HasTrigger(...)`** nas tabelas com trigger (obrigatório no EF Core para SQL Server).
- [ ] `usuarios.senha` mapeada para a propriedade `SenhaHash`.
- *Aceite:* consulta e inserção de uma movimentação funcionam e o saldo muda via trigger.

**T2.2 Contexto de tenant/unidade**
- [ ] `ITenantContext` (`TenantId`, `UnidadeId`, `UsuarioId`, `Papel`) alimentado pelos claims.
- [ ] **Filtros globais** por `TenantId` em todas as entidades de negócio; `SaveChanges` carimba `TenantId` nas inserções e **recusa** entidade com tenant diferente do atual.
- [ ] Entidades por unidade (`EstoqueUnidade`, `Movimentacao`) filtradas também pela `UnidadeId` atual.
- *Aceite:* teste mostrando que um usuário do tenant A não lê nem altera dados do tenant B (inclusive acessando por ID direto → 404).

**T2.3 Interceptor de conexão**
- [ ] A cada abertura de conexão, `sp_set_session_context` para `usuario_id`, `tenant_id`, `unidade_id`, `ip` (nunca assumir que o valor anterior foi limpo pelo pool).
- [ ] **Atrás do proxy:** `UseForwardedHeaders` (`X-Forwarded-For`/`X-Forwarded-Proto`) confiando **apenas** na rede do compose. Sem isso, o IP gravado na auditoria seria o do proxy e a aplicação enxergaria o esquema `http`.
- *Aceite:* linha em `logs_auditoria` traz o usuário e o IP corretos.

**T2.4 Autenticação e autorização**
- [ ] Cookie auth (`HttpOnly`, `SameSite=Lax`, `Secure` conforme ambiente, expiração deslizante), `PasswordHasher<Usuario>`, tela de login, logout (POST + antiforgery).
- [ ] Claims: `usuario_id`, `tenant_id`, `papel`, `unidade_id`.
- [ ] Policies por papel conforme 7.3; `[Authorize]` global por padrão e `[AllowAnonymous]` só no login/healthz.
- [ ] Proteção de `returnUrl` (`Url.IsLocalUrl`). Mensagem de erro de login genérica (não revelar se o e-mail existe).
- [ ] Registrar `LOGIN`/`LOGIN_FALHA`/`LOGOUT` em `logs_auditoria`.
- *Aceite:* rota protegida redireciona para login; `visualizador` recebe 403 ao tentar registrar entrada.

**T2.5 Layout e seletor de unidade**
- [ ] `_Layout` replicando o protótipo (sidebar escura, cabeçalho com seletor, sino, logout, avatar), responsivo (RNF05).
- [ ] `POST /Unidade/Alternar`: valida a unidade contra `usuarios_unidades` + tenant, reemite o cookie com o novo `unidade_id`, registra `ALTERNAR_UNIDADE`.
- *Aceite:* alternar Matriz/Filial muda os dados exibidos; tentar enviar `unidade_id` de outro tenant é recusado.

### Fase 3 — Funcionalidades do MVP

**T3.1 Seed (`Data/Seed`)**
- [ ] Sempre: tenant, Matriz, Filial, admin (`trocar_senha = 1`), categorias (Alimento, Roupa, Brinquedo, Higiene e limpeza, Outros — *sugestão a validar*).
- [ ] Se `SEED_DEMO_DATA=true`: itens do Apêndice A, saldos mínimos de exemplo, um usuário por papel, algumas movimentações.
- *Aceite:* `docker compose up` do zero deixa o sistema utilizável com o admin.

**T3.2 Itens (RF01)** — listar (busca, filtro por categoria), criar, editar, inativar. Campos: nome, categoria (tipo), unidade de medida, preço estimado, origem padrão, observação, quantidade mínima **por unidade**.
**T3.3 Fornecedores (RF06)** — pessoa física (nome), empresa (nome + CNPJ), organização; validação de CPF/CNPJ; fornecedor "anônimo" tratado como `parceiro_id = NULL`.
**T3.4 Nova Entrada (RF02/RF05)** — modal como o protótipo; Origem (Doação/Compra) controla o campo Fornecedor; valor unitário estimado; data; responsável = logado. Cria já como `processada`.
**T3.5 Nova Saída (RF03/RF05)** — Tipo (Uso interno/Doação/Avaria); campo **Beneficiário** aparece e é obrigatório quando Doação; valida saldo no serviço **e** trata o erro 50001 do banco com mensagem amigável.
**T3.6 Estoque e movimentações (RF04)** — abas "Estoque Atual" e "Movimentações" (filtros simples: período, tipo, item); cancelar movimentação (só `admin`) com confirmação e motivo nas observações.
**T3.7 Alertas e dashboard básico (RF11/RF12)** — cards, lista "Alertas de estoque baixo" (`abaixo_do_minimo = 1`), "Últimas movimentações", contador no sino.

*Aceite geral da Fase 3:* roteiro manual completo (cadastrar item → entrada → saída → saldo correto → saldo cai abaixo do mínimo → alerta aparece → cancelar saída → saldo volta → trocar para Filial → saldo da Filial independente).

### Fase 4 — Release do MVP de teste

**T4.1 Smoke test automatizado** (`scripts/smoke.sh`; roda localmente e no servidor de teste; no modo A, com verificação externa de `https://<dominio>/healthz`): sobe o compose do zero (`down -v` + `up`), espera `healthy`, faz login via HTTP, consulta o dashboard, registra entrada e saída, confere o saldo.
**T4.2 `README.md` de deploy:** pré-requisitos (servidor Linux x86-64 — caseiro ou VPS —, Docker Engine + Compose v2, ≥ 4 GB de RAM; preparação em `docs/DEPLOY.md`), passo a passo, como criar `.env`, como resetar (`docker compose down -v`), solução de problemas.
**T4.3 Checklist de segurança mínima** (seção 10, coluna MVP) — todos os itens marcados.
**T4.4** Tag `v0.1.0-mvp`.

**🚦 GATE 2 (aceite do MVP):** no **servidor caseiro** (Ubuntu limpo, modo C: `localhost`/LAN, com Portainer), `cp .env.example .env` → editar senhas → `docker compose up -d --build` → login funcionando e roteiro da Fase 3 passando, **sem nenhum passo manual no banco**.

**T4.5 Deploy de teste na VPS (ambiente da creche)**
- [ ] Pré-requisito: T0.6 concluída (domínio resolvendo para a VPS).
- [ ] Repetir o procedimento em modo A: domínio + HTTPS (`COMPOSE_PROFILES=https`, Portainer desligado ou só por túnel SSH), `COOKIE_SECURE_POLICY=Always`, senhas **novas** (diferentes das do ensaio), `SEED_DEMO_DATA=true` enquanto a creche apenas testa.
- [ ] Entregar à creche um **roteiro de teste de 1 página** (cadastrar item, registrar entrada e saída, ver alerta, alternar Matriz/Filial, cancelar movimentação) e um canal para relatar problemas.
- [ ] **Antes de qualquer dado real** (parceiros com CPF/CNPJ, beneficiários): antecipar o mínimo da E8.3 (backup agendado com cópia externa e **restauração testada**), definir a retenção (Q13), revisar S26 (LGPD) e **recriar o banco do zero** (`down -v`, `SEED_DEMO_DATA=false`) para não misturar dados de teste com reais.
- *Aceite:* `https://<dominio>` com certificado válido; roteiro da Fase 3 passando; S17, S29 e S30 verificados; creche executou o roteiro de teste.

---

## 9. Evolução para a versão completa

### Fase 5 — Funcionalidades restantes

- **E5.1 Usuários e perfis (RF08):** CRUD de usuários (admin), atribuição de unidades (`usuarios_unidades`), ativar/inativar, reset de senha com troca obrigatória, política de senha (mín. 10–12 caracteres, bloqueio de senhas comuns). Avaliar "níveis de hierarquia" do RF08 (hoje 3 papéis; a tabela `papeis_usuario` já permite crescer).
- **E5.2 Relatórios (RF07, RNF10):** filtros (tipo de movimentação, tipo de saída, origem, tipo de item, nome do item, data inicial/final), visualização em tela, **impressão** (CSS `@media print`), **PDF** (QuestPDF) e **CSV** (CsvHelper, protegido contra *CSV/formula injection*: prefixar `'` em células iniciadas por `=`, `+`, `-`, `@`). Consulta por **view** (`R__vw_movimentacoes_detalhe.sql`) com filtro de tenant. Opção de visão **consolidada** (todas as unidades do tenant) apenas para `admin`.
- **E5.3 Dashboard completo (RF11):** itens com maior saída, baixo estoque, nº de movimentações, entradas/saídas recentes, nº de doações realizadas, itens descartados por avaria, gráficos "Doações por mês" e "Doações por tipo" (Chart.js local).
- **E5.4 Log de atividades (RF13):** tela para `admin` com filtros por usuário/ação/data (usuário, ação, data/hora) lendo `logs_auditoria`; auditoria de ações de aplicação (exportações, alteração de senha etc.).
- **E5.5 Página "Sobre a ONG"** (conteúdo a fornecer pela creche).

### Fase 6 — Segurança (endurecimento)

Ver checklist na seção 10. Entregáveis principais: **RLS** com `SESSION_CONTEXT('tenant_id')`; **HSTS** (depois de validar o HTTPS em produção) e revisão do Caddy (cabeçalhos, limites de requisição); certificado do SQL Server; **rate limiting** no login + **lockout**; validação de usuário ativo a cada request (ou *security stamp*); cabeçalhos de segurança + CSP; segredos fora do `.env` (Docker secrets); imagens fixadas por *digest*; varredura de imagem (Trivy) e de dependências.

### Fase 7 — Qualidade

Testes unitários (serviços), de integração (Testcontainers.MsSql: triggers, RLS, isolamento de tenant), E2E (Playwright: login → entrada → saída → relatório), testes de autorização por papel; CI (GitHub Actions: build, test, `docker compose config`, Trivy); cobertura mínima acordada.

### Fase 8 — Operação

**E8.1 Retenção da auditoria** (modo `--maintenance` + serviço `maintenance` no compose)
- [ ] Parâmetros: `Retencao__AuditoriaDadosMeses=24`, `Retencao__AuditoriaAcoesMeses=12`. **Piso de segurança:** valores menores que 12 são rejeitados na inicialização.
- [ ] Fluxo por execução: (1) calcular a data de corte por classe de log (seção 2.1); (2) **exportar** as linhas expiradas para `/archive/auditoria_AAAA-MM.jsonl.gz` (um arquivo por mês); (3) **verificar** contagem de linhas e gravar SHA-256 em `manutencao_log`; (4) só então apagar, em lotes (`DELETE TOP (5000)` em loop, transações curtas, para não inflar o log de transações nem escalar locks); (5) registrar em `manutencao_log` e em `logs_auditoria` (`PURGA_AUDITORIA`).
- [ ] **Nunca** purgar `movimentacoes`, `estoque_unidade`, `itens`, `parceiros`, `usuarios`.
- [ ] Primeira execução em `--dry-run` (apenas relata o que seria exportado/apagado).
- [ ] Usa a credencial `estoque_maint` (A21), nunca `estoque_app`.
- [ ] Agenda semanal (domingo 03:00, `America/Sao_Paulo`) por loop/cron no próprio container; o Express não tem SQL Server Agent.
- [ ] Pós-purga: `UPDATE STATISTICS` e `REORGANIZE` de índices fragmentados. **Não** usar `SHRINKDATABASE` de rotina: o espaço liberado fica reutilizável dentro do arquivo (a purga reduz o *crescimento*, não o tamanho do arquivo) e o shrink fragmenta os índices.
- *Aceite:* com dados de teste antigos, o `--dry-run` lista as linhas corretas; a execução real gera o arquivo, confere contagem e hash, apaga só o que foi exportado e deixa o log consistente; reexecutar não faz nada de novo.

**E8.2 Monitoramento de tamanho e saúde**
- [ ] `BancoSaudeService` lê `vw_saude_banco`, `vw_tamanho_tabelas`, espaço livre do volume e `manutencao_log`; limite configurável `Monitoramento__LimiteBancoGB=50`.
- [ ] Job semanal grava um ponto em `saude_banco_historico` (permite ver a tendência de crescimento).
- [ ] Limiares:

| Indicador | Aviso | Crítico |
|---|---|---|
| Tamanho do banco / limite | ≥ 70% | ≥ 90% |
| Espaço livre do volume do banco | < 20% | < 10% |
| Último backup bem-sucedido | > 36 h | > 72 h |
| Última purga de auditoria bem-sucedida | > 14 dias | > 35 dias |

- [ ] Saídas: página **`/Admin/Saude`** (somente `admin`), **banner** no dashboard do admin quando houver Aviso/Crítico, log Serilog (`Warning`/`Error`).
- [ ] Manter `/healthz` apenas como *liveness* e criar `/healthz/ready` (conectividade com o banco). **Não** ligar o healthcheck do Docker ao tamanho do banco (evita reinício em loop por um problema que reiniciar não resolve).
- [ ] Opcional: notificação externa (e-mail/webhook) — decidir canal com a creche.
- *Aceite:* simulando limite de 0,001 GB, a página mostra Crítico e o banner aparece.

**E8.3 Backup**
- [ ] Sidecar (modo `--maintenance` ou container com `sqlcmd`) executando `BACKUP DATABASE ... WITH COMPRESSION, CHECKSUM` em volume dedicado; retenção sugerida: diários por 30 dias + mensais por 12 meses; registro em `manutencao_log`.
- [ ] **Cópia externa (off-site)** dos backups e dos arquivos de auditoria exportados (ex.: `rclone` para armazenamento S3-compatível, com criptografia). O disco da VPS é ponto único de falha; snapshots do provedor ajudam, mas não substituem.
- [ ] **Teste de restauração** em ambiente limpo, documentado em `docs/RESTAURACAO.md`.
- *Aceite:* restaurar o último backup em um compose novo e passar o smoke test.

**E8.4 Logs e atualização**
- [ ] Rotação de logs da aplicação (30 dias) e do Docker (já no compose); `docker image prune` periódico para não encher o disco da VPS; monitorar também o espaço de `/var/lib/docker`.
- [ ] Procedimento de atualização (backup → pull → migrate → restart) e *rollback* documentados em `docs/DEPLOY.md`.

### Fase 9 — Multi-tenant de verdade *(não planejada — só se o escopo mudar)*

> Hoje o sistema atende **1 matriz + 1 filial** e não há plano de escalar. A estrutura multi-tenant do banco permanece, mas esta fase **não faz parte do planejamento**. Se um dia houver mais organizações, reavaliar antes o limite de memória/CPU do Express e a opção por SQL Server Standard.

Cadastro/onboarding de novos tenants (papel *super-admin* separado, fora dos tenants); personalização por tenant (nome, logo); limites por tenant; revisão de LGPD por tenant; avaliar subdomínio por tenant. Funcionalidade extra **não prevista nos requisitos**: transferência de itens Matriz ⇄ Filial (novo tipo de movimentação) — só se a creche pedir.

---

## 10. Checklist de segurança

| # | Controle | MVP | Completa |
|---|---|:-:|:-:|
| S01 | Nenhum segredo no repositório; `.env` no `.gitignore`; `.env.example` sem valores reais | ✔ | ✔ |
| S02 | Aplicação conecta com `estoque_app` (menor privilégio), **nunca** com `sa` | ✔ | ✔ |
| S03 | Porta 1433 **não publicada** no compose de deploy | ✔ | ✔ |
| S04 | Senhas com `PasswordHasher` (PBKDF2); hash nunca em log/auditoria/JSON | ✔ | ✔ |
| S05 | Antiforgery em todos os POST (`AutoValidateAntiforgeryToken` global) | ✔ | ✔ |
| S06 | Cookie `HttpOnly`, `SameSite`, expiração; `Secure` sempre (com HTTPS) | ✔ (modo A; modo C: `SameAsRequest`) | ✔ |
| S07 | `[Authorize]` global + policy por papel em cada action; ViewModels (sem *mass assignment*) | ✔ | ✔ |
| S08 | Filtro por tenant/unidade em toda consulta; ID de outro tenant → **404** | ✔ | ✔ |
| S09 | Somente consultas parametrizadas (EF/Dapper); proibido concatenar SQL | ✔ | ✔ |
| S10 | Validação server-side de todos os campos (tamanhos, CPF/CNPJ, faixas) | ✔ | ✔ |
| S11 | Erros sem *stack trace* em Production; página de erro amigável | ✔ | ✔ |
| S12 | Container web não-root, `read_only`, `cap_drop: ALL`, `no-new-privileges` | ✔ | ✔ |
| S13 | Volume `dataprotection` persistente | ✔ | ✔ |
| S14 | Auditoria imutável para a aplicação (DENY UPDATE/DELETE em `logs_auditoria` ao `estoque_app`) | ✔ | ✔ |
| S15 | `returnUrl` somente local; mensagem de login genérica | ✔ | ✔ |
| S16 | Seed: app recusa subir em Production com senha de admin vazia/padrão; `trocar_senha` no primeiro acesso | ✔ | ✔ |
| S17 | HTTPS via proxy reverso (Caddy) com redirecionamento HTTP→HTTPS; HSTS após validação | ✔ (modo A) | ✔ |
| S18 | Rate limiting e lockout no login; log de tentativas | — | ✔ |
| S19 | Row-Level Security por `tenant_id` (política de FILTER e BLOCK) | — | ✔ |
| S20 | Cabeçalhos: CSP, `X-Content-Type-Options`, `Referrer-Policy`, `frame-ancestors` | — | ✔ |
| S21 | Revalidar usuário ativo/papel a cada request (ou *security stamp*) | — | ✔ |
| S22 | Imagens por *digest*, Trivy, `dotnet list package --vulnerable` no CI | — | ✔ |
| S23 | Segredos via Docker secrets; rotação de senhas documentada | — | ✔ |
| S24 | CSV com proteção contra *formula injection*; PDF sem dados além do filtro autorizado | — | ✔ |
| S25 | Backups criptografados/retidos e **restauração testada** | — | ✔ |
| S26 | **LGPD:** minimizar dados pessoais (CPF/e-mail/telefone/endereço de parceiros, nome de beneficiários), registrar acesso, definir retenção e como atender pedidos de titulares; avaliar anonimização do campo `beneficiario` em relatórios | — | ✔ |
| S27 | Purga de auditoria só via `estoque_maint` (DELETE restrito a `logs_auditoria`), com exportação verificada, piso mínimo de retenção e registro em `manutencao_log` | — | ✔ |
| S28 | Arquivos de auditoria exportados em volume próprio, incluídos no backup, com acesso restrito (contêm dados pessoais — S26) | — | ✔ |
| S29 | Servidor: SSH só por chave, sem login de root, `fail2ban`, firewall 22/80/443, `unattended-upgrades`, relógio sincronizado | ✔ | ✔ |
| S30 | Nenhuma porta de container publicada além de 80/443 (proxy), do `web` (8080) e do Portainer (9443) nos endereços de bind previstos (padrão `127.0.0.1`); o banco nunca; lembrar que o Docker contorna o `ufw` | ✔ | ✔ |
| S31 | Rotação de logs do Docker e limite de tamanho (disco da VPS) | ✔ | ✔ |
| S32 | Backups com cópia externa criptografada; restauração testada (complementa S25) | — | ✔ |
| S33 | Portainer: UI só em loopback/LAN (nunca internet); 9000 e 8000 não publicados; admin pré-configurado com senha ≥ 12 caracteres; ciência de que `docker.sock` ≈ root e de que o admin vê as senhas nas variáveis de ambiente; na VPS, desligado ou só por túnel SSH | ✔ | ✔ |
| S34 | Servidor caseiro: sem redirecionamento de portas/DMZ/UPnP no roteador; dados fictícios; acesso remoto só por VPN ou túnel SSH | ✔ | ✔ |
| S35 | Conta do domínio no Registro.br: titular correto, autenticação em dois fatores (se disponível), renovação automática, dois contatos de e-mail; credenciais fora do repositório | ✔ | ✔ |

---

## 11. Rastreabilidade requisito → fase

| Requisito | Fase | | Requisito | Fase |
|---|---|---|---|---|
| RF01 Cadastro de itens | 3 (T3.2) | | RNF01 Usabilidade | 2–3, validar com a creche |
| RF02 Entrada | 3 (T3.4) | | RNF02 SQL Server | 0–1 |
| RF03 Saída | 3 (T3.5) | | RNF03 ASP.NET Core | 0 |
| RF04 Controle de estoque | 1, 3 (T3.6) | | RNF04 Autenticação | 2 (T2.4) |
| RF05 Responsável | 3 (T3.4–3.5) | | RNF05 Responsividade | 2 (T2.5) |
| RF06 Fornecedores | 3 (T3.3) | | RNF06 Integridade | 1 (triggers/CHECK/FKs) |
| RF07 Relatórios | 5 (E5.2) | | RNF07 Desempenho | 1 (índices), 7 |
| RF08 Usuários/permissões | 2 (papéis) + 5 (E5.1) | | RNF08 Acesso simultâneo | 1 (transações/locks), 7 |
| RF09 Login | 2 (T2.4) | | RNF09 Confiabilidade | 1, 8 (backup, retenção, monitoramento) |
| RF10 Matriz/Filial | 1 (A02–A04), 2 (T2.5) | | RNF10 PDF/CSV | 5 (E5.2) |
| RF11 Dashboard | 3 (básico) + 5 (E5.3) | | | |
| RF12 Alertas | 3 (T3.7) | | | |
| RF13 Log de atividades | 1 (triggers) + 5 (E5.4) | | | |

---

## 12. Suposições adotadas e pontos a confirmar

Onde houve ambiguidade, foi adotado o padrão abaixo. **Se alguma estiver errada, ajuste antes da Fase 1 (as Q01–Q04 afetam o schema).**

| # | Tema | Suposição adotada |
|---|---|---|
| Q01 | **Modelo multi-tenant** | *Tenant* = uma organização (a creche); cada tenant tem 1 matriz + N filiais. **Catálogo de itens, categorias e fornecedores é compartilhado pelo tenant; saldo e mínimo são por unidade.** |
| Q02 | Usuários | Cada usuário pertence a **um** tenant e pode acessar **uma ou mais** unidades dele (`usuarios_unidades`). Não há usuário em vários tenants. |
| Q03 | Beneficiário | A saída "Doação" exige beneficiário (regra do SQL), mas o protótipo **não tem esse campo** no formulário de saída → adicionamos o campo (condicional). |
| Q04 | Valor | "Valor estimado" do formulário é o **valor unitário** estimado (`valor_unitario`); o total é calculado. Saída baixa o estoque pelo **custo médio** (A10). |
| Q05 | Permissões | Matriz da seção 7.3; **só admin cancela movimentação**. |
| Q06 | Status `pendente` | Não usado no MVP: entradas/saídas nascem `processada`. O estado existe no banco para um futuro fluxo de aprovação. |
| Q07 | Auto-cadastro | A tela de login do protótipo parece ter "Não tem conta? Cadastre-se". Isso conflita com RF08 (controle por admin) → **sem auto-cadastro**; admin cria usuários. |
| Q08 | Nome da filial | Seed usa "Matriz" e "Filial"; o nome real da filial é desconhecido. |
| Q09 | Categorias | Alimento, Roupa, Brinquedo, Higiene e limpeza, Outros (o PDF cita alimentos, roupas, brinquedos e "demais materiais") — validar com a creche. |
| Q10 | Ambientes | (1) Dev; (2) **ensaio** no servidor caseiro do desenvolvedor, por `localhost`/LAN em HTTP, dados fictícios e Portainer (modo C); (3) **teste na creche na VPS Linux x86-64** (Ubuntu/Debian), com HTTPS (modo A), que depois vira produção. O servidor é endurecido na T0.5. |
| Q11 | Quantidades | Inteiras (`INT`), em embalagens/unidades (a planilha controla "Arroz 5 kg" como item separado). Se precisarem de fração (ex.: 2,5 kg), muda para `DECIMAL`. |
| Q12 | Licença QuestPDF | Uso sem fins lucrativos enquadra na licença Community — conferir antes da Fase 5. |
| Q13 | Retenção da auditoria | 24 meses para alterações de dados e 12 meses para ações de aplicação (login etc.), com exportação para arquivo antes de apagar. **Validar com a creche** (prestação de contas, exigências de convênios) e com a revisão de LGPD (S26). |
| Q14 | Escala | 1 matriz + 1 filial, sem plano de escalar; Fase 9 fora do planejamento. Limite de 50 GB tratado como folga ampla; o risco real é disco/backup. |
| Q15 | Alertas | Aviso/Crítico aparecem na tela do admin e no log; notificação por e-mail/webhook é opcional e depende de canal a definir. |
| Q16 | Domínio | **Necessário para o teste da creche**: os usuários precisam acessar por HTTPS, então a VPS precisa de um domínio com registro A apontando para ela. **Será registrado do zero** (seção 6.6, T0.6). O modo B (túnel SSH) só serve ao desenvolvedor. |
| Q19 | Titular e extensão do domínio | **A confirmar:** `.org.br` no nome da creche (precisa de CNPJ e comprovação de entidade sem fins lucrativos); alternativa mais rápida: `.com.br`. Também definir **quem paga e quem renova** (custo ~R$ 40/ano para `.br`) e quem recebe os avisos de vencimento. |
| Q17 | Especificação da VPS | Provedor e plano ainda não definidos: conferir x86-64, ≥ 4 GB de RAM, ≥ 40 GB de SSD e se o plano inclui snapshots/backup. |
| Q18 | Servidor caseiro | **A confirmar:** `uname -m` = `x86_64`, ≥ 4 GB de RAM (mín. ~3,5 GB livres) e disco suficiente. Se for ARM (ex.: Raspberry Pi), a imagem do SQL Server não roda e o servidor não serve para este teste. |

---

## 13. Prompts para o Claude Code e `CLAUDE.md` sugerido

### 13.1 `CLAUDE.md` (colocar na raiz)

```markdown
# Projeto: Sistema de Estoque e Doações — Creche Fraternidade Menino Jesus

Leia `PLANO.md` antes de qualquer tarefa. Ele é a fonte de verdade de escopo, ordem e critérios de aceite.

## Stack
.NET 10 / ASP.NET Core MVC / EF Core 10 / SQL Server Express 2025 (container) / Docker Compose.

## Regras
- Execute uma fase do PLANO.md por vez; só avance com o critério de aceite verificado.
- Banco: mudanças só via `db/migrations/V###__*.sql` (nunca editar migração já aplicada). Sem EF Migrations.
- Todo acesso a dados respeita tenant e unidade atuais (filtros globais). Nunca ler tenant de parâmetro de URL/form.
- Sem SQL concatenado. Sem segredos no código ou no repositório.
- UI e mensagens em pt-BR; entidades de domínio em português; datas gravadas em UTC.
- Antes de concluir uma tarefa: `dotnet build`, `dotnet test`, `docker compose config`.
- Ambiguidade → registrar em `docs/DECISOES.md` e perguntar; não inventar regra de negócio.
- Commits pequenos e descritivos (um por tarefa).
```

### 13.2 Prompts prontos (copiar e colar)

**Fase 0**
> Leia `PLANO.md` e `CLAUDE.md`. Execute a **Fase 0** (T0.1–T0.5), incluindo o Portainer (profile `tools`), o proxy Caddy (profile `https`) e o runbook `docs/DEPLOY.md` com a seção do servidor caseiro. Antes de escrever o compose, valide a tag da imagem `mcr.microsoft.com/mssql/server:2025-*` e se o `sqlcmd` existe em `/opt/mssql-tools18/bin` dentro dela; ajuste o healthcheck conforme o resultado. Ao final, mostre a saída de `docker compose ps` e do `/healthz`.

**Fase 1**
> Execute a **Fase 1**. Parta do `docs/referencia/Cria_Banco.sql`, aplique os ajustes A01–A21 da seção 3 e produza `V001`/`V002`/`V003`, o migrator e os testes das T1.4 e T1.5. Liste cada ajuste e onde foi implementado. Pare no **GATE 1** para minha revisão.

**Fase 2**
> Execute a **Fase 2** (T2.1–T2.5). Priorize o isolamento por tenant: escreva primeiro o teste de que um usuário do tenant A não acessa dados do tenant B e só então implemente os filtros.

**Fase 3**
> Execute a **Fase 3** (T3.1–T3.7), uma tela por vez, validando o roteiro de aceite ao final. Reproduza o layout do protótipo descrito na seção 1.

**Fase 4**
> Execute a **Fase 4**. Rode o smoke test do zero (`docker compose down -v && up`) e me entregue o checklist de segurança do MVP preenchido, apontando qualquer item que não pôde ser cumprido.

**Versão completa**
> Execute a **Fase 5/6/7/8** (uma por vez) do `PLANO.md`. Antes de começar, releia a seção 10 e me diga quais controles de segurança serão cobertos nesta fase.

---

## Apêndice A — Itens de exemplo extraídos da planilha manual (seed demo)

Açúcar (Kg) · Açúcar demerara (Kg) · Adoçante 75 ml · Arroz 5 kg · Arroz 2 kg · Arroz 1 kg · Atum 170 g · Azeite 500 ml · Azeitona (sachê) · Batata palha 380 g · Biscoito aveia e mel 135 g · Biscoito de água e sal 360 g · Biscoito de maisena 360 g · Biscoito de polvilho 200 g · Biscoito rosca de coco 300 g · Cacau 100% 500 g · Canela em pó 50 g · Canjica (Kg) · Chá matte 250 g · Chia · Coco ralado (Kg) · Creme de leite 200 ml · Doce de batata/abóbora (cx) · Ervilha (lata) · Farinha de mandioca 500 g · Farinha de mandioca 1 kg · Farinha de trigo (Kg) · Farinha de rosa 500 g

*(Lista parcial — a planilha completa tem mais produtos. Pedir à creche o arquivo original para importar o catálogo real.)*
