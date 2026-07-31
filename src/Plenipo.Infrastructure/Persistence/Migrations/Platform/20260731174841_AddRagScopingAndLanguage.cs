using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plenipo.Infrastructure.Persistence.Migrations.Platform
{
    /// <summary>
    /// Makes retrieval multilingual, filterable, and trimmable per principal.
    /// <para>
    /// The delicate part is <c>tsv</c>. It shipped as a GENERATED column pinned to the English
    /// configuration, which cannot vary per row — so it is dropped and re-added as an ordinary
    /// column that ingestion writes with each chunk's own configuration. Existing rows are stamped
    /// <c>english</c> rather than the new <c>simple</c> default, because English is what they were
    /// actually built with: this migration preserves their behaviour instead of silently re-defining
    /// it, and re-indexing them under a detected language is a deliberate re-ingest.
    /// </para>
    /// </summary>
    public partial class AddRagScopingAndLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- rag_collections -------------------------------------------------------------
            migrationBuilder.AddColumn<string>(
                name: "Language",
                schema: "platform",
                table: "rag_collections",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "simple");

            migrationBuilder.AddColumn<List<string>>(
                name: "IndexedLanguages",
                schema: "platform",
                table: "rag_collections",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "metadata",
                schema: "platform",
                table: "rag_collections",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'");

            // --- rag_chunks ------------------------------------------------------------------
            migrationBuilder.AddColumn<string>(
                name: "Language",
                schema: "platform",
                table: "rag_chunks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "simple");

            migrationBuilder.AddColumn<List<string>>(
                name: "Principals",
                schema: "platform",
                table: "rag_chunks",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "metadata",
                schema: "platform",
                table: "rag_chunks",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'");

            // --- agent_profiles ---------------------------------------------------------------
            migrationBuilder.AddColumn<List<string>>(
                name: "CollectionScopes",
                schema: "platform",
                table: "agent_profiles",
                type: "text[]",
                nullable: true);

            // Everything indexed before this migration was analysed as English. Say so, rather than
            // letting the new default quietly relabel it.
            migrationBuilder.Sql("""
                UPDATE platform.rag_chunks SET "Language" = 'english';
                UPDATE platform.rag_collections
                SET "Language" = 'english', "IndexedLanguages" = ARRAY['english']::text[];
                """);

            // tsv: generated-and-English becomes written-and-per-row. Dropping the column drops its
            // index with it, so the GIN index is recreated after the backfill.
            migrationBuilder.Sql("""
                ALTER TABLE platform.rag_chunks DROP COLUMN tsv;
                ALTER TABLE platform.rag_chunks ADD COLUMN tsv tsvector;
                UPDATE platform.rag_chunks SET tsv = to_tsvector(CAST("Language" AS regconfig), "Text");
                CREATE INDEX "IX_rag_chunks_tsv" ON platform.rag_chunks USING GIN (tsv);
                """);

            // The two new retrieval predicates get the index types they need: GIN for array overlap
            // (`&&`) and jsonb containment (`@>`). jsonb_path_ops is the smaller, faster operator
            // class and supports exactly the containment queries the filter API generates.
            migrationBuilder.Sql("""
                CREATE INDEX "IX_rag_chunks_principals" ON platform.rag_chunks USING GIN ("Principals");
                CREATE INDEX "IX_rag_chunks_metadata" ON platform.rag_chunks USING GIN (metadata jsonb_path_ops);
                """);

            // pgvector 0.8 iterative scans: without this, an HNSW index combined with the tenant /
            // collection / ACL predicates silently loses recall (the index returns its k nearest, the
            // filter then throws most of them away). Set database-wide so every session inherits it.
            // Guarded: a deployment on pgvector < 0.8 has no such setting and simply keeps exact scan.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    EXECUTE format(
                        'ALTER DATABASE %I SET hnsw.iterative_scan = ''relaxed_order''',
                        current_database());
                EXCEPTION WHEN OTHERS THEN
                    RAISE NOTICE 'Skipped hnsw.iterative_scan (pgvector < 0.8 or insufficient privilege): %', SQLERRM;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS platform."IX_rag_chunks_metadata";
                DROP INDEX IF EXISTS platform."IX_rag_chunks_principals";
                """);

            // Back to the original generated-English column.
            migrationBuilder.Sql("""
                ALTER TABLE platform.rag_chunks DROP COLUMN tsv;
                ALTER TABLE platform.rag_chunks
                ADD COLUMN tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', "Text")) STORED;
                CREATE INDEX "IX_rag_chunks_tsv" ON platform.rag_chunks USING GIN (tsv);
                """);

            migrationBuilder.DropColumn(name: "CollectionScopes", schema: "platform", table: "agent_profiles");
            migrationBuilder.DropColumn(name: "metadata", schema: "platform", table: "rag_chunks");
            migrationBuilder.DropColumn(name: "Principals", schema: "platform", table: "rag_chunks");
            migrationBuilder.DropColumn(name: "Language", schema: "platform", table: "rag_chunks");
            migrationBuilder.DropColumn(name: "metadata", schema: "platform", table: "rag_collections");
            migrationBuilder.DropColumn(name: "IndexedLanguages", schema: "platform", table: "rag_collections");
            migrationBuilder.DropColumn(name: "Language", schema: "platform", table: "rag_collections");
        }
    }
}
