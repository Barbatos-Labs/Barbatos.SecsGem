// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ControlPrograms.UnitTests;

/// <summary>
/// Stream 8 end to end: a real host and a real equipment over one link.
/// </summary>
public sealed class ControlProgramTests
{
    private static readonly byte[] Firmware = [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00];

    [Fact]
    public async Task An_inquire_is_granted_and_names_nothing()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        var grant = await pair.Host.InquireAsync((uint)Firmware.Length, ControlProgramPair.Token);

        // S8F1's whole body is a LENGTH. The boot program is anonymous because a machine has one
        // of it, and the named programs are the ones a host requests rather than sends.
        Assert.Equal(ControlProgramGrant.Accepted, grant);
        Assert.Equal((uint)Firmware.Length, pair.Memory.GrantedLength);
    }

    [Fact]
    public async Task An_inquire_too_large_for_the_store_has_no_space()
    {
        var store = new ControlProgramInMemoryStore { MaxLength = 4 };

        await using var pair = ControlProgramPair.Start(store);
        await pair.OpenAsync();

        Assert.Equal(
            ControlProgramGrant.NoSpace,
            await pair.Host.InquireAsync(1_000_000, ControlProgramPair.Token));

        Assert.Null(store.GrantedLength);
    }

    [Fact]
    public async Task A_program_crosses_unchanged()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        await pair.Host.InquireAsync((uint)Firmware.Length, ControlProgramPair.Token);

        var acknowledgement = await pair.Host.SendAsync(Firmware, ControlProgramPair.Token);

        Assert.Equal(ControlProgramAcknowledgement.Accepted, acknowledgement);

        // Plain bytes, not an encoded item: CPBODY sits where PPBODY sits in stream 7's design and
        // PPBODY is a B. The bytes that went in are the bytes that are held.
        var held = pair.Memory.Find(ControlProgramInMemoryStore.BootProgramId);

        Assert.NotNull(held);
        Assert.Equal(Firmware, held.Body.ToArray());
        Assert.False(held.IsEmpty);
    }

    [Fact]
    public async Task A_send_nobody_granted_is_refused()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        // The in-memory store's rule: E5 says inquire then send, and ACKC8 has one refusal to say
        // "you did not" with - it has no table, so it cannot say which of the two happened.
        Assert.Equal(
            ControlProgramAcknowledgement.Denied,
            await pair.Host.SendAsync(Firmware, ControlProgramPair.Token));

        Assert.Null(pair.Memory.Find(ControlProgramInMemoryStore.BootProgramId));
    }

    [Fact]
    public async Task A_program_that_is_not_the_length_the_inquire_declared_is_refused()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        await pair.Host.InquireAsync(1_000, ControlProgramPair.Token);

        // The check stream 13 could not make. There LENGTH had nothing to measure against, because
        // DSDATA has no format; here CPBODY is a B, so the bytes have a count and the two numbers
        // can disagree.
        Assert.Equal(
            ControlProgramAcknowledgement.Denied,
            await pair.Host.SendAsync(Firmware, ControlProgramPair.Token));

        Assert.Null(pair.Memory.Find(ControlProgramInMemoryStore.BootProgramId));
        Assert.Null(pair.Memory.GrantedLength);
    }

    [Fact]
    public async Task The_two_steps_can_be_run_as_one()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        var (grant, acknowledgement) = await pair.Host.SendProgramAsync(Firmware, ControlProgramPair.Token);

        Assert.Equal(ControlProgramGrant.Accepted, grant);
        Assert.Equal(ControlProgramAcknowledgement.Accepted, acknowledgement);
        Assert.Equal(Firmware, pair.Memory.Find(ControlProgramInMemoryStore.BootProgramId)!.Body.ToArray());
    }

    [Fact]
    public async Task A_refused_inquire_sends_nothing_after_it()
    {
        var store = new ControlProgramInMemoryStore { MaxLength = 4 };

        await using var pair = ControlProgramPair.Start(store);
        await pair.OpenAsync();

        var (grant, acknowledgement) = await pair.Host.SendProgramAsync(Firmware, ControlProgramPair.Token);

        Assert.Equal(ControlProgramGrant.NoSpace, grant);

        // Null rather than Denied: there was no S8F3, so there is no ACKC8 to report. The
        // distinction matters because the two mean different things to whoever is retrying.
        Assert.Null(acknowledgement);
        Assert.DoesNotContain((byte)3, pair.Asked.Select(asked => asked.Function));
    }

    [Fact]
    public async Task A_named_program_comes_back()
    {
        var store = new ControlProgramInMemoryStore();
        store.Add("BOOT-A", Firmware, "1.4.2");

        await using var pair = ControlProgramPair.Start(store);
        await pair.OpenAsync();

        var program = await pair.Host.RequestAsync("BOOT-A", ControlProgramPair.Token);

        Assert.NotNull(program);
        Assert.Equal("BOOT-A", program.Id);
        Assert.Equal(Firmware, program.Body.ToArray());
    }

    [Fact]
    public async Task A_named_program_that_is_not_there_comes_back_empty()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        // S8F6 has no acknowledgement code at all, so "not there" arrives as an empty CPBODY in an
        // otherwise ordinary reply.
        Assert.Null(await pair.Host.RequestAsync("NOPE", ControlProgramPair.Token));
    }

    [Fact]
    public async Task The_directory_lists_the_named_programs_in_order()
    {
        var store = new ControlProgramInMemoryStore();
        store.Add("ZETA", Firmware);
        store.Add("ALPHA", Firmware);
        store.Add("MIKE", Firmware);

        await using var pair = ControlProgramPair.Start(store);
        await pair.OpenAsync();

        Assert.Equal<string>(
            ["ALPHA", "MIKE", "ZETA"],
            await pair.Host.ListAsync(ControlProgramPair.Token));
    }

    [Fact]
    public async Task The_boot_program_is_not_in_the_directory()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        await pair.Host.SendProgramAsync(Firmware, ControlProgramPair.Token);

        // It is held - under the empty identifier, which is this store's choice - but S8F12 lists
        // what a host can ask for by name, and a nameless program is not one of those.
        Assert.NotNull(pair.Memory.Find(ControlProgramInMemoryStore.BootProgramId));
        Assert.Empty(await pair.Host.ListAsync(ControlProgramPair.Token));
    }

    [Fact]
    public async Task The_directory_of_a_machine_with_nothing_is_empty()
    {
        await using var pair = ControlProgramPair.Start();
        await pair.OpenAsync();

        Assert.Empty(await pair.Host.ListAsync(ControlProgramPair.Token));
    }

    [Fact]
    public async Task An_abort_forgets_the_grant_and_keeps_what_is_stored()
    {
        var store = new ControlProgramInMemoryStore();
        store.Add("BOOT-A", Firmware, "1.4.2");

        await using var pair = ControlProgramPair.Start(store);
        await pair.OpenAsync();

        await pair.Host.InquireAsync((uint)Firmware.Length, ControlProgramPair.Token);

        Assert.Equal(
            ControlProgramAcknowledgement.Accepted,
            await pair.Host.AbortAsync("BOOT-A", ControlProgramPair.Token));

        // Aborting is not deleting: stream 8 has no message that removes a program, so one that
        // threw one away would be inventing a message.
        Assert.NotNull(pair.Memory.Find("BOOT-A"));
        Assert.Null(store.GrantedLength);

        // And a second abort has nothing to abort.
        Assert.Equal(
            ControlProgramAcknowledgement.Denied,
            await pair.Host.AbortAsync("BOOT-A", ControlProgramPair.Token));
    }

    [Fact]
    public async Task A_revision_comes_back_and_an_unknown_program_has_none()
    {
        var store = new ControlProgramInMemoryStore();
        store.Add("BOOT-A", Firmware, "1.4.2");

        await using var pair = ControlProgramPair.Start(store);
        await pair.OpenAsync();

        Assert.Equal("1.4.2", await pair.Host.RevisionAsync("BOOT-A", ControlProgramPair.Token));

        // The one transaction stream 8 has that stream 7 does not, and the cheap way to find out
        // whether a program is there before asking for all of it.
        Assert.Null(await pair.Host.RevisionAsync("NOPE", ControlProgramPair.Token));
    }

    [Fact]
    public async Task A_long_identifier_is_cut_to_what_the_dictionary_allows()
    {
        var store = new ControlProgramInMemoryStore();
        var long120 = new string('x', 120);

        store.Add(long120, Firmware, "9.9");

        await using var pair = ControlProgramPair.Start(store);
        await pair.OpenAsync();

        // PPID is an A[120], which is the length this package writes a CPID to - so an identifier
        // longer than that is cut rather than refused, and the cut one is what the machine matches.
        Assert.Equal("9.9", await pair.Host.RevisionAsync(long120 + "and more", ControlProgramPair.Token));
    }

    [Fact]
    public async Task The_host_asks_every_primary_the_equipment_declares()
    {
        var store = new ControlProgramInMemoryStore();
        store.Add("BOOT-A", Firmware, "1.4.2");

        await using var pair = ControlProgramPair.Start(store);
        await pair.OpenAsync();

        await pair.Host.InquireAsync((uint)Firmware.Length, ControlProgramPair.Token);
        await pair.Host.SendAsync(Firmware, ControlProgramPair.Token);
        await pair.Host.RequestAsync("BOOT-A", ControlProgramPair.Token);
        await pair.Host.ListAsync(ControlProgramPair.Token);
        await pair.Host.AbortAsync("BOOT-A", ControlProgramPair.Token);
        await pair.Host.RevisionAsync("BOOT-A", ControlProgramPair.Token);

        // "Every" asserted rather than claimed: the set the equipment declares and the set the host
        // managed to send are compared, not counted.
        Assert.Equal(
            [.. ControlProgramEquipment.HandledMessages.Order()],
            pair.Asked.Distinct().Order());
    }

    [Fact]
    public void The_equipment_declares_six_and_the_host_declares_none()
    {
        Assert.Equal<(byte, byte)>(
            [(8, 1), (8, 3), (8, 5), (8, 11), (8, 13), (8, 15)],
            ControlProgramEquipment.HandledMessages);

        // Every primary in stream 8 is the host's, so nothing in it ever arrives at a host unasked.
        // Barbatos.SecsGem.DataSets is the other end in this repository with an empty list, those
        // two streams being the one-way ones.
        Assert.Empty(ControlProgramHost.HandledMessages);

        // And the blocked pairs are not claimed: every block-transfer item in the reference is
        // undefined.
        Assert.DoesNotContain((byte)7, ControlProgramEquipment.HandledMessages.Select(m => m.Function));
        Assert.DoesNotContain((byte)9, ControlProgramEquipment.HandledMessages.Select(m => m.Function));
    }

    [Fact]
    public void The_grant_agrees_with_the_one_the_material_package_quotes()
    {
        // GRANT is one dictionary entry and two packages here serve it: Barbatos.SecsGem.Material
        // for S3F16 and this one for S8F2. One item cannot mean two things in one repository, so
        // the names and the numbers are checked rather than eyeballed.
        Assert.Equal<string[]>(
            ["Accepted", "Busy", "NoSpace", "DuplicateOrInvalid", "Other"],
            Enum.GetNames<ControlProgramGrant>());

        Assert.Equal(0, (byte)ControlProgramGrant.Accepted);
        Assert.Equal(1, (byte)ControlProgramGrant.Busy);
        Assert.Equal(2, (byte)ControlProgramGrant.NoSpace);
        Assert.Equal(3, (byte)ControlProgramGrant.DuplicateOrInvalid);
        Assert.Equal(4, (byte)ControlProgramGrant.Other);
    }

    [Fact]
    public void The_acknowledgement_has_two_members_and_no_other()
    {
        // ACKC8 has no dictionary entry and no table. Zero is accepted because all eight defined
        // ACKCn items say so, and nothing names anything above it - so a third member would be
        // inventing a distinction between kinds of silence.
        Assert.Equal<string[]>(["Accepted", "Denied"], Enum.GetNames<ControlProgramAcknowledgement>());
        Assert.Equal(0, (byte)ControlProgramAcknowledgement.Accepted);
        Assert.Equal(1, (byte)ControlProgramAcknowledgement.Denied);
    }

    [Fact]
    public void A_program_describes_itself_for_a_log()
    {
        Assert.Equal(
            "BOOT-A, 8 byte(s)",
            new ControlProgram { Id = "BOOT-A", Body = Firmware }.ToString());

        // The boot program has no name to print, so it says what it is instead.
        Assert.Equal(
            "the boot program, 8 byte(s)",
            new ControlProgram { Id = string.Empty, Body = Firmware }.ToString());
    }
}
