using PixelTransfer.Core;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

int iterations = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 20;
if (iterations < 2 || iterations > 1000) throw new ArgumentOutOfRangeException(nameof(iterations));
string? output = args.Length > 1 ? args[1] : null;
var rows = new List<object>();
Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; CPUs={Environment.ProcessorCount}; iterations={iterations}");
Console.WriteLine("scenario,p50_ms,p95_ms,max_ms,cpu_ms_per_call,allocated_bytes_per_call,captures_per_call");
void Measure(string name, Func<int> run)
{
    for (int i = 0; i < 3; i++) run();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var elapsed = new double[iterations];
    long bytes = GC.GetAllocatedBytesForCurrentThread();
    using var process = Process.GetCurrentProcess();
    var cpu = process.TotalProcessorTime;
    int captures = 0;
    for (int i = 0; i < iterations; i++)
    {
        long start = Stopwatch.GetTimestamp();
        captures += run();
        elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds / iterations;
    long allocated = (GC.GetAllocatedBytesForCurrentThread() - bytes) / iterations;
    Array.Sort(elapsed);
    double p50 = elapsed[(int)Math.Ceiling(iterations * .5) - 1];
    double p95 = elapsed[(int)Math.Ceiling(iterations * .95) - 1];
    double max = elapsed[^1], captureMean = (double)captures / iterations;
    Console.WriteLine(FormattableString.Invariant($"{name},{p50:F3},{p95:F3},{max:F3},{cpuMs:F3},{allocated},{captureMean:F1}"));
    rows.Add(new { name, p50_ms = p50, p95_ms = p95, max_ms = max, cpu_ms_per_call = cpuMs,
        allocated_bytes_per_call = allocated, captures_per_call = captureMean });
}
void Check(RgbImage image, TransferPacket? expected)
{
    var result = FrameCodec.Inspect(image);
    if (expected == null ? result.Packets.Count != 0 :
        result.Packets.Count != 1 || !result.Packets[0].Packet.Payload.SequenceEqual(expected.Payload) ||
        !result.Packets[0].Packet.TransferId.SequenceEqual(expected.TransferId))
        throw new Exception("Incorrect packet acceptance or missed/duplicate packet");
}
var packet = SyntheticScenes.Packet();
foreach (var (w, h) in new[] { (1920, 1080), (3840, 2160) })
{
    var plain = SyntheticScenes.Desktop(w, h);
    Measure($"desktop-{w}", () => { Check(plain, null); return 0; });
    foreach (int count in new[] { 3, 12, 24 })
    {
        var scene = SyntheticScenes.Grid(w, h, count);
        Measure($"decoys-{count}-{w}", () => { Check(scene, null); return 0; });
    }
    var scaledDecoys = SyntheticScenes.Grid(w, h, 24, 60);
    Measure($"scaled-decoys-24-{w}", () => { Check(scaledDecoys, null); return 0; });
}
var valid = SyntheticScenes.WithPacket(packet);
Measure("valid-with-21-decoys", () => { Check(valid, packet); return 0; });
var native = FrameCodec.Render(packet);
Measure("native-short", () => { Check(native, packet); return 0; });
var dense = SyntheticScenes.Packet(true);
var denseImage = FrameCodec.Render(dense);
Measure("native-full", () => { Check(denseImage, dense); return 0; });
var rotatedDense = denseImage.Rotate(1);
Measure("native-full-rotated", () => { Check(rotatedDense, dense); return 0; });
var scaledDense = SyntheticScenes.Scale(denseImage, 2);
Measure("scaled-full-2x", () => { Check(scaledDense, dense); return 0; });
var dark = new RgbImage(3840, 2160);
Measure("dark-connected-4k", () => { Check(dark, null); return 0; });
var damaged = SyntheticScenes.WithPacket(packet);
damaged.Pixels[((850 + 104) * damaged.Width + 1300 + 100) * 3] ^= 1;
Measure("damaged-with-21-decoys", () => { Check(damaged, null); return 0; });
var desktop = new FrameBounds(0, 0, valid.Width, valid.Height);
var tracker = new FrameTracker();
var screen = valid;
int captureCount = 0;
RgbImage Capture(FrameBounds area) { captureCount++; return SyntheticScenes.Crop(screen, area); }
int Read(bool expected)
{
    captureCount = 0;
    var result = tracker.Read(Capture, desktop);
    if ((result != null) != expected || result != null && !result.Payload.SequenceEqual(packet.Payload))
        throw new Exception("Tracker acceptance mismatch");
    return captureCount;
}
Read(true);
Measure("continuous-tracked", () => Read(true));
screen = SyntheticScenes.Grid(2560, 1440, 24);
Read(false); // loss transition is measured separately below
Measure("continuous-search-miss", () => Read(false));
Measure("loss-and-reacquisition-cycle", () => {
    screen = valid; int count = Read(true);
    screen = damaged; count += Read(false);
    return count;
});
if (output != null)
{
    string? directory = Path.GetDirectoryName(output);
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    File.WriteAllText(output, JsonSerializer.Serialize(new { runtime = RuntimeInformation.FrameworkDescription,
        os = RuntimeInformation.OSDescription, cpu_count = Environment.ProcessorCount, iterations, warmups = 3, results = rows },
        new JsonSerializerOptions { WriteIndented = true }));
}
