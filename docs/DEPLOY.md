# Deploy — servidor caseiro (ensaio) e VPS (teste da creche)

Runbook da T0.5. Ambientes e modos de acesso estão no `PLANO.md` (seção 6.4).

| Ambiente | Modo | Acesso | Dados | Profiles |
|---|---|---|---|---|
| Servidor caseiro (ensaio, 1º deploy) | **C** | `localhost`, túnel SSH ou IP da LAN, HTTP | fictícios | `tools` |
| VPS (teste da creche → produção) | **A** | `https://<dominio>` | fictícios no teste | `https` (ou `tools,https`) |

## 1. Requisitos do servidor

- Ubuntu 22.04/24.04 LTS ou Debian estável, **x86-64** (`uname -m` → `x86_64`). ARM (Raspberry Pi, VPS ARM) **não** roda o SQL Server.
- 2 vCPU, **4 GB de RAM** (≥ 3,5 GB livres em `free -h`), ≥ 40 GB SSD.
- Docker Engine + Compose v2 pelo **repositório oficial** (não Docker Desktop, não snap).

## 2. Preparação (uma vez)

### 2.1 Usuário `deploy` e chave SSH

```bash
sudo adduser --disabled-password --gecos "" deploy
sudo usermod -aG sudo,docker deploy        # o grupo docker existe após instalar o Docker
sudo mkdir -p /home/deploy/.ssh && sudo cp ~/.ssh/authorized_keys /home/deploy/.ssh/
sudo chown -R deploy:deploy /home/deploy/.ssh && sudo chmod 700 /home/deploy/.ssh && sudo chmod 600 /home/deploy/.ssh/authorized_keys
```

### 2.2 Pacotes, Docker, firewall, fail2ban, swap (script idempotente)

```bash
git clone <repo> estoque-creche && cd estoque-creche
sudo MODO=C ./scripts/server-bootstrap.sh     # servidor caseiro
# sudo MODO=A ./scripts/server-bootstrap.sh   # VPS (abre também 80/443)
```

### 2.3 SSH (manual, de propósito)

Edite `/etc/ssh/sshd_config` (ou crie `/etc/ssh/sshd_config.d/10-endurecer.conf`):

```
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
```

```bash
sudo sshd -t && sudo systemctl reload ssh
```

**Antes de fechar a sessão atual, abra outra e confirme o login por chave como `deploy`.**

### 2.4 Conferência (aceite da T0.5)

```bash
uname -m                      # x86_64
docker compose version        # v2.x
free -h                       # >= 3,5 GB
sudo ss -tlnp                 # modo C: 8080/9443 só em 127.0.0.1 (ou IP da LAN); modo A: 22/80/443
ssh -o PubkeyAuthentication=no deploy@<ip>   # deve ser recusado
```

> Portas publicadas pelo Docker **contornam o ufw**. Quem controla a exposição é o endereço de bind no `.env`
> (`WEB_BIND`, `PORTAINER_BIND`). O banco (1433) nunca é publicado no `compose.yaml`.

## 3. Servidor caseiro (modo C)

- Reserve o IP do servidor no DHCP do roteador.
- **Nenhum** redirecionamento de porta, DMZ ou UPnP no roteador para este servidor.
- Acesso de fora de casa: VPN (WireGuard/Tailscale) ou túnel SSH:
  `ssh -L 8080:localhost:8080 -L 9443:localhost:9443 deploy@<ip>`
- Para acessar de outro PC da LAN: `WEB_BIND=<ip-da-lan>` (só HTTP, só dados fictícios).

## 4. Primeira subida

```bash
cp .env.example .env && chmod 600 .env
nano .env                                   # troque TODAS as senhas; SEED_ADMIN_PASSWORD com 12+ caracteres
printf '%s' 'senha-forte-do-portainer-12+' > secrets/portainer_admin_password.txt && chmod 600 secrets/portainer_admin_password.txt
docker compose up -d --build
docker compose ps                           # sqlserver e web "healthy"; migrator "exited (0)"
curl -fsS http://127.0.0.1:8080/healthz
```

- O **migrator** cria o banco, aplica `db/migrations`, cria os logins `estoque_app`/`estoque_maint` e faz o seed.
- Primeiro login: `SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD`. O sistema obriga a trocar a senha.
- Portainer (profile `tools`): `https://localhost:9443` (aviso de certificado é esperado), usuário `admin`.
- Smoke test (apaga e recria o banco): `./scripts/smoke.sh`

> O arquivo `secrets/portainer_admin_password.txt` precisa existir mesmo com o profile `tools` desligado,
> porque o compose valida os secrets declarados. Se não for usar o Portainer, crie-o com qualquer valor.

## 5. VPS com domínio (modo A)

Pré-requisito: T0.6 (domínio e registro A apontando para a VPS; `dig +short estoque.<dominio>`).

No `.env`:

```
COMPOSE_PROFILES=https
SITE_ADDRESS=estoque.<dominio>
ACME_EMAIL=<email-real>
ACME_CA=https://acme-staging-v02.api.letsencrypt.org/directory   # primeiro o staging
COOKIE_SECURE_POLICY=Always
WEB_BIND=127.0.0.1
```

```bash
docker compose up -d --build
docker compose logs -f proxy        # confirme a emissão do certificado (staging)
# depois troque ACME_CA para o de produção e:
docker compose up -d proxy
SMOKE_URL_EXTERNA=https://estoque.<dominio> ./scripts/smoke.sh
```

Use **senhas novas** (diferentes do ensaio). Antes de qualquer dado real: backup com restauração testada,
retenção definida (Q13), revisão LGPD (S26) e recriar o banco do zero (`down -v`, `SEED_DEMO_DATA=false`).

## 6. Atualização e rollback

```bash
# 1) backup do banco (ver Fase 8; no MVP: docker exec + BACKUP DATABASE manual)
git pull
docker compose up -d --build        # o migrator roda de novo e aplica só migrações novas
curl -fsS http://127.0.0.1:8080/healthz
```

Rollback: `git checkout <tag-anterior> && docker compose up -d --build`; se houve migração de banco, restaurar o backup.

## 7. Reset completo (apaga TODOS os dados)

```bash
docker compose down -v
```

## 8. Domínio (T0.6) — preencher

| Campo | Valor |
|---|---|
| Domínio / subdomínio | |
| Titular (CNPJ da creche) | |
| Vencimento | |
| Contatos de aviso | |
| Onde está o DNS | |
