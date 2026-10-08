#!/usr/bin/env bash
# T4.1 — Smoke test: sobe o compose DO ZERO (apaga volumes!), espera o web ficar
# saudável, faz login, abre o dashboard, cadastra um item, registra entrada e
# saída e confere o saldo.
#
# Uso:  ./scripts/smoke.sh            (pede confirmação antes de apagar os volumes)
#       SMOKE_SIM=1 ./scripts/smoke.sh
#       SMOKE_URL_EXTERNA=https://estoque.exemplo.org ./scripts/smoke.sh   (modo A: confere /healthz externo)
#       SMOKE_BASE=http://localhost:5216 ./scripts/smoke.sh   (instância já no ar, sem Docker e sem apagar nada;
#                                                             precisa de dados demo e das variáveis SEED_* no ambiente/.env)
#
# Usa o usuário demo "operacional@<dominio do SEED_ADMIN_EMAIL>" (sem troca de senha obrigatória),
# por isso força SEED_DEMO_DATA=true nesta execução.
set -euo pipefail
cd "$(dirname "$0")/.."

if [ -f .env ]; then set -a; . ./.env; set +a
elif [ -z "${SMOKE_BASE:-}" ]; then echo "Falta o arquivo .env (copie de .env.example)"; exit 1; fi

BASE="${SMOKE_BASE:-http://${WEB_BIND:-127.0.0.1}:${WEB_PORT:-8080}}"
CODIGO="SMOKE-$(date +%s)"
EMAIL="operacional@${SEED_ADMIN_EMAIL#*@}"
SENHA="${SEED_ADMIN_PASSWORD:?SEED_ADMIN_PASSWORD vazio no .env}"
JAR="$(mktemp)"; trap 'rm -f "$JAR"' EXIT

if [ -z "${SMOKE_BASE:-}" ] && [ "${SMOKE_SIM:-}" != "1" ]; then
  read -r -p "Isto executa 'docker compose down -v' e APAGA o banco. Continuar? [s/N] " r
  [ "$r" = "s" ] || [ "$r" = "S" ] || exit 1
fi

passo() { printf '\n==> %s\n' "$*"; }
falha() { echo "FALHOU: $*"; exit 1; }
token() { grep -o 'name="__RequestVerificationToken" type="hidden" value="[^"]*"' | head -1 | sed 's/.*value="//; s/"$//'; }

if [ -z "${SMOKE_BASE:-}" ]; then
  passo "Recriando o ambiente do zero"
  docker compose down -v --remove-orphans
  SEED_DEMO_DATA=true docker compose up -d --build

  passo "Aguardando o web ficar saudável"
  st=""
  for _ in $(seq 1 60); do
    st="$(docker inspect --format '{{.State.Health.Status}}' "$(docker compose ps -q web)" 2>/dev/null || true)"
    [ "$st" = "healthy" ] && break
    sleep 5
  done
  [ "$st" = "healthy" ] || { docker compose ps; docker compose logs --tail 50 migrator web; falha "web não ficou healthy"; }
fi
curl -fsS "$BASE/healthz" >/dev/null || falha "/healthz"
echo "healthz OK"

passo "Login como $EMAIL"
T="$(curl -fsS -c "$JAR" -b "$JAR" "$BASE/Conta/Login" | token)"
code="$(curl -sS -o /dev/null -w '%{http_code}' -c "$JAR" -b "$JAR" \
  --data-urlencode "Email=$EMAIL" --data-urlencode "Senha=$SENHA" --data-urlencode "__RequestVerificationToken=$T" \
  "$BASE/Conta/Login")"
[ "$code" = "302" ] || falha "login devolveu HTTP $code"

passo "Dashboard"
curl -fsS -b "$JAR" "$BASE/" | grep -q "Doações recebidas" || falha "dashboard"

passo "Cadastrando item $CODIGO"
T="$(curl -fsS -c "$JAR" -b "$JAR" "$BASE/Itens/Criar" | token)"
CAT="$(curl -fsS -b "$JAR" "$BASE/Itens/Criar" | grep -o '<option value="[0-9]*">Alimento' | head -1 | grep -o '[0-9]*')"
curl -fsS -o /dev/null -c "$JAR" -b "$JAR" \
  --data-urlencode "Input.CodigoInterno=$CODIGO" --data-urlencode "Input.Nome=Item do smoke test" \
  --data-urlencode "Input.CategoriaId=$CAT" --data-urlencode "Input.UnidadeMedida=unidade" \
  --data-urlencode "Input.QuantidadeMinima=8" --data-urlencode "__RequestVerificationToken=$T" \
  "$BASE/Itens/Criar"
ITEM="$(curl -fsS -b "$JAR" "$BASE/Itens?busca=$CODIGO" | grep -o '/Itens/Editar/[0-9]*' | head -1 | grep -o '[0-9]*$')"
[ -n "$ITEM" ] || falha "item não encontrado após cadastro"

HOJE="$(date +%F)"
passo "Entrada de 10 unidades"
T="$(curl -fsS -c "$JAR" -b "$JAR" "$BASE/Estoque" | token)"
curl -fsS -o /dev/null -c "$JAR" -b "$JAR" \
  --data-urlencode "Entrada.Origem=doacao_recebida" --data-urlencode "Entrada.ItemId=$ITEM" \
  --data-urlencode "Entrada.Quantidade=10" --data-urlencode "Entrada.ValorUnitario=2,50" \
  --data-urlencode "Entrada.Data=$HOJE" --data-urlencode "__RequestVerificationToken=$T" \
  "$BASE/Estoque/NovaEntrada"

passo "Saída de 4 unidades (uso interno)"
curl -fsS -o /dev/null -c "$JAR" -b "$JAR" \
  --data-urlencode "Saida.ItemId=$ITEM" --data-urlencode "Saida.Quantidade=4" \
  --data-urlencode "Saida.TipoSaida=consumo_interno" --data-urlencode "Saida.Data=$HOJE" \
  --data-urlencode "__RequestVerificationToken=$T" \
  "$BASE/Estoque/NovaSaida"

passo "Conferindo saldo (esperado: 6 unidade, abaixo do mínimo 8)"
PAG="$(curl -fsS -b "$JAR" "$BASE/Estoque?aba=estoque&busca=$CODIGO")"
echo "$PAG" | grep -q '>6 unidade<' || falha "saldo diferente de 6"
echo "$PAG" | grep -q 'Estoque baixo' || falha "alerta de estoque baixo não apareceu"

passo "Saída maior que o saldo deve ser recusada"
PAG="$(curl -fsS -c "$JAR" -b "$JAR" \
  --data-urlencode "Saida.ItemId=$ITEM" --data-urlencode "Saida.Quantidade=999" \
  --data-urlencode "Saida.TipoSaida=avaria" --data-urlencode "Saida.Data=$HOJE" \
  --data-urlencode "__RequestVerificationToken=$T" \
  "$BASE/Estoque/NovaSaida")"
echo "$PAG" | grep -q 'Estoque insuficiente' || falha "saída acima do saldo não foi bloqueada"

if [ -n "${SMOKE_URL_EXTERNA:-}" ]; then
  passo "Verificação externa $SMOKE_URL_EXTERNA/healthz"
  curl -fsS "$SMOKE_URL_EXTERNA/healthz" >/dev/null || falha "healthz externo"
fi

passo "SMOKE TEST OK"
[ -n "${SMOKE_BASE:-}" ] || docker compose ps
