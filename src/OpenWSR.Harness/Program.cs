using OpenWSR.Nexrad;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: OpenWSR.Harness <archive-file> [...]");
    return 1;
}

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
