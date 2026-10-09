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
        fechar();
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
            if (modo !== "filtro") {
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
            const resp = await fetch(box.dataset.url + "?q=" + encodeURIComponent(txt.value.trim()),
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
            if (!hid.value) {
                ev.preventDefault();
                txt.classList.add("is-invalid");
                txt.focus();
            }
        });
    }
}

document.addEventListener("DOMContentLoaded", () => {
    document.querySelectorAll("[data-busca-item]").forEach(iniciarBuscaItem);

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
