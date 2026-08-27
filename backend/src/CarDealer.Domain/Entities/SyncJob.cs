using CarDealer.Domain.Common;
using CarDealer.Domain.Enums;

namespace CarDealer.Domain.Entities;

/// <summary>
/// One synchronization run against a source.
/// </summary>
/// <remarks>
/// Master prompt section 8 requires sync logs to show counts, duration, errors and provider
/// status. The counters here are those, and they are what the POC report is built from.
/// </remarks>
public class SyncJob : Entity, IOptionallyTenantScoped
{
    /// <summary>Null for a run against a shared source populating the global catalog.</summary>
    public long? TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    public long VehicleSourceId { get; set; }

    public VehicleSource VehicleSource { get; set; } = null!;

    public SyncJobType JobType { get; set; }

    public SyncJobStatus Status { get; set; } = SyncJobStatus.Pending;

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public int TotalRecords { get; set; }

    public int CreatedRecords { get; set; }

    public int UpdatedRecords { get; set; }

    public int FailedRecords { get; set; }

    public string? ErrorMessage { get; set; }

    public ICollection<SyncJobItem> Items { get; set; } = new List<SyncJobItem>();
}
