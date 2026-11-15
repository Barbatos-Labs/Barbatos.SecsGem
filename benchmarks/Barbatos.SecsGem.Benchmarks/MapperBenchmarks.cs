// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Mapping;
using BenchmarkDotNet.Attributes;

namespace Barbatos.SecsGem.Benchmarks;

/// <summary>Which resolver supplies the contract for a mapped type.</summary>
public enum Resolver
{
    /// <summary>The contracts the source generator emitted at compile time.</summary>
    Generated,

    /// <summary>The contracts reflection builds at run time.</summary>
    Reflection,
}

/// <summary>
/// The claim the source generator is in the repository to make good on.
/// </summary>
/// <remarks>
/// <para>
/// Correctness is the real argument for the generator - reflection reads metadata order, which the
/// CLI specification does not promise - and correctness does not show up in a table of
/// nanoseconds. What a table can show is the second benefit: direct delegates and a direct
/// <c>new</c> instead of <c>PropertyInfo.GetValue</c> and <c>Activator</c>.
/// </para>
/// <para>
/// Both arms map the same objects with the same converters and the same options. The only
/// difference is which <see cref="ISecsTypeInfoResolver"/> is asked for the shape.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class MapperBenchmarks
{
    private SecsSerializerOptions _options = null!;
    private EventReportSend _report = null!;
    private Person _person = null!;
    private SecsItem _reportItem = null!;

    /// <summary>Gets or sets the resolver under measurement.</summary>
    [Params(Resolver.Generated, Resolver.Reflection)]
    public Resolver Resolution { get; set; }

    /// <summary>Pins the resolver and builds the objects, after proving the generator saw them.</summary>
    /// <exception cref="InvalidOperationException">
    /// The generator produced no contract for a type these benchmarks map.
    /// </exception>
    [GlobalSetup]
    public void Setup()
    {
        // Without this, a generator that silently produced nothing would leave the Generated arm
        // falling through to reflection, and the table would report a dead heat as a result rather
        // than as the bug it is.
        foreach (var type in new[] { typeof(EventReportSend), typeof(EventReport), typeof(VariableValue), typeof(Person) })
        {
            if (!SecsTypeInfoResolver.Generated.Contains(type))
            {
                throw new InvalidOperationException(
                    $"The source generator produced no contract for {type.Name}. Both arms of this "
                    + "benchmark would be reflection, and the comparison would be meaningless. Check "
                    + "that Barbatos.SecsGem.SourceGenerators is referenced as an analyzer.");
            }
        }

        ISecsTypeInfoResolver resolver;

        if (Resolution == Resolver.Generated)
        {
            resolver = SecsTypeInfoResolver.Generated;
        }
        else
        {
            resolver = SecsTypeInfoResolver.Reflection;
        }

        _options = new SecsSerializerOptions { TypeInfoResolver = resolver };
        _report = SampleObjects.Report(reports: 3, valuesPerReport: 4);
        _person = SampleObjects.Person();
        _reportItem = SecsSerializer.Serialize(_report, _options);
    }

    /// <summary>Releases the pre-built item tree.</summary>
    [GlobalCleanup]
    public void Cleanup() => _reportItem.Dispose();

    /// <summary>Maps a nested S6F11 object to an item tree.</summary>
    /// <returns>The root item count, so nothing is optimised away.</returns>
    [Benchmark]
    public int Serialize()
    {
        using var item = SecsSerializer.Serialize(_report, _options);
        return item.Count;
    }

    /// <summary>Maps the item tree back to objects.</summary>
    /// <returns>The report count, so nothing is optimised away.</returns>
    [Benchmark]
    public int Deserialize()
    {
        var report = SecsSerializer.Deserialize<EventReportSend>(_reportItem, _options)!;
        return report.Reports.Count;
    }

    /// <summary>Maps an object to a whole SECS message, header and all.</summary>
    /// <returns>The stream number, so nothing is optimised away.</returns>
    [Benchmark]
    public int ToMessage()
    {
        using var message = SecsSerializer.ToMessage(_report, _options);
        return message.S;
    }

    /// <summary>Maps the attribute-free positional record.</summary>
    /// <returns>The root item count, so nothing is optimised away.</returns>
    [Benchmark]
    public int SerializeRecord()
    {
        using var item = SecsSerializer.Serialize(_person, _options);
        return item.Count;
    }
}
