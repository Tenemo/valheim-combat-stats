namespace CombatStats.Model
{
    /// <summary>When the compact meter is on screen.</summary>
    public enum MeterMode
    {
        /// <summary>Shown from the first fight on: the running fight, or else the latest one.</summary>
        Auto = 0,

        /// <summary>Always on screen, even before the first fight.</summary>
        Always = 1,

        /// <summary>Never shown. Events are still recorded for the detail window.</summary>
        Off = 2,
    }

    /// <summary>The corner or edge of the screen a window is pinned to.</summary>
    public enum ScreenAnchor
    {
        TopLeft = 0,
        TopRight = 1,
        MiddleLeft = 2,
        MiddleRight = 3,
        BottomLeft = 4,
        BottomRight = 5,
    }
}
