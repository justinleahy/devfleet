# Research: request execution progress presentation

**Date researched:** 2026-09-06.  
**Surface:** DevFleet web UI (Blazor Server) showing ongoing progress of executing work requests, realtime.  
**Constraint:** progress must be provider-neutral across Pi, Claude, Muse, and Antigravity runtimes; lifecycle/tool events are durable facts, not estimates. **Never fabricate a percentage.** Only measurable facts already emitted — or new durable events explicitly proposed — may be shown.

This note is the contract for how execution progress is **presented**: what is honest to show when duration and percent-complete are unknown, how live status reaches assistive technology, and which Microsoft Fluent / Blazor Fluent UI patterns fit. It does not invent polling loops, ETAs, or percent-complete heuristics.

---

## The problem

A DevFleet work request ("run this task on node X with provider Y") has **no known total work**. The runtime emits a stream of discrete facts: request accepted, session started, tool invoked, file written, message produced, request completed/failed. There is no denominator. Any percent bar would be a guess, and a guess that moves backward (or pins at 99%) is worse than no bar.

The presentation question is therefore: **show measurable facts, in an accessible live region, using indeterminate progress affordances, without implying a fraction that does not exist.**

---

## Primary sources

| Source | URL |
|---|---|
| WAI-ARIA 1.2 — `progressbar` role | https://www.w3.org/TR/wai-aria-1.2/#progressbar |
| WAI-ARIA 1.2 — `status` role | https://www.w3.org/TR/wai-aria-1.2/#status |
| WAI-ARIA 1.2 — `aria-live` | https://www.w3.org/TR/wai-aria-1.2/#aria-live |
| WAI-ARIA 1.2 — `aria-busy` | https://www.w3.org/TR/wai-aria-1.2/#aria-busy |
| WCAG 2.1 — SC 4.1.3 Status Messages (Understanding) | https://www.w3.org/WAI/WCAG21/Understanding/status-messages.html |
| WAI-ARIA APG — Live Region practices | https://www.w3.org/WAI/ARIA/apg/practices/live-regions/ |
| Microsoft Fluent 2 — Progress bar design guidance | https://fluent2.microsoft.design/components/web/react/progressbar/usage |
| Fluent UI Blazor — FluentProgress / FluentProgressRing docs | https://www.fluentui-blazor.net/Progress |
| ASP.NET Core — Blazor Server hosting model (UI updates over SignalR) | https://learn.microsoft.com/en-us/aspnet/core/blazor/hosting-models |
| ASP.NET Core — Blazor fundamentals (rendering / state changes) | https://learn.microsoft.com/en-us/aspnet/core/blazor/components/rendering |

Key normative facts, verified against the sources above:

1. **ARIA `progressbar`:** `aria-valuenow` is set **only when the value is known**. If progress cannot be determined, authors **omit `aria-valuenow`** — that *is* the indeterminate state. Fabricating a value violates the attribute's contract (it must lie between `aria-valuemin` and `aria-valuemax` and represent the actual value).
2. **`role="status"`** has an implicit `aria-live="polite"` and `aria-atomic="true"`: a status message is announced by screen readers **without moving focus**, at the next graceful opportunity. This is the WCAG 4.1.3 mechanism for "progress of a process" and "waiting state" messages. Urgent failure notices use `role="alert"` (assertive).
3. **Live regions must exist before the update** is injected; toggling `aria-live` on after content changes does not reliably announce. The region should be in the initial render and updated thereafter.
4. **Fluent ProgressBar/ProgressRing:** when the completed amount is unknown, use the **indeterminate** state. In Fluent UI Blazor, `<FluentProgress>` without a `Value` attribute is indeterminate; supplying `Value` makes it determinate. Indeterminate communicates "unspecified wait time" — exactly DevFleet's situation.
5. **Blazor Server** already pushes UI diffs to the browser over a persistent SignalR connection when components re-render; an event-driven projection that calls `StateHasChanged` gets realtime delivery **without any client polling**.

---

## What counts as a measurable fact (and what does not)

| Honest to show | Why | Never show |
|---|---|---|
| Current lifecycle phase (queued / provisioning / running / finishing / done / failed) | Durable event | Percent complete (no denominator exists) |
| Elapsed wall time since `startedAt` | Computable from a durable timestamp | ETA / "time remaining" (not derivable) |
| Count of emitted events / tool calls so far | Facts in the projection | A fraction like "3 of 7 steps" unless a plan with 7 steps was itself emitted |
| Latest activity description (current tool/action, provider-neutral wording) | Latest durable event | Fabricated "almost done" / spinner speed changes implying progress |
| Last-event timestamp; explicit "waiting for provider" state | Absence of events is itself observable | Frozen UI that looks identical to a crashed one |
| Failure reason on completion | Durable terminal event | Silent disappearance of the request |

A determinate bar becomes legitimate **only** when a durable event establishes a denominator (e.g. a multi-step plan event with a known step count, and completion events per step). Until such events exist, the bar stays indeterminate.

---

## Accessible live status pattern (W3C-conformant)

Initial render (region exists from the start):

```razor
<section aria-busy="@_isRunning">
  <FluentProgress Visible="@_isRunning" />   @* no Value → indeterminate *@
  <p role="status" aria-atomic="true">@_statusText</p>
</section>
```

Rules drawn from the primary sources:

- `_statusText` carries short provider-neutral phrases derived from events: "Running build (tool call 12)", "Waiting for provider response", "Completed after 3m 12s".
- **Do not** put focus into the status region; announcements must arrive "without receiving focus" (SC 4.1.3).
- Update the live region on phase changes and at a **throttled** cadence for tool activity (a new announcement per keystroke-level event is noise for screen readers); a reasonable bound is one announcement per transition, plus at most one per few seconds during steady activity.
- Terminal failure uses `role="alert"` (assertive) or a Fluent MessageBar with appropriate intent, because it needs immediate attention; routine progress stays `polite`.
- `aria-busy="true"` on the region while running tells assistive tech not to announce intermediate, inconsistent states.
- The visual progress element must not rely on animation alone: the adjacent text status is the accessible carrier, and `role="progressbar"` (which Fluent's component provides) does **not** announce value changes by itself.

---

## Fluent UI Blazor patterns that fit

- **`<FluentProgress>` (linear, indeterminate)** for an executing request row/card: omit `Value`. Add `Width` for constrained layouts.
- **`<FluentProgressRing>` (circular, indeterminate)** for compact contexts (mobile header, list item, node tile) where a linear bar does not fit.
- **Timeline as event list, not component trickery:** DevFleet's durable lifecycle/tool events map naturally onto a vertical, chronologically ordered list (newest phase highlighted). On desktop this can sit beside the request detail; on mobile it collapses to the latest event plus an expand affordance.
- **`<FluentBadge>` / phase chips** for the coarse lifecycle state — glanceable at both breakpoints, screen-reader friendly as plain text.

Realtime delivery is free: the persisted projection updates, the Blazor Server circuit pushes the re-render over SignalR. Do not add a client-side polling timer "to be safe"; that duplicates the existing architecture and breaks under reconnect anyway — after a circuit reconnect, the re-rendered projection state is authoritative, which is exactly what the persisted-projection design guarantees.

---

## Design constraints for DevFleet

1. **No fabricated percentages, ever.** Progress UI is indeterminate (`<FluentProgress>` / `<FluentProgressRing>` with no `Value`) unless and until a durable event establishes a real denominator (e.g. an explicit plan with step completions). ARIA: determinate markup requires `aria-valuenow`; omit it when unknown — the Fluent components already encode this rule.
2. **Present only projection facts.** Elapsed time from a durable `startedAt`, event/tool-call counts, current phase, latest activity, last-event timestamp. No ETAs, no "N% done" derivations from token counts or heuristics.
3. **Provider-neutral vocabulary at the projection boundary.** Pi, Claude, Muse, and Antigravity emit differently-shaped raw output; normalize into the existing durable lifecycle/tool event stream *before* projection, so the UI renders one vocabulary ("tool call", "message", "phase change") and never branches on provider.
4. **Accessible status via a pre-existing `role="status"` live region** (polite, atomic, `aria-busy` while running; `role="alert"` for terminal failure). Announce on phase transitions, throttled during steady activity; never move focus. This satisfies WCAG SC 4.1.3 and the APG live-region practice.
5. **Mobile-first compactness.** Small viewports get: a progress ring or thin bar + one-line status text + elapsed time; the full event timeline collapses behind a disclosure. No hover-dependent information (timestamps as visible text or focusable tooltips), touch targets per Fluent defaults.
6. **Honest staleness.** Show "last activity: 45s ago" (from the latest durable event timestamp) and an explicit "waiting" phase state, so a stalled provider is visually distinguishable from a dead circuit. After SignalR reconnect, re-render from the persisted projection — never from client-cached optimism.
7. **Realtime through the existing architecture.** Projection updates → Blazor re-render → SignalR diff push. No new polling, no separate websocket channel, no client-side event store.
8. **Terminal states are durable and announced.** Completed/failed render as final facts (duration measured from `startedAt` to the terminal event, failure reason verbatim from the event), announced assertively, and remain visible rather than vanishing.

---

## Recommendation

1. Render every executing request with **indeterminate Fluent progress** (`<FluentProgress>` or `<FluentProgressRing>` without `Value`) plus a text status line; upgrade to determinate **only** if a future durable plan event supplies a denominator.
2. Add a single `role="status"` live region per request view, present in the initial render, updated from the projection on phase changes and throttled activity updates; use `role="alert"` for terminal failure.
3. Surface measurable facts only: phase, elapsed time, event/tool counts, latest activity, last-activity age. No percentages, no ETAs.
4. Normalize provider-specific runtime output into the existing durable lifecycle/tool event stream upstream of the projection; the UI must stay provider-agnostic.
5. Mobile layout: ring/bar + one-line status + elapsed time, timeline collapsed behind an expander; desktop layout: status header + vertical event timeline.
6. Distinguish "waiting on provider" (heartbeat/no recent events) from "disconnected" (circuit reconnect) using persisted event timestamps, not client guesses.

---

## Rejected alternatives

| Alternative | Why rejected |
|---|---|
| Percent bar derived from token usage vs. context window | Context usage is not task completion; bar would regress and lie. |
| ETA extrapolated from "average request duration" | Not a durable fact for *this* request; wrong often enough to destroy trust. |
| Spinner alone, no text | Fails WCAG 4.1.3; `progressbar` role does not self-announce; sighted users also get no phase info. |
| Client-side polling for progress | Duplicates SignalR push; contradicts persisted-projection + realtime-notification architecture. |
| Updating DOM imperatively for "smoothness" | Bypasses Blazor render/SR announcement semantics; live-region updates must go through rendered content. |
| Per-tool-call live announcements | Screen-reader noise; throttle to phase transitions and a bounded cadence. |
| Hiding failed/stalled requests | Terminal facts must persist and be announced (assertively on failure). |
| Determinate bar seeded at 0% "until we know" | ARIA requires omitting `aria-valuenow` when unknown; 0% asserts a measured value that does not exist. |

---

## Decision

**2026-09-06.** Request execution progress is presented as **indeterminate Fluent progress + an accessible `role="status"` live region + a factual event timeline**, fed exclusively by the existing persisted projection over provider-neutral durable lifecycle/tool events, delivered over the existing Blazor Server SignalR circuit. Percentages and ETAs are prohibited unless a future durable event establishes a real denominator. Mobile renders the compact form (ring/bar, one-line status, collapsed timeline); staleness and terminal states are shown as facts, never implied by animation.
