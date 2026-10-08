using Microsoft.Data.SqlClient;
using static Estoque.Tests.Sql;

namespace Estoque.Tests;

/// <summary>T1.4 — regras de negócio garantidas pelo banco (triggers, CHECKs, FKs compostas).</summary>
[Collection(ColecaoBanco.Nome)]
public class RegrasBancoTests(BancoFixture db)
{
    [Fact]
    public async Task Entrada_soma_saldo_e_saida_baixa_pelo_custo_medio()
    {
        await using var c = await db.AbrirAsync();
        var s = await NovoTenant(c);
        await Mov(c, s, "doacao_recebida", 10, 5m);
        await Mov(c, s, "doacao_recebida", 10, 7m);
        Assert.Equal((20, 120m), await Saldo(c, s.Matriz, s.Item));

        // custo médio = 6,00; o valor_unitario (100) da saída é só informativo
        await Mov(c, s, "consumo_interno", 5, 100m);
        Assert.Equal((15, 90m), await Saldo(c, s.Matriz, s.Item));
    }

    [Fact]
    public async Task Saida_maior_que_saldo_falha_com_50001_e_nada_muda()
    {
        await using var c = await db.AbrirAsync();
        var s = await NovoTenant(c);
        await Mov(c, s, "doacao_recebida", 3, 2m);

        var ex = await Assert.ThrowsAsync<SqlException>(() => Mov(c, s, "avaria", 4, 0m));
        Assert.Equal(50001, ex.Number);
        Assert.Equal((3, 6m), await Saldo(c, s.Matriz, s.Item));
        Assert.Equal(1, await Escalar(c, "SELECT COUNT(*) FROM movimentacoes WHERE item_id = @i", ("@i", s.Item)));
    }

    [Fact]
    public async Task Cancelar_movimentacao_processada_estorna()
    {
        await using var c = await db.AbrirAsync();
        var s = await NovoTenant(c);
        await Mov(c, s, "doacao_recebida", 10, 4m);
        var saida = await Mov(c, s, "doacao_realizada", 6, 4m, beneficiario: "Família X");
        Assert.Equal((4, 16m), await Saldo(c, s.Matriz, s.Item));

        await Exec(c, "UPDATE movimentacoes SET status = 'cancelada' WHERE id = @id", ("@id", saida));
        Assert.Equal((10, 40m), await Saldo(c, s.Matriz, s.Item));
    }

    [Fact]
    public async Task Cancelar_entrada_ja_consumida_falha()
    {
        await using var c = await db.AbrirAsync();
        var s = await NovoTenant(c);
        var entrada = await Mov(c, s, "doacao_recebida", 5, 1m);
        await Mov(c, s, "consumo_interno", 3, 0m);

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            Exec(c, "UPDATE movimentacoes SET status = 'cancelada' WHERE id = @id", ("@id", entrada)));
        Assert.Equal(50001, ex.Number);
    }

    [Fact]
    public async Task Duas_linhas_do_mesmo_item_no_mesmo_insert_somam_corretamente()
    {
        await using var c = await db.AbrirAsync();
        var s = await NovoTenant(c);
        await Exec(c, """
            INSERT INTO movimentacoes (tenant_id, unidade_id, tipo_movimentacao, item_id, quantidade, valor_unitario, data_movimentacao, responsavel_id, status)
            VALUES (@t, @u, 'doacao_recebida', @i, 4, 1, '2026-10-01', @r, 'processada'),
                   (@t, @u, 'doacao_recebida', @i, 6, 2, '2026-10-01', @r, 'processada');
            """, ("@t", s.Tenant), ("@u", s.Matriz), ("@i", s.Item), ("@r", s.Usuario));
        Assert.Equal((10, 16m), await Saldo(c, s.Matriz, s.Item));
    }

    [Fact]
    public async Task Regras_de_parceiro_e_beneficiario()
    {
        await using var c = await db.AbrirAsync();
        var s = await NovoTenant(c);
        await Mov(c, s, "doacao_recebida", 5, 1m); // sem parceiro: ok (anônimo)

        var semBenef = await Assert.ThrowsAsync<SqlException>(() => Mov(c, s, "doacao_realizada", 1, 1m));
        Assert.Contains("CK_mov_beneficiario_coerente", semBenef.Message);

        var compraSemParceiro = await Assert.ThrowsAsync<SqlException>(() => Mov(c, s, "compra", 1, 1m));
        Assert.Contains("CK_mov_parceiro_coerente", compraSemParceiro.Message);
    }

    [Fact]
    public async Task Parceiros_sem_documento_podem_repetir_mas_CNPJ_nao()
    {
        await using var c = await db.AbrirAsync();
        var s = await NovoTenant(c);
        const string sql = "INSERT INTO parceiros (tenant_id, nome, tipo, cpf_cnpj) VALUES (@t, @n, @tipo, @doc)";
        await Exec(c, sql, ("@t", s.Tenant), ("@n", "PF 1"), ("@tipo", "pessoa_fisica"), ("@doc", null));
        await Exec(c, sql, ("@t", s.Tenant), ("@n", "PF 2"), ("@tipo", "pessoa_fisica"), ("@doc", null));
        await Exec(c, sql, ("@t", s.Tenant), ("@n", "Empresa 1"), ("@tipo", "empresa"), ("@doc", "11222333000181"));

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            Exec(c, sql, ("@t", s.Tenant), ("@n", "Empresa 2"), ("@tipo", "empresa"), ("@doc", "11222333000181")));
        Assert.Equal(2601, ex.Number);
    }

    [Fact]
    public async Task Segunda_matriz_no_mesmo_tenant_falha()
    {
        await using var c = await db.AbrirAsync();
        var s = await NovoTenant(c);
        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            Exec(c, "INSERT INTO unidades (tenant_id, nome, tipo) VALUES (@t, 'Outra matriz', 'matriz')", ("@t", s.Tenant)));
        Assert.Equal(2601, ex.Number);
    }

    [Fact]
    public async Task Movimentacao_do_tenant_A_com_item_do_tenant_B_falha()
    {
        await using var c = await db.AbrirAsync();
        var a = await NovoTenant(c);
        var b = await NovoTenant(c);
        var ex = await Assert.ThrowsAsync<SqlException>(() => Mov(c, a, "doacao_recebida", 1, 1m, item: b.Item));
        Assert.Contains("FK_mov_item", ex.Message);
    }

    [Fact]
    public async Task Movimentacao_processada_e_imutavel_e_nao_pode_ser_excluida()
    {
        await using var c = await db.AbrirAsync();
        var s = await NovoTenant(c);
        var id = await Mov(c, s, "doacao_recebida", 5, 1m);

        var upd = await Assert.ThrowsAsync<SqlException>(() =>
            Exec(c, "UPDATE movimentacoes SET quantidade = 50 WHERE id = @id", ("@id", id)));
        Assert.Equal(50002, upd.Number);

        var del = await Assert.ThrowsAsync<SqlException>(() =>
            Exec(c, "DELETE FROM movimentacoes WHERE id = @id", ("@id", id)));
        Assert.Equal(50004, del.Number);

        await Exec(c, "UPDATE movimentacoes SET status = 'cancelada' WHERE id = @id", ("@id", id));
        var volta = await Assert.ThrowsAsync<SqlException>(() =>
            Exec(c, "UPDATE movimentacoes SET status = 'processada' WHERE id = @id", ("@id", id)));
        Assert.Equal(50003, volta.Number);
    }

    [Fact]
    public async Task Auditoria_registra_usuario_e_ip_e_nao_contem_hash_de_senha()
    {
        await using var c = await db.AbrirAsync();
        var s = await NovoTenant(c);
        await Exec(c, """
            EXEC sp_set_session_context @key = N'usuario_id', @value = @u;
            EXEC sp_set_session_context @key = N'ip', @value = '10.0.0.7';
            UPDATE usuarios SET senha = 'AQAAAAnovoHash', nome = 'U2' WHERE id = @u;
            """, ("@u", s.Usuario));

        var vazou = await Escalar(c, """
            SELECT COUNT(*) FROM logs_auditoria
            WHERE tenant_id = @t AND (dados_novos LIKE '%AQAAAA%' OR dados_antigos LIKE '%AQAAAA%')
            """, ("@t", s.Tenant));
        Assert.Equal(0, vazou);

        var ip = await Escalar(c, """
            SELECT TOP 1 ip_usuario FROM logs_auditoria
            WHERE tabela_afetada = 'usuarios' AND registro_id = @u AND acao = 'UPDATE' AND usuario_id = @u
            ORDER BY id DESC
            """, ("@u", s.Usuario));
        Assert.Equal("10.0.0.7", ip);
    }
}
