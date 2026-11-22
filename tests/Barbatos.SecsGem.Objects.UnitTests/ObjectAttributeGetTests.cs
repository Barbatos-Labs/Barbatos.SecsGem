// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;

namespace Barbatos.SecsGem.Objects.UnitTests;

/// <summary>
/// S1F19 and S1F20 - stream 1's one E39 row, and the only attribute get in the reference that can
/// say why it failed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it is here and not in <c>Barbatos.SecsGem.Gem</c>:</strong> every item it names -
/// <c>OBJTYPE</c>, <c>OBJID</c>, <c>ATTRID</c>, <c>ATTRDATA</c> - is shared with stream 14, so its
/// vocabulary is E39's. Gem must not depend on this package, and this package already reaches into
/// stream 2 for the enhanced services for the same reason.
/// </para>
/// <para>
/// <strong>Why it is not a duplicate of S14F1:</strong> S14F2 carries no acknowledgement at all.
/// An object the other end has not got comes back as an empty list, and a caller cannot tell that
/// from an object with no attributes or from a provider that declined. S1F20 carries an
/// <c>ERRCODE</c> and an <c>ERRTEXT</c> beside the data. The contrast is asserted below rather
/// than argued, because it is the whole reason the row was served.
/// </para>
/// </remarks>
public class ObjectAttributeGetTests
{
    private static ObjectInMemoryProvider Carriers()
    {
        var provider = new ObjectInMemoryProvider();

        provider.Add("CAR-001", "Carrier");
        provider.Set("CAR-001", "SlotCount", SecsItem.U1(25));
        provider.Set("CAR-001", "LotId", SecsItem.A("LOT-2026-1115"));

        return provider;
    }

    [Fact]
    public async Task An_object_that_exists_comes_back_with_its_attributes_and_no_reason()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        List<(string Name, string Value)> read = [];

        var error = await pair.Asker.GetAttributeAsync(
            "Carrier",
            "CAR-001",
            ["LotId"],
            values => read.AddRange(values.Attributes.Select(a => (a.Name, a.Value.GetString()))),
            ObjectPair.Token);

        // No error, which is what a zero code and no text mean. A caller checks for null rather
        // than for a code, because "it worked" is the absence of a reason.
        Assert.Null(error);
        Assert.Equal(("LotId", "LOT-2026-1115"), Assert.Single(read));
    }

    [Fact]
    public async Task An_object_nobody_has_comes_back_with_the_reason_S14F2_cannot_carry()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        var attributes = 0;

        var error = await pair.Asker.GetAttributeAsync(
            "Carrier",
            "CAR-999",
            ["LotId"],
            values => attributes += values.Attributes.Count,
            ObjectPair.Token);

        // The point of the whole row. Code 1 is this library's choice, because ERRCODE is untabled
        // and 0 is what an answer that worked carries - but the *text* is what a host acts on.
        Assert.NotNull(error);
        Assert.Equal(1u, error!.Code);
        Assert.Equal("no such object", error.Text);
        Assert.Equal("1 no such object", error.ToString());
        Assert.Equal(0, attributes);
    }

    [Fact]
    public async Task The_same_question_on_S14F1_has_no_reason_to_give()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        // The contrast, asserted. S14F2 has no acknowledgement item, so a missing object is an
        // empty list and false is all a caller gets: no code, no text, and no way to tell this
        // from an object that exists and has nothing in it.
        Assert.False(await pair.Asker.GetAttributesAsync(
            "CAR-999",
            "Carrier",
            ["LotId"],
            _ => { },
            ObjectPair.Token));
    }

    [Fact]
    public async Task A_request_that_is_not_an_object_and_an_identifier_is_refused_with_its_own_text()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        // Written by hand, because the public method always builds the three items. The reply is
        // still an S1F20 with a reason in it, which is the one thing this row can do that S14F2
        // cannot - so a body this end could not read says so rather than coming back empty.
        using var primary = new SecsMessage(1, 19, replyExpected: true)
        {
            Name = "GetAttribute",
            Item = SecsItem.A("Carrier").Named("OBJTYPE"),
        };

        using var reply = await pair.AskerLink
            .SendAsync(primary, ObjectPair.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), ObjectPair.Token);

        Assert.Equal(20, reply.F);
        Assert.Equal(3, reply.Item!.Count);
        Assert.Equal(0, reply.Item[0].Count);
        Assert.Equal(1u, reply.Item[1].FirstValueOrDefault<uint>());
        Assert.Contains("object type", reply.Item[2].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_for_every_attribute_is_an_empty_list_as_it_is_on_stream_fourteen()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        var names = new List<string>();

        var error = await pair.Asker.GetAttributeAsync(
            "Carrier",
            "CAR-001",
            [],
            values => names.AddRange(values.Attributes.Select(a => a.Name)),
            ObjectPair.Token);

        Assert.Null(error);
        Assert.Equal<string[]>(["LotId", "SlotCount"], [.. names.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void The_row_is_declared_here_and_nowhere_near_stream_fourteen_is_disturbed()
    {
        // Twelve, and exactly one of them outside stream 14. A package that registers object
        // services declares S1F19 with them, which is how a machine with carriers comes to answer
        // it without anybody wiring stream 1 by hand.
        Assert.Equal(12, ObjectServices.HandledMessages.Count);
        Assert.Equal(((byte)1, (byte)19), Assert.Single(ObjectServices.HandledMessages, m => m.Stream != 14));
        Assert.Equal(11, ObjectServices.HandledMessages.Count(m => m.Stream == 14));

        // And it does not collide with the enhanced half of stream 2, which this package also
        // serves: two reaches into other standards' streams, disjoint from each other.
        Assert.Empty(ObjectServices.HandledMessages.Intersect(EnhancedServicesEquipment.HandledMessages));
    }
}
