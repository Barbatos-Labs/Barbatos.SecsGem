// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Barbatos.SecsGem.Serialization;

/// <summary>
/// Registers the hub of the Hub-and-Spoke serialization model: the codec options, the native
/// binary spoke, the format registry and the transcoder.
/// </summary>
/// <remarks>
/// <para>
/// Shared source, compiled into every assembly that offers an <c>AddSecsXxx()</c> method, rather
/// than shipped from one of them. The hub types themselves live in <c>Barbatos.SecsGem.Core</c>,
/// which has no dependency on <c>Microsoft.Extensions.DependencyInjection.Abstractions</c> and is
/// not allowed one: an item model and a binary codec are useful to a file converter, a Roslyn
/// analyzer and a WebAssembly tool, and not one of those owns a container. So the assembly that
/// already needs DI in order to offer its own spoke is the assembly that registers the hub.
/// </para>
/// <para>
/// Every spoke registers it, and every registration goes through <c>TryAdd</c>: the first call
/// wins and the rest are no-ops, so <c>AddSecsSml().AddSecsJson()</c> builds one hub and not two.
/// </para>
/// <para>
/// <see langword="internal"/> on purpose. The same method made public from five assemblies would
/// be five ambiguous candidates the moment an application references two spokes together.
/// </para>
/// </remarks>
internal static class SecsSerializationRegistration
{
    /// <summary>Adds the codec options, the binary spoke, the registry and the transcoder.</summary>
    /// <param name="services">The service collection. Validated by the caller.</param>
    internal static void AddHub(IServiceCollection services)
    {
        // A host that derives its codec options from its connection options registers them
        // before this runs, and TryAdd leaves that registration alone - which is why AddSecsGem
        // can register SecsGemOptions.CreateCodecOptions() first and have it survive.
        services.TryAddSingleton(SecsCodecOptions.Default);

        // The native binary format is always present: it is the hub written down, which is what
        // lets capture.secs2 -> capture.sml work with no special case in the transcoder.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatReader, Secs2BinaryFormatReader>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatWriter, Secs2BinaryFormatWriter>());

        services.TryAddSingleton<ISecsFormatRegistry>(provider => new SecsFormatRegistry(
            provider.GetServices<ISecsFormatReader>(),
            provider.GetServices<ISecsFormatWriter>()));

        services.TryAddSingleton<SecsTranscoder>();
    }
}
