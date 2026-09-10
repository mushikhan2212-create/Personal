using CarDealer.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CarDealer.Infrastructure.Persistence.Configurations;

// Conventions from the SQL schema spec section 2: UTC datetime2(3), nvarchar for
// multilingual text, foreign keys and indexes mandatory.

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PublicId).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Slug).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Status).HasConversion<byte>().IsRequired();
        builder.Property(x => x.DefaultCurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(x => x.DefaultCountryCode).HasMaxLength(2).IsFixedLength().IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasIndex(x => x.Slug).IsUnique();
        builder.HasIndex(x => x.PublicId).IsUnique();
    }
}

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.PublicId).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(256).IsRequired();
        builder.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.LastName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Phone).HasMaxLength(32);
        builder.Property(x => x.Status).HasConversion<byte>().IsRequired();
        builder.Property(x => x.LastLoginAtUtc).HasPrecision(3);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasPrecision(3).IsRequired();

        // Globally unique: one identity spans tenants (decision D2).
        builder.HasIndex(x => x.Email).IsUnique();
        builder.HasIndex(x => x.PublicId).IsUnique();
    }
}

public class TenantUserConfiguration : IEntityTypeConfiguration<TenantUser>
{
    public void Configure(EntityTypeBuilder<TenantUser> builder)
    {
        builder.ToTable("TenantUsers");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.MembershipStatus).HasConversion<byte>().IsRequired();
        builder.Property(x => x.JoinedAtUtc).HasPrecision(3);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasOne(x => x.Tenant)
            .WithMany(t => t.Memberships)
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.User)
            .WithMany(u => u.Memberships)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.InvitedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();

        // Drives the tenant picker at login: "which tenants can this user enter?"
        builder.HasIndex(x => new { x.UserId, x.MembershipStatus });
    }
}

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.HasKey(x => x.Id);

        // DELETE /roles/{publicId} is a top-level route (D17). System roles are global
        // rows shared by every tenant, so a sequential id here leaks nothing about one
        // tenant in particular - but the rule is the rule, and an exception is exactly
        // the inconsistency open item O8 was raised about.
        builder.Property(x => x.PublicId).IsRequired();
        builder.HasIndex(x => x.PublicId).IsUnique();

        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(256);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Schema delta section 2.2: Name unique per tenant scope, not globally, so two
        // tenants can each define "Sales Manager". SQL Server treats NULLs as distinct in a
        // unique index, so a plain (TenantId, Name) index would let duplicate SYSTEM roles
        // through - the persisted computed column collapses NULL to 0 and closes that hole.
        builder.Property<long>("TenantScope")
            .HasComputedColumnSql("ISNULL([TenantId], 0)", stored: true);

        builder.HasIndex("TenantScope", nameof(Role.Name)).IsUnique();
    }
}

public class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("UserRoles");
        builder.HasKey(x => new { x.UserId, x.RoleId, x.TenantId });

        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasOne(x => x.User)
            .WithMany(u => u.UserRoles)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.TenantId, x.UserId });
    }
}

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Code).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(256);
        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasIndex(x => x.Code).IsUnique();
    }
}

public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermissions");
        builder.HasKey(x => new { x.RoleId, x.PermissionId });

        builder.Property(x => x.CreatedAtUtc).HasPrecision(3).IsRequired();

        builder.HasOne(x => x.Role)
            .WithMany(r => r.RolePermissions)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Permission)
            .WithMany(p => p.RolePermissions)
            .HasForeignKey(x => x.PermissionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
