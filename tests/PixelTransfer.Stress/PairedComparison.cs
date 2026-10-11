using PixelTransfer.Core;
using System.Collections;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;

// Runs both unchanged binaries in one process with alternating order. This controls
// host load/CPU-frequency changes between long, separate benchmark batches.
internal static class PairedComparison
{
    internal static void Run(string baselinePath, int iterations, string output)
    {
        if (iterations < 2 || iterations > 1000) throw new ArgumentOutOfRangeException(nameof(iterations));
        var context = new AssemblyLoadContext("pixel-transfer-baseline", isCollectible: true);
        var baseline = context.LoadFromAssemblyPath(Path.GetFullPath(baselinePath));
        var current = typeof(FrameCodec).Assembly;
        Func<object, object> Inspector(Assembly assembly)
        {
            var imageType = assembly.GetType("PixelTransfer.Core.RgbImage", true)!;
            var method = assembly.GetType("PixelTransfer.Core.FrameCodec", true)!.GetMethod("Inspect")!;
            var input = Expression.Parameter(typeof(object));
            return Expression.Lambda<Func<object, object>>(Expression.Convert(
                Expression.Call(method, Expression.Convert(input, imageType)), typeof(object)), input).Compile();
        }
        var inspect = new[] { Inspector(baseline), Inspector(current) };
        var packet = SyntheticScenes.Packet(true);
        var rendered = FrameCodec.Render(packet);
        var scenes = new (string Name, RgbImage Image, byte[]? Payload)[] {
            ("desktop-4k", SyntheticScenes.Desktop(3840, 2160), null),
            ("scaled-decoys-24-4k", SyntheticScenes.Grid(3840, 2160, 24, 60), null),
            ("native-full", rendered, packet.Payload),
            ("native-full-rotated", rendered.Rotate(1), packet.Payload),
            ("scaled-full-2x", SyntheticScenes.Scale(rendered, 2), packet.Payload),
            ("valid-with-21-decoys", SyntheticScenes.WithPacket(packet), packet.Payload)
        };
        var rows = new List<object>();
        Console.WriteLine($"PAIRED {RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; iterations={iterations}; alternating order");
        Console.WriteLine("scene,baseline_p50_ms,current_p50_ms,baseline_p95_ms,current_p95_ms,baseline_bytes,current_bytes");
        foreach (var (name, image, payload) in scenes)
        {
            var images = new[] { baseline, current }.Select(a => Activator.CreateInstance(
                a.GetType("PixelTransfer.Core.RgbImage", true)!, image.Width, image.Height, image.Pixels)!).ToArray();
            void Validate(object inspection)
            {
                var packets = ((IEnumerable)inspection.GetType().GetProperty("Packets")!.GetValue(inspection)!).Cast<object>().ToArray();
                if (packets.Length != (payload == null ? 0 : 1)) throw new Exception($"{name}: packet acceptance mismatch");
                if (payload == null) return;
                var result = packets[0].GetType().GetProperty("Packet")!.GetValue(packets[0])!;
                var actual = (byte[])result.GetType().GetProperty("Payload")!.GetValue(result)!;
                var id = (byte[])result.GetType().GetProperty("TransferId")!.GetValue(result)!;
                if (!actual.SequenceEqual(payload) || !id.SequenceEqual(packet.TransferId)) throw new Exception($"{name}: payload/ID mismatch");
            }
            for (int warm = 0; warm < 5; warm++) for (int version = 0; version < 2; version++) Validate(inspect[version](images[version]));
            var samples = new[] { new double[iterations], new double[iterations] };
            long[] allocations = [0, 0];
            for (int i = 0; i < iterations; i++)
                for (int slot = 0; slot < 2; slot++)
                {
                    int version = (i + slot) % 2;
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    var result = inspect[version](images[version]);
                    samples[version][i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    allocations[version] += GC.GetAllocatedBytesForCurrentThread() - allocated;
                    Validate(result); // validation/reflection is deliberately outside timing and allocation
                }
            foreach (var sample in samples) Array.Sort(sample);
            double Percentile(int version, double p) => samples[version][(int)Math.Ceiling(iterations * p) - 1];
            double b50 = Percentile(0, .5), c50 = Percentile(1, .5), b95 = Percentile(0, .95), c95 = Percentile(1, .95);
            long bBytes = allocations[0] / iterations, cBytes = allocations[1] / iterations;
            Console.WriteLine(FormattableString.Invariant($"{name},{b50:F3},{c50:F3},{b95:F3},{c95:F3},{bBytes},{cBytes}"));
            rows.Add(new { name, baseline_p50_ms = b50, current_p50_ms = c50, baseline_p95_ms = b95,
                current_p95_ms = c95, baseline_bytes = bBytes, current_bytes = cBytes });
        }
        string? directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(output, JsonSerializer.Serialize(new { runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription, iterations, warmups = 5, order = "alternating in the same process",
            baseline_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(baselinePath))),
            results = rows }, new JsonSerializerOptions { WriteIndented = true }));
        context.Unload();
    }
}
