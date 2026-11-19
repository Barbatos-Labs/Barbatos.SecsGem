// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.PerformanceTracking;

/// <summary>
/// E116's modules as stream 14 objects.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of E116's wire presence. The standard defines no messages: a module is an
/// object, its state and its accounting are read with <c>GetAttr</c>, and the only other thing a
/// host sees is whichever collection events the equipment chose to define for the transitions.
/// </para>
/// <para>
/// <strong>Nothing here is writable, and nothing here is actionable.</strong> Every other provider
/// in this stack has at least a label a host owns; this one has none, and the refusal is the
/// feature. A module's EPT state is an observation of a machine, not an instruction to it - a host
/// that could set <c>EPTState</c> could record a chamber as executing while it stood empty, and
/// then the utilisation this standard exists to produce would be a number the fab wrote down
/// itself. So a set is refused with
/// <see cref="ObjectAcknowledgement.InvalidAttribute"/> and an action with
/// <see cref="ObjectAcknowledgement.Error"/>, and a host that wants the machine to do something
/// tells it to do that something - a remote command, a process job - and reads the consequence
/// here afterwards.
/// </para>
/// <para>
/// <strong>On the attribute names.</strong> E116 is not in the catalogue - not a message, not an
/// item - so these are this library's spelling of E116's concepts rather than quoted item names,
/// and a host discovers them with S14F17 rather than assuming them. A fab whose host expects
/// different spellings changes them in this one file.
/// </para>
/// <para>
/// The durations come from one <see cref="PerformanceTracker.Statistics"/> snapshot, so they agree
/// with each other; the labels are read from the module afterwards. A read that lands in the same
/// instant as a transition can therefore pair a new state with the old task, which is a property
/// of asking a running machine what it is doing and not something this class can fix by holding a
/// lock for longer.
/// </para>
/// </remarks>
public sealed class PerformanceObjectProvider : IObjectProvider
{
    /// <summary>The object type a module is.</summary>
    /// <remarks>
    /// E116 calls the thing a module; this is the spelling that goes on the wire as <c>OBJTYPE</c>,
    /// and it is a constant so that a fab needing another one has somewhere to change it.
    /// </remarks>
    public const string ModuleType = "EPTModule";

    /// <summary>The 16-character form <c>TIMESTAMP</c> has in the catalogue.</summary>
    private const string TimeFormat = "yyyyMMddHHmmssff";

    private static readonly string[] ModuleAttributeNames =
    [
        "ObjID",
        "EPTState",
        "PreviousEPTState",
        "EPTStateEntryTime",
        "BlockedReasonCode",
        "BlockedReasonText",
        "TaskName",
        "TaskType",
        "TaskStartTime",
        "TimeInState",
        "TimeIdle",
        "TimeExecuting",
        "TimeBlocked",
        "TasksCompleted",
        "TasksAbandoned",
    ];

    private readonly PerformanceTracker _tracker;

    /// <summary>Exposes a tracker's modules as objects.</summary>
    /// <param name="tracker">The state machines and the accounting.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tracker"/> is <see langword="null"/>.</exception>
    public PerformanceObjectProvider(PerformanceTracker tracker)
        => _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyList<string>>([ModuleType]);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GetInstancesAsync(string objectType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectType);
        cancellationToken.ThrowIfCancellationRequested();

        List<string> instances = [];

        if (string.Equals(objectType, ModuleType, StringComparison.Ordinal))
        {
            foreach (var module in _tracker.Modules)
            {
                instances.Add(module.ModuleId);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<string>>(instances);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GetAttributeNamesAsync(
        string objectSpecification,
        string objectType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(objectType);
        cancellationToken.ThrowIfCancellationRequested();

        // One type, so there is nothing to disambiguate: a question about any other type is not
        // this provider's, and an empty answer is how the composite says so.
        var mine = string.Equals(objectType, ModuleType, StringComparison.Ordinal)
            || (objectType.Length == 0 && _tracker.FindModule(objectSpecification) is not null);

        return ValueTask.FromResult<IReadOnlyList<string>>(mine ? ModuleAttributeNames : []);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<ObjectAttributeValues>> GetAttributesAsync(
        ObjectAttributeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        List<ObjectAttributeValues> values = [];

        foreach (var specification in request.Objects)
        {
            if (_tracker.Statistics(specification) is not { } statistics
                || _tracker.FindModule(specification) is not { } module)
            {
                // Left out rather than reported, which on S14F2 and S14F20 is the only way an
                // absence can be said: neither carries an acknowledgement code.
                continue;
            }

            List<ObjectAttribute> attributes = [];

            foreach (var name in request.Attributes.Count == 0 ? ModuleAttributeNames : request.Attributes)
            {
                if (Read(module, statistics, name) is { } value)
                {
                    attributes.Add(new ObjectAttribute { Name = name, Value = value });
                }
            }

            values.Add(new ObjectAttributeValues
            {
                ObjectSpecification = specification,
                Attributes = attributes,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<ObjectAttributeValues>>(values);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Always a refusal for a module this provider has, because a module has nothing a host may
    /// set: see the remarks on the class. Checked before anything is written, which here is
    /// trivially true - there is nothing to write.
    /// </remarks>
    public ValueTask<IReadOnlyList<ObjectSetResult>> SetAttributesAsync(
        ObjectAttributeWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();

        List<ObjectSetResult> results = [];

        foreach (var specification in write.Objects)
        {
            results.Add(new ObjectSetResult
            {
                ObjectSpecification = specification,
                Acknowledgement = _tracker.FindModule(specification) is null
                    ? ObjectAcknowledgement.InvalidObjectInstance
                    : ObjectAcknowledgement.InvalidAttribute,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<ObjectSetResult>>(results);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Overridden to refuse rather than left at the default, and the difference matters: the
    /// default answers nothing, which the library reads as "no object of mine", and that would be
    /// a lie here. The module <em>is</em> this provider's and the answer is no - E116 has no
    /// actions, because its state follows what the machine does rather than what it is told.
    /// </remarks>
    public ValueTask<IReadOnlyList<ObjectActionResult>> ActAsync(
        ObjectActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        List<ObjectActionResult> results = [];

        foreach (var specification in request.Objects)
        {
            if (_tracker.FindModule(specification) is null)
            {
                continue;
            }

            results.Add(new ObjectActionResult
            {
                ObjectSpecification = specification,
                Acknowledgement = ObjectAcknowledgement.Error,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<ObjectActionResult>>(results);
    }

    private static SecsItem? Read(PerformanceModule module, PerformanceStatistics statistics, string name) => name switch
    {
        "ObjID" => SecsItem.A(module.ModuleId),
        "EPTState" => SecsItem.A(module.State.ToString()),
        "PreviousEPTState" => SecsItem.A(module.PreviousState.ToString()),
        "EPTStateEntryTime" => Stamp(module.StateSince),

        // A zero-length item for a module that is not blocked, which is this library's rule
        // everywhere: an absent value is an empty item rather than a word like "none" that a host
        // would have to know not to believe.
        "BlockedReasonCode" => SecsItem.A(module.BlockedReason?.Code),
        "BlockedReasonText" => SecsItem.A(module.BlockedReason?.Text),
        "TaskName" => SecsItem.A(module.CurrentTask?.Name),
        "TaskType" => SecsItem.A(module.CurrentTask?.TaskType),
        "TaskStartTime" => module.CurrentTask is { } task ? Stamp(task.StartedAt) : SecsItem.A(null),
        "TimeInState" => Seconds(statistics.TimeInState),
        "TimeIdle" => Seconds(statistics.TimeIdle),
        "TimeExecuting" => Seconds(statistics.TimeExecuting),
        "TimeBlocked" => Seconds(statistics.TimeBlocked),
        "TasksCompleted" => SecsItem.U4((uint)statistics.TasksCompleted),
        "TasksAbandoned" => SecsItem.U4((uint)statistics.TasksAbandoned),
        _ => null,
    };

    private static SecsItem Stamp(DateTimeOffset at)
        => SecsItem.A(at.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));

    /// <summary>Whole seconds as a <c>U4</c>.</summary>
    /// <remarks>
    /// Seconds because that is the resolution a utilisation report is read at, and unsigned
    /// because a duration cannot be negative - which the tracker guarantees by measuring with a
    /// monotonic timestamp rather than a wall clock. Anything over a hundred and thirty-six years
    /// saturates rather than wrapping, since a machine that has been idle that long has a bigger
    /// problem than this attribute.
    /// </remarks>
    private static SecsItem Seconds(TimeSpan span)
        => SecsItem.U4((uint)Math.Clamp(span.TotalSeconds, 0d, uint.MaxValue));
}
