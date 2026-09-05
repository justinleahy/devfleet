# Research: node system resource monitoring

**Date researched:** 2026-09-05.  
**Primary platform:** Fedora Linux (cgroup v2). Node also runs under Docker Compose.  
**Constraint:** observe the node environment without privileged access or secrets. No background polling beyond the existing heartbeat. Metrics must be bounded, finite, non-negative, and **nullable when unavailable**.

This note is the contract for a **node-local snapshot** of CPU, memory, disk, load, and uptime. It distinguishes **host (procfs)** semantics from **cgroup / container** semantics. It does not invent polling, privileges, or extra fields.

---

## Contract target (normalized snapshot)

Wire-facing snapshot (host-owned JSON). All numeric fields except `observedAt` are **nullable**. Never fabricate; never carry a previous reading.

```json
{
  "observedAt": "2026-09-05T12:00:00Z",
  "cpuUsagePercent": 12.5,
  "memoryUsedBytes": 2147483648,
  "memoryTotalBytes": 8589934592,
  "diskUsedBytes": 10737418240,
  "diskTotalBytes": 107374182400,
  "loadAverageOneMinute": 0.61,
  "uptimeSeconds": 86400.12
}
```

| Field | Type | Unit | Range after normalize | Meaning |
|---|---|---|---|---|
| `observedAt` | string | ISO-8601 UTC | required | Wall clock at sample. Not kernel btime. |
| `cpuUsagePercent` | number \| null | percentage points | finite, `0 ≤ x ≤ 100` | Busy fraction of the **relevant CPU budget** over the interval since the previous heartbeat sample. **null on the first sample** (see CPU). |
| `memoryUsedBytes` | number \| null | bytes | finite integer ≥ 0 | Memory charged to the node environment (cgroup) or host used RAM (procfs fallback). |
| `memoryTotalBytes` | number \| null | bytes | finite integer ≥ 0; if both present, `used ≤ total` | Hard memory budget: cgroup `memory.max` when finite; else host `MemTotal`. |
| `diskUsedBytes` | number \| null | bytes | finite integer ≥ 0 | `TotalSize − AvailableFreeSpace` for the **root volume of the process** (`/`). |
| `diskTotalBytes` | number \| null | bytes | finite integer ≥ 0; if both present, `used ≤ total` | `DriveInfo.TotalSize` of `/`. |
| `loadAverageOneMinute` | number \| null | load average (dimensionless) | finite ≥ 0 | First field of `/proc/loadavg` (**host** run-queue average; not cgroup-local). |
| `uptimeSeconds` | number \| null | seconds | finite ≥ 0 | First field of `/proc/uptime` (**host** wall time since boot, including suspend). |

**Fail closed (per field):** missing file, parse failure, overflow, NaN, ±Inf, negative, `used > total`, or CPU percent outside `[0, 100]` → that field is `null`. Other fields may still populate. Do not omit the object; do not reuse stale values.

**Sampling:** one read of each source **on the existing heartbeat tick**. Store only the previous CPU counters (and their timestamp) in process memory. No extra timers.

---

## Primary sources

| Source | URL |
|---|---|
| Linux `/proc` filesystem | https://www.kernel.org/doc/html/latest/filesystems/proc.html |
| Kernel `proc.rst` (same text) | https://raw.githubusercontent.com/torvalds/linux/master/Documentation/filesystems/proc.rst |
| cgroup v2 | https://docs.kernel.org/admin-guide/cgroup-v2.html |
| cgroup v2 `cgroup-v2.rst` | https://raw.githubusercontent.com/torvalds/linux/master/Documentation/admin-guide/cgroup-v2.rst |
| `proc_loadavg(5)` (man-pages 6.18) | https://man7.org/linux/man-pages/man5/proc_loadavg.5.html |
| `proc_uptime(5)` (man-pages 6.18) | https://man7.org/linux/man-pages/man5/proc_uptime.5.html |
| .NET `DriveInfo` | https://learn.microsoft.com/en-us/dotnet/api/system.io.driveinfo |
| `DriveInfo.TotalSize` | https://learn.microsoft.com/en-us/dotnet/api/system.io.driveinfo.totalsize |
| `DriveInfo.AvailableFreeSpace` | https://learn.microsoft.com/en-us/dotnet/api/system.io.driveinfo.availablefreespace |
| `DriveInfo.IsReady` | https://learn.microsoft.com/en-us/dotnet/api/system.io.driveinfo.isready |

Kernel docs cited below are **Linux master `Documentation/`** as of this research date. .NET signatures are **.NET 10** API reference (`view=net-10.0`).

---

## Environment detection (unprivileged)

1. Read `/proc/self/cgroup`. cgroup v2 membership is `0::$PATH` ([cgroup-v2.rst](https://raw.githubusercontent.com/torvalds/linux/master/Documentation/admin-guide/cgroup-v2.rst): “The entry for cgroup v2 is always in the format `0::$PATH`”).
2. Unified hierarchy is typically `/sys/fs/cgroup` + `$PATH`. If `$PATH` is `/` (root cgroup) or cgroup files are missing → **host / procfs path**.
3. Docker Compose on Fedora: non-root cgroup with `cpu.stat`, `memory.current`, `memory.max` when those controllers are enabled on the container.
4. Never require `CAP_SYS_ADMIN`, host `/sys` bind-mounts beyond what the runtime already exposes, `docker.sock`, or reading other processes’ `/proc/<pid>`. Kernel procfs docs: a process may read its own `/proc/PID/*` with no extra permissions.

If cgroup files exist but a given controller file is absent → fall back **per metric**, not globally.

---

## CPU

### Host: `/proc/stat`

Kernel: all numbers in `/proc/stat` are **aggregates since the system first booted**. The first `cpu` line aggregates all `cpuN` lines. Time units are **USER_HZ** (typically hundredths of a second). Columns, left to right:

`user`, `nice`, `system`, `idle`, `iowait`, `irq`, `softirq`, `steal`, `guest`, `guest_nice`.

Kernel warns that **iowait is not reliable**.

**Busy percent (host, two samples t0 and t1):**

Let `idle = idle + iowait` and  
`nonIdle = user + nice + system + irq + softirq + steal`  
(do **not** add `guest` / `guest_nice`; those columns overlap user time in the accounting the kernel documents as extra columns).

```
dIdle    = idle1 − idle0
dTotal   = (idle1 + nonIdle1) − (idle0 + nonIdle0)
percent  = 100 * (1 − dIdle / dTotal)
```

If `dTotal ≤ 0` or either sample missing → `null`. Clamp only if a tiny floating error puts the value slightly outside `[0, 100]`; if it is clearly outside after that, `null`.

### First-sample limitation (required)

Counters are **cumulative since boot**, not a rate. A single read cannot produce utilization. On the **first heartbeat** after process start (or after a counter reset / wrap that cannot be explained as monotonic delta):

- emit `cpuUsagePercent: null`
- keep the counters for the next tick

Do **not** divide since-boot busy time by uptime (that is lifetime average, not current load). Do **not** sleep inside the heartbeat to take a second sample.

### Container: cgroup v2 `cpu.stat` + `cpu.max`

cgroup v2: all CPU durations are **microseconds**. `cpu.stat` always reports `usage_usec`, `user_usec`, `system_usec` for the cgroup **including descendants**. `cpu.max` default is `"max 100000"`; `"max"` for `$MAX` means **no bandwidth limit**.

**When `cpu.max` first token is a finite `$MAX`:**

```
quotaCpus = $MAX / $PERIOD
dUsage    = usage_usec1 − usage_usec0
dWall     = wall_microseconds between samples
percent   = 100 * dUsage / (dWall * quotaCpus)
```

**Unlimited cgroup fallback:** if `$MAX` is `max`, `cpu.max` unreadable, or `quotaCpus` is not a positive finite number → **do not pretend the container has a 1-CPU budget**. Use **host `/proc/stat`** busy percent (visible CPUs). That is the honest “share of the machine” number when Compose did not set `deploy.resources.limits.cpu` / `cpus`.

`cpu.stat` first sample: same as procfs — **null**.

Never use `/proc/stat` `cpu` line **as if it were container-local** when a finite `cpu.max` exists; that over-reports idle.

---

## Memory

### Host: `/proc/meminfo`

Values are **kB** (kernel example and field units). `MemTotal` is “total usable RAM (physical RAM minus a few reserved bits and the kernel binary code)”. `MemAvailable` is an estimate of memory available for new applications without swapping.

**Host formulas (bytes):**

```
memoryTotalBytes = MemTotal * 1024
memoryUsedBytes  = max(0, memoryTotalBytes − MemAvailable * 1024)
```

Prefer `MemAvailable` over `MemFree`. Kernel: `MemFree` is free RAM only; `MemAvailable` accounts for reclaimable cache/slab and watermarks. Overlapping counters must not be summed into a second “used” definition.

If `MemAvailable` is missing, fall back to `MemTotal − MemFree` (still kB × 1024). If `MemTotal` missing or `used > total` → both memory fields `null`.

### Container: `memory.current` and `memory.max`

cgroup v2: **all memory amounts are in bytes**. `memory.current` is “the total amount of memory currently being used by the cgroup and its descendants” (non-root). `memory.max` default is `"max"` — **no hard limit**. Usage may temporarily exceed the limit.

```
memoryUsedBytes  = memory.current          # bytes, integer
memoryTotalBytes = memory.max              # iff finite unsigned integer
```

**Unlimited cgroup fallback:** if `memory.max` is the token `max` or unreadable → **both** `memoryUsedBytes` and `memoryTotalBytes` fall back to **host `/proc/meminfo`** (`MemTotal` / `MemAvailable`, kB × 1024), the same formulas as the host path. Do **not** keep `memoryUsedBytes` from `memory.current` in this case: pairing process-scope charge with host total is the Fedora/systemd unlimited-cgroup lie (tiny used vs host RAM). Finite `memory.max` still uses raw `memory.current` / finite `memory.max`. If after the host formulas `used > total` → **null both** rather than invert.

Do not use `memory.high` (throttle, not hard max) or `memory.min`/`memory.low` (protection).

`/proc/meminfo` inside a container may still show **host** totals unless the runtime virtualizes it. **Do not** treat `MemTotal` as the container limit when `memory.max` is finite.

---

## Disk (.NET `DriveInfo`)

Official signatures (.NET 10):

```csharp
public sealed class DriveInfo
public long TotalSize { get; }             // total size of storage space, bytes
public long AvailableFreeSpace { get; }    // free space available on the drive, bytes (quota-aware)
public long TotalFreeSpace { get; }
public bool IsReady { get; }
public DriveInfo(string driveName)
```

Remarks: `AvailableFreeSpace` **may differ from `TotalFreeSpace` because it takes disk quotas into account**. Properties throw `UnauthorizedAccessException` or `IOException` (and `TotalSize` also `DriveNotFoundException`). Examples gate size reads on `IsReady`.

**Node disk (single volume — the process root):**

```csharp
var d = new DriveInfo("/");
if (!d.IsReady) → disk fields null
  diskTotalBytes = d.TotalSize
  diskUsedBytes  = d.TotalSize - d.AvailableFreeSpace
```

Use **`AvailableFreeSpace`** (what this uid can still write), not `TotalFreeSpace`.

If `TotalSize ≤ 0`, `AvailableFreeSpace < 0`, or `used > total` → null both. Do not enumerate `GetDrives()` (noise: overlay, tmpfs, proc, cgroup, secrets mounts). Do not report Docker **host** bind-mount capacity unless `/` is that mount.

**Container vs host:** `DriveInfo("/")` is the **filesystem the node sees** (container writable layer / named volume), not the Fedora host disk, unless `/` is the host root. That is the correct “will the node fill its volume?” signal.

---

## Load average

`/proc/loadavg` (kernel table + `proc_loadavg(5)`): first three fields are 1, 5, and 15 minute averages of jobs in state R or D. Fourth field `runnable/total`; fifth last PID.

```
loadAverageOneMinute = field 1   // decimal ≥ 0
```

**This is host-global**, not namespaced to a cgroup. In Docker it is **the host’s** load, not “this container’s load”. Do not divide by nproc or by `cpu.max` quota (that is not what the kernel reports). If the operator needs container CPU, they already have `cpuUsagePercent`.

Only the **one-minute** field is in the snapshot. 5- and 15-minute averages are omitted (not in the candidate schema).

---

## Uptime

Kernel table: `/proc/uptime` is “wall clock since boot, combined idle time of all cpus”.  
`proc_uptime(5)`: two numbers **in seconds** — (1) uptime **including time spent in suspend**, (2) idle process time.

```
uptimeSeconds = field 1
```

**Host boot time**, not container start time, and not cgroup lifetime. A Compose node that started one minute ago on a host that has been up 40 days reports ~40 days. Do **not** substitute `/proc/1/stat` starttime or Docker `StartedAt` (privileged / extra API). If a future UI wants “container age”, that is a different field.

`btime` in `/proc/stat` is Unix epoch seconds of boot; do not mix it into `uptimeSeconds`.

---

## Normalization

For every numeric field after the formula:

1. Must be finite (reject NaN, ±Infinity).
2. Must be ≥ 0.
3. Percents: `0 ≤ x ≤ 100` (CPU only).
4. Bytes: integer values (round toward zero after kB×1024); JSON number is fine if integral.
5. `used ≤ total` when both non-null.
6. Otherwise **null** that field (or the used/total pair).

`observedAt` is `DateTime.UtcNow` (or equivalent) at the start of the sample, ISO-8601 with `Z`.

---

## Security

- Read only: `/proc/stat`, `/proc/meminfo`, `/proc/loadavg`, `/proc/uptime`, `/proc/self/cgroup`, and the current cgroup’s `cpu.stat`, `cpu.max`, `memory.current`, `memory.max` under `/sys/fs/cgroup`.
- `DriveInfo("/")` only. No volume labels in the snapshot (`VolumeLabel` can be PII-ish / noisy).
- No `docker` CLI, no `/var/run/docker.sock`, no `/proc/<other-pid>`, no `environ`, no credentials, no cgroup **writes**.
- Snapshot JSON contains only the eight fields. No paths, no raw file text, no exception messages.
- Unprivileged user in the node container is sufficient on Fedora + Compose when the runtime mounts cgroupfs (default).

---

## Host vs container (summary)

| Metric | Bare-metal Fedora | Docker Compose (typical, unlimited) | Docker with cgroup limits |
|---|---|---|---|
| CPU % | `/proc/stat` delta | `/proc/stat` delta (`cpu.max` = `max`) | `cpu.stat` `usage_usec` / (`Δt` × quota) |
| Memory used | `MemTotal − MemAvailable` | `MemTotal − MemAvailable` (`memory.max` = `max`) | `memory.current` |
| Memory total | `MemTotal` | `MemTotal` (`memory.max` = `max`) | finite `memory.max` |
| Disk | `DriveInfo("/")` host root | container writable FS | same |
| Load 1m | host `/proc/loadavg` | **host** load (not isolated) | **host** load |
| Uptime | host `/proc/uptime` | **host** uptime | **host** uptime |
| First CPU sample | null | null | null |

---

## Recommendation

1. Sample **once per existing heartbeat**. Persist only previous CPU counters + timestamp in RAM.
2. Detect cgroup v2 via `/proc/self/cgroup`. Prefer cgroup files for CPU/memory **when they encode a real finite limit** (`cpu.max` / `memory.max`); otherwise procfs (including unlimited `max`).
3. **CPU:** two-sample busy percent; **first sample null**; unlimited cgroup → `/proc/stat`; limited → `usage_usec` vs `cpu.max`.
4. **Memory:** finite `memory.max` → `memory.current` / that limit; unlimited or unreadable `memory.max` → **both** used and total from host `/proc/meminfo` (`MemTotal − MemAvailable`, `MemTotal`); host path is always those meminfo formulas.
5. **Disk:** `DriveInfo("/")` with `IsReady`, `TotalSize`, `AvailableFreeSpace`.
6. **Load / uptime:** procfs field 1 each; document as **host** metrics.
7. Fail closed to `null` per field. No privileged APIs.

---

## Rejected alternatives

| Alternative | Why rejected |
|---|---|
| Lifetime busy / `/proc/uptime` as CPU % | Not an interval; hides current saturation. Contradicts “aggregates since boot”. |
| Sleep 100–1000 ms inside the heartbeat for a second CPU sample | Extra latency; violates “no polling beyond heartbeat”. First sample stays null. |
| `Environment.ProcessorCount` / `GC.GetGCMemoryInfo()` / `Process.WorkingSet` | Process-local CLR, not node environment; GC heap ≠ cgroup / host RAM. |
| cgroup v1 `cpuacct.usage`, `memory.limit_in_bytes` | Fedora + modern Docker are v2; v1 is deprecated. |
| Treat `memory.max = max` as “infinite” and omit total | Schema wants a total when knowable; fall back to `MemTotal`. |
| Treat `memory.max = max` as 0 or as `memory.current` | Lies; 0 implies empty machine. |
| Scale load average by CPU count or quota | Kernel loadavg is jobs, not “percent of this cgroup”. |
| Container start time as uptime | Not in `/proc/uptime`; needs Docker API or pid 1 heuristics. |
| `GetDrives()` sum of all volumes | Counts proc/tmpfs/overlay; leaks mount topology. |
| `TotalFreeSpace` instead of `AvailableFreeSpace` | Ignores quotas; official remarks say they can differ. |
| `iostat`, `vmstat`, `free(1)`, `df`, shelling out | Extra processes; same data is in procfs / DriveInfo. |
| Privileged host PID mount / `docker stats` | Secrets surface, requires docker.sock or host proc; out of constraint. |
| Background `PeriodicTimer` besides heartbeat | Explicitly forbidden. |
| Carrying last-known-good on read failure | Fail closed, same as subscription-usage research. |

---

## Decision

**2026-09-05.** Node system resources are a **nullable, fail-closed snapshot** on the existing heartbeat: procfs for host-global load/uptime and unlimited CPU/memory **used and totals**; cgroup v2 for container charge and finite limits; `DriveInfo("/")` for disk. First CPU sample is **null**. Unlimited `memory.max` / `cpu.max` **fall back to host procfs** (memory: both used and total from `/proc/meminfo`), never to a fake 1-CPU or 0-byte budget, and never pairing `memory.current` with host `MemTotal`.
