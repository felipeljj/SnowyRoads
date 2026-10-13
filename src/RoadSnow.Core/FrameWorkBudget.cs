using System;

namespace RoadSnow.Core;

// Soft CPU deadline, round-robin fairness, and explicit overdue-work metrics.
// An indivisible engine call can exceed the budget; its time remains accounted.
public sealed class FrameWorkBudget
{
    private readonly double[] _interval,
        _due,
        _cost;
    private readonly double[] _admissionCeiling;
    private readonly bool[] _visited;
    private int _cursor;
    private double _now;
    private bool _ready,
        _forced;
    public const double TargetMs = 2.4;
    public double MaximumLateness { get; private set; }
    public int Deferrals { get; private set; }
    public int Forced { get; private set; }

    public FrameWorkBudget(params double[] intervals)
    {
        _interval = intervals;
        _due = new double[intervals.Length];
        _cost = new double[intervals.Length];
        _visited = new bool[intervals.Length];
        _admissionCeiling = new double[intervals.Length];
        for (int i = 0; i < intervals.Length; i++)
        {
            _cost[i] = .1;
            _admissionCeiling[i] = double.PositiveInfinity;
        }
    }

    public void Begin(double now)
    {
        _now = now;
        _forced = false;
        Array.Clear(_visited, 0, _visited.Length);
        if (!_ready)
        {
            for (int i = 0; i < _due.Length; i++)
                _due[i] = now;
            _ready = true;
        }
    }

    public int Next(double spentMs)
    {
        for (int n = 0; n < _due.Length; n++)
        {
            int index = (_cursor + n) % _due.Length;
            if (_visited[index] || _due[index] > _now)
                continue;
            double late = _now - _due[index];
            MaximumLateness = Math.Max(MaximumLateness, late);
            bool force = late >= .5 && !_forced;
            double admission = Math.Min(_cost[index], _admissionCeiling[index]);
            if (spentMs + admission > TargetMs && !force)
            {
                Deferrals++;
                continue;
            }
            if (spentMs + admission > TargetMs)
            {
                _forced = true;
                Forced++;
            }
            _visited[index] = true;
            _cursor = (index + 1) % _due.Length;
            return index;
        }
        return -1;
    }

    public void Finish(int index, double costMs)
    {
        _cost[index] = Math.Max(.01, _cost[index] * .75 + costMs * .25);
        _due[index] = _now + _interval[index];
    }

    public void Reset()
    {
        _ready = false;
        _cursor = 0;
        ResetMetrics();
    }

    public void SetAdmissionCeiling(int index, double costMs) =>
        _admissionCeiling[index] = Math.Max(.01, costMs);

    public void ResetMetrics()
    {
        MaximumLateness = 0;
        Deferrals = Forced = 0;
    }
}
