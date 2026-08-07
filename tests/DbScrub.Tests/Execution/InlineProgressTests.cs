using System.Text;
using DbScrub.Core.Execution;
using Xunit;

namespace DbScrub.Tests.Execution;

/// <summary>
/// Pins the one property that matters about progress reporting: it happens
/// NOW, on the calling thread, before Report returns.
///
/// These tests exist because `new Progress&lt;T&gt;(...)` is the obvious thing to
/// write here and it is wrong (DECISIONS.md D31). Reverting to it would leave
/// every other test passing and reintroduce an intermittent failure at roughly
/// 4% of runs — so the property is asserted directly rather than left to be
/// rediscovered.
/// </summary>
public class InlineProgressTests
{
    [Fact]
    public void TheHandlerRunsOnTheCallingThread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var observed = -1;

        IProgress<string> progress = new InlineProgress<string>(
            _ => observed = Environment.CurrentManagedThreadId);

        progress.Report("anything");

        Assert.Equal(caller, observed);
    }

    [Fact]
    public void EveryReportHasLandedBeforeReportReturns()
    {
        // The actual failure this prevents: a writer still being written to
        // while something else reads it. With Progress<T> the writes are queued
        // to the thread pool and this buffer would usually still be empty here.
        var buffer = new StringBuilder();

        IProgress<string> progress = new InlineProgress<string>(m => buffer.Append(m));

        for (var i = 0; i < 500; i++)
        {
            progress.Report("x");
        }

        // No delay, no wait, no synchronisation. If reporting were asynchronous
        // this read would be a race — which is exactly the bug.
        Assert.Equal(500, buffer.Length);
    }

    [Fact]
    public void OrderIsPreserved()
    {
        // Out-of-order progress is the other half of D31: an operator reading
        // the transcript before approving a destructive run has to be able to
        // trust that what printed first happened first.
        var seen = new List<int>();

        IProgress<int> progress = new InlineProgress<int>(seen.Add);

        for (var i = 0; i < 100; i++)
        {
            progress.Report(i);
        }

        Assert.Equal(Enumerable.Range(0, 100), seen);
    }

    [Fact]
    public void ANullHandlerIsRefusedAtConstruction()
    {
        Assert.Throws<ArgumentNullException>(() => new InlineProgress<string>(null!));
    }
}
