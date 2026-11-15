// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Hsms.UnitTests;

/// <summary>
/// What a caller sees when it sends into a link that is being torn down underneath it.
/// </summary>
/// <remarks>
/// <para>
/// A send runs on the caller's thread and the link's lifetime belongs to the actor, so there is a
/// window: the caller takes the live session, the link ends, and the caller then writes its frame.
/// Everything in that window has to come out as a <see cref="SecsException"/> - the link went away
/// - or as cancellation. Anything else is this library's own plumbing escaping into an
/// application.
/// </para>
/// <para>
/// <strong>This is a stress test, and it is honest about what that means.</strong> It opens the
/// window many times rather than once, because the window cannot be opened deliberately through
/// the public surface. A run that happens not to hit it passes, so the test can give a false pass -
/// what it cannot do is give a false failure, because the only thing it asserts is that one
/// specific wrong exception never arrives.
/// </para>
/// </remarks>
public class HsmsDisposalRaceTests
{
    [Fact]
    public async Task A_send_racing_the_link_closing_is_a_link_failure_and_never_a_disposed_object()
    {
        List<string> unexpected = [];
        var attempts = 0;
        var sends = 0;

        for (var attempt = 0; attempt < 60; attempt++)
        {
            var link = TestLink.Start(active: true);

            await link.SelectAsync();

            attempts++;

            // A caller posting as fast as it can while the link goes away underneath it.
            var posting = Task.Run(async () =>
            {
                for (var i = 0; i < 40; i++)
                {
                    using var message = new SecsMessage(1, 1, replyExpected: false);

                    try
                    {
                        await link.Connection
                            .PostAsync(message, TestContext.Current.CancellationToken)
                            .ConfigureAwait(false);
                        Interlocked.Increment(ref sends);
                    }
                    catch (SecsException)
                    {
                        // The honest failure: there is no link, or it closed before the frame went.
                    }
                    catch (OperationCanceledException)
                    {
                        // Also fine: the link's own cancellation reached the write.
                    }
                    catch (ObjectDisposedException exception)
                        when (exception.ObjectName == typeof(HsmsConnection).FullName)
                    {
                        // Posting to a connection that has already been disposed is the caller's
                        // own mistake, and this is .NET's answer to it. The one this test exists
                        // for named something *inside* the connection - a SemaphoreSlim - which is
                        // plumbing escaping into an application rather than a contract.
                    }
                    catch (Exception exception)
                    {
                        lock (unexpected)
                        {
                            unexpected.Add($"{exception.GetType().Name}: {exception.Message}");
                        }

                        return;
                    }
                }
            },
            TestContext.Current.CancellationToken);

            await link.DisposeAsync();
            await posting;

            if (unexpected.Count > 0)
            {
                break;
            }
        }

        // Named in full in the failure, because "a race failed" is not something anybody can act
        // on - and a collection assertion truncates exactly the part that says which object.
        if (unexpected.Count > 0)
        {
            Assert.Fail(string.Join(Environment.NewLine, unexpected));
        }

        // And the test actually did something: a run where every post failed before the link was
        // even selected would pass this file for the wrong reason.
        Assert.True(attempts > 0, "no attempt completed");
        Assert.True(sends > 0, $"{attempts} attempt(s) and not one post succeeded, so the window was never open");
    }
}
