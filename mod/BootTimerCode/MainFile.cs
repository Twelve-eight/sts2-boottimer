using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;

using Godot;

using HarmonyLib;

using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace BootTimer.BootTimerCode;

/// <summary>
/// Diagnostic mod: trustworthy boot spans for the 2026-09-15 performance plan (task OBS-1).
///
/// Measurement contract (schema sts-performance-v1):
/// - Monotonic timing uses System.Diagnostics.Stopwatch (GetTimestamp + Frequency). Durations are
///   computed from monotonic ticks only; UTC wall time is emitted alongside strictly as a
///   correlation stamp for cross-log joins, never as the duration basis.
/// - Every span carries: invocation id, parent invocation id, start/end thread ids, monotonic
///   start/end ticks, duration in ms and the UTC correlation stamp.
/// - Phases that were never observed (observer installed late, hook not bindable, milestone not
///   reached before the summary safe point) are reported as unobserved with an explicit reason -
///   never as zero.
/// - Success latches represent COMPLETED operations only. Failures are recorded as failures with
///   a bounded exception summary.
/// - A Task returning is not asynchronous completion: the preload queue drain latch is armed by a
///   continuation on the returned session Task, so it fires on real completion (success, fault or
///   cancel), and its thread identity is recorded.
///
/// Config: diagnostics are enabled by default. Setting the environment variable BOOTTIMER_DIAG to
/// "0" or "false" (case-insensitive) disables everything: no Harmony patches are installed at all,
/// so there is no formatting, no sampling and no allocation on any path. The probe is read-only;
/// the mod never writes settings, files or config.
///
/// Output:
/// - Bounded trace: one "[BootTimer] ..." line per span/marker, capped per session (suppressed
///   lines are counted and reported in the summary).
/// - Compact summary block "[BootTimer][SUMMARY] ..." written at bounded safe points (main menu
///   visible, first combat room ready, first effect instantiate), once each, containing span
///   names, monotonic ticks, Stopwatch.Frequency, thread ids, duration ms, invocation ids,
///   unobserved metrics as null with a reason, the UTC correlation stamp, hook binding states and
///   the cumulative observer-overhead counter so a paired run can subtract observer cost.
///
/// Remove this mod after diagnosis (same mod id "BootTimer", same manifest, no new dependencies).
/// </summary>
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "BootTimer";
    public const string ModVersion = "0.1.0"; // keep in sync with BootTimer.json

    public static void Initialize()
    {
        BootDiag.Init();

        long now = Stopwatch.GetTimestamp();
        BootDiag.LatchMilestone(
            "boot-timer-initializer", null, now, now, true, null,
            "observer install point; phases before this tick are unobserved (null), never zero"
            + "; mod_version=" + ModVersion
            + "; config BOOTTIMER_DIAG=" + BootDiag.ConfigRawValue);

        if (!BootDiag.Enabled)
        {
            BootDiag.Emit($"[{ModId}] diagnostics disabled (BOOTTIMER_DIAG={BootDiag.ConfigRawValue}); no patches installed, no tracing, no per-frame work (utc {DateTimeOffset.UtcNow:HH:mm:ss.fff})");
            return;
        }

        var harmony = new Harmony(ModId);
        BootDiag.HarmonyInstance = harmony;

        // Install each engine hook independently so one missing engine member cannot take down
        // the other observations; failures are recorded, never silently skipped.
        foreach (var patchType in new[]
                 {
                     typeof(TryLoadModPatch),
                     typeof(LocManagerInitPatch),
                     typeof(MainMenuReadyPatch),
                     typeof(CombatRoomReadyPatch),
                     typeof(PreloadSubmitPatch),
                 })
        {
            try
            {
                harmony.CreateClassProcessor(patchType).Patch();
            }
            catch (Exception e)
            {
                BootDiag.RecordFailure("hook-install", e, "hook=" + patchType.Name + "; remaining hooks still install independently");
            }
        }

        RegentFxBinder.TryBind(harmony, "initializer");

        BootDiag.RecordMarker("hooks-installed", "engine attribute patches + RegentFX reflection binder armed");
        BootDiag.Emit($"[{ModId}] hooks installed (inclusive TryLoadMod spans, isolated RegentFX LoadScenes/collector/GenVFXNode where bindable, menu/room/queue milestones; utc {DateTimeOffset.UtcNow:HH:mm:ss.fff})");
    }
}

/// <summary>
/// Central diagnostic state and formatting for the BootTimer mod.
///
/// Threading: all registry mutations and trace emissions happen under one small lock (Gate).
/// Per-invocation span state never touches the registry mid-span, so nested or concurrent
/// invocations cannot cross-contaminate; only the completed record is appended.
///
/// Observer overhead: every public BootDiag entry point measures its own duration and accumulates
/// it into ObserverTicks. This is a LOWER bound (hook-side parameter reads and hot early-out
/// checks are not counted). Exposed in every summary so a paired run can subtract observer cost.
/// </summary>
internal static class BootDiag
{
    internal const string ConfigEnvVar = "BOOTTIMER_DIAG";
    internal const string SchemaName = "sts-performance-v1";

    internal const int MaxTraceLines = 200;      // per-session cap for one-line-per-span trace
    internal const int MaxStoredSpans = 256;     // per-session cap for spans kept for the summary
    internal const int MaxModsListed = 24;       // mod ids listed individually before overflow count
    internal const int MaxSummaryWrites = 4;     // bounded summary blocks per session
    internal const int MaxRegentScanAttempts = 5;// bounded reflection-binding attempts
    internal const int MaxNoteLen = 120;
    internal const int MaxExceptionLen = 200;

    private static volatile bool _enabled;
    private static int _initDone;
    internal static string ConfigRawValue = "<unset>";

    internal static long InstallTick;
    internal static string InstallUtc = "";
    internal static long Frequency;
    internal static long ObserverTicks;
    internal static Harmony? HarmonyInstance;

    private static readonly object Gate = new();
    private static readonly List<SpanRecord> Spans = new();
    private static readonly Dictionary<string, MilestoneRecord> Milestones = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> HookStates = new(StringComparer.Ordinal);
    private static readonly List<string> ModIds = new();

    private static long _invSeq;
    private static int _traceLines;
    private static int _traceSuppressed;
    private static int _spansOverflow;
    private static int _modsOverflow;
    private static int _summaryWrites;

    private static volatile bool _firstEffectLoadDone;
    private static volatile bool _firstInstantiateDone;

    [ThreadStatic] private static InvocationCtx? _invTop;

    // Fixed milestone order + default unobserved reasons for the summary. A milestone that never
    // latched is emitted as observed=false with its reason; it is never reported as zero.
    private static readonly (string Name, string Reason)[] ExpectedMilestones =
    {
        ("boot-timer-initializer", "not recorded (diagnostics were disabled before install)"),
        ("regentfx-tryloadmod", "no TryLoadMod invocation for mod id 'RegentFX' was observed (mod absent, loaded before the observer installed, or a different manifest id)"),
        ("regentfx-loadscenes", "LoadScenes was not invoked by summary time (Setting.PreloadEffects=false, suppressed by RegentFXFastBoot, RegentFX absent, or hook binding failed - see hooks line)"),
        ("regentfx-asset-collector", "CollectAssetPathsSafely was not invoked (it only runs inside LoadScenes)"),
        ("main-menu-visible", "main menu was not reached by summary time"),
        ("preload-queue-drained", "no NAssetLoader background session was both submitted and completed after the observer installed; sessions submitted earlier are unobserved, not zero"),
        ("first-effect-scene-load", "no lazy engine-cache fetch (AssetCache.GetScene/GetAsset) inside a RegentFX invocation context was observed; with a working preload the per-scene load cost is folded into the regentfx-loadscenes span"),
        ("first-effect-instantiate", "no node instantiation via the non-generic GenVFXNode(string) overload was observed; generic GenVFXNode<T> callers cannot be patched (open generic) - see hooks line"),
        ("first-combat-room-ready", "no NCombatRoom became ready by summary time"),
    };

    internal static void Init()
    {
        if (Interlocked.Exchange(ref _initDone, 1) != 0)
        {
            return;
        }

        InstallTick = Stopwatch.GetTimestamp();
        InstallUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        Frequency = Stopwatch.Frequency;

        var raw = System.Environment.GetEnvironmentVariable(ConfigEnvVar);
        ConfigRawValue = raw ?? "<unset>";
        _enabled = !string.Equals(raw, "0", StringComparison.Ordinal)
                   && !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool Enabled => _enabled;
    internal static bool FirstInstantiateDone => _firstInstantiateDone;
    internal static bool FirstEffectLoadDone => _firstEffectLoadDone;

    internal static void Emit(string line)
    {
        // A broken log sink must never break the game.
        try
        {
            MegaCrit.Sts2.Core.Logging.Log.Info(line);
        }
        catch
        {
            // swallowed by design (diagnostic observer)
        }
    }

    // ------------------------------------------------------------------
    // Invocation context (invocation-local state carried through __state)
    // ------------------------------------------------------------------

    internal sealed class InvocationCtx
    {
        internal long Id;
        internal long ParentId;
        internal string Hook = "";
        internal long StartTick;
        internal int ThreadId;
        internal InvocationCtx? Parent;
    }

    internal static InvocationCtx BeginInvocation(string hook)
    {
        long o0 = Stopwatch.GetTimestamp();
        try
        {
            var parent = _invTop;
            var ctx = new InvocationCtx
            {
                Id = Interlocked.Increment(ref _invSeq),
                ParentId = parent?.Id ?? 0,
                Hook = hook,
                StartTick = Stopwatch.GetTimestamp(),
                ThreadId = System.Environment.CurrentManagedThreadId,
                Parent = parent,
            };
            _invTop = ctx;
            return ctx;
        }
        finally
        {
            AddObserverOverhead(o0);
        }
    }

    internal static void EndInvocation(InvocationCtx? ctx)
    {
        // Stack discipline: pop exactly the context pushed by the matching prefix.
        if (ctx != null)
        {
            _invTop = ctx.Parent;
        }
    }

    /// <summary>True while the current thread is inside a RegentFX load/instantiate invocation.</summary>
    internal static bool InsideRegentFxLoad()
    {
        for (var c = _invTop; c != null; c = c.Parent)
        {
            if (c.Hook == "regentfx-loadscenes" || c.Hook == "regentfx-genvfxnode")
            {
                return true;
            }
        }

        return false;
    }

    internal static void AddObserverOverhead(long startTick)
        => Interlocked.Add(ref ObserverTicks, Stopwatch.GetTimestamp() - startTick);

    // ------------------------------------------------------------------
    // Records
    // ------------------------------------------------------------------

    internal sealed class SpanRecord
    {
        internal string Name = "";
        internal string Kind = "";
        internal long StartTick;
        internal long EndTick;
        internal int StartThreadId;
        internal int EndThreadId;
        internal long InvocationId;
        internal long ParentInvocationId;
        internal bool Completed;
        internal string? FailureSummary;
        internal string? Note;
        internal string Utc = "";
    }

    internal sealed class MilestoneRecord
    {
        internal string Name = "";
        internal long StartTick;
        internal long EndTick;
        internal int StartThreadId;
        internal int EndThreadId;
        internal long InvocationId;
        internal long ParentInvocationId;
        internal bool Completed;
        internal string? FailureSummary;
        internal string? Note;
        internal string Utc = "";
    }

    // ------------------------------------------------------------------
    // Recording (all bodies are exception-proof: an observer must never break the game)
    // ------------------------------------------------------------------

    internal static void RecordSpan(
        string name, string kind, InvocationCtx? ctx, long startTick, long endTick,
        bool completed, Exception? error, string? note, int? startThreadId = null)
    {
        if (!_enabled)
        {
            return;
        }

        long o0 = Stopwatch.GetTimestamp();
        try
        {
            var rec = new SpanRecord
            {
                Name = name,
                Kind = kind,
                StartTick = startTick,
                EndTick = endTick,
                StartThreadId = startThreadId ?? ctx?.ThreadId ?? System.Environment.CurrentManagedThreadId,
                EndThreadId = System.Environment.CurrentManagedThreadId,
                InvocationId = ctx?.Id ?? 0,
                ParentInvocationId = ctx?.ParentId ?? 0,
                Completed = completed && error == null,
                FailureSummary = error != null ? SummarizeException(error) : null,
                Note = ClampNote(note),
                Utc = CorrelationUtc(),
            };
            lock (Gate)
            {
                if (Spans.Count < MaxStoredSpans)
                {
                    Spans.Add(rec);
                }
                else
                {
                    _spansOverflow++;
                }

                EmitTraceLocked(rec);
            }
        }
        catch
        {
            // swallowed by design (diagnostic observer)
        }
        finally
        {
            AddObserverOverhead(o0);
        }
    }

    internal static void RecordMarker(string name, string? note)
    {
        long now = Stopwatch.GetTimestamp();
        RecordSpan(name, "marker", null, now, now, true, null, note);
    }

    internal static void RecordFailure(string name, Exception error, string? note)
    {
        long now = Stopwatch.GetTimestamp();
        RecordSpan(name, "marker", null, now, now, false, error, note);
    }

    /// <summary>
    /// First-wins success/failure latch for a milestone. Returns true when this call created the
    /// record (so the caller can run one-shot side effects like a summary write).
    /// </summary>
    internal static bool LatchMilestone(
        string name, InvocationCtx? ctx, long startTick, long endTick,
        bool completed, Exception? error, string? note, int? startThreadId = null)
    {
        if (!_enabled)
        {
            return false;
        }

        long o0 = Stopwatch.GetTimestamp();
        try
        {
            lock (Gate)
            {
                if (Milestones.ContainsKey(name))
                {
                    return false;
                }

                var m = new MilestoneRecord
                {
                    Name = name,
                    StartTick = startTick,
                    EndTick = endTick,
                    StartThreadId = startThreadId ?? ctx?.ThreadId ?? System.Environment.CurrentManagedThreadId,
                    EndThreadId = System.Environment.CurrentManagedThreadId,
                    InvocationId = ctx?.Id ?? 0,
                    ParentInvocationId = ctx?.ParentId ?? 0,
                    Completed = completed && error == null,
                    FailureSummary = error != null ? SummarizeException(error) : null,
                    Note = ClampNote(note),
                    Utc = CorrelationUtc(),
                };
                Milestones[name] = m;

                if (name == "first-effect-scene-load")
                {
                    _firstEffectLoadDone = true;
                }

                if (name == "first-effect-instantiate")
                {
                    _firstInstantiateDone = true;
                }

                if (_traceLines < MaxTraceLines)
                {
                    _traceLines++;
                    Emit("[BootTimer] " + FormatRecord(
                        "milestone", name, "milestone", m.InvocationId, m.ParentInvocationId,
                        m.StartTick, m.EndTick, m.StartThreadId, m.EndThreadId,
                        m.Completed, m.FailureSummary, m.Note, m.Utc));
                }
                else
                {
                    _traceSuppressed++;
                }

                return true;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            AddObserverOverhead(o0);
        }
    }

    internal static void FailMilestone(string name, Exception error, string? note)
    {
        long now = Stopwatch.GetTimestamp();
        LatchMilestone(name, null, now, now, false, error, note);
    }

    internal static void NoteModSeen(string modId)
    {
        if (!_enabled || string.IsNullOrEmpty(modId))
        {
            return;
        }

        lock (Gate)
        {
            if (ModIds.Contains(modId))
            {
                return;
            }

            if (ModIds.Count < MaxModsListed)
            {
                ModIds.Add(modId);
            }
            else
            {
                _modsOverflow++;
            }
        }
    }

    internal static void SetHookState(string key, string value)
    {
        if (!_enabled)
        {
            return;
        }

        lock (Gate)
        {
            HookStates[key] = value;
        }
    }

    // ------------------------------------------------------------------
    // Trace emission (bounded) + shared key=value formatting
    // ------------------------------------------------------------------

    private static void EmitTraceLocked(SpanRecord rec)
    {
        if (_traceLines >= MaxTraceLines)
        {
            _traceSuppressed++;
            return;
        }

        _traceLines++;
        Emit("[BootTimer] " + FormatRecord(
            "span", rec.Name, rec.Kind, rec.InvocationId, rec.ParentInvocationId,
            rec.StartTick, rec.EndTick, rec.StartThreadId, rec.EndThreadId,
            rec.Completed, rec.FailureSummary, rec.Note, rec.Utc));
    }

    private static string FormatRecord(
        string label, string name, string kind, long inv, long parent, long t0, long t1,
        int thr0, int thr1, bool completed, string? fail, string? note, string utc)
    {
        long freq = Frequency > 0 ? Frequency : 1;
        double ms = (t1 - t0) * 1000.0 / freq;
        var sb = new StringBuilder(192);
        sb.Append(label).Append('=').Append(name);
        sb.Append(" kind=").Append(kind);
        sb.Append(" inv=").Append(InvStr(inv));
        sb.Append(" parent=").Append(InvStr(parent));
        sb.Append(" t0=").Append(t0.ToString(CultureInfo.InvariantCulture));
        sb.Append(" t1=").Append(t1.ToString(CultureInfo.InvariantCulture));
        sb.Append(" ms=").Append(ms.ToString("F3", CultureInfo.InvariantCulture));
        sb.Append(" freq=").Append(Frequency.ToString(CultureInfo.InvariantCulture));
        sb.Append(" thr=").Append(thr0.ToString(CultureInfo.InvariantCulture));
        sb.Append(" thr1=").Append(thr1.ToString(CultureInfo.InvariantCulture));
        sb.Append(" utc=").Append(utc);
        sb.Append(" status=").Append(completed ? "completed" : "failed");
        if (fail != null)
        {
            sb.Append(" fail=\"").Append(fail).Append('"');
        }

        if (note != null)
        {
            sb.Append(" note=\"").Append(note).Append('"');
        }

        return sb.ToString();
    }

    private static string InvStr(long v) => v <= 0 ? "none" : "I" + v.ToString(CultureInfo.InvariantCulture);

    internal static string SummarizeException(Exception e)
    {
        try
        {
            var s = e.GetType().Name + ": " + e.Message;
            s = s.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');
            return s.Length <= MaxExceptionLen ? s : s.Substring(0, MaxExceptionLen);
        }
        catch
        {
            return "exception-summary-unavailable";
        }
    }

    private static string? ClampNote(string? note)
    {
        if (note == null)
        {
            return null;
        }

        try
        {
            var s = note.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');
            return s.Length <= MaxNoteLen ? s : s.Substring(0, MaxNoteLen);
        }
        catch
        {
            return null;
        }
    }

    internal static string CorrelationUtc()
        => DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------
    // Compact offline summary (bounded number of writes, at safe points only)
    // ------------------------------------------------------------------

    internal static void WriteSummary(string trigger)
    {
        if (!_enabled)
        {
            return;
        }

        long o0 = Stopwatch.GetTimestamp();
        try
        {
            lock (Gate)
            {
                if (_summaryWrites >= MaxSummaryWrites)
                {
                    return;
                }

                _summaryWrites++;

                long obsTicks = Interlocked.Read(ref ObserverTicks);
                long freq = Frequency > 0 ? Frequency : 1;
                double obsMs = obsTicks * 1000.0 / freq;

                Emit("[BootTimer][SUMMARY] schema=" + SchemaName
                     + " trigger=" + trigger
                     + " summary_utc=" + DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                     + " freq=" + Frequency.ToString(CultureInfo.InvariantCulture)
                     + " install_tick=" + InstallTick.ToString(CultureInfo.InvariantCulture)
                     + " install_utc=" + InstallUtc
                     + " observer_ticks=" + obsTicks.ToString(CultureInfo.InvariantCulture)
                     + " observer_ms=" + obsMs.ToString("F3", CultureInfo.InvariantCulture)
                     + " observer_note=\"cumulative time inside BootDiag instrumentation, lower bound; subtract in paired runs\""
                     + " invocations=" + _invSeq.ToString(CultureInfo.InvariantCulture)
                     + " trace_lines=" + _traceLines.ToString(CultureInfo.InvariantCulture)
                     + " trace_suppressed=" + _traceSuppressed.ToString(CultureInfo.InvariantCulture)
                     + " spans_stored=" + Spans.Count.ToString(CultureInfo.InvariantCulture)
                     + " spans_overflow=" + _spansOverflow.ToString(CultureInfo.InvariantCulture)
                     + " summary_write=" + _summaryWrites.ToString(CultureInfo.InvariantCulture));
                Emit("[BootTimer][SUMMARY] coverage=\"ticks are Stopwatch.GetTimestamp values; phases before install_tick are unobserved (null), never zero; UTC is correlation only\"");

                foreach (var (name, reason) in ExpectedMilestones)
                {
                    if (Milestones.TryGetValue(name, out var m))
                    {
                        Emit("[BootTimer][SUMMARY] " + FormatRecord(
                            "milestone", name, "milestone", m.InvocationId, m.ParentInvocationId,
                            m.StartTick, m.EndTick, m.StartThreadId, m.EndThreadId,
                            m.Completed, m.FailureSummary, m.Note, m.Utc));
                    }
                    else
                    {
                        Emit("[BootTimer][SUMMARY] milestone=" + name + " observed=false reason=\"" + reason + "\"");
                    }
                }

                foreach (var s in Spans)
                {
                    Emit("[BootTimer][SUMMARY] " + FormatRecord(
                        "span", s.Name, s.Kind, s.InvocationId, s.ParentInvocationId,
                        s.StartTick, s.EndTick, s.StartThreadId, s.EndThreadId,
                        s.Completed, s.FailureSummary, s.Note, s.Utc));
                }

                Emit("[BootTimer][SUMMARY] mods_seen=" + (ModIds.Count + _modsOverflow).ToString(CultureInfo.InvariantCulture)
                     + " overflow=" + _modsOverflow.ToString(CultureInfo.InvariantCulture)
                     + " ids=\"" + string.Join(",", ModIds) + "\""
                     + " pre_observer_mods=unknown(reason=\"mods loaded before the observer installed are not enumerable via the accessible engine API\")"
                     + " boottimer_self_span=unobserved(reason=\"patches install inside BootTimer's own TryLoadMod invocation\")");
                Emit(HooksLine());
                Emit("[BootTimer][SUMMARY-END] trigger=" + trigger);
            }
        }
        catch
        {
            // swallowed by design (diagnostic observer)
        }
        finally
        {
            AddObserverOverhead(o0);
        }
    }

    private static string HooksLine()
    {
        var sb = new StringBuilder(384);
        sb.Append("[BootTimer][SUMMARY] hooks");
        foreach (var key in new[]
                 {
                     "loadscenes", "collector", "genvfx_nongeneric", "genvfx_generic", "scenefetch",
                     "scenefetch_method", "regentfx_assembly", "fastboot_assembly",
                     "binding_attempts", "binding_last_trigger", "binding_terminal",
                 })
        {
            if (HookStates.TryGetValue(key, out var v))
            {
                sb.Append(' ').Append(key).Append('=');
                if (v.Contains(' '))
                {
                    sb.Append('"').Append(v).Append('"');
                }
                else
                {
                    sb.Append(v);
                }
            }
        }

        foreach (var kv in HookStates)
        {
            if (kv.Key.StartsWith("reason_", StringComparison.Ordinal))
            {
                sb.Append(' ').Append(kv.Key).Append("=\"").Append(kv.Value).Append('"');
            }
        }

        sb.Append(" modscene_cache_count=").Append(RegentFxBinder.ModSceneCacheCountString());
        return sb.ToString();
    }
}

/// <summary>
/// Hook: ModManager.TryLoadMod - per-mod INCLUSIVE load span.
///
/// Producer: engine ModManager, one invocation per mod load attempt; the span is INCLUSIVE of
///   assembly load, PCK mount, FMOD bank work, Harmony patching and the mod initializer
///   (TryLoadMod loads the assembly and runs the initializer inside the same method).
/// Owner: BootTimer. First consumer: BootDiag trace lines and the summary span list.
/// Nested/concurrent safety: per-invocation __state (each call gets its own State object), so
///   nested or interleaved TryLoadMod invocations can never cross-contaminate spans.
/// Failure awareness: Harmony FINALIZER (not only Postfix) closes the span even when the target
///   throws; the finalizer returns void, so the original exception propagates unchanged while a
///   bounded copy is recorded.
/// Cleanup point: process-lifetime patch; removed by uninstalling the mod; no per-frame work.
/// </summary>
[HarmonyPatch(typeof(ModManager), "TryLoadMod")]
internal static class TryLoadModPatch
{
    internal sealed class State
    {
        internal BootDiag.InvocationCtx? Inv;
        internal string ModId = "?";
    }

    private static void Prefix(Mod mod, ref State __state)
    {
        try
        {
            var inv = BootDiag.BeginInvocation("mod-tryloadmod");
            __state = new State { Inv = inv, ModId = mod.manifest?.id ?? "?" };
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("mod-tryloadmod-prefix-failed", e, null);
        }
    }

    private static void Finalizer(Exception __exception, Mod mod, State? __state)
    {
        try
        {
            BootDiag.EndInvocation(__state?.Inv);
            var st = __state;
            if (st == null || st.Inv == null)
            {
                BootDiag.RecordFailure(
                    "mod-tryloadmod", new InvalidOperationException("prefix did not run"), "span start missing");
                return;
            }

            var inv = st.Inv;
            long t1 = Stopwatch.GetTimestamp();
            // state_after is advisory: it is what the field holds at TryLoadMod exit; the engine
            // may finalize ModLoadState after TryLoadMod returns, so it is not a success latch.
            var note = "mod=" + st.ModId + " source=" + mod.modSource + " state_after=" + mod.state;
            BootDiag.RecordSpan("mod-tryloadmod[" + st.ModId + "]", "span", inv, inv.StartTick, t1, __exception == null, __exception, note);
            BootDiag.NoteModSeen(st.ModId);

            if (st.ModId == "RegentFX")
            {
                BootDiag.LatchMilestone(
                    "regentfx-tryloadmod", inv, inv.StartTick, t1, __exception == null, __exception,
                    "inclusive span (assembly+PCK+FMOD+Harmony+initializer); state_after advisory");
            }

            if (st.ModId.Contains("RegentFX", StringComparison.Ordinal))
            {
                // RegentFX or RegentFXFastBoot just finished TryLoadMod: try to bind the
                // RegentFX-specific observation hooks now (the assembly is loaded by the time
                // TryLoadMod returns). Bounded attempts; terminal outcomes stop retrying.
                var harmony = BootDiag.HarmonyInstance;
                if (harmony != null)
                {
                    RegentFxBinder.TryBind(harmony, "tryloadmod:" + st.ModId);
                }
            }
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("mod-tryloadmod-finalizer-failed", e, null);
        }
    }
}

/// <summary>
/// Hook: LocManager.Initialize - loc tables merged marker (success-only).
///
/// Producer: engine LocManager.Initialize. Owner: BootTimer.
/// First consumer: BootDiag trace. Cleanup point: process-lifetime patch; removed with the mod.
/// Failure awareness: finalizer; the marker fires only when Initialize completed without
///   exception (a failed merge must not claim DONE).
/// </summary>
[HarmonyPatch(typeof(LocManager), "Initialize")]
internal static class LocManagerInitPatch
{
    private static void Finalizer(Exception __exception)
    {
        try
        {
            if (__exception != null)
            {
                BootDiag.RecordFailure("loc-initialize", __exception, "LocManager.Initialize threw");
            }
            else
            {
                BootDiag.RecordMarker("loc-initialized", "all loc tables merged");
            }
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("loc-initialize-finalizer-failed", e, null);
        }
    }
}

/// <summary>
/// Hook: NMainMenu._Ready - main menu visible latch (user-visible boot complete).
///
/// Producer: engine NMainMenu node readiness; the menu counts as visible only when _Ready
///   completed without exception.
/// Owner: BootTimer. First consumer: "main-menu-visible" milestone + first summary write
///   (designated safe point).
/// Failure awareness: finalizer; a thrown _Ready is recorded as a milestone failure.
/// Cleanup point: process-lifetime; first-wins latch; no per-frame work.
/// </summary>
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
internal static class MainMenuReadyPatch
{
    private static void Finalizer(Exception __exception)
    {
        try
        {
            if (__exception != null)
            {
                BootDiag.FailMilestone("main-menu-visible", __exception, "NMainMenu._Ready threw; original exception propagates unchanged");
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (BootDiag.LatchMilestone("main-menu-visible", null, now, now, true, null, "NMainMenu._Ready completed"))
            {
                // Final chance to bind RegentFX hooks if it loaded through a path whose
                // TryLoadMod was not observed (e.g. BootTimer sorted after RegentFX and the
                // initializer-scan missed it).
                var harmony = BootDiag.HarmonyInstance;
                if (harmony != null)
                {
                    RegentFxBinder.TryBind(harmony, "main-menu-visible");
                }

                BootDiag.WriteSummary("main-menu-visible");
            }
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("main-menu-visible-finalizer-failed", e, null);
        }
    }
}

/// <summary>
/// Hook: NCombatRoom._Ready - first combat room ready latch.
///
/// Producer: engine NCombatRoom node readiness (any room mode; mode is read via reflection-safe
///   Traverse and recorded when readable, no compile-time dependency on CombatRoomMode).
/// Owner: BootTimer. First consumer: "first-combat-room-ready" milestone + a summary write
///   (designated safe point).
/// Failure awareness: finalizer; only a completed _Ready latches the milestone.
/// Cleanup point: process-lifetime; first-wins latch; no per-frame work.
/// </summary>
[HarmonyPatch(typeof(NCombatRoom), "_Ready")]
internal static class CombatRoomReadyPatch
{
    private static void Finalizer(Exception __exception, NCombatRoom __instance)
    {
        try
        {
            if (__exception != null)
            {
                BootDiag.FailMilestone("first-combat-room-ready", __exception, "NCombatRoom._Ready threw; original exception propagates unchanged");
                return;
            }

            string mode;
            try
            {
                mode = __instance == null
                    ? "unavailable"
                    : Traverse.Create(__instance).Property("Mode").GetValue()?.ToString() ?? "unavailable";
            }
            catch
            {
                mode = "unavailable";
            }

            long now = Stopwatch.GetTimestamp();
            if (BootDiag.LatchMilestone("first-combat-room-ready", null, now, now, true, null, "mode=" + mode))
            {
                BootDiag.WriteSummary("first-combat-room-ready");
            }
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("first-combat-room-ready-finalizer-failed", e, null);
        }
    }
}

/// <summary>
/// Hook: NAssetLoader.LoadInTheBackground - preload queue observation.
///
/// Producer: engine NAssetLoader; each call enqueues one AssetLoadingSession into the background
///   preload queue.
/// Owner: BootTimer. First consumer: "preload-queue-drained" milestone.
/// Completion semantics (hard requirement): the Task RETURN is not asynchronous completion. The
///   drain latch is armed by a continuation on the returned session Task, so it fires when the
///   session ACTUALLY completes (success, fault or cancellation), on whatever thread the
///   continuation runs; both the submit thread and the completion thread are recorded.
/// Late observation honesty: only sessions submitted after this patch installed are counted;
///   sessions submitted earlier are unobserved with that reason, never zero.
/// Cleanup point: each continuation holds only its own session until completion; the outstanding
///   counter is Interlocked-maintained; first-wins latch; no per-frame work.
/// </summary>
[HarmonyPatch(typeof(NAssetLoader), nameof(NAssetLoader.LoadInTheBackground))]
internal static class PreloadSubmitPatch
{
    private static int _outstanding;       // observed sessions submitted but not yet completed
    private static int _observedSessions;
    private static long _firstSubmitTick;
    private static int _firstSubmitThread;
    private static int _drainLatched;

    private static void Finalizer(Exception __exception, AssetLoadingSession session, Task<bool>? __result)
    {
        try
        {
            if (__exception != null)
            {
                BootDiag.RecordFailure("preload-submit", __exception, "session=" + SessionName(session));
                return;
            }

            if (__result == null)
            {
                BootDiag.RecordMarker("preload-submit", "LoadInTheBackground returned no task; session untracked");
                return;
            }

            long submitTick = Stopwatch.GetTimestamp();
            int submitThread = System.Environment.CurrentManagedThreadId;
            int index = Interlocked.Increment(ref _observedSessions);
            Interlocked.Increment(ref _outstanding);
            Interlocked.CompareExchange(ref _firstSubmitTick, submitTick, 0);
            Interlocked.CompareExchange(ref _firstSubmitThread, submitThread, 0);
            BootDiag.RecordMarker("preload-session-submitted", "index=" + index + " session=" + SessionName(session));

            __result.ContinueWith(t =>
            {
                try
                {
                    long doneTick = Stopwatch.GetTimestamp();
                    int remaining = Interlocked.Decrement(ref _outstanding);
                    bool completedOk = t.Status == TaskStatus.RanToCompletion;
                    Exception? err = t.IsFaulted ? t.Exception?.GetBaseException() : null;
                    BootDiag.RecordSpan(
                        "preload-session", "span", null, submitTick, doneTick, completedOk, err,
                        "index=" + index + " session=" + SessionName(session)
                        + " task_status=" + t.Status + " remaining=" + remaining,
                        submitThread);

                    if (remaining != 0)
                    {
                        return;
                    }

                    if (Interlocked.CompareExchange(ref _drainLatched, 1, 0) == 0)
                    {
                        long first = Interlocked.Read(ref _firstSubmitTick);
                        BootDiag.RecordSpan(
                            "preload-queue", "span", null, first, doneTick, true, null,
                            "observed_sessions=" + index
                            + "; sessions submitted before the observer installed are unobserved, not zero",
                            _firstSubmitThread);
                        BootDiag.LatchMilestone(
                            "preload-queue-drained", null, first, doneTick, true, null,
                            "all observed background sessions completed; observed_sessions=" + index,
                            _firstSubmitThread);
                    }
                    else
                    {
                        BootDiag.RecordMarker("preload-queue-redrained", "queue emptied again after the first drain latch");
                    }
                }
                catch (Exception e)
                {
                    BootDiag.RecordFailure("preload-session-continuation-failed", e, null);
                }
            }, TaskScheduler.Default);
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("preload-submit-finalizer-failed", e, null);
        }
    }

    /// <summary>
    /// Session display name. _name is a private engine field (confirmed by the 2026-09-15 engine
    /// decompile); it is read via Harmony Traverse (reflection, compile-safe) once per submission
    /// and once per completion, never on any per-frame path.
    /// </summary>
    private static string SessionName(AssetLoadingSession? session)
    {
        if (session == null)
        {
            return "null";
        }

        try
        {
            return Traverse.Create(session).Field("_name").GetValue<string>() ?? "unavailable";
        }
        catch
        {
            return "unavailable";
        }
    }
}

/// <summary>
/// Reflection binder for the RegentFX-specific hooks and the engine AssetCache scene-fetch hook.
///
/// No compile-time reference to RegentFX (it is an optional third-party mod): types are resolved
/// by name at runtime, mirroring the proven FastBoot discovery pattern (assembly name contains
/// "RegentFX" but is not "RegentFXFastBoot", then type "RegentFX.Scripts.Entry"). Every binding
/// outcome (bound / method missing / patch failed) is recorded as a hook state with a reason; a
/// hook that cannot be bound leaves its milestone unobserved with that reason - never zero.
///
/// Producer: mod load sequence (the RegentFX assembly appears during ModManager.TryLoadMod, or is
///   already loaded when BootTimer initializes late in the order).
/// Owner: BootTimer. First consumer: BootDiag spans/milestones + the summary hooks line.
/// Cleanup point: process-lifetime patches; removed by uninstalling the mod; the number of scan
///   attempts is bounded (MaxRegentScanAttempts) and binding stops after a terminal outcome.
/// </summary>
internal static class RegentFxBinder
{
    private static int _attempts;
    private static bool _terminal;
    internal static System.Collections.IDictionary? ModSceneCache;

    internal static void TryBind(Harmony harmony, string trigger)
    {
        if (_terminal || !BootDiag.Enabled)
        {
            return;
        }

        if (Interlocked.Increment(ref _attempts) > BootDiag.MaxRegentScanAttempts)
        {
            _terminal = true;
            BootDiag.SetHookState("binding_attempts", BootDiag.MaxRegentScanAttempts.ToString(CultureInfo.InvariantCulture));
            BootDiag.SetHookState("binding_terminal", "scan cap reached without RegentFX");
            return;
        }

        BootDiag.SetHookState("binding_attempts", _attempts.ToString(CultureInfo.InvariantCulture));
        BootDiag.SetHookState("binding_last_trigger", trigger);
        try
        {
            Bind(harmony);
        }
        catch (Exception e)
        {
            _terminal = true;
            BootDiag.SetHookState("binding_terminal", "exception: " + BootDiag.SummarizeException(e));
            BootDiag.RecordFailure("regentfx-binding", e, "trigger=" + trigger);
        }
    }

    private static void Bind(Harmony harmony)
    {
        Assembly? regent = null;
        var fastBootPresent = false;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name == null)
            {
                continue;
            }

            if (name == "RegentFXFastBoot")
            {
                fastBootPresent = true;
                continue;
            }

            if (regent == null && name.Contains("RegentFX", StringComparison.Ordinal))
            {
                regent = asm;
            }
        }

        BootDiag.SetHookState("fastboot_assembly", fastBootPresent ? "present" : "absent");
        if (regent == null)
        {
            BootDiag.SetHookState("regentfx_assembly", "not-found");
            // Not terminal: a later trigger may run after RegentFX actually loads.
            return;
        }

        BootDiag.SetHookState("regentfx_assembly", "found");

        var entryType = regent.GetType("RegentFX.Scripts.Entry");
        if (entryType == null)
        {
            _terminal = true;
            BootDiag.SetHookState("regentfx_assembly", "found");
            BootDiag.SetHookState("reason_regentfx_type", "RegentFX.Scripts.Entry not found in assembly (version changed)");
            return;
        }

        // Entry.LoadScenes (private static void) - the inclusive RegentFX preload method observed
        // in ISOLATION (the TryLoadMod span also contains assembly/PCK/FMOD/Harmony work).
        var loadScenes = AccessTools.Method(entryType, "LoadScenes");
        BindHook(harmony, loadScenes, "loadscenes",
            Hook(nameof(RegentFxHooks.LoadScenesPrefix)), Hook(nameof(RegentFxHooks.LoadScenesFinalizer)),
            priority: 800);

        // Entry.CollectAssetPathsSafely (private static List<string>) - the reflection asset-path
        // collector; count of returned paths is recorded when it completes.
        var collector = AccessTools.Method(entryType, "CollectAssetPathsSafely");
        BindHook(harmony, collector, "collector",
            Hook(nameof(RegentFxHooks.CollectorPrefix)), Hook(nameof(RegentFxHooks.CollectorFinalizer)));

        // VFXUtil.GenVFXNode - non-generic overload only. The generic GenVFXNode<T> is an open
        // generic method definition, which Harmony cannot patch (it requires closed
        // instantiations), so those callers stay unobserved and the summary says so.
        MethodInfo? genVfx = null;
        var genericSeen = false;
        var vfxUtil = regent.GetType("RegentFX.Scripts.VFXUtil");
        if (vfxUtil != null)
        {
            foreach (var m in vfxUtil.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name != "GenVFXNode")
                {
                    continue;
                }

                if (m.IsGenericMethodDefinition)
                {
                    genericSeen = true;
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                {
                    genVfx = m;
                    break;
                }
            }
        }

        BootDiag.SetHookState("genvfx_generic", genericSeen ? "detected-unbound" : "not-found");
        if (genericSeen)
        {
            BootDiag.SetHookState("reason_genvfx_generic",
                "open generic method definition; Harmony requires closed instantiations, so GenVFXNode<T> callers are unobserved (the first-instantiate latch uses the non-generic overload)");
        }

        BindHook(harmony, genVfx, "genvfx_nongeneric",
            Hook(nameof(RegentFxHooks.GenVfxPrefix)), Hook(nameof(RegentFxHooks.GenVfxFinalizer)));

        // RegentFX's own scene cache is read (never written) once per summary write for
        // cache-state context (FastBoot suppression or failed preload is visible as count 0).
        ModSceneCache = AccessTools.Field(entryType, "ModSceneCache")?.GetValue(null) as System.Collections.IDictionary;

        // Engine AssetCache fetch hook: fires only when RegentFX's own cache misses and the engine
        // cache is consulted (the lazy first-use load). Bound by name with a documented fallback
        // (GetAsset, also tried when the GetScene patch itself fails, e.g. an unexpected return
        // type); if neither binds, the first-effect-scene-load milestone stays unobserved with a
        // reason. The hook body filters by RegentFX invocation context, not by path, so no
        // original parameter names are required.
        var getScene = AccessTools.Method(typeof(AssetCache), "GetScene", new[] { typeof(string) });
        var sceneBound = false;
        if (getScene != null)
        {
            sceneBound = BindHook(harmony, getScene, "scenefetch",
                Hook(nameof(RegentFxHooks.SceneFetchPrefix)), Hook(nameof(RegentFxHooks.SceneFetchFinalizerPacked)));
            if (sceneBound)
            {
                BootDiag.SetHookState("scenefetch_method", "AssetCache.GetScene");
            }
        }

        if (!sceneBound)
        {
            var getAsset = AccessTools.Method(typeof(AssetCache), "GetAsset", new[] { typeof(string) });
            if (getAsset != null)
            {
                sceneBound = BindHook(harmony, getAsset, "scenefetch",
                    Hook(nameof(RegentFxHooks.SceneFetchPrefix)), Hook(nameof(RegentFxHooks.SceneFetchFinalizerResource)));
                if (sceneBound)
                {
                    BootDiag.SetHookState("scenefetch_method", "AssetCache.GetAsset(fallback)");
                }
            }
            else if (getScene == null)
            {
                BootDiag.SetHookState("scenefetch", "unavailable");
                BootDiag.SetHookState("reason_scenefetch",
                    "neither AssetCache.GetScene(string) nor AssetCache.GetAsset(string) resolved by reflection; the first lazy scene load stays unobserved");
            }
        }

        _terminal = true; // one complete binding pass; never re-patch on later triggers
        BootDiag.SetHookState("binding_terminal", "complete");
    }

    private static MethodInfo Hook(string name)
        => AccessTools.Method(typeof(RegentFxHooks), name)
           ?? throw new InvalidOperationException("BootTimer hook method missing: " + name);

    private static bool BindHook(Harmony harmony, MethodInfo? target, string key, MethodInfo prefix, MethodInfo finalizer, int priority = 400)
    {
        if (target == null)
        {
            BootDiag.SetHookState(key, "unavailable");
            BootDiag.SetHookState("reason_" + key, "method could not be resolved by reflection (name or signature changed)");
            return false;
        }

        try
        {
            // priority=800 for the loadscenes pair: RegentFXFastBoot installs a
            // false-returning SkipLoadScenes prefix (default priority 400) that
            // suppresses the original. Harmony skips LOWER-priority prefixes
            // after a false return, so the observer must run ABOVE the
            // suppressor or its span evidence disappears exactly in B runs.
            harmony.Patch(target,
                prefix: new HarmonyMethod(prefix) { priority = priority },
                finalizer: new HarmonyMethod(finalizer) { priority = priority });
            BootDiag.SetHookState(key, priority == 800 ? "bound (priority=800)" : "bound");
            return true;
        }
        catch (Exception e)
        {
            BootDiag.SetHookState(key, "unavailable");
            BootDiag.SetHookState("reason_" + key, "patch failed: " + BootDiag.SummarizeException(e));
            return false;
        }
    }

    internal static string ModSceneCacheCountString()
    {
        try
        {
            var cache = ModSceneCache;
            if (cache == null)
            {
                return "unknown(reason=RegentFX ModSceneCache not bound)";
            }

            return cache.Count.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception e)
        {
            return "unknown(reason=" + BootDiag.SummarizeException(e) + ")";
        }
    }
}

/// <summary>
/// Hook method bodies for the reflection-bound RegentFX/engine targets.
///
/// All bodies are exception-proof: an exception escaping a Harmony finalizer would replace the
/// original target exception, so every body catches everything and records instead. The
/// finalizers return void, so recorded failures never swallow or replace the original exception.
///
/// Producer: RegentFX Entry/VFXUtil + engine AssetCache. Owner: BootTimer.
/// First consumer: BootDiag spans/milestones. Cleanup point: removed with the mod.
/// </summary>
internal static class RegentFxHooks
{
    internal sealed class State
    {
        internal BootDiag.InvocationCtx? Inv;
    }

    // ---- Entry.LoadScenes (the isolated RegentFX preload span) ----
    // Producer: RegentFX Entry.Init when Setting.PreloadEffects=true (runs whenever not
    //   suppressed). Isolated: measures only LoadScenes itself, unlike the inclusive TryLoadMod
    //   span that also contains assembly/PCK/FMOD/Harmony/initializer work. A near-zero duration
    //   while RegentFXFastBoot is present indicates the original was suppressed; cross-check
    //   modscene_cache_count in the summary.
    // Owner: BootTimer. First consumer: regentfx-loadscenes milestone + summary.
    // Cleanup point: process-lifetime patch; removed with the mod; no per-frame work.
    internal static void LoadScenesPrefix(ref State __state)
    {
        try
        {
            __state = new State { Inv = BootDiag.BeginInvocation("regentfx-loadscenes") };
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("regentfx-loadscenes-prefix-failed", e, null);
        }
    }

    internal static void LoadScenesFinalizer(Exception __exception, State? __state)
    {
        try
        {
            BootDiag.EndInvocation(__state?.Inv);
            var st = __state;
            if (st == null || st.Inv == null)
            {
                BootDiag.RecordFailure(
                    "regentfx-loadscenes", new InvalidOperationException("prefix did not run"), "span start missing");
                return;
            }

            var inv = st.Inv;
            long t1 = Stopwatch.GetTimestamp();
            BootDiag.RecordSpan(
                "regentfx-loadscenes", "span", inv, inv.StartTick, t1, __exception == null, __exception,
                "isolated preload span; per-scene ResourceLoader.Load calls are inside this span");
            if (__exception == null)
            {
                BootDiag.LatchMilestone(
                    "regentfx-loadscenes", inv, inv.StartTick, t1, true, null, "LoadScenes completed (isolated)");
            }
            else
            {
                BootDiag.FailMilestone(
                    "regentfx-loadscenes", __exception, "LoadScenes threw; original exception propagates unchanged");
            }
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("regentfx-loadscenes-finalizer-failed", e, null);
        }
    }

    // ---- Entry.CollectAssetPathsSafely (the RegentFX asset-path collector) ----
    // Producer: RegentFX LoadScenes (called once per preload). Owner: BootTimer.
    // First consumer: regentfx-asset-collector milestone + summary. Nested inside the LoadScenes
    //   invocation; per-invocation __state keeps the two spans independent.
    // Cleanup point: process-lifetime patch; removed with the mod; no per-frame work.
    internal static void CollectorPrefix(ref State __state)
    {
        try
        {
            __state = new State { Inv = BootDiag.BeginInvocation("regentfx-collector") };
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("regentfx-collector-prefix-failed", e, null);
        }
    }

    internal static void CollectorFinalizer(Exception __exception, List<string>? __result, State? __state)
    {
        try
        {
            BootDiag.EndInvocation(__state?.Inv);
            var st = __state;
            if (st == null || st.Inv == null)
            {
                BootDiag.RecordFailure(
                    "regentfx-asset-collector", new InvalidOperationException("prefix did not run"), "span start missing");
                return;
            }

            var inv = st.Inv;
            long t1 = Stopwatch.GetTimestamp();
            var note = __result != null ? "path_count=" + __result.Count : "result unavailable (threw)";
            BootDiag.RecordSpan(
                "regentfx-asset-collector", "span", inv, inv.StartTick, t1, __exception == null, __exception, note);
            if (__exception == null)
            {
                BootDiag.LatchMilestone(
                    "regentfx-asset-collector", inv, inv.StartTick, t1, true, null, note);
            }
            else
            {
                BootDiag.FailMilestone(
                    "regentfx-asset-collector", __exception, "collector threw; original exception propagates unchanged");
            }
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("regentfx-collector-finalizer-failed", e, null);
        }
    }

    // ---- VFXUtil.GenVFXNode(string) (non-generic overload; first effect instantiate) ----
    // Producer: RegentFX effect consumers (PlaySimple / PlaySimpleBack). This overload covers the
    //   non-generic call paths; the generic GenVFXNode<T> paths are unobservable (open generic).
    // Owner: BootTimer. First consumer: first-effect-instantiate milestone + a summary write.
    // Hot-path note: event-frequency (per VFX spawn), NOT per-frame. After the first successful
    //   instantiate latches, the prefix is a single bool read with no allocation and the
    //   finalizer exits on a null state.
    // Cleanup point: process-lifetime patch; removed with the mod.
    internal static void GenVfxPrefix(ref State __state)
    {
        try
        {
            if (BootDiag.FirstInstantiateDone)
            {
                return;
            }

            __state = new State { Inv = BootDiag.BeginInvocation("regentfx-genvfxnode") };
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("regentfx-genvfxnode-prefix-failed", e, null);
        }
    }

    internal static void GenVfxFinalizer(Exception __exception, Node2D? __result, State? __state)
    {
        try
        {
            var st = __state;
            if (st == null || st.Inv == null)
            {
                return; // post-latch fast path or prefix skipped
            }

            BootDiag.EndInvocation(st.Inv);
            var inv = st.Inv;
            long t1 = Stopwatch.GetTimestamp();
            BootDiag.RecordSpan(
                "regentfx-genvfxnode", "span", inv, inv.StartTick, t1,
                __exception == null && __result != null, __exception,
                __result != null ? "instantiated (cache hit or lazy fetch)" : "returned null node");
            if (__exception != null)
            {
                // Known limit (reviewer OPTIONAL-1): a failed first call latches the
                // milestone as failed and a later successful instantiate will not
                // overwrite it - the third summary safe point may then be skipped.
                // Fallback evidence: the per-call "regentfx-genvfxnode" span above
                // still records the later success.
                BootDiag.FailMilestone(
                    "first-effect-instantiate", __exception, "GenVFXNode threw; original exception propagates unchanged");
                return;
            }

            if (__result != null
                && BootDiag.LatchMilestone(
                    "first-effect-instantiate", inv, inv.StartTick, t1, true, null,
                    "first RegentFX effect node instantiated via non-generic GenVFXNode(string); generic GenVFXNode<T> callers are unbound - see hooks line"))
            {
                BootDiag.WriteSummary("first-effect-instantiate");
            }
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("regentfx-genvfxnode-finalizer-failed", e, null);
        }
    }

    // ---- Engine AssetCache scene fetch (first lazy effect scene load) ----
    // Producer: engine AssetCache.GetScene/GetAsset, reached only from RegentFX code paths when
    //   RegentFX's own ModSceneCache misses (the lazy first-use load; with a working preload the
    //   per-scene loads happen inside the regentfx-loadscenes span instead).
    // Owner: BootTimer. First consumer: first-effect-scene-load milestone + summary.
    // Hot-path note: this is a global engine hook. Cost after the latch (or when outside a
    //   RegentFX context) is one volatile bool read; before the latch, inside a RegentFX context,
    //   it is a short stack walk. No allocation on the early-out path. Not per-frame.
    // Failure awareness: finalizer; the latch fires only on a completed fetch with a non-null
    //   result. The finalizer return type matches the bound method (PackedScene vs Resource).
    // Cleanup point: process-lifetime patch; removed with the mod.
    internal static void SceneFetchPrefix(ref State __state)
    {
        try
        {
            if (BootDiag.FirstEffectLoadDone || !BootDiag.InsideRegentFxLoad())
            {
                return;
            }

            __state = new State { Inv = BootDiag.BeginInvocation("regentfx-scenefetch") };
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("regentfx-scenefetch-prefix-failed", e, null);
        }
    }

    internal static void SceneFetchFinalizerPacked(Exception __exception, PackedScene? __result, State? __state)
        => SceneFetchFinalizerCore(__exception, __result != null, __state);

    internal static void SceneFetchFinalizerResource(Exception __exception, Resource? __result, State? __state)
        => SceneFetchFinalizerCore(__exception, __result != null, __state);

    private static void SceneFetchFinalizerCore(Exception __exception, bool gotResult, State? __state)
    {
        try
        {
            var st = __state;
            if (st == null || st.Inv == null)
            {
                return; // outside RegentFX context or post-latch
            }

            BootDiag.EndInvocation(st.Inv);
            var inv = st.Inv;
            long t1 = Stopwatch.GetTimestamp();
            BootDiag.RecordSpan(
                "regentfx-scenefetch", "span", inv, inv.StartTick, t1, __exception == null && gotResult, __exception,
                "lazy engine-cache fetch outside RegentFX ModSceneCache (synchronous load on miss; may return an already-cached resource)");
            if (__exception != null)
            {
                BootDiag.FailMilestone(
                    "first-effect-scene-load", __exception, "AssetCache fetch threw; original exception propagates unchanged");
                return;
            }

            if (gotResult)
            {
                BootDiag.LatchMilestone(
                    "first-effect-scene-load", inv, inv.StartTick, t1, true, null,
                    "first lazy RegentFX scene fetch observed (engine cache consulted; sync load on miss)");
            }
        }
        catch (Exception e)
        {
            BootDiag.RecordFailure("regentfx-scenefetch-finalizer-failed", e, null);
        }
    }
}
