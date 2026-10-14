# Instalação do sistema (servidor de teste)

Este guia serve para instalar o sistema em um servidor Linux. Não é preciso ser especialista: siga os passos na ordem.

## O que você precisa

- Um computador ou servidor com **Ubuntu 22.04 / 24.04** ou **Debian 12**, de **64 bits** (x86-64).
- **4 GB de RAM** e **20 GB de espaço livre**.
- Acesso de administrador (usuário com `sudo`).
- Conexão com a internet.
- Para usar **domínio** (acesso pela internet com HTTPS): o domínio já apontando para o servidor e as portas **80** e **443** liberadas no roteador ou no provedor.

Para confirmar o tipo de processador, rode `uname -m`. A resposta tem que ser `x86_64`. Raspberry Pi e outras placas ARM não servem.

## 1. Instalar o Docker

Primeiro veja se ele já está instalado:

```bash
docker compose version
```

Se aparecer a versão (v2.x), pule para a seção 2. Se aparecer erro, instale com os comandos abaixo.

### Ubuntu

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl git
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo systemctl enable --now docker
sudo usermod -aG docker "$USER"
```

### Debian

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl git
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo systemctl enable --now docker
sudo usermod -aG docker "$USER"
```

> **Importante:** não instale o Docker pelo `snap` nem pelo pacote `docker.io` do Ubuntu. Use só os comandos acima.

Depois de instalar, **saia da sessão e entre de novo** (ou feche o terminal e abra outro). Isso faz valer a permissão do grupo `docker`.

Teste:

```bash
docker run --rm hello-world
```

Se aparecer a mensagem "Hello from Docker!", está pronto.

> Quem está no grupo `docker` tem poder de administrador no servidor. Adicione só pessoas de confiança.

## 2. Baixar o sistema

Não é preciso chave nem senha: o repositório é público.

```bash
git clone https://github.com/JulioCmendestorres/estoque-creche.git
```

Entre na pasta:

```bash
cd estoque-creche
```

## 3. Instalar

```bash
./scripts/instalar.sh
```

O script faz duas perguntas:

| Pergunta | O que responder |
|---|---|
| **Domínio** | Para acessar pela internet, o domínio (ex.: `estoque.minhacreche.org.br`). Para acessar só pela rede local, deixe **em branco** e pressione Enter. |
| **Porta** | Só importa sem domínio. Pressione Enter para usar a `8080`. |

O resto é automático: o script cria as senhas do banco, o administrador inicial e o HTTPS (quando há domínio). A primeira instalação leva de 5 a 15 minutos, porque baixa as imagens e cria o banco.

No final aparecem o endereço do sistema e a **senha do primeiro acesso**. **Anote essa senha agora.** Ela aparece só nesse momento.

- Usuário: `admin@creche.local`
- Senha: a que apareceu na tela

Ao entrar pela primeira vez, o sistema pede para trocar a senha.

## 4. Acessar

- **Sem domínio:** `http://IP-DO-SERVIDOR:PORTA`. Por exemplo, `http://192.168.0.10:8080`. Use só dentro da rede local, com dados de teste.
- **Com domínio:** `https://SEU-DOMINIO`. O certificado é emitido sozinho na primeira vez. Pode levar alguns minutos. Se não abrir, confira:
  - se o DNS do domínio aponta para o IP do servidor;
  - se as portas 80 e 443 estão liberadas.

Para conferir a saúde do sistema:

```bash
curl -fsS http://localhost:8080/healthz
```

Sem domínio, troque `8080` pela porta escolhida. Com domínio, troque `http://localhost:8080` por `https://SEU-DOMINIO`. A resposta esperada é `Healthy`. Qualquer outra coisa indica problema.

## 5. Mudar domínio ou porta depois

Rode o script de novo:

```bash
./scripts/instalar.sh
```

As senhas e os dados são mantidos. Só o endereço muda.

## 6. Atualizar para uma versão nova

```bash
git pull
./scripts/instalar.sh
```

Pressione Enter nas duas perguntas para manter a configuração atual.

## Se algo der errado

| Sintoma | O que fazer |
|---|---|
| `permission denied` ao usar o `docker` | Saia da sessão e entre de novo. Se persistir, rode `sudo usermod -aG docker $USER` e reinicie. |
| `port is already allocated` | Outro programa usa a porta. Rode o script de novo e escolha outra porta. |
| O script termina com "a instalação falhou" | Rode `docker compose logs migrator web` e envie a saída para quem mantém o sistema. |
| Domínio não abre | Confira o DNS e as portas 80/443. Veja `docker compose logs proxy-https`. |
| "já existe um banco desta instalação, mas o .env não foi encontrado" | Não apague nada. Procure o arquivo `.env` original (ele guarda as senhas). |
| Esqueceu a senha do administrador | Ainda não há recuperação de senha nesta versão. Fale com quem mantém o sistema. |

Para ver todos os containers:

```bash
docker compose ps
```

## Para recomeçar do zero (apaga todos os dados)

Só faça isso se tiver certeza:

```bash
docker compose down -v
```

Depois rode `./scripts/instalar.sh` de novo.

## Extras (opcionais)

- **Endurecer o servidor** (firewall, fail2ban, atualizações automáticas): `sudo MODO=C ./scripts/server-bootstrap.sh` (rede local) ou `sudo MODO=A ./scripts/server-bootstrap.sh` (servidor com domínio). O script mostra no final como desativar o login do SSH por senha.
- **Painel visual dos containers (Portainer):** veja o topo do arquivo `compose.portainer.yaml`.
- **Teste automático** (apaga o banco e testa o fluxo inteiro): `./scripts/smoke.sh`.

## Antes de usar dados reais

Esta versão ainda não tem backup automático. Antes de cadastrar fornecedores e beneficiários de verdade, combine como será feito o backup e a restauração.
