using ChartShared.Core.Charts;

namespace ChartShared.Core.Tests.Charts;

[TestClass]
public sealed class SustainCutoffTests
{
    private static readonly HashSet<int> Green = [96];

    [TestMethod]
    public void ASecondNoteOnWhileOneIsHeldIsIgnored()
    {
        // As the reader has it: one note, from 0 to the first note-off; the second note-on and the last note-off pair with nothing.
        List<MidiMessage> messages = [On(0, 96), On(100, 96), Off(200, 96), Off(300, 96)];

        var note = SustainCutoff.NotesOf(messages, Green).Single();

        Assert.AreEqual((0L, 200L), (messages[note.On].Tick, messages[note.Off].Tick));
    }

    [TestMethod]
    public void ANoteOffEndsOnlyTheNoteOnItsChannel()
    {
        List<MidiMessage> messages = [On(0, 96), Off(60, 96, channel: 1), Off(200, 96)];

        var note = SustainCutoff.NotesOf(messages, Green).Single();

        Assert.AreEqual(200L, messages[note.Off].Tick);
    }

    [TestMethod]
    [DataRow(480, null, null)]
    [DataRow(480, 480L, 160L)]
    [DataRow(480, 161L, 160L)]
    [DataRow(480, 160L, 159L)]
    [DataRow(480, 10L, 9L)]
    [DataRow(480, 9L, 8L)]
    [DataRow(24, 10L, 8L)]
    public void ChartBuilderCutoffKeepsTheShortestSustainAndCapsAtAThird(int resolution, long? shortest, long? expected) =>
        Assert.AreEqual(expected, SustainCutoff.ForChartBuilder(resolution, shortest));

    [TestMethod]
    public void ASustainIsntCutShortByANoteOnTheSameTickOnAnotherChannel()
    {
        List<MidiMessage> messages = [On(0, 96), On(0, 96, channel: 1), Off(50, 96), Off(50, 96, channel: 1)];
        var notes = SustainCutoff.NotesOf(messages, Green);
        Assert.HasCount(2, notes);

        SustainCutoff.Normalize(messages, notes, _ => true, 160);

        Assert.AreSequenceEqual([161L, 161L], [.. notes.Select(n => messages[n.Off].Tick)]);
    }

    private static MidiMessage On(long tick, int pitch, int channel = 0) => new(tick, [(byte)(0x90 | channel), (byte)pitch, 100]);

    private static MidiMessage Off(long tick, int pitch, int channel = 0) => new(tick, [(byte)(0x80 | channel), (byte)pitch, 0]);
}
