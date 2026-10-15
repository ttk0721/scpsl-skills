using System;

namespace SlKit;

/// <summary>
/// Marker type identifying SlKit's clock entry in the PlayerLoop. Its <see cref="Type"/> identity
/// is what makes <see cref="SlClock.Install"/> idempotent.
/// </summary>
internal struct SlKitClockMarker
{
}
