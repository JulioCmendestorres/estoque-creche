namespace Estoque.Web.Infrastructure.Security;

/// <summary>
/// Catálogo de permissões que um perfil pode receber (tabela perfis_permissoes guarda o código).
/// Consultar estoque, movimentações e o dashboard da própria unidade é liberado a todo usuário ativo.
/// Usuários e perfis são exclusivos do master (não são permissão: evita que alguém se dê mais acesso).
/// </summary>
public static class Permissoes
{
    public const string EstoqueMovimentar = "estoque.movimentar";
    public const string EstoqueCancelar = "estoque.cancelar";
    public const string CadastroItens = "cadastros.itens";
    public const string CadastroCategorias = "cadastros.categorias";
    public const string CadastroUnidadesMedida = "cadastros.unidades_medida";
    public const string CadastroFornecedores = "cadastros.fornecedores";
    public const string RelatoriosVer = "relatorios.ver";
    public const string RelatoriosExportar = "relatorios.exportar";
    public const string VisaoConsolidada = "global.consolidado";

    /// <param name="Global">Afeta ou mostra dados da matriz e da filial ao mesmo tempo.</param>
    public sealed record Definicao(string Codigo, string Grupo, string Nome, string Descricao, bool Global);

    public static readonly IReadOnlyList<Definicao> Todas =
    [
        new(EstoqueMovimentar, "Estoque", "Registrar entradas e saídas", "Nova Entrada e Nova Saída na própria unidade.", false),
        new(EstoqueCancelar, "Estoque", "Cancelar movimentações", "Estorna uma movimentação processada da própria unidade.", false),
        new(CadastroItens, "Cadastros compartilhados", "Itens", "Criar, editar e inativar itens do catálogo.", true),
        new(CadastroCategorias, "Cadastros compartilhados", "Categorias", "Criar, editar e inativar categorias.", true),
        new(CadastroUnidadesMedida, "Cadastros compartilhados", "Unidades de medida", "Criar, editar e inativar unidades de medida.", true),
        new(CadastroFornecedores, "Cadastros compartilhados", "Fornecedores", "Criar, editar e inativar fornecedores e doadores.", true),
        new(RelatoriosVer, "Relatórios", "Ver relatórios", "Relatório de movimentações da própria unidade, na tela e impresso.", false),
        new(RelatoriosExportar, "Relatórios", "Exportar PDF e CSV", "Baixar o relatório em PDF ou CSV (fica registrado na auditoria).", false),
        new(VisaoConsolidada, "Visão global", "Ver todas as unidades", "Relatório consolidado da matriz e da filial juntas.", true),
    ];

    private static readonly HashSet<string> Codigos = Todas.Select(p => p.Codigo).ToHashSet();

    public static bool Existe(string codigo) => Codigos.Contains(codigo);
}
