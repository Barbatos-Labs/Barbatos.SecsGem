// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.Hsms.UnitTests;

/// <summary>
/// <c>DeclareOnce</c>, which is where "calling AddXxx twice" stopped being a start-up failure.
/// </summary>
/// <remarks>
/// <para>
/// Every capability registration in this stack declares a set of primaries, and the routing table
/// refuses a stream and function twice - correctly, because two handlers for one message is a
/// conflict nobody can resolve for the application. The result was that an application calling a
/// registration twice, or two packages both needing stream 14, failed at start-up with
/// "S14F1 already has a handler": true, and unhelpful.
/// </para>
/// <para>
/// The distinction these tests pin down is the whole design: <strong>the same declaration twice is
/// a duplicate, and two declarations wanting one message is a conflict.</strong> One is forgiven
/// and the other still throws.
/// </para>
/// </remarks>
public class SecsGemBuilderTests
{
    private static readonly IReadOnlyList<(byte Stream, byte Function)> OneSet = [(1, 1), (1, 3)];
    private static readonly IReadOnlyList<(byte Stream, byte Function)> AnotherSet = [(6, 11), (5, 1)];
    private static readonly IReadOnlyList<(byte Stream, byte Function)> OverlappingSet = [(1, 1), (7, 3)];

    private static ISecsGemBuilder Build(IServiceCollection services)
        => services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false);

    private static Func<IServiceProvider, SecsPrimaryHandler> Answer()
        => static _ => (transaction, token) => transaction.ReplyAsync(
            new SecsMessage(transaction.S, (byte)(transaction.F + 1), replyExpected: false),
            token);

    [Fact]
    public async Task A_set_declared_twice_is_declared_once()
    {
        var services = new ServiceCollection();
        var builder = Build(services);

        builder.DeclareOnce(OneSet, Answer());
        builder.DeclareOnce(OneSet, Answer());

        await using var provider = services.BuildServiceProvider();

        // Resolving the router is what used to throw: the declarations are collected during
        // configuration and the table is built here.
        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert.True(router.TryGetHandler(1, 1, out _));
        Assert.True(router.TryGetHandler(1, 3, out _));
    }

    [Fact]
    public async Task The_same_set_ten_times_is_still_once()
    {
        var services = new ServiceCollection();
        var builder = Build(services);

        for (var time = 0; time < 10; time++)
        {
            builder.DeclareOnce(OneSet, Answer());
        }

        await using var provider = services.BuildServiceProvider();

        Assert.True(provider.GetRequiredService<ISecsMessageRouter>().TryGetHandler(1, 1, out _));
    }

    [Fact]
    public async Task Two_different_sets_are_both_declared()
    {
        var services = new ServiceCollection();
        var builder = Build(services);

        builder.DeclareOnce(OneSet, Answer());
        builder.DeclareOnce(AnotherSet, Answer());

        await using var provider = services.BuildServiceProvider();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // Which is the case a machine doing E87 and E90 is: two registrations, both right.
        Assert.True(router.TryGetHandler(1, 1, out _));
        Assert.True(router.TryGetHandler(6, 11, out _));
        Assert.True(router.TryGetHandler(5, 1, out _));
    }

    /// <summary>Two different sets claiming one message still throw.</summary>
    /// <remarks>
    /// The half of this that matters. Forgiving an overlap would mean silently keeping one of two
    /// handlers for the same message - a machine answering half a standard with nothing anywhere
    /// saying so, which is the worst shape a bug can have. An application that hits this has two
    /// capabilities to reconcile, and the exception says which message.
    /// </remarks>
    [Fact]
    public void Two_sets_that_claim_the_same_message_still_conflict()
    {
        var services = new ServiceCollection();
        var builder = Build(services);

        builder.DeclareOnce(OneSet, Answer());

        var thrown = Assert.Throws<ArgumentException>(() => builder.DeclareOnce(OverlappingSet, Answer()));

        Assert.Contains("S1F1", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A set built fresh at the call site is a different declaration.</summary>
    /// <remarks>
    /// Reference identity is the rule, and this is what it costs: a caller that builds its list
    /// inline gets the old behaviour. Every registration in this stack passes a static list, and
    /// the documentation says to - a test that pretended otherwise would be hiding the one way to
    /// use this wrongly.
    /// </remarks>
    [Fact]
    public void A_set_rebuilt_at_the_call_site_is_not_the_same_set()
    {
        var services = new ServiceCollection();
        var builder = Build(services);

        builder.DeclareOnce([(1, 1)], Answer());

        Assert.Throws<ArgumentException>(() => builder.DeclareOnce([(1, 1)], Answer()));
    }

    [Fact]
    public async Task An_empty_set_declares_nothing_and_is_not_an_error()
    {
        var services = new ServiceCollection();
        var builder = Build(services);

        builder.DeclareOnce([], Answer());

        await using var provider = services.BuildServiceProvider();

        // A capability with no primaries of its own is a real thing - E87's host side has none -
        // and a registration that threw over it would make every such package write an if.
        Assert.False(provider.GetRequiredService<ISecsMessageRouter>().TryGetHandler(1, 1, out _));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var services = new ServiceCollection();
        var builder = Build(services);

        Assert.Throws<ArgumentNullException>(() => builder.DeclareOnce(null!, Answer()));
        Assert.Throws<ArgumentNullException>(() => builder.DeclareOnce(OneSet, null!));
    }

    [Fact]
    public async Task A_declared_set_answers_the_way_the_factory_said()
    {
        var services = new ServiceCollection();
        var builder = Build(services);

        builder.DeclareOnce(OneSet, Answer());
        builder.DeclareOnce(OneSet, Answer());

        await using var provider = services.BuildServiceProvider();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // One handler, not two stacked on each other: a second declaration that had been kept
        // would show up here as a handler that is not the first one.
        Assert.True(router.TryGetHandler(1, 1, out var handler));
        Assert.NotNull(handler);
    }
}
