namespace Estoque.Web.Models.Entities;

/// <summary>Entidade que pertence a um tenant: o DbContext filtra e carimba o TenantId.</summary>
public interface ITenantEntity
{
    int TenantId { get; set; }
}

/// <summary>Entidade cujo dado é por unidade (matriz/filial): filtrada também pela unidade atual.</summary>
public interface IUnidadeEntity : ITenantEntity
{
    int UnidadeId { get; set; }
}

public class Tenant
{
    public int Id { get; set; }
    public string Nome { get; set; } = "";
    public string? Cnpj { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; }
}

public class Unidade : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Nome { get; set; } = "";
    public string Tipo { get; set; } = TiposUnidade.Filial;
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; }
}

public static class TiposUnidade
{
    public const string Matriz = "matriz";
    public const string Filial = "filial";
}

public class Usuario : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Nome { get; set; } = "";
    public string Email { get; set; } = "";
    public string SenhaHash { get; set; } = "";
    public string Papel { get; set; } = Papeis.Operacional;
    public bool Ativo { get; set; } = true;
    public bool TrocarSenha { get; set; }
    public DateTime? UltimoLoginEm { get; set; }
    public int TentativasFalhasLogin { get; set; }
    public DateTime? BloqueadoAte { get; set; }
    public DateTime CriadoEm { get; set; }
    public DateTime? AtualizadoEm { get; set; }
}

public static class Papeis
{
    public const string Admin = "admin";
    public const string Operacional = "operacional";
    public const string Visualizador = "visualizador";
}

public class UsuarioUnidade : ITenantEntity
{
    public int TenantId { get; set; }
    public int UsuarioId { get; set; }
    public int UnidadeId { get; set; }
    public Unidade? Unidade { get; set; }
}

public class UnidadeMedida
{
    public string Codigo { get; set; } = "";
    public string? Descricao { get; set; }
    public bool Ativo { get; set; } = true;
}

public class Categoria : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Nome { get; set; } = "";
    public string? Descricao { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; }
}

public class Item : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string CodigoInterno { get; set; } = "";
    public string Nome { get; set; } = "";
    public string? Descricao { get; set; }
    public int CategoriaId { get; set; }
    public Categoria? Categoria { get; set; }
    public string UnidadeMedida { get; set; } = "unidade";
    public decimal? PrecoEstimado { get; set; }
    public string? OrigemPadrao { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; }
    public DateTime? AtualizadoEm { get; set; }
}

public class EstoqueUnidade : IUnidadeEntity
{
    public int TenantId { get; set; }
    public int UnidadeId { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public int QuantidadeAtual { get; set; }
    public int QuantidadeMinima { get; set; }
    public decimal ValorTotalEstoque { get; set; }
    public bool AbaixoDoMinimo { get; set; }
    public DateTime? AtualizadoEm { get; set; }
}

public class Parceiro : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Nome { get; set; } = "";
    public string Tipo { get; set; } = TiposParceiro.Empresa;
    public string? CpfCnpj { get; set; }
    public string? Email { get; set; }
    public string? Telefone { get; set; }
    public string? Endereco { get; set; }
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; }
    public DateTime? AtualizadoEm { get; set; }
}

public static class TiposParceiro
{
    public const string PessoaFisica = "pessoa_fisica";
    public const string Empresa = "empresa";
    public const string Organizacao = "organizacao";

    public static readonly IReadOnlyDictionary<string, string> Rotulos = new Dictionary<string, string>
    {
        [PessoaFisica] = "Pessoa física",
        [Empresa] = "Empresa",
        [Organizacao] = "Organização",
    };
}

public class Movimentacao : IUnidadeEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int UnidadeId { get; set; }
    // Gerado pelo DEFAULT do banco (SEQUENCE). Precisa ficar null na inserção: com "" o EF enviaria o valor.
    public string NumeroMovimentacao { get; set; } = null!;
    public string TipoMovimentacao { get; set; } = "";
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    public int Quantidade { get; set; }
    public decimal ValorUnitario { get; set; }
    public decimal ValorTotal { get; set; }
    public DateOnly DataMovimentacao { get; set; }
    public int? ParceiroId { get; set; }
    public Parceiro? Parceiro { get; set; }
    public string? DescricaoSaida { get; set; }
    public string? Beneficiario { get; set; }
    public int ResponsavelId { get; set; }
    public Usuario? Responsavel { get; set; }
    public string? Observacoes { get; set; }
    public string Status { get; set; } = StatusMovimentacao.Pendente;
    public DateTime CriadoEm { get; set; }
    public DateTime? AtualizadoEm { get; set; }

    public bool EhEntrada => TiposMovimentacao.EhEntrada(TipoMovimentacao);
}

public static class TiposMovimentacao
{
    public const string DoacaoRecebida = "doacao_recebida";
    public const string Compra = "compra";
    public const string DoacaoRealizada = "doacao_realizada";
    public const string ConsumoInterno = "consumo_interno";
    public const string Avaria = "avaria";

    public static readonly string[] Entradas = [DoacaoRecebida, Compra];
    public static readonly string[] Saidas = [ConsumoInterno, DoacaoRealizada, Avaria];

    public static bool EhEntrada(string tipo) => tipo is DoacaoRecebida or Compra;

    public static readonly IReadOnlyDictionary<string, string> Rotulos = new Dictionary<string, string>
    {
        [DoacaoRecebida] = "Doação recebida",
        [Compra] = "Compra",
        [DoacaoRealizada] = "Doação realizada",
        [ConsumoInterno] = "Uso interno",
        [Avaria] = "Avaria",
    };

    public static string Rotulo(string tipo) => Rotulos.TryGetValue(tipo, out var r) ? r : tipo;
}

public static class StatusMovimentacao
{
    public const string Pendente = "pendente";
    public const string Processada = "processada";
    public const string Cancelada = "cancelada";
}

public class LogAuditoria
{
    public long Id { get; set; }
    public int? TenantId { get; set; }
    public int? UnidadeId { get; set; }
    public int? UsuarioId { get; set; }
    public string TabelaAfetada { get; set; } = "";
    public string Acao { get; set; } = "";
    public int? RegistroId { get; set; }
    public string? DadosAntigos { get; set; }
    public string? DadosNovos { get; set; }
    public string? IpUsuario { get; set; }
    public DateTime CriadoEm { get; set; }
}
