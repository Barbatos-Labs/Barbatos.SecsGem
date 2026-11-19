// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.PerformanceTracking;

/// <summary>
/// What a module is doing with its time - E116's EPT state.
/// </summary>
/// <remarks>
/// <para>
/// Three states, and the whole standard is the distinction between the second and the third. A
/// machine that is not working is either not working because nobody has given it anything to do,
/// or not working because something is stopping it - and those two have different owners. The
/// first is the factory's problem and the second is the machine's, so a number that adds them
/// together cannot be acted on by anybody.
/// </para>
/// <code>
/// Idle ---------- given something to do ---------------&gt; Executing
/// Idle ---------- something is in the way -------------&gt; Blocked
///
/// Executing ----- finished, or abandoned --------------&gt; Idle
/// Executing ----- something got in the way -----------&gt; Blocked   (the task is kept)
///
/// Blocked ------- the way is clear --------------------&gt; Executing (a task was kept)
///     |      ---- the way is clear, nothing to do -----&gt; Idle      (no task was kept)
///     |      ---- the task is over anyway -------------&gt; Idle
/// </code>
/// <para>
/// <strong><see cref="Blocked"/> keeps the task.</strong> A module stopped half way through a job
/// is still that job's module, so the time it stands there belongs to that job - which is the
/// number a fab is actually after, because it is the difference between a recipe that is slow and
/// a recipe that keeps running out of something. See
/// <see cref="PerformanceTaskEndedEventArgs.Blocked"/>, which is where it comes out.
/// </para>
/// <para>
/// <strong>On provenance, and it is as thin here as it was for E90.</strong> The catalogue that
/// checks every message in this stack has no mention of equipment performance tracking at all -
/// not a message, not an item - because E116 defines a state model and no messages. The three
/// state names and the idea of a task and a blocked reason are E116's; their numbers here, the
/// spelling of every attribute in <see cref="PerformanceObjectProvider"/> and each rule about
/// which call is refused from which state are this library's reading of it, checked by tests
/// rather than against reference data. <see cref="PerformanceTracker"/> is the one file to change
/// when a fab disagrees.
/// </para>
/// </remarks>
public enum PerformanceState
{
    /// <summary>Nothing to do, and nothing in the way.</summary>
    /// <remarks>Zero because it is where a module starts: a module that has just been declared has been given nothing.</remarks>
    Idle = 0,

    /// <summary>Doing something, and that something has a name.</summary>
    Executing = 1,

    /// <summary>Something is in the way.</summary>
    /// <remarks>With or without a task; the two are different facts and <see cref="PerformanceModule.CurrentTask"/> tells them apart.</remarks>
    Blocked = 2,
}

/// <summary>
/// The kinds of work a module can be executing, as E116 divides them.
/// </summary>
/// <remarks>
/// <para>
/// Strings rather than an enum, and for the same reason the carrier actions in the E87 package are
/// strings: the vocabulary belongs to the standard and to the fab, and a closed enum here would
/// cap what a machine can say about itself. These four are the spellings this library uses in its
/// own sample and tests; a <see cref="PerformanceTask.TaskType"/> that is none of them is not an
/// error anywhere in this package.
/// </para>
/// <para>
/// They matter because they are what splits <see cref="PerformanceState.Executing"/> into time a
/// wafer benefited from and time it did not. A module running a recipe and a module running its
/// own calibration are both executing, and only one of them is making product.
/// </para>
/// </remarks>
public static class PerformanceTaskTypes
{
    /// <summary>Work on product: a recipe, a measurement a lot asked for.</summary>
    public const string Process = "Process";

    /// <summary>Work the machine needs doing so that it can process: a pump-down, a purge, a transfer.</summary>
    public const string Support = "Support";

    /// <summary>Work on the machine itself: a calibration, a qualification, a clean.</summary>
    public const string Maintenance = "Maintenance";

    /// <summary>Work somebody is doing to find something out, on purpose.</summary>
    public const string Engineering = "Engineering";
}
