// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;

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

        // Four: the two the reference documents, plus the verify and the load inquire it
        // illustrates - S7F9's and S7F1's twins for this half of the stream.
        Assert.Equal(4, GemEquipment.FormattedProcessProgramMessages.Count);
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
