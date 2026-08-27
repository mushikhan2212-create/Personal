using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarDealer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase05Catalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExchangeRates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BaseCurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    QuoteCurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(18,8)", precision: 18, scale: 8, nullable: false),
                    AsOfUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeRates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Makes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CountryCode = table.Column<string>(type: "nchar(2)", fixedLength: true, maxLength: 2, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Makes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VehicleSources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: true),
                    TenantScope = table.Column<long>(type: "bigint", nullable: false, computedColumnSql: "ISNULL([TenantId], 0)", stored: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProviderType = table.Column<byte>(type: "tinyint", nullable: false),
                    SourceType = table.Column<byte>(type: "tinyint", nullable: false),
                    BaseUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    IsShared = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleSources_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MakeId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Models_Makes_MakeId",
                        column: x => x.MakeId,
                        principalTable: "Makes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: true),
                    VehicleSourceId = table.Column<long>(type: "bigint", nullable: false),
                    JobType = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    TotalRecords = table.Column<int>(type: "int", nullable: false),
                    CreatedRecords = table.Column<int>(type: "int", nullable: false),
                    UpdatedRecords = table.Column<int>(type: "int", nullable: false),
                    FailedRecords = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncJobs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SyncJobs_VehicleSources_VehicleSourceId",
                        column: x => x.VehicleSourceId,
                        principalTable: "VehicleSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VehicleSourceConfigurations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    VehicleSourceId = table.Column<long>(type: "bigint", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CredentialReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SyncEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SyncIntervalMinutes = table.Column<int>(type: "int", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastFailureAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleSourceConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleSourceConfigurations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VehicleSourceConfigurations_VehicleSources_VehicleSourceId",
                        column: x => x.VehicleSourceId,
                        principalTable: "VehicleSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceMakeModelAliases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VehicleSourceId = table.Column<long>(type: "bigint", nullable: true),
                    RawMake = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RawModel = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    MakeId = table.Column<int>(type: "int", nullable: true),
                    ModelId = table.Column<int>(type: "int", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceMakeModelAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceMakeModelAliases_Makes_MakeId",
                        column: x => x.MakeId,
                        principalTable: "Makes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceMakeModelAliases_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceMakeModelAliases_VehicleSources_VehicleSourceId",
                        column: x => x.VehicleSourceId,
                        principalTable: "VehicleSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Vehicles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: true),
                    TenantScope = table.Column<long>(type: "bigint", nullable: false, computedColumnSql: "ISNULL([TenantId], 0)", stored: true),
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Make = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    MakeId = table.Column<int>(type: "int", nullable: true),
                    ModelId = table.Column<int>(type: "int", nullable: true),
                    Variant = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ModelYear = table.Column<int>(type: "int", nullable: true),
                    RegistrationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    BodyType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Engine = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    EngineDisplacementCc = table.Column<int>(type: "int", nullable: true),
                    FuelType = table.Column<byte>(type: "tinyint", nullable: false),
                    Transmission = table.Column<byte>(type: "tinyint", nullable: false),
                    Drivetrain = table.Column<byte>(type: "tinyint", nullable: false),
                    SteeringSide = table.Column<byte>(type: "tinyint", nullable: false),
                    Doors = table.Column<byte>(type: "tinyint", nullable: true),
                    Seats = table.Column<byte>(type: "tinyint", nullable: true),
                    Mileage = table.Column<int>(type: "int", nullable: true),
                    MileageUnit = table.Column<byte>(type: "tinyint", nullable: false),
                    ExteriorColor = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    InteriorColor = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Condition = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AuctionGrade = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    InteriorGrade = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    InspectionScore = table.Column<decimal>(type: "decimal(3,1)", precision: 3, scale: 1, nullable: true),
                    Vin = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ChassisNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LotNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CanonicalHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CanonicalHashSource = table.Column<byte>(type: "tinyint", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Vehicles_Makes_MakeId",
                        column: x => x.MakeId,
                        principalTable: "Makes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Vehicles_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Vehicles_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SyncJobItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SyncJobId = table.Column<long>(type: "bigint", nullable: false),
                    ExternalListingId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncJobItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncJobItems_SyncJobs_SyncJobId",
                        column: x => x.SyncJobId,
                        principalTable: "SyncJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantVehicles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    VehicleId = table.Column<long>(type: "bigint", nullable: false),
                    TenantPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    TenantCurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    TenantStatus = table.Column<byte>(type: "tinyint", nullable: true),
                    IsHidden = table.Column<bool>(type: "bit", nullable: false),
                    IsPinned = table.Column<bool>(type: "bit", nullable: false),
                    InternalNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantVehicles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantVehicles_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TenantVehicles_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehicleImages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: true),
                    TenantScope = table.Column<long>(type: "bigint", nullable: false, computedColumnSql: "ISNULL([TenantId], 0)", stored: true),
                    VehicleId = table.Column<long>(type: "bigint", nullable: false),
                    ImageUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    ImageType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SourceImageId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleImages_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VehicleImages_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehicleListings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<long>(type: "bigint", nullable: true),
                    TenantScope = table.Column<long>(type: "bigint", nullable: false, computedColumnSql: "ISNULL([TenantId], 0)", stored: true),
                    VehicleId = table.Column<long>(type: "bigint", nullable: false),
                    VehicleSourceId = table.Column<long>(type: "bigint", nullable: false),
                    ExternalListingId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SourceUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    SourceStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Price = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    PriceType = table.Column<byte>(type: "tinyint", nullable: false),
                    PriceBaseCurrency = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    BaseCurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    ExchangeRateId = table.Column<long>(type: "bigint", nullable: true),
                    FreightCost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    FreightCurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    PortOfLoading = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PortOfDischarge = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LocationCountryCode = table.Column<string>(type: "nchar(2)", fixedLength: true, maxLength: 2, nullable: true),
                    LocationCity = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RawPayload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleListings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleListings_ExchangeRates_ExchangeRateId",
                        column: x => x.ExchangeRateId,
                        principalTable: "ExchangeRates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VehicleListings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VehicleListings_VehicleSources_VehicleSourceId",
                        column: x => x.VehicleSourceId,
                        principalTable: "VehicleSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VehicleListings_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehicleMatchCandidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VehicleId = table.Column<long>(type: "bigint", nullable: false),
                    CandidateVehicleId = table.Column<long>(type: "bigint", nullable: false),
                    Score = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    SignalsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    ReviewedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleMatchCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleMatchCandidates_Users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VehicleMatchCandidates_Vehicles_CandidateVehicleId",
                        column: x => x.CandidateVehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VehicleMatchCandidates_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VehicleMergeHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SurvivingVehicleId = table.Column<long>(type: "bigint", nullable: false),
                    MergedVehicleId = table.Column<long>(type: "bigint", nullable: false),
                    MergedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ReasonsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RepointedListingIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MergedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false),
                    RevertedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: true),
                    RevertedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", precision: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleMergeHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleMergeHistory_Users_MergedByUserId",
                        column: x => x.MergedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VehicleMergeHistory_Users_RevertedByUserId",
                        column: x => x.RevertedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VehicleMergeHistory_Vehicles_MergedVehicleId",
                        column: x => x.MergedVehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VehicleMergeHistory_Vehicles_SurvivingVehicleId",
                        column: x => x.SurvivingVehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VehicleListingImages",
                columns: table => new
                {
                    VehicleListingId = table.Column<long>(type: "bigint", nullable: false),
                    VehicleImageId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleListingImages", x => new { x.VehicleListingId, x.VehicleImageId });
                    table.ForeignKey(
                        name: "FK_VehicleListingImages_VehicleImages_VehicleImageId",
                        column: x => x.VehicleImageId,
                        principalTable: "VehicleImages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VehicleListingImages_VehicleListings_VehicleListingId",
                        column: x => x.VehicleListingId,
                        principalTable: "VehicleListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeRates_Pair_AsOf_Desc",
                table: "ExchangeRates",
                columns: new[] { "BaseCurrencyCode", "QuoteCurrencyCode", "AsOfUtc" },
                unique: true,
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Makes_Name",
                table: "Makes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Models_MakeId_Name",
                table: "Models",
                columns: new[] { "MakeId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceMakeModelAliases_MakeId",
                table: "SourceMakeModelAliases",
                column: "MakeId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceMakeModelAliases_ModelId",
                table: "SourceMakeModelAliases",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceMakeModelAliases_RawMake_RawModel",
                table: "SourceMakeModelAliases",
                columns: new[] { "RawMake", "RawModel" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceMakeModelAliases_VehicleSourceId_RawMake_RawModel",
                table: "SourceMakeModelAliases",
                columns: new[] { "VehicleSourceId", "RawMake", "RawModel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncJobItems_SyncJobId_Status",
                table: "SyncJobItems",
                columns: new[] { "SyncJobId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncJobs_Source_CreatedAt",
                table: "SyncJobs",
                columns: new[] { "VehicleSourceId", "CreatedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_SyncJobs_TenantId",
                table: "SyncJobs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantVehicles_TenantId_IsHidden",
                table: "TenantVehicles",
                columns: new[] { "TenantId", "IsHidden" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantVehicles_TenantId_VehicleId",
                table: "TenantVehicles",
                columns: new[] { "TenantId", "VehicleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantVehicles_VehicleId",
                table: "TenantVehicles",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleImages_TenantId",
                table: "VehicleImages",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleImages_VehicleId_SortOrder",
                table: "VehicleImages",
                columns: new[] { "VehicleId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleListingImages_VehicleImageId",
                table: "VehicleListingImages",
                column: "VehicleImageId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleListings_ExchangeRateId",
                table: "VehicleListings",
                column: "ExchangeRateId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleListings_Scope_BasePrice",
                table: "VehicleListings",
                columns: new[] { "TenantScope", "PriceBaseCurrency", "IsActive" })
                .Annotation("SqlServer:Include", new[] { "VehicleId", "CurrencyCode", "PriceType" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleListings_TenantId",
                table: "VehicleListings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleListings_VehicleId",
                table: "VehicleListings",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleListings_VehicleSourceId",
                table: "VehicleListings",
                column: "VehicleSourceId");

            migrationBuilder.CreateIndex(
                name: "UX_VehicleListings_Scope_Source_External",
                table: "VehicleListings",
                columns: new[] { "TenantScope", "VehicleSourceId", "ExternalListingId" },
                unique: true,
                filter: "[ExternalListingId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleMatchCandidates_CandidateVehicleId",
                table: "VehicleMatchCandidates",
                column: "CandidateVehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleMatchCandidates_ReviewedByUserId",
                table: "VehicleMatchCandidates",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleMatchCandidates_Status_Score",
                table: "VehicleMatchCandidates",
                columns: new[] { "Status", "Score" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleMatchCandidates_VehicleId_CandidateVehicleId",
                table: "VehicleMatchCandidates",
                columns: new[] { "VehicleId", "CandidateVehicleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleMergeHistory_MergedByUserId",
                table: "VehicleMergeHistory",
                column: "MergedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleMergeHistory_MergedVehicleId",
                table: "VehicleMergeHistory",
                column: "MergedVehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleMergeHistory_RevertedByUserId",
                table: "VehicleMergeHistory",
                column: "RevertedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleMergeHistory_SurvivingVehicleId",
                table: "VehicleMergeHistory",
                column: "SurvivingVehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_CanonicalHash",
                table: "Vehicles",
                column: "CanonicalHash",
                filter: "[CanonicalHash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_MakeId",
                table: "Vehicles",
                column: "MakeId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_ModelId",
                table: "Vehicles",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_PublicId",
                table: "Vehicles",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_Scope_Make_Model",
                table: "Vehicles",
                columns: new[] { "TenantScope", "MakeId", "ModelId", "ModelYear", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_TenantId",
                table: "Vehicles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleSourceConfigurations_TenantId_VehicleSourceId",
                table: "VehicleSourceConfigurations",
                columns: new[] { "TenantId", "VehicleSourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleSourceConfigurations_VehicleSourceId",
                table: "VehicleSourceConfigurations",
                column: "VehicleSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleSources_TenantId",
                table: "VehicleSources",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_VehicleSources_Scope_Code",
                table: "VehicleSources",
                columns: new[] { "TenantScope", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceMakeModelAliases");

            migrationBuilder.DropTable(
                name: "SyncJobItems");

            migrationBuilder.DropTable(
                name: "TenantVehicles");

            migrationBuilder.DropTable(
                name: "VehicleListingImages");

            migrationBuilder.DropTable(
                name: "VehicleMatchCandidates");

            migrationBuilder.DropTable(
                name: "VehicleMergeHistory");

            migrationBuilder.DropTable(
                name: "VehicleSourceConfigurations");

            migrationBuilder.DropTable(
                name: "SyncJobs");

            migrationBuilder.DropTable(
                name: "VehicleImages");

            migrationBuilder.DropTable(
                name: "VehicleListings");

            migrationBuilder.DropTable(
                name: "ExchangeRates");

            migrationBuilder.DropTable(
                name: "VehicleSources");

            migrationBuilder.DropTable(
                name: "Vehicles");

            migrationBuilder.DropTable(
                name: "Models");

            migrationBuilder.DropTable(
                name: "Makes");
        }
    }
}
