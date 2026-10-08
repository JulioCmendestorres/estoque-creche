using System.Security.Claims;
using Estoque.Web.Data;
using Estoque.Web.Infrastructure;
using Estoque.Web.Infrastructure.Security;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.Entities;
using Estoque.Web.Models.ViewModels;
using Estoque.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.Controllers;

/// <summary>RF02–RF05: abas "Estoque Atual" e "Movimentações"; Nova Entrada / Nova Saída; cancelamento.</summary>
public class EstoqueController(EstoqueDbContext db, MovimentacaoService movimentacoes) : Controller
{
    public async Task<IActionResult> Index(string aba = "estoque", string? busca = null, [FromQuery] FiltroMovimentacoes? filtro = null)
        => View(await MontarAsync(aba, busca, filtro ?? new FiltroMovimentacoes()));

    [Authorize(Policy = Policies.PodeEditar), HttpPost]
    public async Task<IActionResult> NovaEntrada([Bind(Prefix = "Entrada")] EntradaInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await movimentacoes.RegistrarEntradaAsync(input);
            if (r.Ok)
            {
                TempData["Sucesso"] = "Entrada registrada.";
                return RedirectToAction(nameof(Index), new { aba = "movimentacoes" });
            }
            ModelState.AddModelError(string.Empty, r.Erro!);
        }
        return View(nameof(Index), await MontarAsync("estoque", null, new FiltroMovimentacoes(), entrada: input, modal: "entrada"));
    }

    [Authorize(Policy = Policies.PodeEditar), HttpPost]
    public async Task<IActionResult> NovaSaida([Bind(Prefix = "Saida")] SaidaInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await movimentacoes.RegistrarSaidaAsync(input);
            if (r.Ok)
            {
                TempData["Sucesso"] = "Saída registrada.";
                return RedirectToAction(nameof(Index), new { aba = "movimentacoes" });
            }
            ModelState.AddModelError(string.Empty, r.Erro!);
        }
        return View(nameof(Index), await MontarAsync("estoque", null, new FiltroMovimentacoes(), saida: input, modal: "saida"));
    }

    [Authorize(Policy = Policies.SomenteAdmin), HttpPost]
    public async Task<IActionResult> Cancelar(int id, string? motivo)
    {
        var r = await movimentacoes.CancelarAsync(id, motivo, User.Identity?.Name ?? "?");
        if (r.Ok) TempData["Sucesso"] = "Movimentação cancelada e estoque estornado.";
        else TempData["Erro"] = r.Erro;
        return RedirectToAction(nameof(Index), new { aba = "movimentacoes" });
    }

    private async Task<EstoqueIndexViewModel> MontarAsync(string aba, string? busca, FiltroMovimentacoes filtro,
        EntradaInput? entrada = null, SaidaInput? saida = null, string? modal = null)
    {
        var saldosQ = db.Estoques.Include(e => e.Item).ThenInclude(i => i!.Categoria).Where(e => e.Item!.Ativo);
        if (!string.IsNullOrWhiteSpace(busca))
            saldosQ = saldosQ.Where(e => e.Item!.Nome.Contains(busca.Trim()) || e.Item.CodigoInterno.Contains(busca.Trim()));
        var saldos = await saldosQ.OrderBy(e => e.Item!.Nome).ToListAsync();

        var movQ = db.Movimentacoes.Include(m => m.Item).Include(m => m.Responsavel).Include(m => m.Parceiro).AsQueryable();
        if (filtro.De is not null) movQ = movQ.Where(m => m.DataMovimentacao >= filtro.De);
        if (filtro.Ate is not null) movQ = movQ.Where(m => m.DataMovimentacao <= filtro.Ate);
        if (!string.IsNullOrEmpty(filtro.Tipo)) movQ = movQ.Where(m => m.TipoMovimentacao == filtro.Tipo);
        if (filtro.ItemId is > 0) movQ = movQ.Where(m => m.ItemId == filtro.ItemId);
        var movs = await movQ.OrderByDescending(m => m.DataMovimentacao).ThenByDescending(m => m.Id).Take(500).ToListAsync();

        var hoje = Tempo.Hoje;
        return new EstoqueIndexViewModel
        {
            Aba = aba == "movimentacoes" ? "movimentacoes" : "estoque",
            Busca = busca,
            Saldos = saldos,
            Movimentacoes = movs,
            Filtro = filtro,
            ItensAtivos = await db.Itens.Where(i => i.Ativo).OrderBy(i => i.Nome).ToListAsync(),
            SaldoPorItem = await db.Estoques.ToDictionaryAsync(e => e.ItemId, e => e.QuantidadeAtual),
            Parceiros = await db.Parceiros.Where(p => p.Ativo).OrderBy(p => p.Nome).ToListAsync(),
            Entrada = entrada ?? new EntradaInput { Data = hoje },
            Saida = saida ?? new SaidaInput { Data = hoje },
            ModalAberto = modal,
            NomeResponsavel = User.Identity?.Name ?? "",
            NomeUnidade = User.FindFirstValue(ClaimsApp.UnidadeNome) ?? "",
        };
    }
}
