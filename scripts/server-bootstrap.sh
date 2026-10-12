#!/usr/bin/env bash
# T0.5 — Preparação idempotente do servidor (Ubuntu 22.04/24.04 ou Debian estável, x86-64).
# Instala Docker Engine + Compose v2 pelo repositório OFICIAL (não snap), ufw, fail2ban,
# unattended-upgrades, swap e sincronização de relógio.
#
# NÃO altera o sshd_config (passo manual, mostrado no final, para não bloquear seu acesso).
#
# Uso: sudo MODO=C ./scripts/server-bootstrap.sh     # servidor caseiro (só 22 no ufw)
#      sudo MODO=A ./scripts/server-bootstrap.sh     # VPS com domínio (22, 80, 443)
set -euo pipefail

MODO="${MODO:-C}"
SWAP_GB="${SWAP_GB:-2}"

[ "$(id -u)" -eq 0 ] || { echo "Execute com sudo."; exit 1; }
[ "$(uname -m)" = "x86_64" ] || { echo "CPU $(uname -m): a imagem do SQL Server exige x86_64."; exit 1; }

. /etc/os-release
case "$ID" in ubuntu|debian) ;; *) echo "SO não suportado: $ID"; exit 1 ;; esac

echo "==> Pacotes base"
apt-get update -y
apt-get install -y ca-certificates curl gnupg ufw fail2ban unattended-upgrades

echo "==> Docker (repositório oficial)"
if snap list docker >/dev/null 2>&1; then
  echo "Docker via snap detectado. Remova com 'snap remove docker' antes de continuar."; exit 1
fi
if ! command -v docker >/dev/null 2>&1; then
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL "https://download.docker.com/linux/$ID/gpg" -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/$ID $VERSION_CODENAME stable" \
    > /etc/apt/sources.list.d/docker.list
  apt-get update -y
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
fi
systemctl enable --now docker

echo "==> Firewall (ufw) — lembrete: portas publicadas pelo Docker contornam o ufw"
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp
if [ "$MODO" = "A" ]; then ufw allow 80/tcp; ufw allow 443/tcp; ufw allow 443/udp; fi
ufw --force enable

echo "==> fail2ban (sshd) e atualizações automáticas de segurança"
systemctl enable --now fail2ban
dpkg-reconfigure -f noninteractive unattended-upgrades

echo "==> Relógio sincronizado e fuso UTC"
timedatectl set-timezone UTC
timedatectl set-ntp true || true

echo "==> Swap de ${SWAP_GB} GB (rede de segurança)"
if ! swapon --show | grep -q /swapfile; then
  fallocate -l "${SWAP_GB}G" /swapfile
  chmod 600 /swapfile
  mkswap /swapfile
  swapon /swapfile
  grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi

echo
echo "Concluído. Conferência:"
uname -m
docker compose version
free -h
ufw status verbose | head -20
echo
cat <<'FIM'
PRÓXIMO PASSO MANUAL (recomendado): endurecer o SSH.
  1. Confirme que você consegue entrar por CHAVE SSH (abra outra sessão e teste).
  2. Crie /etc/ssh/sshd_config.d/10-endurecer.conf com:
         PermitRootLogin no
         PasswordAuthentication no
         KbdInteractiveAuthentication no
  3. sudo sshd -t && sudo systemctl reload ssh
  4. Teste de novo o login por chave ANTES de fechar a sessão atual.
FIM
