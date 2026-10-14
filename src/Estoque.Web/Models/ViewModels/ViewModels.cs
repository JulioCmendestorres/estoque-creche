using System.ComponentModel.DataAnnotations;
using Estoque.Web.Models.Entities;

namespace Estoque.Web.Models.ViewModels;

public class LoginInput
{
    [Required(ErrorMessage = "Informe o e-mail."), EmailAddress(ErrorMessage = "E-mail inválido."), StringLength(255)]
    [Display(Name = "E-mail")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Informe a senha."), DataType(DataType.Password), StringLength(200)]
    [Display(Name = "Senha")]
    public string Senha { get; set; } = "";

    public string? ReturnUrl { get; set; }
}

public class AlterarSenhaInput
{
    [Required(ErrorMessage = "Informe a senha atual."), DataType(DataType.Password), StringLength(200)]
    [Display(Name = "Senha atual")]
    public string SenhaAtual { get; set; } = "";

    [Required(ErrorMessage = "Informe a nova senha."), DataType(DataType.Password)]
    [StringLength(200, MinimumLength = 12, ErrorMessage = "A nova senha precisa ter ao menos 12 caracteres.")]
    [Display(Name = "Nova senha")]
    public string NovaSenha { get; set; } = "";

    [Required(ErrorMessage = "Confirme a nova senha."), DataType(DataType.Password)]
    [Compare(nameof(NovaSenha), ErrorMessage = "As senhas não conferem.")]
    [Display(Name = "Confirmar nova senha")]
    public string Confirmacao { get; set; } = "";
}

public class DashboardViewModel
{
    public int DoacoesRecebidas { get; init; }
    public int ItensEmEstoque { get; init; }
    public int ItensDistintos { get; init; }
    public int Beneficiarios { get; init; }
    public int DoacoesRealizadas { get; init; }
    public IReadOnlyList<EstoqueUnidade> Alertas { get; init; } = [];
    public IReadOnlyList<Movimentacao> UltimasMovimentacoes { get; init; } = [];
}

// ---------------------------------------------------------------- Itens

public class ItemInput
{
    public int Id { get; set; }

    [StringLength(50), Display(Name = "Código interno")]
    [RegularExpression(@"^[A-Za-z0-9\-_.]*$", ErrorMessage = "Use apenas letras, números, '-', '_' ou '.'.")]
    public string? CodigoInterno { get; set; }

    [Required(ErrorMessage = "Informe o nome."), StringLength(255), Display(Name = "Nome")]
    public string Nome { get; set; } = "";

    [Required(ErrorMessage = "Selecione a categoria."), Display(Name = "Categoria (tipo)")]
    public int? CategoriaId { get; set; }

    [Required(ErrorMessage = "Selecione a unidade de medida."), StringLength(10), Display(Name = "Unidade de medida")]
    public string UnidadeMedida { get; set; } = "unidade";

    [Range(0, 99999999, ErrorMessage = "Preço inválido."), Display(Name = "Preço estimado (R$)")]
    public decimal? PrecoEstimado { get; set; }

    [Display(Name = "Origem padrão")]
    [RegularExpression("^(doacao|compra)?$", ErrorMessage = "Origem inválida.")]
    public string? OrigemPadrao { get; set; }

    [StringLength(2000), Display(Name = "Observação")]
    public string? Descricao { get; set; }

    [Range(0, 1000000, ErrorMessage = "Quantidade mínima inválida."), Display(Name = "Quantidade mínima nesta unidade")]
    public int QuantidadeMinima { get; set; }
}

public class ItemFormViewModel
{
    public ItemInput Input { get; init; } = new();
    public IReadOnlyList<Categoria> Categorias { get; init; } = [];
    public IReadOnlyList<UnidadeMedida> UnidadesMedida { get; init; } = [];
    public string NomeUnidade { get; init; } = "";
}

public class ItemLinha
{
    public required Item Item { get; init; }
    public int Saldo { get; init; }
    public int Minimo { get; init; }
    public bool Alerta { get; init; }
}

public class ItensIndexViewModel
{
    public string? Busca { get; init; }
    public int? CategoriaId { get; init; }
    public bool MostrarInativos { get; init; }
    public IReadOnlyList<ItemLinha> Itens { get; init; } = [];
    public IReadOnlyList<Categoria> Categorias { get; init; } = [];
}

// ---------------------------------------------------------------- Categorias

public class CategoriaInput
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Informe o nome."), StringLength(100), Display(Name = "Nome")]
    public string Nome { get; set; } = "";

    [StringLength(1000), Display(Name = "Descrição")]
    public string? Descricao { get; set; }
}

// ---------------------------------------------------------------- Parceiros

public class ParceiroInput : IValidatableObject
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Informe o nome."), StringLength(255), Display(Name = "Nome")]
    public string Nome { get; set; } = "";

    [Required, Display(Name = "Tipo")]
    [RegularExpression("^(pessoa_fisica|empresa|organizacao)$", ErrorMessage = "Tipo inválido.")]
    public string Tipo { get; set; } = TiposParceiro.Empresa;

    [StringLength(18), Display(Name = "CNPJ")]
    public string? Cnpj { get; set; }

    [EmailAddress(ErrorMessage = "E-mail inválido."), StringLength(255), Display(Name = "E-mail")]
    public string? Email { get; set; }

    [StringLength(20), Display(Name = "Telefone")]
    public string? Telefone { get; set; }

    [StringLength(1000), Display(Name = "Endereço")]
    public string? Endereco { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var doc = Infrastructure.Documentos.SomenteDigitos(Cnpj);
        if (doc.Length == 0) yield break;
        if (doc.Length == 11)
            yield return new ValidationResult("Não registramos CPF. Informe apenas CNPJ ou deixe em branco.", [nameof(Cnpj)]);
        else if (!Infrastructure.Documentos.CnpjValido(doc))
            yield return new ValidationResult("CNPJ inválido.", [nameof(Cnpj)]);
    }
}

public class UnidadeMedidaInput
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Informe a sigla."), StringLength(10), Display(Name = "Sigla")]
    [RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", ErrorMessage = "Use letras, números, '.', '-' ou '_', sem espaços.")]
    public string Codigo { get; set; } = "";

    [Required(ErrorMessage = "Informe o nome."), StringLength(50), Display(Name = "Nome")]
    public string Descricao { get; set; } = "";
}

public class UnidadeMedidaLinha
{
    public required UnidadeMedida Unidade { get; init; }
    public int ItensUsando { get; init; }
}

// ---------------------------------------------------------------- Estoque

public class EntradaInput
{
    [Required, Display(Name = "Origem")]
    public string Origem { get; set; } = TiposMovimentacao.DoacaoRecebida;

    [Required(ErrorMessage = "Selecione o item."), Range(1, int.MaxValue, ErrorMessage = "Selecione o item."), Display(Name = "Item")]
    public int ItemId { get; set; }

    [Range(1, 1000000, ErrorMessage = "A quantidade deve ser maior que zero."), Display(Name = "Quantidade")]
    public int Quantidade { get; set; } = 1;

    [Range(0, 99999999, ErrorMessage = "Valor inválido."), Display(Name = "Valor unitário estimado (R$)")]
    public decimal? ValorUnitario { get; set; }

    [Display(Name = "Data")]
    public DateOnly Data { get; set; }

    [Display(Name = "Fornecedor")]
    public int? ParceiroId { get; set; }

    [StringLength(2000), Display(Name = "Observação")]
    public string? Observacao { get; set; }
}

public class SaidaInput
{
    [Required(ErrorMessage = "Selecione o item."), Range(1, int.MaxValue, ErrorMessage = "Selecione o item."), Display(Name = "Item")]
    public int ItemId { get; set; }

    [Range(1, 1000000, ErrorMessage = "A quantidade deve ser maior que zero."), Display(Name = "Quantidade")]
    public int Quantidade { get; set; } = 1;

    [Required, Display(Name = "Tipo de saída")]
    public string TipoSaida { get; set; } = TiposMovimentacao.ConsumoInterno;

    [StringLength(255), Display(Name = "Beneficiário")]
    public string? Beneficiario { get; set; }

    [Range(0, 99999999, ErrorMessage = "Valor inválido."), Display(Name = "Valor unitário (informativo)")]
    public decimal? ValorUnitario { get; set; }

    [Display(Name = "Data")]
    public DateOnly Data { get; set; }

    [StringLength(2000), Display(Name = "Observação")]
    public string? Observacao { get; set; }
}

public class FiltroMovimentacoes
{
    public DateOnly? De { get; set; }
    public DateOnly? Ate { get; set; }
    public string? Tipo { get; set; }
    public int? ItemId { get; set; }
}

public class EstoqueIndexViewModel
{
    public string Aba { get; init; } = "estoque";
    public string? Busca { get; init; }
    public IReadOnlyList<EstoqueUnidade> Saldos { get; init; } = [];
    public IReadOnlyList<Movimentacao> Movimentacoes { get; init; } = [];
    public FiltroMovimentacoes Filtro { get; init; } = new();
    // Itens são buscados sob demanda (/Itens/Buscar); aqui vão só os já escolhidos, para reexibir o formulário
    public Item? ItemEntrada { get; init; }
    public Item? ItemSaida { get; init; }
    public Item? ItemFiltro { get; init; }
    public IReadOnlyList<Parceiro> Parceiros { get; init; } = [];
    public EntradaInput Entrada { get; init; } = new();
    public SaidaInput Saida { get; init; } = new();
    public string? ModalAberto { get; init; }
    public string NomeResponsavel { get; init; } = "";
    public string NomeUnidade { get; init; } = "";
}

/// <summary>Parâmetros do campo de busca de item (_BuscaItem): substitui o dropdown com todos os itens.</summary>
public record BuscaItemModel(
    string IdCampo,              // id do input oculto com o ItemId (ex.: Entrada_ItemId)
    string NomeCampo,            // name enviado no POST (ex.: Entrada.ItemId)
    string Modo,                 // "entrada", "saida", "filtro" ou "relatorio" (inclui itens inativos)
    Item? Selecionado = null,
    bool Obrigatorio = true,
    string? IdUnidadeAlvo = null,    // input somente leitura que recebe a unidade do item escolhido
    string? IdValorAlvo = null);     // input editável que recebe o preço estimado cadastrado no item

public class UnidadeOpcao
{
    public int Id { get; init; }
    public string Nome { get; init; } = "";
    public string Tipo { get; init; } = "";
}

/// <summary>Filtros do relatório de movimentações (E5.2). Tipo aceita um tipo específico ou os grupos "entradas"/"saidas".</summary>
public class FiltroRelatorio
{
    public const string TodasEntradas = "entradas";
    public const string TodasSaidas = "saidas";

    public DateOnly? De { get; set; }
    public DateOnly? Ate { get; set; }
    public string? Tipo { get; set; }
    public int? CategoriaId { get; set; }
    public int? ItemId { get; set; }
    public int? ParceiroId { get; set; }
    public bool IncluirCanceladas { get; set; }
    /// <summary>Todas as unidades do usuário (só admin; o controller ignora para os demais papéis).</summary>
    public bool Consolidado { get; set; }
}

public sealed record LinhaRelatorio(
    string Numero,
    DateOnly Data,
    string Tipo,
    string Unidade,
    string Item,
    string CodigoItem,
    string Categoria,
    int Quantidade,
    string UnidadeMedida,
    decimal ValorUnitario,
    decimal ValorTotal,
    string? Parceiro,
    string? Beneficiario,
    string Responsavel,
    string Status,
    string? Observacoes)
{
    public bool EhEntrada => TiposMovimentacao.EhEntrada(Tipo);
    public bool Cancelada => Status == StatusMovimentacao.Cancelada;
    /// <summary>Entrada: fornecedor/doador; saída: beneficiário (doação) ou o tipo de uso.</summary>
    public string OrigemDestino => EhEntrada ? Parceiro ?? "Anônimo" : Beneficiario ?? "—";
}

/// <summary>Totais por tipo, só de movimentações processadas (canceladas não contam).</summary>
public sealed record ResumoTipo(string Tipo, int Movimentacoes, decimal ValorTotal)
{
    public bool EhEntrada => TiposMovimentacao.EhEntrada(Tipo);
}

public class RelatorioMovimentacoes
{
    public required FiltroRelatorio Filtro { get; init; }
    public required string NomeOrganizacao { get; init; }
    /// <summary>Nome da unidade atual, ou "Todas as unidades" no consolidado.</summary>
    public required string Abrangencia { get; init; }
    public required IReadOnlyList<LinhaRelatorio> Linhas { get; init; }
    public required IReadOnlyList<ResumoTipo> Resumo { get; init; }
    /// <summary>Total de linhas que atendem ao filtro (pode ser maior que Linhas.Count quando há limite).</summary>
    public int TotalEncontrado { get; init; }
    public bool Truncado => TotalEncontrado > Linhas.Count;
    public DateTime GeradoEmUtc { get; init; }
    public string GeradoPor { get; init; } = "";
    /// <summary>Descrição legível dos filtros aplicados, para cabeçalho de PDF/CSV.</summary>
    public IReadOnlyList<string> FiltrosDescritos { get; init; } = [];

    public decimal TotalEntradas => Resumo.Where(r => r.EhEntrada).Sum(r => r.ValorTotal);
    public decimal TotalSaidas => Resumo.Where(r => !r.EhEntrada).Sum(r => r.ValorTotal);
}

public class RelatorioIndexViewModel
{
    public required FiltroRelatorio Filtro { get; init; }
    public RelatorioMovimentacoes? Relatorio { get; init; }
    public string? Erro { get; init; }
    public bool PodeConsolidar { get; init; }
    public Item? ItemFiltro { get; init; }
    public IReadOnlyList<Categoria> Categorias { get; init; } = [];
    public IReadOnlyList<Parceiro> Parceiros { get; init; } = [];
}
