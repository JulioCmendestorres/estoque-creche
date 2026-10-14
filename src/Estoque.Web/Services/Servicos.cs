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
    public const int ItemDesativadoNaUnidade = 50006;

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
        if (await DesativadoNaUnidadeAsync(item.Id)) return Resultado.Falha(MensagemDesativadoNaUnidade);

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
        if (await DesativadoNaUnidadeAsync(item.Id)) return Resultado.Falha(MensagemDesativadoNaUnidade);

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
    private const string MensagemDesativadoNaUnidade = "Este item está desativado nesta unidade. Reative-o na tela de Itens para movimentar.";

    // Filtro global de Estoques = unidade atual
    private Task<bool> DesativadoNaUnidadeAsync(int itemId) => db.Estoques.AnyAsync(e => e.ItemId == itemId && !e.Ativo);

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
                ErrosBanco.ItemDesativadoNaUnidade => Resultado.Falha(MensagemDesativadoNaUnidade),
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
        var estoqueAtivo = db.Estoques.Where(e => e.Item!.Ativo && e.Ativo);

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
            .Where(e => e.AbaixoDoMinimo && e.Ativo && e.Item!.Ativo)
            .OrderBy(e => e.QuantidadeAtual - e.QuantidadeMinima).ThenBy(e => e.Item!.Nome);

    public Task<int> ContarAlertasAsync() => db.Estoques.CountAsync(e => e.AbaixoDoMinimo && e.Ativo && e.Item!.Ativo);
}

/// <summary>
/// D-38: travas dos cadastros compartilhados entre matriz e filial. Desativar algo que outra unidade
/// ainda usa é recusado com a explicação de onde está o uso. Devolve null quando pode desativar.
/// </summary>
public sealed class TravasCadastroService(EstoqueDbContext db)
{
    /// <summary>Item no catálogo inteiro: nenhuma unidade pode ter saldo.</summary>
    public async Task<string?> MotivoParaInativarItemAsync(int itemId)
    {
        var tenantId = db.Tenant.TenantId;
        // O filtro de unidade é desligado só aqui (o tenant é reaplicado): a trava olha todas as unidades
        var comSaldo = await db.Estoques.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && e.ItemId == itemId && e.QuantidadeAtual > 0)
            .Select(e => new { e.UnidadeId, e.QuantidadeAtual, e.Item!.UnidadeMedida })
            .ToListAsync();
        if (comSaldo.Count == 0) return null;

        var nomes = await db.Unidades.ToDictionaryAsync(u => u.Id, u => u.Nome);
        var onde = string.Join("; ", comSaldo.Select(e => $"{nomes.GetValueOrDefault(e.UnidadeId, "?")} ainda tem {e.QuantidadeAtual} {e.UnidadeMedida}"));
        return $"Não é possível inativar: {onde}. Zere o saldo (saída ou avaria) em cada unidade antes.";
    }

    /// <summary>Item só na unidade atual: o saldo dela precisa estar zerado.</summary>
    public async Task<string?> MotivoParaDesativarNaUnidadeAsync(int itemId)
    {
        var saldo = await db.Estoques.Where(e => e.ItemId == itemId)
            .Select(e => new { e.QuantidadeAtual, e.Item!.UnidadeMedida }).FirstOrDefaultAsync();
        return saldo is { QuantidadeAtual: > 0 }
            ? $"Ainda há {saldo.QuantidadeAtual} {saldo.UnidadeMedida} nesta unidade. Registre a saída (ou avaria) antes de desativar."
            : null;
    }

    public async Task<string?> MotivoParaInativarCategoriaAsync(int categoriaId) =>
        Descrever(await db.Itens.Where(i => i.CategoriaId == categoriaId && i.Ativo).OrderBy(i => i.Nome)
                .Select(i => i.Nome).Take(4).ToListAsync(),
            await db.Itens.CountAsync(i => i.CategoriaId == categoriaId && i.Ativo),
            "esta categoria", "Mude a categoria desses itens ou inative-os antes.");

    public async Task<string?> MotivoParaInativarUnidadeMedidaAsync(string codigo) =>
        Descrever(await db.Itens.Where(i => i.UnidadeMedida == codigo && i.Ativo).OrderBy(i => i.Nome)
                .Select(i => i.Nome).Take(4).ToListAsync(),
            await db.Itens.CountAsync(i => i.UnidadeMedida == codigo && i.Ativo),
            "esta unidade de medida", "Mude a unidade desses itens ou inative-os antes.");

    private static string? Descrever(List<string> exemplos, int total, string oQue, string acao) =>
        total == 0 ? null
            : $"Não é possível inativar: {total} item(ns) ativo(s) usam {oQue} ({string.Join(", ", exemplos)}{(total > exemplos.Count ? ", …" : "")}). {acao}";
}
