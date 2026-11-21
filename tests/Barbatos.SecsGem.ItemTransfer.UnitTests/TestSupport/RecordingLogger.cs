// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.ItemTransfer.UnitTests;

/// <summary>
/// A logger that keeps what it was told, so a test can assert on a diagnostic.
/// </summary>
/// <remarks>
/// Worth having wherever a branch's whole effect is the log line, and stream 21 has several: a
/// reply that answers for an item nobody asked about, and an acknowledgement above 1 - none of
/// this stream's acknowledgement items has a table at all, so the code is logged because there is
/// nowhere else for it to go.
/// </remarks>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    /// <summary>Gets everything logged, in order.</summary>
    public IReadOnlyCollection<(LogLevel Level, string Message)> Entries => _entries;

    /// <summary>Gets the messages logged at a level or above.</summary>
    /// <param name="level">The lowest level wanted.</param>
    /// <returns>The messages.</returns>
    public IReadOnlyList<string> From(LogLevel level)
        => [.. _entries.Where(entry => entry.Level >= level).Select(entry => entry.Message)];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        _entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
