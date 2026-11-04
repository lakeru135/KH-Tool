using System.Diagnostics;

namespace KhBatchRunner.Diagnostics;

/// <summary>
/// Times the phases of a dialog's construction into <see cref="RunTrace"/>.
///
/// WHY. "Batch Run is slow to open" is a feeling until something writes down where the time went,
/// and the answer cannot be measured from outside Revit: the same two windows build in 71 ms and
/// 214 ms in the standalone controller, so whatever is slow is on the Revit side of the seam —
/// the action registry, the theme lookup, a store that turns out to be reading a network path.
///
/// Usage is one line per phase. Only totals over <see cref="NoiseFloorMs"/> are written, so an
/// already-fast dialog leaves nothing in the trail.
/// </summary>
internal sealed class OpenTimer
{
    /// <summary>Phases faster than this are not worth a line — they are never the problem.</summary>
    private const int NoiseFloorMs = 15;

    private readonly string _what;
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Stopwatch _phase = Stopwatch.StartNew();

    public OpenTimer(string what) => _what = what;

    /// <summary>Closes the phase that just ran and starts the next.</summary>
    public void Mark(string phase)
    {
        long ms = _phase.ElapsedMilliseconds;
        _phase.Restart();
        if (ms >= NoiseFloorMs) RunTrace.Mark($"{_what}: {phase} took {ms} ms");
    }

    /// <summary>Records the whole construction. Always written, so there is a total to compare
    /// against even when no single phase stood out.</summary>
    public void Done()
    {
        _total.Stop();
        RunTrace.Mark($"{_what}: opened in {_total.ElapsedMilliseconds} ms");
    }
}
