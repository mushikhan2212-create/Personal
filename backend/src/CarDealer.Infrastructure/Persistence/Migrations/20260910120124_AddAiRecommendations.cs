using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarDealer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiRecommendations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AIRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    InputHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InputMetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OutputMetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TokenUsageJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Cost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AIRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AIRequests_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VehicleRecommendations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerRequirementId = table.Column<long>(type: "bigint", nullable: false),
                    VehicleId = table.Column<long>(type: "bigint", nullable: false),
                    Score = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    ReasonsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Source = table.Column<byte>(type: "tinyint", nullable: false),
                    AIRequestId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleRecommendations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleRecommendations_AIRequests_AIRequestId",
                        column: x => x.AIRequestId,
                        principalTable: "AIRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_VehicleRecommendations_CustomerRequirements_CustomerRequirementId",
                        column: x => x.CustomerRequirementId,
                        principalTable: "CustomerRequirements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VehicleRecommendations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VehicleRecommendations_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AIRequests_TenantId_CreatedAtUtc",
                table: "AIRequests",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AIRequests_TenantId_InputHash_Status",
                table: "AIRequests",
                columns: new[] { "TenantId", "InputHash", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleRecommendations_AIRequestId",
                table: "VehicleRecommendations",
                column: "AIRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleRecommendations_CustomerRequirementId_Rank",
                table: "VehicleRecommendations",
                columns: new[] { "CustomerRequirementId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleRecommendations_CustomerRequirementId_VehicleId",
                table: "VehicleRecommendations",
                columns: new[] { "CustomerRequirementId", "VehicleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleRecommendations_TenantId",
                table: "VehicleRecommendations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleRecommendations_VehicleId",
                table: "VehicleRecommendations",
                column: "VehicleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VehicleRecommendations");

            migrationBuilder.DropTable(
                name: "AIRequests");
        }
    }
}
