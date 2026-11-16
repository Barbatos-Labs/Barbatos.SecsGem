// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.ProcessJobs;

/// <summary>
/// E40's process jobs as stream 14 objects - where a job is described, as against driven.
/// </summary>
/// <remarks>
/// <para>
/// E40 has a stream of its own, so this provider is not the standard's whole wire presence the way
/// E90's and E94's providers were. It is the other half of a division the standard makes itself:
/// <strong>stream 16 drives a job and stream 14 describes it.</strong> S16F1 carries three items -
/// an identifier, a recipe and some material - and everything else a job has is an attribute, so a
/// host creates a job over stream 16 and then sets how its recipe may be tuned, whether it starts
/// itself and what order its material goes in over stream 14, while the job is still queued.
/// </para>
/// <para>
/// <strong>Every attribute name here is a data item the catalogue defines.</strong> That is worth
/// saying because it is the first time in these four packages it has been true: E87's, E90's and
/// E94's attribute names are those standards as this library reads them, while
/// <c>PRJOBID</c>, <c>PRSTATE</c>, <c>PPID</c>, <c>PRRECIPEMETHOD</c>, <c>PRPROCESSSTART</c>,
/// <c>PRMTRLORDER</c> and <c>PRPAUSEEVENTID</c> are all in the dictionary, five of them with their
/// code values. A host still discovers them with S14F17 rather than assuming them.
/// </para>
/// <para>
/// <strong>No create and no delete.</strong> S14F5 and S14F7 are refused for a process job, which
/// is the one place in this stack a provider turns down the actionable half on purpose: E40 has
/// S16F1 and S16F9 for those, and a create over stream 14 would have to invent the recipe that
/// S16F1 insists on. An action does work - a host with nothing but stream 14 can send E40's
/// commands over S14F9 - but S16F3 says more about a refusal, because <c>CMDA</c> can tell a host
/// that a command does not exist where <c>OBJACK</c> has one code for every no.
/// </para>
/// </remarks>
public sealed class ProcessJobObjectProvider : IObjectProvider
{
    /// <summary>The object type a process job is.</summary>
    public const string ProcessJobType = "ProcessJob";

    private static readonly string[] AttributeNames =
    [
        "PRJOBID",
        "PRSTATE",
        "PPID",
        "PRRECIPEMETHOD",
        "PRPROCESSSTART",
        "PRMTRLORDER",
        "PRPAUSEEVENTID",
    ];

    private readonly ProcessJobManager _manager;

    /// <summary>Exposes a manager's process jobs as objects.</summary>
    /// <param name="manager">The state machines.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> is <see langword="null"/>.</exception>
    public ProcessJobObjectProvider(ProcessJobManager manager)
        => _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyList<string>>([ProcessJobType]);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GetInstancesAsync(string objectType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectType);
        cancellationToken.ThrowIfCancellationRequested();

        List<string> instances = [];

        if (IsMine(objectType))
        {
            foreach (var job in _manager.Jobs)
            {
                instances.Add(job.ProcessJobId);
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

        var mine = objectType.Length > 0
            ? IsMine(objectType)
            : _manager.Find(objectSpecification) is not null;

        return ValueTask.FromResult<IReadOnlyList<string>>(mine ? AttributeNames : []);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<ObjectAttributeValues>> GetAttributesAsync(
        ObjectAttributeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        List<ObjectAttributeValues> values = [];

        if (!IsMine(request.ObjectType))
        {
            return ValueTask.FromResult<IReadOnlyList<ObjectAttributeValues>>(values);
        }

        foreach (var specification in request.Objects)
        {
            if (_manager.Find(specification) is not { } job)
            {
                continue;
            }

            List<ObjectAttribute> attributes = [];

            foreach (var name in request.Attributes.Count == 0 ? AttributeNames : request.Attributes)
            {
                if (Read(job, name) is { } value)
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
    /// Five attributes are settable: the recipe and the three that say how it is to be run, while
    /// the job is still <see cref="ProcessJobState.Queued"/>, and the pause event at any time
    /// before the job is over. <c>PRJOBID</c> and <c>PRSTATE</c> are the machine's - a host that
    /// could write the state would start a job by saying it had started, which is the same refusal
    /// E87, E90 and E94 all make.
    /// </remarks>
    public ValueTask<IReadOnlyList<ObjectSetResult>> SetAttributesAsync(
        ObjectAttributeWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();

        List<ObjectSetResult> results = [];

        if (!IsMine(write.ObjectType))
        {
            return ValueTask.FromResult<IReadOnlyList<ObjectSetResult>>(results);
        }

        foreach (var specification in write.Objects)
        {
            if (_manager.Find(specification) is null)
            {
                continue;
            }

            results.Add(new ObjectSetResult
            {
                ObjectSpecification = specification,
                Acknowledgement = Write(specification, write.Attributes),
            });
        }

        return ValueTask.FromResult<IReadOnlyList<ObjectSetResult>>(results);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// E40's commands, for a host that has stream 14 and not stream 16. <c>RCMD</c> goes to
    /// <see cref="ProcessJobManager.Command"/> unchanged, and a refusal comes back as
    /// <see cref="ObjectAcknowledgement.Error"/> - which is why a host that can send S16F3 should:
    /// <c>CMDA</c> distinguishes a command that does not exist from one that cannot be done now,
    /// and this reply cannot.
    /// </remarks>
    public ValueTask<IReadOnlyList<ObjectActionResult>> ActAsync(
        ObjectActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        List<ObjectActionResult> results = [];

        if (!IsMine(request.ObjectType))
        {
            return ValueTask.FromResult<IReadOnlyList<ObjectActionResult>>(results);
        }

        foreach (var specification in request.Objects)
        {
            if (_manager.Find(specification) is null)
            {
                continue;
            }

            var result = _manager.Command(specification, request.Command);

            results.Add(new ObjectActionResult
            {
                ObjectSpecification = specification,
                Acknowledgement = result.IsAccepted
                    ? ObjectAcknowledgement.Successful
                    : ObjectAcknowledgement.Error,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<ObjectActionResult>>(results);
    }

    private static bool IsMine(string objectType)
        => objectType.Length == 0 || string.Equals(objectType, ProcessJobType, StringComparison.Ordinal);

    private static SecsItem? Read(ProcessJob job, string name) => name switch
    {
        "PRJOBID" => SecsItem.A(job.ProcessJobId),

        // The state as its code, not its name: PRSTATE is U1 with ten defined values, so a number
        // here is the standard's own answer rather than this library's spelling of it.
        "PRSTATE" => SecsItem.U1((byte)job.State),
        "PPID" => SecsItem.A(job.RecipeId),
        "PRRECIPEMETHOD" => SecsItem.U1((byte)job.RecipeMethod),
        "PRPROCESSSTART" => SecsItem.Boolean(job.StartsAutomatically),
        "PRMTRLORDER" => SecsItem.U1((byte)job.MaterialOrder),

        // A job with no pause event says so with a zero-length item rather than with event 0.
        "PRPAUSEEVENTID" => job.PauseEventId is { } pauseEvent ? SecsItem.U4(pauseEvent) : SecsItem.U4(),
        _ => null,
    };

    private ObjectAcknowledgement Write(string specification, IReadOnlyList<ObjectAttribute> attributes)
    {
        // Checked before anything is written, so a set naming one read-only attribute changes
        // nothing - the rule every other provider in this stack follows.
        foreach (var attribute in attributes)
        {
            if (!IsSettable(attribute.Name))
            {
                return ObjectAcknowledgement.InvalidAttribute;
            }
        }

        foreach (var attribute in attributes)
        {
            // Through the manager rather than onto the field, so a job the machine has taken up
            // refuses the edit here exactly as it would anywhere else.
            var result = attribute.Name switch
            {
                "PPID" => _manager.Describe(specification, recipeId: Text(attribute.Value)),
                "PRRECIPEMETHOD" => _manager.Describe(
                    specification,
                    recipeMethod: Number(attribute.Value) == (byte)ProcessJobRecipeMethod.WithVariableTuning
                        ? ProcessJobRecipeMethod.WithVariableTuning
                        : ProcessJobRecipeMethod.WithoutVariableTuning),
                "PRPROCESSSTART" => _manager.Describe(specification, startsAutomatically: Flag(attribute.Value)),
                "PRMTRLORDER" => _manager.Describe(specification, materialOrder: Order(attribute.Value)),
                _ => _manager.SetPauseEvent(
                    specification,
                    attribute.Value.Count == 0 ? null : (uint)Number(attribute.Value)),
            };

            if (!result.IsAccepted)
            {
                return ObjectAcknowledgement.Error;
            }
        }

        return ObjectAcknowledgement.Successful;
    }

    private static bool IsSettable(string name)
        => name is "PPID" or "PRRECIPEMETHOD" or "PRPROCESSSTART" or "PRMTRLORDER" or "PRPAUSEEVENTID";

    private static string Text(SecsItem item)
        => item.Format.IsText() && item.Count > 0 ? item.GetString().TrimEnd() : string.Empty;

    /// <summary>Reads a code out of whatever format it arrived in.</summary>
    private static ulong Number(SecsItem item)
    {
        if (item.Format.IsText())
        {
            return ulong.TryParse(Text(item), CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        if (item.IsList || item.Count == 0)
        {
            return 0;
        }

        return item.Format switch
        {
            SecsFormat.Binary or SecsFormat.U1 => item.FirstValueOrDefault<byte>(),
            SecsFormat.U2 => item.FirstValueOrDefault<ushort>(),
            SecsFormat.U4 => item.FirstValueOrDefault<uint>(),
            SecsFormat.U8 => item.FirstValueOrDefault<ulong>(),
            SecsFormat.I1 => NonNegative(item.FirstValueOrDefault<sbyte>()),
            SecsFormat.I2 => NonNegative(item.FirstValueOrDefault<short>()),
            SecsFormat.I4 => NonNegative(item.FirstValueOrDefault<int>()),
            SecsFormat.I8 => NonNegative(item.FirstValueOrDefault<long>()),
            SecsFormat.Boolean => item.FirstValueOrDefault<bool>() ? 1UL : 0UL,
            _ => 0,
        };
    }

    private static ulong NonNegative(long value) => value < 0 ? 0 : (ulong)value;

    /// <summary>Reads <c>PRPROCESSSTART</c>, which is a <c>BOOLEAN</c> and sometimes is not.</summary>
    /// <remarks>Tolerant read: a machine that sends one, zero, <c>TRUE</c> or <c>True</c> means the same thing, and the item's one defined thing is its format.</remarks>
    private static bool Flag(SecsItem item)
    {
        if (item.Format is SecsFormat.Boolean && item.Count > 0)
        {
            return item.FirstValueOrDefault<bool>();
        }

        if (item.Format.IsText() && item.Count > 0)
        {
            var text = Text(item);

            return bool.TryParse(text, out var parsed)
                ? parsed
                : text is not ("0" or "");
        }

        return Number(item) != 0;
    }

    /// <summary>Reads <c>PRMTRLORDER</c>, with anything undefined reading as arrival sequence.</summary>
    /// <remarks>
    /// The catalogue defines three code values and no zero, so an unrecognised one has to read as
    /// something: arrival sequence is the only one of the three that needs nothing of the machine,
    /// which makes it the safe answer rather than the favourable one.
    /// </remarks>
    private static ProcessJobMaterialOrder Order(SecsItem item) => Number(item) switch
    {
        (ulong)ProcessJobMaterialOrder.MaximizeThroughput => ProcessJobMaterialOrder.MaximizeThroughput,
        (ulong)ProcessJobMaterialOrder.ListedSequence => ProcessJobMaterialOrder.ListedSequence,
        _ => ProcessJobMaterialOrder.ArrivalSequence,
    };
}
