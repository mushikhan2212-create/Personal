using System.ComponentModel.DataAnnotations;

namespace CarDealer.Integrations.Carapis;

/// <summary>Configuration for the Carapis adapter.</summary>
/// <remarks>
/// The API key is configuration, never source (master prompt section 14, criterion I1). It
/// reaches this object from the environment or a secret store, by way of
/// <c>VehicleSourceConfigurations.CredentialReference</c>, and must never be written to
/// appsettings or to a log line.
/// </remarks>
public sealed class CarapisOptions
{
    public const string SectionName = "Carapis";

    /// <summary>
    /// No <c>/v2</c> segment - paths are <c>/apix/...</c> and the published version is 1.0.0.
    /// </summary>
    [Required]
    public string BaseUrl { get; set; } = "https://api.carapis.com";

    /// <summary>Sent as <c>X-API-Key</c>. Bearer is also accepted; this is the scheme the vendor's own client uses.</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Source codes the POC is permitted to sync (decision D12). Every request carries one of
    /// these; an unfiltered call is never made.
    /// </summary>
    /// <remarks>
    /// Codes are validated by a count call before being trusted: six sources appear under two
    /// or three codes disagreeing about themselves, and <c>sbt_japan</c> returns nothing where
    /// <c>sbtjapan</c> returns 1,722.
    /// </remarks>
    public List<string> PermittedSourceCodes { get; set; } = ["sbtjapan", "goonet_exchange"];

    [Range(1, 100)]
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// Hard ceiling on pages per sync run. Master prompt section 18 forbids unlimited
    /// synchronization; this is the quota, and it is configuration rather than a constant.
    /// </summary>
    [Range(1, 10_000)]
    public int MaxPagesPerRun { get; set; } = 20;

    [Range(1, 10)]
    public int MaxRetries { get; set; } = 3;

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;
}
