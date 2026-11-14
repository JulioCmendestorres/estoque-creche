// Painel: gráfico de colunas "Entradas e saídas por mês" (Chart.js local).
// Os outros gráficos da página são HTML/CSS puros e não dependem deste arquivo.
document.addEventListener("DOMContentLoaded", () => {
    const tela = document.getElementById("graficoMeses");
    const bruto = document.getElementById("dadosMeses");
    if (!tela || !bruto || !window.Chart) return;

    const dados = JSON.parse(bruto.textContent);
    const css = getComputedStyle(document.documentElement);
    const cor = nome => css.getPropertyValue(nome).trim();
    const moeda = new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" });
    const curto = new Intl.NumberFormat("pt-BR", { notation: "compact", maximumFractionDigits: 1 });

    Chart.defaults.font.family = css.getPropertyValue("--bs-font-sans-serif").trim();
    Chart.defaults.font.size = 13;
    Chart.defaults.color = cor("--apagado");

    const serie = (rotulo, valores, tom) => ({
        label: rotulo,
        data: valores,
        backgroundColor: tom,
        hoverBackgroundColor: tom,
        borderRadius: { topLeft: 4, topRight: 4 },   // só a ponta do dado; a base fica reta no eixo
        borderSkipped: "bottom",
        maxBarThickness: 18,
    });

    new Chart(tela, {
        type: "bar",
        data: {
            labels: dados.rotulos,
            datasets: [
                serie("Entradas", dados.entradas, cor("--grafico-entrada")),
                serie("Saídas", dados.saidas, cor("--grafico-saida")),
            ],
        },
        options: {
            maintainAspectRatio: false,
            animation: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? false : { duration: 300 },
            interaction: { mode: "index", intersect: false },   // passa o mouse no mês inteiro, não só na coluna
            datasets: { bar: { categoryPercentage: 0.62, barPercentage: 0.9 } },
            scales: {
                x: { grid: { display: false }, border: { color: cor("--borda-forte") }, ticks: { maxRotation: 0, autoSkipPadding: 8 } },
                y: {
                    beginAtZero: true,
                    border: { display: false },
                    grid: { color: cor("--borda") },
                    ticks: { maxTicksLimit: 5, callback: v => "R$ " + curto.format(v) },
                },
            },
            plugins: {
                legend: { display: false },   // a legenda é HTML, no cabeçalho do cartão
                tooltip: {
                    backgroundColor: cor("--tinta"),
                    padding: 10,
                    cornerRadius: 6,
                    boxPadding: 4,
                    usePointStyle: true,
                    callbacks: { label: c => ` ${c.dataset.label}: ${moeda.format(c.parsed.y)}` },
                },
            },
        },
    });
});
