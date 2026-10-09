using Estoque.Web.Data;
using Estoque.Web.Infrastructure.Security;
using Estoque.Web.Models.Entities;
using Estoque.Web.Models.ViewModels;
using Estoque.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.Controllers;

/// <summary>Unidades de medida cadastráveis por tenant (kg, pacote, lata, ...). Inativar em vez de excluir.</summary>
public class UnidadesMedidaController(EstoqueDbContext db) : Controller
{
    public async Task<IActionResult> Index()
    {
        var unidades = await db.UnidadesMedida.OrderByDescending(u => u.Ativo).ThenBy(u => u.Descricao).ToListAsync();
        var uso = await db.Itens.GroupBy(i => i.UnidadeMedida).Select(g => new { Codigo = g.Key, Qtd = g.Count() })
            .ToDictionaryAsync(x => x.Codigo, x => x.Qtd);
        return View(unidades.Select(u => new UnidadeMedidaLinha
        {
            Unidade = u,
            ItensUsando = uso.TryGetValue(u.Codigo, out var n) ? n : 0,
        }).ToList());
    }

    [Authorize(Policy = Policies.PodeEditar)]
    public IActionResult Criar() => View("Form", new UnidadeMedidaInput());

    [Authorize(Policy = Policies.PodeEditar), HttpPost]
    public async Task<IActionResult> Criar(UnidadeMedidaInput input)
    {
        if (!ModelState.IsValid) return View("Form", input);
        db.UnidadesMedida.Add(new UnidadeMedida
        {
            Codigo = input.Codigo.Trim().ToLowerInvariant(),
            Descricao = input.Descricao.Trim(),
        });
        if (!await Salvar(input)) return View("Form", input);
        TempData["Sucesso"] = "Unidade de medida cadastrada.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = Policies.PodeEditar)]
    public async Task<IActionResult> Editar(int id)
    {
        var u = await db.UnidadesMedida.FirstOrDefaultAsync(x => x.Id == id);
        return u is null ? NotFound() : View("Form", new UnidadeMedidaInput { Id = u.Id, Codigo = u.Codigo, Descricao = u.Descricao });
    }

    /// <summary>Só o nome muda: a sigla é a chave que os itens guardam.</summary>
    [Authorize(Policy = Policies.PodeEditar), HttpPost]
    public async Task<IActionResult> Editar(int id, UnidadeMedidaInput input)
    {
        var u = await db.UnidadesMedida.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return NotFound();
        input.Id = id;
        input.Codigo = u.Codigo;
        ModelState.Remove(nameof(input.Codigo));
        if (!ModelState.IsValid) return View("Form", input);
        u.Descricao = input.Descricao.Trim();
        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Unidade de medida atualizada.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = Policies.PodeEditar), HttpPost]
    public async Task<IActionResult> AlterarStatus(int id, bool ativo)
    {
        var u = await db.UnidadesMedida.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return NotFound();
        u.Ativo = ativo;
        await db.SaveChangesAsync();
        TempData["Sucesso"] = $"Unidade \"{u.Descricao}\" {(ativo ? "reativada" : "inativada")}.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> Salvar(UnidadeMedidaInput input)
    {
        try { await db.SaveChangesAsync(); return true; }
        catch (DbUpdateException ex) when (ErrosBanco.Unicidade(ex))
        {
            db.ChangeTracker.Clear();
            ModelState.AddModelError(nameof(input.Codigo), "Já existe uma unidade com esta sigla.");
            return false;
        }
    }
}
