// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;

namespace Barbatos.SecsGem.ItemTransfer.UnitTests;

/// <summary>
/// Whose item is whose at every hand-off in this package, driven over a wire.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An item handed to a <c>SecsMessage</c> becomes that message's</strong>, and a reply is
/// disposed once it has been sent - so a body built out of a store's own item leaves the store
/// holding a payload that has gone back to the pool. <c>ItemTransferCodec.AttributeBody</c> did
/// that, which is the defect the sweep after the recipe chapter was looking for: the same two E39
/// items, the same hand-off, the same mistake one package later.
/// </para>
/// <para>
/// <strong>This package had described the hazard and called it the caller's.</strong>
/// <c>ItemInMemoryStore.Add</c>'s remarks said an attribute past the pool threshold "would go back
/// to the pool after the first S21F3 and be lent out again on the second" and asked the caller not
/// to dispose it - a lifetime no caller could honour, because the reply that disposed it was the
/// library's. So a stated reason can be right about the bytes and wrong about whose job it is.
/// </para>
/// <para>
/// The other half of the recipe defect - a codec that <em>keeps</em> an item that arrived - cannot
/// happen here, and not because anything guards it: <strong>stream 21 has no row that sets an
/// attribute.</strong> S21F3 asks and no function answers with one, so <c>Add</c> is the only door
/// and nothing a peer sends ever reaches it. The one thing a peer does send whole is
/// <c>ITEMDATA</c>, and <c>ItemPayload.From</c> has copied it to bytes since the package was
/// written - there is no borrowing route for it to take, which is why there is no test here for it.
/// </para>
/// </remarks>
public sealed class ItemAttributeOwnershipTests
{
    // Over SecsCodecOptions.PooledPayloadThreshold, which is 1024. The format matters as much as
    // the size: DecodeArray is the only decode that borrows, so B, BOOLEAN and the numerics can
    // take this path and text cannot - an A[n] becomes a StringItem holding a string, which owns
    // nothing and survives any dispose. Every test of this pair set text, so none could fail.
    private const int Borrowed = 4096;

    [Fact(Timeout = 15_000)]
    public async Task A_value_larger_than_the_codecs_pool_threshold_survives_the_request_it_answered()
    {
        await using var pair = ItemPair.Start();

        var large = new byte[Borrowed];

        large.AsSpan().Fill((byte)'x');

        // Two of them, because ArrayPool keeps one buffer per bucket in the returning thread's own
        // slot and the rest where anybody can rent them - and the thread that disposes a reply is
        // never this one, so a single attribute can end up somewhere nothing else will rent it.
        // Controls say one is in fact enough here, 5 runs of 5, for the reason the comment over the
        // churn gives; two costs nothing and does not depend on that holding.
        using var first = Borrow(large);
        using var second = Borrow(large);

        pair.RightStore.Add(
            new Item { Id = "LOG-BIG", Type = "log", Length = 2 },
            attributes:
            [
                new ItemAttribute { Name = "Capture", Value = first },
                new ItemAttribute { Name = "Also", Value = second },
            ]);

        await pair.OpenAsync();

        // The first request is also what used to destroy what the store was holding, so it is both
        // the control and the hazard: the values read here are the host's own copies, taken inside
        // the callback while its reply is alive, and they are right either way.
        var before = await pair.Left.AttributesAsync(
            new ItemAttributeRequest { ItemId = "LOG-BIG" },
            Values,
            ItemPair.Token);

        // Then the pool is churned by hand, which this test does not strictly need and keeps
        // anyway. In the recipe pair it was load-bearing: a store that holds BYTES re-decodes the
        // same value into the same size class on the second request and writes it back unchanged,
        // so without a churn the control passed for the wrong reason. This store holds the ITEM, so
        // nothing restores it and the second exchange corrupts it unaided - with the copy reverted
        // and no churn at all the value comes back as somebody else's HSMS frame, 3 runs of 3. The
        // churn stays so the test does not rest on the library happening to rent that buffer next.
        Churn();

        var after = await pair.Left.AttributesAsync(
            new ItemAttributeRequest { ItemId = "LOG-BIG" },
            Values,
            ItemPair.Token);

        Assert.Equal(2, before.Length);
        Assert.All(before, value => Assert.Equal(large, value));

        // What the store still has, read two ways: over the wire, and out of the items the test
        // handed it. The second is the sharper of the two, because it does not depend on this
        // package being able to answer at all.
        Assert.Equal(2, after.Length);
        Assert.All(after, value => Assert.Equal(large, value));
        Assert.Equal(large, first.GetSpan<byte>().ToArray());
        Assert.Equal(large, second.GetSpan<byte>().ToArray());
    }

    // A value a caller holds as a decoded item, which is what an application forwarding an
    // attribute off another link or out of its own records has - SecsItem.B over an array of the
    // test's own would be unpooled, own nothing, and survive the defect untouched.
    private static SecsItem Borrow(byte[] values)
    {
        using var built = SecsItem.B(values);

        return SecsItem.Decode(built.ToByteArray());
    }

    // Copied inside the callback, which is where ItemAttribute.Value is alive: on this end every
    // value is a view into the reply, and the reply is disposed when AttributesAsync returns.
    private static byte[][] Values(IReadOnlyList<ItemAttribute> attributes)
        => [.. attributes.Select(attribute => attribute.Value.GetSpan<byte>().ToArray())];

    // Writes a known byte over every 4 KiB buffer the shared pool is holding, which is what a later
    // decode of a different payload would do to one that had been handed back. Nothing in the
    // library can do this on purpose, so the test does it.
    private static void Churn()
    {
        List<byte[]> rented = [];

        for (var i = 0; i < 64; i++)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(Borrowed);

            buffer.AsSpan().Fill((byte)'z');
            rented.Add(buffer);
        }

        foreach (var buffer in rented)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
