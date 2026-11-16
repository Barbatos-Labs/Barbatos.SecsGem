// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.Objects;

/// <summary>
/// The objects this end has, and their attributes.
/// </summary>
/// <remarks>
/// <para>
/// This interface is the whole point of the package. Stream 14 is a protocol over an object model
/// it does not define: the objects belong to other standards - E87 names a carrier, E90 a
/// substrate, E94 a control job - and to the application that implements them. So the library
/// carries <c>GetAttr</c>, <c>SetAttr</c> and the three namelists, and the five methods here are
/// where the objects actually live.
/// </para>
/// <para>
/// Which means an application can answer stream 14 for objects of its own invention, and that is
/// not a misuse: E5 does not restrict the object types, and a machine that exposes its chambers
/// and its load ports as objects with attributes is using the stream for what it is.
/// </para>
/// <para>
/// Every method is asked inside a transaction the other end is waiting on, so none should take
/// long. Attribute values returned from here become the reply's and are disposed with it; values
/// handed to <see cref="SetAttributesAsync"/> are live views that end with the call. See
/// <see cref="ObjectAttribute"/> for both rules.
/// </para>
/// </remarks>
public interface IObjectProvider
{
    /// <summary>Which object types exist - S14F13.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The type names.</returns>
    ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken cancellationToken);

    /// <summary>Which instances of a type exist - S14F15.</summary>
    /// <param name="objectType">The type.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The object specifications. An empty list for a type that has no instances, or none this end knows.</returns>
    ValueTask<IReadOnlyList<string>> GetInstancesAsync(string objectType, CancellationToken cancellationToken);

    /// <summary>Which attributes an object has - S14F17.</summary>
    /// <param name="objectSpecification">The object, which may be empty when the question is about the type.</param>
    /// <param name="objectType">The type.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The attribute names.</returns>
    ValueTask<IReadOnlyList<string>> GetAttributeNamesAsync(
        string objectSpecification,
        string objectType,
        CancellationToken cancellationToken);

    /// <summary>Reads attributes - S14F1 and S14F19.</summary>
    /// <param name="request">Which objects, and which of their attributes.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One entry per object that could be read. An object left out of the answer is an object this end does not have.</returns>
    /// <remarks>
    /// Serves both the single-object and the multi-object message: the single one arrives as a
    /// request naming one object, which keeps a provider from having to implement the same read
    /// twice.
    /// </remarks>
    ValueTask<IReadOnlyList<ObjectAttributeValues>> GetAttributesAsync(
        ObjectAttributeRequest request,
        CancellationToken cancellationToken);

    /// <summary>Writes attributes - S14F3 and S14F21.</summary>
    /// <param name="write">Which objects, and what to set on them.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One result per object. An object with no result reads as <see cref="ObjectAcknowledgement.Error"/>.</returns>
    ValueTask<IReadOnlyList<ObjectSetResult>> SetAttributesAsync(
        ObjectAttributeWrite write,
        CancellationToken cancellationToken);
}

/// <summary>
/// An object provider that keeps objects and their attributes in memory.
/// </summary>
/// <remarks>
/// <para>
/// Enough for a simulator, a test, or an application whose objects are few and rebuilt at
/// start-up. Attribute values are held as encoded bytes rather than as items, because an item
/// belongs to the message it came in and this class outlives every message it sees.
/// </para>
/// <para>
/// An unknown object type is refused with <see cref="ObjectAcknowledgement.InvalidObjectType"/>,
/// an unknown instance with <see cref="ObjectAcknowledgement.InvalidObjectInstance"/>, and an
/// attribute the object does not already have with
/// <see cref="ObjectAcknowledgement.InvalidAttribute"/> - which makes this class strict by default:
/// a set cannot invent an attribute. <see cref="AllowNewAttributes"/> turns that off.
/// </para>
/// </remarks>
public sealed class ObjectInMemoryProvider : IObjectProvider
{
    private readonly ConcurrentDictionary<string, ObjectEntry> _objects = new(StringComparer.Ordinal);

    private sealed class ObjectEntry
    {
        public required string ObjectType { get; init; }

        public ConcurrentDictionary<string, ReadOnlyMemory<byte>> Attributes { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Gets or sets whether a set may add an attribute an object does not already have.</summary>
    /// <remarks><see langword="false"/> by default: an object model with attributes that appear on demand is not a model.</remarks>
    public bool AllowNewAttributes { get; set; }

    /// <summary>Gets how many objects are held.</summary>
    public int Count => _objects.Count;

    /// <summary>Adds an object, or replaces one.</summary>
    /// <param name="objectSpecification">What it is called.</param>
    /// <param name="objectType">What type it is.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public void Add(string objectSpecification, string objectType)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(objectType);

        _objects[objectSpecification] = new ObjectEntry { ObjectType = objectType };
    }

    /// <summary>Sets an attribute on an object held here.</summary>
    /// <param name="objectSpecification">The object.</param>
    /// <param name="attribute">The attribute.</param>
    /// <param name="value">Its value. Encoded on the way in, so the item is not kept.</param>
    /// <returns><see langword="false"/> when there is no such object.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public bool Set(string objectSpecification, string attribute, SecsItem value)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(value);

        if (!_objects.TryGetValue(objectSpecification, out var entry))
        {
            return false;
        }

        entry.Attributes[attribute] = ObjectAttributeStore.Encode(value);
        return true;
    }

    /// <summary>Forgets everything.</summary>
    public void Clear() => _objects.Clear();

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        HashSet<string> types = new(StringComparer.Ordinal);

        foreach (var entry in _objects)
        {
            types.Add(entry.Value.ObjectType);
        }

        return ValueTask.FromResult<IReadOnlyList<string>>([.. types]);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> GetInstancesAsync(string objectType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectType);
        cancellationToken.ThrowIfCancellationRequested();

        List<string> instances = [];

        foreach (var (specification, entry) in _objects)
        {
            if (string.Equals(entry.ObjectType, objectType, StringComparison.Ordinal))
            {
                instances.Add(specification);
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
        cancellationToken.ThrowIfCancellationRequested();

        // Named instance first; failing that, the first instance of the type, because the question
        // "what attributes does this type have" is answered by any instance of it.
        if (_objects.TryGetValue(objectSpecification, out var entry))
        {
            return ValueTask.FromResult<IReadOnlyList<string>>([.. entry.Attributes.Keys]);
        }

        foreach (var candidate in _objects)
        {
            if (string.Equals(candidate.Value.ObjectType, objectType, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<IReadOnlyList<string>>([.. candidate.Value.Attributes.Keys]);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<string>>([]);
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
            if (!_objects.TryGetValue(specification, out var entry))
            {
                // Left out rather than reported: S14F2 and S14F20 carry attributes and no
                // acknowledgement code, so an absence is the only thing they can say.
                continue;
            }

            List<ObjectAttribute> attributes = [];

            // An empty request is a request for everything, which is the only thing a zero-length
            // ATTRID list can mean.
            var wanted = request.Attributes.Count == 0 ? [.. entry.Attributes.Keys] : request.Attributes;

            foreach (var name in wanted)
            {
                if (entry.Attributes.TryGetValue(name, out var encoded))
                {
                    attributes.Add(new ObjectAttribute
                    {
                        Name = name,
                        Value = ObjectAttributeStore.Decode(encoded),
                    });
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
                Acknowledgement = Apply(specification, write),
            });
        }

        return ValueTask.FromResult<IReadOnlyList<ObjectSetResult>>(results);
    }

    private ObjectAcknowledgement Apply(string specification, ObjectAttributeWrite write)
    {
        if (!_objects.TryGetValue(specification, out var entry))
        {
            return ObjectAcknowledgement.InvalidObjectInstance;
        }

        if (write.ObjectType.Length > 0 && !string.Equals(entry.ObjectType, write.ObjectType, StringComparison.Ordinal))
        {
            return ObjectAcknowledgement.InvalidObjectType;
        }

        // Checked before anything is written, so a set that names one bad attribute changes
        // nothing at all. A half-applied set is worse than a refused one.
        if (!AllowNewAttributes)
        {
            foreach (var attribute in write.Attributes)
            {
                if (!entry.Attributes.ContainsKey(attribute.Name))
                {
                    return ObjectAcknowledgement.InvalidAttribute;
                }
            }
        }

        foreach (var attribute in write.Attributes)
        {
            entry.Attributes[attribute.Name] = ObjectAttributeStore.Encode(attribute.Value);
        }

        return ObjectAcknowledgement.Successful;
    }
}
