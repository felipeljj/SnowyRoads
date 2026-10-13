using System.Globalization;
using UnityEngine;

namespace RoadSnow.Mod;

public partial class RoadSnowSystem
{
    private int _performanceCalls;
    private int _performanceDryCalls;
    private double _performanceTotal;
    private double _performanceMaximum;
    private float _performanceNext;

    private void RecordPerformance(double milliseconds)
    {
        _performanceCalls++;
        if (_dormant)
            _performanceDryCalls++;
        _performanceTotal += milliseconds;
        _performanceMaximum = System.Math.Max(_performanceMaximum, milliseconds);
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (_performanceNext == 0)
            _performanceNext = now + 20;
        if (now < _performanceNext)
            return;

        // Whole-update timings include discovery and footsteps, unlike tickMs.
        Mod.log.Info(
            string.Format(
                CultureInfo.InvariantCulture,
                "RoadSnow update window: calls={0}, dormant={1}, avgMs={2:F3}, maxMs={3:F3}, nativeSnow={4}, deposits={5}",
                _performanceCalls,
                _performanceDryCalls,
                _performanceTotal / _performanceCalls,
                _performanceMaximum,
                _nativeSnowPresent,
                _plowedSnow.Deposits.Count
            )
        );
        _performanceCalls = _performanceDryCalls = 0;
        _performanceTotal = _performanceMaximum = 0;
        _performanceNext = now + 20;
    }
}
