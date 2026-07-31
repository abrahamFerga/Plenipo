using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plenipo.Infrastructure.Persistence.Migrations.Platform
{
    /// <summary>
    /// The row-level-security backstop on the retrieval tables.
    /// <para>
    /// Hybrid search is the only place the platform writes raw SQL, so it is the only place a future
    /// edit could drop a tenant predicate — and there the consequence is a cross-tenant leak. These
    /// policies make the database refuse that, independently of the application code.
    /// </para>
    /// <para>
    /// The policies are PERMISSIVE WHEN UNSET, deliberately: when <c>plenipo.tenant_id</c> is empty
    /// (migrations, ops tooling, a background scope that legitimately spans tenants) the row is
    /// visible. A fail-closed policy would be stronger, but it would also mean any code path that
    /// forgets to publish the session tenant returns zero rows — turning a defence-in-depth layer
    /// into an outage. This shape can only ever remove rows the application already intended to
    /// exclude, so enabling it cannot break a correct caller. <c>FORCE</c> is set so the table owner
    /// (which is how the application connects in single-role deployments) is subject to the policy
    /// too — without it, RLS would be silently inert.
    /// </para>
    /// </summary>
    public partial class AddRagRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION platform.current_tenant() RETURNS uuid
                LANGUAGE sql STABLE AS $$
                    SELECT NULLIF(current_setting('plenipo.tenant_id', true), '')::uuid
                $$;

                ALTER TABLE platform.rag_chunks ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform.rag_chunks FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS rag_chunks_tenant_isolation ON platform.rag_chunks;
                CREATE POLICY rag_chunks_tenant_isolation ON platform.rag_chunks
                    USING (platform.current_tenant() IS NULL OR "TenantId" = platform.current_tenant())
                    WITH CHECK (platform.current_tenant() IS NULL OR "TenantId" = platform.current_tenant());

                ALTER TABLE platform.rag_collections ENABLE ROW LEVEL SECURITY;
                ALTER TABLE platform.rag_collections FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS rag_collections_tenant_isolation ON platform.rag_collections;
                CREATE POLICY rag_collections_tenant_isolation ON platform.rag_collections
                    USING (platform.current_tenant() IS NULL OR "TenantId" = platform.current_tenant())
                    WITH CHECK (platform.current_tenant() IS NULL OR "TenantId" = platform.current_tenant());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS rag_collections_tenant_isolation ON platform.rag_collections;
                ALTER TABLE platform.rag_collections NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE platform.rag_collections DISABLE ROW LEVEL SECURITY;

                DROP POLICY IF EXISTS rag_chunks_tenant_isolation ON platform.rag_chunks;
                ALTER TABLE platform.rag_chunks NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE platform.rag_chunks DISABLE ROW LEVEL SECURITY;

                DROP FUNCTION IF EXISTS platform.current_tenant();
                """);
        }
    }
}
