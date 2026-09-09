using CarDealer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarDealer.Infrastructure.Persistence.Configurations;

/// <summary>
/// Customers and the requirements they are shopping against.
/// </summary>
/// <remarks>
/// Indexes are those named in SQL schema spec section 7 - lookup by phone and by email within a
/// tenant, and requirements by customer and status. All three are the queries the CRM screens
/// actually run.
/// </remarks>
public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.PublicId).IsRequired();
        builder.HasIndex(c => c.PublicId).IsUnique();

        builder.Property(c => c.FirstName).HasMaxLength(128);
        builder.Property(c => c.LastName).HasMaxLength(128);
        builder.Property(c => c.Phone).HasMaxLength(32);
        builder.Property(c => c.Email).HasMaxLength(256);
        builder.Property(c => c.CountryCode).HasMaxLength(2);
        builder.Property(c => c.City).HasMaxLength(128);
        builder.Property(c => c.PreferredLanguage).HasMaxLength(16);
        builder.Property(c => c.Notes).HasMaxLength(4000);

        builder.HasOne(c => c.Tenant)
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not Cascade: deleting a user must never take their customers with them.
        // The relationship survives the salesperson leaving, and someone reassigns it.
        builder.HasOne(c => c.AssignedUser)
            .WithMany()
            .HasForeignKey(c => c.AssignedUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting a customer takes their requirements. Erasure has to mean erasure - see the
        // remarks on Customer about O3.
        builder.HasMany(c => c.Requirements)
            .WithOne(r => r.Customer)
            .HasForeignKey(r => r.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        // Not unique: a household shares a phone number and a company shares a reception desk.
        builder.HasIndex(c => new { c.TenantId, c.Phone });
        builder.HasIndex(c => new { c.TenantId, c.Email });
    }
}

/// <summary>
/// The running note log against a customer.
/// </summary>
public class CustomerNoteConfiguration : IEntityTypeConfiguration<CustomerNote>
{
    public void Configure(EntityTypeBuilder<CustomerNote> builder)
    {
        builder.ToTable("CustomerNotes");

        builder.HasKey(n => n.Id);

        // Long enough for a real account of a phone call, bounded so one paste cannot turn a
        // customer row into a document.
        builder.Property(n => n.Body).HasMaxLength(4000).IsRequired();
        builder.Property(n => n.CreatedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(n => n.EditedAtUtc).HasPrecision(3);

        builder.HasOne(n => n.Tenant)
            .WithMany()
            .HasForeignKey(n => n.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting a customer takes their notes. Erasure has to reach everything derived from
        // the person (O3), and a note is the most personal thing here after the phone number.
        builder.HasOne(n => n.Customer)
            .WithMany()
            .HasForeignKey(n => n.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: a salesperson leaving must not delete what they recorded. The note stays
        // and keeps their name on it.
        builder.HasOne(n => n.CreatedByUser)
            .WithMany()
            .HasForeignKey(n => n.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // The only query this table serves: one customer's notes, newest first.
        builder.HasIndex(n => new { n.CustomerId, n.CreatedAtUtc })
            .IsDescending(false, true);
    }
}

public class CustomerRequirementConfiguration : IEntityTypeConfiguration<CustomerRequirement>
{
    public void Configure(EntityTypeBuilder<CustomerRequirement> builder)
    {
        builder.ToTable("CustomerRequirements");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Name).HasMaxLength(128);
        builder.Property(r => r.Make).HasMaxLength(64);
        builder.Property(r => r.Model).HasMaxLength(64);
        builder.Property(r => r.Variant).HasMaxLength(128);
        builder.Property(r => r.BodyType).HasMaxLength(64);
        builder.Property(r => r.ExteriorColor).HasMaxLength(64);
        builder.Property(r => r.CurrencyCode).HasMaxLength(3);
        builder.Property(r => r.DestinationCountryCode).HasMaxLength(2);
        builder.Property(r => r.DestinationCity).HasMaxLength(128);
        builder.Property(r => r.RawRequirementText).HasMaxLength(4000);

        // Money, not floating point: 18,2 matches VehicleListing.Price so a budget and a price
        // are the same kind of number on both sides of a comparison.
        builder.Property(r => r.MinPrice).HasPrecision(18, 2);
        builder.Property(r => r.MaxPrice).HasPrecision(18, 2);

        builder.HasOne(r => r.Tenant)
            .WithMany()
            .HasForeignKey(r => r.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.TenantId, r.CustomerId, r.Status });
    }
}

/// <summary>
/// A match that arrived after the customer asked for it.
/// </summary>
/// <remarks>
/// The unique index is the whole idempotency story. The scan runs on a schedule and can be
/// triggered by hand; without it, every run would raise the same alert again and the inbox
/// would fill with the same car. With it, an alert exists once per requirement and vehicle,
/// ever, and a re-scan is a no-op rather than something to be careful about.
/// </remarks>
public class RequirementAlertConfiguration : IEntityTypeConfiguration<RequirementAlert>
{
    public void Configure(EntityTypeBuilder<RequirementAlert> builder)
    {
        builder.ToTable("RequirementAlerts");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.PriceBaseAtMatch).HasPrecision(18, 2);
        builder.Property(a => a.BaseCurrencyCode).HasMaxLength(3);

        builder.HasOne(a => a.Tenant)
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting a customer deletes their requirements, and their requirements' alerts go
        // with them. Erasure has to reach everything derived from the person (O3).
        builder.HasOne(a => a.CustomerRequirement)
            .WithMany()
            .HasForeignKey(a => a.CustomerRequirementId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict rather than cascade: a vehicle belongs to the shared catalogue, and letting
        // a source deletion silently remove one tenant's alerts would erase the record of what
        // a salesperson was told. The delete path clears these explicitly instead.
        builder.HasOne(a => a.Vehicle)
            .WithMany()
            .HasForeignKey(a => a.VehicleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(a => a.SeenByUser)
            .WithMany()
            .HasForeignKey(a => a.SeenByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.CustomerRequirementId, a.VehicleId }).IsUnique();

        // The inbox query: this tenant's unseen alerts, newest first.
        builder.HasIndex(a => new { a.TenantId, a.SeenAtUtc, a.MatchedAtUtc });
    }
}
