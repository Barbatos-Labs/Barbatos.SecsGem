// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem;

/// <summary>TIAACK - what happened to a trace the host asked for.</summary>
/// <remarks>
/// <para>
/// Zero means accepted, as everywhere else here. The five refusals are the five ways an S2F23 can
/// be wrong, and naming them is the difference between a host fixing its request and a host
/// retrying it unchanged.
/// </para>
/// <para>
/// <strong>One caveat worth knowing.</strong> SEMI E5's own table for TIAACK is these six codes,
/// but <c>Barbatos.SecsGem.Catalog</c> flags S2F24 as a representative entry and lists only 0 and
/// 1 for it. The two sources agree on the only thing a host must get right - zero is accepted and
/// non-zero is not - so a host that tests for zero is safe against either reading, and one that
/// switches on the refusal is reading the standard rather than this repository's reference table.
/// </para>
/// </remarks>
public enum GemTraceAcknowledgement : byte
{
    /// <summary>Accepted; the trace is running.</summary>
    Accepted = 0,

    /// <summary>More variables than this equipment will sample in one trace.</summary>
    TooManyVariables = 1,

    /// <summary>As many traces are already running as this equipment allows.</summary>
    NoMoreTracesAllowed = 2,

    /// <summary>DSPER was not a period this equipment can sample on.</summary>
    InvalidPeriod = 3,

    /// <summary>At least one of the variables is not one this equipment has.</summary>
    UnknownVariable = 4,

    /// <summary>REPGSZ is not a group size that divides the samples asked for.</summary>
    InvalidGroupSize = 5,
}

/// <summary>
/// A trace: sample these variables on this interval, and report them in groups of that many.
/// </summary>
/// <remarks>
/// <para>
/// E30's trace data collection, which is the answer to a host that wants a value every second
/// without asking for it every second. The equipment samples on its own clock and sends a group
/// when it has one, so a trace of 600 samples reported in groups of 10 is sixty messages rather
/// than six hundred.
/// </para>
/// <para>
/// <strong>A request for zero samples is a request to stop.</strong> E5 defines S2F23 as
/// <em>initialising</em> a trace, so re-initialising a running TRID to take no samples and
/// cancelling it are the same request - and <see cref="GemHost.StopTraceAsync"/> sends exactly
/// that. The standard's wording leaves room for reading zero as "run for ever" instead; this is
/// the safer of the two, because a host that meant "stop" and was understood as "for ever" floods,
/// while one that meant "for ever" and was understood as "stop" simply gets no data and asks
/// again.
/// </para>
/// </remarks>
public sealed class GemTraceRequest
{
    /// <summary>Initialises a request.</summary>
    /// <param name="id">TRID.</param>
    /// <param name="period">DSPER, the interval between samples.</param>
    /// <param name="totalSamples">TOTSMP. Zero cancels the trace with this identifier.</param>
    /// <param name="groupSize">REPGSZ, how many samples travel in one S6F1.</param>
    /// <param name="variableIds">The SVIDs to sample, in the order they should be reported. Copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="variableIds"/> is <see langword="null"/>.</exception>
    public GemTraceRequest(
        uint id,
        TimeSpan period,
        int totalSamples,
        int groupSize,
        IReadOnlyList<uint> variableIds)
    {
        ArgumentNullException.ThrowIfNull(variableIds);

        Id = id;
        Period = period;
        TotalSamples = totalSamples;
        GroupSize = groupSize;
        VariableIds = [.. variableIds];
    }

    /// <summary>Gets the trace identifier.</summary>
    public uint Id { get; }

    /// <summary>Gets the interval between samples.</summary>
    /// <remarks>
    /// DSPER travels as <c>hhmmss</c> or <c>hhmmsscc</c>, so anything finer than a hundredth of a
    /// second cannot be expressed and anything longer than a day cannot either.
    /// </remarks>
    public TimeSpan Period { get; }

    /// <summary>Gets how many samples to take in total. Zero cancels the trace.</summary>
    public int TotalSamples { get; }

    /// <summary>Gets how many samples travel in one report.</summary>
    public int GroupSize { get; }

    /// <summary>Gets the variables to sample, in the order they are reported.</summary>
    public IReadOnlyList<uint> VariableIds { get; }

    /// <summary>Returns the identifier and what it asked for.</summary>
    /// <returns>For example <c>trace 1: 60 samples of 2 variable(s) every 00:00:01 in groups of 10</c>.</returns>
    public override string ToString() => TotalSamples == 0
        ? $"trace {Id}: cancel"
        : $"trace {Id}: {TotalSamples} samples of {VariableIds.Count} variable(s) every {Period} in groups of {GroupSize}";
}
