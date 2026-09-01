using System.Text.Json;
using CarDealer.Api.Serialization;

namespace CarDealer.IntegrationTests;

/// <summary>
/// Guards the wire format of every ...Utc field the API returns.
/// </summary>
/// <remarks>
/// The bug this exists for: values read back from SQL Server's datetime2 arrive with
/// Kind = Unspecified and serialised without a Z, while values still in memory carried one.
/// A browser reads the first as local time, so "last synced" was wrong by the viewer's offset
/// - silently, and only for the fields that had been to the database.
/// </remarks>
public class UtcDateTimeConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new UtcDateTimeConverter() },
    };

    private static string Write(DateTime value) => JsonSerializer.Serialize(value, Options);

    [Fact]
    public void Unspecified_kind_is_written_as_utc_without_being_shifted()
    {
        // What EF hands back from a datetime2 column.
        var fromDatabase = new DateTime(2026, 9, 1, 14, 33, 50, 992, DateTimeKind.Unspecified);

        // The hour must survive untouched - only the marker is added. Converting instead of
        // stamping would move it by the server's offset, which is the opposite of the fix.
        Assert.Equal("\"2026-09-01T14:33:50.992Z\"", Write(fromDatabase));
    }

    [Fact]
    public void Utc_kind_is_unchanged()
    {
        var inMemory = new DateTime(2026, 9, 1, 14, 48, 50, 19, DateTimeKind.Utc);

        Assert.Equal("\"2026-09-01T14:48:50.019Z\"", Write(inMemory));
    }

    [Fact]
    public void Local_kind_is_converted_rather_than_relabelled()
    {
        var local = new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Local);

        Assert.Equal($"\"{local.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss.fff}Z\"", Write(local));
    }

    [Fact]
    public void Every_kind_serialises_with_an_explicit_zone_marker()
    {
        // The actual contract: a browser calling new Date() on any of these gets UTC, not
        // whatever the viewer's offset happens to be.
        foreach (var kind in new[] { DateTimeKind.Utc, DateTimeKind.Local, DateTimeKind.Unspecified })
        {
            var json = Write(new DateTime(2026, 9, 1, 14, 0, 0, kind));

            Assert.EndsWith("Z\"", json);
        }
    }

    [Fact]
    public void Round_trip_preserves_the_instant()
    {
        var original = new DateTime(2026, 9, 1, 14, 33, 50, 992, DateTimeKind.Unspecified);

        var round = JsonSerializer.Deserialize<DateTime>(Write(original), Options);

        Assert.Equal(DateTimeKind.Utc, round.Kind);
        Assert.Equal(DateTime.SpecifyKind(original, DateTimeKind.Utc), round);
    }
}
