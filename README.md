# Estoque & Doações — Creche Fraternidade Menino Jesus

Sistema web (ASP.NET Core MVC + SQL Server) para controle de estoque e doações por unidade (Matriz/Filial):
itens, fornecedores, entradas (doação/compra), saídas (uso interno/doação/avaria), saldo por unidade,
alertas de estoque mínimo, dashboard e auditoria. Escopo e roadmap: [`PLANO.md`](PLANO.md).

**Versão: MVP (Fases 0–4 do plano).** Relatórios PDF/CSV, gráficos, CRUD de usuários e tela de log ficam para a versão completa.

## Rodar localmente sem Docker (SQL Server em `localhost`)

Pré-requisitos: .NET SDK 10, SQL Server 2022/2025 local com autenticação mista habilitada.

```bash
cp src/Estoque.Web/appsettings.Local.example.json src/Estoque.Web/appsettings.Local.json
# edite as senhas no arquivo copiado
dotnet run --project src/Estoque.Web --no-launch-profile -- --migrate
dotnet run --project src/Estoque.Web --launch-profile http
```

Abra http://localhost:5216 e entre com `SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD` (a troca de senha é obrigatória no primeiro acesso).
Com `SEED_DEMO_DATA=true` também existem `operacional@creche.local` e `visualizador@creche.local`, com a mesma senha do admin.

Testes de integração (criam e apagam bancos `EstoqueTeste_*`; usam `ESTOQUE_TEST_ADMIN_CS` ou localhost com autenticação Windows):

```bash
dotnet test
```

## Rodar com Docker (servidor de ensaio / VPS)

Pré-requisitos: Linux **x86-64**, Docker Engine + Compose v2, ≥ 4 GB de RAM. Preparação do servidor: [`docs/DEPLOY.md`](docs/DEPLOY.md).

```bash
cp .env.example .env && chmod 600 .env          # troque todas as senhas
printf '%s' '<senha-portainer-12+>' > secrets/portainer_admin_password.txt
docker compose up -d --build
curl -fsS http://127.0.0.1:8080/healthz
```

| Serviço | Papel |
|---|---|
| `sqlserver` | SQL Server 2025 Express; porta **não** publicada |
| `migrator` | mesmo binário com `--migrate`: cria o banco, aplica `db/migrations`, cria os logins e faz o seed; termina com código 0 |
| `web` | aplicação (porta 8080 em `WEB_BIND`), conecta como `estoque_app` |
| `proxy` | Caddy com HTTPS automático (profile `https`) |
| `portainer` | gestão visual (profile `tools`, `https://localhost:9443`) |

- Smoke test (**apaga o banco**): `./scripts/smoke.sh`
- Reset completo: `docker compose down -v`
- Dev com banco exposto em `127.0.0.1:1433`: `docker compose -f compose.yaml -f compose.dev.yaml up -d --build`

### Solução de problemas

| Sintoma | Causa provável |
|---|---|
| `migrator` sai com erro de senha | `MSSQL_SA_PASSWORD` fraca (mín. 8 caracteres, 3 classes) ou `SEED_ADMIN_PASSWORD` < 12 caracteres / valor padrão |
| `migrator`: "checksum diferente" | alguém editou uma migração já aplicada; reverta e crie uma nova `V###` |
| `sqlserver` nunca fica healthy | ver `docs/DECISOES.md` D-07 (caminho do `sqlcmd` na imagem) e `docker compose logs sqlserver` |
| Logout a cada restart | volume `dataprotection` removido |
| CPU ARM | a imagem do SQL Server só roda em x86-64 |

## Estrutura

```
db/migrations/      V001 schema · V002 triggers · V003 manutenção (aplicados pelo migrator)
docs/               DECISOES.md · DEPLOY.md · referencia/Cria_Banco.sql (original)
src/Estoque.Web/    Controllers · Data (DbContext, Migrator, Seed, Interceptors) · Services · Views
tests/Estoque.Tests regras do banco (T1.4), migrator (T1.3), isolamento de tenant (T2.2)
scripts/            smoke.sh · server-bootstrap.sh
```

## Checklist de segurança do MVP (PLANO.md §10)

| # | Controle | Situação |
|---|---|---|
| S01 | Sem segredos no repositório | ✔ `.env`, `secrets/*` e `appsettings.Local.json` no `.gitignore` |
| S02 | App conecta como `estoque_app`, nunca `sa` | ✔ |
| S03 | 1433 não publicada no deploy | ✔ (só `compose.dev.yaml`, em loopback) |
| S04 | PBKDF2 (`PasswordHasher`); hash fora da auditoria | ✔ testado |
| S05 | Antiforgery global | ✔ POST sem token → 400 |
| S06 | Cookie HttpOnly, SameSite=Lax, expiração 8 h deslizante; Secure configurável | ✔ |
| S07 | `[Authorize]` global + policies; ViewModels | ✔ visualizador/operacional recebem acesso negado nas ações restritas |
| S08 | Filtro por tenant/unidade; ID de outro tenant → 404 | ✔ testado |
| S09 | Só consultas parametrizadas | ✔ (DDL de login com escape no servidor) |
| S10 | Validação server-side | ✔ DataAnnotations + CPF/CNPJ + regras no serviço e no banco |
| S11 | Sem stack trace em Production | ✔ `/Home/Erro` |
| S12 | Container não-root, `read_only`, `cap_drop`, `no-new-privileges` | ✔ no compose (**não validado em Docker**, ver abaixo) |
| S13 | Volume `dataprotection` | ✔ |
| S14 | `estoque_app` sem UPDATE/DELETE em `logs_auditoria` | ✔ verificado com `HAS_PERMS_BY_NAME` |
| S15 | `returnUrl` local; mensagem de login genérica | ✔ |
| S16 | Senha do admin validada; troca obrigatória | ✔ |
| S17 | HTTPS via Caddy | ✔ configurado (modo A; depende do domínio) |
| S29 | Servidor endurecido | runbook + `server-bootstrap.sh` (execução no servidor pendente) |
| S30 | Portas publicadas só nos binds previstos | ✔ no compose |
| S31 | Rotação de logs do Docker | ✔ |
| S33/S34 | Portainer só em loopback/LAN; sem port forwarding | ✔ compose / runbook |
| S35 | Conta do domínio | pendente (T0.6, não-código) |

**Não verificado nesta máquina:** a máquina de desenvolvimento não tem Docker, então `docker compose config`, o build da
imagem, os healthchecks e o `smoke.sh` em modo Docker ficam para o GATE 2 no servidor caseiro.
O `smoke.sh` foi validado contra a aplicação local (`SMOKE_BASE=http://localhost:5216`).
