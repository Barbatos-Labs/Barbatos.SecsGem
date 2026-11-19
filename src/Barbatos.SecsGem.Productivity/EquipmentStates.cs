// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Productivity;

/// <summary>
/// The six basic equipment states - E10.
/// </summary>
/// <remarks>
/// <para>
/// Not a sequence. Every other state machine in this stack says which transition may follow which;
/// this one says nothing of the kind, because E10 is a <em>classification of time</em> rather than
/// a life cycle. A tool can go from producing to broken to not-scheduled in any order a day
/// happens to take, and a library that refused one of those orderings would be refusing a real
/// Tuesday.
/// </para>
/// <para>
/// What it does say is how the six add up, and that is the whole structure:
/// </para>
/// <code>
/// Total Time
/// |-- Non-Scheduled Time                  nobody asked the tool to be available
/// `-- Operations Time
///     |-- Equipment Downtime
///     |   |-- Unscheduled Downtime        it broke
///     |   `-- Scheduled Downtime          planned: maintenance, setup, a consumable
///     `-- Equipment Uptime
///         |-- Engineering Time            able to produce, being used to find something out
///         |-- Standby Time                able to produce, nothing to produce with
///         `-- Productive Time             producing
/// </code>
/// <para>
/// The distinction that pays for the whole standard is between <see cref="Standby"/> and the two
/// downtimes, and it is the same one E116 draws one level down: a tool with nothing to do is the
/// factory's problem, and a tool that cannot work is the tool's. <see cref="NonScheduled"/> is a
/// third thing again - not a complaint about anybody, just a shift nobody staffed - and it is the
/// reason E10 has both a total and an operations denominator for every ratio.
/// </para>
/// <para>
/// <strong>On provenance, and it is the thinnest in this repository.</strong> The catalogue that
/// checks every message in this stack has no mention of E10 or E79 at all - not a message, not an
/// item, not a code value - and that is not a gap in the reference data. These two standards
/// define <em>measurements</em>: six state names, how they add up, and the arithmetic over them.
/// There is nothing for SECS-II to carry, which is why this package has no wire presence and no
/// object type; see the README for where the numbers belong instead.
/// </para>
/// </remarks>
public enum EquipmentState
{
    /// <summary>Nobody asked the tool to be available: an unworked shift, a holiday, a plant shutdown.</summary>
    /// <remarks>
    /// Zero because it is where a tool starts: a tracker that has just been created has been told
    /// nothing, and claiming the tool was on standby would put time in a denominator nobody
    /// scheduled.
    /// </remarks>
    NonScheduled = 0,

    /// <summary>It broke.</summary>
    /// <remarks>Entering this state is what this package counts as a failure, and leaving it as a repair.</remarks>
    UnscheduledDowntime = 1,

    /// <summary>Not available, and planned that way: maintenance, setup, a consumable change, a facility.</summary>
    ScheduledDowntime = 2,

    /// <summary>Able to produce, and being used to find something out instead.</summary>
    Engineering = 3,

    /// <summary>Able to produce, with nothing to produce - no material, no operator, no carrier.</summary>
    Standby = 4,

    /// <summary>Producing.</summary>
    Productive = 5,
}
