# Projeto: Sistema de Estoque e Doações — Creche Fraternidade Menino Jesus

Leia `PLANO.md` antes de qualquer tarefa. Ele é a fonte de verdade de escopo, ordem e critérios de aceite.
Decisões tomadas durante a implementação estão em `docs/DECISOES.md`.

## Stack
.NET 10 / ASP.NET Core MVC / EF Core 10 / SQL Server Express 2025 (container) / Docker Compose.

## Regras
- Execute uma fase do PLANO.md por vez; só avance com o critério de aceite verificado.
- Banco: mudanças só via `db/migrations/V###__*.sql` (nunca editar migração já aplicada — o migrator falha pelo checksum). Sem EF Migrations.
- Todo acesso a dados respeita tenant e unidade atuais (filtros globais). Nunca ler tenant de parâmetro de URL/form.
- Sem SQL concatenado. Sem segredos no código ou no repositório (`appsettings.Local.json` e `.env` são gitignored).
- UI e mensagens em pt-BR; entidades de domínio em português; datas gravadas em UTC.
- Antes de concluir uma tarefa: `dotnet build`, `dotnet test`, `docker compose config`.
- Ambiguidade → registrar em `docs/DECISOES.md` e perguntar; não inventar regra de negócio.
- Commits pequenos e descritivos (um por tarefa).

## Comandos locais (sem Docker, SQL Server em localhost)
- Migração + seed: `dotnet run --project src/Estoque.Web --no-launch-profile -- --migrate`
- App: `dotnet run --project src/Estoque.Web --launch-profile http` → http://localhost:5216
- Testes (criam e apagam bancos `EstoqueTeste_*`): `dotnet test`
