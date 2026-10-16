# Portainer no servidor (ver os containers do projeto)

Guia curto para instalar o Portainer no servidor e ver os containers do sistema Estoque & Doações
(`web`, `sqlserver`, proxy e `migrator`). É opcional e não faz parte da instalação normal.

**Contexto do projeto (para quem for ajudar):** o sistema roda com Docker Compose na pasta do repositório
(`estoque-creche`, clonada de https://github.com/JulioCmendestorres/estoque-creche.git), instalado com
`./scripts/instalar.sh`. O arquivo `compose.portainer.yaml` já existe no repositório. Servidor Linux, acesso por terminal.

Todos os comandos abaixo rodam **no servidor, dentro da pasta do projeto**.

## 1. Criar a senha do Portainer

Gera uma senha aleatória e a guarda em `secrets/portainer_admin_password.txt` (pasta ignorada pelo Git):

```bash
mkdir -p secrets && openssl rand -base64 18 | tr -d '\n' > secrets/portainer_admin_password.txt
```

Para ver a senha e anotá-la:

```bash
cat secrets/portainer_admin_password.txt
```

## 2. Subir o Portainer

Não mexe nos containers do sistema:

```bash
docker compose -f compose.yaml -f compose.portainer.yaml up -d
```

## 3. Acessar

- Usuário: `admin`
- Senha: a do passo 1

Por segurança a porta só abre dentro do próprio servidor (`127.0.0.1:9443`).

- **Pelo navegador do próprio servidor:** `https://localhost:9443`
- **De outro computador da rede:** abra um túnel SSH (troque o usuário e o IP) e deixe o terminal aberto:

```bash
ssh -L 9443:localhost:9443 usuario@IP-DO-SERVIDOR
```

Depois abra `https://localhost:9443` no navegador do seu computador.

O navegador avisa que o certificado não é confiável. É esperado (o certificado é do próprio Portainer); pode continuar.

## 4. Ver os containers

No Portainer, clique no ambiente **local** e depois em **Containers**. Dali dá para ver o estado, os logs e
reiniciar cada container.

## Parar o Portainer

```bash
docker compose -f compose.yaml -f compose.portainer.yaml stop portainer
```

## Se algo der errado

| Sintoma | O que fazer |
|---|---|
| `secrets/portainer_admin_password.txt` não encontrado | Refaça o passo 1 dentro da pasta do projeto. |
| Não abre em `https://localhost:9443` | Confira com `docker compose -f compose.yaml -f compose.portainer.yaml ps` se o `portainer` está rodando; veja `docker compose -f compose.yaml -f compose.portainer.yaml logs portainer`. |
| Não abre de outro computador | Esperado: a porta é só local. Use o túnel SSH do passo 3. |
| Esqueceu a senha | A senha do arquivo só vale na criação. Se já a trocou dentro do Portainer, refaça só o Portainer (não afeta o sistema): rode os 3 comandos da seção abaixo. |

### Refazer só o Portainer (senha perdida)

Remove o container e os dados do Portainer, sem tocar no sistema:

```bash
docker compose -f compose.yaml -f compose.portainer.yaml rm -sf portainer
```

```bash
docker volume ls | grep portainer
```

Apague o volume que aparecer (o nome termina em `portainer_data`) e suba de novo pelo passo 2:

```bash
docker volume rm NOME-DO-VOLUME
```

## Cuidados

- O Portainer acessa o Docker com poder de administrador do servidor, e quem entra nele enxerga as variáveis
  dos containers, inclusive as senhas do banco. Por isso a porta fica fechada para a rede: **não exponha**.
- Rodar `./scripts/instalar.sh` depois não derruba o Portainer; ele só mostra um aviso de container "órfão",
  que pode ser ignorado.
- Nunca rode `docker compose down -v`: o `-v` apaga o banco e todos os dados do sistema.
