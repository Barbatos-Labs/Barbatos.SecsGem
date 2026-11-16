// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Objects;

/// <summary>
/// Several object providers behind one, because stream 14 has one provider per connection.
/// </summary>
/// <remarks>
/// <para>
/// A machine that does E87 and E90 has carriers, load ports, substrates and substrate locations,
/// and they come from two packages that know nothing about each other. Stream 14 asks one
/// provider, so something has to put them together - and leaving that to each application meant
/// every application writing this class, or worse, discovering that the second registration
/// silently did nothing.
/// </para>
/// <para>
/// <strong>How the answers combine.</strong> Object types are concatenated and de-duplicated, so
/// two providers claiming the same type name produce one. Instances and attribute values are
/// concatenated as they are: every provider in this stack already leaves out objects it does not
/// have, which is what makes concatenation correct rather than merely convenient - and that is the
/// contract <see cref="IObjectProvider.GetAttributesAsync"/> states.
/// </para>
/// <para>
/// A set goes to the providers in turn and stops at the first that does not answer
/// <see cref="ObjectAcknowledgement.InvalidObjectInstance"/> - that code being the one way a
/// provider says "not mine". A write nobody owns comes back as that same code, which is what a
/// single provider would have said.
/// </para>
/// </remarks>
public sealed class CompositeObjectProvider : IObjectProvider
{
    private readonly IObjectProvider[] _providers;

    /// <summary>Puts several providers behind one.</summary>
    /// <param name="providers">The providers, asked in order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="providers"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="providers"/> is empty, or contains a <see langword="null"/>.</exception>
    public CompositeObjectProvider(params IObjectProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        if (providers.Length == 0)
        {
            throw new ArgumentException("A composite of no providers answers nothing; pass at least one.", nameof(providers));
        }

        foreach (var provider in providers)
        {
            if (provider is null)
            {
                throw new ArgumentException("One of the providers is null.", nameof(providers));
            }
        }

        _providers = [.. providers];
    }

    /// <summary>Puts several providers behind one.</summary>
    /// <param name="providers">The providers, asked in order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="providers"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="providers"/> is empty, or contains a <see langword="null"/>.</exception>
    public CompositeObjectProvider(IEnumerable<IObjectProvider> providers)
        : this(providers is null ? throw new ArgumentNullException(nameof(providers)) : [.. providers])
    {
    }

    /// <summary>Gets the providers this one asks, in order.</summary>
    public IReadOnlyList<IObjectProvider> Providers => _providers;

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken cancellationToken)
    {
        List<string> types = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (var provider in _providers)
        {
            foreach (var type in await provider.GetObjectTypesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (seen.Add(type))
                {
                    types.Add(type);
                }
            }
        }

        return types;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<string>> GetInstancesAsync(string objectType, CancellationToken cancellationToken)
    {
        List<string> instances = [];

        foreach (var provider in _providers)
        {
            instances.AddRange(await provider.GetInstancesAsync(objectType, cancellationToken).ConfigureAwait(false));
        }

        return instances;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<string>> GetAttributeNamesAsync(
        string objectSpecification,
        string objectType,
        CancellationToken cancellationToken)
    {
        // The first provider that has the object answers, because a namelist is about one object
        // and concatenating two objects' attributes would advertise names the object does not have.
        foreach (var provider in _providers)
        {
            var held = await provider
                .GetAttributesAsync(
                    new ObjectAttributeRequest { Objects = [objectSpecification], ObjectType = objectType, Attributes = [] },
                    cancellationToken)
                .ConfigureAwait(false);

            if (held.Count > 0)
            {
                return await provider
                    .GetAttributeNamesAsync(objectSpecification, objectType, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Nobody has it. Asking the first provider anyway is how a host discovers what a type's
        // attributes are before any instance of it exists, which S14F17 allows.
        return await _providers[0]
            .GetAttributeNamesAsync(objectSpecification, objectType, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<ObjectAttributeValues>> GetAttributesAsync(
        ObjectAttributeRequest request,
        CancellationToken cancellationToken)
    {
        List<ObjectAttributeValues> values = [];

        foreach (var provider in _providers)
        {
            values.AddRange(await provider.GetAttributesAsync(request, cancellationToken).ConfigureAwait(false));
        }

        return values;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<ObjectSetResult>> SetAttributesAsync(
        ObjectAttributeWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        List<ObjectSetResult> results = [];

        foreach (var specification in write.Objects)
        {
            results.Add(await SetOneAsync(specification, write, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<ObjectActionResult>> ActAsync(
        ObjectActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ObjectActionResult> results = [];

        // Concatenated, because a provider answers only for the objects it has - the same contract
        // that makes a get concatenate. An object nobody owns gets its answer from the caller of
        // this, which fills the gaps.
        foreach (var provider in _providers)
        {
            results.AddRange(await provider.ActAsync(request, cancellationToken).ConfigureAwait(false) ?? []);
        }

        return results;
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectCreateResult> CreateAsync(
        ObjectCreateRequest request,
        CancellationToken cancellationToken)
    {
        ObjectCreateResult? last = null;

        // The first provider that makes objects of that type wins. InvalidObjectType is how a
        // provider says the type is not its, which is also the default for one that makes nothing.
        foreach (var provider in _providers)
        {
            last = await provider.CreateAsync(request, cancellationToken).ConfigureAwait(false);

            if (last.Acknowledgement is not ObjectAcknowledgement.InvalidObjectType)
            {
                return last;
            }
        }

        return last ?? new ObjectCreateResult { Acknowledgement = ObjectAcknowledgement.InvalidObjectType };
    }

    /// <inheritdoc/>
    public async ValueTask<ObjectAcknowledgement> DeleteAsync(
        ObjectDeleteRequest request,
        CancellationToken cancellationToken)
    {
        var last = ObjectAcknowledgement.InvalidObjectInstance;

        foreach (var provider in _providers)
        {
            last = await provider.DeleteAsync(request, cancellationToken).ConfigureAwait(false);

            // Anything but "not mine" is an answer, and for a delete there are two ways to say
            // that: the type is not this provider's, or the instance is not.
            if (last is not (ObjectAcknowledgement.InvalidObjectType or ObjectAcknowledgement.InvalidObjectInstance))
            {
                return last;
            }
        }

        return last;
    }

    private async ValueTask<ObjectSetResult> SetOneAsync(
        string specification,
        ObjectAttributeWrite write,
        CancellationToken cancellationToken)
    {
        var one = new ObjectAttributeWrite
        {
            Objects = [specification],
            ObjectType = write.ObjectType,
            Attributes = write.Attributes,
        };

        ObjectSetResult? last = null;

        foreach (var provider in _providers)
        {
            var results = await provider.SetAttributesAsync(one, cancellationToken).ConfigureAwait(false);

            if (results.Count == 0)
            {
                continue;
            }

            last = results[0];

            // Anything but "not mine" is an answer: the provider that owns the object has spoken,
            // whether it took the write or refused it.
            if (last.Acknowledgement is not ObjectAcknowledgement.InvalidObjectInstance)
            {
                return last;
            }
        }

        return last ?? new ObjectSetResult
        {
            ObjectSpecification = specification,
            Acknowledgement = ObjectAcknowledgement.InvalidObjectInstance,
        };
    }
}
