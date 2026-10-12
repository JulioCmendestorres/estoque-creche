# Estoque & Doações — Creche Fraternidade Menino Jesus

Sistema web (ASP.NET Core MVC + SQL Server) para controle de estoque e doações por unidade (Matriz/Filial):
itens, fornecedores, entradas (doação/compra), saídas (uso interno/doação/avaria), saldo por unidade,
alertas de estoque mínimo, dashboard e auditoria. Escopo e roadmap: [`PLANO.md`](PLANO.md).

**Versão: MVP (Fases 0–4 do plano).** Relatórios PDF/CSV, gráficos, cadastro de usuários e tela de log ficam para a versão completa.

## Instalar no servidor

Servidor Linux **x86-64** com Docker e 4 GB de RAM.

```bash
git clone git@github.com:JulioCmendestorres/estoque-creche.git
cd estoque-creche
./scripts/instalar.sh
```

O script pergunta apenas o **domínio** e a **porta**; senhas e administrador inicial são gerados e mostrados no final.
Para atualizar ou mudar domínio/porta, rode o script de novo. Detalhes e solução de problemas: [`docs/DEPLOY.md`](docs/DEPLOY.md).

## Desenvolvimento (sem Docker, SQL Server em `localhost`)

Pré-requisitos: .NET SDK 10 e SQL Server 2022/2025 local com autenticação mista.

```bash
cp src/Estoque.Web/appsettings.Local.example.json src/Estoque.Web/appsettings.Local.json
# edite as senhas no arquivo copiado
dotnet run --project src/Estoque.Web --no-launch-profile -- --migrate
dotnet run --project src/Estoque.Web --launch-profile http
```

Abra http://localhost:5216 e entre com `SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD`. Com `SEED_DEMO_DATA=true` o banco já vem
com itens e movimentações de exemplo (e usuários `operacional@` e `visualizador@` com a mesma senha do admin).

Testes de integração (criam e apagam bancos `EstoqueTeste_*`; usam `ESTOQUE_TEST_ADMIN_CS` ou localhost com autenticação Windows):

```bash
dotnet test
```

## Estrutura

```
db/migrations/      V001 schema · V002 triggers · V003 manutenção · V004 só CNPJ + unidades por tenant (aplicados pelo migrator)
docs/               DEPLOY.md · DECISOES.md · referencia/Cria_Banco.sql (original)
src/Estoque.Web/    Controllers · Data (DbContext, Migrator, Seed, Interceptors) · Services · Views
tests/Estoque.Tests regras do banco (T1.4), migrator (T1.3), isolamento de tenant (T2.2)
scripts/            instalar.sh · smoke.sh · server-bootstrap.sh
compose.yaml        sqlserver · migrator · web · proxy (Caddy)      compose.portainer.yaml  opcional
```

## Checklist de segurança do MVP (PLANO.md §10)

| # | Controle | Situação |
|---|---|---|
| S01 | Sem segredos no repositório | ✔ `.env`, `secrets/*` e `appsettings.Local.json` no `.gitignore` |
| S02 | App conecta como `estoque_app`, nunca `sa` | ✔ |
| S03 | 1433 não publicada no deploy | ✔ (só `compose.dev.yaml`, em loopback) |
| S04 | PBKDF2 (`PasswordHasher`); hash fora da auditoria | ✔ testado |
| S05 | Antiforgery global | ✔ POST sem token → 400 |
| S06 | Cookie HttpOnly, SameSite=Lax, expiração 8 h deslizante; Secure | ✔ `Always` quando há domínio (HTTPS) |
| S07 | `[Authorize]` global + policies; ViewModels | ✔ visualizador/operacional recebem acesso negado nas ações restritas |
| S08 | Filtro por tenant/unidade; ID de outro tenant → 404 | ✔ testado |
| S09 | Só consultas parametrizadas | ✔ (DDL de login com escape no servidor) |
| S10 | Validação server-side | ✔ DataAnnotations + CNPJ + regras no serviço e no banco |
| S11 | Sem stack trace em Production | ✔ `/Home/Erro` |
| S12 | Container não-root, `read_only`, `cap_drop`, `no-new-privileges` | ✔ no compose (**não validado em Docker**, ver abaixo) |
| S13 | Volume `dataprotection` | ✔ |
| S14 | `estoque_app` sem UPDATE/DELETE em `logs_auditoria` | ✔ verificado com `HAS_PERMS_BY_NAME` |
| S15 | `returnUrl` local; mensagem de login genérica | ✔ |
| S16 | Senha do admin gerada; troca obrigatória no 1º acesso | ✔ |
| S17 | HTTPS via Caddy | ✔ automático quando há domínio |
| S26 | LGPD: minimizar dados pessoais | parcial: **CPF de pessoa não é registrado** (só CNPJ, opcional); restante na versão completa |
| S29 | Servidor endurecido | `server-bootstrap.sh` (execução no servidor pendente) |
| S30 | Portas publicadas | ✔ só o proxy (Caddy) publica porta; banco e web ficam na rede interna do compose |
| S31 | Rotação de logs do Docker | ✔ |
| S33/S34 | Portainer opcional, só loopback; sem port forwarding | ✔ `compose.portainer.yaml` / DEPLOY |
| S35 | Conta do domínio | pendente (T0.6, não-código) |

**Não verificado nesta máquina:** a máquina de desenvolvimento não tem Docker. O `instalar.sh` foi testado com um `docker`
simulado (geração do `.env`, domínio/porta, reexecução, mensagens de erro) e a estrutura do `compose.yaml` foi validada,
mas `docker compose config`, o build da imagem, os healthchecks e o `smoke.sh` em modo Docker ficam para o primeiro deploy
no servidor. O `smoke.sh` foi validado contra a aplicação local (`SMOKE_BASE=http://localhost:5216`).
