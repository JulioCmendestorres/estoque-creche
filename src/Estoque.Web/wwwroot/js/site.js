// Comportamentos simples de formulário, sem dependências além do Bootstrap.

/**
 * Busca de item com autocomplete (substitui o dropdown com todos os itens).
 * Digita nome/código → lista com até 20 resultados vindos de /Itens/Buscar → escolhe com clique
 * ou setas + Enter. O id escolhido fica no input oculto; digitar de novo invalida a escolha.
 * Todo texto vindo do servidor entra por textContent (nunca innerHTML).
 */
function iniciarBuscaItem(box) {
    const txt = box.querySelector('input[type="text"]');
    const hid = box.querySelector('input[type="hidden"]');
    const lista = box.querySelector(".busca-item-lista");
    const modo = box.dataset.modo;
    const obrigatorio = box.dataset.obrigatorio === "true";
    const alvo = box.dataset.unidadeAlvo ? document.getElementById(box.dataset.unidadeAlvo) : null;
    let itens = [], ativo = -1, timer = 0, seq = 0;

    const definirAlvo = valor => { if (alvo) alvo.value = valor || ""; };

    // Preço estimado do item preenche o valor, mas o campo continua editável. Se a pessoa já digitou
    // outro valor, uma nova escolha de item não o sobrescreve (data-auto marca o que foi preenchido aqui).
    const valorAlvo = box.dataset.valorAlvo ? document.getElementById(box.dataset.valorAlvo) : null;
    if (valorAlvo) valorAlvo.addEventListener("input", () => { valorAlvo.dataset.auto = "0"; });
    const preencherValor = preco => {
        if (!valorAlvo) return;
        if (valorAlvo.value.trim() !== "" && valorAlvo.dataset.auto !== "1") return;
        valorAlvo.value = preco == null ? "" : Number(preco).toLocaleString("pt-BR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
        valorAlvo.dataset.auto = "1";
    };

    function fechar() {
        lista.hidden = true;
        txt.setAttribute("aria-expanded", "false");
        ativo = -1;
    }

    function mensagem(texto) {
        const d = document.createElement("div");
        d.className = "busca-item-vazio";
        d.textContent = texto;
        lista.replaceChildren(d);
        lista.hidden = false;
        txt.setAttribute("aria-expanded", "true");
    }

    function escolher(it) {
        hid.value = it.id;
        txt.value = it.nome;
        txt.classList.remove("is-invalid");
        definirAlvo(it.unidadeMedida);
        preencherValor(it.precoEstimado);
        fechar();
        box.dispatchEvent(new CustomEvent("item-escolhido", { bubbles: true }));
    }

    function marcar(i) {
        const botoes = [...lista.querySelectorAll("button:not([disabled])")];
        if (!botoes.length) return;
        ativo = (i + botoes.length) % botoes.length;
        botoes.forEach((b, n) => b.classList.toggle("ativo", n === ativo));
        botoes[ativo].scrollIntoView({ block: "nearest" });
    }

    function desenhar() {
        if (!itens.length) { mensagem("Nenhum item encontrado."); return; }
        lista.replaceChildren(...itens.map(it => {
            const b = document.createElement("button");
            b.type = "button";
            b.className = "list-group-item list-group-item-action d-flex justify-content-between align-items-center gap-3";
            b.setAttribute("role", "option");
            const esq = document.createElement("span");
            const nome = document.createElement("span");
            nome.textContent = it.nome;
            const cod = document.createElement("small");
            cod.className = "text-muted d-block";
            cod.textContent = it.codigoInterno;
            esq.append(nome, cod);
            b.append(esq);
            if (modo === "entrada" || modo === "saida") {
                const saldo = document.createElement("small");
                saldo.className = it.saldo > 0 ? "text-nowrap" : "text-nowrap text-danger";
                saldo.textContent = "saldo " + it.saldo + " " + it.unidadeMedida;
                b.append(saldo);
                if (modo === "saida" && it.saldo <= 0) { b.disabled = true; b.title = "Sem saldo nesta unidade"; }
            }
            b.addEventListener("mousedown", ev => ev.preventDefault()); // não tira o foco do campo
            b.addEventListener("click", () => escolher(it));
            return b;
        }));
        lista.hidden = false;
        txt.setAttribute("aria-expanded", "true");
        ativo = -1;
    }

    async function buscar() {
        const minha = ++seq;
        try {
            const resp = await fetch(box.dataset.url + "?q=" + encodeURIComponent(txt.value.trim()) + (modo === "relatorio" || modo === "filtro" ? "&inativos=true" : ""),
                { credentials: "same-origin", headers: { Accept: "application/json" } });
            if (!resp.ok || !(resp.headers.get("content-type") || "").includes("json")) throw new Error("resposta inesperada");
            const dados = await resp.json();
            if (minha !== seq) return; // resposta antiga: já digitou outra coisa
            itens = dados;
            desenhar();
        } catch {
            if (minha === seq) mensagem("Não foi possível buscar. Recarregue a página e tente de novo.");
        }
    }

    txt.addEventListener("input", () => {
        hid.value = "";          // digitou de novo: a escolha anterior deixa de valer
        definirAlvo("");
        txt.classList.remove("is-invalid");
        clearTimeout(timer);
        timer = setTimeout(buscar, 200);
    });
    txt.addEventListener("focus", () => { if (!hid.value) buscar(); });
    txt.addEventListener("blur", () => setTimeout(fechar, 120));
    txt.addEventListener("keydown", ev => {
        if (ev.key === "ArrowDown") { ev.preventDefault(); if (lista.hidden) buscar(); else marcar(ativo + 1); }
        else if (ev.key === "ArrowUp") { ev.preventDefault(); marcar(ativo - 1); }
        else if (ev.key === "Enter" && !lista.hidden) {
            ev.preventDefault();
            const botoes = [...lista.querySelectorAll("button:not([disabled])")];
            const alvoBtn = botoes[ativo >= 0 ? ativo : 0];
            if (alvoBtn) alvoBtn.click();
        }
        else if (ev.key === "Escape" && !lista.hidden) { ev.stopPropagation(); fechar(); }
    });

    // Só envia se um item foi realmente escolhido na lista (obrigatório nas movimentações)
    const form = box.closest("form");
    if (form && obrigatorio) {
        form.addEventListener("submit", ev => {
            if (!box.isConnected) return; // linha removida do formulário
            if (!hid.value) {
                ev.preventDefault();
                txt.classList.add("is-invalid");
                txt.focus();
            }
        });
    }
}

/**
 * Entrada com vários itens: as linhas saem de um <template> com "__i__" no lugar do índice.
 * Índices nunca são reaproveitados; o campo Entrada.Linhas.Index de cada linha diz ao servidor quais existem.
 */
function iniciarLinhasEntrada(area) {
    const molde = area.querySelector("template");
    const lista = area.querySelector("[data-linhas]");
    const contador = area.querySelector("[data-contador]");
    const btnAdicionar = area.querySelector("[data-adicionar]");
    const maximo = Number(area.dataset.max) || 50;
    let proximo = lista.children.length;

    const linhas = () => [...lista.querySelectorAll("[data-entrada-linha]")];
    const vazia = linha => !linha.querySelector('input[type="hidden"][name$=".ItemId"]').value
        && !linha.querySelector('.busca-item input[type="text"]').value.trim();

    function atualizar() {
        const n = linhas().length;
        contador.textContent = n === 1 ? "1 item" : n + " itens";
        lista.querySelectorAll("[data-remover]").forEach(b => { b.hidden = n <= 1; });
        btnAdicionar.disabled = n >= maximo;
    }

    function adicionar() {
        if (linhas().length >= maximo) return;
        lista.insertAdjacentHTML("beforeend", molde.innerHTML.replaceAll("__i__", String(proximo++)));
        const nova = lista.lastElementChild;
        iniciarBuscaItem(nova.querySelector("[data-busca-item]"));
        atualizar();
        nova.querySelector('.busca-item input[type="text"]').focus();
    }

    btnAdicionar.addEventListener("click", adicionar);

    lista.addEventListener("click", ev => {
        const btn = ev.target.closest("[data-remover]");
        if (!btn) return;
        btn.closest("[data-entrada-linha]").remove();
        atualizar();
        btnAdicionar.focus();
    });

    // Escolheu o item: segue direto para a quantidade
    lista.addEventListener("item-escolhido", ev => {
        const qtd = ev.target.closest("[data-entrada-linha]").querySelector("[data-linha-quantidade]");
        qtd.focus();
        qtd.select();
    });

    // Enter na quantidade ou no valor abre a próxima linha em vez de enviar a entrada pela metade
    lista.addEventListener("keydown", ev => {
        if (ev.key !== "Enter" || ev.target.closest(".busca-item")) return;
        ev.preventDefault();
        const ultima = linhas().at(-1);
        if (vazia(ultima)) ultima.querySelector('.busca-item input[type="text"]').focus();
        else adicionar();
    });

    // Linhas em branco que sobraram não vão para o servidor (fase de captura: antes da validação de cada busca)
    area.closest("form").addEventListener("submit", () => {
        linhas().forEach(l => { if (vazia(l) && linhas().length > 1) l.remove(); });
        atualizar();
    }, true);

    atualizar();
}

document.addEventListener("DOMContentLoaded", () => {
    document.querySelectorAll("[data-busca-item]").forEach(iniciarBuscaItem);
    document.querySelectorAll("[data-entrada-linhas]").forEach(iniciarLinhasEntrada);

    // Mostra/oculta um bloco conforme o valor de um select: data-mostrar-quando="idDoSelect=valor1|valor2"
    document.querySelectorAll("[data-mostrar-quando]").forEach(bloco => {
        const [selId, valores] = bloco.dataset.mostrarQuando.split("=");
        const sel = document.getElementById(selId);
        if (!sel) return;
        const lista = valores.split("|");
        const atualizar = () => {
            const visivel = lista.includes(sel.value);
            bloco.hidden = !visivel;
            bloco.querySelectorAll("[data-obrigatorio-se-visivel]").forEach(i => { i.required = visivel; });
        };
        sel.addEventListener("change", atualizar);
        atualizar();
    });

    // Campo obrigatório condicional: data-obrigatorio-quando="idDoSelect=valor"
    document.querySelectorAll("[data-obrigatorio-quando]").forEach(campo => {
        const [selId, valores] = campo.dataset.obrigatorioQuando.split("=");
        const sel = document.getElementById(selId);
        if (!sel) return;
        const lista = valores.split("|");
        const rotulo = document.querySelector(`label[for="${campo.id}"] .obrigatorio`);
        const atualizar = () => {
            const obrig = lista.includes(sel.value);
            campo.required = obrig;
            if (rotulo) rotulo.hidden = !obrig;
        };
        sel.addEventListener("change", atualizar);
        atualizar();
    });

    // Reabre o modal indicado pelo servidor (reexibição com erro de validação)
    const abrir = document.body.dataset.abrirModal;
    if (abrir && window.bootstrap) {
        const el = document.getElementById(abrir);
        if (el) bootstrap.Modal.getOrCreateInstance(el).show();
    }

    // Formulários com data-confirmar="mensagem" pedem confirmação antes de enviar (vazio = não pergunta)
    document.querySelectorAll("form[data-confirmar]").forEach(f => f.addEventListener("submit", ev => {
        const msg = f.dataset.confirmar;
        if (msg && !window.confirm(msg)) ev.preventDefault();
    }));

    // Botão de copiar: data-copiar="id do elemento com o texto"
    document.querySelectorAll("[data-copiar]").forEach(b => b.addEventListener("click", async () => {
        const alvo = document.getElementById(b.dataset.copiar);
        try {
            await navigator.clipboard.writeText(alvo.textContent.trim());
            b.innerHTML = '<i class="bi bi-check2"></i>';
        } catch { /* sem permissão de área de transferência: o texto continua visível */ }
    }));

    // Relatórios: imprimir usa o CSS @media print (esconde menu e filtros)
    document.querySelectorAll("[data-imprimir]").forEach(b => b.addEventListener("click", () => window.print()));

    // Modal de cancelamento: recebe id e número da movimentação do botão que o abriu
    const modalCancelar = document.getElementById("modalCancelar");
    if (modalCancelar) {
        modalCancelar.addEventListener("show.bs.modal", ev => {
            const btn = ev.relatedTarget;
            modalCancelar.querySelector("input[name=id]").value = btn.dataset.id;
            modalCancelar.querySelector("[data-numero]").textContent = btn.dataset.numero;
        });
    }
});
