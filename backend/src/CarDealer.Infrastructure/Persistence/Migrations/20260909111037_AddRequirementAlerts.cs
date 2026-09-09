using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarDealer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRequirementAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequirementAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerRequirementId = table.Column<long>(type: "bigint", nullable: false),
                    VehicleId = table.Column<long>(type: "bigint", nullable: false),
                    MatchedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PriceBaseAtMatch = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    BaseCurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    SeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SeenByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequirementAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RequirementAlerts_CustomerRequirements_CustomerRequirementId",
                        column: x => x.CustomerRequirementId,
                        principalTable: "CustomerRequirements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RequirementAlerts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequirementAlerts_Users_SeenByUserId",
                        column: x => x.SeenByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RequirementAlerts_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequirementAlerts_CustomerRequirementId_VehicleId",
                table: "RequirementAlerts",
                columns: new[] { "CustomerRequirementId", "VehicleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequirementAlerts_SeenByUserId",
                table: "RequirementAlerts",
                column: "SeenByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RequirementAlerts_TenantId_SeenAtUtc_MatchedAtUtc",
                table: "RequirementAlerts",
                columns: new[] { "TenantId", "SeenAtUtc", "MatchedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RequirementAlerts_VehicleId",
                table: "RequirementAlerts",
                column: "VehicleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequirementAlerts");
        }
    }
}
