namespace RoadSnow.Core;

public sealed class SnowActivity
{
    public bool Active { get; private set; }

    public void Restore(bool active) => Active = active;

    public bool Update(
        bool winter,
        bool snowing,
        bool snapshotReady,
        bool nativeSnow,
        bool residualSnow
    )
    {
        if (snowing || winter && snapshotReady && nativeSnow)
            Active = true;
        else if (snapshotReady && !nativeSnow && !residualSnow)
            Active = false;
        return Active;
    }
}
