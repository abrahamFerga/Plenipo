using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plenipo.Infrastructure.Persistence.Migrations.Platform
{
    /// <inheritdoc />
    public partial class AddRagCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rag_chunks",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    EmbeddingModel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rag_chunks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rag_collections",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModuleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    EmbeddingModel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rag_collections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rag_chunks_CollectionId_FileId",
                schema: "platform",
                table: "rag_chunks",
                columns: new[] { "CollectionId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_rag_chunks_TenantId_CollectionId",
                schema: "platform",
                table: "rag_chunks",
                columns: new[] { "TenantId", "CollectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_rag_collections_TenantId_ModuleId_ResourceType_ResourceId",
                schema: "platform",
                table: "rag_collections",
                columns: new[] { "TenantId", "ModuleId", "ResourceType", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_rag_collections_TenantId_Name",
                schema: "platform",
                table: "rag_collections",
                columns: new[] { "TenantId", "Name" });

            // The retrieval columns are SQL-only by design (unmapped in EF, see RagChunk): pgvector
            // for the vector arm and a generated tsvector for the full-text arm. The vector column
            // is dimensionless so an embedding-model migration (different dimensions) is a re-embed,
            // not a schema change; small collections use exact scan, so no HNSW index yet — that is
            // a deliberate per-collection decision once one outgrows ~50K chunks.
            // Requires the pgvector extension (dev/CI use the pgvector/pgvector image; on Azure
            // Database for PostgreSQL, allowlist and CREATE EXTENSION "vector" first).
            migrationBuilder.Sql("""
                CREATE EXTENSION IF NOT EXISTS vector;
                ALTER TABLE platform.rag_chunks ADD COLUMN embedding vector;
                ALTER TABLE platform.rag_chunks ADD COLUMN tsv tsvector GENERATED ALWAYS AS (to_tsvector('english', "Text")) STORED;
                CREATE INDEX "IX_rag_chunks_tsv" ON platform.rag_chunks USING GIN (tsv);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rag_chunks",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "rag_collections",
                schema: "platform");
        }
    }
}
