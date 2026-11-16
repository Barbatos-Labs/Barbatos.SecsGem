// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ProcessJobs;

/// <summary>What a job's status says - S16F6 and S16F8.</summary>
/// <remarks>
/// <strong>Two properties for one field, on purpose.</strong> <c>JOBSTATE</c> has no dictionary
/// entry; what this library reads it as comes from <c>PRSTATE</c>, which defines ten codes. A
/// machine that sends an eleventh is not lying, it is using a state this library has no name for -
/// so <see cref="State"/> is <see langword="null"/> and <see cref="StateCode"/> is what arrived.
/// Reading an unnamed code as <see cref="ProcessJobState.Queued"/>, the zero value, would tell a
/// host the job has not started.
/// </remarks>
public sealed class ProcessJobStatus
{
    /// <summary>Which job - <c>PRJOBID</c>.</summary>
    public required string ProcessJobId { get; init; }

    /// <summary>The recipe it runs - <c>PPID</c>.</summary>
    public string RecipeId { get; init; } = string.Empty;

    /// <summary>What state it is in, or <see langword="null"/> for a code this library cannot name.</summary>
    public ProcessJobState? State { get; init; }

    /// <summary>The state code exactly as it arrived - <c>JOBSTATE</c>.</summary>
    public byte StateCode { get; init; }
}

/// <summary>Where a job is in the queue - S16F20.</summary>
public sealed class ProcessJobQueuePosition
{
    /// <summary>Which job.</summary>
    public required string ProcessJobId { get; init; }

    /// <summary>Its place, counting from one - <c>QPOSN</c>. Zero for a job that is not waiting.</summary>
    public ushort Position { get; init; }

    /// <summary>What state it is in, or <see langword="null"/> for a code this library cannot name.</summary>
    public ProcessJobState? State { get; init; }

    /// <summary>The state code exactly as it arrived.</summary>
    public byte StateCode { get; init; }
}

/// <summary>A job changed state, as a host hears it - S16F11.</summary>
/// <remarks>
/// E40's own way of telling a host, and the one thing it has that E90 and E94 do not: a state
/// change is a transaction of its own rather than a collection event somebody had to define and
/// number.
/// </remarks>
public sealed class ProcessJobStateReport
{
    /// <summary>Which job.</summary>
    public required string ProcessJobId { get; init; }

    /// <summary>What state it is in now, or <see langword="null"/> for a code this library cannot name.</summary>
    public ProcessJobState? State { get; init; }

    /// <summary>The state code exactly as it arrived.</summary>
    public byte StateCode { get; init; }

    /// <summary>When, by the equipment's clock - <c>TIME</c>.</summary>
    /// <remarks><see langword="null"/> when the timestamp was absent or in a form this library cannot read; the report is still delivered, because when it happened matters less than that it did.</remarks>
    public DateTime? At { get; init; }
}

/// <summary>One job in a queue report - S16F29.</summary>
public sealed class ProcessJobQueueEntry
{
    /// <summary>Which job.</summary>
    public required string ProcessJobId { get; init; }

    /// <summary>What state it is in, or <see langword="null"/> for a code this library cannot name.</summary>
    public ProcessJobState? State { get; init; }

    /// <summary>The state code exactly as it arrived.</summary>
    public byte StateCode { get; init; }
}

/// <summary>A recipe parameter - <c>CPNAME</c> and <c>CPVAL</c>.</summary>
/// <remarks>Both are <c>A</c> items in the catalogue, so a parameter is a pair of strings. A value sent in a numeric format is read as its digits.</remarks>
public sealed class ProcessJobParameter
{
    /// <summary>What the parameter is called.</summary>
    public required string Name { get; init; }

    /// <summary>Its value.</summary>
    public string Value { get; init; } = string.Empty;
}

/// <summary>What happened to one parameter of a change - S16F18.</summary>
public sealed class ProcessJobParameterResult
{
    /// <summary>Which parameter.</summary>
    public required string Name { get; init; }

    /// <summary>Whether it was changed - <c>CPACK</c>.</summary>
    public required ProcessJobParameterAcknowledgement Acknowledgement { get; init; }
}

/// <summary>What happened to one job of a multi-command - S16F24.</summary>
public sealed class ProcessJobCommandResult
{
    /// <summary>Which job.</summary>
    public required string ProcessJobId { get; init; }

    /// <summary>Whether it was done - <c>CMDA</c>.</summary>
    public required ProcessJobCommandAcknowledgement Acknowledgement { get; init; }
}

/// <summary>A job changed state, as the equipment's host-side listener hears it - S16F11.</summary>
public sealed class ProcessJobStateReportEventArgs : EventArgs
{
    /// <summary>What happened.</summary>
    /// <remarks>Valid for the life of the handler.</remarks>
    public required ProcessJobStateReport Report { get; init; }

    /// <summary>Gets or sets what goes back as <c>PJACK</c>.</summary>
    public ProcessJobAcknowledgement Acknowledgement { get; set; } = ProcessJobAcknowledgement.Accepted;
}

/// <summary>A queue status report that arrived - S16F29.</summary>
public sealed class ProcessJobQueueReportEventArgs : EventArgs
{
    /// <summary>The queue, as the machine sees it.</summary>
    /// <remarks>Valid for the life of the handler.</remarks>
    public required IReadOnlyList<ProcessJobQueueEntry> Jobs { get; init; }

    /// <summary>Gets or sets what goes back as <c>PJACK</c>.</summary>
    public ProcessJobAcknowledgement Acknowledgement { get; set; } = ProcessJobAcknowledgement.Accepted;
}
