namespace RoadSnow.Core;

public readonly struct WeatherSample
{
    public float TemperatureCelsius { get; }
    public float SnowfallMmPerHour { get; }
    public float RainfallMmPerHour { get; }

    public WeatherSample(
        float temperatureCelsius,
        float snowfallMmPerHour,
        float rainfallMmPerHour = 0
    )
    {
        Guard.Finite(temperatureCelsius, nameof(temperatureCelsius));
        Guard.NonNegative(snowfallMmPerHour, nameof(snowfallMmPerHour));
        Guard.NonNegative(rainfallMmPerHour, nameof(rainfallMmPerHour));
        TemperatureCelsius = temperatureCelsius;
        SnowfallMmPerHour = snowfallMmPerHour;
        RainfallMmPerHour = rainfallMmPerHour;
    }
}
