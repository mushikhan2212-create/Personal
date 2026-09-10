using CarDealer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarDealer.Infrastructure.Persistence.Configurations;

public class AIRequestConfiguration : IEntityTypeConfiguration<AIRequest>
{
    public void Configure(EntityTypeBuilder<AIRequest> builder)
    {
        builder.ToTable("AIRequests");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Provider).HasMaxLength(32).IsRequired();
        builder.Property(r => r.Model).HasMaxLength(128).IsRequired();
        builder.Property(r => r.Operation).HasMaxLength(32).IsRequired();
        builder.Property(r => r.InputHash).HasMaxLength(64).IsRequired();
        builder.Property(r => r.FailureReason).HasMaxLength(1000);

        // Money, not a float. A rounding error in a cost column is a rounding error in a bill.
        builder.Property(r => r.Cost).HasPrecision(18, 6);

        builder.Property(r => r.CreatedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(r => r.CompletedAtUtc).HasPrecision(3);

        // Restrict for two reasons that happen to agree. Structurally, a cascade here gives SQL
        // Server a second route from Tenant to VehicleRecommendations (via the set-null on
        // AIRequestId) alongside the one through Customer, and it refuses two. Substantively, a
        // spend record that disappears when a tenant is removed is a spend record that cannot
        // be reconciled against a bill which will still arrive.
        builder.HasOne(r => r.Tenant)
            .WithMany()
            .HasForeignKey(r => r.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // The reuse lookup: this tenant's successful answer to this exact question. Filtered on
        // status because a rejected or failed call must never be served as an answer - it is
        // kept for the spend and quality record, not to be replayed.
        builder.HasIndex(r => new { r.TenantId, r.InputHash, r.Status });

        // The spend query: what this tenant has run recently.
        builder.HasIndex(r => new { r.TenantId, r.CreatedAtUtc });
    }
}

public class VehicleRecommendationConfiguration : IEntityTypeConfiguration<VehicleRecommendation>
{
    public void Configure(EntityTypeBuilder<VehicleRecommendation> builder)
    {
        builder.ToTable("VehicleRecommendations");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Score).HasPrecision(5, 4);
        builder.Property(r => r.ReasonsJson).HasMaxLength(2000);
        builder.Property(r => r.CreatedAtUtc).HasPrecision(3).IsRequired();

        // Restrict, matching CustomerNote and for the same structural reason: the cascade from
        // the tenant already arrives through Customer to CustomerRequirement to here, and SQL
        // Server refuses two cascade paths to the same table. The one that carries the erasure
        // guarantee is the requirement's, so this one gives way.
        builder.HasOne(r => r.Tenant)
            .WithMany()
            .HasForeignKey(r => r.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting a customer takes their requirements, and their requirements take the
        // rankings computed against them. Erasure has to reach everything derived from the
        // person (open item O3), and a ranking is derived from what they asked for.
        builder.HasOne(r => r.CustomerRequirement)
            .WithMany()
            .HasForeignKey(r => r.CustomerRequirementId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, matching RequirementAlert: the vehicle belongs to the shared catalogue, and
        // letting a catalogue deletion silently erase what a salesperson was shown would remove
        // the record of a recommendation somebody may have quoted from.
        builder.HasOne(r => r.Vehicle)
            .WithMany()
            .HasForeignKey(r => r.VehicleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.AIRequest)
            .WithMany()
            .HasForeignKey(r => r.AIRequestId)
            .OnDelete(DeleteBehavior.SetNull);

        // One row per car per requirement. A second ranking replaces the first rather than
        // accumulating beside it - two orderings of the same set is not a history, it is an
        // ambiguity about which one the salesperson actually saw.
        builder.HasIndex(r => new { r.CustomerRequirementId, r.VehicleId }).IsUnique();

        // The display query: this requirement's ranking, best first.
        builder.HasIndex(r => new { r.CustomerRequirementId, r.Rank });
    }
}
