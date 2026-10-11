# Image search performance investigation — 2026-10-12 (Asia/Taipei)

Baseline: `v0.1.2-preview`, commit `44419172ad16b5f212442f34d1fcd909efd44901`.

## Confirmed findings

- Native candidate extraction copied every geometrically plausible data rectangle before checking the 36-byte packet header. A deterministic 4K scene with 24 decoys allocated about 1.24 GB per inspection and took about 2.3 seconds. This is a reproduced synthetic workload issue, not evidence of an observed company RDP incident.
- `Inspect` now gates native payload reads on bounded header fields and copies only the declared packet length. The public `Locate`/calibration path still reads the complete data area. `TransferPacket.Parse` still verifies CRC; encryption, AES-GCM, collector integrity and scale/rotation sampling rules are unchanged.
- Standard size candidates are built once rather than rebuilding LINQ/Distinct state for each marker triple. Estimated custom dimensions are still tried first and duplicate dimensions are still skipped.
- A tracker whose crop equals the desktop used to capture and inspect that exact full desktop twice after a miss. It now clears its bounds after the first miss and searches again on the next normal poll. Subregion loss still falls back to the desktop in the same call.

## Initial local measurements (default tiered runtime)

Ubuntu 24.04.3 LTS, x64, .NET SDK 10.0.100 / runtime 10.0.0, 8 exposed processors. Release builds; identical fixed-seed harness; 3 warmups and 20 measured calls per scene. Baseline and current benchmark processes were run sequentially, without concurrent core tests. Shared-host scheduling can affect tails; these values are samples, not service-level guarantees. Allocation units below are decimal MB. The search-linux-baseline/current JSON files describe the initial default-tiered local run, before the final row-copy optimization (PR commit 5864e17fd350c80aa88586c4fa3eb0b76f39c1e9). After the first Windows run showed batch-dependent shifts even in unchanged scan-only controls, the benchmark project was changed to disable tiered compilation for both versions. This affects only the benchmark executable; the final controlled Windows comparison is recorded in the PR and CI artifacts.

| Scene | Baseline p50 ms | Current p50 ms | Baseline p95 ms | Current p95 ms | Baseline MB/call | Current MB/call |
|---|---:|---:|---:|---:|---:|---:|
| desktop-1920 | 6.432 | 6.805 | 8.985 | 10.788 | 2.079 | 2.079 |
| decoys-3-1920 | 6.984 | 6.290 | 10.278 | 9.663 | 2.079 | 2.079 |
| decoys-12-1920 | 26.434 | 6.300 | 37.678 | 7.237 | 14.698 | 2.093 |
| decoys-24-1920 | 499.520 | 6.195 | 550.150 | 12.403 | 278.028 | 2.192 |
| scaled-decoys-24-1920 | 9.290 | 5.728 | 10.359 | 6.203 | 7.898 | 2.083 |
| desktop-3840 | 24.507 | 24.172 | 26.774 | 37.608 | 8.304 | 8.304 |
| decoys-3-3840 | 22.676 | 21.922 | 25.974 | 23.998 | 8.303 | 8.303 |
| decoys-12-3840 | 229.742 | 21.467 | 251.737 | 25.109 | 123.458 | 8.327 |
| decoys-24-3840 | 2391.448 | 21.608 | 2496.577 | 26.079 | 1235.779 | 8.444 |
| scaled-decoys-24-3840 | 25.232 | 21.937 | 27.290 | 25.462 | 14.133 | 8.318 |
| valid-with-21-decoys | 69.797 | 9.254 | 74.817 | 12.741 | 43.753 | 3.725 |
| native-short | 1.535 | 1.071 | 2.093 | 1.240 | 0.531 | 0.252 |
| native-full | 1.607 | 1.543 | 1.848 | 1.662 | 0.789 | 0.773 |
| native-full-rotated | 1.675 | 1.560 | 2.155 | 1.648 | 0.789 | 0.773 |
| scaled-full-2x | 3.082 | 2.576 | 3.636 | 2.852 | 1.464 | 1.464 |
| dark-connected-4k | 141.789 | 134.337 | 151.430 | 167.536 | 8.328 | 8.328 |
| damaged-with-21-decoys | 75.682 | 9.571 | 101.386 | 10.384 | 43.753 | 3.725 |
| continuous-tracked | 1.726 | 1.105 | 1.911 | 1.187 | 1.222 | 0.944 |
| continuous-search-miss | 1012.185 | 14.595 | 1094.447 | 15.935 | 536.227 | 14.851 |
| loss-and-reacquisition-cycle | 152.819 | 20.113 | 176.776 | 30.129 | 110.847 | 30.512 |

The JSON files alongside this report also contain max latency, process CPU time and capture counts. Tracker timings include synthetic RGB capture/copy; the cycle row contains reacquisition and subsequent loss (three captures). Other rows measure a single inspection/read.

All measured calls produced the expected packet count and exact payload/ID. No false acceptance was observed in these fixtures. Ordinary native/full/rotated and scaled/full median times did not regress; unchanged image-scanning work and scheduling explain why not every tail metric improves.

## Validation and remaining limits

- The original 42 core tests passed locally on the original version. The expanded 55-test suite passed locally. It preserves those tests and adds 13 regression cases covering allocations, decoys, malformed lengths, custom dimensions, partial RGB tails, fresh payload reads, repeated misses, reacquisition, duplicate captures and AES-GCM rejection.
- Windows CI now triggers on PRs, runs core tests and both benchmark versions, builds the Windows app, and executes the existing WPF/native-screen smoke test. Check the linked PR workflow for the current run status; this report does not assert a CI result before it completes.
- No timing-only CI gate: the strict gate is acceptance correctness plus a warmed 4K/24-decoy allocation budget of pixel count + 8 MB, which the baseline exceeds by over 1 GB. Timings are retained in CI artifacts/job summaries for review.
- The marker cap remains 24. More than 24 intentionally produces no candidates, even if a real frame is present. This task does not remove that detection limit.
- A large connected dark 4K area still costs roughly 134 ms locally because of the existing flood fill. Full-image scanning remains proportional to image area. No screenshot cache, parallel search, pooled payload memory or new polling loop was introduced.
- Synthetic sustained reads omit the application delay; production waits remain 750 ms while searching and 200 ms while tracking. Process CPU milliseconds/call are not end-to-end RDP CPU percentages.
- Actual RDP capture/compression, display changes during capture, long-duration desktop use and company workstations have not been exercised. No real secret data or external workstation was used.

Reproduction and measurement definitions: [stress harness README](../PixelTransfer.Stress/README.md).

## Final paired local controls

To control host-load differences between separate batches, the harness now loads
both Core assemblies in one process and alternates call order, with identical
immutable images and five warmups. Validation and reflection stay outside timing.
The final implementation also uses row copies for unrotated native packets and
row-based pixel extraction for rotations, removing per-pixel division/modulo.
The complete 55-test Release suite passed again after this final optimization.

30 measured pairs, same Linux SDK/runtime as above; tiered compilation disabled.
The corresponding raw data is `search-linux-paired.json`.

| Scene | Baseline p50 ms | Final p50 ms | Baseline p95 ms | Final p95 ms |
|---|---:|---:|---:|---:|
| desktop-4k | 23.887 | 24.220 | 33.530 | 33.962 |
| scaled-decoys-24-4k | 26.268 | 23.424 | 29.889 | 26.207 |
| native-full | 1.575 | 1.206 | 2.000 | 2.891 |
| native-full-rotated | 1.710 | 1.718 | 2.530 | 2.186 |
| scaled-full-2x | 2.884 | 2.771 | 3.614 | 3.047 |
| valid-with-21-decoys | 69.499 | 9.000 | 75.152 | 10.333 |

The paired scan-only control is comparable; full native median latency improves,
rotation and 2x restoration remain comparable, and cluttered valid-frame searches
improve substantially. Tail noise remains visible and is not hidden or used as an
absolute CI gate. Windows runs this same paired comparison and uploads
`search-paired.json`; see the PR for the final verified run and Windows numbers.
