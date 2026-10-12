# Instalar no servidor

Funciona em qualquer servidor Linux **x86-64** (Ubuntu/Debian) com **Docker** e **4 GB de RAM**.
A única coisa que você informa é o **domínio** e a **porta**. Senhas e o administrador inicial são gerados sozinhos.

## 1. Instalar

```bash
git clone git@github.com:JulioCmendestorres/estoque-creche.git
cd estoque-creche
./scripts/instalar.sh
```

O script pergunta:

| Pergunta | O que responder |
|---|---|
| **Domínio** | Em branco para acessar por IP. Com domínio (ex.: `estoque.minhacreche.org.br`) o HTTPS é ligado sozinho. |
| **Porta** | Porta do sistema quando **não** há domínio (padrão `8080`). Com domínio, o HTTPS usa as portas 80 e 443. |

No final ele mostra o endereço e a **senha do primeiro acesso**. Anote: o usuário é `admin@creche.local` e o sistema pede para trocar a senha ao entrar.

## 2. Acessar

- **Sem domínio:** `http://IP-DO-SERVIDOR:PORTA`. É HTTP simples: use só na rede local e com dados de teste.
- **Com domínio:** `https://SEU-DOMINIO`. Antes de rodar o script, o DNS do domínio precisa apontar para o servidor e as portas **80 e 443** precisam estar liberadas. O certificado leva alguns minutos na primeira vez.

## 3. Mudar domínio ou porta depois

Rode `./scripts/instalar.sh` de novo. As senhas e os dados são mantidos.

## 4. Atualizar para uma versão nova

```bash
git pull
./scripts/instalar.sh
```

Aperte Enter nas duas perguntas para manter o que já estava configurado.

## Se algo der errado

| Sintoma | O que fazer |
|---|---|
| A instalação falha | `docker compose logs migrator web` |
| "port is already allocated" | Já há algo usando a porta. Rode o script de novo com outra porta, ou libere as portas 80/443. |
| Domínio não abre | Confira o DNS e as portas 80/443; veja `docker compose logs proxy-https`. |
| "já existe um banco, mas o .env não foi encontrado" | Restaure o `.env` original. Para recomeçar **apagando todos os dados**: `docker compose down -v`. |
| Esqueceu a senha do administrador | Ainda não há tela de recuperação (versão completa). Fale com quem mantém o sistema. |

## Extras (opcionais)

- **Preparar um servidor novo** (Docker, firewall, fail2ban, atualizações automáticas): `sudo MODO=A ./scripts/server-bootstrap.sh` (VPS com domínio) ou `MODO=C` (rede local). No final, o script mostra como desativar o login do SSH por senha.
- **Portainer** (painel dos containers): instruções no topo de `compose.portainer.yaml`.
- **Testar tudo automaticamente** (apaga o banco): `./scripts/smoke.sh`.
- **Apagar tudo e recomeçar**: `docker compose down -v` (apaga os dados).

## Antes de usar dados reais

O backup automático ainda não existe (versão completa). Combine como será feito o backup e a restauração antes de cadastrar fornecedores e beneficiários de verdade.
