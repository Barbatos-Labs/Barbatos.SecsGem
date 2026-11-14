// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Net;
using Microsoft.Extensions.Options;

namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// Checks <see cref="SecsGemOptions"/> for the mistakes the attributes cannot see.
/// </summary>
/// <remarks>
/// <para>
/// Range attributes catch a negative port. They cannot catch a T8 longer than T3, which is the
/// configuration that looks fine, starts fine, and then produces a transaction timeout whose
/// real cause was a stalled message - at three in the morning, in a fab, with the engineer who
/// wrote the config asleep. Cross-field rules belong here, and they run at startup.
/// </para>
/// <para>
/// Registered through <c>ValidateOnStart</c>, so a bad configuration fails the host's startup
/// rather than the first message.
/// </para>
/// </remarks>
public sealed class SecsGemOptionsValidator : IValidateOptions<SecsGemOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, SecsGemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (!IPAddress.TryParse(options.IpAddress, out _))
        {
            failures.Add($"{nameof(options.IpAddress)} '{options.IpAddress}' is not an IP address.");
        }

        // Every bound is checked here rather than through [Range] attributes and
        // ValidateDataAnnotations(): that extension reflects over the options type, which is a
        // trim hole the AOT analyzer rightly reports, and it produces messages like "The field
        // Port must be between 1 and 65535" when it could say which link failed to start.
        CheckRange(failures, options.Port, 1, 65535, nameof(options.Port));
        CheckRange(failures, options.SocketReceiveBufferSize, 512, 1024 * 1024, nameof(options.SocketReceiveBufferSize));
        CheckRange(failures, options.EncodeBufferInitialSize, 64, 1024 * 1024, nameof(options.EncodeBufferInitialSize));
        CheckRange(failures, options.PrimaryMessageQueueCapacity, 1, 65536, nameof(options.PrimaryMessageQueueCapacity));
        CheckRange(failures, options.PooledPayloadThreshold, 0, int.MaxValue, nameof(options.PooledPayloadThreshold));

        CheckPositive(failures, options.T3, nameof(options.T3));
        CheckPositive(failures, options.T5, nameof(options.T5));
        CheckPositive(failures, options.T6, nameof(options.T6));
        CheckPositive(failures, options.T7, nameof(options.T7));
        CheckPositive(failures, options.T8, nameof(options.T8));

        if (options.T8 >= options.T3)
        {
            failures.Add(
                $"T8 ({options.T8}) must be shorter than T3 ({options.T3}). T8 detects a message that stalled mid-transfer; " +
                "if it is the longer of the two, the transaction times out first and the real fault - a peer that died " +
                "half-way through a message - is reported as a late reply.");
        }

        if (options.T6 >= options.T3)
        {
            failures.Add(
                $"T6 ({options.T6}) should be shorter than T3 ({options.T3}): a control transaction that outlives a data " +
                "transaction leaves the link unusable while data messages are still being accepted.");
        }

        if (options.LinkTestInterval > TimeSpan.Zero && options.LinkTestInterval <= options.T6)
        {
            failures.Add(
                $"{nameof(options.LinkTestInterval)} ({options.LinkTestInterval}) must be longer than T6 ({options.T6}), " +
                "or the next Linktest is due before the previous one has timed out.");
        }

        if (options.MaxMessageLength < MessageHeader.Length)
        {
            failures.Add($"{nameof(options.MaxMessageLength)} must be at least {MessageHeader.Length} bytes - the size of a header.");
        }

        if (options.EncodeBufferInitialSize > options.MaxMessageLength)
        {
            failures.Add(
                $"{nameof(options.EncodeBufferInitialSize)} ({options.EncodeBufferInitialSize}) is larger than " +
                $"{nameof(options.MaxMessageLength)} ({options.MaxMessageLength}), so every send would start by " +
                "allocating more than a message is allowed to be.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);

        static void CheckPositive(List<string> failures, TimeSpan value, string name)
        {
            if (value <= TimeSpan.Zero)
            {
                failures.Add($"{name} must be greater than zero, but is {value}.");
            }
        }

        static void CheckRange(List<string> failures, int value, int minimum, int maximum, string name)
        {
            if (value < minimum || value > maximum)
            {
                failures.Add($"{name} must be between {minimum} and {maximum}, but is {value}.");
            }
        }
    }
}
