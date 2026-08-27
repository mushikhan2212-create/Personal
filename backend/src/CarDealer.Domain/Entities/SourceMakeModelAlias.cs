using CarDealer.Domain.Common;

namespace CarDealer.Domain.Entities;

/// <summary>
/// Maps a raw make/model string from a source onto the canonical
/// <see cref="Entities.Make"/>/<see cref="Entities.Model"/>
/// (docs/spec/03-canonical-vehicle-model.md section 7).
/// </summary>
/// <remarks>
/// This is where Japanese to English normalization lives.
///
/// An unmapped alias must NOT drop the vehicle. Normalize what maps, leave MakeId/ModelId
/// null otherwise: the vehicle stays in the catalog and stays searchable by its raw text, it
/// is simply absent from facets until someone adds the alias. Dropping unmapped vehicles
/// would silently lose inventory.
/// </remarks>
public class SourceMakeModelAlias : Entity
{
    /// <summary>Null means the alias applies to every source.</summary>
    public long? VehicleSourceId { get; set; }

    public VehicleSource? VehicleSource { get; set; }

    public string RawMake { get; set; } = string.Empty;

    /// <summary>Null maps the make alone, leaving the model unresolved.</summary>
    public string? RawModel { get; set; }

    public int? MakeId { get; set; }

    public Make? Make { get; set; }

    public int? ModelId { get; set; }

    public Model? Model { get; set; }
}
