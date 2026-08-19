using OpenWSR.Ingest;

namespace OpenWSR.App.Tests;

/// <summary>
/// MRMS keys carry the valid time in their name. Reading it wrong makes a fresh composite
/// look stale — or worse, a stale one look fresh.
/// </summary>
public class MrmsClientTests
{
    [Fact]
    public void ParsesTheValidTimeFromARealKey()
    {
        var t = MrmsClient.TimeOf(
            "CONUS/MergedReflectivityQCComposite_00.50/20260819/"
            + "MRMS_MergedReflectivityQCComposite_00.50_20260819-144439.grib2.gz");

        Assert.NotNull(t);
        Assert.Equal(new DateTime(2026, 8, 19, 14, 44, 39, DateTimeKind.Utc), t!.Value);
        Assert.Equal(DateTimeKind.Utc, t.Value.Kind);
    }

    [Fact]
    public void HandlesABareFileNameWithNoPrefix()
    {
        var t = MrmsClient.TimeOf("MRMS_MergedReflectivityQCComposite_00.50_20260101-000000.grib2.gz");
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), t!.Value);
    }

    [Fact]
    public void TheProductNamesOwnUnderscoresDoNotConfuseIt()
    {
        // "_00.50_" sits between the product and the timestamp; the parse anchors on the
        // last dash rather than counting underscores.
        var t = MrmsClient.TimeOf("MRMS_PrecipRate_00.00_20261231-235959.grib2.gz");
        Assert.Equal(new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc), t!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense.grib2.gz")]
    [InlineData("MRMS_Thing_20260819.grib2.gz")]          // no time part
    [InlineData("MRMS_Thing_20261319-000000.grib2.gz")]   // month 13
    [InlineData("MRMS_Thing_ABCDEFGH-IJKLMN.grib2.gz")]   // not digits
    public void RubbishKeysReturnNothingRatherThanGuessing(string key) =>
        Assert.Null(MrmsClient.TimeOf(key));
}
