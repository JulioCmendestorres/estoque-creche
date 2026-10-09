using Estoque.Web.Infrastructure.Tenancy;
using Estoque.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Estoque.Web.Data;

/// <summary>
/// Mapeia o schema existente (database-first manual, sem EF Migrations).
/// Isolamento: filtros globais por tenant (e por unidade nas entidades por unidade)
/// e SaveChanges recusando gravação fora do tenant atual.
/// </summary>
public class EstoqueDbContext(DbContextOptions<EstoqueDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public ITenantContext Tenant { get; } = tenant;

    // Lidas pelos filtros globais: o EF parametriza por instância do contexto.
    private int? TenantAtual => Tenant.TenantId;
    private int? UnidadeAtual => Tenant.UnidadeId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Unidade> Unidades => Set<Unidade>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<UsuarioUnidade> UsuariosUnidades => Set<UsuarioUnidade>();
    public DbSet<UnidadeMedida> UnidadesMedida => Set<UnidadeMedida>();
    public DbSet<Categoria> Categorias => Set<Categoria>();
    public DbSet<Item> Itens => Set<Item>();
    public DbSet<EstoqueUnidade> Estoques => Set<EstoqueUnidade>();
    public DbSet<Parceiro> Parceiros => Set<Parceiro>();
    public DbSet<Movimentacao> Movimentacoes => Set<Movimentacao>();
    public DbSet<LogAuditoria> LogsAuditoria => Set<LogAuditoria>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.Cnpj).HasColumnName("cnpj");
            e.Property(x => x.Ativo).HasColumnName("ativo");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasQueryFilter(x => x.Id == TenantAtual);
        });

        mb.Entity<Unidade>(e =>
        {
            e.ToTable("unidades");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.Tipo).HasColumnName("tipo");
            e.Property(x => x.Ativo).HasColumnName("ativo");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasQueryFilter(x => x.TenantId == TenantAtual);
        });

        mb.Entity<Usuario>(e =>
        {
            e.ToTable("usuarios", t => t.HasTrigger("trg_usuarios_auditoria"));
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.SenhaHash).HasColumnName("senha");
            e.Property(x => x.Papel).HasColumnName("papel");
            e.Property(x => x.Ativo).HasColumnName("ativo");
            e.Property(x => x.TrocarSenha).HasColumnName("trocar_senha");
            e.Property(x => x.UltimoLoginEm).HasColumnName("ultimo_login_em");
            e.Property(x => x.TentativasFalhasLogin).HasColumnName("tentativas_falhas_login");
            e.Property(x => x.BloqueadoAte).HasColumnName("bloqueado_ate");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").ValueGeneratedOnAddOrUpdate();
            e.HasQueryFilter(x => x.TenantId == TenantAtual);
        });

        mb.Entity<UsuarioUnidade>(e =>
        {
            e.ToTable("usuarios_unidades");
            e.HasKey(x => new { x.UsuarioId, x.UnidadeId });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UsuarioId).HasColumnName("usuario_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.HasOne(x => x.Unidade).WithMany().HasForeignKey(x => x.UnidadeId);
            e.HasQueryFilter(x => x.TenantId == TenantAtual);
        });

        mb.Entity<UnidadeMedida>(e =>
        {
            e.ToTable("unidades_medida", t => t.HasTrigger("trg_unidades_medida_auditoria"));
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Codigo).HasColumnName("codigo");
            e.Property(x => x.Descricao).HasColumnName("descricao");
            e.Property(x => x.Ativo).HasColumnName("ativo");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasQueryFilter(x => x.TenantId == TenantAtual);
        });

        mb.Entity<Categoria>(e =>
        {
            e.ToTable("categorias_itens", t => t.HasTrigger("trg_categorias_itens_auditoria"));
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.Descricao).HasColumnName("descricao");
            e.Property(x => x.Ativo).HasColumnName("ativo");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasQueryFilter(x => x.TenantId == TenantAtual);
        });

        mb.Entity<Item>(e =>
        {
            e.ToTable("itens", t => t.HasTrigger("trg_itens_auditoria"));
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CodigoInterno).HasColumnName("codigo_interno");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.Descricao).HasColumnName("descricao");
            e.Property(x => x.CategoriaId).HasColumnName("categoria_id");
            e.Property(x => x.UnidadeMedida).HasColumnName("unidade_medida");
            e.Property(x => x.PrecoEstimado).HasColumnName("preco_estimado").HasPrecision(10, 2);
            e.Property(x => x.OrigemPadrao).HasColumnName("origem_padrao");
            e.Property(x => x.Ativo).HasColumnName("ativo");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").ValueGeneratedOnAddOrUpdate();
            e.HasOne(x => x.Categoria).WithMany().HasForeignKey(x => x.CategoriaId);
            e.HasQueryFilter(x => x.TenantId == TenantAtual);
        });

        mb.Entity<EstoqueUnidade>(e =>
        {
            e.ToTable("estoque_unidade", t =>
            {
                t.HasTrigger("trg_estoque_unidade_auditoria");
                t.HasTrigger("trg_est_sem_delete");
            });
            e.HasKey(x => new { x.UnidadeId, x.ItemId });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.ItemId).HasColumnName("item_id");
            e.Property(x => x.QuantidadeAtual).HasColumnName("quantidade_atual");
            e.Property(x => x.QuantidadeMinima).HasColumnName("quantidade_minima");
            e.Property(x => x.ValorTotalEstoque).HasColumnName("valor_total_estoque").HasPrecision(12, 2);
            e.Property(x => x.AbaixoDoMinimo).HasColumnName("abaixo_do_minimo")
                .HasComputedColumnSql("CAST(CASE WHEN quantidade_atual <= quantidade_minima THEN 1 ELSE 0 END AS BIT)", stored: true);
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").ValueGeneratedOnAddOrUpdate();
            e.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId);
            e.HasQueryFilter(x => x.TenantId == TenantAtual && x.UnidadeId == UnidadeAtual);
        });

        mb.Entity<Parceiro>(e =>
        {
            e.ToTable("parceiros", t => t.HasTrigger("trg_parceiros_auditoria"));
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.Tipo).HasColumnName("tipo");
            e.Property(x => x.Cnpj).HasColumnName("cnpj");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.Telefone).HasColumnName("telefone");
            e.Property(x => x.Endereco).HasColumnName("endereco");
            e.Property(x => x.Ativo).HasColumnName("ativo");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").ValueGeneratedOnAddOrUpdate();
            e.HasQueryFilter(x => x.TenantId == TenantAtual);
        });

        mb.Entity<Movimentacao>(e =>
        {
            e.ToTable("movimentacoes", t =>
            {
                t.HasTrigger("trg_mov_validacao");
                t.HasTrigger("trg_mov_estoque");
                t.HasTrigger("trg_mov_sem_delete");
                t.HasTrigger("trg_movimentacoes_auditoria");
            });
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.NumeroMovimentacao).HasColumnName("numero_movimentacao")
                .ValueGeneratedOnAdd().Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
            e.Property(x => x.TipoMovimentacao).HasColumnName("tipo_movimentacao");
            e.Property(x => x.ItemId).HasColumnName("item_id");
            e.Property(x => x.Quantidade).HasColumnName("quantidade");
            e.Property(x => x.ValorUnitario).HasColumnName("valor_unitario").HasPrecision(10, 2);
            e.Property(x => x.ValorTotal).HasColumnName("valor_total").HasPrecision(12, 2)
                .HasComputedColumnSql("CAST(quantidade * valor_unitario AS DECIMAL(12,2))", stored: true);
            e.Property(x => x.DataMovimentacao).HasColumnName("data_movimentacao");
            e.Property(x => x.ParceiroId).HasColumnName("parceiro_id");
            e.Property(x => x.DescricaoSaida).HasColumnName("descricao_saida");
            e.Property(x => x.Beneficiario).HasColumnName("beneficiario");
            e.Property(x => x.ResponsavelId).HasColumnName("responsavel_id");
            e.Property(x => x.Observacoes).HasColumnName("observacoes");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(x => x.AtualizadoEm).HasColumnName("atualizado_em").ValueGeneratedOnAddOrUpdate();
            e.Ignore(x => x.EhEntrada);
            e.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId);
            e.HasOne(x => x.Parceiro).WithMany().HasForeignKey(x => x.ParceiroId);
            e.HasOne(x => x.Responsavel).WithMany().HasForeignKey(x => x.ResponsavelId);
            e.HasQueryFilter(x => x.TenantId == TenantAtual && x.UnidadeId == UnidadeAtual);
        });

        mb.Entity<LogAuditoria>(e =>
        {
            e.ToTable("logs_auditoria");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UnidadeId).HasColumnName("unidade_id");
            e.Property(x => x.UsuarioId).HasColumnName("usuario_id");
            e.Property(x => x.TabelaAfetada).HasColumnName("tabela_afetada");
            e.Property(x => x.Acao).HasColumnName("acao");
            e.Property(x => x.RegistroId).HasColumnName("registro_id");
            e.Property(x => x.DadosAntigos).HasColumnName("dados_antigos");
            e.Property(x => x.DadosNovos).HasColumnName("dados_novos");
            e.Property(x => x.IpUsuario).HasColumnName("ip_usuario");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasQueryFilter(x => x.TenantId == TenantAtual);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GarantirTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GarantirTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Carimba tenant/unidade nas inserções e recusa qualquer gravação fora do tenant
    /// (ou da unidade, para entidades por unidade) atual.
    /// </summary>
    private void GarantirTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantEntity>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            var tenantAtual = Tenant.TenantId
                ?? throw new InvalidOperationException("Gravação sem tenant definido no contexto.");

            if (entry.State == EntityState.Added && entry.Entity.TenantId == 0)
                entry.Entity.TenantId = tenantAtual;
            if (entry.Entity.TenantId != tenantAtual)
                throw new InvalidOperationException("Tentativa de gravar dados de outro tenant.");

            if (entry.Entity is IUnidadeEntity porUnidade)
            {
                var unidadeAtual = Tenant.UnidadeId
                    ?? throw new InvalidOperationException("Gravação sem unidade definida no contexto.");
                if (entry.State == EntityState.Added && porUnidade.UnidadeId == 0)
                    porUnidade.UnidadeId = unidadeAtual;
                if (porUnidade.UnidadeId != unidadeAtual)
                    throw new InvalidOperationException("Tentativa de gravar dados de outra unidade.");
            }
        }
    }
}
