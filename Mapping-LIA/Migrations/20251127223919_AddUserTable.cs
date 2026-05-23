using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mapping_LIA.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Competences_Areas_AreaId",
                table: "Competences");

            migrationBuilder.DropForeignKey(
                name: "FK_Competences_Categories_CategoryId",
                table: "Competences");

            migrationBuilder.DropForeignKey(
                name: "FK_Competences_Subcategories_SubcategoryId",
                table: "Competences");

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Competences_Areas_AreaId",
                table: "Competences",
                column: "AreaId",
                principalTable: "Areas",
                principalColumn: "AreaId");

            migrationBuilder.AddForeignKey(
                name: "FK_Competences_Categories_CategoryId",
                table: "Competences",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "CategoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Competences_Subcategories_SubcategoryId",
                table: "Competences",
                column: "SubcategoryId",
                principalTable: "Subcategories",
                principalColumn: "SubcategoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Competences_Areas_AreaId",
                table: "Competences");

            migrationBuilder.DropForeignKey(
                name: "FK_Competences_Categories_CategoryId",
                table: "Competences");

            migrationBuilder.DropForeignKey(
                name: "FK_Competences_Subcategories_SubcategoryId",
                table: "Competences");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.AddForeignKey(
                name: "FK_Competences_Areas_AreaId",
                table: "Competences",
                column: "AreaId",
                principalTable: "Areas",
                principalColumn: "AreaId",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Competences_Categories_CategoryId",
                table: "Competences",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "CategoryId",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Competences_Subcategories_SubcategoryId",
                table: "Competences",
                column: "SubcategoryId",
                principalTable: "Subcategories",
                principalColumn: "SubcategoryId",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
