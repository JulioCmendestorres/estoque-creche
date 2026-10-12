#!/usr/bin/env bash
# Instala ou atualiza o sistema com UM comando:   ./scripts/instalar.sh
#
# A única coisa que ele pergunta é o DOMÍNIO e a PORTA. Todo o resto (senhas do banco, administrador
# inicial, HTTPS) é gerado/derivado e gravado no arquivo .env. Pode rodar de novo a qualquer momento:
# as senhas são mantidas e só o domínio/porta (e o código novo) são aplicados.
#
#   Sem domínio  -> HTTP em http://<ip-do-servidor>:<porta>   (rede local)
#   Com domínio  -> HTTPS automático em https://<dominio>     (usa as portas 80 e 443)
#
# Sem perguntas (automação):  DOMINIO=meu.dominio.com.br PORTA=8080 ./scripts/instalar.sh
set -euo pipefail
cd "$(dirname "$0")/.."

ENV_FILE=.env
PORTA_PADRAO=8080
ADMIN_EMAIL_PADRAO="admin@creche.local"

erro() { printf '\nERRO: %s\n' "$*" >&2; exit 1; }
aviso() { printf 'Aviso: %s\n' "$*" >&2; }

# valor atual de uma chave do .env (vazio se não existir)
valor() { [ -f "$ENV_FILE" ] && { grep -E "^$1=" "$ENV_FILE" | head -1 | cut -d= -f2- || true; } || true; }

# 24 caracteres hexadecimais aleatórios (sem símbolos que quebrem connection string ou compose)
aleatorio() {
  if command -v openssl >/dev/null 2>&1; then openssl rand -hex 12
  else head -c 12 /dev/urandom | od -An -tx1 | tr -d ' \n'; fi
}

# ---------------------------------------------------------------- pré-requisitos
command -v docker >/dev/null 2>&1 || erro "Docker não encontrado. Instale o Docker Engine (veja docs/DEPLOY.md)."
docker compose version >/dev/null 2>&1 || erro "O plugin 'docker compose' (v2) não foi encontrado."
[ "$(uname -m)" = "x86_64" ] || erro "CPU $(uname -m): a imagem do SQL Server só roda em x86-64."
if [ -r /proc/meminfo ]; then
  mem_kb="$(awk '/^MemTotal:/ {print $2}' /proc/meminfo)"
  [ "${mem_kb:-0}" -ge 3500000 ] || aviso "menos de 4 GB de RAM: o SQL Server pode ficar instável."
fi

# ---------------------------------------------------------------- domínio e porta (únicas perguntas)
DOM_ATUAL="$(valor DOMINIO)"
PORTA_ATUAL="$(valor PORTA)"; PORTA_ATUAL="${PORTA_ATUAL:-$PORTA_PADRAO}"

if [ -n "${DOMINIO+x}" ]; then
  DOM="$DOMINIO"
else
  printf 'Domínio (ex.: estoque.minhacreche.org.br). Deixe em branco para acessar por IP e porta.\n'
  [ -n "$DOM_ATUAL" ] && printf 'Atual: %s  (Enter mantém; digite - para remover)\n' "$DOM_ATUAL"
  read -r -p "Domínio: " resp || resp=""
  case "$resp" in "") DOM="$DOM_ATUAL" ;; "-") DOM="" ;; *) DOM="$resp" ;; esac
fi

if [ -n "${PORTA+x}" ]; then
  PORTA_ESC="$PORTA"
else
  if [ -n "$DOM" ]; then
    printf '\nCom domínio o HTTPS usa as portas 80 e 443; a porta abaixo só vale se você remover o domínio.\n'
  fi
  read -r -p "Porta [${PORTA_ATUAL}]: " resp || resp=""
  PORTA_ESC="${resp:-$PORTA_ATUAL}"
fi

DOM="$(printf '%s' "$DOM" | tr 'A-Z' 'a-z' | sed -e 's#^https\?://##' -e 's#/.*$##')"
if [ -n "$DOM" ] && ! printf '%s' "$DOM" | grep -Eq '^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$'; then
  erro "domínio inválido: '$DOM' (use algo como estoque.minhacreche.org.br)."
fi
printf '%s' "$PORTA_ESC" | grep -Eq '^[0-9]{2,5}$' && [ "$PORTA_ESC" -ge 1 ] && [ "$PORTA_ESC" -le 65535 ] \
  || erro "porta inválida: '$PORTA_ESC' (use um número entre 1 e 65535)."

# ---------------------------------------------------------------- segredos (gerados uma vez, mantidos depois)
SA="$(valor MSSQL_SA_PASSWORD)"; APP="$(valor APP_DB_PASSWORD)"; MAINT="$(valor MAINT_DB_PASSWORD)"
ADM="$(valor SEED_ADMIN_PASSWORD)"; ADM_EMAIL="$(valor SEED_ADMIN_EMAIL)"; ADM_EMAIL="${ADM_EMAIL:-$ADMIN_EMAIL_PADRAO}"
DEMO="${SEED_DEMO_DATA:-$(valor SEED_DEMO_DATA)}"
NOVA=0

if [ -z "$SA$APP$MAINT" ]; then
  # O sa só aceita a senha definida na criação do volume: se já existe banco e o .env sumiu, gerar outra quebraria tudo.
  if docker volume inspect estoque-creche_mssql_data >/dev/null 2>&1; then
    erro "já existe um banco desta instalação, mas o .env não foi encontrado. Restaure o .env original, ou apague o banco para recomeçar do zero (APAGA OS DADOS): docker compose down -v"
  fi
  SA="$(aleatorio)Aa1"; APP="$(aleatorio)Aa1"; MAINT="$(aleatorio)Aa1"
  NOVA=1
elif [ -z "$SA" ] || [ -z "$APP" ] || [ -z "$MAINT" ]; then
  erro "o .env existente está incompleto. Apague-o e rode o script de novo (só é seguro se ainda não houver banco)."
fi
[ -n "$ADM" ] || { ADM="Adm-$(aleatorio)"; NOVA=1; }

# ---------------------------------------------------------------- valores derivados
if [ -n "$DOM" ]; then PERFIL=https; SITE="$DOM"; COOKIE=Always
else PERFIL=http; SITE=":80"; COOKIE=SameAsRequest; fi

umask 077
{
  echo "# Gerado por scripts/instalar.sh. Não precisa editar: para mudar domínio ou porta, rode o script de novo."
  echo "# Contém senhas: não envie para o Git nem compartilhe."
  echo "DOMINIO=$DOM"
  echo "PORTA=$PORTA_ESC"
  echo "COMPOSE_PROFILES=$PERFIL"
  echo "SITE_ADDRESS=$SITE"
  echo "COOKIE_SECURE_POLICY=$COOKIE"
  echo "MSSQL_SA_PASSWORD=$SA"
  echo "APP_DB_PASSWORD=$APP"
  echo "MAINT_DB_PASSWORD=$MAINT"
  echo "SEED_ADMIN_EMAIL=$ADM_EMAIL"
  echo "SEED_ADMIN_PASSWORD=$ADM"
  [ -z "$DEMO" ] || echo "SEED_DEMO_DATA=$DEMO"
} > "$ENV_FILE.tmp"
mv "$ENV_FILE.tmp" "$ENV_FILE"
chmod 600 "$ENV_FILE" 2>/dev/null || true

# ---------------------------------------------------------------- subir
# Se o modo mudou (com/sem domínio), o proxy do outro modo precisa sair para liberar as portas.
docker compose --profile http --profile https rm -sf proxy-http proxy-https >/dev/null 2>&1 || true

printf '\nSubindo o sistema (a primeira vez demora alguns minutos: baixa imagens e cria o banco)...\n'
if ! docker compose up -d --build; then
  printf '\n'
  docker compose logs --tail 40 migrator 2>/dev/null || true
  erro "a instalação falhou. Veja as mensagens acima e rode: docker compose logs"
fi

printf 'Aguardando o sistema ficar pronto'
pronto=0
for _ in $(seq 1 60); do
  st="$(docker inspect --format '{{.State.Health.Status}}' "$(docker compose ps -q web)" 2>/dev/null || true)"
  if [ "$st" = "healthy" ]; then pronto=1; break; fi
  printf '.'; sleep 5
done
printf '\n'
[ "$pronto" = "1" ] || erro "o sistema não ficou pronto a tempo. Veja: docker compose logs web migrator"

if [ -n "$DOM" ]; then
  URL="https://$DOM"
else
  IP="$(hostname -I 2>/dev/null | awk '{print $1}' || true)"
  URL="http://${IP:-localhost}:$PORTA_ESC"
fi

printf '\n============================================================\n'
printf ' Sistema no ar:  %s\n' "$URL"
if [ "$NOVA" = "1" ]; then
  printf '\n Primeiro acesso (o sistema pede para trocar a senha):\n'
  printf '   Usuário: %s\n   Senha:   %s\n' "$ADM_EMAIL" "$ADM"
  printf '\n Anote a senha agora. Depois da troca, o valor guardado no .env deixa de valer.\n'
fi
if [ -n "$DOM" ]; then
  printf '\n O certificado HTTPS pode levar alguns minutos na primeira vez. Se não abrir:\n'
  printf '  confira se o DNS de %s aponta para este servidor e se as portas 80 e 443 estão liberadas.\n' "$DOM"
else
  printf '\n Acesso por HTTP: use só na rede local e com dados de teste. Para a creche (internet), use um domínio.\n'
fi
printf '============================================================\n'
