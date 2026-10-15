using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixelTransfer.Core;

public enum DiagnosticMode { AutomaticReceive, ManualReceive, ProbeScreen, ProbeFile, SyntheticRegression, WindowsSmoke }
public enum DiagnosticEnd { Running, Completed, Stopped, Error }
public enum CaptureKind { FullSearch, TrackedRegion, ManualRegion, ProbeScreen, ProbeFile }

// Scalar-only allowlist. Never add image/packet objects, paths, names, IDs or exception messages here.
public sealed record DisplayMetrics(int Width, int Height, double? DpiX, double? DpiY);
public sealed record DiagnosticSample(long Number, double ElapsedMs, CaptureKind Kind,
    DisplayMetrics Display, int CaptureWidth, int CaptureHeight, double CaptureAndInspectMs,
    bool Located, bool ValidPacket, bool CrcFailureObserved, bool? ProbePassed,
    int? FrameWidth, int? FrameHeight, double? Scale, int? ChangedChannels);
public sealed record DiagnosticSummary(long Samples, long LocatedSamples, double? LocalizationRate,
    long ValidPacketSamples, long CrcFailureSamples, long ProbeSamples, long ProbePasses,
    long ReceiveCycles, long AcceptedCycles, long DuplicateCycles, int UniquePackets, int TotalPackets,
    long TrackingLosses, long Reacquisitions, long FullSearches, double? FirstAcceptedMs,
    double MeanCaptureAndInspectMs, double MaxCaptureAndInspectMs, long DroppedDetails);
public sealed record DiagnosticReport(int SchemaVersion, string ApplicationBuild, DiagnosticMode Mode,
    string Evidence, string RealRdpValidation, DateTimeOffset StartedUtc, DateTimeOffset ExportedUtc,
    DateTimeOffset? EndedUtc, DiagnosticEnd End, double ElapsedMs, DiagnosticSummary Summary,
    IReadOnlyList<DiagnosticSample> RecentSamples);

public sealed class DiagnosticSession
{
    public const int DetailLimit = 256;
    private readonly object gate = new();
    private readonly Queue<DiagnosticSample> details = new();
    private readonly TimeProvider time;
    private readonly long started;
    private readonly DateTimeOffset startedUtc;
    private readonly DiagnosticMode mode;
    private DiagnosticEnd end;
    private DateTimeOffset? endedUtc;
    private double? endedMs;
    private long samples, located, valid, crc, probes, probePasses, cycles, accepted, duplicates;
    private int unique, total;
    private long losses, reacquisitions, fullSearches;
    private double durationSum, durationMax;
    private double? firstAccepted;

    public DiagnosticSession(DiagnosticMode mode, TimeProvider? timeProvider = null)
    {
        this.mode = mode;
        time = timeProvider ?? TimeProvider.System;
        startedUtc = time.GetUtcNow(); started = time.GetTimestamp();
    }
    private double Elapsed => time.GetElapsedTime(started).TotalMilliseconds;

    public void RecordInspection(FrameInspection inspection, DisplayMetrics display, int width, int height,
        double durationMs, CaptureKind kind)
    {
        var frame = inspection.Packets.FirstOrDefault();
        Record(display, width, height, durationMs, kind, inspection.HasFrameGeometry,
            frame != null, inspection.CrcFailureObserved, null, frame?.Packet.Size, frame?.Scale, null);
    }
    public void RecordProbe(ProbeResult? result, DisplayMetrics display, int width, int height,
        double durationMs, bool fromFile = false) => Record(display, width, height, durationMs,
            fromFile ? CaptureKind.ProbeFile : CaptureKind.ProbeScreen, result != null,
            false, false, result?.Passed ?? false, result?.Size, null, result?.ChangedChannels);

    private void Record(DisplayMetrics display, int width, int height, double durationMs, CaptureKind kind,
        bool hasGeometry, bool validPacket, bool crcFailure, bool? probePassed, FrameSize? size,
        double? scale, int? changedChannels)
    {
        lock (gate)
        {
            if (end != DiagnosticEnd.Running) return;
            samples++; if (hasGeometry) located++; if (validPacket) valid++; if (crcFailure) crc++;
            if (probePassed != null) { probes++; if (probePassed.Value) probePasses++; }
            durationSum += durationMs; durationMax = Math.Max(durationMax, durationMs);
            if (details.Count == DetailLimit) details.Dequeue();
            details.Enqueue(new(samples, Elapsed, kind, display, width, height, durationMs,
                hasGeometry, validPacket, crcFailure, probePassed, size?.Width, size?.Height, scale, changedChannels));
        }
    }
    public void RecordReceiveCycle(bool packetAccepted, int uniquePackets, int totalPackets,
        long trackingLosses = 0, long reacquisitionCount = 0, long fullSearchCount = 0)
    {
        lock (gate)
        {
            if (end != DiagnosticEnd.Running) return;
            cycles++;
            if (packetAccepted)
            {
                accepted++; firstAccepted ??= Elapsed;
                if (uniquePackets == unique) duplicates++;
            }
            unique = uniquePackets; total = totalPackets;
            losses = trackingLosses; reacquisitions = reacquisitionCount; fullSearches = fullSearchCount;
        }
    }
    public void Finish(DiagnosticEnd result)
    {
        if (result == DiagnosticEnd.Running) throw new ArgumentException("A final outcome is required.", nameof(result));
        lock (gate)
        {
            if (end != DiagnosticEnd.Running) return;
            end = result; endedMs = Elapsed; endedUtc = time.GetUtcNow();
        }
    }
    public DiagnosticReport Snapshot()
    {
        lock (gate)
        {
            // Version/commit only from the built assembly, never from a user-supplied path or label.
            string build = typeof(DiagnosticSession).Assembly.GetCustomAttributes(false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
            string evidence = mode == DiagnosticMode.SyntheticRegression ? "synthetic" :
                mode == DiagnosticMode.WindowsSmoke ? "local-windows-smoke" :
                mode == DiagnosticMode.ProbeFile ? "offline-image" : "live-screen-environment-unconfirmed";
            return new(1, build, mode, evidence, "not-verified", startedUtc, time.GetUtcNow(), endedUtc,
                end, endedMs ?? Elapsed, new(samples, located, samples == 0 ? null : (double)located / samples,
                valid, crc, probes, probePasses, cycles, accepted, duplicates, unique, total, losses,
                reacquisitions, fullSearches, firstAccepted, samples == 0 ? 0 : durationSum / samples,
                durationMax, samples - details.Count), details.ToArray());
        }
    }
    public string ToJson() => JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    });
    public void Export(string path)
    {
        // Explicit export only. Refuse overwrite, including a path selected in the save dialog.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(ToJson());
    }
}
