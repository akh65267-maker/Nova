using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatalogService.Migrations
{
    /// <inheritdoc />
    public partial class AddCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Name",
                table: "Categories",
                column: "Name",
                unique: true);

            // The categories the shop has always had, plus any a product already uses.
            migrationBuilder.Sql(@"
                INSERT INTO ""Categories"" (""Id"", ""Name"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), name, now()
                FROM (
                    SELECT unnest(ARRAY['Electronics', 'Home & Kitchen', 'Fashion', 'Sports & Outdoors']) AS name
                    UNION
                    SELECT DISTINCT ""Category"" FROM ""Products""
                ) AS names;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Categories");
        }
    }
}
