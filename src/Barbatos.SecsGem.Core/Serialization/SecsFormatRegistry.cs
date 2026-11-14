// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Serialization;

/// <summary>
/// Looks a format's reader and writer up by identifier or by file extension.
/// </summary>
/// <remarks>
/// The hub of the Hub-and-Spoke design in one sentence: the registry knows the formats, the
/// transcoder knows only the registry, and this assembly knows neither SML nor JSON nor any
/// other spoke. Adding a format means registering a reader and a writer - never editing
/// anything here.
/// </remarks>
public interface ISecsFormatRegistry
{
    /// <summary>Gets every registered format.</summary>
    IReadOnlyCollection<SecsFormatDescriptor> Formats { get; }

    /// <summary>Finds the reader for a format.</summary>
    /// <param name="formatId">The format identifier, case-insensitive.</param>
    /// <param name="reader">The reader, when one is registered.</param>
    /// <returns><see langword="true"/> when a reader was found.</returns>
    bool TryGetReader(string formatId, [NotNullWhen(true)] out ISecsFormatReader? reader);

    /// <summary>Finds the writer for a format.</summary>
    /// <param name="formatId">The format identifier, case-insensitive.</param>
    /// <param name="writer">The writer, when one is registered.</param>
    /// <returns><see langword="true"/> when a writer was found.</returns>
    bool TryGetWriter(string formatId, [NotNullWhen(true)] out ISecsFormatWriter? writer);

    /// <summary>Resolves a format from a file name, a path or a bare extension.</summary>
    /// <param name="pathOrExtension">A path such as <c>recipes/common.sml</c>, or just <c>.sml</c>.</param>
    /// <param name="formatId">The identifier of the format that claims that extension.</param>
    /// <returns><see langword="true"/> when a format claims the extension.</returns>
    bool TryResolveFormatId(string pathOrExtension, [NotNullWhen(true)] out string? formatId);
}

/// <summary>
/// The default <see cref="ISecsFormatRegistry"/>.
/// </summary>
/// <remarks>
/// In a dependency-injection container this is constructed from every registered
/// <see cref="ISecsFormatReader"/> and <see cref="ISecsFormatWriter"/>, so
/// <c>AddSecsSml().AddSecsJson()</c> is all a format needs to become reachable. Outside a
/// container, use <see cref="Register(ISecsFormatReader)"/> and
/// <see cref="Register(ISecsFormatWriter)"/>.
/// </remarks>
public sealed class SecsFormatRegistry : ISecsFormatRegistry
{
    private readonly Dictionary<string, ISecsFormatReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ISecsFormatWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SecsFormatDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initialises an empty registry.</summary>
    public SecsFormatRegistry()
    {
    }

    /// <summary>Initialises a registry from every reader and writer the container knows about.</summary>
    /// <param name="readers">The registered readers.</param>
    /// <param name="writers">The registered writers.</param>
    public SecsFormatRegistry(IEnumerable<ISecsFormatReader> readers, IEnumerable<ISecsFormatWriter> writers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(writers);

        foreach (var reader in readers)
        {
            Register(reader);
        }

        foreach (var writer in writers)
        {
            Register(writer);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<SecsFormatDescriptor> Formats => _descriptors.Values;

    /// <summary>Registers a reader, replacing any reader already registered for its format.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>This registry, for chaining.</returns>
    public SecsFormatRegistry Register(ISecsFormatReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _readers[reader.Descriptor.Id] = reader;
        Describe(reader.Descriptor);
        return this;
    }

    /// <summary>Registers a writer, replacing any writer already registered for its format.</summary>
    /// <param name="writer">The writer.</param>
    /// <returns>This registry, for chaining.</returns>
    public SecsFormatRegistry Register(ISecsFormatWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writers[writer.Descriptor.Id] = writer;
        Describe(writer.Descriptor);
        return this;
    }

    /// <inheritdoc/>
    public bool TryGetReader(string formatId, [NotNullWhen(true)] out ISecsFormatReader? reader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatId);
        return _readers.TryGetValue(formatId, out reader);
    }

    /// <inheritdoc/>
    public bool TryGetWriter(string formatId, [NotNullWhen(true)] out ISecsFormatWriter? writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatId);
        return _writers.TryGetValue(formatId, out writer);
    }

    /// <inheritdoc/>
    public bool TryResolveFormatId(string pathOrExtension, [NotNullWhen(true)] out string? formatId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrExtension);

        // Accept a path, a bare file name, ".sml" or even "sml".
        var extension = Path.GetExtension(pathOrExtension);
        if (string.IsNullOrEmpty(extension))
        {
            extension = pathOrExtension.StartsWith('.') ? pathOrExtension : "." + pathOrExtension;
        }

        return _extensions.TryGetValue(extension, out formatId);
    }

    private void Describe(SecsFormatDescriptor descriptor)
    {
        _descriptors[descriptor.Id] = descriptor;
        foreach (var extension in descriptor.FileExtensions)
        {
            // First registration wins, so an application can add a format that claims ".txt"
            // without stealing it from one registered earlier.
            _extensions.TryAdd(extension, descriptor.Id);
        }
    }
}
