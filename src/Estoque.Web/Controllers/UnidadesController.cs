using Estoque.Web.Infrastructure.Security;
using Estoque.Web.Models.ViewModels;
using Estoque.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Estoque.Web.Controllers;

/// <summary>D-46: cadastro de unidades (só o master). Criar, renomear, desativar e reativar; não há exclusão.</summary>
[SomenteMaster]
public class UnidadesController(UnidadesService unidades) : Controller
{
    public async Task<IActionResult> Index() => View(await unidades.ListarAsync());

    public IActionResult Criar() => View("Form", new UnidadeInput());

    [HttpPost]
    public async Task<IActionResult> Criar(UnidadeInput input)
    {
        input.Id = 0;
        return await SalvarAsync(input, $"Unidade \"{input.Nome?.Trim()}\" criada. Para trabalhar nela, escolha-a no topo da tela.");
    }

    public async Task<IActionResult> Editar(int id)
    {
        var u = await unidades.ObterAsync(id);
        return u is null ? NotFound() : View("Form", new UnidadeInput { Id = u.Id, Nome = u.Nome });
    }

    [HttpPost]
    public async Task<IActionResult> Editar(int id, UnidadeInput input)
    {
        input.Id = id;
        return await SalvarAsync(input, "Nome da unidade alterado.");
    }

    [HttpPost]
    public async Task<IActionResult> Desativar(int id, string? confirmacao)
    {
        var r = await unidades.DesativarAsync(id, confirmacao);
        if (r.Ok) TempData["Sucesso"] = "Unidade desativada. O histórico dela continua nos relatórios.";
        else TempData["Erro"] = r.Erro;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Reativar(int id)
    {
        var r = await unidades.ReativarAsync(id);
        if (r.Ok) TempData["Sucesso"] = "Unidade reativada.";
        else TempData["Erro"] = r.Erro;
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> SalvarAsync(UnidadeInput input, string sucesso)
    {
        if (ModelState.IsValid)
        {
            var r = await unidades.SalvarAsync(input);
            if (r.Ok)
            {
                TempData["Sucesso"] = sucesso;
                return RedirectToAction(nameof(Index));
            }
            ModelState.AddModelError(nameof(input.Nome), r.Erro!);
        }
        return View("Form", input);
    }
}
