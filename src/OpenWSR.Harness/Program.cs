using OpenWSR.Nexrad;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: OpenWSR.Harness <archive-file> [...]  |  --soak <SITE> <minutes>");
    return 1;
}

if (args[0] == "--soak")
    return await Soak.RunAsync(args[1], int.Parse(args[2]));

foreach (var path in args)
{
    Console.WriteLine($"=== {Path.GetFileName(path)} ===");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var volume = ArchiveFile.DecodeFile(path);
    sw.Stop();

    Console.WriteLine(
        $"{volume.SiteId}  {volume.StartTimeUtc:yyyy-MM-dd HH:mm:ss}Z  " +
        $"lat {volume.LatDeg:F4}  lon {volume.LonDeg:F4}  alt {volume.AltitudeM:F0} m  " +
        $"VCP {volume.VcpNumber}  ({volume.Sweeps.Count} sweeps, decoded in {sw.ElapsedMilliseconds} ms)");
    Console.WriteLine($"{"cut",3} {"elev",6} {"moment",-24} {"radials",7} {"gates",6} {"min",9} {"max",9}");

    foreach (var sweep in volume.Sweeps)
    {
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        foreach (var v in sweep.Data)
        {
            if (float.IsNaN(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        Console.WriteLine(
            $"{sweep.ElevationIndex,3} {sweep.ElevationAngleDeg,6:F2} {sweep.Moment,-24} " +
            $"{sweep.RadialCount,7} {sweep.GateCount,6} {min,9:F1} {max,9:F1}");
    }
    Console.WriteLine();
}

return 0;

/// <summary>Phase 6 acceptance: run the live pipeline for N minutes and report health.</summary>
internal static class Soak
{
    public static async Task<int> RunAsync(string site, int minutes)
    {
        Console.WriteLine($"Live soak: {site} for {minutes} min, started {DateTime.UtcNow:u}");
        long startMemory = GC.GetTotalMemory(forceFullCollection: true);

        using var feed = new OpenWSR.Ingest.LiveFeed();
        var volumes = new Dictionary<DateTime, (int Emissions, int MaxCuts, bool Complete)>();
        int totalEmissions = 0;
        var errors = new List<string>();
        object gate = new();

        feed.StatusChanged += s =>
        {
            lock (gate)
            {
                Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {s}");
                if (s.Contains("error", StringComparison.OrdinalIgnoreCase)) errors.Add(s);
            }
        };
        feed.VolumeUpdated += (volume, complete) =>
        {
            lock (gate)
            {
                totalEmissions++;
                var key = volume.StartTimeUtc;
                var prev = volumes.TryGetValue(key, out var v) ? v : (0, 0, false);
                int cuts = volume.Sweeps.Select(s => s.ElevationIndex).Distinct().Count();
                if (cuts < prev.Item2)
                    errors.Add($"REGRESSION: volume {key:HH:mm:ss} cuts went {prev.Item2} -> {cuts}");
                volumes[key] = (prev.Item1 + 1, Math.Max(prev.Item2, cuts), prev.Item3 | complete);
                Console.WriteLine(
                    $"[{DateTime.UtcNow:HH:mm:ss}] volume {key:HH:mm:ss}Z: {volume.Sweeps.Count} sweeps, " +
                    $"{cuts} cuts{(complete ? " COMPLETE" : "")}");
            }
        };

        feed.Start(site);
        await Task.Delay(TimeSpan.FromMinutes(minutes));
        feed.Stop();

        long endMemory = GC.GetTotalMemory(forceFullCollection: true);
        Console.WriteLine();
        Console.WriteLine($"=== Soak summary ({minutes} min) ===");
        Console.WriteLine($"volumes seen: {volumes.Count}, snapshots emitted: {totalEmissions}");
        foreach (var (key, v) in volumes.OrderBy(kv => kv.Key))
            Console.WriteLine($"  {key:HH:mm:ss}Z  emissions={v.Emissions}  maxCuts={v.MaxCuts}  complete={v.Complete}");
        Console.WriteLine($"managed memory: {startMemory / 1048576.0:F1} MB -> {endMemory / 1048576.0:F1} MB");
        Console.WriteLine($"errors: {errors.Count}");
        foreach (var error in errors) Console.WriteLine($"  {error}");

        bool pass = errors.Count == 0 && volumes.Count > 0
            && volumes.Values.Count(v => v.Complete) >= Math.Max(1, minutes / 11);
        Console.WriteLine(pass ? "SOAK PASS" : "SOAK FAIL");
        return pass ? 0 : 2;
    }
}
