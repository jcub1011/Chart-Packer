using ChartShared.Audio.Playback;

namespace ChartShared.Core.Tests.Audio;

/// <summary>A file on a cloud or network drive that fails a read for a moment, and is opened again.</summary>
[TestClass]
public sealed class ReopeningStreamTests
{
    private static readonly byte[] Content = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();

    private static readonly TimeSpan[] ThreeRetries = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(3)];

    [TestMethod]
    public void ReadsAndSeeksLikeTheStreamItOpens()
    {
        var source = new Source();
        using var stream = source.Reopening();

        Assert.AreEqual(100, stream.Length);
        byte[] first = new byte[10];
        Assert.AreEqual(10, stream.Read(first, 0, 10));
        CollectionAssert.AreEqual(Content[..10], first);

        stream.Seek(-5, SeekOrigin.End);
        byte[] last = new byte[10];
        Assert.AreEqual(5, stream.Read(last, 0, 10));
        CollectionAssert.AreEqual(Content[95..], last[..5]);
        Assert.AreEqual(0, stream.Read(last, 0, 10));

        Assert.AreEqual(1, source.Opens);
    }

    [TestMethod]
    public void ReadsAgainFromTheSamePlaceAfterAFailedRead()
    {
        var source = new Source();
        var waits = new List<TimeSpan>();
        using var stream = source.Reopening(waits.Add);
        byte[] buffer = new byte[10];
        stream.ReadExactly(buffer);

        source.FailingReads = 2;
        Assert.AreEqual(10, stream.Read(buffer, 0, 10));

        CollectionAssert.AreEqual(Content[10..20], buffer);
        Assert.AreEqual(10 + 10, stream.Position);
        Assert.AreEqual(3, source.Opens, "the first open, and one for each failure");
        CollectionAssert.AreEqual(ThreeRetries[..2], waits);
        Assert.AreEqual(2, source.Disposed, "each failed stream is let go of");
    }

    [TestMethod]
    public void ReadsFromWhereItWasSoughtAfterAFailedRead()
    {
        var source = new Source();
        using var stream = source.Reopening();
        source.FailingReads = 1;

        stream.Seek(60, SeekOrigin.Begin);
        byte[] buffer = new byte[4];
        Assert.AreEqual(4, stream.Read(buffer, 0, 4));

        CollectionAssert.AreEqual(Content[60..64], buffer);
    }

    [TestMethod]
    public void GivesUpWithTheDrivesErrorOnceItHasRetriedEveryTime()
    {
        var source = new Source { FailingReads = int.MaxValue };
        var waits = new List<TimeSpan>();
        using var stream = source.Reopening(waits.Add);

        var error = Assert.Throws<IOException>(() => stream.Read(new byte[10], 0, 10));

        StringAssert.Contains(error.Message, "no such device");
        CollectionAssert.AreEqual(ThreeRetries, waits);
        Assert.AreEqual(4, source.Opens, "the first open and three retries");
    }

    [TestMethod]
    public void CountsAFailedReopenAsAnAttempt()
    {
        var source = new Source();
        using var stream = source.Reopening();
        source.FailingReads = 1;
        source.FailingOpens = 2;

        byte[] buffer = new byte[10];
        Assert.AreEqual(10, stream.Read(buffer, 0, 10));

        CollectionAssert.AreEqual(Content[..10], buffer);
    }

    [TestMethod]
    public void RefusesAFileThatChangedItsLength()
    {
        var source = new Source();
        using var stream = source.Reopening();
        source.FailingReads = 1;
        source.Length = 50;

        Assert.Throws<IOException>(() => stream.Read(new byte[10], 0, 10));
    }

    [TestMethod]
    public void DoesNotRetryTheFirstOpen()
    {
        var source = new Source { FailingOpens = 1 };

        Assert.Throws<IOException>(() => source.Reopening());
        Assert.AreEqual(1, source.Opens);
    }

    [TestMethod]
    public void StopsRetryingOnceDisposed()
    {
        var source = new Source { FailingReads = int.MaxValue };
        ReopeningStream? stream = null;
        stream = source.Reopening(_ => stream!.Dispose());

        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[10], 0, 10));
        Assert.AreEqual(1, source.Opens, "nothing is opened for a stream that's gone");
    }

    [TestMethod]
    public void CannotBeWrittenTo()
    {
        using var stream = new Source().Reopening();

        Assert.IsFalse(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
    }

    // Opens a stream over Content, on request failing the next few reads or opens as a drive that's dropped out does.
    private sealed class Source
    {
        public int Opens { get; private set; }

        public int Disposed { get; private set; }

        public int FailingReads { get; set; }

        public int FailingOpens { get; set; }

        public long Length { get; set; } = Content.Length;

        public ReopeningStream Reopening(Action<TimeSpan>? wait = null) => new(Open, ThreeRetries, wait ?? (_ => { }));

        private Stream Open()
        {
            Opens++;
            if (FailingOpens > 0)
            {
                FailingOpens--;
                throw new IOException("A device which does not exist was specified: no such device.");
            }

            return new Flaky(this, new MemoryStream(Content.Take((int)Length).ToArray()));
        }

        private sealed class Flaky(Source owner, MemoryStream inner) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => true;

            public override bool CanWrite => false;

            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => inner.Position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (owner.FailingReads > 0)
                {
                    owner.FailingReads--;
                    throw new IOException("A device which does not exist was specified: no such device.");
                }

                return inner.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

            public override void Flush()
            {
            }

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    owner.Disposed++;
                base.Dispose(disposing);
            }
        }
    }
}
