# Synthetic image search stress harness

Run from the repository root with .NET 10:

```sh
dotnet run --project tests/PixelTransfer.Tests -c Release
dotnet run --project tests/PixelTransfer.Stress -c Release -- 20 artifacts/search.json
```

The optional first argument is the measured iteration count (2–1000, default 20),
followed by an optional JSON output path. Every scenario gets three untimed warmups.
The benchmark executable disables tiered compilation for both versions, so slow
baseline scenes cannot receive a different optimization tier just because they run
longer. This does not change the production application runtime configuration.
No packages, screenshots, confidential files, network connections, or workstation
access are required. All images and packet bytes are generated deterministically.

Scenarios cover 1080p and 4K desktops; 3/12/24 aligned native marker decoys;
24 scaled decoys; valid and CRC-damaged packets among 21 decoys; short and full
native payloads, rotation and 2x restoration; a connected dark 4K component;
continuous tracked reads, continuous misses, and loss/reacquisition cycles.
Every measured iteration checks expected acceptance and exact payload/transfer ID.

The CSV console output and JSON record nearest-rank p50/p95/max wall time,
process CPU milliseconds per call, current-thread allocated bytes per call, and
synthetic capture count. A cycle contains two `Read` calls: reacquisition, then loss
(cropped miss plus desktop fallback). Other scenarios measure one `Inspect` or
`Read` per call. Image generation is outside timing; tracker scenarios include an
RGB crop/copy as a synthetic capture. JSON creation and logging are outside timing.
The process CPU figure includes GC/background runtime work and is **not** a real
RDP application's CPU percentage. Allocation is churn, not retained heap/RSS.

Continuous tests invoke the existing receiver search synchronously, without
sleeping. They do not implement a second receiver loop or cache screenshot data.
The application's existing 750 ms search-miss / 200 ms tracked cadence is unchanged.
Real capture, WPF scheduling, RDP compression, network effects, and real-time
frame arrival are outside this synthetic benchmark.

## Regression gates and baseline

The normal core test executable also runs `StressRegression.cs`. It checks false
acceptance, corrupted and malicious headers, valid packets among decoys, payload
updates, loss and reacquisition, capture count, custom dimensions, partial RGB
triples, and AES-GCM rejection after a CRC-valid damaged encrypted envelope.
A warmed 4K/24-decoy call must allocate less than `pixel count + 8 MB`. This large
headroom makes the gate tolerant of runtime noise while rejecting eager extraction
of all native candidate payloads (over 1 GB on the pinned baseline).

Timings are reported rather than subject to a fragile absolute CI deadline.
The existing job timeout bounds the overall run; acceptance and allocation checks
fail CI. For an intentional performance change, compare both p50 and p95, allocation
and process CPU on the **same machine/runtime**. Small sub-millisecond differences
or single outliers do not demonstrate a regression.

Windows CI runs this exact harness on both the current PR and the original
`v0.1.2-preview` commit `44419172ad16b5f212442f34d1fcd909efd44901`, creates a
comparison in its job summary, and uploads `search-current.json` and
`search-baseline.json` in `windows-test-results`. It then publishes and runs the
existing WPF/native-screen smoke test. This does not release a new app version.

To reproduce the baseline manually, check out that commit in a separate directory
and copy only this harness's `.cs` and `.csproj` files into its
`tests/PixelTransfer.Stress` directory. Do not copy `bin` or `obj`. Run the same
command and iteration count there. Run the two processes sequentially.

## Deliberate limits

The existing cap of 24 detected markers is retained. More than 24 returns no
candidates: this is fail-closed behavior, not a guarantee that a valid frame remains
detectable amid arbitrarily many decoys. The full image still needs to be scanned,
and a large dark connected component still incurs flood-fill work. CRC is a
transport corruption check; only AES-GCM authenticates the decrypted file. Passing
synthetic tests or Windows smoke tests is not proof of real RDP compatibility.

## Paired control comparison

Long separate runs can experience different CPU frequency or host load even with
the same JIT policy. CI additionally loads the built original Core DLL into an
isolated AssemblyLoadContext, then alternates original/current `Inspect` calls on
the exact same immutable synthetic pixels in one process. Both calls use compiled
delegates; reflection, image creation and result validation are outside timing.
Five warmups and 30 measured pairs cover scan-only controls, scaled decoys and
native/full/rotated/scaled valid packets. `search-paired.json` records both p50/p95
and allocation counts, plus the baseline DLL hash. Prefer these paired controls
when investigating small regressions; separate-run timings alone cannot establish
that a sub-millisecond or host-load-sensitive difference is caused by the change.

```sh
dotnet run --project tests/PixelTransfer.Stress -c Release -- --compare-core /path/to/original/PixelTransfer.Core.dll 30 artifacts/search-paired.json
```
