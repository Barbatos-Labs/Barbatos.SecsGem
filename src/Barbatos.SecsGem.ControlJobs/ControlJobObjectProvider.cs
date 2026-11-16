// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.ControlJobs;

/// <summary>
/// E94's control jobs as stream 14 objects - created, read, commanded and deleted over the wire.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of E94's wire presence, and it is the first provider in this stack that uses
/// all of stream 14 rather than its reading half. E87 had five transactions of its own and E90 had
/// attributes; E94 has a host creating an object with S14F5, driving it with S14F9, reading it with
/// S14F1 and removing it with S14F7 - which is why the actionable half of the Objects package had
/// to exist before this package could.
/// </para>
/// <para>
/// <strong>What a host can and cannot do.</strong> It creates a job, changes what a queued job is
/// for, commands it, and deletes it once it is over. It cannot assign <c>State</c>: a job that a
/// host declared <c>Executing</c> without the machine starting it would be a lie told in an
/// attribute. Every state change goes through <see cref="ControlJobManager"/>, whose refusals
/// become <see cref="ObjectAcknowledgement.Error"/> here - <c>OBJACK</c> has five codes and none of
/// them means "not from that state", so the reason stays on this side, in
/// <see cref="ControlJobResult.Reason"/>, where a log can have it.
/// </para>
/// <para>
/// <strong>On provenance.</strong> The attribute names are E94 as this library reads it and are not
/// checked against anything: the catalogue in this stack has no E94 rows. A host discovers them
/// with S14F17 rather than assuming them, which is the one honest answer to that. What <em>is</em>
/// checked is every item they travel in, all of which are stream 14's and all of which the
/// catalogue defines.
/// </para>
/// <para>
/// An object this provider does not have is left out of its answers rather than refused, so a
/// machine that also does E87 and E90 composes: each provider answers for its own objects and
/// <see cref="ObjectServices"/> reports the rest as
/// <see cref="ObjectAcknowledgement.InvalidObjectInstance"/>.
/// </para>
/// </remarks>
public sealed class ControlJobObjectProvider : IObjectProvider
{
    /// <summary>The object type a control job is - E94's own name for it.</summary>
    public const string ControlJobType = "ControlJob";

    private static readonly string[] AttributeNames =
    [
        "ObjID",
        "State",
        "Completion",
        "CarrierInputSpec",
        "StartMethod",
        "ProcessOrderMgmt",
        "ProcessingCtrlSpec",
    ];

    private readonly ControlJobManager _manager;
    private int _created;

    /// <summary>Exposes a manager's control jobs as objects.</summary>
    /// <param name="manager">The state machine.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> is <see langword="null"/>.</exception>
    public ControlJobObjectProvider(ControlJobManager manager)
        => _manager = manager ?? throw new ArgumentNullException(nameof(manager));

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult<IReadOnlyList<string>>([ControlJobType]);
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
                instances.Add(job.ControlJobId);
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

        // The type when it is given, the instance when it is not - and nothing when neither names
        // a control job, because a provider that answered this for somebody else's object would
        // tell a host a substrate has a StartMethod.
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
                // Left out, which on S14F2 is the only way an absence can be said.
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
    /// Four attributes are settable and only while the job is
    /// <see cref="ControlJobState.Queued"/>: the material, who starts it, and the two strings that
    /// say how. Everything else - its name, its state, how it ended - is the machine's.
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
    /// Where E94's commands arrive. <c>RCMD</c> goes to <see cref="ControlJobManager.Command"/>
    /// unchanged, so a command this library has never heard of is refused by the state machine
    /// rather than rejected here, and a fab with a command of its own can subclass nothing and
    /// still be understood by its own code.
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

                // Error rather than a code naming the state, because OBJACK has no such code. The
                // reason is in result.Reason on this side of the wire.
                Acknowledgement = result.IsAccepted
                    ? ObjectAcknowledgement.Successful
                    : ObjectAcknowledgement.Error,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<ObjectActionResult>>(results);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// A host queues a control job by creating one, which is E94 working the way stream 14 was
    /// built to be worked: the job starts <see cref="ControlJobState.Queued"/> with whatever
    /// attributes the request carried.
    /// </para>
    /// <para>
    /// The name comes from an <c>ObjID</c>, <c>ObjectID</c> or <c>ControlJobID</c> attribute when
    /// the host supplied one, and otherwise the machine names it - which is what S14F6 carrying an
    /// <c>OBJID</c> back is for.
    /// </para>
    /// </remarks>
    public ValueTask<ObjectCreateResult> CreateAsync(ObjectCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(request.ObjectType, ControlJobType, StringComparison.Ordinal))
        {
            // The one place an empty type is refused rather than taken as "mine": a create has to
            // say what it is creating, and guessing would make every unnamed create a control job.
            return ValueTask.FromResult(new ObjectCreateResult
            {
                Acknowledgement = ObjectAcknowledgement.InvalidObjectType,
            });
        }

        List<string> carriers = [];
        var startMethod = ControlJobStartMethod.Automatic;
        var processOrder = string.Empty;
        var processingSpecification = string.Empty;
        string? name = null;

        foreach (var attribute in request.Attributes)
        {
            switch (attribute.Name)
            {
                case "ObjID" or "ObjectID" or "ControlJobID":
                    name = Text(attribute.Value);
                    break;

                case "CarrierInputSpec":
                    carriers = Carriers(attribute.Value);
                    break;

                case "StartMethod":
                    startMethod = StartMethod(attribute.Value);
                    break;

                case "ProcessOrderMgmt":
                    processOrder = Text(attribute.Value);
                    break;

                case "ProcessingCtrlSpec":
                    processingSpecification = Text(attribute.Value);
                    break;

                default:
                    // An attribute a control job does not have, refused before anything is queued:
                    // a job created from a misspelt request is worse than no job.
                    return ValueTask.FromResult(new ObjectCreateResult
                    {
                        Acknowledgement = ObjectAcknowledgement.InvalidAttribute,
                    });
            }
        }

        if (name is null || name.Length == 0)
        {
            do
            {
                name = $"{ControlJobType}{Interlocked.Increment(ref _created)}";
            }
            while (_manager.Find(name) is not null);
        }

        var result = _manager.Create(name, carriers, startMethod, processOrder, processingSpecification);

        return ValueTask.FromResult(result.IsAccepted
            ? new ObjectCreateResult
            {
                Acknowledgement = ObjectAcknowledgement.Successful,
                ObjectId = name,
            }
            : new ObjectCreateResult
            {
                // A name already taken, which is the only thing Create refuses a new job for.
                Acknowledgement = ObjectAcknowledgement.InvalidObjectInstance,
            });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Only a finished job, the manager's rule: deleting a running one would leave its material
    /// with nothing accounting for it. A host that wants it gone sooner aborts, stops or cancels it
    /// first, which is a decision rather than a tidy-up.
    /// </remarks>
    public ValueTask<ObjectAcknowledgement> DeleteAsync(ObjectDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsMine(request.ObjectType))
        {
            return ValueTask.FromResult(ObjectAcknowledgement.InvalidObjectType);
        }

        if (_manager.Find(request.ObjectSpecification) is null)
        {
            return ValueTask.FromResult(ObjectAcknowledgement.InvalidObjectInstance);
        }

        return ValueTask.FromResult(_manager.Delete(request.ObjectSpecification).IsAccepted
            ? ObjectAcknowledgement.Successful
            : ObjectAcknowledgement.Error);
    }

    private static bool IsMine(string objectType)
        => objectType.Length == 0 || string.Equals(objectType, ControlJobType, StringComparison.Ordinal);

    private static SecsItem? Read(ControlJob job, string name) => name switch
    {
        "ObjID" or "ControlJobID" => SecsItem.A(job.ControlJobId),
        "State" => SecsItem.A(job.State.ToString()),

        // A job that has not ended says so with a zero-length item rather than with a word that
        // would read as an ending, which is the rule this library follows for every absent value.
        "Completion" => SecsItem.A(job.Completion?.ToString()),
        "CarrierInputSpec" => SecsItem.L(job.Carriers.Select(carrier => SecsItem.A(carrier))),
        "StartMethod" => SecsItem.A(job.StartMethod.ToString()),
        "ProcessOrderMgmt" => SecsItem.A(job.ProcessOrder),
        "ProcessingCtrlSpec" => SecsItem.A(job.ProcessingSpecification),
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
            // Through the manager rather than onto the field, so a job that has been selected
            // refuses the edit here exactly as it would anywhere else.
            var result = attribute.Name switch
            {
                "CarrierInputSpec" => _manager.Describe(specification, carriers: Carriers(attribute.Value)),
                "StartMethod" => _manager.Describe(specification, startMethod: StartMethod(attribute.Value)),
                "ProcessOrderMgmt" => _manager.Describe(specification, processOrder: Text(attribute.Value)),
                _ => _manager.Describe(specification, processingSpecification: Text(attribute.Value)),
            };

            if (!result.IsAccepted)
            {
                return ObjectAcknowledgement.Error;
            }
        }

        return ObjectAcknowledgement.Successful;
    }

    private static bool IsSettable(string name)
        => name is "CarrierInputSpec" or "StartMethod" or "ProcessOrderMgmt" or "ProcessingCtrlSpec";

    private static string Text(SecsItem item)
        => item.Format.IsText() && item.Count > 0 ? item.GetString().TrimEnd() : string.Empty;

    /// <summary>Reads a carrier input specification from a list, or from a bare item.</summary>
    /// <remarks>Tolerant read: E94 says a list, and a host that sends one carrier as one item means one carrier.</remarks>
    private static List<string> Carriers(SecsItem item)
    {
        if (!item.IsList)
        {
            var single = Text(item);

            return single.Length == 0 ? [] : [single];
        }

        List<string> carriers = [];

        foreach (var entry in item.Items)
        {
            var carrier = Text(entry);

            if (carrier.Length > 0)
            {
                carriers.Add(carrier);
            }
        }

        return carriers;
    }

    /// <summary>Reads a start method from its name, or from a number.</summary>
    /// <remarks>
    /// Tolerant read again: the name is what this package writes, and a machine that sends E94's
    /// code - 0 automatic, 1 host-initiated - is still understood. Anything else is
    /// <see cref="ControlJobStartMethod.Automatic"/>, because a job nobody can start is worse than
    /// one that starts itself.
    /// </remarks>
    private static ControlJobStartMethod StartMethod(SecsItem item)
    {
        var text = item.Format.IsText() || item.IsList || item.Count == 0
            ? Text(item)
            : item.Format switch
            {
                SecsFormat.Binary or SecsFormat.U1 => item.FirstValueOrDefault<byte>().ToString(CultureInfo.InvariantCulture),
                SecsFormat.U2 => item.FirstValueOrDefault<ushort>().ToString(CultureInfo.InvariantCulture),
                SecsFormat.U4 => item.FirstValueOrDefault<uint>().ToString(CultureInfo.InvariantCulture),
                SecsFormat.I1 => item.FirstValueOrDefault<sbyte>().ToString(CultureInfo.InvariantCulture),
                SecsFormat.I2 => item.FirstValueOrDefault<short>().ToString(CultureInfo.InvariantCulture),
                SecsFormat.I4 => item.FirstValueOrDefault<int>().ToString(CultureInfo.InvariantCulture),
                _ => string.Empty,
            };

        return text switch
        {
            "1" or nameof(ControlJobStartMethod.HostInitiated) => ControlJobStartMethod.HostInitiated,
            _ => ControlJobStartMethod.Automatic,
        };
    }
}
