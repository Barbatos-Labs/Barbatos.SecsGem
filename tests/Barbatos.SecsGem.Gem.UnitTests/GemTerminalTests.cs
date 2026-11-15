// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// Stream 10: the operator standing at the machine.
/// </summary>
/// <remarks>
/// Both directions have a person at one end, which is what makes the acknowledgement codes worth
/// testing rather than assuming. "Accepted for display" is not "somebody read it", "will not be
/// displayed" is a decision, and "terminal not available" is a fault - and a host retrying is only
/// right for the third.
/// </remarks>
public class GemTerminalTests
{
    private static void Model(GemEquipmentModelBuilder builder)
        => builder.AddStatusVariable(1, "State", () => SecsItem.U1(5));

    // ----------------------------------------------------------------- opt-in

    [Fact]
    public void Terminal_services_are_declared_on_their_own()
    {
        foreach (var pair in GemEquipment.TerminalMessages)
        {
            Assert.DoesNotContain(pair, GemEquipment.HandledMessages);
        }

        // S10F3, S10F5 and S10F9 - what the equipment answers. S10F1 is one it sends, and S10F7 is
        // the entry the catalogue flags as representative rather than defined.
        Assert.Equal(3, GemEquipment.TerminalMessages.Count);
        Assert.All(GemEquipment.TerminalMessages, p => Assert.Equal(10, p.Stream));
        Assert.All(GemEquipment.TerminalMessages, p => Assert.True(p.Function % 2 == 1));
        Assert.DoesNotContain(((byte)10, (byte)7), GemEquipment.TerminalMessages);

        // And the host answers the one the equipment sends.
        Assert.Contains(((byte)10, (byte)1), GemHost.HandledMessages);
    }

    [Fact]
    public async Task Equipment_with_no_terminal_says_so_rather_than_refusing_each_message()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        byte? function = null;
        pair.Host.ErrorReported += (_, e) => function = e.Function;

        var asked = pair.Host.DisplayTerminalMessageAsync("anybody there?", cancellationToken: GemPair.Token).AsTask();

        await GemPair.WaitUntilAsync(() => function is not null, "a stream 9 report to arrive");

        // S9F3, not S9F5 and not ACKC10 2: the whole stream is unknown to this machine. A host
        // told "terminal not available" would retry something that is never going to work, which
        // is the difference this capability being opt-in is for.
        Assert.Equal((byte)3, function);

        await pair.AdvanceUntilAsync(TimeSpan.FromSeconds(5), () => asked.IsCompleted, "T3 to expire");
        await Assert.ThrowsAsync<SecsTimeoutException>(() => asked);
    }

    // ----------------------------------------------------------------- host to operator

    [Fact]
    public async Task A_single_block_reaches_the_operator()
    {
        var terminal = new GemInMemoryTerminal();

        await using var pair = GemPair.Start(Model, terminal: terminal);
        await pair.OpenAsync();

        var ackc10 = await pair.Host.DisplayTerminalMessageAsync("load port 2 is open", 1, GemPair.Token);

        Assert.Equal(GemTerminalAcknowledgement.Accepted, ackc10);

        var shown = Assert.Single(terminal.Displayed);

        Assert.Equal((byte)1, shown.TerminalId);
        Assert.False(shown.IsBroadcast);
        Assert.Equal(["load port 2 is open"], shown.Lines);
        Assert.Equal("terminal 1: 1 block(s)", shown.ToString());
    }

    [Fact]
    public async Task Several_blocks_arrive_in_order_and_keep_their_blank_line()
    {
        var terminal = new GemInMemoryTerminal();

        await using var pair = GemPair.Start(Model, terminal: terminal);
        await pair.OpenAsync();

        string[] lines = ["Lot LOT-1 is ready.", string.Empty, "Press START when the cassette is seated."];

        var ackc10 = await pair.Host.DisplayTerminalMessageAsync(lines, 0, GemPair.Token);

        Assert.Equal(GemTerminalAcknowledgement.Accepted, ackc10);

        var shown = Assert.Single(terminal.Displayed);

        // The blank line survives, which is the whole reason terminal text is read with its own
        // reader: the identifier readers drop an empty item, and here an empty line is the layout
        // somebody chose for an operator to read.
        Assert.Equal(lines, shown.Lines);
    }

    [Fact]
    public async Task A_broadcast_says_that_it_is_one()
    {
        var terminal = new GemInMemoryTerminal();

        await using var pair = GemPair.Start(Model, terminal: terminal);
        await pair.OpenAsync();

        Assert.Equal(
            GemTerminalAcknowledgement.Accepted,
            await pair.Host.BroadcastAsync("the fab is going to maintenance in ten minutes", GemPair.Token));

        var shown = Assert.Single(terminal.Displayed);

        // There is no TID in an S10F9 body at all, so zero here is the absence of a choice rather
        // than terminal number zero - and equipment with several terminals can only tell the
        // difference from this flag.
        Assert.True(shown.IsBroadcast);
        Assert.Equal((byte)0, shown.TerminalId);
        Assert.Equal("broadcast: 1 block(s)", shown.ToString());
    }

    [Fact]
    public async Task A_terminal_that_is_switched_off_refuses_and_keeps_nothing()
    {
        var terminal = new GemInMemoryTerminal
        {
            Acknowledgement = GemTerminalAcknowledgement.TerminalNotAvailable,
        };

        await using var pair = GemPair.Start(Model, terminal: terminal);
        await pair.OpenAsync();

        Assert.Equal(
            GemTerminalAcknowledgement.TerminalNotAvailable,
            await pair.Host.DisplayTerminalMessageAsync("anybody there?", cancellationToken: GemPair.Token));

        // The code reaches the host, which is the point: the machine has a terminal and it cannot
        // be reached, so this is the one answer worth retrying.
        Assert.Empty(terminal.Displayed);
    }

    [Fact]
    public async Task A_body_this_stack_cannot_read_is_not_shown_to_anybody()
    {
        var terminal = new GemInMemoryTerminal();

        await using var link = GemLink.Start(Model, terminal: terminal);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(10, 3, replyExpected: true)
        {
            // One item where E5 wants TID and TEXT.
            Item = SecsItem.L(SecsItem.A("no identifier here")),
        });

        Assert.Equal(4, reply.F);

        // ACKC10 has no code for "that body made no sense", and 1 is the closest honest one: it
        // arrived and nobody is going to see it.
        Assert.Equal((byte)GemTerminalAcknowledgement.WillNotDisplay, reply.Item!.FirstValueOrDefault<byte>());
        Assert.Empty(terminal.Displayed);
    }

    [Fact]
    public async Task Either_display_function_is_understood_whichever_way_the_text_arrives()
    {
        var terminal = new GemInMemoryTerminal();

        await using var link = GemLink.Start(Model, terminal: terminal);
        await link.OpenAsync();

        // A single-block function carrying a list, and a multi-block function carrying one bare
        // item. Both are a host using the other one's shape, and refusing the text over the shape
        // of its container would help nobody standing at the machine.
        using var single = await link.RequestAsync(new SecsMessage(10, 3, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.B(0), SecsItem.L(SecsItem.A("one"), SecsItem.A("two"))),
        });

        Assert.Equal((byte)GemTerminalAcknowledgement.Accepted, single.Item!.FirstValueOrDefault<byte>());

        using var multi = await link.RequestAsync(new SecsMessage(10, 5, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.B(0), SecsItem.A("just the one")),
        });

        Assert.Equal(6, multi.F);
        Assert.Equal((byte)GemTerminalAcknowledgement.Accepted, multi.Item!.FirstValueOrDefault<byte>());

        Assert.Equal(2, terminal.Displayed.Count);
        Assert.Equal(["one", "two"], terminal.Displayed.First().Lines);
        Assert.Equal(["just the one"], terminal.Displayed.Last().Lines);
    }

    // ----------------------------------------------------------------- operator to host

    [Fact]
    public async Task An_operator_message_reaches_the_host_and_its_answer_comes_back()
    {
        await using var pair = GemPair.Start(Model, terminal: new GemInMemoryTerminal());
        await pair.OpenAsync();

        GemTerminalRequestEventArgs? seen = null;

        pair.Host.TerminalRequestReceived += (_, e) =>
        {
            seen = e;

            // A host with nowhere to put the message says so, rather than accepting it and leaving
            // somebody at a machine believing it was read.
            e.Acknowledgement = GemTerminalAcknowledgement.WillNotDisplay;
        };

        var ackc10 = await pair.Equipment.SendTerminalRequestAsync("cassette jammed in load port 2", 2, GemPair.Token);

        Assert.Equal(GemTerminalAcknowledgement.WillNotDisplay, ackc10);

        Assert.NotNull(seen);
        Assert.Equal((byte)2, seen.TerminalId);
        Assert.Equal("cassette jammed in load port 2", seen.Text);
        Assert.Equal("terminal 2: cassette jammed in load port 2", seen.ToString());
    }

    [Fact]
    public async Task An_operator_message_with_no_host_listening_is_not_held()
    {
        // Selected, but the host has denied S1F13 - the state a machine does most of its waiting
        // in: powered on, cabled, and not yet talking to anybody.
        await using var link = GemLink.Start(Model, terminal: new GemInMemoryTerminal());

        await link.SelectAsync();
        using var denied = await link.EstablishAsync(GemCommunicationAcknowledgement.Denied);

        await GemLink.WaitUntilAsync(() => !link.Equipment.IsCommunicating, "the equipment to be not communicating");

        // Null rather than an acknowledgement, and deliberately not spooled: a message from an
        // operator is a prompt for a person at the host, and handing it over an hour later when
        // both of them have gone home is worse than telling the caller it did not go.
        Assert.Null(await link.Equipment.SendTerminalRequestAsync("anybody there?", 0, GemLink.Token));
    }

    // ----------------------------------------------------------------- the terminal on its own

    [Fact]
    public async Task The_in_memory_terminal_keeps_what_it_can_and_drops_the_oldest()
    {
        var terminal = new GemInMemoryTerminal { Capacity = 2 };
        var token = TestContext.Current.CancellationToken;

        foreach (var text in new[] { "first", "second", "third" })
        {
            Assert.Equal(
                GemTerminalAcknowledgement.Accepted,
                await terminal.DisplayAsync(new GemTerminalMessage(0, [text]), token));
        }

        // Bounded, because a host that broadcasts every minute for a month should not be able to
        // grow a simulator's memory without limit.
        Assert.Equal(2, terminal.Displayed.Count);
        Assert.Equal(["second"], terminal.Displayed.First().Lines);
        Assert.Equal(["third"], terminal.Displayed.Last().Lines);
    }

    [Fact]
    public void A_message_owns_its_text_and_refuses_a_hole()
    {
        List<string> lines = ["one", "two"];
        var message = new GemTerminalMessage(3, lines);

        lines.Clear();

        Assert.Equal(2, message.Lines.Count);
        Assert.Equal((byte)3, message.TerminalId);

        Assert.Throws<ArgumentNullException>(() => new GemTerminalMessage(0, [null!]));
    }
}
