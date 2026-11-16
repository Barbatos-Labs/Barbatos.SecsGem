// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Objects;

/// <summary>
/// Object services: <c>GetAttr</c>, <c>SetAttr</c> and the three namelists, in both directions.
/// </summary>
/// <remarks>
/// <para>
/// One class rather than an equipment and a host, because stream 14 does not have those. E5 marks
/// the attribute messages as travelling either way: a host reads a carrier's attributes from a
/// machine, and a machine reads a control job's attributes from the host, and neither is the
/// passive end. So this class asks and answers, and an entity that only asks is one built without
/// a provider.
/// </para>
/// <para>
/// The three namelists are the exception: E5 has them travel host to equipment, so an entity
/// answering S14F13 is playing the equipment. Nothing here enforces that - the stream is generic
/// and so is this - but it is worth knowing which way round the standard expects them.
/// </para>
/// <para>
/// <strong>What this package does not define.</strong> The objects. Stream 14 is a protocol over
/// an object model that other standards own - E87 names a carrier, E90 a substrate, E94 a control
/// job - so the five methods of <see cref="IObjectProvider"/> are where the objects live, and they
/// are the application's. That is not a shortcut: a library that shipped its own object types
/// would be shipping a guess at somebody else's standard.
/// </para>
/// <para>
/// <strong>A get cannot fail.</strong> S14F2 and S14F20 carry attributes and no acknowledgement
/// code - the item lists have none - so an object that could not be read is simply absent from the
/// answer, and a caller compares what came back against what it asked for. The two set messages do
/// carry <c>OBJACK</c>, and three of its five codes name which part of the request was wrong.
/// </para>
/// </remarks>
public sealed class ObjectServices
{
    private readonly HsmsConnection _connection;
    private readonly IObjectProvider? _provider;
    private readonly ILogger _logger;

    /// <summary>Initialises object services over a connection.</summary>
    /// <param name="connection">The link. This object does not run it; it only uses it.</param>
    /// <param name="provider">The objects this end has. <see langword="null"/> for an entity that only asks.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public ObjectServices(
        HsmsConnection connection,
        IObjectProvider? provider = null,
        ILogger<ObjectServices>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _provider = provider;
        _logger = logger ?? NullLogger<ObjectServices>.Instance;
    }

    /// <summary>Every primary this class can answer - the eleven in the implemented subset.</summary>
    /// <remarks>
    /// Declare these only on an entity that has a provider. An entity that only asks and declares
    /// them answers every <c>GetAttr</c> with an empty list, which is worse than the S9F5 it would
    /// otherwise have sent.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (14, 1),    // GetAttr Request
        (14, 3),    // SetAttr Request
        (14, 5),    // Create Object Request
        (14, 7),    // Delete Object Request
        (14, 9),    // Object Action Request
        (14, 13),   // Object Type Namelist Request
        (14, 15),   // Object Instance Namelist Request
        (14, 17),   // Object Attribute Namelist Request
        (14, 19),   // Multi-Object GetAttr Request
        (14, 21),   // Multi-Object SetAttr Request
        (14, 27),   // Object Multi-Action Request
    ];

    /// <summary>Gets a value indicating whether this end can answer as well as ask.</summary>
    public bool CanAnswer => _provider is not null;

    /// <summary>Gets the objects this end answers out of, or <see langword="null"/> for an end that only asks.</summary>
    /// <remarks>
    /// <para>
    /// Worth having for two reasons. An application that wants to read its own objects in process -
    /// to log them, to show them on a screen - should not have to resolve and compose the providers
    /// again to do it. And when several packages each contribute objects, this is the
    /// <see cref="CompositeObjectProvider"/> that resulted, which is the only place the composition
    /// is visible: resolving <see cref="IObjectProvider"/> from a container gives whichever was
    /// registered last, not all of them.
    /// </para>
    /// <para>
    /// Reading through it is reading the machine's own objects, so the ownership rules on
    /// <see cref="ObjectAttribute"/> apply: a value it hands back is fresh and yours to dispose.
    /// </para>
    /// </remarks>
    public IObjectProvider? Provider => _provider;

    /// <summary>Answers one of the primaries in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the reply is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (_provider is null)
        {
            _logger.LogWarning(
                "S{S}F{F} arrived but this end has no object provider, so there is nothing to answer it with.",
                transaction.S,
                transaction.F);

            return;
        }

        var reply = transaction.S == 14
            ? await BuildReplyAsync(transaction, _provider, cancellationToken).ConfigureAwait(false)
            : null;

        if (reply is null)
        {
            _logger.LogWarning(
                "S{S}F{F} reached the object services handler with nothing to answer it.",
                transaction.S,
                transaction.F);

            return;
        }

        using (reply)
        {
            if (transaction.ReplyExpected)
            {
                await transaction.ReplyAsync(reply, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // ---------------------------------------------------------------- namelists

    /// <summary>Asks which object types the other end has - S14F13, answered by S14F14.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The type names.</returns>
    /// <exception cref="SecsException">The reply was not a namelist.</exception>
    public async ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(14, 13, replyExpected: true) { Name = "ObjectTypeNamelistRequest" },
            cancellationToken).ConfigureAwait(false);

        if (!ObjectCodec.TryReadNameList(reply.Item, out var names))
        {
            throw new SecsException("S14F14 did not carry a list of object types.");
        }

        return names;
    }

    /// <summary>Asks which instances of a type exist - S14F15, answered by S14F16.</summary>
    /// <param name="objectType">The type.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The object specifications.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="objectType"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not a namelist.</exception>
    public async ValueTask<IReadOnlyList<string>> GetInstancesAsync(
        string objectType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectType);

        using var reply = await RequestAsync(
            new SecsMessage(14, 15, replyExpected: true)
            {
                Name = "ObjectInstanceNamelistRequest",
                Item = ObjectCodec.TextBody(objectType, "OBJTYPE"),
            },
            cancellationToken).ConfigureAwait(false);

        if (!ObjectCodec.TryReadNameList(reply.Item, out var names))
        {
            throw new SecsException("S14F16 did not carry a list of object instances.");
        }

        return names;
    }

    /// <summary>Asks which attributes an object has - S14F17, answered by S14F18.</summary>
    /// <param name="objectSpecification">The object, or an empty string to ask about the type.</param>
    /// <param name="objectType">The type.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The attribute names.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not a namelist.</exception>
    public async ValueTask<IReadOnlyList<string>> GetAttributeNamesAsync(
        string objectSpecification,
        string objectType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(objectType);

        using var reply = await RequestAsync(
            new SecsMessage(14, 17, replyExpected: true)
            {
                Name = "ObjectAttributeNamelistRequest",
                Item = ObjectCodec.AttributeNamesRequestBody(objectSpecification, objectType),
            },
            cancellationToken).ConfigureAwait(false);

        if (!ObjectCodec.TryReadNameList(reply.Item, out var names))
        {
            throw new SecsException("S14F18 did not carry a list of attribute names.");
        }

        return names;
    }

    // -------------------------------------------------------------------- gets

    /// <summary>Reads attributes of one object - S14F1, answered by S14F2.</summary>
    /// <param name="objectSpecification">The object.</param>
    /// <param name="objectType">Its type.</param>
    /// <param name="attributes">Which attributes, or an empty list for all of them.</param>
    /// <param name="read">Called with the attributes while the reply is alive. Copy anything kept past it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see langword="false"/> when the other end answered an empty list, which means it does not have that object.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not attribute data.</exception>
    /// <remarks>
    /// The callback is what keeps the item contract honest. An attribute value is a view into the
    /// reply, and the reply is disposed when this method returns - so handing the values back would
    /// be handing back memory that has gone. Everything a caller needs to keep, it copies inside
    /// the callback.
    /// </remarks>
    public async ValueTask<bool> GetAttributesAsync(
        string objectSpecification,
        string objectType,
        IReadOnlyList<string> attributes,
        Action<ObjectAttributeValues> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(objectType);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(read);

        var request = new ObjectAttributeRequest
        {
            Objects = [objectSpecification],
            ObjectType = objectType,
            Attributes = attributes,
        };

        using var reply = await RequestAsync(
            new SecsMessage(14, 1, replyExpected: true)
            {
                Name = "GetAttrRequest",
                Item = ObjectCodec.GetRequestBody(request),
            },
            cancellationToken).ConfigureAwait(false);

        if (!ObjectCodec.TryReadGetData(reply.Item, out var values))
        {
            throw new SecsException("S14F2 did not carry attribute data.");
        }

        if (values is null)
        {
            return false;
        }

        read(values);
        return true;
    }

    /// <summary>Reads attributes of several objects - S14F19, answered by S14F20.</summary>
    /// <param name="objectSpecifications">The objects.</param>
    /// <param name="attributes">Which attributes, or an empty list for all of them.</param>
    /// <param name="read">Called with each object's attributes while the reply is alive.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many objects came back. An object left out is one the other end does not have.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not attribute data.</exception>
    public async ValueTask<int> GetAttributesAsync(
        IReadOnlyList<string> objectSpecifications,
        IReadOnlyList<string> attributes,
        Action<ObjectAttributeValues> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectSpecifications);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(read);

        var request = new ObjectAttributeRequest { Objects = objectSpecifications, Attributes = attributes };

        using var reply = await RequestAsync(
            new SecsMessage(14, 19, replyExpected: true)
            {
                Name = "MultiObjectGetAttrRequest",
                Item = ObjectCodec.MultiGetRequestBody(request),
            },
            cancellationToken).ConfigureAwait(false);

        if (!ObjectCodec.TryReadMultiGetData(reply.Item, out var values))
        {
            throw new SecsException("S14F20 did not carry attribute data.");
        }

        foreach (var entry in values)
        {
            read(entry);
        }

        return values.Count;
    }

    // -------------------------------------------------------------------- sets

    /// <summary>Writes attributes of one object - S14F3, answered by S14F4.</summary>
    /// <param name="objectSpecification">The object.</param>
    /// <param name="objectType">Its type.</param>
    /// <param name="attributes">The attributes and their new values, which become this library's and are disposed with the message.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it took, and if not, which part was wrong.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public async ValueTask<ObjectAcknowledgement> SetAttributesAsync(
        string objectSpecification,
        string objectType,
        IReadOnlyList<ObjectAttribute> attributes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(objectType);
        ArgumentNullException.ThrowIfNull(attributes);

        var write = new ObjectAttributeWrite
        {
            Objects = [objectSpecification],
            ObjectType = objectType,
            Attributes = attributes,
        };

        using var reply = await RequestAsync(
            new SecsMessage(14, 3, replyExpected: true)
            {
                Name = "SetAttrRequest",
                Item = ObjectCodec.SetRequestBody(write),
            },
            cancellationToken).ConfigureAwait(false);

        var code = ObjectCodec.TryReadSetData(reply.Item)
            ?? throw new SecsException("S14F4 carried no OBJACK.");

        return ObjectCodec.Acknowledgement(code);
    }

    /// <summary>Writes the same attributes on several objects - S14F21, answered by S14F22.</summary>
    /// <param name="objectSpecifications">The objects.</param>
    /// <param name="attributes">The attributes and their new values, which become this library's.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One result per object the other end reported on.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not a list of acknowledgements.</exception>
    /// <remarks>
    /// One set of attribute values across every object named, which is what the message is for: the
    /// same recipe identifier onto four carriers in one transaction rather than four.
    /// </remarks>
    public async ValueTask<IReadOnlyList<ObjectSetResult>> SetAttributesAsync(
        IReadOnlyList<string> objectSpecifications,
        IReadOnlyList<ObjectAttribute> attributes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectSpecifications);
        ArgumentNullException.ThrowIfNull(attributes);

        var write = new ObjectAttributeWrite { Objects = objectSpecifications, Attributes = attributes };

        using var reply = await RequestAsync(
            new SecsMessage(14, 21, replyExpected: true)
            {
                Name = "MultiObjectSetAttrRequest",
                Item = ObjectCodec.MultiSetRequestBody(write),
            },
            cancellationToken).ConfigureAwait(false);

        if (!ObjectCodec.TryReadMultiSetData(reply.Item, out var results))
        {
            throw new SecsException("S14F22 did not carry a list of acknowledgements.");
        }

        return results;
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Tells one object to do something - S14F9, answered by S14F10.</summary>
    /// <param name="objectSpecification">Which object.</param>
    /// <param name="objectType">Its type.</param>
    /// <param name="command">What to do - <c>RCMD</c>.</param>
    /// <param name="parameters">What to do it with.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was done, and if not, which part was wrong.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// The message that makes stream 14 more than a property bag. An object with a state machine
    /// behind it - a control job, a carrier - is driven with this rather than by having its state
    /// assigned, which is why the providers in this stack refuse a write of a state attribute.
    /// </remarks>
    public async ValueTask<ObjectAcknowledgement> ActAsync(
        string objectSpecification,
        string objectType,
        string command,
        IReadOnlyList<ObjectParameter>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(objectType);
        ArgumentNullException.ThrowIfNull(command);

        var request = new ObjectActionRequest
        {
            Objects = [objectSpecification],
            ObjectType = objectType,
            Command = command,
            Parameters = parameters ?? [],
        };

        using var reply = await RequestAsync(
            new SecsMessage(14, 9, replyExpected: true)
            {
                Name = "ObjectActionRequest",
                Item = ObjectCodec.ActionBody(request),
            },
            cancellationToken).ConfigureAwait(false);

        if (!ObjectCodec.TryReadActionResults(reply.Item, out var results) || results.Count == 0)
        {
            throw new SecsException("S14F10 carried no OBJACK.");
        }

        return results[0].Acknowledgement;
    }

    /// <summary>Tells several objects to do the same thing - S14F27, answered by S14F28.</summary>
    /// <param name="objectSpecifications">Which objects.</param>
    /// <param name="command">What to do - <c>RCMD</c>.</param>
    /// <param name="parameters">What to do it with.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One result per object the other end reported on.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not a list of acknowledgements.</exception>
    public async ValueTask<IReadOnlyList<ObjectActionResult>> ActAsync(
        IReadOnlyList<string> objectSpecifications,
        string command,
        IReadOnlyList<ObjectParameter>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectSpecifications);
        ArgumentNullException.ThrowIfNull(command);

        var request = new ObjectActionRequest
        {
            Objects = objectSpecifications,
            Command = command,
            Parameters = parameters ?? [],
        };

        using var reply = await RequestAsync(
            new SecsMessage(14, 27, replyExpected: true)
            {
                Name = "ObjectMultiActionRequest",
                Item = ObjectCodec.MultiActionBody(request),
            },
            cancellationToken).ConfigureAwait(false);

        if (!ObjectCodec.TryReadActionResults(reply.Item, out var results))
        {
            throw new SecsException("S14F28 did not carry a list of acknowledgements.");
        }

        return results;
    }

    // ------------------------------------------------------- making and unmaking

    /// <summary>Asks for an object to exist - S14F5, answered by S14F6.</summary>
    /// <param name="objectType">What kind.</param>
    /// <param name="attributes">What it should start out as. These become this library's and are disposed with the message.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What was made, or why not.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// The object is named by the other end: S14F6 carries an <c>OBJID</c> back, and
    /// <see cref="ObjectCreateResult.ObjectId"/> is what to use afterwards. A caller that wants a
    /// particular name passes it as an attribute and leaves the machine to agree or refuse -
    /// which is also how E94 gives a control job its identifier.
    /// </remarks>
    public async ValueTask<ObjectCreateResult> CreateAsync(
        string objectType,
        IReadOnlyList<ObjectAttribute>? attributes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectType);

        var request = new ObjectCreateRequest { ObjectType = objectType, Attributes = attributes ?? [] };

        using var reply = await RequestAsync(
            new SecsMessage(14, 5, replyExpected: true)
            {
                Name = "CreateObjectRequest",
                Item = ObjectCodec.CreateBody(request),
            },
            cancellationToken).ConfigureAwait(false);

        return ObjectCodec.TryReadCreateResult(reply.Item)
            ?? throw new SecsException("S14F6 carried no OBJACK.");
    }

    /// <summary>Asks for an object to stop existing - S14F7, answered by S14F8.</summary>
    /// <param name="objectSpecification">Which one.</param>
    /// <param name="objectType">Its type, or an empty string to leave it unsaid.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it is gone.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public async ValueTask<ObjectAcknowledgement> DeleteAsync(
        string objectSpecification,
        string objectType = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(objectType);

        var request = new ObjectDeleteRequest
        {
            ObjectType = objectType,
            ObjectSpecification = objectSpecification,
        };

        using var reply = await RequestAsync(
            new SecsMessage(14, 7, replyExpected: true)
            {
                Name = "DeleteObjectRequest",
                Item = ObjectCodec.DeleteBody(request),
            },
            cancellationToken).ConfigureAwait(false);

        return ObjectCodec.TryReadDeleteResult(reply.Item)
            ?? throw new SecsException("S14F8 carried no OBJACK.");
    }

    // --------------------------------------------------------------- the pieces

    private async ValueTask<SecsMessage?> BuildReplyAsync(
        SecsTransaction transaction,
        IObjectProvider provider,
        CancellationToken cancellationToken)
    {
        switch (transaction.F)
        {
            case 1:
            {
                if (!ObjectCodec.TryReadGetRequest(transaction.Message.Item, out var request) || request is null)
                {
                    return Empty(2, "GetAttrData", "S14F1");
                }

                var values = await provider.GetAttributesAsync(request, cancellationToken).ConfigureAwait(false);

                // No OBJACK on S14F2, so an object this end does not have is an empty list. The
                // asking end reads that as "not here" rather than as a failure.
                return Reply(2, "GetAttrData", ObjectCodec.GetDataBody(values.Count > 0 ? values[0] : null));
            }

            case 3:
            {
                if (!ObjectCodec.TryReadSetRequest(transaction.Message.Item, out var write) || write is null)
                {
                    return Reply(4, "SetAttrData", ObjectCodec.SetDataBody([], ObjectAcknowledgement.Error));
                }

                var results = await provider.SetAttributesAsync(write, cancellationToken).ConfigureAwait(false);

                return Reply(
                    4,
                    "SetAttrData",
                    ObjectCodec.SetDataBody(
                        write.Attributes,
                        results.Count > 0 ? results[0].Acknowledgement : ObjectAcknowledgement.Error));
            }

            case 5:
            {
                if (!ObjectCodec.TryReadCreate(transaction.Message.Item, out var create) || create is null)
                {
                    return Reply(
                        6,
                        "CreateObjectAcknowledge",
                        ObjectCodec.CreateResultBody(new ObjectCreateResult
                        {
                            Acknowledgement = ObjectAcknowledgement.InvalidObjectType,
                        }));
                }

                var made = await provider.CreateAsync(create, cancellationToken).ConfigureAwait(false);

                return Reply(6, "CreateObjectAcknowledge", ObjectCodec.CreateResultBody(made));
            }

            case 7:
            {
                if (!ObjectCodec.TryReadDelete(transaction.Message.Item, out var delete) || delete is null)
                {
                    return Reply(
                        8,
                        "DeleteObjectAcknowledge",
                        ObjectCodec.DeleteResultBody(string.Empty, ObjectAcknowledgement.InvalidObjectInstance));
                }

                var gone = await provider.DeleteAsync(delete, cancellationToken).ConfigureAwait(false);

                return Reply(8, "DeleteObjectAcknowledge", ObjectCodec.DeleteResultBody(delete.ObjectSpecification, gone));
            }

            case 9:
            {
                if (!ObjectCodec.TryReadAction(transaction.Message.Item, out var action) || action is null)
                {
                    return Reply(10, "ObjectActionAcknowledge", ObjectCodec.ActionResultBody(
                    [
                        new ObjectActionResult
                        {
                            ObjectSpecification = string.Empty,
                            Acknowledgement = ObjectAcknowledgement.Error,
                        },
                    ]));
                }

                return Reply(10, "ObjectActionAcknowledge", await ActedAsync(provider, action, cancellationToken).ConfigureAwait(false));
            }

            case 27:
            {
                if (!ObjectCodec.TryReadMultiAction(transaction.Message.Item, out var action) || action is null)
                {
                    return Empty(28, "ObjectMultiActionAcknowledge", "S14F27");
                }

                return Reply(28, "ObjectMultiActionAcknowledge", await ActedAsync(provider, action, cancellationToken).ConfigureAwait(false));
            }

            case 13:
            {
                var types = await provider.GetObjectTypesAsync(cancellationToken).ConfigureAwait(false);

                return Reply(14, "ObjectTypeNamelistData", ObjectCodec.NameListBody(types ?? [], "OBJTYPE"));
            }

            case 15:
            {
                var objectType = ObjectCodec.TryReadText(transaction.Message.Item);

                if (objectType is null)
                {
                    return Empty(16, "ObjectInstanceNamelistData", "S14F15");
                }

                var instances = await provider.GetInstancesAsync(objectType, cancellationToken).ConfigureAwait(false);

                return Reply(16, "ObjectInstanceNamelistData", ObjectCodec.NameListBody(instances ?? [], "OBJSPEC"));
            }

            case 17:
            {
                if (!ObjectCodec.TryReadAttributeNamesRequest(
                        transaction.Message.Item,
                        out var specification,
                        out var type))
                {
                    return Empty(18, "ObjectAttributeNamelistData", "S14F17");
                }

                var names = await provider
                    .GetAttributeNamesAsync(specification, type, cancellationToken)
                    .ConfigureAwait(false);

                return Reply(18, "ObjectAttributeNamelistData", ObjectCodec.NameListBody(names ?? [], "ATTRID"));
            }

            case 19:
            {
                if (!ObjectCodec.TryReadMultiGetRequest(transaction.Message.Item, out var request) || request is null)
                {
                    return Empty(20, "MultiObjectGetAttrData", "S14F19");
                }

                var values = await provider.GetAttributesAsync(request, cancellationToken).ConfigureAwait(false);

                return Reply(20, "MultiObjectGetAttrData", ObjectCodec.MultiGetDataBody(values ?? []));
            }

            case 21:
            {
                if (!ObjectCodec.TryReadMultiSetRequest(transaction.Message.Item, out var write) || write is null)
                {
                    return Empty(22, "MultiObjectSetAttrData", "S14F21");
                }

                var results = await provider.SetAttributesAsync(write, cancellationToken).ConfigureAwait(false);

                return Reply(
                    22,
                    "MultiObjectSetAttrData",
                    ObjectCodec.MultiSetDataBody(results ?? [], write.Attributes));
            }

            default:
                return null;
        }
    }

    /// <summary>Asks the provider to act, and makes sure every object named gets an answer.</summary>
    /// <remarks>
    /// A provider answers only for the objects it has, so an object nobody owns would otherwise
    /// come back with no result at all - and a host reading a shorter list than it sent has to
    /// work out which one is missing. Filling the gaps with
    /// <see cref="ObjectAcknowledgement.InvalidObjectInstance"/> says it directly.
    /// </remarks>
    private static async ValueTask<SecsItem> ActedAsync(
        IObjectProvider provider,
        ObjectActionRequest request,
        CancellationToken cancellationToken)
    {
        var results = await provider.ActAsync(request, cancellationToken).ConfigureAwait(false) ?? [];

        List<ObjectActionResult> answered = [];

        foreach (var specification in request.Objects)
        {
            var found = false;

            foreach (var result in results)
            {
                if (string.Equals(result.ObjectSpecification, specification, StringComparison.Ordinal))
                {
                    answered.Add(result);
                    found = true;

                    break;
                }
            }

            if (!found)
            {
                answered.Add(new ObjectActionResult
                {
                    ObjectSpecification = specification,
                    Acknowledgement = ObjectAcknowledgement.InvalidObjectInstance,
                });
            }
        }

        return ObjectCodec.ActionResultBody(answered);
    }

    private async ValueTask<SecsMessage> RequestAsync(SecsMessage primary, CancellationToken cancellationToken)
    {
        // The primary is disposed here, and with it every attribute value the caller handed over.
        // That is the contract on ObjectAttribute: what you give this library, it owns.
        using (primary)
        {
            return await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);
        }
    }

    private static SecsMessage Reply(byte function, string name, SecsItem body)
        => new(14, function, replyExpected: false) { Name = name, Item = body };

    private SecsMessage Empty(byte function, string name, string primary)
    {
        _logger.LogWarning(
            "{Primary} arrived with a body this end could not read, and its reply has no code to say so; answering a zero-length list.",
            primary);

        return Reply(function, name, SecsItem.L());
    }
}
