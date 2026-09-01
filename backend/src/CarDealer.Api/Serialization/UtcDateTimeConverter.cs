using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarDealer.Api.Serialization;

/// <summary>
/// Writes every <see cref="DateTime"/> as ISO-8601 in UTC, with the trailing Z.
/// </summary>
/// <remarks>
/// Without this the API emits two different formats for fields that are all named ...Utc,
/// and which one you get depends only on whether the value has been to the database:
///
///   accessTokenExpiresAtUtc  2026-09-01T14:48:50.0193906Z   - built in memory, Kind = Utc
///   createdAtUtc             2026-09-01T14:33:50.992        - read from datetime2, Kind = Unspecified
///
/// SQL Server's datetime2 stores no offset, so EF hands back Unspecified and
/// System.Text.Json faithfully writes no suffix. A browser then reads the second one as
/// local time: `new Date("2026-09-01T14:33:50.992")` in Karachi is 14:33 PKT, five hours
/// off. The field is not wrong in the database, only on the wire.
///
/// Treating Unspecified as UTC is safe here because it is true of every DateTime this API
/// exposes - all of them are ...Utc columns written from IDateTimeProvider.UtcNow. Should a
/// genuine local-time field ever be added, it must carry its own offset type
/// (DateTimeOffset) rather than rely on this.
/// </remarks>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTime().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),

            // Round-tripped through the database. Stamp the Kind rather than convert: the
            // value is already UTC, and ToUniversalTime would shift it by the server's offset.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        writer.WriteStringValue(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }
}
