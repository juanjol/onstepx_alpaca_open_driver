using OnStepX.Core.Protocol;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// The test strings are built following the exact order in which
/// <c>Status.command.cpp</c> concatenates the characters, so that they are
/// what the firmware would genuinely emit.
/// </summary>
public class MountStatusTests
{
    [Fact]
    public void GemTrackingSiderealAndUnparked()
    {
        // Tracking (no 'n'), no goto ('N'), not parked ('p'), GEM ('E'),
        // west pier ('W'), and the three digits 2, 5, 0.
        var st = MountStatus.Parse("NpEW250");

        Assert.True(st.IsTracking);
        Assert.False(st.IsGotoActive);
        Assert.False(st.IsSlewing);
        Assert.Equal(ParkState.Unparked, st.ParkState);
        Assert.False(st.IsParked);
        Assert.Equal(MountKind.Gem, st.MountKind);
        Assert.Equal(PierSide.West, st.PierSide);
        Assert.Equal(TrackingCompensation.None, st.Compensation);
        Assert.Equal(MountTrackingRate.Sidereal, st.TrackingRate);
        Assert.Equal(MeridianFlipHomeMode.DirectSlew, st.MeridianFlipHomeMode);

        Assert.True(st.TrailingDigitsValid);
        Assert.Equal(2, st.PulseGuideRateSelect);
        Assert.Equal(5, st.GuideRateSelect);
        Assert.Equal(0, st.GeneralErrorCode);
    }

    [Fact]
    public void NotTrackingWithGotoActiveAndHoming()
    {
        // 'n' not tracking, absence of 'N' means goto active, 'p' not
        // parked, 'h' going to home, GEM, east pier.
        var st = MountStatus.Parse("nphET000");

        Assert.False(st.IsTracking);
        Assert.True(st.IsGotoActive);
        Assert.True(st.IsHoming);
        Assert.True(st.IsSlewing);
        Assert.Equal(PierSide.East, st.PierSide);
    }

    [Fact]
    public void ParkedAtHomeWithBuzzerAndAutoFlip()
    {
        var st = MountStatus.Parse("nNPHrzaKo440");

        Assert.False(st.IsTracking);
        Assert.False(st.IsGotoActive);
        Assert.Equal(ParkState.Parked, st.ParkState);
        Assert.True(st.IsParked);
        Assert.True(st.IsAtHome);
        Assert.True(st.BuzzerEnabled);
        Assert.True(st.AutoMeridianFlip);
        Assert.Equal(MountKind.Fork, st.MountKind);
        Assert.Equal(PierSide.None, st.PierSide);
        Assert.Equal(TrackingCompensation.RefractionDualAxis, st.Compensation);
    }

    [Theory]
    [InlineData(ParkState.Unparked, 'p')]
    [InlineData(ParkState.Parking, 'I')]
    [InlineData(ParkState.Parked, 'P')]
    [InlineData(ParkState.ParkFailed, 'F')]
    public void EveryParkStateIsRecognised(ParkState expected, char flag)
    {
        var st = MountStatus.Parse($"N{flag}EW000");

        Assert.Equal(expected, st.ParkState);
    }

    [Fact]
    public void MissingParkFlagYieldsUnknownRatherThanUnparked()
    {
        // Important that "not reported" is not confused with "not parked".
        var st = MountStatus.Parse("NEW000");

        Assert.Equal(ParkState.Unknown, st.ParkState);
        Assert.False(st.IsParked);
    }

    [Theory]
    [InlineData(MountKind.Gem, 'E')]
    [InlineData(MountKind.Fork, 'K')]
    [InlineData(MountKind.AltAzm, 'A')]
    [InlineData(MountKind.AltAlt, 'L')]
    public void EveryMountKindIsRecognised(MountKind expected, char flag)
    {
        Assert.Equal(expected, MountStatus.Parse($"Np{flag}W000").MountKind);
    }

    [Theory]
    [InlineData(PierSide.None, 'o')]
    [InlineData(PierSide.East, 'T')]
    [InlineData(PierSide.West, 'W')]
    public void EveryPierSideIsRecognised(PierSide expected, char flag)
    {
        Assert.Equal(expected, MountStatus.Parse($"NpE{flag}000").PierSide);
    }
}

/// <summary>
/// Compensation is encoded with two paired characters. This is the part of
/// the format that is easiest to misinterpret.
/// </summary>
public class MountStatusCompensationTests
{
    [Fact]
    public void RefractionWithSingleAxisEmitsBothRAndS()
    {
        // RC_REFRACTION writes 'r' and then 's'.
        var st = MountStatus.Parse("NprsEW000");

        Assert.Equal(TrackingCompensation.RefractionSingleAxis, st.Compensation);
    }

    [Fact]
    public void RefractionDualAxisEmitsOnlyR()
    {
        var st = MountStatus.Parse("NprEW000");

        Assert.Equal(TrackingCompensation.RefractionDualAxis, st.Compensation);
    }

    [Fact]
    public void ModelWithSingleAxisEmitsBothTAndS()
    {
        var st = MountStatus.Parse("NptsEW000");

        Assert.Equal(TrackingCompensation.ModelSingleAxis, st.Compensation);
    }

    [Fact]
    public void ModelDualAxisEmitsOnlyT()
    {
        var st = MountStatus.Parse("NptEW000");

        Assert.Equal(TrackingCompensation.ModelDualAxis, st.Compensation);
    }

    [Theory]
    [InlineData("Np(EW000", MountTrackingRate.Lunar)]
    [InlineData("NpOEW000", MountTrackingRate.Solar)]
    [InlineData("NpkEW000", MountTrackingRate.King)]
    [InlineData("NpEW000", MountTrackingRate.Sidereal)]
    public void TrackingRateIsReadableOnlyWithoutCompensation(string raw, MountTrackingRate expected)
    {
        var st = MountStatus.Parse(raw);

        Assert.Equal(TrackingCompensation.None, st.Compensation);
        Assert.Equal(expected, st.TrackingRate);
    }

    [Theory]
    [InlineData("NprEW000")]
    [InlineData("NprsEW000")]
    [InlineData("NptEW000")]
    [InlineData("NptsEW000")]
    public void TrackingRateIsUnknownWhenCompensationIsActive(string raw)
    {
        // The firmware only emits the rate characters inside the
        // rc == RC_NONE branch, so with compensation active the data does
        // not come and it must be queried with :GT#. Returning Sidereal by
        // default here would be making up information.
        var st = MountStatus.Parse(raw);

        Assert.NotEqual(TrackingCompensation.None, st.Compensation);
        Assert.Equal(MountTrackingRate.Unknown, st.TrackingRate);
    }
}

public class MountStatusMeridianAndPecTests
{
    [Theory]
    [InlineData("NpEW000", MeridianFlipHomeMode.DirectSlew)]
    [InlineData("NpvEW000", MeridianFlipHomeMode.VisitHome)]
    [InlineData("NpuEW000", MeridianFlipHomeMode.PauseAtHome)]
    public void MeridianFlipHomeModeDefaultsToDirectSlew(string raw, MeridianFlipHomeMode expected)
    {
        // Without 'v' or 'u' the mode is direct slew. This is an absence
        // with meaning, not missing data.
        Assert.Equal(expected, MountStatus.Parse(raw).MeridianFlipHomeMode);
    }

    [Fact]
    public void WaitingAtHomeIsIndependentOfTheFlipMode()
    {
        var st = MountStatus.Parse("NpwuEW000");

        Assert.True(st.WaitingAtHome);
        Assert.Equal(MeridianFlipHomeMode.PauseAtHome, st.MeridianFlipHomeMode);
    }

    [Theory]
    [InlineData('/', PecState.Ignore)]
    [InlineData(',', PecState.ReadyPlaying)]
    [InlineData('~', PecState.Playing)]
    [InlineData(';', PecState.ReadyRecording)]
    [InlineData('^', PecState.Recording)]
    public void EveryPecStateIsRecognised(char flag, PecState expected)
    {
        var st = MountStatus.Parse($"NpR{flag}EW000");

        Assert.True(st.PecRecorded);
        Assert.Equal(expected, st.PecState);
    }

    [Fact]
    public void PecIsUnknownWhenNotCompiledIn()
    {
        var st = MountStatus.Parse("NpEW000");

        Assert.False(st.PecRecorded);
        Assert.Equal(PecState.Unknown, st.PecState);
    }
}

public class MountStatusTrailingDigitsTests
{
    [Fact]
    public void TrailingDigitsArePositionalNotFlags()
    {
        var st = MountStatus.Parse("NpEW987");

        Assert.True(st.TrailingDigitsValid);
        Assert.Equal(9, st.PulseGuideRateSelect);
        Assert.Equal(8, st.GuideRateSelect);
        Assert.Equal(7, st.GeneralErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Np")]
    [InlineData("NpEW")]
    [InlineData("NpEW1")]
    [InlineData("NpEW12")]
    public void MalformedTrailingDigitsAreFlaggedWithoutLosingTheFlags(string raw)
    {
        var st = MountStatus.Parse(raw);

        Assert.False(st.TrailingDigitsValid);
        Assert.Equal(-1, st.PulseGuideRateSelect);
        Assert.Equal(-1, st.GuideRateSelect);
        Assert.Equal(-1, st.GeneralErrorCode);
    }

    [Fact]
    public void FlagsStillParseWhenTrailingDigitsAreMissing()
    {
        // No protocol flag is a digit, so when the three trailing digits
        // are not found, the whole string can be treated as flags with no
        // risk of confusion.
        var st = MountStatus.Parse("nNPHEW");

        Assert.False(st.TrailingDigitsValid);
        Assert.False(st.IsTracking);
        Assert.Equal(ParkState.Parked, st.ParkState);
        Assert.True(st.IsAtHome);
        Assert.Equal(MountKind.Gem, st.MountKind);
    }

    [Fact]
    public void TrailingHashIsTolerated()
    {
        var st = MountStatus.Parse("NpEW250#");

        Assert.True(st.TrailingDigitsValid);
        Assert.Equal(2, st.PulseGuideRateSelect);
    }

    [Fact]
    public void NullAndEmptyDoNotThrow()
    {
        var fromNull = MountStatus.Parse(null);
        var fromEmpty = MountStatus.Parse("");

        Assert.False(fromNull.TrailingDigitsValid);
        Assert.False(fromEmpty.TrailingDigitsValid);
        Assert.Equal(ParkState.Unknown, fromNull.ParkState);
        Assert.Equal(MountKind.Unknown, fromNull.MountKind);
    }

    [Fact]
    public void RawIsPreservedForDiagnostics()
    {
        Assert.Equal("NpEW250", MountStatus.Parse("NpEW250").Raw);
    }
}

public class MeridianPierSideTests
{
    [Theory]
    // :Gm# uses different letters from :GU# for the same concept.
    [InlineData("N", PierSide.None)]
    [InlineData("E", PierSide.East)]
    [InlineData("W", PierSide.West)]
    [InlineData("N#", PierSide.None)]
    [InlineData("E#", PierSide.East)]
    public void GmUsesItsOwnLetters(string payload, PierSide expected)
    {
        Assert.Equal(expected, MountStatus.ParseMeridianPierSide(payload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("X")]
    public void UnrecognisedMeridianReplyIsUnknown(string? payload)
    {
        Assert.Equal(PierSide.Unknown, MountStatus.ParseMeridianPierSide(payload));
    }

    [Fact]
    public void GuAndGmDisagreeOnLettersForTheSameConcept()
    {
        // Documented trap: in :GU# east is 'T' and in :Gm# it is 'E', which
        // in :GU# means GEM. Mixing the two parsers gives absurd results.
        Assert.Equal(PierSide.East, MountStatus.Parse("NpET000").PierSide);
        Assert.Equal(PierSide.East, MountStatus.ParseMeridianPierSide("E"));
        Assert.Equal(MountKind.Gem, MountStatus.Parse("NpET000").MountKind);
    }
}
