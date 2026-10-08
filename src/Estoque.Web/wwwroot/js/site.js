// Comportamentos simples de formulário, sem dependências além do Bootstrap.
document.addEventListener("DOMContentLoaded", () => {
    // Unidade de medida vem do item selecionado (somente leitura)
    document.querySelectorAll("select[data-unidade-alvo]").forEach(sel => {
        const alvo = document.getElementById(sel.dataset.unidadeAlvo);
        const atualizar = () => {
            const opt = sel.selectedOptions[0];
            if (alvo) alvo.value = opt ? (opt.dataset.unidade || "") : "";
        };
        sel.addEventListener("change", atualizar);
        atualizar();
    });

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
