// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ControlPrograms.UnitTests;

/// <summary>
/// The in-memory store on its own, without a link.
/// </summary>
/// <remarks>
/// Three of this store's rules cannot be seen through a pair, because something between the store
/// and the assertion covers for them - the codec drops an empty identifier on the way back, and a
/// program the store has never heard of is absent whether or not the revision check is there. Each
/// of the three had a negative control that found nothing until the rule was tested where it
/// lives.
/// </remarks>
public sealed class ControlProgramStoreTests
{
    private static readonly byte[] Firmware = [0x7F, 0x45, 0x4C, 0x46];

    [Fact]
    public async Task The_boot_program_is_not_in_the_directory_the_store_builds()
    {
        var store = new ControlProgramInMemoryStore();

        await store.InquireAsync((uint)Firmware.Length, TestContext.Current.CancellationToken);
        await store.SaveAsync(Firmware, TestContext.Current.CancellationToken);
        store.Add("NAMED", Firmware);

        // Over a link this cannot be seen: the codec drops a zero-length CPID on the way back, so
        // a store that listed the boot program would look the same from a host. The rule is the
        // store's, so it is asserted on the store.
        Assert.Equal<string>(["NAMED"], await store.ListAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(store.Find(ControlProgramInMemoryStore.BootProgramId));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task The_directory_is_sorted_rather_than_however_the_table_holds_it()
    {
        var store = new ControlProgramInMemoryStore();

        // Inserted in reverse, and eight of them: a ConcurrentDictionary enumerates by bucket, and
        // string hashing is randomised per process, so eight keys coming back sorted by accident
        // is one chance in forty thousand. Three were not enough - a control that removed the sort
        // found nothing with three.
        string[] inserted = ["HOTEL", "GOLF", "FOXTROT", "ECHO", "DELTA", "CHARLIE", "BRAVO", "ALPHA"];

        foreach (var id in inserted)
        {
            store.Add(id, Firmware);
        }

        Assert.Equal<string>(
            ["ALPHA", "BRAVO", "CHARLIE", "DELTA", "ECHO", "FOXTROT", "GOLF", "HOTEL"],
            await store.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_program_that_exists_with_no_revision_has_none_to_report()
    {
        var store = new ControlProgramInMemoryStore();

        store.Add("NO-REV", Firmware);
        store.Add("HAS-REV", Firmware, "2.0.1");

        // The case a pair cannot reach either: a program the store never heard of is absent with or
        // without this rule, so the one that matters is a program that is there and has no revision
        // - which is what every program the host sends looks like, since S8F3 carries no SOFTREV.
        Assert.Null(await store.RevisionAsync("NO-REV", TestContext.Current.CancellationToken));
        Assert.Equal("2.0.1", await store.RevisionAsync("HAS-REV", TestContext.Current.CancellationToken));
        Assert.Null(await store.RevisionAsync("NOPE", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_second_program_beyond_the_capacity_has_no_room()
    {
        var store = new ControlProgramInMemoryStore { Capacity = 1 };

        store.Add("ALREADY", Firmware);

        // Capacity counts the named programs; the boot program's slot is not one of them, which is
        // why the store's own check adds one to it.
        Assert.Equal(
            ControlProgramGrant.Accepted,
            await store.InquireAsync(4, TestContext.Current.CancellationToken));

        store.Add("AND-ANOTHER", Firmware);

        Assert.Equal(
            ControlProgramGrant.NoSpace,
            await store.InquireAsync(4, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_store_with_no_length_limit_grants_anything()
    {
        var store = new ControlProgramInMemoryStore { MaxLength = 0 };

        Assert.Equal(
            ControlProgramGrant.Accepted,
            await store.InquireAsync(uint.MaxValue, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_null_identifier_is_refused_rather_than_stored()
    {
        var store = new ControlProgramInMemoryStore();

        Assert.Throws<ArgumentNullException>(() => store.Add(null!, Firmware));
    }
}
