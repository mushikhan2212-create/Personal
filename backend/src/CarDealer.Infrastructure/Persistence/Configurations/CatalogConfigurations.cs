using CarDealer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarDealer.Infrastructure.Persistence.Configurations;

// Phase 0.5 vehicle catalog. Conventions from the SQL schema spec section 2; the shape comes
// from docs/spec/04-schema-delta.md sections 1, 3, 5 and 6 plus
// docs/spec/03-canonical-vehicle-model.md.
//
// TenantScope is a persisted computed column, ISNULL(TenantId, 0), on every table whose
// TenantId is nullable. It exists because SQL Server treats NULLs as DISTINCT in a unique
// index: without it, a unique index over (TenantId, ...) stops constraining exactly the
// global rows it most needs to, and every sync run silently inserts another duplicate
// (docs/spec/04-schema-delta.md section 1.2).

public class VehicleSourceConfigurationMap : IEntityTypeConfiguration<VehicleSource>
{
    public void Configure(EntityTypeBuilder<VehicleSource> builder)
    {
        builder.ToTable("VehicleSources");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Code).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ProviderType).HasConversion<byte>().IsRequired();
        builder.Property(x => x.SourceType).HasConversion<byte>().IsRequired();
        builder.Property(x => x.BaseUrl).HasMaxLength(512);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasPrecision(3).IsRequired();

        builder.Property(x => x.TenantScope)
            .HasComputedColumnSql("ISNULL([TenantId], 0)", stored: true);

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // The base schema's UNIQUE(TenantId, Code) permits duplicate SHARED source codes,
        // because those rows have a null TenantId. Scope-based uniqueness is the fix.
        builder.HasIndex(x => new { x.TenantScope, x.Code })
            .IsUnique()
            .HasDatabaseName("UX_VehicleSources_Scope_Code");
    }
}

public class VehicleSourceConfigurationEntityMap : IEntityTypeConfiguration<VehicleSourceConfiguration>
{
    public void Configure(EntityTypeBuilder<VehicleSourceConfiguration> builder)
    {
        builder.ToTable("VehicleSourceConfigurations");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CredentialReference).HasMaxLength(256);
        builder.Property(x => x.LastError).HasMaxLength(2000);
        builder.Property(x => x.LastSuccessAtUtc).HasPrecision(3);
        builder.Property(x => x.LastFailureAtUtc).HasPrecision(3);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.VehicleSource)
            .WithMany(x => x.Configurations)
            .HasForeignKey(x => x.VehicleSourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.TenantId, x.VehicleSourceId }).IsUnique();
    }
}

public class MakeConfiguration : IEntityTypeConfiguration<Make>
{
    public void Configure(EntityTypeBuilder<Make> builder)
    {
        builder.ToTable("Makes");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(64).IsRequired();
        builder.Property(x => x.CountryCode).HasMaxLength(2).IsFixedLength();

        builder.HasIndex(x => x.Name).IsUnique();
    }
}

public class ModelConfiguration : IEntityTypeConfiguration<Model>
{
    public void Configure(EntityTypeBuilder<Model> builder)
    {
        builder.ToTable("Models");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(64).IsRequired();

        builder.HasOne(x => x.Make)
            .WithMany(x => x.Models)
            .HasForeignKey(x => x.MakeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.MakeId, x.Name }).IsUnique();
    }
}

public class SourceMakeModelAliasConfiguration : IEntityTypeConfiguration<SourceMakeModelAlias>
{
    public void Configure(EntityTypeBuilder<SourceMakeModelAlias> builder)
    {
        builder.ToTable("SourceMakeModelAliases");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.RawMake).HasMaxLength(128).IsRequired();
        builder.Property(x => x.RawModel).HasMaxLength(128);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasOne(x => x.VehicleSource)
            .WithMany()
            .HasForeignKey(x => x.VehicleSourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Make)
            .WithMany()
            .HasForeignKey(x => x.MakeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Model)
            .WithMany()
            .HasForeignKey(x => x.ModelId)
            .OnDelete(DeleteBehavior.Restrict);

        // HasFilter(null) is load-bearing. EF Core's default for a unique index over nullable
        // columns is to add "WHERE [col] IS NOT NULL", which would exclude from the index
        // exactly the rows that matter most here: a null VehicleSourceId means the alias
        // applies to every source, and a null RawModel means it maps the make alone. Under the
        // default filter those rows are not constrained at all, so "TOYOTA -> Toyota" could be
        // inserted globally any number of times.
        //
        // Removing the filter is safe because SQL Server compares NULLs as EQUAL for
        // uniqueness - verified against SQL Server 2022, where a second (NULL, 'TOYOTA', NULL)
        // is rejected. That is the opposite of the ANSI behaviour EF's default emulates.
        builder.HasIndex(x => new { x.VehicleSourceId, x.RawMake, x.RawModel })
            .IsUnique()
            .HasFilter(null);

        builder.HasIndex(x => new { x.RawMake, x.RawModel });
    }
}

public class ExchangeRateConfiguration : IEntityTypeConfiguration<ExchangeRate>
{
    public void Configure(EntityTypeBuilder<ExchangeRate> builder)
    {
        builder.ToTable("ExchangeRates");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.BaseCurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(x => x.QuoteCurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();

        // Wider than money on purpose: FX needs the precision (decision D6).
        builder.Property(x => x.Rate).HasPrecision(18, 8).IsRequired();
        builder.Property(x => x.AsOfUtc).HasPrecision(3).IsRequired();
        builder.Property(x => x.Source).HasMaxLength(64).IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasIndex(x => new { x.BaseCurrencyCode, x.QuoteCurrencyCode, x.AsOfUtc }).IsUnique();
        builder.HasIndex(x => new { x.BaseCurrencyCode, x.QuoteCurrencyCode, x.AsOfUtc })
            .HasDatabaseName("IX_ExchangeRates_Pair_AsOf_Desc")
            .IsDescending(false, false, true);
    }
}

public class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        builder.ToTable("Vehicles");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PublicId).IsRequired();

        builder.Property(x => x.Make).HasMaxLength(128);
        builder.Property(x => x.Model).HasMaxLength(128);
        builder.Property(x => x.Variant).HasMaxLength(128);
        builder.Property(x => x.BodyType).HasMaxLength(64);
        builder.Property(x => x.Engine).HasMaxLength(64);
        builder.Property(x => x.ExteriorColor).HasMaxLength(64);
        builder.Property(x => x.InteriorColor).HasMaxLength(64);
        builder.Property(x => x.Condition).HasMaxLength(64);

        builder.Property(x => x.FuelType).HasConversion<byte>().IsRequired();
        builder.Property(x => x.Transmission).HasConversion<byte>().IsRequired();
        builder.Property(x => x.Drivetrain).HasConversion<byte>().IsRequired();
        builder.Property(x => x.SteeringSide).HasConversion<byte>().IsRequired();
        builder.Property(x => x.MileageUnit).HasConversion<byte>().IsRequired();
        builder.Property(x => x.Status).HasConversion<byte>().IsRequired();
        builder.Property(x => x.CanonicalHashSource).HasConversion<byte?>();

        // Stored verbatim as short strings, not enums: auction houses do not share a grading
        // vocabulary (docs/spec/03-canonical-vehicle-model.md section 4).
        builder.Property(x => x.AuctionGrade).HasMaxLength(8);
        builder.Property(x => x.InteriorGrade).HasMaxLength(4);
        builder.Property(x => x.InspectionScore).HasPrecision(3, 1);

        builder.Property(x => x.Vin).HasMaxLength(64);
        builder.Property(x => x.ChassisNumber).HasMaxLength(64);
        builder.Property(x => x.LotNumber).HasMaxLength(64);
        builder.Property(x => x.CanonicalHash).HasMaxLength(128);

        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasPrecision(3).IsRequired();

        builder.Property(x => x.TenantScope)
            .HasComputedColumnSql("ISNULL([TenantId], 0)", stored: true);

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.CanonicalMake)
            .WithMany()
            .HasForeignKey(x => x.MakeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.CanonicalModel)
            .WithMany()
            .HasForeignKey(x => x.ModelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.PublicId).IsUnique();

        // Filtered so null hashes are excluded: a null hash must never match anything,
        // including another null (docs/spec/04-schema-delta.md section 3.1). CanonicalHash was
        // the only unindexed lookup key in the original design.
        builder.HasIndex(x => x.CanonicalHash)
            .HasDatabaseName("IX_Vehicles_CanonicalHash")
            .HasFilter("[CanonicalHash] IS NOT NULL");

        // Replaces the base schema's Vehicles(TenantId, Make, Model, ModelYear, Status):
        // scope rather than tenant, and normalized ids rather than free text.
        builder.HasIndex(x => new { x.TenantScope, x.MakeId, x.ModelId, x.ModelYear, x.Status })
            .HasDatabaseName("IX_Vehicles_Scope_Make_Model");
    }
}

public class VehicleListingConfiguration : IEntityTypeConfiguration<VehicleListing>
{
    public void Configure(EntityTypeBuilder<VehicleListing> builder)
    {
        builder.ToTable("VehicleListings");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ExternalListingId).HasMaxLength(128);
        builder.Property(x => x.SourceUrl).HasMaxLength(1024);
        builder.Property(x => x.SourceStatus).HasMaxLength(64);

        builder.Property(x => x.Price).HasPrecision(18, 2);
        builder.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength();
        builder.Property(x => x.PriceType).HasConversion<byte>().IsRequired();
        builder.Property(x => x.PriceBaseCurrency).HasPrecision(18, 2);
        builder.Property(x => x.BaseCurrencyCode).HasMaxLength(3).IsFixedLength();

        builder.Property(x => x.FreightCost).HasPrecision(18, 2);
        builder.Property(x => x.FreightCurrencyCode).HasMaxLength(3).IsFixedLength();
        builder.Property(x => x.PortOfLoading).HasMaxLength(64);
        builder.Property(x => x.PortOfDischarge).HasMaxLength(64);
        builder.Property(x => x.LocationCountryCode).HasMaxLength(2).IsFixedLength();
        builder.Property(x => x.LocationCity).HasMaxLength(128);

        builder.Property(x => x.FirstSeenAtUtc).HasPrecision(3).IsRequired();
        builder.Property(x => x.LastSeenAtUtc).HasPrecision(3).IsRequired();
        builder.Property(x => x.LastSyncedAtUtc).HasPrecision(3);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasPrecision(3).IsRequired();

        builder.Property(x => x.TenantScope)
            .HasComputedColumnSql("ISNULL([TenantId], 0)", stored: true);

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Vehicle)
            .WithMany(x => x.Listings)
            .HasForeignKey(x => x.VehicleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.VehicleSource)
            .WithMany()
            .HasForeignKey(x => x.VehicleSourceId)
            .OnDelete(DeleteBehavior.Restrict);

        // Pins the rate used, so historical reports stay stable as rates move (decision D6).
        builder.HasOne(x => x.ExchangeRate)
            .WithMany()
            .HasForeignKey(x => x.ExchangeRateId)
            .OnDelete(DeleteBehavior.Restrict);

        // The duplicate guard for re-sync. Filtered because a listing without an external id
        // cannot be deduplicated by it.
        builder.HasIndex(x => new { x.TenantScope, x.VehicleSourceId, x.ExternalListingId })
            .IsUnique()
            .HasDatabaseName("UX_VehicleListings_Scope_Source_External")
            .HasFilter("[ExternalListingId] IS NOT NULL");

        // Serves cross-currency range search, which is the common query (decision D6).
        builder.HasIndex(x => new { x.TenantScope, x.PriceBaseCurrency, x.IsActive })
            .HasDatabaseName("IX_VehicleListings_Scope_BasePrice")
            .IncludeProperties(x => new { x.VehicleId, x.CurrencyCode, x.PriceType });
    }
}

public class VehicleImageConfiguration : IEntityTypeConfiguration<VehicleImage>
{
    public void Configure(EntityTypeBuilder<VehicleImage> builder)
    {
        builder.ToTable("VehicleImages");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ImageUrl).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.ImageType).HasMaxLength(32);
        builder.Property(x => x.SourceImageId).HasMaxLength(128);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();

        builder.Property(x => x.TenantScope)
            .HasComputedColumnSql("ISNULL([TenantId], 0)", stored: true);

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Vehicle)
            .WithMany(x => x.Images)
            .HasForeignKey(x => x.VehicleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.VehicleId, x.SortOrder });
    }
}

public class VehicleListingImageConfiguration : IEntityTypeConfiguration<VehicleListingImage>
{
    public void Configure(EntityTypeBuilder<VehicleListingImage> builder)
    {
        builder.ToTable("VehicleListingImages");
        builder.HasKey(x => new { x.VehicleListingId, x.VehicleImageId });

        builder.HasOne(x => x.VehicleListing)
            .WithMany()
            .HasForeignKey(x => x.VehicleListingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.VehicleImage)
            .WithMany()
            .HasForeignKey(x => x.VehicleImageId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class TenantVehicleConfiguration : IEntityTypeConfiguration<TenantVehicle>
{
    public void Configure(EntityTypeBuilder<TenantVehicle> builder)
    {
        builder.ToTable("TenantVehicles");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TenantPrice).HasPrecision(18, 2);
        builder.Property(x => x.TenantCurrencyCode).HasMaxLength(3).IsFixedLength();
        builder.Property(x => x.TenantStatus).HasConversion<byte?>();
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Vehicle)
            .WithMany()
            .HasForeignKey(x => x.VehicleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.TenantId, x.VehicleId }).IsUnique();
        builder.HasIndex(x => new { x.TenantId, x.IsHidden });
    }
}

public class VehicleMatchCandidateConfiguration : IEntityTypeConfiguration<VehicleMatchCandidate>
{
    public void Configure(EntityTypeBuilder<VehicleMatchCandidate> builder)
    {
        builder.ToTable("VehicleMatchCandidates");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Score).HasPrecision(5, 4).IsRequired();
        builder.Property(x => x.Status).HasConversion<byte>().IsRequired();
        builder.Property(x => x.ReviewedAtUtc).HasPrecision(3);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasOne(x => x.Vehicle)
            .WithMany()
            .HasForeignKey(x => x.VehicleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.CandidateVehicle)
            .WithMany()
            .HasForeignKey(x => x.CandidateVehicleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.ReviewedByUser)
            .WithMany()
            .HasForeignKey(x => x.ReviewedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Only catches duplicates because pairs are normalized to VehicleId < CandidateVehicleId
        // before insert. Without that normalization each pair is stored twice and this
        // constraint never fires (docs/spec/04-schema-delta.md section 3.2).
        builder.HasIndex(x => new { x.VehicleId, x.CandidateVehicleId }).IsUnique();

        // The review queue: highest-scoring pending suggestions first.
        builder.HasIndex(x => new { x.Status, x.Score })
            .HasDatabaseName("IX_VehicleMatchCandidates_Status_Score")
            .IsDescending(false, true);
    }
}

public class VehicleMergeHistoryConfiguration : IEntityTypeConfiguration<VehicleMergeHistory>
{
    public void Configure(EntityTypeBuilder<VehicleMergeHistory> builder)
    {
        builder.ToTable("VehicleMergeHistory");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MergedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(x => x.RevertedAtUtc).HasPrecision(3);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasOne(x => x.SurvivingVehicle)
            .WithMany()
            .HasForeignKey(x => x.SurvivingVehicleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.MergedVehicle)
            .WithMany()
            .HasForeignKey(x => x.MergedVehicleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.MergedByUser)
            .WithMany()
            .HasForeignKey(x => x.MergedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.RevertedByUser)
            .WithMany()
            .HasForeignKey(x => x.RevertedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.SurvivingVehicleId);
        builder.HasIndex(x => x.MergedVehicleId);
    }
}

public class SyncJobConfiguration : IEntityTypeConfiguration<SyncJob>
{
    public void Configure(EntityTypeBuilder<SyncJob> builder)
    {
        builder.ToTable("SyncJobs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.JobType).HasConversion<byte>().IsRequired();
        builder.Property(x => x.Status).HasConversion<byte>().IsRequired();
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);
        builder.Property(x => x.StartedAtUtc).HasPrecision(3);
        builder.Property(x => x.CompletedAtUtc).HasPrecision(3);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.VehicleSource)
            .WithMany()
            .HasForeignKey(x => x.VehicleSourceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.VehicleSourceId, x.CreatedAtUtc })
            .HasDatabaseName("IX_SyncJobs_Source_CreatedAt")
            .IsDescending(false, true);
    }
}

public class SyncJobItemConfiguration : IEntityTypeConfiguration<SyncJobItem>
{
    public void Configure(EntityTypeBuilder<SyncJobItem> builder)
    {
        builder.ToTable("SyncJobItems");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ExternalListingId).HasMaxLength(128);
        builder.Property(x => x.Status).HasConversion<byte>().IsRequired();
        builder.Property(x => x.ErrorMessage).HasMaxLength(2000);
        builder.Property(x => x.ProcessedAtUtc).HasPrecision(3);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasOne(x => x.SyncJob)
            .WithMany(x => x.Items)
            .HasForeignKey(x => x.SyncJobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.SyncJobId, x.Status });
    }
}
