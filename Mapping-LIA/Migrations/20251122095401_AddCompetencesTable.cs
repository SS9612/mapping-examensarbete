using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mapping_LIA.Migrations
{
    /// <inheritdoc />
    public partial class AddCompetencesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Competences",
                columns: table => new
                {
                    CompetenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Normalized = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AreaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubcategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Confidence = table.Column<double>(type: "float", nullable: false),
                    MatchedType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Competences", x => x.CompetenceId);
                    table.ForeignKey(
                        name: "FK_Competences_Areas_AreaId",
                        column: x => x.AreaId,
                        principalTable: "Areas",
                        principalColumn: "AreaId",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_Competences_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "CategoryId",
                        onDelete: ReferentialAction.NoAction);
                    table.ForeignKey(
                        name: "FK_Competences_Subcategories_SubcategoryId",
                        column: x => x.SubcategoryId,
                        principalTable: "Subcategories",
                        principalColumn: "SubcategoryId",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Competences_AreaId",
                table: "Competences",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_Competences_CategoryId",
                table: "Competences",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Competences_Normalized",
                table: "Competences",
                column: "Normalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Competences_SubcategoryId",
                table: "Competences",
                column: "SubcategoryId");

            // Add check constraint: At least one of AreaId, CategoryId, or SubcategoryId must be non-null
            migrationBuilder.Sql(@"
                ALTER TABLE Competences
                ADD CONSTRAINT CK_Competences_AtLeastOneReference
                CHECK (AreaId IS NOT NULL OR CategoryId IS NOT NULL OR SubcategoryId IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop check constraint first
            migrationBuilder.Sql(@"
                ALTER TABLE Competences
                DROP CONSTRAINT CK_Competences_AtLeastOneReference");

            migrationBuilder.DropTable(
                name: "Competences");
        }
    }
}
