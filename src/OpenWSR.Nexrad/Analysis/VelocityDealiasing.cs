namespace OpenWSR.Nexrad.Analysis;

/// <summary>
/// Unfolds aliased radial velocity.
///
/// A Doppler radar measures velocity from phase shift, which wraps: anything outside
/// ±V<sub>nyquist</sub> is reported shifted by a whole number of 2·V<sub>nyquist</sub>
/// intervals. On a strong mesocyclone that reverses the apparent sign — the inbound half
/// of a couplet reads outbound — so folded velocity does not merely omit information, it
/// asserts the opposite of the truth.
///
/// The approach is region-based, after Py-ART's <c>dealias_region_based</c>: segment the
/// sweep into regions of continuous velocity, measure the mean jump across every shared
/// boundary, and choose a whole-interval shift per region that makes the boundaries agree.
/// It needs no sounding and no previous volume, which matters here because both live
/// partial volumes and 1991 archive files have to work.
/// </summary>
public static class VelocityDealiasing
{
    /// <summary>
    /// Bins per aliasing interval when growing regions. Py-ART's default of 3 is a
    /// balance: coarser bins merge genuinely-discontinuous areas into one region, finer
    /// ones shatter smooth flow into fragments that the graph then has to stitch back.
    /// </summary>
    public const int IntervalSplits = 3;

    /// <summary>Regions smaller than this are folded into their strongest neighbour rather
    /// than being trusted to vote on their own shift.</summary>
    private const int MinRegionGates = 10;

    /// <summary>
    /// A correction only travels across a boundary at least this many gate pairs wide. A
    /// thinner one is one or two gate pairs, not an average, and cannot support a claim
    /// that its two sides are a whole interval apart.
    ///
    /// The value is measured, not guessed, against the KTLX 2013-05-20 20:16Z volume:
    ///
    ///   width 5 -> 0.47 % of gates corrected, largest shift 1, peak 78.2 m/s
    ///   width 3 -> 0.96 %,                    largest shift 2, peak 130.5 m/s
    ///   width 2 -> 1.01 %,                    largest shift 2, peak 130.5 m/s
    ///
    /// There is a cliff between 3 and 5. Below it, corrections chain — one region reached
    /// through another already-shifted one — and reach ±2 intervals, which no single
    /// boundary can justify, because the raw field spans exactly one. 130 m/s is not a
    /// wind; it is two stacked guesses. Py-ART uses only ±1 on this volume too, so the
    /// conservative setting agrees with the reference on the conclusion even though it
    /// corrects fewer gates on the way there.
    ///
    /// Note this is not a signal-quality problem, though it looks like one: the corrected
    /// gates average 29 dBZ against 16 dBZ for untouched ones, and even at 250 km the
    /// tenth percentile is 14.5 dBZ. Filtering weak returns would discard the wrong gates.
    /// </summary>
    private const int MinBoundaryGates = 5;

    /// <summary>
    /// How close the mean step has to sit to a whole interval before it is read as a fold.
    /// A genuine fold boundary steps by very nearly one interval; regions merely separated
    /// by a bin edge step by almost nothing. Ambiguous boundaries in between are noise, and
    /// rounding them silently turns a coin-flip into a 52 m/s error.
    /// </summary>
    private const double FoldTolerance = 0.35;

    /// <summary>
    /// Unfold one velocity sweep. Returns the sweep unchanged when it carries no Nyquist
    /// velocity (pre-2000 archives often do not) or is not a velocity moment.
    /// </summary>
    public static Sweep Dealias(Sweep sweep)
    {
        if (sweep.Moment != Moment.Velocity) return sweep;
        if (sweep.AliasingIntervalMs is not { } interval || interval <= 0) return sweep;

        var unfolded = DealiasGrid(
            sweep.Data, sweep.RadialCount, sweep.GateCount, interval);
        return sweep with { Data = unfolded };
    }

    /// <summary>
    /// The grid-level entry point, separated so it can be tested on synthetic fields with
    /// no Sweep plumbing. <paramref name="data"/> is row-major (radial, gate) with NaN for
    /// no-data; the result is a new array of the same shape.
    /// </summary>
    public static float[] DealiasGrid(
        float[] data, int radialCount, int gateCount, float interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
        if (data.Length != radialCount * gateCount)
            throw new ArgumentException("data length must be radialCount * gateCount", nameof(data));

        var labels = LabelRegions(data, radialCount, gateCount, interval, out int regionCount);
        if (regionCount == 0) return (float[])data.Clone();

        var edges = AccumulateEdges(data, labels, radialCount, gateCount);
        var shifts = SolveShifts(labels, edges, regionCount, interval);

        var result = new float[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            int label = labels[i];
            result[i] = label < 0 ? data[i] : data[i] + shifts[label] * interval;
        }
        return result;
    }

    // ---- 1. regions of continuous velocity ----

    /// <summary>
    /// Flood-fill gates into regions, where a gate joins its neighbour only if both fall in
    /// the same velocity bin. Labels are -1 for no-data. Azimuth wraps: radial 0 and the
    /// last radial are neighbours, or the seam becomes a false discontinuity all the way up
    /// every sweep.
    /// </summary>
    private static int[] LabelRegions(
        float[] data, int radialCount, int gateCount, float interval, out int regionCount)
    {
        float binWidth = interval / IntervalSplits;
        var labels = new int[data.Length];
        Array.Fill(labels, -1);

        var queue = new Queue<int>();
        int next = 0;

        for (int start = 0; start < data.Length; start++)
        {
            if (labels[start] >= 0 || float.IsNaN(data[start])) continue;

            int label = next++;
            int startBin = (int)MathF.Floor(data[start] / binWidth);
            labels[start] = label;
            queue.Clear();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                int radial = index / gateCount;
                int gate = index % gateCount;

                // Four-connected, wrapping in azimuth but not in range. Inlined rather
                // than yielded: this runs once per gate over a ~720x1200 grid.
                if (gate > 0) Visit(index - 1);
                if (gate < gateCount - 1) Visit(index + 1);
                if (radialCount > 1)
                {
                    Visit((radial == radialCount - 1 ? 0 : radial + 1) * gateCount + gate);
                    if (radialCount > 2)
                        Visit((radial == 0 ? radialCount - 1 : radial - 1) * gateCount + gate);
                }
            }

            void Visit(int neighbour)
            {
                if (labels[neighbour] >= 0 || float.IsNaN(data[neighbour])) return;
                if ((int)MathF.Floor(data[neighbour] / binWidth) != startBin) return;
                labels[neighbour] = label;
                queue.Enqueue(neighbour);
            }
        }

        regionCount = next;
        return labels;
    }

    // ---- 2. how far apart are neighbouring regions ----

    private readonly record struct Boundary(double DiffSum, int Count)
    {
        public double MeanDiff => Count == 0 ? 0 : DiffSum / Count;
    }

    /// <summary>
    /// For every pair of adjacent regions, accumulate the velocity step across their shared
    /// boundary. The count is how many gate pairs touch, which later decides which
    /// boundaries are trustworthy enough to fix a region's shift.
    /// </summary>
    private static Dictionary<(int Low, int High), Boundary> AccumulateEdges(
        float[] data, int[] labels, int radialCount, int gateCount)
    {
        var edges = new Dictionary<(int, int), Boundary>();

        void Add(int a, int b)
        {
            int la = labels[a], lb = labels[b];
            if (la < 0 || lb < 0 || la == lb) return;

            // Order the key so the difference always reads low -> high.
            var key = la < lb ? (la, lb) : (lb, la);
            double diff = la < lb ? data[b] - data[a] : data[a] - data[b];
            var existing = edges.TryGetValue(key, out var e) ? e : default;
            edges[key] = new Boundary(existing.DiffSum + diff, existing.Count + 1);
        }

        for (int radial = 0; radial < radialCount; radial++)
        {
            int rowStart = radial * gateCount;
            int nextRadial = radial == radialCount - 1 ? 0 : radial + 1;
            int nextRow = nextRadial * gateCount;

            for (int gate = 0; gate < gateCount; gate++)
            {
                int index = rowStart + gate;
                if (gate < gateCount - 1) Add(index, index + 1);
                if (radialCount > 1) Add(index, nextRow + gate);
            }
        }
        return edges;
    }

    // ---- 3. choose a whole-interval shift per region ----

    /// <summary>
    /// Grow the solution outward from the largest region, always crossing the strongest
    /// remaining boundary first. Each newly reached region takes the shift that best
    /// cancels the mean step across the boundary it was reached through — so the decision
    /// is made where the evidence is strongest, and weak boundaries never get to overrule
    /// a well-established region.
    /// </summary>
    private static int[] SolveShifts(
        int[] labels, Dictionary<(int Low, int High), Boundary> edges, int regionCount, float interval)
    {
        var sizes = new int[regionCount];
        foreach (int label in labels)
            if (label >= 0) sizes[label]++;

        var adjacency = new List<(int Other, double MeanDiff, int Count)>[regionCount];
        for (int i = 0; i < regionCount; i++) adjacency[i] = [];
        foreach (var (key, boundary) in edges)
        {
            adjacency[key.Low].Add((key.High, boundary.MeanDiff, boundary.Count));
            adjacency[key.High].Add((key.Low, -boundary.MeanDiff, boundary.Count));
        }

        var shifts = new int[regionCount];
        var settled = new bool[regionCount];

        // Largest region first; every disconnected group gets its own seed, anchored at
        // zero so an isolated island keeps the values the radar actually reported.
        foreach (int seed in Enumerable.Range(0, regionCount).OrderByDescending(r => sizes[r]))
        {
            if (settled[seed] || sizes[seed] == 0) continue;
            settled[seed] = true;
            shifts[seed] = 0;

            var frontier = new PriorityQueue<(int From, int To), long>();
            void Push(int from)
            {
                foreach (var (other, meanDiff, count) in adjacency[from])
                {
                    // A correction only travels across a boundary solid enough to carry it.
                    // Crossing hairline boundaries and merely inheriting the shift let one
                    // bad decision walk out through low-SNR speckle and compound: a chain
                    // of two spurious folds turned −22 m/s into +82 m/s at 250 km.
                    if (!settled[other] && count >= MinBoundaryGates)
                        frontier.Enqueue((from, other), -count); // strongest boundary first
                }
            }
            Push(seed);

            while (frontier.TryDequeue(out var step, out _))
            {
                var (from, to) = step;
                if (settled[to]) continue;

                var link = adjacency[from].First(a => a.Other == to);
                shifts[to] = shifts[from] - FoldsAcross(link, sizes[to], interval);
                settled[to] = true;
                Push(to);
            }
        }
        return shifts;
    }

    /// <summary>
    /// How many whole intervals the far side of a boundary is folded by, or zero when the
    /// evidence does not support a fold. Defaulting to zero is the conservative answer: it
    /// leaves the radar's own reading in place rather than asserting a velocity that was
    /// never measured.
    /// </summary>
    private static int FoldsAcross(
        (int Other, double MeanDiff, int Count) link, int targetSize, float interval)
    {
        if (targetSize < MinRegionGates || link.Count < MinBoundaryGates) return 0;

        double intervals = link.MeanDiff / interval;
        int n = (int)Math.Round(intervals, MidpointRounding.AwayFromZero);
        if (n == 0) return 0;

        // Only commit when the step really does land near a whole interval.
        return Math.Abs(intervals - n) <= FoldTolerance ? n : 0;
    }
}
