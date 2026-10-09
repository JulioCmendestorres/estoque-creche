using System.Data;
using Estoque.Web.Models.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

namespace Estoque.Web.Data.Seed;

public sealed record SeedOptions
{
    public string? AdminEmail { get; init; }
    public string? AdminPassword { get; init; }
    public bool DemoData { get; init; }
    public bool Producao { get; init; }
}

/// <summary>
/// T3.1: roda ao final do --migrate, só se ainda não existir tenant.
/// Sempre: tenant, Matriz, Filial, admin (trocar_senha = 1) e categorias.
/// Com SEED_DEMO_DATA=true: itens da planilha (Apêndice A), mínimos, parceiros,
/// um usuário por papel e algumas movimentações.
/// </summary>
public sealed class Seeder(string dbConnectionString, SeedOptions opt, ILogger logger)
{
    public const string NomeTenant = "Creche Fraternidade Menino Jesus";
    public const int TamanhoMinimoSenha = 12;

    private static readonly string[] SenhasProibidas =
        ["troque-esta-senha", "TroqueEstaSenha", "changeme", "admin123456", "senha123456", "123456789012"];

    // Ponto de partida; o tenant cadastra, renomeia e inativa unidades pela tela "Unidades de medida"
    private static readonly (string Codigo, string Descricao)[] UnidadesPadrao =
    [
        ("unidade", "Unidade avulsa"), ("kg", "Quilograma"), ("litro", "Litro"), ("pacote", "Pacote"),
        ("caixa", "Caixa"), ("lata", "Lata"), ("sache", "Sachê"),
    ];

    private static readonly string[] Categorias =["Alimento", "Roupa", "Brinquedo", "Higiene e limpeza", "Outros"];

    // Apêndice A do PLANO.md (lista parcial da planilha da creche)
    private static readonly (string Nome, string Unidade, decimal Preco)[] ItensDemo =
    [
        ("Açúcar (Kg)", "kg", 4.50m), ("Açúcar demerara (Kg)", "kg", 7.90m), ("Adoçante 75 ml", "unidade", 6.00m),
        ("Arroz 5 kg", "pacote", 27.00m), ("Arroz 2 kg", "pacote", 12.00m), ("Arroz 1 kg", "pacote", 6.50m),
        ("Atum 170 g", "lata", 8.00m), ("Azeite 500 ml", "unidade", 32.00m), ("Azeitona (sachê)", "sache", 5.00m),
        ("Batata palha 380 g", "pacote", 11.00m), ("Biscoito aveia e mel 135 g", "pacote", 4.00m),
        ("Biscoito de água e sal 360 g", "pacote", 6.00m), ("Biscoito de maisena 360 g", "pacote", 6.00m),
        ("Biscoito de polvilho 200 g", "pacote", 7.00m), ("Biscoito rosca de coco 300 g", "pacote", 6.50m),
        ("Cacau 100% 500 g", "pacote", 30.00m), ("Canela em pó 50 g", "unidade", 5.00m), ("Canjica (Kg)", "kg", 8.00m),
        ("Chá matte 250 g", "caixa", 9.00m), ("Chia", "pacote", 10.00m), ("Coco ralado (Kg)", "kg", 40.00m),
        ("Creme de leite 200 ml", "caixa", 3.50m), ("Doce de batata/abóbora (cx)", "caixa", 15.00m),
        ("Ervilha (lata)", "lata", 4.00m), ("Farinha de mandioca 500 g", "pacote", 6.00m),
        ("Farinha de mandioca 1 kg", "pacote", 10.00m), ("Farinha de trigo (Kg)", "kg", 5.50m),
        ("Farinha de rosca 500 g", "pacote", 7.00m),
    ];

    public static void ValidarSenhaAdmin(SeedOptions opt)
    {
        if (string.IsNullOrWhiteSpace(opt.AdminEmail) || !opt.AdminEmail.Contains('@'))
            throw new InvalidOperationException("SEED_ADMIN_EMAIL não definido ou inválido.");
        var senha = opt.AdminPassword ?? "";
        if (senha.Length < TamanhoMinimoSenha)
            throw new InvalidOperationException($"SEED_ADMIN_PASSWORD precisa ter ao menos {TamanhoMinimoSenha} caracteres.");
        if (opt.Producao && SenhasProibidas.Any(p => string.Equals(p, senha, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("SEED_ADMIN_PASSWORD usa um valor padrão. Defina uma senha própria no .env.");
    }

    public async Task ExecutarAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(dbConnectionString);
        await conn.OpenAsync(ct);

        var jaExiste = (int)(await Escalar(conn, null, "SELECT COUNT(*) FROM tenants;", ct))!;
        if (jaExiste > 0)
        {
            logger.LogInformation("Seed ignorado: já existe tenant cadastrado.");
            return;
        }

        ValidarSenhaAdmin(opt);
        var hasher = new PasswordHasher<Usuario>();

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            var tenantId = await Inserir(conn, tx, "INSERT INTO tenants (nome) VALUES (@nome);", ct,
                P("@nome", NomeTenant));
            var matrizId = await Inserir(conn, tx,
                "INSERT INTO unidades (tenant_id, nome, tipo) VALUES (@t, N'Matriz', 'matriz');", ct, P("@t", tenantId));
            var filialId = await Inserir(conn, tx,
                "INSERT INTO unidades (tenant_id, nome, tipo) VALUES (@t, N'Filial', 'filial');", ct, P("@t", tenantId));

            var categorias = new Dictionary<string, int>();
            foreach (var c in Categorias)
                categorias[c] = await Inserir(conn, tx,
                    "INSERT INTO categorias_itens (tenant_id, nome) VALUES (@t, @n);", ct, P("@t", tenantId), P("@n", c));

            foreach (var (codigo, descricao) in UnidadesPadrao)
                await Inserir(conn, tx,
                    "INSERT INTO unidades_medida (tenant_id, codigo, descricao) VALUES (@t, @c, @d);", ct,
                    P("@t", tenantId), P("@c", codigo), P("@d", descricao));

            var adminId = await CriarUsuario(conn, tx, tenantId, "Administrador", opt.AdminEmail!,
                hasher.HashPassword(null!, opt.AdminPassword!), Papeis.Admin, trocarSenha: true, [matrizId, filialId], ct);

            if (opt.DemoData)
                await SemearDemo(conn, tx, tenantId, matrizId, filialId, adminId, categorias["Alimento"], hasher, ct);

            await tx.CommitAsync(ct);
            logger.LogInformation("Seed concluído: tenant {Tenant}, Matriz e Filial, admin {Email}{Demo}.",
                NomeTenant, opt.AdminEmail, opt.DemoData ? " + dados de demonstração" : "");
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task SemearDemo(SqlConnection conn, SqlTransaction tx, int tenantId, int matrizId, int filialId,
        int adminId, int categoriaAlimento, PasswordHasher<Usuario> hasher, CancellationToken ct)
    {
        // Usuários de exemplo, um por papel; mesma senha do admin (somente ambiente de teste)
        var hashDemo = hasher.HashPassword(null!, opt.AdminPassword!);
        var dominio = opt.AdminEmail!.Split('@')[1];
        var operadorId = await CriarUsuario(conn, tx, tenantId, "Operador Demo", $"operacional@{dominio}", hashDemo,
            Papeis.Operacional, trocarSenha: false, [matrizId, filialId], ct);
        await CriarUsuario(conn, tx, tenantId, "Visualizador Demo", $"visualizador@{dominio}", hashDemo,
            Papeis.Visualizador, trocarSenha: false, [matrizId], ct);

        var mercadoId = await Inserir(conn, tx, """
            INSERT INTO parceiros (tenant_id, nome, tipo, cnpj, telefone) VALUES (@t, N'Mercado Bom Preço (fictício)', 'empresa', '11222333000181', '(11) 4000-0000');
            """, ct, P("@t", tenantId));
        await Inserir(conn, tx, """
            INSERT INTO parceiros (tenant_id, nome, tipo)
            VALUES (@t, N'Doador pessoa física (fictício)', 'pessoa_fisica');
            """, ct, P("@t", tenantId));

        var itens = new List<int>();
        var n = 0;
        foreach (var (nome, unidade, preco) in ItensDemo)
        {
            n++;
            var id = await Inserir(conn, tx, """
                INSERT INTO itens (tenant_id, codigo_interno, nome, categoria_id, unidade_medida, preco_estimado, origem_padrao) VALUES (@t, @cod, @nome, @cat, @um, @preco, 'doacao');
                """, ct, P("@t", tenantId), P("@cod", $"ALI-{n:000}"), P("@nome", nome), P("@cat", categoriaAlimento),
                P("@um", unidade), P("@preco", preco));
            itens.Add(id);
            foreach (var unidadeId in new[] { matrizId, filialId })
                await Inserir(conn, tx, """
                    INSERT INTO estoque_unidade (tenant_id, unidade_id, item_id, quantidade_minima) VALUES (@t, @u, @i, @min);
                    """, ct, P("@t", tenantId), P("@u", unidadeId), P("@i", id), P("@min", 5));
        }

        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
        async Task Mov(int unidadeId, string tipo, int item, int qtd, decimal valor, int dias, int? parceiro = null,
            string? beneficiario = null, int? responsavel = null) =>
            await Inserir(conn, tx, """
                INSERT INTO movimentacoes (tenant_id, unidade_id, tipo_movimentacao, item_id, quantidade, valor_unitario,
                                           data_movimentacao, parceiro_id, beneficiario, responsavel_id, status, observacoes)
                VALUES (@t, @u, @tipo, @i, @q, @v, @d, @p, @b, @r, 'processada', N'Dado de demonstração');
                """, ct, P("@t", tenantId), P("@u", unidadeId), P("@tipo", tipo), P("@i", item), P("@q", qtd),
                P("@v", valor), P("@d", hoje.AddDays(-dias)), P("@p", parceiro), P("@b", beneficiario),
                P("@r", responsavel ?? adminId));

        // Matriz: entradas para metade dos itens; alguns ficam abaixo do mínimo após as saídas
        for (var i = 0; i < itens.Count; i += 2)
            await Mov(matrizId, i % 4 == 0 ? TiposMovimentacao.Compra : TiposMovimentacao.DoacaoRecebida,
                itens[i], 10 + i, ItensDemo[i].Preco, 20 - i / 2, parceiro: i % 4 == 0 ? mercadoId : null);
        await Mov(matrizId, TiposMovimentacao.ConsumoInterno, itens[0], 8, ItensDemo[0].Preco, 5, responsavel: operadorId);
        await Mov(matrizId, TiposMovimentacao.DoacaoRealizada, itens[2], 9, ItensDemo[2].Preco, 4,
            beneficiario: "Família atendida (fictícia)", responsavel: operadorId);
        await Mov(matrizId, TiposMovimentacao.Avaria, itens[4], 11, ItensDemo[4].Preco, 3, responsavel: operadorId);

        // Filial: estoque independente
        await Mov(filialId, TiposMovimentacao.DoacaoRecebida, itens[0], 6, ItensDemo[0].Preco, 6);
        await Mov(filialId, TiposMovimentacao.DoacaoRecebida, itens[3], 3, ItensDemo[3].Preco, 6);
    }

    private static async Task<int> CriarUsuario(SqlConnection conn, SqlTransaction tx, int tenantId, string nome, string email,
        string hash, string papel, bool trocarSenha, int[] unidades, CancellationToken ct)
    {
        var id = await Inserir(conn, tx, """
            INSERT INTO usuarios (tenant_id, nome, email, senha, papel, trocar_senha) VALUES (@t, @nome, @email, @senha, @papel, @trocar);
            """, ct, P("@t", tenantId), P("@nome", nome), P("@email", email.Trim().ToLowerInvariant()),
            P("@senha", hash), P("@papel", papel), P("@trocar", trocarSenha));
        foreach (var u in unidades)
            await Inserir(conn, tx, """
                INSERT INTO usuarios_unidades (tenant_id, usuario_id, unidade_id) VALUES (@t, @usr, @u);
                """, ct, P("@t", tenantId), P("@usr", id), P("@u", u));
        return id;
    }

    private static SqlParameter P(string nome, object? valor) => valor switch
    {
        null => new SqlParameter(nome, DBNull.Value),
        string s => new SqlParameter(nome, SqlDbType.NVarChar, Math.Max(s.Length, 1)) { Value = s },
        DateOnly d => new SqlParameter(nome, SqlDbType.Date) { Value = d.ToDateTime(TimeOnly.MinValue) },
        _ => new SqlParameter(nome, valor),
    };

    private static async Task<int> Inserir(SqlConnection conn, SqlTransaction tx, string sql, CancellationToken ct,
        params SqlParameter[] ps)
    {
        // OUTPUT sem INTO não é permitido em tabelas com trigger; SCOPE_IDENTITY ignora o escopo dos triggers
        var r = await Escalar(conn, tx, sql.TrimEnd().TrimEnd(';') + ";\nSELECT CAST(SCOPE_IDENTITY() AS INT);", ct, ps);
        return r is null or DBNull ? 0 : Convert.ToInt32(r);
    }

    private static async Task<object?> Escalar(SqlConnection conn, SqlTransaction? tx, string sql, CancellationToken ct,
        params SqlParameter[] ps)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddRange(ps);
        return await cmd.ExecuteScalarAsync(ct);
    }
}
