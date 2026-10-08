using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// A row of the Sections tab (charter-spec C8 items 3, 5 and 6): a text event, where it is, and how to go there,
    /// rename it, move it or delete it. A section is renamed by its name; a free text event's text is changed as it's
    /// written; a song event is what its text says, so it's moved or deleted but not renamed.
    /// </summary>
    public sealed class EventItem : ObservableObject
    {
        private readonly SectionsViewModel _owner;
        private string _name;
        private string _tickText;
        private string _where;

        internal EventItem(SectionsViewModel owner, EventRow row, string where)
        {
            _owner = owner;
            Row = row;
            _name = NameOf(row);
            _tickText = TickOf(row);
            _where = where;
            GoToCommand = new RelayCommand(() => owner.GoTo(this));
            DeleteCommand = new RelayCommand(() => owner.Delete(this));
        }

        /// <summary>The event, the track it's on, and what it is.</summary>
        internal EventRow Row { get; private set; }

        /// <summary>Whether its fields are filled again at the next refresh whatever's typed in them, after an edit of its own.</summary>
        internal bool IsStale { get; set; }

        public bool IsSection => Row.Kind == EventRowKind.Section;

        public bool IsSongEvent => Row.Kind == EventRowKind.SongEvent;

        public bool IsText => Row.Kind == EventRowKind.Text;

        /// <summary>A song event in words, e.g. "Music start"; empty for another event.</summary>
        public string Label => Row.SongEvent is { } kind ? SectionsViewModel.SongEventName(kind) : "";

        /// <summary>A song event's text, as it's written, for its label's tooltip.</summary>
        public string WrittenAs => string.Format(CultureInfo.CurrentCulture, Strings.SectionsSongEventToolTipFormat, Row.Event.Text);

        /// <summary>What's typed to rename it: a section's name, or another event's text as it's written.</summary>
        public string Name
        {
            get => _name;
            set
            {
                if (value == _name)
                    return;
                _name = value;
                OnPropertyChanged();
                _owner.Rename(this, value);
            }
        }

        /// <summary>Its tick, which is typed to move it.</summary>
        public string TickText
        {
            get => _tickText;
            set
            {
                if (value == _tickText)
                    return;
                _tickText = value;
                OnPropertyChanged();
                _owner.Move(this, value);
            }
        }

        /// <summary>Where it is: its time in the song, and its measure and beat.</summary>
        public string Where
        {
            get => _where;
            private set => SetProperty(ref _where, value);
        }

        public IRelayCommand GoToCommand { get; }

        public IRelayCommand DeleteCommand { get; }

        /// <summary>Takes up the event as an edit of its own has made it, so the next refresh finds this row for it.</summary>
        internal void Rekey(TextEvent now) => Row = Row with { Event = now };

        /// <summary>
        /// Takes up <paramref name="row"/>, the event as its track now has it. A field is filled again only when what it
        /// shows has changed, or after an edit of its own, so what's being typed in it isn't lost to someone else's edit.
        /// </summary>
        internal void Update(EventRow row, string where)
        {
            var old = Row;
            bool force = IsStale;
            Row = row;
            IsStale = false;
            if (old.Kind != row.Kind)
            {
                OnPropertyChanged(nameof(IsSection));
                OnPropertyChanged(nameof(IsSongEvent));
                OnPropertyChanged(nameof(IsText));
                OnPropertyChanged(nameof(Label));
            }

            if (old.Event.Text != row.Event.Text)
                OnPropertyChanged(nameof(WrittenAs));

            string name = NameOf(row);
            if (force || name != NameOf(old))
            {
                _name = name;
                OnPropertyChanged(nameof(Name));
            }

            string tick = TickOf(row);
            if (force || tick != TickOf(old))
            {
                _tickText = tick;
                OnPropertyChanged(nameof(TickText));
            }

            Where = where;
        }

        private static string NameOf(EventRow row) => row.SectionName ?? row.Event.Text;

        private static string TickOf(EventRow row) => row.Event.Tick.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The Sections tab (charter-spec C8 items 3, 5 and 6): every text event of <c>EVENTS</c> in time order, sections,
    /// song events and free text alike, then the focused part's own text events, each with its time and its measure and
    /// beat. Each can be gone to, renamed, moved by its tick and deleted, as one undoable step, and <b>Insert section</b>
    /// and <b>Insert event</b> add them at the cursor, on the grid.
    /// </summary>
    /// <remarks>
    /// The rows follow the chart (an edit, an undo or redo, or a drag's preview), the song's delay and the focus. They're
    /// worked out once the changes of a moment are in, and a row whose event is still there is kept, with whatever's typed
    /// in its fields.
    /// </remarks>
    public sealed partial class SectionsViewModel : ViewModelBase, IDisposable
    {
        private readonly ChartEditorViewModel _editor;
        private ChartInstrument? _part;
        private ChartInstrument? _focused;
        private bool _scheduled;
        private bool _disposed;

        // A row inserted to be renamed, whose name is edited once it's listed.
        private (ChartInstrument? Part, TextEvent Event)? _editNext;

        // Each tick's time and measure and beat, kept while the tempo map and the delay are the ones they were worked out
        // with, so a refresh for an edit that moves nothing in time, such as a phrase dragged, works none out again.
        private readonly Dictionary<long, string> _whereByTick = [];
        private TempoMap? _whereMap;
        private TimeSpan _whereDelay;

        public SectionsViewModel(ChartEditorViewModel editor)
        {
            _editor = editor;
            InsertSectionMenu =
            [
                .. SectionEvents.CommonNames.Select(name => LaneMenuItem.Choice(name, false, () => Session.Tools.InsertSection(name))),
                LaneMenuItem.Choice(Strings.SectionsCustomSection, false, InsertCustomSection),
            ];
            InsertEventMenu = EventMenuOf(null);
            Session.ChartChanged += OnChartChanged;
            Session.StateChanged += OnSessionState;
            Session.SongSettingsChanged += OnSongSettingsChanged;
            _focused = Session.Focused?.Instrument;
            Refresh();
        }

        /// <summary>EVENTS' text events, in time order.</summary>
        public ObservableCollection<EventItem> Events { get; } = [];

        /// <summary>The focused part's own text events, in time order.</summary>
        public ObservableCollection<EventItem> PartEvents { get; } = [];

        [ObservableProperty]
        public partial bool HasEvents { get; private set; }

        [ObservableProperty]
        public partial bool HasPartEvents { get; private set; }

        /// <summary>The heading over the focused part's text events, e.g. "Text events on Drums".</summary>
        [ObservableProperty]
        public partial string PartHeading { get; private set; } = "";

        /// <summary><b>Insert section</b>'s menu: the common names, then Custom….</summary>
        public IReadOnlyList<LaneMenuItem> InsertSectionMenu { get; }

        /// <summary><b>Insert event</b>'s menu: the song events, a text event, and one on the focused part's track.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<LaneMenuItem> InsertEventMenu { get; private set; }

        /// <summary>Raised when a row inserted to be renamed is listed, for the view to put the keyboard in its name.</summary>
        public event EventHandler<EventItem>? EditRequested;

        private EditorSession Session => _editor.Session;

        /// <summary>A song event, in words: Coda, Music start, Music end, End.</summary>
        internal static string SongEventName(SongEvent songEvent) => songEvent switch
        {
            SongEvent.Coda => Strings.SongEventCoda,
            SongEvent.MusicStart => Strings.SongEventMusicStart,
            SongEvent.MusicEnd => Strings.SongEventMusicEnd,
            _ => Strings.SongEventEnd,
        };

        public void Dispose()
        {
            _disposed = true;
            Session.ChartChanged -= OnChartChanged;
            Session.StateChanged -= OnSessionState;
            Session.SongSettingsChanged -= OnSongSettingsChanged;
        }

        /// <summary>Moves the cursor to <paramref name="item"/>'s event. The focus stays where it is.</summary>
        internal void GoTo(EventItem item) => Session.MoveCursorTo(item.Row.Event.Tick);

        internal void Delete(EventItem item) => Session.Tools.DeleteEvent(item.Row.Part, item.Row.Event);

        /// <summary>Renames <paramref name="item"/>'s event: a section by its name, another event by its whole text.</summary>
        internal void Rename(EventItem item, string text)
        {
            var row = item.Row;
            if (row.Kind == EventRowKind.SongEvent)
                return;
            var now = row.Kind == EventRowKind.Section
                ? Session.Tools.RenameSection(row.Event, text)
                : Session.Tools.ChangeEventText(row.Part, row.Event, text);
            Edited(item, now);
        }

        /// <summary>Moves <paramref name="item"/>'s event to the tick typed; anything else typed is put back.</summary>
        internal void Move(EventItem item, string text)
        {
            TextEvent? now = null;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out long tick))
                now = Session.Tools.MoveEvent(item.Row.Part, item.Row.Event, tick);
            Edited(item, now);
        }

        // Done or not, the row shows the event as it then is.
        private void Edited(EventItem item, TextEvent? now)
        {
            if (now is { } changed)
                item.Rekey(changed);
            item.IsStale = true;
            Schedule();
        }

        private void InsertCustomSection()
        {
            if (Session.Tools.InsertSection(SectionEvents.FreeName(Session.Chart, Strings.SectionsCustomName)) is { } inserted)
                _editNext = (null, inserted);
        }

        private void InsertTextEvent(ChartInstrument? part)
        {
            if (Session.Tools.InsertEvent(part, EditorTools.NewEventText) is { } inserted)
                _editNext = (part, inserted);
        }

        private IReadOnlyList<LaneMenuItem> EventMenuOf(ChartInstrument? part)
        {
            var items = SongEvents.All
                .Select(kind => LaneMenuItem.Choice(SongEventName(kind), false, () => Session.Tools.InsertEvent(null, SongEvents.TextOf(kind))))
                .ToList();
            items.Add(LaneMenuItem.Choice(Strings.SectionsTextEvent, false, () => InsertTextEvent(null)));
            if (part is { } instrument)
            {
                string header = string.Format(CultureInfo.CurrentCulture, Strings.SectionsPartTextEventFormat, ChartLabels.InstrumentName(instrument));
                items.Add(LaneMenuItem.Choice(header, false, () => InsertTextEvent(instrument)));
            }

            return items;
        }

        private void OnChartChanged(object? sender, EditorChartChangedEventArgs e)
        {
            // EVENTS, the times (the tempo map) and the parts there are, or the focused part's own events.
            var affected = e.Affected;
            if ((affected.Song & (SongChanges.Events | SongChanges.TempoMap | SongChanges.Parts)) != 0
                || affected.Regions.Any(r => r.Difficulty is null && r.Part == _part))
            {
                Schedule();
            }
        }

        // The delay moves every event's time.
        private void OnSongSettingsChanged(object? sender, EventArgs e) => Schedule();

        // The part events listed follow the focus.
        private void OnSessionState(object? sender, EventArgs e)
        {
            var focused = Session.Focused?.Instrument;
            if (focused == _focused)
                return;
            _focused = focused;
            Schedule();
        }

        // Once the changes of a moment are in, so a drag's preview or a run of edits is refreshed once.
        private void Schedule()
        {
            if (_scheduled)
                return;
            _scheduled = true;
            Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
        }

        private void Refresh()
        {
            _scheduled = false;
            if (_disposed)
                return;

            var chart = Session.Chart;
            var map = Session.TempoMap;
            if (!ReferenceEquals(map, _whereMap) || Session.Delay != _whereDelay || _whereByTick.Count > 4096)
            {
                _whereByTick.Clear();
                _whereMap = map;
                _whereDelay = Session.Delay;
            }

            ChartInstrument? part = Session.Focused is { } key && chart.PartOf(key.Instrument) is not null ? key.Instrument : null;
            if (part != _part)
            {
                _part = part;
                PartHeading = part is { } instrument
                    ? string.Format(CultureInfo.CurrentCulture, Strings.SectionsPartHeadingFormat, ChartLabels.InstrumentName(instrument))
                    : "";
                InsertEventMenu = EventMenuOf(part);
            }

            Sync(Events, EventRows.Of(chart, null), map);
            Sync(PartEvents, part is null ? [] : EventRows.Of(chart, part), map);
            HasEvents = Events.Count > 0;
            HasPartEvents = PartEvents.Count > 0;

            if (_editNext is { } wanted)
            {
                _editNext = null;
                var item = Events.Concat(PartEvents).FirstOrDefault(i => i.Row.Part == wanted.Part && i.Row.Event == wanted.Event);
                if (item is not null)
                    EditRequested?.Invoke(this, item);
            }
        }

        /// <summary>
        /// Makes <paramref name="items"/> the rows of <paramref name="rows"/>, keeping each row whose event is still there,
        /// and changing the list as little as it can, so the rows kept keep their fields and what's typed in them.
        /// </summary>
        private void Sync(ObservableCollection<EventItem> items, IReadOnlyList<EventRow> rows, TempoMap map)
        {
            var unused = items.ToList();
            var next = new List<EventItem>(rows.Count);
            foreach (var row in rows)
            {
                string where = WhereOf(row.Event.Tick, map);
                int found = unused.FindIndex(i => i.Row.Part == row.Part && i.Row.Event == row.Event);
                if (found < 0)
                {
                    next.Add(new EventItem(this, row, where));
                    continue;
                }

                var item = unused[found];
                unused.RemoveAt(found);
                item.Update(row, where);
                next.Add(item);
            }

            foreach (var gone in unused)
                items.Remove(gone);
            for (int i = 0; i < next.Count; i++)
            {
                if (i < items.Count && ReferenceEquals(items[i], next[i]))
                    continue;
                int at = items.IndexOf(next[i]);
                if (at >= 0)
                    items.Move(at, i);
                else
                    items.Insert(i, next[i]);
            }
        }

        /// <summary>Where <paramref name="tick"/> is: its time in the song, and its measure and beat.</summary>
        private string WhereOf(long tick, TempoMap map)
        {
            if (!_whereByTick.TryGetValue(tick, out string? where))
            {
                where = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.SectionsWhereFormat,
                    SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, Session.AudioAt(tick))), decimals: 2),
                    PropertiesViewModel.MeasureBeat(map, tick));
                _whereByTick[tick] = where;
            }

            return where;
        }
    }
}
