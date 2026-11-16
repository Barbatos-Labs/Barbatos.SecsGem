// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.MaterialFlow;

/// <summary>
/// How a process job's ending reaches the substrates it was running: one convention, written down
/// where it can be replaced.
/// </summary>
/// <remarks>
/// <para>
/// E40 ends a job as completed, stopped, aborted or cancelled. E90 ends work on a wafer as
/// processed, aborted, stopped or rejected, or lets it be skipped. <strong>Two of those words are
/// the same word</strong> - and nothing in either standard joins them, so a job somebody stopped
/// leaves its wafers <see cref="Barbatos.SecsGem.Substrates.SubstrateProcessingState.InProcess"/>
/// for ever: a machine with no idea what happened to the lot, reported by a host that was told the
/// job is over.
/// </para>
/// <para>
/// That is what this bridge is for, and the two decisions in it are both options: whether work in
/// progress is ended with the job, and whether wafers nobody got to are skipped.
/// </para>
/// </remarks>
public sealed class ProcessJobSubstrateOptions
{
    /// <summary>Whether a job's ending ends the work on wafers that were being processed.</summary>
    /// <remarks>
    /// Default <see langword="true"/>. A stopped job ends its wafers
    /// <see cref="Barbatos.SecsGem.Substrates.SubstrateProcessingState.Stopped"/> and an aborted
    /// one <see cref="Barbatos.SecsGem.Substrates.SubstrateProcessingState.Aborted"/>, which is
    /// the one mapping here that needs no choosing: the standards use the same two words for the
    /// same two things.
    /// </remarks>
    public bool EndsWorkInProgress { get; set; } = true;

    /// <summary>Whether wafers nobody got to are skipped when a job ends early.</summary>
    /// <remarks>
    /// Default <see langword="true"/>, and only for a job that ended early - stopped, aborted or
    /// cancelled. E90 is explicit that skipping is for a substrate that still needs processing and
    /// that one being worked on is stopped instead, which is exactly the distinction a half-run lot
    /// needs: these wafers were never touched, those were.
    /// </remarks>
    public bool SkipsUntouched { get; set; } = true;

    /// <summary>Whether a job that ends settles its substrates by itself.</summary>
    /// <remarks>Default <see langword="true"/>: the whole value of the join is that nobody has to remember.</remarks>
    public bool SettlesWhenTheJobEnds { get; set; } = true;

    /// <summary>Whether a job being set up has its material checked against the tracker.</summary>
    /// <remarks>
    /// Default <see langword="true"/>. A process job's material is a list of identifiers, by
    /// design - E40 does not know what a substrate is - so a job can name wafers nothing is
    /// tracking, and the machine would process material it cannot account for. The check cannot
    /// <em>refuse</em> the transition, because it hears about it afterwards; it reports, and an
    /// application that wants a refusal calls <c>Check</c> before it begins setting up.
    /// </remarks>
    public bool ChecksMaterialWhenSettingUp { get; set; } = true;
}

/// <summary>What a process job's substrates needed, and what was odd about them.</summary>
/// <remarks>
/// Three lists rather than one boolean, because a half-run lot is three different things at once:
/// wafers whose work this bridge ended, wafers it skipped, and wafers that do not fit the story the
/// job is telling.
/// </remarks>
public sealed class ProcessJobSubstrateResult
{
    /// <summary>Whether anything was attempted.</summary>
    /// <remarks>
    /// <see langword="false"/> only when nothing could be: no such job, or a job that has not
    /// ended. A job whose wafers were all odd is still accepted, and <see cref="Unexpected"/> says
    /// what was odd about each.
    /// </remarks>
    public required bool IsAccepted { get; init; }

    /// <summary>Why nothing was attempted. Empty when something was.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Which job.</summary>
    public required string ProcessJobId { get; init; }

    /// <summary>The substrates whose work this bridge ended.</summary>
    public IReadOnlyList<string> Ended { get; init; } = [];

    /// <summary>The substrates this bridge skipped.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>The substrates that did not fit, and what did not fit about them.</summary>
    /// <remarks>
    /// Material the tracker has never heard of, a wafer still being worked on after a job the
    /// machine called complete, a wafer already processed under a job that was cancelled before it
    /// ran - and any refusal the tracker gave. <strong>None of it is corrected here.</strong> A
    /// completed job with an unprocessed wafer in it is a machine telling two different stories,
    /// and a bridge that quietly picked one would destroy the evidence of the other.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Unexpected { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets how many substrates this bridge moved.</summary>
    public int Count => Ended.Count + Skipped.Count;

    internal static ProcessJobSubstrateResult Denied(string processJobId, string reason)
        => new() { IsAccepted = false, ProcessJobId = processJobId, Reason = reason };
}

/// <summary>A process job's substrates were settled, or its material was checked.</summary>
public sealed class ProcessJobSubstrateEventArgs : EventArgs
{
    /// <summary>What happened.</summary>
    public required ProcessJobSubstrateResult Result { get; init; }
}
