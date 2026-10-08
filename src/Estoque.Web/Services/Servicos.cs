using System.Text.Json;
using Estoque.Web.Data;
using Estoque.Web.Infrastructure;
using Estoque.Web.Models.Entities;
using Estoque.Web.Models.ViewModels;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.Services;

public sealed record Resultado(bool Ok, string? Erro = null)
{
    public static readonly Resultado Sucesso = new(true);
    public static Resultado Falha(string erro) => new(false, erro);
}

/// <summary>Ações de aplicação que os triggers não enxergam (A16): login, logout, troca de unidade...</summary>
public sealed class AuditoriaService(EstoqueDbContext db)
{
    public async Task RegistrarAsync(string acao, int? tenantId, int? unidadeId, int? usuarioId, object? dados = null)
    {
        db.LogsAuditoria.Add(new LogAuditoria
        {
            TenantId = tenantId,
            UnidadeId = unidadeId,
            UsuarioId = usuarioId,
            TabelaAfetada = "aplicacao",
            Acao = acao,
            RegistroId = usuarioId,
            DadosNovos = dados is null ? null : JsonSerializer.Serialize(dados),
            IpUsuario = db.Tenant.Ip is { Length: > 45 } ip ? ip[..45] : db.Tenant.Ip,
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>Erros lançados pelos triggers (THROW 5000x) e constraints, traduzidos para o usuário.</summary>
public static class ErrosBanco
{
    public const int EstoqueInsuficiente = 50001;
    public const int MovimentacaoImutavel = 50002;
    public const int TransicaoInvalida = 50003;

    public static int? Numero(Exception ex) => (ex as SqlException ?? ex.InnerException as SqlException)?.Number;

    public static bool Unicidade(Exception ex) => Numero(ex) is 2601 or 2627;
}

public sealed class MovimentacaoService(EstoqueDbContext db)
{
    public async Task<Resultado> RegistrarEntradaAsync(EntradaInput input)
    {
        if (!TiposMovimentacao.Entradas.Contains(input.Origem))
            return Resultado.Falha("Origem inválida.");

        var item = await db.Itens.FirstOrDefaultAsync(i => i.Id == input.ItemId && i.Ativo);
        if (item is null) return Resultado.Falha("Item não encontrado ou inativo.");

        int? parceiroId = input.ParceiroId is > 0 ? input.ParceiroId : null;
        if (parceiroId is not null && !await db.Parceiros.AnyAsync(p => p.Id == parceiroId && p.Ativo))
            return Resultado.Falha("Fornecedor não encontrado ou inativo.");
        if (input.Origem == TiposMovimentacao.Compra && parceiroId is null)
            return Resultado.Falha("Compra exige fornecedor identificado.");
        if (input.Data.Year < 2000)
            return Resultado.Falha("Informe a data da entrada.");
        if (input.Data > Tempo.Hoje)
            return Resultado.Falha("A data da entrada não pode ser futura.");

        db.Movimentacoes.Add(new Movimentacao
        {
            TipoMovimentacao = input.Origem,
            ItemId = item.Id,
            Quantidade = input.Quantidade,
            ValorUnitario = input.ValorUnitario ?? item.PrecoEstimado ?? 0,
            DataMovimentacao = input.Data,
            ParceiroId = parceiroId,
            Observacoes = Limpar(input.Observacao),
            ResponsavelId = db.Tenant.UsuarioId!.Value,
            Status = StatusMovimentacao.Processada,
        });
        return await SalvarAsync();
    }

    public async Task<Resultado> RegistrarSaidaAsync(SaidaInput input)
    {
        if (!TiposMovimentacao.Saidas.Contains(input.TipoSaida))
            return Resultado.Falha("Tipo de saída inválido.");

        var item = await db.Itens.FirstOrDefaultAsync(i => i.Id == input.ItemId && i.Ativo);
        if (item is null) return Resultado.Falha("Item não encontrado ou inativo.");

        var beneficiario = Limpar(input.Beneficiario);
        if (input.TipoSaida == TiposMovimentacao.DoacaoRealizada && beneficiario is null)
            return Resultado.Falha("Informe o beneficiário da doação.");
        if (input.Data.Year < 2000)
            return Resultado.Falha("Informe a data da saída.");
        if (input.Data > Tempo.Hoje)
            return Resultado.Falha("A data da saída não pode ser futura.");

        // Validação na aplicação; o trigger (THROW 50001) é a rede de segurança para concorrência
        var saldo = await db.Estoques.Where(e => e.ItemId == item.Id).Select(e => (int?)e.QuantidadeAtual).FirstOrDefaultAsync() ?? 0;
        if (input.Quantidade > saldo)
            return Resultado.Falha($"Estoque insuficiente: saldo atual de {item.Nome} é {saldo} {item.UnidadeMedida}.");

        db.Movimentacoes.Add(new Movimentacao
        {
            TipoMovimentacao = input.TipoSaida,
            ItemId = item.Id,
            Quantidade = input.Quantidade,
            ValorUnitario = input.ValorUnitario ?? item.PrecoEstimado ?? 0,
            DataMovimentacao = input.Data,
            Beneficiario = input.TipoSaida == TiposMovimentacao.DoacaoRealizada ? beneficiario : null,
            DescricaoSaida = TiposMovimentacao.Rotulo(input.TipoSaida),
            Observacoes = Limpar(input.Observacao),
            ResponsavelId = db.Tenant.UsuarioId!.Value,
            Status = StatusMovimentacao.Processada,
        });
        return await SalvarAsync();
    }

    /// <summary>Excluir no protótipo = cancelar (estorno pelo trigger). Só admin (controller).</summary>
    public async Task<Resultado> CancelarAsync(int id, string? motivo, string nomeUsuario)
    {
        var mov = await db.Movimentacoes.FirstOrDefaultAsync(m => m.Id == id);
        if (mov is null) return Resultado.Falha("Movimentação não encontrada.");
        if (mov.Status != StatusMovimentacao.Processada)
            return Resultado.Falha("Somente movimentações processadas podem ser canceladas.");

        motivo = Limpar(motivo);
        if (motivo is null) return Resultado.Falha("Informe o motivo do cancelamento.");

        var nota = $"[Cancelada em {Tempo.Local(DateTime.UtcNow):dd/MM/yyyy HH:mm} por {nomeUsuario}: {motivo}]";
        mov.Observacoes = string.IsNullOrEmpty(mov.Observacoes) ? nota : $"{mov.Observacoes}\n{nota}";
        mov.Status = StatusMovimentacao.Cancelada;
        var r = await SalvarAsync();
        return r.Ok || r.Erro != MensagemEstoqueInsuficiente
            ? r
            : Resultado.Falha("Não é possível cancelar: o estoque atual ficaria negativo (os itens já saíram).");
    }

    private const string MensagemEstoqueInsuficiente = "Estoque insuficiente para concluir a operação.";

    private async Task<Resultado> SalvarAsync()
    {
        try
        {
            await db.SaveChangesAsync();
            return Resultado.Sucesso;
        }
        catch (DbUpdateException ex)
        {
            db.ChangeTracker.Clear();
            return ErrosBanco.Numero(ex) switch
            {
                ErrosBanco.EstoqueInsuficiente => Resultado.Falha(MensagemEstoqueInsuficiente),
                ErrosBanco.MovimentacaoImutavel => Resultado.Falha("Movimentação processada não pode ser alterada."),
                ErrosBanco.TransicaoInvalida => Resultado.Falha("Transição de status inválida."),
                547 => Resultado.Falha("Os dados violam uma regra de negócio (verifique fornecedor, beneficiário e valores)."),
                _ => throw new InvalidOperationException("Falha ao gravar a movimentação.", ex),
            };
        }
    }

    private static string? Limpar(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed class DashboardService(EstoqueDbContext db)
{
    public async Task<DashboardViewModel> MontarAsync()
    {
        var processadas = db.Movimentacoes.Where(m => m.Status == StatusMovimentacao.Processada);
        var estoqueAtivo = db.Estoques.Where(e => e.Item!.Ativo);

        return new DashboardViewModel
        {
            DoacoesRecebidas = await processadas.CountAsync(m => m.TipoMovimentacao == TiposMovimentacao.DoacaoRecebida),
            ItensEmEstoque = await estoqueAtivo.SumAsync(e => (int?)e.QuantidadeAtual) ?? 0,
            ItensDistintos = await estoqueAtivo.CountAsync(e => e.QuantidadeAtual > 0),
            Beneficiarios = await processadas
                .Where(m => m.TipoMovimentacao == TiposMovimentacao.DoacaoRealizada && m.Beneficiario != null)
                .Select(m => m.Beneficiario).Distinct().CountAsync(),
            DoacoesRealizadas = await processadas.CountAsync(m => m.TipoMovimentacao == TiposMovimentacao.DoacaoRealizada),
            Alertas = await ConsultarAlertas().Take(10).ToListAsync(),
            UltimasMovimentacoes = await db.Movimentacoes
                .Include(m => m.Item).Include(m => m.Responsavel)
                .OrderByDescending(m => m.CriadoEm).ThenByDescending(m => m.Id)
                .Take(10).ToListAsync(),
        };
    }

    public IQueryable<EstoqueUnidade> ConsultarAlertas() =>
        db.Estoques.Include(e => e.Item)
            .Where(e => e.AbaixoDoMinimo && e.Item!.Ativo)
            .OrderBy(e => e.QuantidadeAtual - e.QuantidadeMinima).ThenBy(e => e.Item!.Nome);

    public Task<int> ContarAlertasAsync() => db.Estoques.CountAsync(e => e.AbaixoDoMinimo && e.Item!.Ativo);
}
