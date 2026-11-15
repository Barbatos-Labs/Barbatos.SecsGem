// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Options;

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// How the GEM layer behaves. Bound from <c>Barbatos:Gem</c> and validated on start.
/// </summary>
/// <remarks>
/// Separate from <c>SecsGemOptions</c> on purpose: those are E37 - addresses, timers, frame limits
/// - and these are E30. A fab changes the port without touching the model name, and a model name
/// has no business in a section about sockets.
/// </remarks>
public sealed class GemOptions
{
    /// <summary>The configuration section these are bound from.</summary>
    public const string SectionName = "Barbatos:Gem";

    /// <summary>E30's cap on the length of MDLN and SOFTREV.</summary>
    public const int MaxIdentityLength = 20;

    /// <summary>E30's cap on the length of alarm text.</summary>
    public const int MaxAlarmTextLength = 120;

    /// <summary>Gets or sets MDLN - the equipment model name, as S1F2 and S1F14 report it.</summary>
    public string ModelName { get; set; } = "BRBTOS";

    /// <summary>Gets or sets SOFTREV - the software revision, as S1F2 and S1F14 report it.</summary>
    public string SoftwareRevision { get; set; } = "0.1.0";

    /// <summary>Gets or sets a value indicating whether communication may be established at all.</summary>
    /// <remarks>
    /// <see langword="false"/> is E30's DISABLED state: no S1F13 goes out, and one that arrives is
    /// denied. It exists for equipment that is physically cabled to a host it has not been
    /// commissioned to talk to yet.
    /// </remarks>
    public bool CommunicationEnabled { get; set; } = true;

    /// <summary>Gets or sets how long to wait before retrying S1F13 after a denial or a timeout.</summary>
    /// <remarks>
    /// E30's WAIT DELAY. Without it, two entities that come up together spend the day exchanging
    /// denied requests, each one a transaction the other has to answer.
    /// </remarks>
    public TimeSpan EstablishCommunicationsRetryInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the control state to attempt once communication is established.</summary>
    /// <remarks>
    /// <see cref="GemControlState.OnlineRemote"/> suits equipment that exists to be driven;
    /// <see cref="GemControlState.OnlineLocal"/> suits a machine where a person is normally in
    /// charge; <see cref="GemControlState.EquipmentOffline"/> means it will not go on-line until
    /// something asks.
    /// </remarks>
    public GemControlState DefaultControlState { get; set; } = GemControlState.OnlineRemote;

    /// <summary>Gets or sets a value indicating whether S6F11 asks for an S6F12.</summary>
    /// <remarks>
    /// With the W bit set, an event report occupies a transaction until the host answers or T3
    /// expires. Clearing it makes reporting fire-and-forget, which is the right trade for
    /// high-rate trace data and the wrong one for anything a host must not miss.
    /// </remarks>
    public bool EventReportsExpectReply { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether S5F1 asks for an S5F2.</summary>
    public bool AlarmReportsExpectReply { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether S2F31 may set the equipment clock.</summary>
    /// <remarks>
    /// This never touches the operating system clock. The GEM layer keeps an offset of its own and
    /// applies it to the time it <em>reports</em>, so a host that sets the clock changes S2F18 and
    /// the timestamps on reports, and changes nothing else on the machine. Refusing instead is a
    /// matter of setting this to <see langword="false"/>, which answers TIACK 1.
    /// </remarks>
    public bool AcceptHostClockSet { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the equipment holds reports for an absent host.</summary>
    /// <remarks>
    /// <para>
    /// This is the configuration override, not the switch. Spooling happens only when an
    /// <see cref="IGemSpool"/> is configured - <c>AddGemSpooling</c> - and equipment that never
    /// calls it does not spool and does not answer S6F23, whatever this says. Having registered a
    /// spool, the sensible default is to use it, so this defaults to <see langword="true"/>; set it
    /// to <see langword="false"/> in <c>Barbatos:Gem</c> to turn a spooling build off without
    /// rebuilding it.
    /// </para>
    /// <para>
    /// Spooling off means a report that cannot be delivered is logged and dropped, which is the
    /// right answer for most equipment: a report about something that happened an hour ago is
    /// rarely worth acting on. Spooling on means the equipment holds what it could not send and
    /// waits for the host to ask with S6F23. It never pushes the backlog unasked - the host decides
    /// whether it wants an hour of history or a clean start.
    /// </para>
    /// </remarks>
    public bool SpoolingEnabled { get; set; } = true;

    /// <summary>Gets or sets how many messages the default spool will hold.</summary>
    public int SpoolCapacity { get; set; } = 1_000;

    /// <summary>Gets or sets what the default spool does when it is full.</summary>
    public GemSpoolOverflowPolicy SpoolOverflow { get; set; } = GemSpoolOverflowPolicy.DiscardNewest;

    /// <summary>Gets or sets where a durable spool keeps its records.</summary>
    /// <remarks>
    /// Used by <c>AddGemFileSpooling</c> when it is called without a directory of its own, so the
    /// path can come from configuration rather than from the code that wires the equipment up -
    /// which is where a path belongs when the same binary is deployed to more than one machine.
    /// Ignored entirely by the in-memory spool.
    /// </remarks>
    public string? SpoolDirectory { get; set; }

    /// <summary>Gets or sets how many messages one S6F23 transmit will send. Zero means all of them.</summary>
    /// <remarks>
    /// E30's MaxSpoolTransmit. A cap matters because the alternative is a thousand transactions
    /// back to back the moment a link recovers, which is how a host that has just come up gets
    /// knocked over by the equipment that was waiting for it.
    /// </remarks>
    public int MaxSpoolTransmit { get; set; }

    /// <summary>Gets or sets a value indicating whether the link going down resets the host's configuration.</summary>
    /// <remarks>
    /// On by default. A host that reconnects has no reason to believe its report definitions
    /// survived, and E30 does not promise they did - so keeping them would be inventing a
    /// guarantee that only shows up as a host receiving a report it never asked for.
    /// </remarks>
    public bool ResetLinkStateOnDisconnect { get; set; } = true;
}

/// <summary>Validates <see cref="GemOptions"/> on start.</summary>
/// <remarks>
/// A model name too long for MDLN is the kind of mistake that surfaces as equipment a host refuses
/// to talk to, days later and somewhere else. Failing the host at start is cheaper.
/// </remarks>
public sealed class GemOptionsValidator : IValidateOptions<GemOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, GemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.ModelName))
        {
            failures.Add($"{nameof(GemOptions.ModelName)} (MDLN) must not be empty.");
        }
        else if (options.ModelName.Length > GemOptions.MaxIdentityLength)
        {
            failures.Add(
                $"{nameof(GemOptions.ModelName)} is {options.ModelName.Length} characters; "
                + $"E30 allows {GemOptions.MaxIdentityLength}.");
        }

        if (options.SoftwareRevision.Length > GemOptions.MaxIdentityLength)
        {
            failures.Add(
                $"{nameof(GemOptions.SoftwareRevision)} is {options.SoftwareRevision.Length} characters; "
                + $"E30 allows {GemOptions.MaxIdentityLength}.");
        }

        if (options.EstablishCommunicationsRetryInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(GemOptions.EstablishCommunicationsRetryInterval)} must be positive; "
                + "a zero delay is a denial loop at the speed of the link.");
        }

        if (options.SpoolingEnabled && options.SpoolCapacity < 1)
        {
            failures.Add(
                $"{nameof(GemOptions.SpoolCapacity)} must be at least 1 when spooling is enabled; "
                + "a spool that holds nothing is a slower way of dropping the message.");
        }

        if (options.MaxSpoolTransmit < 0)
        {
            failures.Add($"{nameof(GemOptions.MaxSpoolTransmit)} must not be negative. Zero means all of them.");
        }

        // Set but blank, rather than not set. The second is how most equipment is configured; the
        // first is a typo in a configuration file, and it would otherwise surface as a directory
        // called "" the first time a host went away.
        if (options.SpoolDirectory is not null && string.IsNullOrWhiteSpace(options.SpoolDirectory))
        {
            failures.Add(
                $"{nameof(GemOptions.SpoolDirectory)} is set to blank. Leave it unset for no durable "
                + "spool, or give it a path.");
        }

        if (options.DefaultControlState is GemControlState.AttemptOnline or GemControlState.HostOffline)
        {
            failures.Add(
                $"{nameof(GemOptions.DefaultControlState)} must be a state the equipment can choose: "
                + "EquipmentOffline, OnlineLocal or OnlineRemote. AttemptOnline is transient and "
                + "HostOffline is the host's decision to make.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
