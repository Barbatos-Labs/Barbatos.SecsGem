// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Productivity;

/// <summary>
/// One tool's time in E10's six states, and the arithmetic E10 and E79 do over it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One tracker is one tool.</strong> E10 measures a piece of equipment, so an application
/// with four tools makes four of these - which is the opposite of
/// <c>Barbatos.SecsGem.PerformanceTracking</c>, whose tracker holds many modules because E116 is
/// explicitly per module. The two standards sit one level apart: E116 says what a chamber was
/// doing this minute, E10 says what the tool was for this shift.
/// </para>
/// <para>
/// <strong>One mover, because E10 is a classification and not a sequence.</strong> Every other
/// behaviour package here has a verb per transition - <c>Open</c>, <c>BeginTask</c>,
/// <c>Unblock</c> - because those standards say which transition may follow which. E10 says
/// nothing of the kind: a tool can go from producing to broken to not-scheduled in whatever order
/// a day takes. So there is <see cref="MoveTo"/> and no per-state method, because there is no
/// per-state precondition to hang one on.
/// </para>
/// <para>
/// <strong>The counts are the transitions.</strong> A failure is an entry into
/// <see cref="EquipmentState.UnscheduledDowntime"/> and a repair is an exit from it into a state
/// the tool can work in; a scheduled interrupt is an entry into
/// <see cref="EquipmentState.ScheduledDowntime"/>. Nothing has to be counted by hand, so the
/// counts cannot drift away from the time buckets - and <c>MTBF</c> cannot disagree with the
/// availability computed from the same window.
/// </para>
/// <para>
/// <strong>Durations come from the monotonic timestamp, timestamps from the wall clock.</strong>
/// Both are the same <see cref="TimeProvider"/>, as in the E116 package and for the same reason: a
/// shift report is measured, not read off a clock somebody may correct half way through it.
/// </para>
/// <para>
/// Thread safety: one lock around every change. The events are raised inside it, after the state
/// has moved, so a handler must queue rather than call back in.
/// </para>
/// </remarks>
public sealed class ProductivityTracker
{
    private const int StateCount = 6;

    private readonly TimeSpan[] _buckets = new TimeSpan[StateCount];
    private readonly TimeProvider _clock;
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    private EquipmentState _state = EquipmentState.NonScheduled;
    private string _reason = string.Empty;
    private DateTimeOffset _stateSince;
    private DateTimeOffset _since;
    private long _stateStamp;
    private TimeSpan _completedRepairTime;
    private int _failures;
    private int _scheduledInterrupts;
    private int _repairs;
    private long _cycles;
    private long _actualUnits;
    private long _effectiveUnits;
    private TimeSpan _theoreticalActualTime;
    private TimeSpan _theoreticalEffectiveTime;

    /// <summary>Starts accounting for one tool, in <see cref="EquipmentState.NonScheduled"/>.</summary>
    /// <param name="clock">The clock, which is every duration this package reports. <see langword="null"/> means <see cref="TimeProvider.System"/>.</param>
    /// <remarks>
    /// Non-scheduled is the honest starting state: a tracker that has just been created has been
    /// told nothing, and starting it on standby would put time the factory never scheduled into
    /// the denominator of every operations ratio.
    /// </remarks>
    public ProductivityTracker(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _since = _clock.GetUtcNow();
        _stateSince = _since;
        _stateStamp = _clock.GetTimestamp();
    }

    /// <summary>Raised after the tool moves between states.</summary>
    /// <remarks>Raised inside the tracker's lock. Queue the work; do not call back in.</remarks>
    public event EventHandler<EquipmentStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when the tool stays in its state for a different stated reason.</summary>
    /// <remarks>
    /// Never accompanied by <see cref="StateChanged"/>: the tool did not move, and nothing was
    /// counted. "Pump failed" becoming "awaiting parts" is one failure with two phases - which is
    /// a distinction E10 cares about, since the second is not the tool's fault and the first is.
    /// </remarks>
    public event EventHandler<ProductivityReasonChangedEventArgs>? ReasonChanged;

    /// <summary>Gets what the tool is doing now.</summary>
    public EquipmentState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Gets why, when the state has a stated reason.</summary>
    public string StateReason
    {
        get
        {
            lock (_gate)
            {
                return _reason;
            }
        }
    }

    /// <summary>Gets when the tool entered the state it is in.</summary>
    /// <remarks>
    /// Wall clock, because this is the one figure a person reads off a screen. A reset does not
    /// move it: the counters start again, and the tool did not change state.
    /// </remarks>
    public DateTimeOffset StateSince
    {
        get
        {
            lock (_gate)
            {
                return _stateSince;
            }
        }
    }

    /// <summary>Puts the tool in one of E10's six states.</summary>
    /// <param name="state">Which state.</param>
    /// <param name="reason">
    /// Why. Required for the two downtime states, because a downtime nobody explained is a
    /// downtime nobody can act on; optional for the rest, and forced empty for
    /// <see cref="EquipmentState.Productive"/>, which needs no excuse.
    /// </param>
    /// <returns>Whether it was allowed, and why not.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is not one of the six.</exception>
    /// <remarks>
    /// <para>
    /// Two refusals, and both are a caller saying nothing: a downtime with no reason, and the state
    /// the tool is already in for the reason it is already in it. The second matters more than it
    /// looks - forgiving it would restart the clock on the current stretch every time a polling
    /// loop reported the same state again, and every duration in the report would come out short.
    /// </para>
    /// <para>
    /// The same state for a <em>different</em> reason is accepted and reported as
    /// <see cref="ReasonChanged"/> with no transition, so the stretch is not broken in two and
    /// nothing is counted twice.
    /// </para>
    /// </remarks>
    public ProductivityResult MoveTo(EquipmentState state, string reason = "")
    {
        ArgumentNullException.ThrowIfNull(reason);

        if (state is < EquipmentState.NonScheduled or > EquipmentState.Productive)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "That is not one of E10's six states.");
        }

        // Productive needs no excuse, and a reason stored against it would show up in a report as
        // though producing were something to explain.
        var stated = state is EquipmentState.Productive ? string.Empty : reason;

        lock (_gate)
        {
            if (state is EquipmentState.UnscheduledDowntime or EquipmentState.ScheduledDowntime
                && stated.Length == 0)
            {
                return ProductivityResult.Refused($"{state} needs a reason.");
            }

            if (_state == state)
            {
                if (string.Equals(_reason, stated, StringComparison.Ordinal))
                {
                    return ProductivityResult.Refused(
                        stated.Length == 0
                            ? $"the tool is already {state}."
                            : $"the tool is already {state} ({stated}).");
                }

                var was = _reason;
                _reason = stated;

                ReasonChanged?.Invoke(this, new ProductivityReasonChangedEventArgs
                {
                    State = state,
                    Previous = was,
                    Current = stated,
                });

                return ProductivityResult.Ok;
            }

            var now = _clock.GetUtcNow();
            var stamp = _clock.GetTimestamp();
            var elapsed = _clock.GetElapsedTime(_stateStamp, stamp);
            var previous = _state;

            _buckets[(int)previous] += elapsed;

            // A repair is an exit from unscheduled downtime into a state the tool can work in -
            // which is the evidence that it was fixed. A shift that ends while the tool is still
            // broken is not a completed repair, so its stretch stays in the downtime bucket and
            // out of the mean.
            if (previous is EquipmentState.UnscheduledDowntime && IsUptime(state))
            {
                _completedRepairTime += elapsed;
                _repairs++;
            }

            if (state is EquipmentState.UnscheduledDowntime)
            {
                _failures++;
            }
            else if (state is EquipmentState.ScheduledDowntime)
            {
                _scheduledInterrupts++;
            }

            _state = state;
            _reason = stated;
            _stateSince = now;
            _stateStamp = stamp;

            StateChanged?.Invoke(this, new EquipmentStateChangedEventArgs
            {
                Previous = previous,
                Current = state,
                Reason = stated,
                Elapsed = elapsed,
            });

            return ProductivityResult.Ok;
        }
    }

    /// <summary>Counts whatever the fab counts as cycles.</summary>
    /// <param name="cycles">How many.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cycles"/> is negative.</exception>
    /// <remarks>
    /// E10 leaves the unit to the equipment - a wafer, a lot, a robot move - so this package takes
    /// a number rather than deciding. It feeds <see cref="ProductivityReport.MeanCyclesBetweenFailures"/>
    /// and nothing else.
    /// </remarks>
    public void RecordCycles(long cycles = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cycles);

        lock (_gate)
        {
            _cycles += cycles;
        }
    }

    /// <summary>Counts units out of the tool, and what they should have taken.</summary>
    /// <param name="actual">How many came out.</param>
    /// <param name="effective">How many of those were good.</param>
    /// <param name="theoreticalEach">What one of them should have taken at the tool's theoretical rate.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative, more units were good than came out, or <paramref name="theoreticalEach"/> is negative.</exception>
    /// <remarks>
    /// <para>
    /// The theoretical time comes in <em>per report</em> rather than being a property of the
    /// tracker, and that is the whole reason this method has three arguments. E79's theoretical
    /// production time is a sum over units, and a tool that runs two recipes has two rates - so a
    /// single rate on the tracker would be wrong for every lot but one.
    /// </para>
    /// <para>
    /// Units are counted whenever they are reported, with no check on what state the tool is in.
    /// E79 divides them by <em>productive</em> time, so report the units the tool produced
    /// productively: counting an engineering wafer here makes
    /// <see cref="ProductivityReport.RateEfficiency"/> come out above one, which is the number
    /// telling you the fab meant not to.
    /// </para>
    /// </remarks>
    public void RecordUnits(long actual, long effective, TimeSpan theoreticalEach)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actual);
        ArgumentOutOfRangeException.ThrowIfNegative(effective);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(effective, actual);
        ArgumentOutOfRangeException.ThrowIfLessThan(theoreticalEach, TimeSpan.Zero);

        lock (_gate)
        {
            _actualUnits += actual;
            _effectiveUnits += effective;
            _theoreticalActualTime += actual * theoreticalEach;
            _theoreticalEffectiveTime += effective * theoreticalEach;
        }
    }

    /// <summary>Writes down everything this tracker holds.</summary>
    /// <returns>A faithful record of the window, the six buckets and every count.</returns>
    /// <remarks>
    /// Taken under the tracker's lock, and the open stretch is folded into its bucket first, so a
    /// snapshot is a complete window rather than one with the current state's time still running.
    /// </remarks>
    public ProductivityTrackerSnapshot Capture()
    {
        lock (_gate)
        {
            var elapsed = _clock.GetElapsedTime(_stateStamp, _clock.GetTimestamp());

            return new ProductivityTrackerSnapshot
            {
                CapturedAt = _clock.GetUtcNow(),
                Since = _since,
                State = _state,
                StateReason = _reason,
                StateSince = _stateSince,
                NonScheduled = Bucket(EquipmentState.NonScheduled, elapsed),
                UnscheduledDowntime = Bucket(EquipmentState.UnscheduledDowntime, elapsed),
                ScheduledDowntime = Bucket(EquipmentState.ScheduledDowntime, elapsed),
                Engineering = Bucket(EquipmentState.Engineering, elapsed),
                Standby = Bucket(EquipmentState.Standby, elapsed),
                Productive = Bucket(EquipmentState.Productive, elapsed),
                Failures = _failures,
                ScheduledInterrupts = _scheduledInterrupts,
                Repairs = _repairs,
                CompletedRepairTime = _completedRepairTime,
                Cycles = _cycles,
                ActualUnits = _actualUnits,
                EffectiveUnits = _effectiveUnits,
                TheoreticalActualTime = _theoreticalActualTime,
                TheoreticalEffectiveTime = _theoreticalEffectiveTime,
            };
        }
    }

    /// <summary>Reads a snapshot back into a tracker that has accounted for nothing yet.</summary>
    /// <param name="snapshot">What <see cref="Capture"/> wrote.</param>
    /// <param name="whileOff">Which bucket the time the tool was off belongs in.</param>
    /// <returns>What came back, and where that time went.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <strong>Nearly all of this is a ledger, so nearly all of it comes back.</strong> E10 is an
    /// accounting standard: the six buckets, the failure and repair counts, the units and their
    /// theoretical times are a record of a window, and a tracker that lost them to a reboot would
    /// report a day that did not happen. <see cref="ProductivityReport.Since"/> comes back too,
    /// because the window is the thing being reported on.
    /// </para>
    /// <para>
    /// <strong>Which leaves the time the tool was off, and here this package does the opposite of
    /// <c>Barbatos.SecsGem.PerformanceTracking</c>.</strong> E116 hands that interval back
    /// uncharged, because its three states have nowhere honest to put it. E10 has six states and
    /// two of them are exactly this: a tool nobody scheduled, and a tool that was scheduled and
    /// could not work. And it <em>must</em> be charged, because every E10 ratio divides by some sum
    /// of the buckets - so an hour in none of them is an hour that silently improves whichever
    /// ratio it would have reduced. The six buckets add up to the window before a restore and they
    /// add up to it afterwards.
    /// </para>
    /// <para>
    /// Which bucket is the caller's to say, since only the fab knows whether the machine was broken
    /// or simply not wanted. The default is
    /// <see cref="EquipmentState.UnscheduledDowntime"/> - the reading that cannot flatter, the same
    /// choice every default in this stack's joins makes. A fab that powers its tools down between
    /// shifts passes <see cref="EquipmentState.NonScheduled"/> and gets the other honest answer.
    /// </para>
    /// <para>
    /// Afterwards the tool is in <paramref name="whileOff"/>, entered now, with a stated reason
    /// saying the machine restarted - so a <see cref="Report"/> taken before the application has
    /// told the tracker anything still describes something true. The application then calls
    /// <see cref="MoveTo"/> when it knows what the tool is actually doing, and if that is the same
    /// state, E10's own rule about a reason changing without a move applies and nothing is counted
    /// twice.
    /// </para>
    /// <para>
    /// Refused outright for a tracker that has already accounted for something, because adding one
    /// ledger to another is one wrong ledger. Nothing is raised while restoring.
    /// </para>
    /// </remarks>
    public ProductivityRestoreReport Restore(
        ProductivityTrackerSnapshot snapshot,
        EquipmentState whileOff = EquipmentState.UnscheduledDowntime)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            for (var index = 0; index < StateCount; index++)
            {
                if (_buckets[index] != TimeSpan.Zero)
                {
                    return ProductivityRestoreReport.Denied(
                        "this tracker has already accounted for time; a snapshot is read into one that has "
                        + "not, because two ledgers added together are one wrong ledger.");
                }
            }

            if (_failures != 0 || _repairs != 0 || _cycles != 0 || _actualUnits != 0)
            {
                return ProductivityRestoreReport.Denied(
                    "this tracker has already counted something; a snapshot is read into one that has not.");
            }

            var now = _clock.GetUtcNow();
            var stamp = _clock.GetTimestamp();
            var off = snapshot.CapturedAt == default || now <= snapshot.CapturedAt
                ? TimeSpan.Zero
                : now - snapshot.CapturedAt;

            _buckets[(int)EquipmentState.NonScheduled] = snapshot.NonScheduled;
            _buckets[(int)EquipmentState.UnscheduledDowntime] = snapshot.UnscheduledDowntime;
            _buckets[(int)EquipmentState.ScheduledDowntime] = snapshot.ScheduledDowntime;
            _buckets[(int)EquipmentState.Engineering] = snapshot.Engineering;
            _buckets[(int)EquipmentState.Standby] = snapshot.Standby;
            _buckets[(int)EquipmentState.Productive] = snapshot.Productive;

            _failures = snapshot.Failures;
            _scheduledInterrupts = snapshot.ScheduledInterrupts;
            _repairs = snapshot.Repairs;
            _completedRepairTime = snapshot.CompletedRepairTime;
            _cycles = snapshot.Cycles;
            _actualUnits = snapshot.ActualUnits;
            _effectiveUnits = snapshot.EffectiveUnits;
            _theoreticalActualTime = snapshot.TheoreticalActualTime;
            _theoreticalEffectiveTime = snapshot.TheoreticalEffectiveTime;

            _since = snapshot.Since == default ? now : snapshot.Since;

            if (off == TimeSpan.Zero)
            {
                // No gap, so nothing happened to account for: this is a hand-over rather than an
                // outage, and the tool comes back exactly as it was. Charging a state and counting
                // an interruption here would invent a failure out of a process restarting
                // instantly, and the counts would stop agreeing with the buckets.
                _state = snapshot.State;
                _reason = snapshot.StateReason;
                _stateSince = snapshot.StateSince == default ? now : snapshot.StateSince;
                _stateStamp = stamp;
            }
            else
            {
                // The stretch the machine was off is left *open* in the state it is charged to,
                // by winding this state's stamp back to the capture, rather than added to the
                // bucket here. Every other method in this class folds the open stretch in, so the
                // window and the buckets agree from the first moment - and a repair that finishes
                // after the restart has the whole outage inside it, which it would not if the
                // clock on this state started now.
                _state = whileOff;
                _reason = whileOff is EquipmentState.Productive ? string.Empty : "the machine restarted";
                _stateSince = snapshot.CapturedAt;
                _stateStamp = stamp - Stamps(off);

                // Entering a downtime state is an interruption whoever caused it, so a restart is
                // counted the way the equivalent MoveTo would count it - unless the tool was
                // already in that state when it was captured, which is one stretch and not two.
                if (snapshot.State != whileOff)
                {
                    if (whileOff is EquipmentState.UnscheduledDowntime)
                    {
                        _failures++;
                    }
                    else if (whileOff is EquipmentState.ScheduledDowntime)
                    {
                        _scheduledInterrupts++;
                    }
                }
            }

            return new ProductivityRestoreReport
            {
                IsAccepted = true,
                Unaccounted = off,
                UnaccountedChargedTo = off == TimeSpan.Zero ? snapshot.State : whileOff,
            };
        }
    }

    /// <summary>Where the tool's time went, and what that says about it.</summary>
    /// <returns>A report covering the window so far.</returns>
    /// <remarks>
    /// The current state's elapsed time is counted in, so the six buckets always add up to the
    /// window - across a <see cref="Restore"/> as well, which is why the time a tool spent switched
    /// off has to be charged to one of them. Asking twice without the tool moving gives two
    /// different answers, and both are right.
    /// </remarks>
    public ProductivityReport Report()
    {
        lock (_gate)
        {
            var elapsed = _clock.GetElapsedTime(_stateStamp, _clock.GetTimestamp());

            return new ProductivityReport
            {
                Since = _since,
                State = _state,
                StateReason = _reason,
                TimeInState = elapsed,
                NonScheduled = Bucket(EquipmentState.NonScheduled, elapsed),
                UnscheduledDowntime = Bucket(EquipmentState.UnscheduledDowntime, elapsed),
                ScheduledDowntime = Bucket(EquipmentState.ScheduledDowntime, elapsed),
                Engineering = Bucket(EquipmentState.Engineering, elapsed),
                Standby = Bucket(EquipmentState.Standby, elapsed),
                Productive = Bucket(EquipmentState.Productive, elapsed),
                Failures = _failures,
                ScheduledInterrupts = _scheduledInterrupts,
                Repairs = _repairs,
                Cycles = _cycles,
                ActualUnits = _actualUnits,
                EffectiveUnits = _effectiveUnits,
                TheoreticalActualTime = _theoreticalActualTime,
                TheoreticalEffectiveTime = _theoreticalEffectiveTime,
                MeanTimeToRepair = _repairs == 0 ? TimeSpan.Zero : _completedRepairTime / _repairs,
            };
        }
    }

    /// <summary>Starts the accounting again from now.</summary>
    /// <returns>Always accepted; a result for symmetry with everything else that changes the tracker.</returns>
    /// <remarks>
    /// For a shift boundary, or a report that covers a period. It changes nothing about what the
    /// tool is doing: no state moves, no event is raised, and <see cref="StateSince"/> still says
    /// when the tool really entered the state it is in. A repair running across the reset keeps
    /// its stretch in the new window only from the reset onwards, which is the same compromise
    /// every period report makes.
    /// </remarks>
    public ProductivityResult Reset()
    {
        lock (_gate)
        {
            Array.Clear(_buckets);

            _completedRepairTime = TimeSpan.Zero;
            _failures = 0;
            _scheduledInterrupts = 0;
            _repairs = 0;
            _cycles = 0;
            _actualUnits = 0;
            _effectiveUnits = 0;
            _theoreticalActualTime = TimeSpan.Zero;
            _theoreticalEffectiveTime = TimeSpan.Zero;
            _since = _clock.GetUtcNow();
            _stateStamp = _clock.GetTimestamp();

            return ProductivityResult.Ok;
        }
    }

    private static bool IsUptime(EquipmentState state)
        => state is EquipmentState.Engineering or EquipmentState.Standby or EquipmentState.Productive;

    /// <summary>How many of this clock's timestamp units an interval is.</summary>
    /// <remarks>
    /// Only <see cref="Restore"/> needs this, because it is the one place that has to name a
    /// moment in the past in a unit the clock chose. Scaled through <see cref="double"/> rather
    /// than by multiplying ticks, which overflows a <see cref="long"/> for an outage of about a
    /// day on a clock that counts in ticks - and a day is a perfectly ordinary outage.
    /// </remarks>
    private long Stamps(TimeSpan interval)
        => (long)(interval.Ticks * ((double)_clock.TimestampFrequency / TimeSpan.TicksPerSecond));

    private TimeSpan Bucket(EquipmentState state, TimeSpan elapsed)
        => _buckets[(int)state] + (_state == state ? elapsed : TimeSpan.Zero);
}
