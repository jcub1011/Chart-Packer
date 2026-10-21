using System.Numerics;

namespace ChartShared.Core.Charting.Difficulty;

/// <summary>
/// Drums' costs (docs/difficulty-model.md §14.1): the kick, and the hands, two of them, each hit given to the hand it
/// costs least.
/// </summary>
/// <remarks>
/// <para><b>Kick.</b> Its rate from the kick before, more past the interval double-kick footwork starts at, and more when
/// it's alone between the hands' hits rather than with one.</para>
/// <para><b>Hands.</b> Each hand's rate from its own hit before, more past its limit, and more for going to another pad,
/// across the kit, or between a tom and a cymbal. One pad is hit by whichever hand that costs least, so fast single
/// strokes alternate; two are hit by both, the left hand the leftmost, and more than two cost more again.</para>
/// <para>In a roll or trill lane, intervals count no shorter than <see cref="DifficultyParameters.LaneIntervalMs"/>.</para>
/// </remarks>
internal sealed class DrumCosts(DifficultyParameters parameters) : PhysicalCosts
{
    private readonly DifficultyParameters _p = parameters;
    private double _lastKick = double.NaN;
    private double _lastHands = double.NaN;
    private Hand _left = Hand.Unused, _right = Hand.Unused;

    public override (double First, double Second) Cost(in DifficultyNote note, double guarded, bool first)
    {
        double time = note.TimeMs;
        int pads = note.Lanes & DrumPads.Pads;
        double floor = note.Kind == NoteKind.Roll ? Math.Max(_p.LaneIntervalMs, _p.MinIntervalMs) : _p.MinIntervalMs;
        double kick = 0, hands = 0;
        if ((note.Lanes & DrumPads.Kick) != 0)
        {
            if (!double.IsNaN(_lastKick))
            {
                double interval = Math.Max(time - _lastKick, _p.MinIntervalMs);
                bool between = pads == 0 && !double.IsNaN(_lastHands) && time - _lastHands < _p.IndependenceWindowMs;
                kick = _p.StrumScale * Math.Pow(1000 / interval, _p.StrumSpeedPower)
                    * Math.Pow(Math.Max(1, _p.DoubleKickMs / interval), _p.DoubleKickPower)
                    * (between ? 1 + _p.KickIndependenceCost : 1);
            }

            _lastKick = time;
        }

        if (pads != 0)
        {
            hands = _p.NoteScale * HandsCost(pads, time, floor);
            _lastHands = time;
        }

        return (kick, hands);
    }

    public override long MoveToken(in DifficultyNote note, byte previousLanes, int iota, bool first) =>
        ((long)note.Lanes << 16) | ((long)note.Kind << 12) | (long)(iota + 1);

    // What the hands pay for pads, and where they are after.
    private double HandsCost(int pads, double time, double floor)
    {
        if (BitOperations.PopCount((uint)pads) == 1)
        {
            int bit = BitOperations.TrailingZeroCount(pads);
            double left = Cost(_left, bit, time, floor), right = Cost(_right, bit, time, floor);

            // The cheaper hand; of two alike, the one rested longer, then the right.
            bool useLeft = left < right || (left == right && RestedLonger(_left, _right));
            if (useLeft)
                _left = new Hand(time, bit);
            else
                _right = new Hand(time, bit);
            return useLeft ? left : right;
        }

        int lowest = -1, highest = -1;
        for (int b = 0; b < 7; b++)
        {
            if ((pads & (1 << b)) == 0)
                continue;
            if (lowest < 0 || Position(b) < Position(lowest))
                lowest = b;
            if (highest < 0 || Position(b) >= Position(highest))
                highest = b;
        }

        double cost = Cost(_left, lowest, time, floor) + Cost(_right, highest, time, floor);
        int extra = BitOperations.PopCount((uint)pads) - 2;
        if (extra > 0 && !double.IsNaN(_lastHands))
            cost += Math.Pow(1000 / Math.Max(time - _lastHands, floor), _p.NoteSpeedPower) * _p.ExtraPadCost * extra;

        _left = new Hand(time, lowest);
        _right = new Hand(time, highest);
        return cost;
    }

    // What hand pays to hit pad at time: none for its first hit.
    private double Cost(Hand hand, int pad, double time, double floor)
    {
        if (hand.Pad < 0)
            return 0;
        double interval = Math.Max(time - hand.Time, floor);
        double rate = Math.Pow(1000 / interval, _p.NoteSpeedPower) * Math.Pow(Math.Max(1, _p.HandLimitMs / interval), _p.HandLimitPower);
        double move = 1 + (_p.PadMoveCost * Math.Abs(Position(pad) - Position(hand.Pad)));
        if (pad != hand.Pad)
            move += _p.PadChangeCost;
        if (DrumPads.IsCymbal(pad, _p.Instrument) != DrumPads.IsCymbal(hand.Pad, _p.Instrument))
            move += _p.CymbalRowCost;
        return rate * move;
    }

    private double Position(int pad) => DrumPads.PositionOf(pad, _p.Instrument);

    // Whether a has rested longer than b: a hand that hasn't hit has rested longest.
    private static bool RestedLonger(Hand a, Hand b) => b.Pad >= 0 && (a.Pad < 0 || a.Time < b.Time);

    // A hand's last hit: when, and which pad's bit; -1 for none yet.
    private readonly record struct Hand(double Time, int Pad)
    {
        public static Hand Unused { get; } = new(double.NaN, -1);
    }
}
