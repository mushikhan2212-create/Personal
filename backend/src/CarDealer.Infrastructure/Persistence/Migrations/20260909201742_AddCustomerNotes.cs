using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarDealer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerNotes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    EditedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerNotes_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomerNotes_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerNotes_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerNotes_CreatedByUserId",
                table: "CustomerNotes",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerNotes_CustomerId_CreatedAtUtc",
                table: "CustomerNotes",
                columns: new[] { "CustomerId", "CreatedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerNotes_TenantId",
                table: "CustomerNotes",
                column: "TenantId");

            // Everything already written in the single Customers.Notes box becomes this
            // customer's first log entry, rather than being stranded behind a screen that no
            // longer shows it. Dated to when the customer was created, because that is the only
            // honest date available - the old column recorded no time of its own - and authored
            // by nobody, because it recorded no author either and inventing one would put words
            // in a salesperson's mouth.
            //
            // The column itself is deliberately left in place and left populated. Dropping it
            // would destroy the original in the same statement that copies it, and a migration
            // that cannot be checked afterwards is a migration nobody can trust. Nothing writes
            // to it from here on; a later migration can drop it once this has been verified
            // against real data.
            migrationBuilder.Sql("""
                SET QUOTED_IDENTIFIER ON;

                INSERT INTO [CustomerNotes] ([TenantId], [CustomerId], [Body], [CreatedByUserId], [CreatedAtUtc])
                SELECT [TenantId], [Id], LEFT(LTRIM(RTRIM([Notes])), 4000), NULL, [CreatedAtUtc]
                FROM [Customers]
                WHERE [Notes] IS NOT NULL AND LTRIM(RTRIM([Notes])) <> '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerNotes");
        }
    }
}
