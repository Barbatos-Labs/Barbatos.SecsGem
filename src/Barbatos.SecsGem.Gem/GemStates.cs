// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// The GEM communication state, as SEMI E30 draws it.
/// </summary>
/// <remarks>
/// <para>
/// E30 nests these: DISABLED, or ENABLED with the substates NOT COMMUNICATING (itself split into
/// WAIT CRA and WAIT DELAY) and COMMUNICATING. This enum flattens the leaves, because a flat state
/// is the one a caller can switch on and a nested one is three questions.
/// </para>
/// <para>
/// A selected HSMS link is <em>not</em> the same thing as communicating. The link carries bytes;
/// communicating means both sides have agreed, through S1F13 and S1F14, that they are ready to
/// exchange application messages. Equipment that reports an event before that agreement is
/// equipment a host is entitled to ignore.
/// </para>
/// </remarks>
public enum GemCommunicationState
{
    /// <summary>Communication is switched off. No S1F13 is sent and an incoming one is denied.</summary>
    Disabled,

    /// <summary>Enabled, and an S1F13 has gone out with no S1F14 back yet.</summary>
    /// <remarks>E30 calls this WAIT CRA - waiting for the Communication Request Acknowledge.</remarks>
    WaitingForAcknowledge,

    /// <summary>Enabled, and waiting out the retry interval before trying S1F13 again.</summary>
    /// <remarks>
    /// E30 calls this WAIT DELAY. It exists so that two entities that come up together do not
    /// spend the day exchanging denied requests.
    /// </remarks>
    WaitingToRetry,

    /// <summary>Both sides have agreed. Application messages may flow.</summary>
    Communicating,
}

/// <summary>
/// The GEM control state, as SEMI E30 draws it.
/// </summary>
/// <remarks>
/// <para>
/// This is the model that decides whether the host may <em>command</em> the equipment or only watch
/// it. Off-line means the equipment answers almost nothing; on-line local means a person at the
/// machine is in charge; on-line remote means the host is.
/// </para>
/// <para>
/// The distinction that matters in practice is between <see cref="EquipmentOffline"/> and
/// <see cref="HostOffline"/>. Both refuse host commands, and they are not the same event: the first
/// is the machine's own decision, the second is the host having asked for it with S1F15. Equipment
/// that collapses the two cannot tell an operator why it will not take commands.
/// </para>
/// </remarks>
public enum GemControlState
{
    /// <summary>Off-line by the equipment's own decision. Only S1F1, S1F13 and S1F17 are answered.</summary>
    EquipmentOffline,

    /// <summary>Trying to go on-line, which needs communication established first.</summary>
    AttemptOnline,

    /// <summary>Off-line because the host asked for it with S1F15.</summary>
    HostOffline,

    /// <summary>On-line, with control at the machine. The host may read but not command.</summary>
    OnlineLocal,

    /// <summary>On-line, with control at the host. Remote commands are accepted.</summary>
    OnlineRemote,
}

/// <summary>Reports a communication state transition.</summary>
/// <remarks>
/// Raised on the thread that made the transition, so a slow handler delays whatever caused it.
/// Keep one short, or hand the work to something else.
/// </remarks>
public sealed class GemCommunicationStateChangedEventArgs : EventArgs
{
    /// <summary>Initialises the arguments.</summary>
    /// <param name="previous">The state left behind.</param>
    /// <param name="current">The state now in force.</param>
    /// <param name="reason">Why, in a few words, for a log line.</param>
    public GemCommunicationStateChangedEventArgs(
        GemCommunicationState previous,
        GemCommunicationState current,
        string? reason = null)
    {
        Previous = previous;
        Current = current;
        Reason = reason;
    }

    /// <summary>Gets the state left behind.</summary>
    public GemCommunicationState Previous { get; }

    /// <summary>Gets the state now in force.</summary>
    public GemCommunicationState Current { get; }

    /// <summary>Gets why the transition happened, when a reason was given.</summary>
    public string? Reason { get; }

    /// <summary>Returns a one-line description.</summary>
    /// <returns>For example <c>WaitingForAcknowledge -&gt; Communicating (S1F14 COMMACK=0)</c>.</returns>
    public override string ToString()
        => Reason is null ? $"{Previous} -> {Current}" : $"{Previous} -> {Current} ({Reason})";
}

/// <summary>Reports a control state transition.</summary>
/// <remarks>
/// Raised on the thread that made the transition, so a slow handler delays whatever caused it.
/// Keep one short, or hand the work to something else.
/// </remarks>
public sealed class GemControlStateChangedEventArgs : EventArgs
{
    /// <summary>Initialises the arguments.</summary>
    /// <param name="previous">The state left behind.</param>
    /// <param name="current">The state now in force.</param>
    /// <param name="reason">Why, in a few words, for a log line.</param>
    public GemControlStateChangedEventArgs(
        GemControlState previous,
        GemControlState current,
        string? reason = null)
    {
        Previous = previous;
        Current = current;
        Reason = reason;
    }

    /// <summary>Gets the state left behind.</summary>
    public GemControlState Previous { get; }

    /// <summary>Gets the state now in force.</summary>
    public GemControlState Current { get; }

    /// <summary>Gets why the transition happened, when a reason was given.</summary>
    public string? Reason { get; }

    /// <summary>Returns a one-line description.</summary>
    /// <returns>For example <c>OnlineRemote -&gt; HostOffline (S1F15)</c>.</returns>
    public override string ToString()
        => Reason is null ? $"{Previous} -> {Current}" : $"{Previous} -> {Current} ({Reason})";
}
