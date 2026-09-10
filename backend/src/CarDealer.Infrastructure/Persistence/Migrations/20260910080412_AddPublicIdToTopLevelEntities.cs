using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarDealer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicIdToTopLevelEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "VehicleMergeHistory",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "VehicleMatchCandidates",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "Roles",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "PublicId",
                table: "RequirementAlerts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every existing row gets its own identifier before the unique indexes below exist.
            //
            // EF's AddColumn fills a non-nullable Guid with all-zeros, which is the same value
            // for every row - so creating the unique index first would fail on any table that
            // already holds more than one. NEWID() rather than NEWSEQUENTIALID() on purpose:
            // a sequential GUID is guessable from its neighbours, which would reintroduce the
            // enumerability this migration exists to remove (open item O8, decision D17).
            migrationBuilder.Sql("""
                SET QUOTED_IDENTIFIER ON;

                UPDATE [VehicleMergeHistory] SET [PublicId] = NEWID();
                UPDATE [VehicleMatchCandidates] SET [PublicId] = NEWID();
                UPDATE [Roles] SET [PublicId] = NEWID();
                UPDATE [RequirementAlerts] SET [PublicId] = NEWID();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleMergeHistory_PublicId",
                table: "VehicleMergeHistory",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleMatchCandidates_PublicId",
                table: "VehicleMatchCandidates",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_PublicId",
                table: "Roles",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequirementAlerts_PublicId",
                table: "RequirementAlerts",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VehicleMergeHistory_PublicId",
                table: "VehicleMergeHistory");

            migrationBuilder.DropIndex(
                name: "IX_VehicleMatchCandidates_PublicId",
                table: "VehicleMatchCandidates");

            migrationBuilder.DropIndex(
                name: "IX_Roles_PublicId",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_RequirementAlerts_PublicId",
                table: "RequirementAlerts");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "VehicleMergeHistory");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "VehicleMatchCandidates");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "RequirementAlerts");
        }
    }
}
