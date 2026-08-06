using OnStepX.Core.Protocol;
using Xunit;

namespace OnStepX.Core.Tests;

public class Lx200FormatParsingTests
{
    [Theory]
    // sDD*MM, declination in low precision.
    [InlineData("+45*30", 45.5)]
    [InlineData("-45*30", -45.5)]
    [InlineData("+00*00", 0.0)]
    [InlineData("-00*30", -0.5)]
    // sDD*MM:SS, high precision.
    [InlineData("+45*30:36", 45.51)]
    [InlineData("-12*34:56", -12.582222222222222)]
    // sDD*MM:SS.SSS, maximum precision.
    [InlineData("+45*30:36.000", 45.51)]
    // sDD*MM'SS, altitude with apostrophe.
    [InlineData("+45*30'36", 45.51)]
    // HH:MM:SS, right ascension.
    [InlineData("12:30:00", 12.5)]
    [InlineData("23:59:59", 23.999722222222222)]
    // HH:MM.T, low precision with tenths of a minute.
    [InlineData("12:30.0", 12.5)]
    [InlineData("12:30.6", 12.51)]
    // HH:MM:SS.SSSS, maximum precision.
    [InlineData("12:30:00.0000", 12.5)]
    // DDD*MM and DDD*MM'SS, unsigned azimuth.
    [InlineData("180*30", 180.5)]
    [InlineData("359*59'59", 359.99972222222222)]
    // sDDD*MM, rotator.
    [InlineData("+180*30", 180.5)]
    [InlineData("-180*30", -180.5)]
    public void ParsesEveryDocumentedFormat(string text, double expected)
    {
        Assert.True(Lx200Format.TryParse(text, out double value));
        Assert.Equal(expected, value, precision: 9);
    }

    [Theory]
    [InlineData("+45*30#")]
    [InlineData(" +45*30 ")]
    [InlineData("+45*30#  ")]
    public void TrailingHashAndWhitespaceAreTolerated(string text)
    {
        Assert.True(Lx200Format.TryParse(text, out double value));
        Assert.Equal(45.5, value, precision: 9);
    }

    [Fact]
    public void MissingSignIsTreatedAsPositive()
    {
        Assert.True(Lx200Format.TryParse("45*30", out double value));
        Assert.Equal(45.5, value, precision: 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#")]
    [InlineData("abc")]
    [InlineData("+45*xx")]
    // Four fields do not exist in this protocol.
    [InlineData("1:2:3:4")]
    // Minutes and seconds cannot reach 60, a response like this is corrupted.
    [InlineData("+45*60")]
    [InlineData("+45*30:60")]
    [InlineData("12:60:00")]
    public void RejectsMalformedInput(string? text)
    {
        Assert.False(Lx200Format.TryParse(text, out double value));
        Assert.Equal(0, value);
    }
}

public class Lx200FormatWritingTests
{
    [Theory]
    [InlineData(45.5, "+45*30")]
    [InlineData(-45.5, "-45*30")]
    [InlineData(0.0, "+00*00")]
    [InlineData(-0.5, "-00*30")]
    [InlineData(90.0, "+90*00")]
    [InlineData(-90.0, "-90*00")]
    public void FormatDegreesLowMatchesProtocol(double degrees, string expected)
    {
        Assert.Equal(expected, Lx200Format.FormatDegreesLow(degrees));
    }

    [Theory]
    [InlineData(45.51, "+45*30:36")]
    [InlineData(-45.51, "-45*30:36")]
    [InlineData(0.0, "+00*00:00")]
    public void FormatDegreesHighUsesColonSeparator(double degrees, string expected)
    {
        Assert.Equal(expected, Lx200Format.FormatDegreesHigh(degrees));
    }

    [Theory]
    [InlineData(45.51, "+45*30'36")]
    [InlineData(-12.0, "-12*00'00")]
    public void FormatAltitudeHighUsesApostropheSeparator(double degrees, string expected)
    {
        // Altitude uses an apostrophe where declination uses a colon. This
        // is one of the protocol's asymmetries that must be respected when
        // writing.
        Assert.Equal(expected, Lx200Format.FormatAltitudeHigh(degrees));
    }

    [Theory]
    [InlineData(12.5, "12:30:00")]
    [InlineData(0.0, "00:00:00")]
    [InlineData(23.999722222222222, "23:59:59")]
    public void FormatHoursHighMatchesProtocol(double hours, string expected)
    {
        Assert.Equal(expected, Lx200Format.FormatHoursHigh(hours));
    }

    [Theory]
    [InlineData(12.5, "12:30.0")]
    [InlineData(12.51, "12:30.6")]
    public void FormatHoursLowUsesTenthsOfMinute(double hours, string expected)
    {
        Assert.Equal(expected, Lx200Format.FormatHoursLow(hours));
    }

    [Theory]
    [InlineData(180.5, "180*30")]
    [InlineData(0.0, "000*00")]
    [InlineData(5.5, "005*30")]
    public void FormatAzimuthLowPadsToThreeDigitsWithoutSign(double degrees, string expected)
    {
        Assert.Equal(expected, Lx200Format.FormatAzimuthLow(degrees));
    }

    [Theory]
    [InlineData(180.5, "+180*30")]
    [InlineData(-180.5, "-180*30")]
    [InlineData(0.0, "+000*00")]
    public void FormatRotatorAngleKeepsSignAndThreeDigits(double degrees, string expected)
    {
        Assert.Equal(expected, Lx200Format.FormatRotatorAngle(degrees));
    }

    [Fact]
    public void HighestPrecisionDeclinationHasThreeDecimals()
    {
        Assert.Equal("+45*30:36.000", Lx200Format.FormatDegreesHighest(45.51));
    }

    [Fact]
    public void HighestPrecisionRightAscensionHasFourDecimals()
    {
        // :GRH# carries four decimals, one more than :GDH#.
        Assert.Equal("12:30:00.0000", Lx200Format.FormatHoursHighest(12.5));
    }
}

/// <summary>
/// The cases that break a naive floating point implementation.
/// </summary>
public class Lx200FormatCarryTests
{
    [Fact]
    public void HoursJustUnderTwentyFourDoNotOverflowToTwentyFour()
    {
        // With direct rounding this would give "24:00:00", which is not valid.
        Assert.Equal("00:00:00", Lx200Format.FormatHoursHigh(23.99999999));
    }

    [Fact]
    public void AzimuthJustUnderThreeSixtyDoesNotOverflowToThreeSixty()
    {
        // With direct rounding this would give "360*00".
        Assert.Equal("000*00", Lx200Format.FormatAzimuthLow(359.9999));
        Assert.Equal("000*00'00", Lx200Format.FormatAzimuthHigh(359.99999999));
    }

    [Fact]
    public void SecondsRoundingCarriesIntoMinutes()
    {
        // 45 degrees, 30 minutes and 59.6 seconds. Seconds round to 60,
        // and the carry must roll up to 31 minutes instead of writing a
        // ":60".
        double value = 45 + 30.0 / 60 + 59.6 / 3600;

        Assert.Equal("+45*31:00", Lx200Format.FormatDegreesHigh(value));
    }

    [Fact]
    public void MinutesRoundingCarriesIntoDegrees()
    {
        // 45 degrees and 59.7 minutes round to 60, which must roll up to
        // 46 degrees.
        double value = 45 + 59.7 / 60;

        Assert.Equal("+46*00", Lx200Format.FormatDegreesLow(value));
    }

    [Fact]
    public void CarryPropagatesThroughBothLevelsAtOnce()
    {
        // 45 degrees, 59 minutes and 59.6 seconds: chained carry all the
        // way up to degrees.
        double value = 45 + 59.0 / 60 + 59.6 / 3600;

        Assert.Equal("+46*00:00", Lx200Format.FormatDegreesHigh(value));
    }

    [Fact]
    public void NoFormatterEverEmitsSixtyInMinutesOrSeconds()
    {
        // Wide sweep: no value must produce a field of 60.
        for (int i = 0; i < 20000; i++)
        {
            double degrees = -90.0 + i * 180.0 / 20000.0;
            double hours = i * 24.0 / 20000.0;

            AssertNoSixtyField(Lx200Format.FormatDegreesLow(degrees));
            AssertNoSixtyField(Lx200Format.FormatDegreesHigh(degrees));
            AssertNoSixtyField(Lx200Format.FormatDegreesHighest(degrees));
            AssertNoSixtyField(Lx200Format.FormatAltitudeHigh(degrees));
            AssertNoSixtyField(Lx200Format.FormatHoursLow(hours));
            AssertNoSixtyField(Lx200Format.FormatHoursHigh(hours));
            AssertNoSixtyField(Lx200Format.FormatHoursHighest(hours));
            AssertNoSixtyField(Lx200Format.FormatAzimuthLow(hours * 15));
            AssertNoSixtyField(Lx200Format.FormatAzimuthHigh(hours * 15));
        }
    }

    private static void AssertNoSixtyField(string formatted)
    {
        // The parser rejects any minute or second field equal to or
        // greater than 60, so if the formatter emits one, the parser
        // gives it away.
        Assert.True(
            Lx200Format.TryParse(formatted, out _),
            $"The formatter produced a value the parser rejects: {formatted}");
    }

    [Theory]
    [InlineData(24.0, "00:00:00")]
    [InlineData(25.5, "01:30:00")]
    [InlineData(-1.0, "23:00:00")]
    [InlineData(-0.5, "23:30:00")]
    public void HoursAreWrappedIntoRange(double hours, string expected)
    {
        Assert.Equal(expected, Lx200Format.FormatHoursHigh(hours));
    }

    [Theory]
    [InlineData(360.0, "000*00")]
    [InlineData(370.5, "010*30")]
    [InlineData(-10.5, "349*30")]
    public void AzimuthIsWrappedIntoRange(double degrees, string expected)
    {
        Assert.Equal(expected, Lx200Format.FormatAzimuthLow(degrees));
    }
}

/// <summary>
/// Round trip: what is written must be readable again with the same value
/// within the resolution of the format.
/// </summary>
public class Lx200FormatRoundTripTests
{
    [Fact]
    public void DeclinationRoundTripsWithinOneArcsecond()
    {
        for (int i = -900; i <= 900; i++)
        {
            double original = i / 10.0;

            string formatted = Lx200Format.FormatDegreesHigh(original);

            Assert.True(Lx200Format.TryParse(formatted, out double parsed), formatted);
            Assert.Equal(original, parsed, precision: 4);
        }
    }

    [Fact]
    public void RightAscensionRoundTripsWithinOneSecond()
    {
        for (int i = 0; i < 2400; i++)
        {
            double original = i / 100.0;

            string formatted = Lx200Format.FormatHoursHigh(original);

            Assert.True(Lx200Format.TryParse(formatted, out double parsed), formatted);
            // One second of time is 1/3600 of an hour.
            Assert.True(
                Math.Abs(original - parsed) <= 0.5 / 3600.0,
                $"{original} came back as {parsed} from {formatted}");
        }
    }

    [Fact]
    public void HighestPrecisionRoundTripsMuchTighter()
    {
        for (int i = -900; i <= 900; i += 7)
        {
            double original = i / 10.0 + 0.000123;

            string formatted = Lx200Format.FormatDegreesHighest(original);

            Assert.True(Lx200Format.TryParse(formatted, out double parsed), formatted);
            Assert.Equal(original, parsed, precision: 6);
        }
    }
}
