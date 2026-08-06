using Microsoft.Extensions.Logging.Abstractions;
using OnStepX.Devices;
using Xunit;

namespace OnStepX.Core.Tests;

/// <summary>
/// The whole point of this device is honesty about what it does not know. A zero that
/// looks like a measurement is worse than an exception, because a client will act on it.
/// </summary>
public sealed class OnStepObservingConditionsTests : DeviceTestBase
{
    private OnStepObservingConditions Create() =>
        new(Connection, () => Settings, NullLoggerFactory.Instance);

    [Fact]
    public void AllSensorsPresentAreReported()
    {
        using OnStepObservingConditions weather = Create();
        weather.Connected = true;

        Assert.Equal(14.2, weather.Temperature, precision: 1);
        Assert.Equal(942.5, weather.Pressure, precision: 1);
        Assert.Equal(61.0, weather.Humidity, precision: 1);
        Assert.InRange(weather.DewPoint, 5.0, 14.2);
    }

    /// <summary>
    /// The most important test in this file.
    /// </summary>
    [Fact]
    public void AnAbsentSensorThrowsRatherThanReadingZero()
    {
        Device.Weather.HasPressure = false;

        using OnStepObservingConditions weather = Create();
        weather.Connected = true;

        // Zero hectopascals is not a pressure, it is a missing sensor. Returning it would
        // have a client computing a refraction correction from vacuum.
        Assert.Throws<ASCOM.PropertyNotImplementedException>(() => _ = weather.Pressure);

        // The sensors that are fitted still work.
        Assert.Equal(14.2, weather.Temperature, precision: 1);
        Assert.Equal(61.0, weather.Humidity, precision: 1);
    }

    [Fact]
    public void TheDewPointNeedsBothTemperatureAndHumidity()
    {
        Device.Weather.HasHumidity = false;

        using OnStepObservingConditions weather = Create();
        weather.Connected = true;

        // A dew point of zero against a real temperature reads as an imminent dew alarm,
        // and a safety monitor would close the roof over an invented number.
        Assert.Throws<ASCOM.PropertyNotImplementedException>(() => _ = weather.DewPoint);
        Assert.Throws<ASCOM.PropertyNotImplementedException>(() => _ = weather.Humidity);

        Assert.Equal(14.2, weather.Temperature, precision: 1);
    }

    [Fact]
    public void ConnectingFailsWhenTheFirmwareHasNoSensorsAtAll()
    {
        Device.Weather.HasTemperature = false;
        Device.Weather.HasPressure = false;
        Device.Weather.HasHumidity = false;

        using OnStepObservingConditions weather = Create();

        Assert.Throws<ASCOM.NotConnectedException>(() => weather.Connected = true);
    }

    [Theory]
    [InlineData("CloudCover")]
    [InlineData("RainRate")]
    [InlineData("SkyBrightness")]
    [InlineData("SkyQuality")]
    [InlineData("SkyTemperature")]
    [InlineData("StarFWHM")]
    [InlineData("WindDirection")]
    [InlineData("WindGust")]
    [InlineData("WindSpeed")]
    public void SensorsOnStepDoesNotHaveAllThrow(string property)
    {
        using OnStepObservingConditions weather = Create();
        weather.Connected = true;

        Func<double> read = property switch
        {
            "CloudCover" => () => weather.CloudCover,
            "RainRate" => () => weather.RainRate,
            "SkyBrightness" => () => weather.SkyBrightness,
            "SkyQuality" => () => weather.SkyQuality,
            "SkyTemperature" => () => weather.SkyTemperature,
            "StarFWHM" => () => weather.StarFWHM,
            "WindDirection" => () => weather.WindDirection,
            "WindGust" => () => weather.WindGust,
            _ => () => weather.WindSpeed,
        };

        Assert.Throws<ASCOM.PropertyNotImplementedException>(() => read());
    }

    [Fact]
    public void AveragePeriodAcceptsOnlyZero()
    {
        // The driver reports instantaneous readings, so claiming any averaging would be a
        // lie about the data.
        using OnStepObservingConditions weather = Create();
        weather.Connected = true;

        Assert.Equal(0.0, weather.AveragePeriod);

        weather.AveragePeriod = 0.0;

        Assert.Throws<ASCOM.InvalidValueException>(() => weather.AveragePeriod = 1.0);
    }

    [Fact]
    public void TimeSinceLastUpdateReportsTheAgeOfTheReading()
    {
        using OnStepObservingConditions weather = Create();
        weather.Connected = true;

        double age = weather.TimeSinceLastUpdate(string.Empty);

        Assert.InRange(age, 0.0, 30.0);
    }

    [Fact]
    public void TimeSinceLastUpdateThrowsForAnAbsentSensor()
    {
        Device.Weather.HasPressure = false;

        using OnStepObservingConditions weather = Create();
        weather.Connected = true;

        Assert.Throws<ASCOM.PropertyNotImplementedException>(
            () => weather.TimeSinceLastUpdate("Pressure"));

        // A sensor that is fitted answers normally.
        Assert.InRange(weather.TimeSinceLastUpdate("Temperature"), 0.0, 30.0);
    }

    [Fact]
    public void SensorDescriptionsExistForEverySupportedSensor()
    {
        using OnStepObservingConditions weather = Create();
        weather.Connected = true;

        foreach (string sensor in new[] { "Temperature", "Pressure", "Humidity", "DewPoint" })
        {
            Assert.False(string.IsNullOrWhiteSpace(weather.SensorDescription(sensor)));
        }
    }

    [Fact]
    public void SensorDescriptionThrowsForSomethingOnStepDoesNotMeasure()
    {
        using OnStepObservingConditions weather = Create();
        weather.Connected = true;

        Assert.Throws<ASCOM.PropertyNotImplementedException>(
            () => weather.SensorDescription("WindSpeed"));
    }

    [Fact]
    public void RefreshTakesANewReading()
    {
        using OnStepObservingConditions weather = Create();
        weather.Connected = true;

        Assert.Equal(14.2, weather.Temperature, precision: 1);

        // Change the world behind the driver's back, then refresh.
        Device.Weather.Temperature = -3.5;

        weather.Refresh();

        Assert.Equal(-3.5, weather.Temperature, precision: 1);
    }

    [Fact]
    public void DeviceStateOnlyListsSensorsThatExist()
    {
        // This is what lets a client tell "not fitted" from "reading zero" in one call.
        Device.Weather.HasPressure = false;

        using OnStepObservingConditions weather = Create();
        weather.Connected = true;

        var names = weather.DeviceState.Select(v => v.Name).ToList();

        Assert.Contains("Temperature", names);
        Assert.Contains("Humidity", names);
        Assert.DoesNotContain("Pressure", names);
    }

    [Fact]
    public void NegativeTemperaturesAreReadCorrectly()
    {
        Device.Weather.Temperature = -12.5;

        using OnStepObservingConditions weather = Create();
        weather.Connected = true;

        Assert.Equal(-12.5, weather.Temperature, precision: 1);
    }

    [Fact]
    public void ASensorReadingExactlyZeroIsStillAMeasurement()
    {
        // The distinction the probe relies on: a fitted sensor at freezing answers "0.0"
        // while an absent one answers a bare "0". Confusing the two would drop a real
        // sensor the moment the temperature crossed zero, which is exactly when a dew
        // warning matters most.
        Device.Weather.Temperature = 0.0;

        using OnStepObservingConditions weather = Create();
        weather.Connected = true;

        Assert.Equal(0.0, weather.Temperature, precision: 1);
    }

    [Fact]
    public void ReadingAPropertyWhileDisconnectedThrowsNotConnected()
    {
        using OnStepObservingConditions weather = Create();

        Assert.Throws<ASCOM.NotConnectedException>(() => _ = weather.Temperature);
    }
}
