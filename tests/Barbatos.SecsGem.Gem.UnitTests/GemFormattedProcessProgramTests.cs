// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// The other half of stream 7: a recipe the standard gives a shape to, rather than a block of bytes
/// whose meaning is between the host and the machine.
/// </summary>
/// <remarks>
/// The interesting cases are all at the edges of the body. A formatted program is the one place in
/// E30 where the equipment has to understand the structure of a recipe rather than store it, so
/// everything the structure can be and the stack cannot represent has to come back as a refusal
/// instead of a program that was quietly changed on the way in.
/// </remarks>
public class GemFormattedProcessProgramTests
{
    private static void Model(GemEquipmentModelBuilder builder)
        => builder.AddStatusVariable(1, "State", () => SecsItem.U1(5));

    private static SecsItem Text(params string[] values)
    {
        var items = new SecsItem[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            items[i] = SecsItem.A(values[i]);
        }

        return SecsItem.L(items);
    }

    private static SecsItem Command(byte code, params string[] parameters)
        => SecsItem.L(SecsItem.U1(code), Text(parameters));

    private static SecsItem Program(string id, params SecsItem[] commands)
        => SecsItem.L(SecsItem.A(id), SecsItem.A("BRBTOS"), SecsItem.A("1.0.0"), SecsItem.L(commands));

    // ----------------------------------------------------------------- opt-in

    [Fact(Timeout = 15_000)]
    public void The_formatted_half_is_declared_on_its_own()
    {
        // Two capabilities, not one with two spellings: equipment may offer either half of stream 7,
        // both, or neither, so neither list may leak into the other or into the core set.
        foreach (var pair in GemEquipment.FormattedProcessProgramMessages)
        {
            Assert.DoesNotContain(pair, GemEquipment.HandledMessages);
            Assert.DoesNotContain(pair, GemEquipment.ProcessProgramMessages);
        }

        // Five: the two the reference documents, plus the verify, the load inquire and the
        // directory it illustrates - S7F9's, S7F1's and S7F19's twins for this half of the stream.
        Assert.Equal(5, GemEquipment.FormattedProcessProgramMessages.Count);
        Assert.All(GemEquipment.FormattedProcessProgramMessages, p => Assert.Equal(7, p.Stream));
        Assert.All(GemEquipment.FormattedProcessProgramMessages, p => Assert.True(p.Function % 2 == 1));
    }

    [Fact(Timeout = 15_000)]
    public async Task Both_halves_can_be_registered_together()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddGemEquipment(builder => builder.AddStatusVariable(1, "A", () => SecsItem.U4(1u)), runAsHostedService: false)
            .AddGemProcessPrograms()
            .AddGemFormattedProcessPrograms();

        await using var provider = services.BuildServiceProvider();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // The pair that would fail here is S7F17 or S7F19 being declared twice: the routing table
        // refuses a second handler for the same stream and function, so a shared message appearing
        // in both lists would throw while the router was being built rather than in a fab.
        foreach (var (stream, function) in GemEquipment.ProcessProgramMessages)
        {
            Assert.True(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler.");
        }

        foreach (var (stream, function) in GemEquipment.FormattedProcessProgramMessages)
        {
            Assert.True(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler.");
        }

        Assert.NotNull(provider.GetRequiredService<IGemFormattedProcessProgramStore>());
    }

    [Fact(Timeout = 15_000)]
    public async Task The_formatted_store_can_be_the_only_one()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddGemEquipment(builder => builder.AddStatusVariable(1, "A", () => SecsItem.U4(1u)), runAsHostedService: false)
            .AddGemFormattedProcessPrograms();

        await using var provider = services.BuildServiceProvider();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert.True(router.TryGetHandler(7, 23, out _));
        Assert.True(router.TryGetHandler(7, 25, out _));

        // And the unformatted half stays undeclared, which is what makes S7F3 answer S9F5 rather
        // than an acknowledgement code meaning no.
        Assert.False(router.TryGetHandler(7, 3, out _));
        Assert.Null(provider.GetService<IGemProcessProgramStore>());
    }

    // ----------------------------------------------------------------- S7F23 and S7F25

    [Fact(Timeout = 15_000)]
    public async Task A_formatted_program_survives_the_round_trip()
    {
        await using var link = GemLink.Start(Model, formatted: new GemInMemoryFormattedProcessProgramStore());
        await link.OpenAsync();

        using var sent = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
        {
            Item = Program(
                "RECIPE-7",
                Command(12, "SETPOINT", "350.0"),
                Command(13, "RAMP")),
        });

        Assert.Equal(24, sent.F);
        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());

        using var read = await link.RequestAsync(new SecsMessage(7, 25, replyExpected: true)
        {
            Item = SecsItem.A("RECIPE-7"),
        });

        Assert.Equal(26, read.F);
        Assert.Equal(4, read.Item!.Count);
        Assert.Equal("RECIPE-7", read.Item[0].GetString());
        Assert.Equal("BRBTOS", read.Item[1].GetString());
        Assert.Equal("1.0.0", read.Item[2].GetString());
        Assert.Equal(2, read.Item[3].Count);

        var first = read.Item[3][0];
        Assert.Equal(SecsFormat.U1, first[0].Format);
        Assert.Equal((byte)12, first[0].FirstValueOrDefault<byte>());
        Assert.Equal(2, first[1].Count);
        Assert.Equal("SETPOINT", first[1][0].GetString());
        Assert.Equal("350.0", first[1][1].GetString());

        var second = read.Item[3][1];
        Assert.Equal((byte)13, second[0].FirstValueOrDefault<byte>());
        Assert.Equal(1, second[1].Count);
        Assert.Equal("RAMP", second[1][0].GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_program_with_no_commands_is_still_a_program()
    {
        await using var link = GemLink.Start(Model, formatted: new GemInMemoryFormattedProcessProgramStore());
        await link.OpenAsync();

        using var sent = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
        {
            Item = Program("EMPTY"),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());

        using var read = await link.RequestAsync(new SecsMessage(7, 25, replyExpected: true)
        {
            Item = SecsItem.A("EMPTY"),
        });

        // Four items with an empty command list, not a zero-length list: the program exists, and
        // "no commands" is a different answer from "no such program".
        Assert.Equal(4, read.Item!.Count);
        Assert.Equal(0, read.Item[3].Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_formatted_program_nobody_sent_comes_back_as_a_zero_length_list()
    {
        await using var link = GemLink.Start(Model, formatted: new GemInMemoryFormattedProcessProgramStore());
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(7, 25, replyExpected: true)
        {
            Item = SecsItem.A("NOPE"),
        });

        // S7F26 has no acknowledgement code to carry a refusal, so the empty body is the refusal -
        // the same shape S7F6 uses for the same reason.
        Assert.True(reply.Item!.IsList);
        Assert.Equal(0, reply.Item.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_parameter_keeps_the_padding_it_arrived_with()
    {
        await using var link = GemLink.Start(Model, formatted: new GemInMemoryFormattedProcessProgramStore());
        await link.OpenAsync();

        using var sent = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
        {
            Item = Program("RECIPE-7", Command(1, "350.0   ", string.Empty)),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());

        using var read = await link.RequestAsync(new SecsMessage(7, 25, replyExpected: true)
        {
            Item = SecsItem.A("RECIPE-7"),
        });

        // A PPARM is not trimmed, because S7F26 has to give back what S7F23 sent and a trim is not
        // reversible. A fixed-width field full of spaces is still the host's value.
        Assert.Equal("350.0   ", read.Item![3][0][1][0].GetString());
        Assert.Equal(string.Empty, read.Item[3][0][1][1].GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task The_identifier_is_trimmed_the_way_every_other_PPID_is()
    {
        await using var link = GemLink.Start(Model, formatted: new GemInMemoryFormattedProcessProgramStore());
        await link.OpenAsync();

        using var sent = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.A("RECIPE-7   "),
                SecsItem.A("BRBTOS  "),
                SecsItem.A("1.0.0 "),
                SecsItem.L()),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());

        // PPID, MDLN and SOFTREV are fixed-width fields in E5, so the padding is framing rather
        // than value - and a host that asks for the untrimmed name would not find it.
        using var read = await link.RequestAsync(new SecsMessage(7, 25, replyExpected: true)
        {
            Item = SecsItem.A("RECIPE-7"),
        });

        Assert.Equal(4, read.Item!.Count);
        Assert.Equal("BRBTOS", read.Item[1].GetString());
        Assert.Equal("1.0.0", read.Item[2].GetString());
    }

    // ----------------------------------------------------------------- bodies the stack refuses

    [Theory(Timeout = 15_000)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task A_body_of_the_wrong_length_is_a_length_error(int count)
    {
        var fields = new SecsItem[count];

        for (var i = 0; i < count; i++)
        {
            fields[i] = SecsItem.A("x");
        }

        await using var link = GemLink.Start(Model, formatted: new GemInMemoryFormattedProcessProgramStore());
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
        {
            Item = SecsItem.L(fields),
        });

        // ACKC7 has no code meaning "that body made no sense", and LengthError is what S7F3 already
        // answers for one this stack could not read at all.
        Assert.Equal((byte)GemProcessProgramAcknowledgement.LengthError, reply.Item!.FirstValueOrDefault<byte>());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_command_that_is_not_a_code_and_a_list_is_a_length_error()
    {
        await using var link = GemLink.Start(Model, formatted: new GemInMemoryFormattedProcessProgramStore());
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.A("RECIPE-7"),
                SecsItem.A("BRBTOS"),
                SecsItem.A("1.0.0"),

                // A bare CCODE where E5 wants a list of CCODE and its parameters.
                SecsItem.L(SecsItem.U1(1))),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.LengthError, reply.Item!.FirstValueOrDefault<byte>());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_parameter_this_stack_cannot_carry_is_mode_unsupported()
    {
        await using var link = GemLink.Start(Model, formatted: new GemInMemoryFormattedProcessProgramStore());
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
        {
            Item = Program(
                "RECIPE-7",
                SecsItem.L(SecsItem.U1(1), SecsItem.L(SecsItem.U4(350u)))),
        });

        // The body is the shape E5 defines, so this is not a length error: the equipment does
        // formatted process programs, just not ones whose parameters are numbers. A host told
        // "length error" would go looking for the wrong thing.
        Assert.Equal((byte)GemProcessProgramAcknowledgement.ModeUnsupported, reply.Item!.FirstValueOrDefault<byte>());

        using var read = await link.RequestAsync(new SecsMessage(7, 25, replyExpected: true)
        {
            Item = SecsItem.A("RECIPE-7"),
        });

        // And nothing was stored. A refusal that half-stored the program would be worse than either
        // answer on its own.
        Assert.Equal(0, read.Item!.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_command_code_in_a_wider_format_is_read_when_it_fits_and_refused_when_it_does_not()
    {
        await using var link = GemLink.Start(Model, formatted: new GemInMemoryFormattedProcessProgramStore());
        await link.OpenAsync();

        using var fits = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
        {
            Item = Program("FITS", SecsItem.L(SecsItem.U4(12u), Text("A"))),
        });

        // Identifiers in this layer are read in whatever numeric format the host chose, and a U4
        // carrying 12 is the same command as a U1 carrying 12.
        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, fits.Item!.FirstValueOrDefault<byte>());

        using var read = await link.RequestAsync(new SecsMessage(7, 25, replyExpected: true)
        {
            Item = SecsItem.A("FITS"),
        });

        Assert.Equal(SecsFormat.U1, read.Item![3][0][0].Format);
        Assert.Equal((byte)12, read.Item[3][0][0].FirstValueOrDefault<byte>());

        using var overflows = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
        {
            Item = Program("WIDE", SecsItem.L(SecsItem.U4(300u), Text("A"))),
        });

        // E5 types CCODE U1[1]. Truncating 300 would store a different command and tell the host
        // its recipe landed.
        Assert.Equal((byte)GemProcessProgramAcknowledgement.ModeUnsupported, overflows.Item!.FirstValueOrDefault<byte>());
    }

    // ----------------------------------------------------------------- S7F17 and S7F19, both halves

    [Fact(Timeout = 15_000)]
    public async Task The_directory_covers_both_halves_without_repeating_a_name()
    {
        await using var link = GemLink.Start(
            Model,
            programs: new GemInMemoryProcessProgramStore(),
            formatted: new GemInMemoryFormattedProcessProgramStore());

        await link.OpenAsync();

        using var unformatted = await link.RequestAsync(new SecsMessage(7, 3, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("PLAIN"), SecsItem.B(1, 2, 3)),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, unformatted.Item!.FirstValueOrDefault<byte>());

        foreach (var id in new[] { "SHAPED", "PLAIN" })
        {
            using var sent = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
            {
                Item = Program(id, Command(1, "A")),
            });

            Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());
        }

        using var listed = await link.RequestAsync(new SecsMessage(7, 19, replyExpected: true));

        // A host asking what is stored is asking about programs, not about which transfer flavour
        // put them there - and PLAIN, which is in both stores, is one program.
        Assert.Equal(20, listed.F);
        Assert.Equal(2, listed.Item!.Count);
        Assert.Equal("PLAIN", listed.Item[0].GetString());
        Assert.Equal("SHAPED", listed.Item[1].GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task The_directory_lists_a_formatted_program_when_the_other_half_is_empty()
    {
        await using var link = GemLink.Start(
            Model,
            programs: new GemInMemoryProcessProgramStore(),
            formatted: new GemInMemoryFormattedProcessProgramStore());

        await link.OpenAsync();

        using var sent = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
        {
            Item = Program("SHAPED", Command(1, "A")),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());

        using var listed = await link.RequestAsync(new SecsMessage(7, 19, replyExpected: true));

        Assert.Equal(1, listed.Item!.Count);
        Assert.Equal("SHAPED", listed.Item[0].GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task Deleting_a_formatted_program_works_through_the_shared_message()
    {
        var formatted = new GemInMemoryFormattedProcessProgramStore();

        await using var link = GemLink.Start(
            Model,
            programs: new GemInMemoryProcessProgramStore(),
            formatted: formatted);

        await link.OpenAsync();

        using var sent = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
        {
            Item = Program("SHAPED", Command(1, "A")),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());

        using var deleted = await link.RequestAsync(new SecsMessage(7, 17, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("SHAPED")),
        });

        // The unformatted store has never heard of SHAPED and says NotFound. Reporting that would
        // be refusing something that just happened, so an Accepted from either store wins.
        Assert.Equal(18, deleted.F);
        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, deleted.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(0, formatted.Count);

        using var again = await link.RequestAsync(new SecsMessage(7, 17, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("SHAPED")),
        });

        // And when no store had it, NotFound is the truth.
        Assert.Equal((byte)GemProcessProgramAcknowledgement.NotFound, again.Item!.FirstValueOrDefault<byte>());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_empty_delete_list_empties_both_halves()
    {
        var programs = new GemInMemoryProcessProgramStore();
        var formatted = new GemInMemoryFormattedProcessProgramStore();

        await using var link = GemLink.Start(Model, programs: programs, formatted: formatted);
        await link.OpenAsync();

        using var plain = await link.RequestAsync(new SecsMessage(7, 3, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("PLAIN"), SecsItem.B(1)),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, plain.Item!.FirstValueOrDefault<byte>());

        using var shaped = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
        {
            Item = Program("SHAPED", Command(1, "A")),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, shaped.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(1, programs.Count);
        Assert.Equal(1, formatted.Count);

        using var deleted = await link.RequestAsync(new SecsMessage(7, 17, replyExpected: true) { Item = SecsItem.L() });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, deleted.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(0, programs.Count);
        Assert.Equal(0, formatted.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_store_that_is_still_working_is_the_answer_even_when_the_other_half_is_gone()
    {
        var programs = new GemInMemoryProcessProgramStore();

        await using var link = GemLink.Start(
            Model,
            programs: programs,
            formatted: new FixedAnswerFormattedStore(GemProcessProgramAcknowledgement.PerformedLater));

        await link.OpenAsync();

        using var plain = await link.RequestAsync(new SecsMessage(7, 3, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("BOTH"), SecsItem.B(1)),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, plain.Item!.FirstValueOrDefault<byte>());

        using var deleted = await link.RequestAsync(new SecsMessage(7, 17, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("BOTH")),
        });

        // The unformatted copy is gone and the formatted store will get to its own later. Two
        // stores answer one ACKC7, and Accepted here would tell the host the identifier is free
        // when it is not yet - so the store that is still working wins.
        Assert.Equal((byte)GemProcessProgramAcknowledgement.PerformedLater, deleted.Item!.FirstValueOrDefault<byte>());
    }

    // ----------------------------------------------------------------- the types on their own

    // ----------------------------------------------------------------- S7F35 and S7F36

    [Fact(Timeout = 15_000)]
    public async Task The_formatted_directory_carries_every_program_whole()
    {
        await using var link = GemLink.Start(Model, formatted: new GemInMemoryFormattedProcessProgramStore());
        await link.OpenAsync();

        foreach (var id in new[] { "RECIPE-1", "RECIPE-2" })
        {
            using var sent = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
            {
                Item = Program(id, Command(12, "SETPOINT", "350.0")),
            });

            Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, sent.Item!.FirstValueOrDefault<byte>());
        }

        using var listed = await link.RequestAsync(new SecsMessage(7, 35, replyExpected: true));

        // The whole question this row was refused over, and the only place the answer is visible.
        // Each entry is what S7F26 carries one program in, because the reference records this row
        // with PPBODY where it records S7F8 and S7F20 with PPID alone.
        Assert.Equal(36, listed.F);
        Assert.Equal(2, listed.Item!.Count);

        foreach (var entry in listed.Item.Items)
        {
            Assert.Equal(4, entry.Count);
            Assert.StartsWith("RECIPE-", entry[0].GetString(), StringComparison.Ordinal);
            Assert.Equal("BRBTOS", entry[1].GetString());
            Assert.Equal("1.0.0", entry[2].GetString());
            Assert.Equal(1, entry[3].Count);
            Assert.Equal((byte)12, entry[3][0][0].FirstValueOrDefault<byte>());
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task The_formatted_directory_is_the_formatted_half_alone()
    {
        await using var link = GemLink.Start(
            Model,
            programs: new GemInMemoryProcessProgramStore(),
            formatted: new GemInMemoryFormattedProcessProgramStore());

        await link.OpenAsync();

        using var plain = await link.RequestAsync(new SecsMessage(7, 3, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("PLAIN"), SecsItem.B(1, 2, 3)),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, plain.Item!.FirstValueOrDefault<byte>());

        using var shaped = await link.RequestAsync(new SecsMessage(7, 23, replyExpected: true)
        {
            Item = Program("SHAPED", Command(1, "A")),
        });

        Assert.Equal((byte)GemProcessProgramAcknowledgement.Accepted, shaped.Item!.FirstValueOrDefault<byte>());

        // S7F19 is not per-format and answers for both halves. This row is per-format, which is
        // the whole reason it is worth serving: nothing else here lets a host ask which of a
        // machine's programs are structured.
        using var both = await link.RequestAsync(new SecsMessage(7, 19, replyExpected: true));

        Assert.Equal(2, both.Item!.Count);

        using var formattedOnly = await link.RequestAsync(new SecsMessage(7, 35, replyExpected: true));

        Assert.Equal(1, formattedOnly.Item!.Count);
        Assert.Equal("SHAPED", formattedOnly.Item[0][0].GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_machine_holding_no_formatted_programs_answers_an_empty_directory()
    {
        await using var link = GemLink.Start(Model, formatted: new GemInMemoryFormattedProcessProgramStore());
        await link.OpenAsync();

        using var listed = await link.RequestAsync(new SecsMessage(7, 35, replyExpected: true));

        // An empty directory is a true answer and the row has no acknowledgement to say anything
        // else with - the same position S7F8 and S7F20 are in. What it must not be is absent: a
        // machine with a formatted store declares the row whether or not anything is in it.
        Assert.Equal(36, listed.F);
        Assert.True(listed.Item!.IsList);
        Assert.Equal(0, listed.Item.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_identifier_the_store_loses_between_the_list_and_the_load_is_left_out()
    {
        await using var link = GemLink.Start(Model, formatted: new ForgetfulStore());
        await link.OpenAsync();

        using var listed = await link.RequestAsync(new SecsMessage(7, 35, replyExpected: true));

        // A directory is what is there now. The alternative would be an entry with no commands,
        // which is a real answer for a real program - A_program_with_no_commands_is_still_a_program
        // above - so it must not also mean "this one went away".
        Assert.Equal(1, listed.Item!.Count);
        Assert.Equal("HERE", listed.Item[0][0].GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task The_host_reads_the_directory_as_programs()
    {
        await using var pair = GemPair.Start(formatted: new GemInMemoryFormattedProcessProgramStore());
        await pair.OpenAsync();

        foreach (var id in new[] { "RECIPE-1", "RECIPE-2" })
        {
            Assert.Equal(
                GemProcessProgramAcknowledgement.Accepted,
                await pair.Host.SendFormattedProcessProgramAsync(
                    new GemFormattedProcessProgram(id, "BRBTOS", "1.0.0", [new GemProcessProgramCommand(12, ["SETPOINT"])]),
                    GemPair.Token));
        }

        var directory = await pair.Host.RequestFormattedProcessProgramDirectoryAsync(GemPair.Token);

        Assert.Equal(2, directory.Count);
        Assert.Equal<string[]>(["RECIPE-1", "RECIPE-2"], [.. directory.Select(program => program.Id).Order()]);
        Assert.All(directory, program => Assert.Equal("SETPOINT", Assert.Single(Assert.Single(program.Commands).Parameters)));
    }

    [Fact(Timeout = 15_000)]
    public async Task One_unwrapped_program_is_read_too_and_four_wrapped_ones_are_not_mistaken_for_one()
    {
        // The nesting here is an inference - the reference records an item list and no shape - so
        // this end writes one reading and reads two. The four-program case is the one that could
        // go wrong, because a single program is also a list of four: what separates them is that a
        // program's first item is text where a directory's is a list.
        var single = GemPair.Start(
            formatted: new GemInMemoryFormattedProcessProgramStore(),
            answerEquipment: transaction => transaction.Message is { S: 7, F: 35 }
                ? new SecsMessage(7, 36, replyExpected: false)
                {
                    Item = Program("ALONE", Command(1, "A")),
                }
                : null);

        await using (single)
        {
            await single.OpenAsync();

            var one = await single.Host.RequestFormattedProcessProgramDirectoryAsync(GemPair.Token);

            Assert.Equal("ALONE", Assert.Single(one).Id);
        }

        var four = GemPair.Start(
            formatted: new GemInMemoryFormattedProcessProgramStore(),
            answerEquipment: transaction => transaction.Message is { S: 7, F: 35 }
                ? new SecsMessage(7, 36, replyExpected: false)
                {
                    Item = SecsItem.L(
                        Program("A", Command(1, "P")),
                        Program("B", Command(1, "P")),
                        Program("C", Command(1, "P")),
                        Program("D", Command(1, "P"))),
                }
                : null);

        await using (four)
        {
            await four.OpenAsync();

            var all = await four.Host.RequestFormattedProcessProgramDirectoryAsync(GemPair.Token);

            Assert.Equal<string[]>(["A", "B", "C", "D"], [.. all.Select(program => program.Id)]);
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task A_directory_of_identifiers_reads_as_nothing_and_the_log_says_how_many()
    {
        var log = new RecordingLogger<GemHost>();

        await using var pair = GemPair.Start(
            formatted: new GemInMemoryFormattedProcessProgramStore(),
            hostLogger: log,
            answerEquipment: transaction => transaction.Message is { S: 7, F: 35 }
                ? new SecsMessage(7, 36, replyExpected: false)
                {
                    Item = Text("RECIPE-1", "RECIPE-2"),
                }
                : null);

        await pair.OpenAsync();

        // What the reading this chapter rejected looks like from here. S7F36 has no
        // acknowledgement item, so there is nowhere to tell the machine its directory was
        // unreadable - which makes the log the only place the two empty answers differ, and
        // asserting it is the only thing that stops the warning being deleted.
        Assert.Empty(await pair.Host.RequestFormattedProcessProgramDirectoryAsync(GemPair.Token));

        Assert.Contains(
            log.From(LogLevel.Warning),
            message => message.Contains("S7F36", StringComparison.Ordinal)
                && message.Contains('2', StringComparison.Ordinal));
    }

    /// <summary>A store that lists an identifier it will not load, which is a delete racing a directory.</summary>
    private sealed class ForgetfulStore : IGemFormattedProcessProgramStore
    {
        public ValueTask<GemProcessProgramAcknowledgement> SaveAsync(
            GemFormattedProcessProgram program,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(GemProcessProgramAcknowledgement.Accepted);

        public ValueTask<GemFormattedProcessProgram?> LoadAsync(string id, CancellationToken cancellationToken)
            => ValueTask.FromResult<GemFormattedProcessProgram?>(
                id == "HERE"
                    ? new GemFormattedProcessProgram("HERE", "BRBTOS", "1.0.0", [])
                    : null);

        public ValueTask<GemProcessProgramAcknowledgement> DeleteAsync(string id, CancellationToken cancellationToken)
            => ValueTask.FromResult(GemProcessProgramAcknowledgement.Accepted);

        public ValueTask<GemProcessProgramAcknowledgement> DeleteAllAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(GemProcessProgramAcknowledgement.Accepted);

        public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>(["HERE", "GONE"]);
    }

    [Fact(Timeout = 15_000)]
    public void A_program_owns_its_commands()
    {
        List<GemProcessProgramCommand> commands = [new(1, ["A"])];
        var program = new GemFormattedProcessProgram("RECIPE-7", "BRBTOS", "1.0.0", commands);

        // The handler builds these from a message that is disposed as soon as it returns, and a
        // store keeps them for as long as it likes. A program that aliased the caller's list would
        // change under the store.
        commands.Clear();

        Assert.Single(program.Commands);
        Assert.Equal("RECIPE-7 (BRBTOS 1.0.0, 1 commands)", program.ToString());
    }

    [Fact(Timeout = 15_000)]
    public void A_command_owns_its_parameters_and_refuses_a_hole()
    {
        List<string> parameters = ["A", "B"];
        var command = new GemProcessProgramCommand(7, parameters);

        parameters.Clear();

        Assert.Equal(2, command.Parameters.Count);
        Assert.Equal("CCODE 7 (2 parameters)", command.ToString());

        // A null parameter cannot be written back to the host, so it is refused where it is given
        // rather than where it is sent.
        Assert.Throws<ArgumentNullException>(() => new GemProcessProgramCommand(7, [null!]));
        Assert.Throws<ArgumentNullException>(() => new GemFormattedProcessProgram("A", "M", "R", [null!]));
    }

    [Fact(Timeout = 15_000)]
    public async Task The_in_memory_formatted_store_enforces_its_capacity()
    {
        var store = new GemInMemoryFormattedProcessProgramStore { Capacity = 2 };
        var token = TestContext.Current.CancellationToken;

        static GemFormattedProcessProgram Shaped(string id)
            => new(id, "BRBTOS", "1.0.0", [new GemProcessProgramCommand(1, ["A"])]);

        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync(Shaped("A"), token));
        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync(Shaped("B"), token));
        Assert.Equal(GemProcessProgramAcknowledgement.PermissionNotGranted, await store.SaveAsync(Shaped("C"), token));

        // Replacing one that is already there is not a new slot, so it still fits.
        Assert.Equal(GemProcessProgramAcknowledgement.Accepted, await store.SaveAsync(Shaped("A"), token));
        Assert.Equal(2, store.Count);

        Assert.Null(await store.LoadAsync("C", token));
        Assert.NotNull(await store.LoadAsync("A", token));
        Assert.Equal(new[] { "A", "B" }, (await store.ListAsync(token)).ToArray());
    }

    [Fact(Timeout = 15_000)]
    public async Task The_in_memory_formatted_store_refuses_an_identifier_E5_would_not_allow()
    {
        var store = new GemInMemoryFormattedProcessProgramStore();
        var token = TestContext.Current.CancellationToken;
        var tooLong = new string('x', GemInMemoryProcessProgramStore.MaxIdLength + 1);

        Assert.Equal(
            GemProcessProgramAcknowledgement.NotFound,
            await store.SaveAsync(new GemFormattedProcessProgram(tooLong, "M", "R", []), token));

        Assert.Equal(
            GemProcessProgramAcknowledgement.NotFound,
            await store.SaveAsync(new GemFormattedProcessProgram(string.Empty, "M", "R", []), token));

        Assert.Equal(0, store.Count);
    }
}

/// <summary>A formatted store that answers every delete with one fixed code.</summary>
/// <remarks>
/// <see cref="GemInMemoryFormattedProcessProgramStore"/> only ever says Accepted or NotFound, so
/// the rule for two stores answering one ACKC7 cannot be exercised through it.
/// </remarks>
/// <param name="answer">The code every delete answers with.</param>
internal sealed class FixedAnswerFormattedStore(GemProcessProgramAcknowledgement answer)
    : IGemFormattedProcessProgramStore
{
    /// <inheritdoc/>
    public ValueTask<GemProcessProgramAcknowledgement> SaveAsync(
        GemFormattedProcessProgram program,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(GemProcessProgramAcknowledgement.Accepted);

    /// <inheritdoc/>
    public ValueTask<GemFormattedProcessProgram?> LoadAsync(string id, CancellationToken cancellationToken)
        => ValueTask.FromResult<GemFormattedProcessProgram?>(null);

    /// <inheritdoc/>
    public ValueTask<GemProcessProgramAcknowledgement> DeleteAsync(string id, CancellationToken cancellationToken)
        => ValueTask.FromResult(answer);

    /// <inheritdoc/>
    public ValueTask<GemProcessProgramAcknowledgement> DeleteAllAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(answer);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<string>>([]);
}
