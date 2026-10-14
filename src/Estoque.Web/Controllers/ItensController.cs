using Estoque.Web.Data;
using Estoque.Web.Infrastructure.Security;
using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.Entities;
using Estoque.Web.Models.ViewModels;
using Estoque.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.Controllers;

/// <summary>RF01 — catálogo de itens do tenant; quantidade mínima é por unidade.</summary>
public class ItensController(EstoqueDbContext db, TenantContext tenant) : Controller
{
    public async Task<IActionResult> Index(string? busca, int? categoriaId, bool inativos = false)
    {
        var q = db.Itens.Include(i => i.Categoria).AsQueryable();
        if (!inativos) q = q.Where(i => i.Ativo);
        if (!string.IsNullOrWhiteSpace(busca))
        {
            var b = busca.Trim();
            // collation CI_AI: "acucar" encontra "Açúcar"
            q = q.Where(i => i.Nome.Contains(b) || i.CodigoInterno.Contains(b));
        }
        if (categoriaId is > 0) q = q.Where(i => i.CategoriaId == categoriaId);

        var itens = await q.OrderBy(i => i.Nome).ToListAsync();
        var ids = itens.Select(i => i.Id).ToList();
        var saldos = await db.Estoques.Where(e => ids.Contains(e.ItemId)).ToDictionaryAsync(e => e.ItemId);

        return View(new ItensIndexViewModel
        {
            Busca = busca,
            CategoriaId = categoriaId,
            MostrarInativos = inativos,
            Categorias = await db.Categorias.Where(c => c.Ativo).OrderBy(c => c.Nome).ToListAsync(),
            Itens = itens.Select(i => saldos.TryGetValue(i.Id, out var s)
                ? new ItemLinha { Item = i, Saldo = s.QuantidadeAtual, Minimo = s.QuantidadeMinima, Alerta = s.AbaixoDoMinimo }
                : new ItemLinha { Item = i }).ToList(),
        });
    }

    /// <summary>
    /// Busca incremental para os formulários de movimentação: todos os termos digitados precisam
    /// aparecer no nome ou no código (sem distinguir acento/maiúscula). Devolve no máximo 20 itens
    /// ativos do tenant, com o saldo da unidade atual. O filtro de tenant vem do DbContext.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Buscar(string? q, bool inativos = false)
    {
        var texto = (q ?? "").Trim();
        if (texto.Length > 100) texto = texto[..100];
        var termos = texto.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(5).ToList();

        // Relatórios também procuram itens inativados (que ainda têm histórico)
        var query = inativos ? db.Itens : db.Itens.Where(i => i.Ativo);
        foreach (var termo in termos)
            query = query.Where(i => i.Nome.Contains(termo) || i.CodigoInterno.Contains(termo));

        var itens = await query
            .OrderBy(i => i.Nome)
            .Take(20)
            .Select(i => new
            {
                i.Id,
                i.Nome,
                i.CodigoInterno,
                i.UnidadeMedida,
                i.PrecoEstimado,
                Saldo = db.Estoques.Where(e => e.ItemId == i.Id).Select(e => (int?)e.QuantidadeAtual).FirstOrDefault() ?? 0,
            })
            .ToListAsync();
        return Json(itens);
    }

    [Permissao(Permissoes.CadastroItens)]
    public async Task<IActionResult> Criar() => View("Form", await MontarForm(new ItemInput()));

    [Permissao(Permissoes.CadastroItens), HttpPost]
    public async Task<IActionResult> Criar(ItemInput input)
    {
        await ValidarReferencias(input);
        if (!ModelState.IsValid) return View("Form", await MontarForm(input));

        var item = new Item();
        Aplicar(item, input);
        item.CodigoInterno = string.IsNullOrWhiteSpace(input.CodigoInterno)
            ? await GerarCodigoAsync()
            : input.CodigoInterno.Trim().ToUpperInvariant();
        db.Itens.Add(item);
        if (!await Salvar(input)) return View("Form", await MontarForm(input));

        await DefinirMinimoAsync(item.Id, input.QuantidadeMinima);
        TempData["Sucesso"] = $"Item \"{item.Nome}\" cadastrado.";
        return RedirectToAction(nameof(Index));
    }

    [Permissao(Permissoes.CadastroItens)]
    public async Task<IActionResult> Editar(int id)
    {
        var item = await db.Itens.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound();
        var minimo = await db.Estoques.Where(e => e.ItemId == id).Select(e => e.QuantidadeMinima).FirstOrDefaultAsync();
        return View("Form", await MontarForm(new ItemInput
        {
            Id = item.Id,
            CodigoInterno = item.CodigoInterno,
            Nome = item.Nome,
            CategoriaId = item.CategoriaId,
            UnidadeMedida = item.UnidadeMedida,
            PrecoEstimado = item.PrecoEstimado,
            OrigemPadrao = item.OrigemPadrao,
            Descricao = item.Descricao,
            QuantidadeMinima = minimo,
        }));
    }

    [Permissao(Permissoes.CadastroItens), HttpPost]
    public async Task<IActionResult> Editar(int id, ItemInput input)
    {
        var item = await db.Itens.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound();
        input.Id = id;
        await ValidarReferencias(input, unidadeAtualDoItem: item.UnidadeMedida);
        if (string.IsNullOrWhiteSpace(input.CodigoInterno))
            ModelState.AddModelError(nameof(input.CodigoInterno), "Informe o código interno.");
        if (!ModelState.IsValid) return View("Form", await MontarForm(input));

        Aplicar(item, input);
        item.CodigoInterno = input.CodigoInterno!.Trim().ToUpperInvariant();
        if (!await Salvar(input)) return View("Form", await MontarForm(input));

        await DefinirMinimoAsync(item.Id, input.QuantidadeMinima);
        TempData["Sucesso"] = $"Item \"{item.Nome}\" atualizado.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Exclusão = inativação (o histórico de movimentações é preservado).</summary>
    [Permissao(Permissoes.CadastroItens), HttpPost]
    public async Task<IActionResult> AlterarStatus(int id, bool ativo)
    {
        var item = await db.Itens.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound();
        item.Ativo = ativo;
        await db.SaveChangesAsync();
        TempData["Sucesso"] = $"Item \"{item.Nome}\" {(ativo ? "reativado" : "inativado")}.";
        return RedirectToAction(nameof(Index), new { inativos = !ativo });
    }

    private static void Aplicar(Item item, ItemInput input)
    {
        item.Nome = input.Nome.Trim();
        item.CategoriaId = input.CategoriaId!.Value;
        item.UnidadeMedida = input.UnidadeMedida;
        item.PrecoEstimado = input.PrecoEstimado;
        item.OrigemPadrao = string.IsNullOrEmpty(input.OrigemPadrao) ? null : input.OrigemPadrao;
        item.Descricao = string.IsNullOrWhiteSpace(input.Descricao) ? null : input.Descricao.Trim();
    }

    private async Task ValidarReferencias(ItemInput input, string? unidadeAtualDoItem = null)
    {
        if (input.CategoriaId is > 0 && !await db.Categorias.AnyAsync(c => c.Id == input.CategoriaId && c.Ativo))
            ModelState.AddModelError(nameof(input.CategoriaId), "Categoria inválida.");
        // Um item pode manter a unidade que já tinha, mesmo inativada depois; trocar exige unidade ativa
        if (!await db.UnidadesMedida.AnyAsync(u => u.Codigo == input.UnidadeMedida && (u.Ativo || u.Codigo == unidadeAtualDoItem)))
            ModelState.AddModelError(nameof(input.UnidadeMedida), "Unidade de medida inválida.");
    }

    private async Task<bool> Salvar(ItemInput input)
    {
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (ErrosBanco.Unicidade(ex))
        {
            db.ChangeTracker.Clear();
            ModelState.AddModelError(nameof(input.CodigoInterno), "Já existe um item com este código interno.");
            return false;
        }
    }

    private async Task DefinirMinimoAsync(int itemId, int minimo)
    {
        var est = await db.Estoques.FirstOrDefaultAsync(e => e.ItemId == itemId);
        if (est is null)
        {
            if (minimo == 0) return;
            db.Estoques.Add(new EstoqueUnidade { ItemId = itemId, QuantidadeMinima = minimo });
        }
        else if (est.QuantidadeMinima != minimo)
        {
            est.QuantidadeMinima = minimo;
        }
        await db.SaveChangesAsync();
    }

    private async Task<string> GerarCodigoAsync()
    {
        var n = await db.Itens.IgnoreQueryFilters().CountAsync(i => i.TenantId == tenant.TenantId) + 1;
        while (true)
        {
            var codigo = $"ITEM-{n:0000}";
            if (!await db.Itens.AnyAsync(i => i.CodigoInterno == codigo)) return codigo;
            n++;
        }
    }

    private async Task<ItemFormViewModel> MontarForm(ItemInput input) => new()
    {
        Input = input,
        Categorias = await db.Categorias.Where(c => c.Ativo || c.Id == input.CategoriaId).OrderBy(c => c.Nome).ToListAsync(),
        // mantém a unidade atual do item mesmo que tenha sido inativada depois
        UnidadesMedida = await db.UnidadesMedida.Where(u => u.Ativo || u.Codigo == input.UnidadeMedida)
            .OrderBy(u => u.Descricao).ToListAsync(),
        NomeUnidade = db.Tenant.UnidadeNome ?? "",
    };
}
