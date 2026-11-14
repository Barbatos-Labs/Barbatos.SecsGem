// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem;

/// <summary>
/// The base of every error this stack raises for a SECS-level problem.
/// </summary>
/// <remarks>
/// Catch this to handle "the protocol went wrong" without also catching socket and cancellation
/// failures. The message that caused the problem, when there is one, hangs off
/// <see cref="SecsMessage"/>.
/// </remarks>
public class SecsException : Exception
{
    /// <summary>Initialises a new instance with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public SecsException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance with a message and an inner exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SecsException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initialises a new instance tied to the message that caused the problem.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="secsMessage">The SECS message the failure concerns.</param>
    public SecsException(string message, SecsMessage? secsMessage)
        : base(message)
        => SecsMessage = secsMessage;

    /// <summary>Gets the SECS message this failure concerns, when one is known.</summary>
    public SecsMessage? SecsMessage { get; }
}

/// <summary>
/// Thrown when a byte stream does not decode to a valid SECS-II item tree.
/// </summary>
/// <remarks>
/// The HSMS layer answers this with <c>S9F7 (Illegal Data)</c> when it can identify the
/// offending transaction.
/// </remarks>
public sealed class SecsDecodeException : SecsException
{
    /// <summary>Initialises a new instance with a message.</summary>
    /// <param name="message">What was wrong with the data.</param>
    public SecsDecodeException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance with a message and the byte offset it was found at.</summary>
    /// <param name="message">What was wrong with the data.</param>
    /// <param name="offset">The offset within the message body where decoding failed.</param>
    public SecsDecodeException(string message, long offset)
        : base($"{message} (at byte offset {offset})")
        => Offset = offset;

    /// <summary>Gets the offset within the message body where decoding failed, or <c>-1</c> when unknown.</summary>
    public long Offset { get; } = -1;
}

/// <summary>
/// Thrown when one of the HSMS protocol timers expires.
/// </summary>
/// <remarks>
/// <see cref="Timer"/> names which one, so a caller can tell a transaction timeout (T3, worth
/// retrying) from an inter-character timeout (T8, which has already dropped the connection).
/// </remarks>
public sealed class SecsTimeoutException : SecsException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="timer">The timer that expired.</param>
    /// <param name="interval">The configured interval.</param>
    /// <param name="secsMessage">The message in flight, when there is one.</param>
    public SecsTimeoutException(SecsTimer timer, TimeSpan interval, SecsMessage? secsMessage = null)
        : base($"{timer} timeout after {interval.TotalSeconds:0.###} s.", secsMessage)
    {
        Timer = timer;
        Interval = interval;
    }

    /// <summary>Gets the timer that expired.</summary>
    public SecsTimer Timer { get; }

    /// <summary>Gets the interval that was exceeded.</summary>
    public TimeSpan Interval { get; }
}

/// <summary>
/// The HSMS protocol timers, as named by SEMI E37.
/// </summary>
public enum SecsTimer
{
    /// <summary>Reply timeout: a primary message was sent and no secondary arrived.</summary>
    T3,

    /// <summary>Connect separation timeout: the minimum wait between two connection attempts.</summary>
    T5,

    /// <summary>Control transaction timeout: a control request got no control response.</summary>
    T6,

    /// <summary>Not-selected timeout: the link reached CONNECTED but never reached SELECTED.</summary>
    T7,

    /// <summary>Network inter-character timeout: a message began arriving and then stalled mid-way.</summary>
    T8,
}
