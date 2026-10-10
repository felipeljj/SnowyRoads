using System;

namespace RoadSnow.Core;

public sealed class FootstepPath
{
    private readonly double _spacing;
    private readonly double _halfTrack;
    private double _distanceToNext;
    private bool _isLeftFoot = true;

    public FootstepPath(float spacingMeters = 0.55f, float halfTrackMeters = 0.08f)
    {
        Guard.Positive(spacingMeters, nameof(spacingMeters));
        Guard.NonNegative(halfTrackMeters, nameof(halfTrackMeters));
        _spacing = spacingMeters;
        _halfTrack = halfTrackMeters;
        _distanceToNext = _spacing * 0.5;
    }

    public void Advance(float fromX, float fromZ, float toX, float toZ, Action<Footprint> stamp)
    {
        Guard.Finite(fromX, nameof(fromX));
        Guard.Finite(fromZ, nameof(fromZ));
        Guard.Finite(toX, nameof(toX));
        Guard.Finite(toZ, nameof(toZ));
        if (stamp == null)
        {
            throw new ArgumentNullException(nameof(stamp));
        }

        double deltaX = (double)toX - fromX;
        double deltaZ = (double)toZ - fromZ;
        double distance = Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
        if (distance < 0.000001)
        {
            return;
        }

        if (distance / _spacing > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(toX), "Too many steps in one interval.");
        }

        double forwardX = deltaX / distance;
        double forwardZ = deltaZ / distance;
        double distanceAlongPath = _distanceToNext;
        while (distanceAlongPath <= distance)
        {
            double progress = distanceAlongPath / distance;
            double footOffset = _isLeftFoot ? -_halfTrack : _halfTrack;
            stamp(
                new Footprint(
                    (float)(fromX + deltaX * progress + forwardZ * footOffset),
                    (float)(fromZ + deltaZ * progress - forwardX * footOffset),
                    (float)forwardX,
                    (float)forwardZ,
                    (float)progress,
                    _isLeftFoot
                )
            );
            _isLeftFoot = !_isLeftFoot;
            distanceAlongPath += _spacing;
        }

        _distanceToNext = distanceAlongPath - distance;
    }
}

public readonly struct Footprint
{
    public float X { get; }
    public float Z { get; }
    public float ForwardX { get; }
    public float ForwardZ { get; }
    public float IntervalFraction { get; }
    public bool LeftFoot { get; }

    public Footprint(
        float x,
        float z,
        float forwardX,
        float forwardZ,
        float fraction,
        bool leftFoot
    )
    {
        X = x;
        Z = z;
        ForwardX = forwardX;
        ForwardZ = forwardZ;
        IntervalFraction = fraction;
        LeftFoot = leftFoot;
    }
}
