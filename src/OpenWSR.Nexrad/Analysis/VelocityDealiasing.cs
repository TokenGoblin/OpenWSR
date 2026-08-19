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
    /// Work out how many whole intervals each region is folded by, following Py-ART's
    /// <c>dealias_region_based</c> structure.
    ///
    /// The important part is that regions are <em>merged</em> rather than walked. Repeatedly
    /// take the strongest remaining boundary, fold the smaller side onto the larger, and
    /// then <b>combine the merged side's remaining boundaries with the base node's</b>. That
    /// last step is what an ordinary spanning-tree walk cannot do: a region touching a large
    /// merged area through three separate thin boundaries is judged on all three at once,
    /// where a walk looks at each in isolation, finds none of them convincing on its own,
    /// and leaves the region unfolded.
    ///
    /// That is where the missing correction rate went. It was never a threshold that needed
    /// loosening — loosening one only lets a single thin boundary make a decision it cannot
    /// support, which is how two stacked guesses once turned -22 m/s into +82 m/s.
    ///
    /// Measured on the KTLX 2013-05-20 20:16Z volume, against Py-ART 2.2.5:
    ///
    ///   cut          walk            merge           Py-ART
    ///   elev 2       738 (0.47 %)    1288 (0.83 %)   1393 (0.89 %)
    ///   elev 6      2788 (1.84 %)    3478 (2.29 %)   4116 (2.71 %)
    ///
    /// Largest shift stays +/-1 either way, and the peak unfolded speed *fell* from 78.2 to
    /// 75.2 m/s — the extra corrections are more coherent, not wilder. Do not try to close
    /// the remaining 8-15 % by relaxing anything here; that was tried on the walk and it
    /// bought the rate with +/-2 chains that fabricate 130 m/s winds. Nor is it a
    /// signal-quality problem: corrected gates average 29 dBZ against 16 dBZ for untouched
    /// ones, so a gatefilter discards precisely the wrong gates.
    /// </summary>
    private static int[] SolveShifts(
        int[] labels, Dictionary<(int Low, int High), Boundary> edges, int regionCount, float interval)
    {
        var regionSize = new int[regionCount];
        foreach (int label in labels)
            if (label >= 0) regionSize[label]++;

        // Regions merge into nodes; a node's unwrap applies to every region inside it.
        var parent = new int[regionCount];
        var nodeSize = new long[regionCount];
        var regionsInNode = new List<int>[regionCount];
        var unwrap = new int[regionCount];
        for (int i = 0; i < regionCount; i++)
        {
            parent[i] = i;
            nodeSize[i] = regionSize[i];
            regionsInNode[i] = [i];
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        // adjacency[a][b] holds the sum of (v_a - v_b) across the shared boundary, counted
        // in whole intervals, with the number of gate pairs behind it. Both directions are
        // stored so a merge can rewrite either side without searching for it.
        var adjacency = new Dictionary<int, (int Count, double SumDiff)>[regionCount];
        for (int i = 0; i < regionCount; i++) adjacency[i] = [];

        var queue = new PriorityQueue<(int A, int B), long>();
        foreach (var ((low, high), boundary) in edges)
        {
            // Boundary.DiffSum reads low -> high as the sum of (v_high - v_low).
            double sum = boundary.DiffSum / interval;
            adjacency[low][high] = (boundary.Count, -sum);
            adjacency[high][low] = (boundary.Count, sum);
            queue.Enqueue((low, high), -boundary.Count);
        }

        while (queue.TryDequeue(out var pair, out long priority))
        {
            int a = Find(pair.A), b = Find(pair.B);
            if (a == b) continue;
            if (!adjacency[a].TryGetValue(b, out var link)) continue;

            // Merging changes boundary strengths, so a queued entry can be out of date.
            // Re-queue at the current strength rather than acting on a stale one, or a
            // boundary that has since become the strongest would be skipped.
            if (-priority != link.Count)
            {
                queue.Enqueue((a, b), -link.Count);
                continue;
            }

            double meanDiff = link.SumDiff / link.Count;   // intervals, oriented a -> b
            int folds = (int)Math.Round(meanDiff, MidpointRounding.AwayFromZero);

            // Fold the smaller side onto the larger: the bigger area is the better
            // reference, and moving it instead would drag more gates than it fixes.
            int baseNode, mergeNode;
            if (nodeSize[a] > nodeSize[b])
            {
                baseNode = a;
                mergeNode = b;
            }
            else
            {
                baseNode = b;
                mergeNode = a;
                folds = -folds;
            }

            if (folds != 0)
            {
                foreach (int region in regionsInNode[mergeNode]) unwrap[region] += folds;

                // The merged side has moved, so every boundary it still has now reads
                // differently by exactly that many intervals per gate pair.
                foreach (int other in adjacency[mergeNode].Keys.ToList())
                {
                    var edge = adjacency[mergeNode][other];
                    adjacency[mergeNode][other] = (edge.Count, edge.SumDiff + edge.Count * folds);
                    var back = adjacency[other][mergeNode];
                    adjacency[other][mergeNode] = (back.Count, back.SumDiff - back.Count * folds);
                }
            }

            // Drop the edge just used, then hand the merged node's remaining boundaries to
            // the base node, combining any that lead to the same neighbour. This combining
            // is the whole point of the structure.
            adjacency[baseNode].Remove(mergeNode);
            adjacency[mergeNode].Remove(baseNode);

            foreach (var (other, edge) in adjacency[mergeNode])
            {
                adjacency[other].Remove(mergeNode);

                var combined = adjacency[baseNode].TryGetValue(other, out var existing)
                    ? (Count: existing.Count + edge.Count, SumDiff: existing.SumDiff + edge.SumDiff)
                    : edge;

                adjacency[baseNode][other] = combined;
                adjacency[other][baseNode] = (combined.Count, -combined.SumDiff);
                queue.Enqueue((baseNode, other), -combined.Count);
            }
            adjacency[mergeNode].Clear();

            parent[mergeNode] = baseNode;
            nodeSize[baseNode] += nodeSize[mergeNode];
            regionsInNode[baseNode].AddRange(regionsInNode[mergeNode]);
            regionsInNode[mergeNode] = [];
        }

        AnchorOnLargestRegion(regionCount, regionSize, regionsInNode, parent, unwrap);
        return unwrap;
    }

    /// <summary>
    /// Merging only ever fixes regions <em>relative to each other</em>, so a whole connected
    /// group can come out shifted as a block. Anchor each group on its largest region, which
    /// leaves the values the radar actually reported standing wherever the evidence is
    /// strongest, and leaves an isolated island alone entirely.
    ///
    /// Py-ART instead centres each sweep so the mean fold is zero. That spreads a correction
    /// onto gates that never needed one, which is the wrong trade here: a forecaster reading
    /// a number off the screen should get the measured value unless there was a reason to
    /// change it.
    /// </summary>
    private static void AnchorOnLargestRegion(
        int regionCount, int[] regionSize, List<int>[] regionsInNode, int[] parent, int[] unwrap)
    {
        for (int node = 0; node < regionCount; node++)
        {
            if (parent[node] != node || regionsInNode[node].Count == 0) continue;

            int largest = -1;
            foreach (int region in regionsInNode[node])
                if (largest < 0 || regionSize[region] > regionSize[largest]) largest = region;

            int anchor = unwrap[largest];
            if (anchor == 0) continue;
            foreach (int region in regionsInNode[node]) unwrap[region] -= anchor;
        }
    }
}
