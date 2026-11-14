using Estoque.Web.Data;
using Estoque.Web.Models.Entities;
using Estoque.Web.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.Services;

/// <summary>
/// D-46: unidades da organização (só o master). Uma unidade é nome + ativa/inativa; não há exclusão,
/// porque movimentações e auditoria apontam para ela. "Remover" = desativar, e exige digitar o nome.
/// </summary>
public sealed class UnidadesService(EstoqueDbContext db)
{
    public async Task<List<UnidadeLinha>> ListarAsync()
    {
        var tenantId = db.Tenant.TenantId;
        var unidades = await db.Unidades.OrderByDescending(u => u.Ativo).ThenBy(u => u.Nome).ToListAsync();
        var usuarios = await db.Usuarios.Where(u => u.Ativo && !u.Master)
            .GroupBy(u => u.UnidadeId).Select(g => new { g.Key, Qtd = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Qtd);
        // O filtro de unidade é desligado só aqui (o tenant é reaplicado): a tela mostra todas as unidades
        var saldos = await db.Estoques.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && e.QuantidadeAtual > 0)
            .GroupBy(e => e.UnidadeId).Select(g => new { g.Key, Qtd = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Qtd);
        return [.. unidades.Select(u => new UnidadeLinha(u, usuarios.GetValueOrDefault(u.Id), saldos.GetValueOrDefault(u.Id), u.Id == db.Tenant.UnidadeId))];
    }

    public Task<Unidade?> ObterAsync(int id) => db.Unidades.FirstOrDefaultAsync(u => u.Id == id);

    /// <summary>Cria (Id = 0) ou renomeia. O nome é único na organização, sem distinguir maiúsculas nem acentos.</summary>
    public async Task<Resultado> SalvarAsync(UnidadeInput input)
    {
        var nome = (input.Nome ?? "").Trim();
        if (nome.Length == 0) return Resultado.Falha("Informe o nome da unidade.");
        if (nome.Length > 100) return Resultado.Falha("O nome pode ter até 100 caracteres.");

        Unidade? unidade;
        if (input.Id == 0)
        {
            unidade = new Unidade { TenantId = db.Tenant.TenantId!.Value, Nome = nome, Ativo = true };
            db.Unidades.Add(unidade);
        }
        else
        {
            unidade = await ObterAsync(input.Id);
            if (unidade is null) return Resultado.Falha("Unidade não encontrada.");
            unidade.Nome = nome;
        }

        try
        {
            await db.SaveChangesAsync();
            return Resultado.Sucesso;
        }
        catch (DbUpdateException ex) when (ErrosBanco.Unicidade(ex))
        {
            db.ChangeTracker.Clear();
            return Resultado.Falha($"Já existe uma unidade chamada \"{nome}\".");
        }
    }

    /// <summary>Por que a unidade não pode ser desativada agora; null quando pode.</summary>
    public async Task<string?> MotivoParaDesativarAsync(Unidade unidade)
    {
        if (unidade.Id == db.Tenant.UnidadeId)
            return "Você está nesta unidade. Troque para outra no topo da tela antes de desativá-la.";
        if (!await db.Unidades.AnyAsync(u => u.Ativo && u.Id != unidade.Id))
            return "Esta é a única unidade ativa. O sistema precisa de pelo menos uma.";

        var usuarios = await db.Usuarios.CountAsync(u => u.UnidadeId == unidade.Id && u.Ativo && !u.Master);
        if (usuarios > 0)
            return $"{usuarios} {(usuarios == 1 ? "usuário ativo está cadastrado" : "usuários ativos estão cadastrados")} nesta unidade. " +
                   "Mova cada um para outra unidade ou inative-os antes.";

        var tenantId = db.Tenant.TenantId;
        var comSaldo = await db.Estoques.IgnoreQueryFilters()
            .CountAsync(e => e.TenantId == tenantId && e.UnidadeId == unidade.Id && e.QuantidadeAtual > 0);
        if (comSaldo > 0)
            return $"Ainda há {comSaldo} {(comSaldo == 1 ? "item com saldo" : "itens com saldo")} nesta unidade. " +
                   "Registre a saída do estoque antes de desativá-la.";
        return null;
    }

    /// <summary>
    /// Desativa a unidade. <paramref name="nomeDigitado"/> precisa ser exatamente o nome dela
    /// (confirmação no estilo "digite o nome para confirmar"). O histórico fica preservado.
    /// </summary>
    public async Task<Resultado> DesativarAsync(int id, string? nomeDigitado)
    {
        var unidade = await ObterAsync(id);
        if (unidade is null) return Resultado.Falha("Unidade não encontrada.");
        if (!unidade.Ativo) return Resultado.Falha("Esta unidade já está desativada.");
        if (!string.Equals((nomeDigitado ?? "").Trim(), unidade.Nome, StringComparison.Ordinal))
            return Resultado.Falha($"O nome digitado não confere. Para desativar, digite exatamente \"{unidade.Nome}\".");

        var motivo = await MotivoParaDesativarAsync(unidade);
        if (motivo is not null) return Resultado.Falha(motivo);

        unidade.Ativo = false;
        await db.SaveChangesAsync();
        return Resultado.Sucesso;
    }

    public async Task<Resultado> ReativarAsync(int id)
    {
        var unidade = await ObterAsync(id);
        if (unidade is null) return Resultado.Falha("Unidade não encontrada.");
        unidade.Ativo = true;
        await db.SaveChangesAsync();
        return Resultado.Sucesso;
    }
}
