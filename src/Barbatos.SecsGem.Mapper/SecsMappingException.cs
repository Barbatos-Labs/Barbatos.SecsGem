// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Mapping;

/// <summary>
/// Thrown when an object and an item tree do not fit each other.
/// </summary>
/// <remarks>
/// <see cref="MemberPath"/> is the point of this type. SECS-II is positional, so "expected U4,
/// found ASCII" on its own sends an engineer counting brackets; <c>Report.Values[2].Value</c>
/// does not.
/// </remarks>
public sealed class SecsMappingException : SecsException
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="message">What did not fit.</param>
    public SecsMappingException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance with an inner exception.</summary>
    /// <param name="message">What did not fit.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SecsMappingException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initialises a new instance naming the member the failure concerns.</summary>
    /// <param name="message">What did not fit.</param>
    /// <param name="memberPath">The dotted path to the member, for example <c>Report.Values[2].Value</c>.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SecsMappingException(string message, string memberPath, Exception? innerException = null)
        : base($"{memberPath}: {message}", innerException)
        => MemberPath = memberPath;

    /// <summary>Gets the dotted path to the member the failure concerns, when one is known.</summary>
    public string? MemberPath { get; }
}
