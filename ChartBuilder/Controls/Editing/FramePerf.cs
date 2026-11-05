using System;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using ChartShared.UI.Controls;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// A readout of what an editor lane costs while the song plays, for finding where a slow lane's time goes. It is off unless the
    /// environment variable <c>CHARTBUILDER_PERF</c> is <c>1</c>; then each lane shows a few lines in its top left corner, once a
    /// second, and appends them to <c>chartbuilder-perf.log</c> in the temporary folder, to be sent on.
    /// </summary>
    /// <remarks>
    /// It measures the UI thread, which is where the lane is drawn: the time the song's moving takes, and the time each layer takes to
    /// record its drawing. It cannot see the thread that puts that drawing on the screen: that is what is left over. The frames per
    /// second are the rate the screen asks for a frame at, which that thread holds back when it can't keep up, so a lane that takes a
    /// few milliseconds here and still runs slowly is waiting on the graphics, and one that takes most of the frame is waiting on this.
    /// </remarks>
    internal sealed class FramePerf
    {
        /// <summary>What is timed: each layer's drawing, and the notes standing up on a perspective highway, which the host draws.</summary>
        public enum Slot
        {
            Song,
            Strike,
            Notes,
            Playtest,
            Overlay,
            Gems,
            Marks,
        }

        /// <summary>What can be switched off while the readout is on, to see what a lane's drawing costs: <c>Ctrl+Shift+F1</c> to <c>F7</c> in the order here, and <c>F8</c> for all on again.</summary>
        [Flags]
        public enum Part
        {
            None = 0,
            Audio = 1,
            LaneFill = 2,
            BeatLines = 4,
            Markings = 8,
            Tails = 16,
            Strike = 32,
            StandingNotes = 64,

            /// <summary>The clip rectangles drawn on the plane, round the waveform, the tails and the strike line.</summary>
            PlaneClips = 128,

            /// <summary>Switched "off", the canvas's own clip to its bounds put back on a tilted lane, as it was: a tilt makes it a clip to a slanted quadrilateral.</summary>
            CanvasClip = 256,
        }

        private const Part Content = Part.Audio | Part.LaneFill | Part.BeatLines | Part.Markings | Part.Tails | Part.Strike | Part.StandingNotes;

        private const double WarmSeconds = 1.5;
        private const double MeasureSeconds = 3;

        private static readonly (string Name, Part Off)[] Steps =
        [
            ("warm up (ignore)", Part.None),
            ("all drawn", Part.None),
            ("no audio", Part.Audio),
            ("no lane fill", Part.LaneFill),
            ("no beat lines", Part.BeatLines),
            ("no markings", Part.Markings),
            ("no tails", Part.Tails),
            ("no strike", Part.Strike),
            ("no standing notes", Part.StandingNotes),
            ("canvas clip back (as was)", Part.CanvasClip),
            ("no plane clips", Part.PlaneClips),
            ("no content", Content),
            ("nothing, just the tilt", Content | Part.PlaneClips),
            ("all drawn (again)", Part.None),
        ];

        private static int s_off;

        /// <summary>Whether a benchmark is running, which draws what the lanes draw while the song plays, as it plays, though it does not.</summary>
        public static bool SimulatesPlaying { get; private set; }

        private const double WindowSeconds = 1;
        private const string VariableName = "CHARTBUILDER_PERF";

        private static readonly int Slots = Enum.GetValues<Slot>().Length;
        private static readonly string[] SlotNames = ["song", "strike", "notes", "playtest", "overlay", "gems", "marks"];
        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "chartbuilder-perf.log");
        private static readonly Typeface Face = new("Consolas, Cascadia Mono, Courier New, monospace");
        private static readonly IBrush TextBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        private static readonly IBrush BadgeBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xC8, 0x00, 0x00, 0x00));

        private readonly Func<string> _header;
        private readonly double[] _sum = new double[Slots];
        private readonly double[] _max = new double[Slots];
        private readonly int[] _count = new int[Slots];

        private long _windowStart;
        private long _lastFrame;
        private int _frames;
        private double _maxInterval;
        private double _followSum;
        private double _followMax;
        private long _holdUntil;
        private int[] _collections = [0, 0, 0];
        private long _tilesBuilt;
        private string _renderer = "renderer ?";
        private bool _benchmarking;
        private string? _benchmark;
        private TextLayout? _text;
        private string _lines = "";

        public FramePerf(Func<string> header)
        {
            _header = header;
            _collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            _tilesBuilt = LaneWaveformPainter.TilesBuilt;
        }

        /// <summary>Whether the readout is asked for.</summary>
        public static bool IsEnabled { get; } = Environment.GetEnvironmentVariable(VariableName) is "1" or "true";

        /// <summary>Finds out what the screen is drawn with, a graphics card or the processor, which is far slower for a tilted lane.</summary>
        public async void Probe(Compositor? compositor)
        {
            if (compositor is null)
                return;

            try
            {
                _renderer = await compositor.TryGetCompositionGpuInterop() is null ? "SOFTWARE" : "GPU";
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                _renderer = "renderer unknown";
            }
        }

        /// <summary>Whether <paramref name="part"/> is switched off: never when the readout is not asked for.</summary>
        public static bool IsOff(Part part) => (s_off & (int)part) != 0;

        /// <summary>Switches the part a function key stands for, F1 to F9, off or on, or F11 puts them all on: whether <paramref name="key"/> was one.</summary>
        public static bool Toggle(Avalonia.Input.Key key)
        {
            if (key == Avalonia.Input.Key.F11)
            {
                s_off = 0;
                return true;
            }

            int index = key - Avalonia.Input.Key.F1;
            if (index is < 0 or > 8)
                return false;

            s_off ^= 1 << index;
            return true;
        }

        /// <summary>
        /// Plays the same stretch of the song at its own speed once for each of the settings above, in turn, and says how fast the screen
        /// was asked for frames in each: what the parts cost without the song's position, which sets how much there is to draw, deciding it.
        /// </summary>
        /// <param name="top">What asks for the frames.</param>
        /// <param name="start">Where in the song, in seconds, each setting starts.</param>
        /// <param name="follow">Takes the view to a place in the song, in seconds, and has the lane drawn.</param>
        /// <param name="apply">Has the lane take up the settings: its clip, and its layers drawn again.</param>
        public void Benchmark(Avalonia.Controls.TopLevel top, double start, Action<double> follow, Action apply)
        {
            if (_benchmarking)
                return;

            _benchmarking = true;
            SimulatesPlaying = true;
            var results = new List<string>();
            int step = -1;
            TimeSpan stepStart = default, last = default;
            int frames = 0;
            double maxGap = 0, measured = 0;

            void Finish()
            {
                s_off = 0;
                follow(start);
                apply();
                _benchmarking = false;
                SimulatesPlaying = false;
                _holdUntil = Stopwatch.GetTimestamp() + (120 * Stopwatch.Frequency);
                results.Insert(0, string.Create(CultureInfo.InvariantCulture, $"BENCHMARK {_header()}  [{_renderer}]"));
                _benchmark = string.Join(Environment.NewLine, results);
                _text?.Dispose();
                _text = new TextLayout(_benchmark, Face, 10, TextBrush);
                Append(_benchmark);
                Rolled?.Invoke();
            }

            void Begin(TimeSpan time)
            {
                s_off = (int)Steps[step].Off;
                stepStart = time;
                last = default;
                frames = 0;
                maxGap = 0;
                measured = 0;
                apply();
                follow(start);
            }

            void OnFrame(TimeSpan time)
            {
                if (step < 0)
                {
                    step = 0;
                    Begin(time);
                }
                else
                {
                    double t = (time - stepStart).TotalSeconds;
                    if (t >= WarmSeconds && last != default)
                    {
                        double gap = (time - last).TotalMilliseconds;
                        frames++;
                        measured += gap;
                        maxGap = Math.Max(maxGap, gap);
                    }

                    if (t >= WarmSeconds + MeasureSeconds)
                    {
                        results.Add(string.Create(CultureInfo.InvariantCulture, $"{Steps[step].Name,-24} {(frames == 0 ? 0 : 1000 * frames / measured),5:F1} fps  every {(frames == 0 ? 0 : measured / frames),5:F1} ms  worst {maxGap,4:F0} ms"));
                        step++;
                        if (step >= Steps.Length)
                        {
                            Finish();
                            return;
                        }

                        _text?.Dispose();
                        _text = new TextLayout(string.Join(Environment.NewLine, results.Prepend("BENCHMARK running, leave the mouse alone...")), Face, 10, TextBrush);
                        Rolled?.Invoke();
                        Begin(time);
                    }
                    else
                    {
                        follow(start + t);
                    }
                }

                last = time;
                top.RequestAnimationFrame(OnFrame);
            }

            top.RequestAnimationFrame(OnFrame);
        }

        /// <summary>Raised when a second's figures are in and the readout is to be drawn again.</summary>
        public event Action? Rolled;

        /// <summary>The moment, for <see cref="Add"/>; 0 for a lane with no readout.</summary>
        public static long Now(FramePerf? perf) => perf is null ? 0 : Stopwatch.GetTimestamp();

        /// <summary>A frame of the song playing is begun: the screen asked for it.</summary>
        public void Frame()
        {
            long now = Stopwatch.GetTimestamp();
            if (_windowStart == 0)
                _windowStart = now;

            if (_lastFrame != 0)
                _maxInterval = Math.Max(_maxInterval, Milliseconds(now - _lastFrame));
            _lastFrame = now;
            _frames++;

            if (Milliseconds(now - _windowStart) >= WindowSeconds * 1000)
                Roll(now);
        }

        /// <summary>The song's moving, from <paramref name="start"/>, is done.</summary>
        public void Follow(long start)
        {
            double ms = Milliseconds(Stopwatch.GetTimestamp() - start);
            _followSum += ms;
            _followMax = Math.Max(_followMax, ms);
        }

        /// <summary>A layer's drawing, from <paramref name="start"/>, is done.</summary>
        public void Add(Slot slot, long start)
        {
            double ms = Milliseconds(Stopwatch.GetTimestamp() - start);
            int i = (int)slot;
            _sum[i] += ms;
            _count[i]++;
            _max[i] = Math.Max(_max[i], ms);
        }

        /// <summary>Draws the last second's figures, as a badge with its top left corner at <paramref name="at"/>, or its bottom left when <paramref name="upwards"/>.</summary>
        public void Draw(DrawingContext context, Point at, bool upwards = false)
        {
            if (_text is null)
                return;

            if (upwards)
                at = new Point(at.X, at.Y - _text.Height);

            context.FillRectangle(BadgeBrush, new Rect(at, new Size(_text.Width, _text.Height)).Inflate(4));
            _text.Draw(context, at);
        }

        private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private void Roll(long now)
        {
            double seconds = Milliseconds(now - _windowStart) / 1000;
            var culture = CultureInfo.InvariantCulture;
            if (_benchmarking || now < _holdUntil)
            {
                ResetWindow(now);
                return;
            }

            var text = new StringBuilder();
            text.AppendLine($"{_header()}  [{_renderer}]");
            text.AppendLine(s_off == 0 ? "all drawn (Ctrl+Shift+F1..F9 switch parts off, F11 all on, F12 benchmark)" : $"OFF: {(Part)s_off}");
            text.AppendLine(string.Create(culture, $"fps {_frames / seconds:F1}  every {1000 * seconds / _frames:F1}ms  worst {_maxInterval:F1}ms"));

            double drawn = 0;
            for (int i = 0; i < Slots; i++)
                drawn += _sum[i];
            text.AppendLine(string.Create(culture, $"UI per frame {(_followSum + drawn) / _frames:F2}ms  (moving {_followSum / _frames:F2}, max {_followMax:F1})"));

            var slots = new StringBuilder();
            int shown = 0;
            for (int i = 0; i < Slots; i++)
            {
                if (_count[i] == 0)
                    continue;
                if (slots.Length > 0)
                    slots.Append(shown++ % 2 == 0 ? Environment.NewLine : "  ");
                else
                    shown = 1;
                slots.Append(string.Create(culture, $"{SlotNames[i]} {_sum[i] / _count[i]:F2}ms x{_count[i] / seconds:F0} max {_max[i]:F1}"));
            }

            text.AppendLine(slots.ToString());

            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long built = LaneWaveformPainter.TilesBuilt;
            text.Append(string.Create(culture, $"GC {g0 - _collections[0]}/{g1 - _collections[1]}/{g2 - _collections[2]}  waveform built {built - _tilesBuilt} kept {LaneWaveformPainter.TileCount}"));
            _collections = [g0, g1, g2];
            _tilesBuilt = built;

            _lines = text.ToString();
            _text?.Dispose();
            _text = new TextLayout(_lines, Face, 10, TextBrush);
            Append(_lines);

            ResetWindow(now);
            Rolled?.Invoke();
        }

        private void ResetWindow(long now)
        {
            Array.Clear(_sum);
            Array.Clear(_max);
            Array.Clear(_count);
            _frames = 0;
            _maxInterval = 0;
            _followSum = 0;
            _followMax = 0;
            _windowStart = now;
        }

        private static void Append(string lines)
        {
            try
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {lines.ReplaceLineEndings(" | ")}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A readout that can't be kept is still shown.
            }
        }
    }
}
