using Estoque.Web.Data;
using Estoque.Web.Infrastructure;
using Estoque.Web.Infrastructure.Security;
using Estoque.Web.Models.Entities;
using Estoque.Web.Models.ViewModels;
using Estoque.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.Controllers;

/// <summary>
/// E5.1: usuários (só o master). Não há exclusão: inativar mantém o histórico das movimentações.
/// A senha nunca é digitada pelo master: o sistema gera uma temporária, mostrada uma única vez,
/// e o usuário é obrigado a trocá-la no primeiro acesso.
/// </summary>
[SomenteMaster]
public class UsuariosController(EstoqueDbContext db, IPasswordHasher<Usuario> hasher) : Controller
{
    // O formulário usa UsuarioFormViewModel.Input: campos e erros ficam sob "Input."
    private const string Prefixo = "Input";

    public async Task<IActionResult> Index() =>
        View(await db.Usuarios.Include(u => u.Perfil).Include(u => u.Unidade)
            .OrderByDescending(u => u.Master).ThenByDescending(u => u.Ativo).ThenBy(u => u.Nome)
            .ToListAsync());

    public async Task<IActionResult> Criar() => View("Form", await MontarAsync(new UsuarioInput()));

    [HttpPost, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Criar([Bind(Prefix = Prefixo)] UsuarioInput input)
    {
        input.Master = false;
        await ValidarAsync(input, null);
        if (!ModelState.IsValid) return View("Form", await MontarAsync(input));

        var senha = Senhas.GerarTemporaria();
        var usuario = new Usuario
        {
            Nome = input.Nome.Trim(),
            Email = Normalizar(input.Email),
            PerfilId = input.PerfilId,
            UnidadeId = input.UnidadeId,
            TrocarSenha = true,
        };
        usuario.SenhaHash = hasher.HashPassword(usuario, senha);
        db.Usuarios.Add(usuario);
        if (!await SalvarAsync()) return View("Form", await MontarAsync(input));
        return View("SenhaGerada", new SenhaGeradaViewModel(usuario.Nome, usuario.Email, senha, Novo: true));
    }

    public async Task<IActionResult> Editar(int id)
    {
        var u = await db.Usuarios.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return NotFound();
        return View("Form", await MontarAsync(new UsuarioInput
        {
            Id = u.Id, Nome = u.Nome, Email = u.Email, PerfilId = u.PerfilId, UnidadeId = u.UnidadeId, Master = u.Master,
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Editar(int id, [Bind(Prefix = Prefixo)] UsuarioInput input)
    {
        var u = await db.Usuarios.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return NotFound();
        input.Id = id;
        input.Master = u.Master;
        await ValidarAsync(input, u);
        if (!ModelState.IsValid) return View("Form", await MontarAsync(input));

        u.Nome = input.Nome.Trim();
        u.Email = Normalizar(input.Email);
        u.UnidadeId = input.UnidadeId;
        if (!u.Master) u.PerfilId = input.PerfilId;
        if (!await SalvarAsync()) return View("Form", await MontarAsync(input));
        TempData["Sucesso"] = "Usuário atualizado.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> AlterarStatus(int id, bool ativo)
    {
        var u = await db.Usuarios.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return NotFound();
        if (u.Master)
        {
            TempData["Erro"] = "O usuário master não pode ser inativado.";
            return RedirectToAction(nameof(Index));
        }
        u.Ativo = ativo;
        await db.SaveChangesAsync();
        TempData["Sucesso"] = ativo ? $"{u.Nome} foi reativado." : $"{u.Nome} foi inativado e não consegue mais entrar.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Gera nova senha temporária e desbloqueia o usuário. O master troca a própria senha em "Alterar senha".</summary>
    [HttpPost, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> RedefinirSenha(int id)
    {
        var u = await db.Usuarios.FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return NotFound();
        if (u.Master)
        {
            TempData["Erro"] = "Para trocar a senha do master, use \"Alterar senha\" no menu do seu usuário.";
            return RedirectToAction(nameof(Index));
        }
        var senha = Senhas.GerarTemporaria();
        u.SenhaHash = hasher.HashPassword(u, senha);
        u.TrocarSenha = true;
        u.TentativasFalhasLogin = 0;
        u.BloqueadoAte = null;
        await db.SaveChangesAsync();
        return View("SenhaGerada", new SenhaGeradaViewModel(u.Nome, u.Email, senha, Novo: false));
    }

    private async Task ValidarAsync(UsuarioInput input, Usuario? atual)
    {
        var email = Normalizar(input.Email);
        // E-mail é o login e é único no sistema inteiro (UQ_usuarios_email)
        if (!string.IsNullOrEmpty(email) &&
            await db.Usuarios.IgnoreQueryFilters().AnyAsync(u => u.Email == email && u.Id != input.Id))
            ModelState.AddModelError(Prefixo + "." + nameof(input.Email), "Este e-mail já está em uso.");

        if (!input.Master)
        {
            if (input.PerfilId is not > 0)
                ModelState.AddModelError(Prefixo + "." + nameof(input.PerfilId), "Escolha o perfil de acesso.");
            else if (input.PerfilId != atual?.PerfilId && !await db.Perfis.AnyAsync(p => p.Id == input.PerfilId && p.Ativo))
                ModelState.AddModelError(Prefixo + "." + nameof(input.PerfilId), "Perfil não encontrado ou inativo.");
        }

        if (input.UnidadeId > 0 && input.UnidadeId != atual?.UnidadeId &&
            !await db.Unidades.AnyAsync(u => u.Id == input.UnidadeId && u.Ativo))
            ModelState.AddModelError(Prefixo + "." + nameof(input.UnidadeId), "Unidade não encontrada ou inativa.");
    }

    private async Task<bool> SalvarAsync()
    {
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (ErrosBanco.Unicidade(ex))
        {
            db.ChangeTracker.Clear();
            ModelState.AddModelError(Prefixo + "." + nameof(UsuarioInput.Email), "Este e-mail já está em uso.");
            return false;
        }
    }

    private async Task<UsuarioFormViewModel> MontarAsync(UsuarioInput input) => new()
    {
        Input = input,
        Perfis = await db.Perfis.Where(p => p.Ativo || p.Id == input.PerfilId).OrderBy(p => p.Nome).ToListAsync(),
        Unidades = await db.Unidades.Where(u => u.Ativo || u.Id == input.UnidadeId)
            .OrderBy(u => u.Tipo == TiposUnidade.Matriz ? 0 : 1).ThenBy(u => u.Nome).ToListAsync(),
    };

    private static string Normalizar(string? email) => (email ?? "").Trim().ToLowerInvariant();
}

/// <summary>E5.1: perfis de acesso (só o master). Cada perfil é uma lista de permissões do catálogo.</summary>
[SomenteMaster]
public class PerfisController(EstoqueDbContext db) : Controller
{
    public async Task<IActionResult> Index()
    {
        var perfis = await db.Perfis.Include(p => p.Permissoes)
            .OrderByDescending(p => p.Ativo).ThenBy(p => p.Nome).ToListAsync();
        var uso = await db.Usuarios.Where(u => u.Ativo && u.PerfilId != null)
            .GroupBy(u => u.PerfilId!.Value).Select(g => new { g.Key, Total = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Total);
        return View(perfis.Select(p => new PerfilLinha(p, uso.GetValueOrDefault(p.Id))).ToList());
    }

    public IActionResult Criar() => View("Form", new PerfilInput());

    [HttpPost]
    public async Task<IActionResult> Criar(PerfilInput input)
    {
        if (!ModelState.IsValid) return View("Form", input);
        var perfil = new Perfil { Nome = input.Nome.Trim(), Descricao = Limpar(input.Descricao) };
        foreach (var p in Validas(input.Permissoes))
            perfil.Permissoes.Add(new PerfilPermissao { Permissao = p });
        db.Perfis.Add(perfil);
        if (!await SalvarAsync()) return View("Form", input);
        TempData["Sucesso"] = "Perfil criado.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Editar(int id)
    {
        var p = await db.Perfis.Include(x => x.Permissoes).FirstOrDefaultAsync(x => x.Id == id);
        return p is null ? NotFound() : View("Form", new PerfilInput
        {
            Id = p.Id, Nome = p.Nome, Descricao = p.Descricao, Permissoes = p.Permissoes.Select(x => x.Permissao).ToList(),
        });
    }

    [HttpPost]
    public async Task<IActionResult> Editar(int id, PerfilInput input)
    {
        var perfil = await db.Perfis.Include(x => x.Permissoes).FirstOrDefaultAsync(x => x.Id == id);
        if (perfil is null) return NotFound();
        input.Id = id;
        if (!ModelState.IsValid) return View("Form", input);

        perfil.Nome = input.Nome.Trim();
        perfil.Descricao = Limpar(input.Descricao);
        var desejadas = Validas(input.Permissoes);
        perfil.Permissoes.RemoveAll(x => !desejadas.Contains(x.Permissao));
        foreach (var p in desejadas.Except(perfil.Permissoes.Select(x => x.Permissao)))
            perfil.Permissoes.Add(new PerfilPermissao { Permissao = p });
        if (!await SalvarAsync()) return View("Form", input);
        TempData["Sucesso"] = "Perfil atualizado. As permissões valem a partir da próxima tela aberta por quem usa o perfil.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> AlterarStatus(int id, bool ativo)
    {
        var p = await db.Perfis.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        p.Ativo = ativo;
        await db.SaveChangesAsync();
        TempData["Sucesso"] = ativo
            ? $"Perfil {p.Nome} reativado."
            : $"Perfil {p.Nome} inativado: quem o usa passa a só consultar estoque e movimentações até receber outro perfil.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Só códigos do catálogo; qualquer outro valor enviado no formulário é descartado.</summary>
    private static HashSet<string> Validas(IEnumerable<string>? permissoes) =>
        (permissoes ?? []).Where(Permissoes.Existe).ToHashSet();

    private async Task<bool> SalvarAsync()
    {
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (ErrosBanco.Unicidade(ex))
        {
            db.ChangeTracker.Clear();
            ModelState.AddModelError(nameof(PerfilInput.Nome), "Já existe um perfil com este nome.");
            return false;
        }
    }

    private static string? Limpar(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
