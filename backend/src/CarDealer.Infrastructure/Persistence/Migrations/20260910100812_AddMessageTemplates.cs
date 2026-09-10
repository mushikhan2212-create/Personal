using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarDealer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageTemplates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageTemplates_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_PublicId",
                table: "MessageTemplates",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_TenantId_Channel_Name",
                table: "MessageTemplates",
                columns: new[] { "TenantId", "Channel", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageTemplates_TenantId_Channel_SortOrder",
                table: "MessageTemplates",
                columns: new[] { "TenantId", "Channel", "SortOrder" });

            // Gives every tenant that already exists the starter set, so the feature is useful
            // the moment it ships rather than presenting an empty list.
            //
            // Written out as literal SQL rather than read from StarterTemplates, because a
            // migration is history: it has to produce the same rows in a year's time as it did
            // today. Reading the constants would make this insert change whenever somebody
            // edits the starters, and a fresh database would then diverge from the one this ran
            // against. New tenants get the current constants from DatabaseSeeder instead.
            //
            // The bodies are single-line with a <<NL>> sentinel that SQL Server expands. The
            // alternative - real newlines inside a C# raw string literal - depends on the
            // closing delimiter's indentation to decide what to strip from every line, and gets
            // silently wrong in a way nobody sees until a customer reads a message with three
            // spaces in front of every bullet.
            migrationBuilder.Sql("""
                SET QUOTED_IDENTIFIER ON;

                INSERT INTO [MessageTemplates]
                    ([TenantId], [PublicId], [Name], [Channel], [Body], [SortOrder],
                     [CreatedAtUtc], [UpdatedAtUtc])
                SELECT
                    t.[Id],
                    NEWID(),
                    s.[Name],
                    N'whatsapp',
                    REPLACE(s.[Body], N'<<NL>>', CHAR(10)),
                    s.[SortOrder],
                    SYSUTCDATETIME(),
                    SYSUTCDATETIME()
                FROM [Tenants] t
                CROSS JOIN (VALUES
                    (N'New car offer', N'Hi {FirstName|there},<<NL>><<NL>>I have a {Vehicle|car} that might suit you:<<NL>><<NL>>• Year: {Year}<<NL>>• Mileage: {Mileage}<<NL>>• Colour: {Colour}<<NL>>• Fuel: {Fuel}<<NL>>• Transmission: {Transmission}<<NL>>• Steering: {Steering}<<NL>><<NL>>Happy to answer any questions.<<NL>>{DealerName}', 10),
                    (N'Price quote', N'Hi {FirstName|there},<<NL>><<NL>>Here is my price for the {Vehicle|car}:<<NL>><<NL>>• Year: {Year}<<NL>>• Mileage: {Mileage}<<NL>>• Colour: {Colour}<<NL>><<NL>>Price: {Price}<<NL>><<NL>>Let me know if you would like me to hold it for you.<<NL>>{DealerName}', 20),
                    (N'Follow-up', N'Hi {FirstName|there},<<NL>><<NL>>Just following up on the {Vehicle|car} I sent you. Is it still of interest, or would you like me to keep looking?<<NL>><<NL>>{DealerName}', 30),
                    (N'Car no longer available', N'Hi {FirstName|there},<<NL>><<NL>>The {Vehicle|car} I sent you has gone, so I have taken it off your list. I will keep looking and send you the next one that fits.<<NL>><<NL>>{DealerName}', 40),
                    (N'Plain message', N'Hi {FirstName|there},<<NL>><<NL>>{DealerName}', 50)
                ) AS s([Name], [Body], [SortOrder]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageTemplates");
        }
    }
}
