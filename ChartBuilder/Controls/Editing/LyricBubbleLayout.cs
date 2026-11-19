using System;
using System.Collections.Generic;
using Avalonia;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>One lyric bubble to place: the bar of the note it belongs to, and how wide its text is.</summary>
    /// <param name="Note">The note's bar, on the screen.</param>
    /// <param name="LabelWidth">The width of the bubble's text, without its padding.</param>
    internal readonly record struct LyricBubbleInput(Rect Note, double LabelWidth);

    /// <summary>A bubble as placed: where it is, and the line down from it to its note.</summary>
    /// <param name="Index">The bubble's place in the inputs.</param>
    /// <param name="Level">How many levels above the lowest position it was moved, to clear the ones before it.</param>
    /// <param name="Bubble">The bubble's rectangle.</param>
    /// <param name="LineFrom">Where the line leaves the bubble, on its bottom edge.</param>
    /// <param name="LineTo">Where the line meets the note, on its top edge.</param>
    internal readonly record struct LyricBubble(int Index, int Level, Rect Bubble, Point LineFrom, Point LineTo);

    /// <summary>
    /// Where a vocal part's lyric bubbles go (charter-spec C3 item 7): each hovering above its note, joined to it by a line,
    /// moved up level by level where it would cover the bubble of a note before it, so lyrics that run together in time
    /// can still be read. One whose note is too near the lane's top for a bubble above it goes below the note instead. One that
    /// fits on no level is left out, as the lane is zoomed out.
    /// </summary>
    internal static class LyricBubbleLayout
    {
        /// <summary>
        /// Places the bubbles of <paramref name="inputs"/>, which are in the order of their notes in time, one after another,
        /// each on the lowest level that clears the ones already placed.
        /// </summary>
        /// <param name="inputs">The bubbles to place, in time order.</param>
        /// <param name="height">How tall a bubble is.</param>
        /// <param name="padding">The room either side of a bubble's text.</param>
        /// <param name="line">How long the line from the lowest level's bubble to its note is.</param>
        /// <param name="gapX">The least room left between bubbles along the time axis.</param>
        /// <param name="gapY">The least room left between bubbles across it, and between the levels.</param>
        /// <param name="levels">How many levels a bubble may be moved up to.</param>
        /// <param name="minTop">The least a bubble's top may be: the lane's top, which none go above.</param>
        /// <param name="maxBottom">The most a bubble's bottom may be: the lane's bottom. A bubble that has no room above its note goes below it, within this.</param>
        /// <returns>The bubbles placed, in the order of their inputs.</returns>
        public static List<LyricBubble> Place(
            ReadOnlySpan<LyricBubbleInput> inputs, double height, double padding, double line, double gapX, double gapY, int levels,
            double minTop = double.NegativeInfinity, double maxBottom = double.PositiveInfinity)
        {
            var placed = new List<LyricBubble>(inputs.Length);
            double widest = 0;
            for (int i = 0; i < inputs.Length; i++)
            {
                var note = inputs[i].Note;
                double width = inputs[i].LabelWidth + (padding * 2);
                double lineX = note.Left + Math.Min(4, note.Width / 2);
                lineX = Math.Clamp(lineX, note.Left + 1, note.Left + Math.Max(1, width - 1));

                for (int level = 0; level < levels; level++)
                {
                    double offset = line + (level * (height + gapY));
                    var bubble = new Rect(note.Left, note.Top - offset - height, width, height);
                    bool above = bubble.Top >= minTop;
                    if (!above)
                    {
                        // No room above the note: below it, if there's room there.
                        bubble = new Rect(note.Left, note.Bottom + offset, width, height);
                        if (bubble.Bottom > maxBottom)
                            break;
                    }

                    if (Clears(placed, bubble, widest, gapX, gapY))
                    {
                        var from = above ? new Point(lineX, bubble.Bottom) : new Point(lineX, bubble.Top);
                        var to = above ? new Point(lineX, note.Top) : new Point(lineX, note.Bottom);
                        placed.Add(new LyricBubble(i, level, bubble, from, to));
                        widest = Math.Max(widest, width);
                        break;
                    }
                }
            }

            return placed;
        }

        /// <summary>
        /// Where to start placing bubbles so that those from <paramref name="index"/> on go where they would were every bubble
        /// before them placed too, so they don't move as the notes before them are left out. A note whose left edge is at
        /// least <paramref name="reach"/> (the widest a bubble can be, and the gap after it) past the one before it is clear of
        /// every bubble before it, so its bubble goes on its lowest level whatever came before, and so on from it: walking
        /// back from <paramref name="index"/> to the first such note finds where placing can start.
        /// </summary>
        /// <param name="index">The first note whose bubble is wanted.</param>
        /// <param name="leftOf">The left edge of a note's bar, by its index; never less than the one before's.</param>
        /// <param name="reach">The widest a bubble can be, with the least gap along the time axis.</param>
        /// <param name="maxBack">The most notes to walk back over, so a lane zoomed far out isn't walked to its start.</param>
        /// <returns>The index of the note to start placing from: <paramref name="index"/> or before it.</returns>
        public static int IndependentFrom(int index, Func<int, double> leftOf, double reach, int maxBack)
        {
            int stop = Math.Max(0, index - maxBack);
            if (index <= stop)
                return Math.Max(0, index);
            double left = leftOf(index);
            int i = index;
            while (i > stop)
            {
                double before = leftOf(i - 1);
                if (left - before >= reach)
                    break;
                left = before;
                i--;
            }

            return i;
        }

        // Whether bubble keeps the gaps from every one placed. The placed are in time order, so their left edges don't go back:
        // once one is further left than the widest could reach, so are all before it.
        private static bool Clears(List<LyricBubble> placed, Rect bubble, double widest, double gapX, double gapY)
        {
            for (int i = placed.Count - 1; i >= 0; i--)
            {
                var other = placed[i].Bubble;
                if (other.Left + widest + gapX <= bubble.Left)
                    return true;
                if (other.Left < bubble.Right + gapX && bubble.Left < other.Right + gapX
                    && other.Top < bubble.Bottom + gapY && bubble.Top < other.Bottom + gapY)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
