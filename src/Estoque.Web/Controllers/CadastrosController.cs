using Estoque.Web.Data;
using Estoque.Web.Infrastructure;
using Estoque.Web.Infrastructure.Security;
using Estoque.Web.Models.Entities;
using Estoque.Web.Models.ViewModels;
using Estoque.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.Controllers;

/// <summary>Categorias (tipo do item).</summary>
public class CategoriasController(EstoqueDbContext db) : Controller
{
    public async Task<IActionResult> Index() =>
        View(await db.Categorias.OrderByDescending(c => c.Ativo).ThenBy(c => c.Nome).ToListAsync());

    [Authorize(Policy = Policies.PodeEditar)]
    public IActionResult Criar() => View("Form", new CategoriaInput());

    [Authorize(Policy = Policies.PodeEditar), HttpPost]
    public async Task<IActionResult> Criar(CategoriaInput input)
    {
        if (!ModelState.IsValid) return View("Form", input);
        db.Categorias.Add(new Categoria { Nome = input.Nome.Trim(), Descricao = Limpar(input.Descricao) });
        if (!await Salvar(input)) return View("Form", input);
        TempData["Sucesso"] = "Categoria cadastrada.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = Policies.PodeEditar)]
    public async Task<IActionResult> Editar(int id)
    {
        var c = await db.Categorias.FirstOrDefaultAsync(x => x.Id == id);
        return c is null ? NotFound() : View("Form", new CategoriaInput { Id = c.Id, Nome = c.Nome, Descricao = c.Descricao });
    }

    [Authorize(Policy = Policies.PodeEditar), HttpPost]
    public async Task<IActionResult> Editar(int id, CategoriaInput input)
    {
        var c = await db.Categorias.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        input.Id = id;
        if (!ModelState.IsValid) return View("Form", input);
        c.Nome = input.Nome.Trim();
        c.Descricao = Limpar(input.Descricao);
        if (!await Salvar(input)) return View("Form", input);
        TempData["Sucesso"] = "Categoria atualizada.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = Policies.PodeEditar), HttpPost]
    public async Task<IActionResult> AlterarStatus(int id, bool ativo)
    {
        var c = await db.Categorias.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        c.Ativo = ativo;
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> Salvar(CategoriaInput input)
    {
        try { await db.SaveChangesAsync(); return true; }
        catch (DbUpdateException ex) when (ErrosBanco.Unicidade(ex))
        {
            db.ChangeTracker.Clear();
            ModelState.AddModelError(nameof(input.Nome), "Já existe uma categoria com este nome.");
            return false;
        }
    }

    private static string? Limpar(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>RF06 — fornecedores/doadores. Fornecedor anônimo = movimentação sem parceiro.</summary>
public class ParceirosController(EstoqueDbContext db) : Controller
{
    public async Task<IActionResult> Index(string? busca, bool inativos = false)
    {
        var q = db.Parceiros.AsQueryable();
        if (!inativos) q = q.Where(p => p.Ativo);
        if (!string.IsNullOrWhiteSpace(busca))
        {
            var b = busca.Trim();
            var digitos = Documentos.SomenteDigitos(b);
            q = q.Where(p => p.Nome.Contains(b) || (digitos.Length > 0 && p.CpfCnpj!.Contains(digitos)));
        }
        ViewBag.Busca = busca;
        ViewBag.Inativos = inativos;
        return View(await q.OrderBy(p => p.Nome).ToListAsync());
    }

    [Authorize(Policy = Policies.PodeEditar)]
    public IActionResult Criar() => View("Form", new ParceiroInput());

    [Authorize(Policy = Policies.PodeEditar), HttpPost]
    public async Task<IActionResult> Criar(ParceiroInput input)
    {
        if (!ModelState.IsValid) return View("Form", input);
        var p = new Parceiro();
        Aplicar(p, input);
        db.Parceiros.Add(p);
        if (!await Salvar(input)) return View("Form", input);
        TempData["Sucesso"] = $"Fornecedor \"{p.Nome}\" cadastrado.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = Policies.PodeEditar)]
    public async Task<IActionResult> Editar(int id)
    {
        var p = await db.Parceiros.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        return View("Form", new ParceiroInput
        {
            Id = p.Id, Nome = p.Nome, Tipo = p.Tipo, CpfCnpj = Documentos.Formatar(p.CpfCnpj) is "—" ? null : Documentos.Formatar(p.CpfCnpj),
            Email = p.Email, Telefone = p.Telefone, Endereco = p.Endereco,
        });
    }

    [Authorize(Policy = Policies.PodeEditar), HttpPost]
    public async Task<IActionResult> Editar(int id, ParceiroInput input)
    {
        var p = await db.Parceiros.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        input.Id = id;
        if (!ModelState.IsValid) return View("Form", input);
        Aplicar(p, input);
        if (!await Salvar(input)) return View("Form", input);
        TempData["Sucesso"] = $"Fornecedor \"{p.Nome}\" atualizado.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = Policies.PodeEditar), HttpPost]
    public async Task<IActionResult> AlterarStatus(int id, bool ativo)
    {
        var p = await db.Parceiros.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        p.Ativo = ativo;
        await db.SaveChangesAsync();
        TempData["Sucesso"] = $"Fornecedor \"{p.Nome}\" {(ativo ? "reativado" : "inativado")}.";
        return RedirectToAction(nameof(Index));
    }

    private static void Aplicar(Parceiro p, ParceiroInput input)
    {
        var doc = Documentos.SomenteDigitos(input.CpfCnpj);
        p.Nome = input.Nome.Trim();
        p.Tipo = input.Tipo;
        p.CpfCnpj = doc.Length == 0 ? null : doc;
        p.Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim();
        p.Telefone = string.IsNullOrWhiteSpace(input.Telefone) ? null : input.Telefone.Trim();
        p.Endereco = string.IsNullOrWhiteSpace(input.Endereco) ? null : input.Endereco.Trim();
    }

    private async Task<bool> Salvar(ParceiroInput input)
    {
        try { await db.SaveChangesAsync(); return true; }
        catch (DbUpdateException ex) when (ErrosBanco.Unicidade(ex))
        {
            db.ChangeTracker.Clear();
            ModelState.AddModelError(nameof(input.CpfCnpj), "Já existe um fornecedor com este CPF/CNPJ.");
            return false;
        }
    }
}
