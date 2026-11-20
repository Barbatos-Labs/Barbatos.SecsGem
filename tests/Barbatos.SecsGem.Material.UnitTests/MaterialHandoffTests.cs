// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Material.UnitTests;

/// <summary>
/// The interlocked material handoff - stream 4's S4F1 to S4F18, at both ends of one link.
/// </summary>
/// <remarks>
/// <para>
/// Stream 4 was the best-covered stream this repository had not implemented: 27 rows, and every
/// item name in all of them defined in the dictionary, which is true of no other stream here. So
/// unusually little of this is inference - <c>RSACK</c> and <c>RRACK</c> arrive with their own
/// tables of values, and the two acknowledgement enums are quoted from them.
/// </para>
/// <para>
/// What is new in these tests is the symmetry. Every row of the handshake is marked as travelling
/// either way, so there is no end that answers and no end that asks: both register the same nine
/// primaries, and <c>Either_end_may_play_either_role</c> is the test that says this
/// implementation believes it too.
/// </para>
/// </remarks>
public class MaterialHandoffTests
{
    private const string Lot = "LOT-7";

    /// <summary>Collects every step an end is told about, in order.</summary>
    private static List<MaterialHandoffEventArgs> Watch(MaterialHandoff end)
    {
        List<MaterialHandoffEventArgs> seen = [];

        end.HandoffReported += (_, args) => seen.Add(args);

        return seen;
    }

    [Fact]
    public async Task An_offer_is_accepted_and_the_other_end_hears_it()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var seen = Watch(pair.Receiver);

        var answer = await pair.Sender.OfferAsync(1, Lot, MaterialPair.Token);

        Assert.Equal(MaterialSendAcknowledgement.WillAccept, answer);

        var offer = Assert.Single(seen);

        Assert.Equal(MaterialHandoffStep.ReadyToSend, offer.Step);
        Assert.Equal((byte)1, offer.PortId);
        Assert.Equal(Lot, offer.MaterialId);
        Assert.Equal("ReadyToSend port 1 (LOT-7)", offer.ToString());
    }

    /// <summary>E5 gives four reasons a port cannot take material, and they are worth sending.</summary>
    /// <remarks>
    /// <c>RSACK</c> is one of the few items in this stream that arrives with a table of values, so
    /// these five names are quoted from the dictionary rather than read off a format. A bare
    /// refusal sends somebody to a log file for an afternoon; "port occupied" does not.
    /// </remarks>
    [Theory]
    [InlineData(MaterialSendAcknowledgement.InvalidPort)]
    [InlineData(MaterialSendAcknowledgement.PortOccupied)]
    [InlineData(MaterialSendAcknowledgement.Busy)]
    [InlineData(MaterialSendAcknowledgement.NotInRemoteMode)]
    public async Task An_offer_may_be_refused_with_any_of_E5s_reasons(MaterialSendAcknowledgement reason)
    {
        await using var pair = MaterialPair.Start();

        pair.Receiver.AnswerOffer = (_, _) => reason;

        await pair.OpenAsync();

        Assert.Equal(reason, await pair.Sender.OfferAsync(1, Lot, MaterialPair.Token));
    }

    [Fact]
    public async Task The_answer_may_depend_on_the_port_and_the_material()
    {
        await using var pair = MaterialPair.Start();

        pair.Receiver.AnswerOffer = (portId, materialId) => portId == 1 && materialId == Lot
            ? MaterialSendAcknowledgement.WillAccept
            : MaterialSendAcknowledgement.InvalidPort;

        await pair.OpenAsync();

        Assert.Equal(
            MaterialSendAcknowledgement.WillAccept,
            await pair.Sender.OfferAsync(1, Lot, MaterialPair.Token));

        Assert.Equal(
            MaterialSendAcknowledgement.InvalidPort,
            await pair.Sender.OfferAsync(2, Lot, MaterialPair.Token));
    }

    [Fact]
    public async Task A_request_to_receive_is_granted_or_denied()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var seen = Watch(pair.Sender);

        // The receiver asks the sender for material, which is the direction the other eight go in.
        Assert.Equal(
            MaterialReceiveAcknowledgement.Granted,
            await pair.Receiver.RequestMaterialAsync(1, Lot, MaterialPair.Token));

        pair.Sender.AnswerRequest = (_, _) => MaterialReceiveAcknowledgement.Denied;

        Assert.Equal(
            MaterialReceiveAcknowledgement.Denied,
            await pair.Receiver.RequestMaterialAsync(1, Lot, MaterialPair.Token));

        Assert.Equal(2, seen.Count);
        Assert.All(seen, step => Assert.Equal(MaterialHandoffStep.RequestToReceive, step.Step));
    }

    /// <summary>The whole handshake, in the order E5 names the steps.</summary>
    /// <remarks>
    /// Seven of the nine have no reply at all - E5 gives them no secondary - so they are posted
    /// and the interlock is what tells the sender it was heard. The order here is E5's and this
    /// package does not enforce it; see the test below.
    /// </remarks>
    [Fact]
    public async Task A_whole_handoff_runs_in_the_order_E5_names()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var atReceiver = Watch(pair.Receiver);
        var atSender = Watch(pair.Sender);

        Assert.Equal(
            MaterialSendAcknowledgement.WillAccept,
            await pair.Sender.OfferAsync(1, Lot, MaterialPair.Token));

        await pair.Sender.ReportAsync(MaterialHandoffStep.SendMaterial, 1, Lot, MaterialPair.Token);
        await pair.Receiver.ReportAsync(MaterialHandoffStep.MaterialReceived, 1, Lot, MaterialPair.Token);
        await pair.Sender.ReportAsync(MaterialHandoffStep.HandshakeComplete, 1, Lot, MaterialPair.Token);

        await MaterialPair.WaitUntilAsync(() => atReceiver.Count == 3, "the receiver to hear three steps");
        await MaterialPair.WaitUntilAsync(() => atSender.Count == 1, "the sender to hear one step");

        Assert.Equal(
            [MaterialHandoffStep.ReadyToSend, MaterialHandoffStep.SendMaterial, MaterialHandoffStep.HandshakeComplete],
            atReceiver.Select(step => step.Step));

        Assert.Equal(MaterialHandoffStep.MaterialReceived, Assert.Single(atSender).Step);
    }

    /// <summary>The three faults E5 gives this stream, which is more than it gives most.</summary>
    [Theory]
    [InlineData(MaterialHandoffStep.StuckInSender)]
    [InlineData(MaterialHandoffStep.StuckInReceiver)]
    [InlineData(MaterialHandoffStep.SendIncompleteTimeout)]
    [InlineData(MaterialHandoffStep.NotReadyToSend)]
    public async Task A_fault_is_one_message_and_needs_no_answer(MaterialHandoffStep step)
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var seen = Watch(pair.Receiver);

        await pair.Sender.ReportAsync(step, 3, Lot, MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(() => seen.Count == 1, $"the receiver to hear {step}");

        Assert.Equal(step, Assert.Single(seen).Step);
        Assert.Equal((byte)3, seen[0].PortId);
    }

    /// <summary>The two that are answered cannot be posted, because a reply is waiting for them.</summary>
    [Theory]
    [InlineData(MaterialHandoffStep.ReadyToSend)]
    [InlineData(MaterialHandoffStep.RequestToReceive)]
    public async Task The_two_answered_steps_are_refused_by_the_method_that_posts(MaterialHandoffStep step)
    {
        await using var pair = MaterialPair.Start();

        var thrown = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await pair.Sender.ReportAsync(step, 1, Lot, MaterialPair.Token));

        Assert.Equal("step", thrown.ParamName);
        Assert.Contains($"S4F{(byte)step} expects a reply", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>What arrived is announced before this end decides what to say about it.</summary>
    /// <remarks>
    /// The order matters for the two answered steps, and it is the only one that makes the event
    /// mean the same thing for all nine: it carries what the other end said, not what this end
    /// did. A handler that wants both records them in the answer delegate, which has the port and
    /// the material in hand and returns the answer.
    /// </remarks>
    [Fact]
    public async Task A_step_is_announced_before_the_answer_is_decided()
    {
        await using var pair = MaterialPair.Start();

        List<string> order = [];

        pair.Receiver.HandoffReported += (_, args) => order.Add($"heard {args.Step}");
        pair.Receiver.AnswerOffer = (_, _) =>
        {
            order.Add("answered");

            return MaterialSendAcknowledgement.WillAccept;
        };

        await pair.OpenAsync();

        Assert.Equal(
            MaterialSendAcknowledgement.WillAccept,
            await pair.Sender.OfferAsync(1, Lot, MaterialPair.Token));

        Assert.Equal(["heard ReadyToSend", "answered"], order);
    }

    /// <summary>The claim the catalogue makes about this stream, tested.</summary>
    /// <remarks>
    /// Every row of S4F1 to S4F18 is marked as travelling either way, because the two parties are
    /// a sender and a receiver of material rather than a host and a machine. So the same nine
    /// primaries are registered at both ends, and the end this test calls the receiver can run the
    /// whole handshake in the other direction.
    /// </remarks>
    [Fact]
    public async Task Either_end_may_play_either_role()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var atSender = Watch(pair.Sender);

        // The same calls as the handshake test, with the two ends swapped.
        Assert.Equal(
            MaterialSendAcknowledgement.WillAccept,
            await pair.Receiver.OfferAsync(2, Lot, MaterialPair.Token));

        await pair.Receiver.ReportAsync(MaterialHandoffStep.SendMaterial, 2, Lot, MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(() => atSender.Count == 2, "the other end to hear two steps");

        Assert.Equal(
            [MaterialHandoffStep.ReadyToSend, MaterialHandoffStep.SendMaterial],
            atSender.Select(step => step.Step));

        // And both ends declared the same list, which is what makes that possible.
        Assert.Equal(9, MaterialHandoff.HandledMessages.Count);
        Assert.All(MaterialHandoff.HandledMessages, message => Assert.Equal(4, message.Stream));
    }

    [Fact]
    public async Task Every_primary_this_end_declares_is_one_it_can_send()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        await pair.Sender.OfferAsync(1, Lot, MaterialPair.Token);
        await pair.Sender.RequestMaterialAsync(1, Lot, MaterialPair.Token);

        foreach (var step in Enum.GetValues<MaterialHandoffStep>())
        {
            if (step is MaterialHandoffStep.ReadyToSend or MaterialHandoffStep.RequestToReceive)
            {
                continue;
            }

            await pair.Sender.ReportAsync(step, 1, Lot, MaterialPair.Token);
        }

        await MaterialPair.WaitUntilAsync(
            () => MaterialHandoff.HandledMessages.All(message => pair.AskedOfEquipment.Contains(message)),
            "all nine handoff primaries to arrive");

        // And stream 3's two lists share nothing with stream 4's, so a machine may register all
        // three without a duplicate declaration.
        Assert.Empty(MaterialHandoff.HandledMessages.Intersect(MaterialEquipment.HandledMessages));
        Assert.Empty(MaterialHandoff.HandledMessages.Intersect(MaterialHost.HandledMessages));
    }

    /// <summary>An equipment-specific code arrives as Other, with the number logged.</summary>
    /// <remarks>
    /// E5 reserves 5 to 63 of <c>RSACK</c> and 2 to 63 of <c>RRACK</c> for the equipment, so an
    /// unfamiliar code is legitimate rather than an error - and it must not read as the zero of
    /// the enum, which is "will accept". This package collapses the range to one member and logs
    /// the arriving code, the same rule <c>MaterialGrant</c> keeps.
    /// </remarks>
    [Fact]
    public async Task An_equipment_specific_code_is_not_read_as_acceptance()
    {
        await using var pair = MaterialPair.Start(hostAnswer: transaction => transaction.F switch
        {
            1 => new SecsMessage(4, 2, replyExpected: false) { Item = SecsItem.B(9).Named("RSACK") },
            _ => new SecsMessage(4, 18, replyExpected: false) { Item = SecsItem.B(40).Named("RRACK") },
        });

        await pair.OpenAsync();

        Assert.Equal(
            MaterialSendAcknowledgement.Other,
            await pair.Receiver.OfferAsync(1, Lot, MaterialPair.Token));

        Assert.Equal(
            MaterialReceiveAcknowledgement.Other,
            await pair.Receiver.RequestMaterialAsync(1, Lot, MaterialPair.Token));
    }

    [Fact]
    public async Task A_reply_with_no_code_in_it_is_an_exception()
    {
        await using var pair = MaterialPair.Start(hostAnswer: _ => new SecsMessage(4, 2, replyExpected: false)
        {
            Item = SecsItem.L(),
        });

        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SecsException>(
            async () => await pair.Receiver.OfferAsync(1, Lot, MaterialPair.Token));

        Assert.Equal("S4F2 carried no RSACK.", thrown.Message);
    }

    /// <summary>A port on its own is still a step, because a handoff step is about a port first.</summary>
    [Fact]
    public async Task A_bare_port_number_reads_as_a_step_with_no_material()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var seen = Watch(pair.Receiver);

        using (var bare = new SecsMessage(4, 5, replyExpected: false) { Item = SecsItem.U1(4).Named("PTN") })
        {
            await pair.HostLink.PostAsync(bare, MaterialPair.Token);
        }

        await MaterialPair.WaitUntilAsync(() => seen.Count == 1, "the bare step to arrive");

        Assert.Equal(MaterialHandoffStep.HandshakeComplete, seen[0].Step);
        Assert.Equal((byte)4, seen[0].PortId);
        Assert.Equal(string.Empty, seen[0].MaterialId);
        Assert.Equal("HandshakeComplete port 4", seen[0].ToString());
    }

    /// <summary>A body with no port in it stalls rather than being answered.</summary>
    /// <remarks>
    /// Seven of the nine have no acknowledgement code anywhere, so there is nothing to refuse
    /// with: the message is logged and dropped, and the interlock stops - which is what the other
    /// end would see if the frame had been lost, and is a state the handshake already has messages
    /// for.
    /// </remarks>
    [Fact]
    public async Task A_step_with_no_port_in_it_raises_nothing()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var seen = Watch(pair.Receiver);

        using (var empty = new SecsMessage(4, 9, replyExpected: false) { Item = SecsItem.L() })
        {
            await pair.HostLink.PostAsync(empty, MaterialPair.Token);
        }

        await MaterialPair.WaitUntilAsync(
            () => pair.AskedOfEquipment.Contains(((byte)4, (byte)9)),
            "the unreadable step to reach the handler");

        Assert.Empty(seen);
    }
}
