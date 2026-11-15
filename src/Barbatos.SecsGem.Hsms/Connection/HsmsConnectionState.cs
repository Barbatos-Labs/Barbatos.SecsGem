// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// Where an HSMS-SS connection is in its lifecycle - SEMI E37.
/// </summary>
/// <remarks>
/// E37 draws these as NOT CONNECTED, CONNECTED/NOT SELECTED and CONNECTED/SELECTED. The nesting
/// adds nothing here, because HSMS-SS allows exactly one selection at a time, so the three states
/// are a plain sequence: a link is established, then selected, and data flows only in the last of
/// them.
/// </remarks>
public enum HsmsConnectionState
{
    /// <summary>No TCP connection. An active entity is retrying, no sooner than every T5.</summary>
    NotConnected,

    /// <summary>
    /// Connected but not selected. T7 is running: a link that sits here is dropped rather than
    /// left half-open.
    /// </summary>
    /// <remarks>
    /// An active entity sends Select.req on arriving here. A passive one waits for it. Data
    /// messages are refused with <see cref="RejectReason.EntityNotSelected"/>.
    /// </remarks>
    Connected,

    /// <summary>Selected. Data messages flow, and Linktest keeps the link honest.</summary>
    Selected,
}

/// <summary>
/// Reports a connection state transition.
/// </summary>
/// <remarks>
/// Raised on the connection's own actor loop, so a handler runs before the next transition is
/// processed and sees the states in order. A slow handler therefore delays the connection; do
/// real work elsewhere.
/// </remarks>
public sealed class HsmsConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="previous">The state being left.</param>
    /// <param name="current">The state being entered.</param>
    /// <param name="reason">Why, in a few words, for logs.</param>
    public HsmsConnectionStateChangedEventArgs(
        HsmsConnectionState previous,
        HsmsConnectionState current,
        string? reason = null)
    {
        Previous = previous;
        Current = current;
        Reason = reason;
    }

    /// <summary>Gets the state being left.</summary>
    public HsmsConnectionState Previous { get; }

    /// <summary>Gets the state being entered.</summary>
    public HsmsConnectionState Current { get; }

    /// <summary>Gets why the transition happened, when there is something useful to say.</summary>
    /// <remarks>For logs and diagnostics. Do not branch on it; branch on <see cref="Current"/>.</remarks>
    public string? Reason { get; }

    /// <summary>Returns a diagnostic rendering of the transition.</summary>
    /// <returns><c>Connected -&gt; Selected (Select.req from peer)</c>.</returns>
    public override string ToString()
        => Reason is null ? $"{Previous} -> {Current}" : $"{Previous} -> {Current} ({Reason})";
}
