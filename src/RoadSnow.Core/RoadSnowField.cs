using System;

namespace RoadSnow.Core;

public sealed class RoadSnowField
{
    private readonly float[] _depth;
    private readonly byte[] _meltRates;
    private readonly SnowSettings _settings;
    private float _observedDepth;
    private bool _hasDepth;
    public int Revision { get; private set; }
    public int Columns { get; }
    public int Rows { get; }
    public float WidthMeters { get; }
    public float LengthMeters { get; }
    public float CellWidthMeters => WidthMeters / Columns;
    public float CellLengthMeters => LengthMeters / Rows;

    public RoadSnowField(
        float widthMeters,
        float lengthMeters,
        float cellSizeMeters = 0.15f,
        SnowSettings? settings = null,
        int columnsOverride = 0,
        int rowsOverride = 0
    )
    {
        Guard.Positive(widthMeters, nameof(widthMeters));
        Guard.Positive(lengthMeters, nameof(lengthMeters));
        Guard.Positive(cellSizeMeters, nameof(cellSizeMeters));
        double columns = Math.Ceiling((double)widthMeters / cellSizeMeters);
        double rows = Math.Ceiling((double)lengthMeters / cellSizeMeters);
        if (columnsOverride < 0 || rowsOverride < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columnsOverride));
        }

        if (columnsOverride > 0)
        {
            columns = columnsOverride;
        }

        if (rowsOverride > 0)
        {
            rows = rowsOverride;
        }

        if (columns * rows > 1_048_576 || columns > 1_048_576 || rows > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cellSizeMeters),
                "Patch exceeds 1,048,576 cells."
            );
        }

        Columns = (int)columns;
        Rows = (int)rows;
        WidthMeters = widthMeters;
        LengthMeters = lengthMeters;
        _settings = settings ?? new SnowSettings();
        _depth = new float[Columns * Rows];
        _meltRates = new byte[_depth.Length];
        for (int z = 0; z < Rows; z++)
        {
            for (int x = 0; x < Columns; x++)
            {
                float px = (x + 0.5f) * CellWidthMeters;
                float pz = (z + 0.5f) * CellLengthMeters;
                float distanceToCurb = Math.Min(px, WidthMeters - px);
                double curb =
                    _settings.CurbRetentionWidthMeters > 0
                        ? 1 - Math.Min(1, distanceToCurb / _settings.CurbRetentionWidthMeters)
                        : 0;
                curb = curb * curb * (3 - 2 * curb);
                int edge = px < WidthMeters / 2 ? 0 : 1;
                double alongRoad = (double)pz + _settings.PatternOffsetMeters;
                double noise =
                    0.65 * ValueNoise(alongRoad / 2.8, edge)
                    + 0.35 * ValueNoise(alongRoad / 0.65 + px * 1.8, edge + 17);
                double retainedRate = 0.15 + 0.65 * noise;
                _meltRates[z * Columns + x] = (byte)
                    Math.Round(255 * (1 - curb * (1 - retainedRate)));
            }
        }
    }

    public void Advance(WeatherSample weather, float simulatedHours)
    {
        Guard.NonNegative(simulatedHours, nameof(simulatedHours));
        if (simulatedHours == 0)
        {
            return;
        }

        double addition = weather.TemperatureCelsius <= 0 ? weather.SnowfallMmPerHour : 0;
        double melting =
            weather.TemperatureCelsius > 0
                ? weather.TemperatureCelsius * (double)_settings.MeltMmPerDegreeHour
                    + weather.RainfallMmPerHour * (double)_settings.RainMeltMultiplier
                : 0;
        if (addition == 0 && (!_hasDepth || melting == 0))
        {
            return;
        }

        bool changed = false;
        bool anyDepth = false;
        for (int i = 0; i < _depth.Length; i++)
        {
            double delta = (addition - melting * (_meltRates[i] / 255.0)) * simulatedHours;
            float next = (float)Math.Max(0, Math.Min(_settings.MaximumDepthMm, _depth[i] + delta));
            changed |= next != _depth[i];
            anyDepth |= next > 0;
            _depth[i] = next;
        }

        _hasDepth = anyDepth;
        if (changed)
        {
            Revision++;
        }
    }

    public bool StampWheelPath(float fromX, float fromZ, float toX, float toZ)
    {
        Guard.Finite(fromX, nameof(fromX));
        Guard.Finite(fromZ, nameof(fromZ));
        Guard.Finite(toX, nameof(toX));
        Guard.Finite(toZ, nameof(toZ));
        if (!_hasDepth || _settings.TireRemovalFraction == 0)
        {
            return false;
        }

        double radius = _settings.TireWidthMeters / 2.0;
        double minX = Math.Min(fromX, toX) - radius;
        double maxX = Math.Max(fromX, toX) + radius;
        double minZ = Math.Min(fromZ, toZ) - radius;
        double maxZ = Math.Max(fromZ, toZ) + radius;
        if (maxX < 0 || minX >= WidthMeters || maxZ < 0 || minZ >= LengthMeters)
        {
            return false;
        }

        int startX = (int)Math.Max(0, Math.Min(Columns - 1, Math.Floor(minX / CellWidthMeters)));
        int endX = (int)Math.Max(0, Math.Min(Columns - 1, Math.Floor(maxX / CellWidthMeters)));
        int startZ = (int)Math.Max(0, Math.Min(Rows - 1, Math.Floor(minZ / CellLengthMeters)));
        int endZ = (int)Math.Max(0, Math.Min(Rows - 1, Math.Floor(maxZ / CellLengthMeters)));
        double dx = (double)toX - fromX;
        double dz = (double)toZ - fromZ;
        double lengthSquared = dx * dx + dz * dz;
        bool changed = false;
        for (int z = startZ; z <= endZ; z++)
        {
            for (int x = startX; x <= endX; x++)
            {
                double px = (x + 0.5) * CellWidthMeters;
                double pz = (z + 0.5) * CellLengthMeters;
                double t =
                    lengthSquared > 0
                        ? Math.Max(
                            0,
                            Math.Min(1, ((px - fromX) * dx + (pz - fromZ) * dz) / lengthSquared)
                        )
                        : 0;
                double ox = px - (fromX + t * dx);
                double oz = pz - (fromZ + t * dz);
                double distance = Math.Sqrt(ox * ox + oz * oz);
                if (distance >= radius)
                {
                    continue;
                }

                double u = distance / radius;
                double strength = 1 - u * u * (3 - 2 * u);
                int index = z * Columns + x;
                double compacted = Math.Min(_depth[index], _settings.FullCoverageDepthMm);
                double target = compacted * (1 - _settings.TireRemovalFraction);
                float next = (float)(compacted + (target - compacted) * strength);
                changed |= next != _depth[index];
                _depth[index] = next;
            }
        }

        if (changed)
        {
            Revision++;
        }

        return changed;
    }

    public float GetDepthMm(int column, int row) => _depth[Index(column, row)];

    public bool StampFootprint(float centerX, float centerZ, float forwardX, float forwardZ)
    {
        Guard.Finite(centerX, nameof(centerX));
        Guard.Finite(centerZ, nameof(centerZ));
        Guard.Finite(forwardX, nameof(forwardX));
        Guard.Finite(forwardZ, nameof(forwardZ));
        if (!_hasDepth)
        {
            return false;
        }

        double magnitude = Math.Sqrt((double)forwardX * forwardX + (double)forwardZ * forwardZ);
        if (magnitude < 0.0001 || _settings.FootprintRemovalFraction == 0)
        {
            return false;
        }

        double fx = forwardX / magnitude;
        double fz = forwardZ / magnitude;
        double length = _settings.FootprintLengthMeters;
        double width = _settings.FootprintWidthMeters;
        double radius = Math.Max(length, width);
        if (
            centerX + radius < 0
            || centerX - radius >= WidthMeters
            || centerZ + radius < 0
            || centerZ - radius >= LengthMeters
        )
        {
            return false;
        }

        bool changed = false;
        int startX = (int)
            Math.Max(0, Math.Min(Columns - 1, Math.Floor((centerX - radius) / CellWidthMeters)));
        int endX = (int)
            Math.Max(0, Math.Min(Columns - 1, Math.Floor((centerX + radius) / CellWidthMeters)));
        int startZ = (int)
            Math.Max(0, Math.Min(Rows - 1, Math.Floor((centerZ - radius) / CellLengthMeters)));
        int endZ = (int)
            Math.Max(0, Math.Min(Rows - 1, Math.Floor((centerZ + radius) / CellLengthMeters)));
        for (int z = startZ; z <= endZ; z++)
        {
            for (int x = startX; x <= endX; x++)
            {
                double dx = (x + 0.5) * CellWidthMeters - centerX;
                double dz = (z + 0.5) * CellLengthMeters - centerZ;
                double side = dx * fz - dz * fx;
                double along = dx * fx + dz * fz;
                double sole =
                    Math.Pow(side / (width * 0.5), 2)
                    + Math.Pow((along - length * 0.15) / (length * 0.35), 2);
                double heel =
                    Math.Pow(side / (width * 0.38), 2)
                    + Math.Pow((along + length * 0.32) / (length * 0.18), 2);
                double u = Math.Sqrt(Math.Min(sole, heel));
                if (u >= 1)
                {
                    continue;
                }

                double strength = 1 - u * u * (3 - 2 * u);
                int index = z * Columns + x;
                double compacted = Math.Min(_depth[index], _settings.FullCoverageDepthMm);
                double target = compacted * (1 - _settings.FootprintRemovalFraction);
                float next = (float)(compacted + (target - compacted) * strength);
                changed |= next != _depth[index];
                _depth[index] = next;
            }
        }

        if (changed)
        {
            Revision++;
        }

        return changed;
    }

    public float GetCoverage(int column, int row) =>
        Math.Min(1, GetDepthMm(column, row) / _settings.FullCoverageDepthMm);

    public void ScaleMeltRate(int index, float rate)
    {
        Guard.NonNegative(rate, nameof(rate));
        _meltRates[index] = (byte)Math.Round(Math.Min(1, rate) * _meltRates[index]);
    }

    public bool ApplyTraffic(float[] strengths, float hours, float temperature)
    {
        Guard.NonNegative(hours, nameof(hours));
        Guard.Finite(temperature, nameof(temperature));
        if (strengths.Length != _depth.Length)
        {
            throw new ArgumentException("Traffic mask size mismatch.");
        }

        if (!_hasDepth || hours == 0)
        {
            return false;
        }

        float warmth = Math.Max(0, Math.Min(1, (temperature + 8) / 10));
        float residue =
            Math.Min(_observedDepth, _settings.FullCoverageDepthMm) * (0.8f - warmth * 0.15f);
        bool changed = false;
        for (int i = 0; i < _depth.Length; i++)
        {
            float strength = Math.Max(0, Math.Min(1, strengths[i]));
            if (strength == 0 || _depth[i] <= residue)
            {
                continue;
            }

            float next = residue + (_depth[i] - residue) * (float)Math.Exp(-strength * hours * 6);
            changed |= next != _depth[i];
            _depth[i] = next;
        }

        if (changed)
        {
            Revision++;
        }

        return changed;
    }

    public void CopyCoverageTo(byte[] destination)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (destination.Length != _depth.Length)
        {
            throw new ArgumentException("Mask size must match the patch.", nameof(destination));
        }

        for (int i = 0; i < _depth.Length; i++)
        {
            destination[i] = (byte)
                Math.Round(Math.Min(1, _depth[i] / _settings.FullCoverageDepthMm) * 255);
        }
    }

    public void InitializeDepth(float depthMm)
    {
        Guard.NonNegative(depthMm, nameof(depthMm));
        float value = Math.Min(depthMm, _settings.MaximumDepthMm);
        for (int i = 0; i < _depth.Length; i++)
        {
            _depth[i] = value;
        }

        _observedDepth = depthMm;
        _hasDepth = value > 0;
        Revision++;
    }

    public bool SynchronizeObservedDepth(float depthMm, bool allowAccumulation = true)
    {
        Guard.NonNegative(depthMm, nameof(depthMm));
        float observed = depthMm;
        if (observed == 0)
        {
            if (!_hasDepth && _observedDepth == 0)
            {
                return false;
            }

            bool changed = false;
            for (int i = 0; i < _depth.Length; i++)
            {
                changed |= _depth[i] != 0;
                _depth[i] = 0;
            }

            _observedDepth = 0;
            _hasDepth = false;
            if (changed)
            {
                Revision++;
            }

            return changed;
        }

        float delta = observed - _observedDepth;
        if (Math.Abs(delta) < 0.00001f)
        {
            return false;
        }

        _observedDepth = observed;
        if (delta > 0 && !allowAccumulation)
        {
            return false;
        }

        bool actualChange = false;
        for (int i = 0; i < _depth.Length; i++)
        {
            float meltRate = _meltRates[i] / 255f;
            float change = delta >= 0 ? delta : delta * meltRate;
            float ceiling = Math.Min(_settings.MaximumDepthMm, observed * (2 - meltRate));
            float next = Math.Max(0, Math.Min(ceiling, _depth[i] + change));
            actualChange |= next != _depth[i];
            _depth[i] = next;
        }

        _hasDepth = true;
        if (actualChange)
        {
            Revision++;
        }

        return actualChange;
    }

    private int Index(int column, int row)
    {
        if ((uint)column >= (uint)Columns || (uint)row >= (uint)Rows)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        return row * Columns + column;
    }

    private double ValueNoise(double position, int phase)
    {
        position %= 1_000_000;
        int cell = (int)Math.Floor(position);
        double t = position - cell;
        t = t * t * (3 - 2 * t);
        double a = Hash(cell, phase);
        return a + (Hash(cell + 1, phase) - a) * t;
    }

    private double Hash(int cell, int phase)
    {
        unchecked
        {
            uint value =
                (uint)cell * 374761393u
                + (uint)_settings.PatternSeed * 668265263u
                + (uint)phase * 1274126177u;
            value = (value ^ (value >> 13)) * 1274126177u;
            value ^= value >> 16;
            return (value & 0x00ffffff) / 16777215.0;
        }
    }
}
