// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Hsms.UnitTests;

/// <summary>
/// The routing table, including handlers that are built from a container.
/// </summary>
/// <remarks>
/// No link and no container here - a hand-written <see cref="IServiceProvider"/> is enough, because
/// what matters is only <em>when</em> the factory runs and what it is handed.
/// </remarks>
public class SecsMessageRouterBuilderTests
{
    private static ValueTask Nothing(SecsTransaction transaction, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    [Fact]
    public void A_direct_handler_is_in_the_table()
    {
        var router = new SecsMessageRouterBuilder()
            .OnPrimary(1, 1, Nothing)
            .Build();

        Assert.True(router.TryGetHandler(1, 1, out var handler));
        Assert.NotNull(handler);
        Assert.True(router.IsKnownStream(1));
        Assert.False(router.IsKnownStream(2));
    }

    [Fact]
    public void A_factory_handler_is_built_when_the_router_is_built()
    {
        var provider = new Provider();
        var built = 0;

        var builder = new SecsMessageRouterBuilder()
            .OnPrimary(1, 13, _ =>
            {
                built++;
                return Nothing;
            });

        Assert.Equal(0, built);

        var router = builder.Build(provider);

        Assert.Equal(1, built);
        Assert.True(router.TryGetHandler(1, 13, out _));
    }

    [Fact]
    public void The_factory_is_handed_the_provider_it_was_built_with()
    {
        var provider = new Provider();
        IServiceProvider? seen = null;

        new SecsMessageRouterBuilder()
            .OnPrimary(1, 13, p =>
            {
                seen = p;
                return Nothing;
            })
            .Build(provider);

        Assert.Same(provider, seen);
    }

    [Fact]
    public void Building_without_a_provider_refuses_rather_than_dropping_a_handler()
    {
        var builder = new SecsMessageRouterBuilder().OnPrimary(1, 13, _ => Nothing);

        // Silently building a router that is missing a handler would show up as the peer getting
        // S9F5 for a message this entity does in fact understand.
        var error = Assert.Throws<InvalidOperationException>(builder.Build);

        Assert.Contains("service provider", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_with_no_factories_still_builds_without_a_provider()
    {
        var router = new SecsMessageRouterBuilder()
            .OnPrimary(1, 1, Nothing)
            .Build();

        Assert.True(router.TryGetHandler(1, 1, out _));
    }

    [Fact]
    public void A_factory_cannot_claim_a_pair_a_direct_handler_already_has()
    {
        var builder = new SecsMessageRouterBuilder().OnPrimary(1, 1, Nothing);

        Assert.Throws<ArgumentException>(() => builder.OnPrimary(1, 1, _ => Nothing));
    }

    [Fact]
    public void A_direct_handler_cannot_claim_a_pair_a_factory_already_has()
    {
        var builder = new SecsMessageRouterBuilder().OnPrimary(1, 1, _ => Nothing);

        Assert.Throws<ArgumentException>(() => builder.OnPrimary(1, 1, Nothing));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(14)]
    public void A_factory_is_held_to_the_same_rule_about_odd_functions(byte function)
    {
        var builder = new SecsMessageRouterBuilder();

        // Only a primary is received and answered, and a primary has an odd function. The rule does
        // not get softer because the handler arrives by a different route.
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.OnPrimary(1, function, _ => Nothing));
    }

    [Fact]
    public void A_catch_all_registered_as_a_factory_is_reported_before_it_is_built()
    {
        var builder = new SecsMessageRouterBuilder();

        Assert.False(builder.HasCatchAll);

        builder.OnUnhandledPrimary(_ => Nothing);

        // CatchAll itself is still null, because it does not exist until a provider builds it -
        // which is why asking the question needs its own property.
        Assert.True(builder.HasCatchAll);
        Assert.Null(builder.CatchAll);

        var router = builder.Build(new Provider());
        Assert.NotNull(router.CatchAll);
    }

    [Fact]
    public void Only_one_catch_all_is_accepted_whichever_route_it_came_by()
    {
        var first = new SecsMessageRouterBuilder().OnUnhandledPrimary(Nothing);
        Assert.Throws<ArgumentException>(() => first.OnUnhandledPrimary(_ => Nothing));

        var second = new SecsMessageRouterBuilder().OnUnhandledPrimary(_ => Nothing);
        Assert.Throws<ArgumentException>(() => second.OnUnhandledPrimary(Nothing));
    }

    [Fact]
    public void A_factory_that_returns_null_is_an_error_rather_than_a_missing_handler()
    {
        var builder = new SecsMessageRouterBuilder().OnPrimary(1, 13, _ => null!);

        var error = Assert.Throws<InvalidOperationException>(() => builder.Build(new Provider()));

        Assert.Contains("S1F13", error.Message, StringComparison.Ordinal);
    }

    private sealed class Provider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
