using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using ChartPacker.Core.Dedup;
using ChartPacker.Core.Scanning;
using ChartPacker.Resources;
using ChartPacker.UI.Formatting;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// One row of a duplicate group: a copy on the Exact tab, a variant (with any identical copies of
    /// it) on the Same chart tab, or a chart (with every variant of it) on the Merge tab. Keeping or
    /// deleting it keeps or deletes every copy it stands for, except that once its group is merged
    /// only the copies identical to the one merged are deleted.
    /// </summary>
    public sealed partial class DedupMemberViewModel : ViewModelBase
    {
        private readonly DedupGroupViewModel _group;
        private readonly List<List<SongFingerprint>> _variants;
        private readonly List<SongFingerprint> _entries;
        private HashSet<string>? _merged;
        private bool _settingKeeper;

        public DedupMemberViewModel(DedupGroupViewModel group, IEnumerable<DedupVariant> variants)
        {
            _group = group;
            _variants = [.. variants.Select(v => v.Entries.ToList())];
            _entries = [.. _variants.SelectMany(v => v)];
            Representative = _entries[0];
            ChooseRepresentative();
        }

        /// <summary>Every copy this row stands for.</summary>
        public IReadOnlyList<SongFingerprint> Entries => _entries;

        /// <summary>
        /// The copies deleted with this row: every copy until the group is merged, then only the
        /// copies identical to the one the merge read, since nothing else of the others is in it.
        /// </summary>
        public IReadOnlyList<SongFingerprint> DeletableEntries => _merged is null ? _entries : [.. _entries.Where(e => _merged.Contains(e.Entry.FullPath))];

        /// <summary>The copies identical to the one shown.</summary>
        private IReadOnlyList<SongFingerprint> Identical =>
            _merged is not null ? DeletableEntries : _variants.FirstOrDefault(v => v.Contains(Representative)) ?? [Representative];

        /// <summary>The copy shown, and the one kept or merged from.</summary>
        public SongFingerprint Representative { get; private set; }

        public string Path => Representative.Entry.RelativePath;

        public string FullPath => Representative.Entry.FullPath;

        /// <summary>Kind, size and date, and the charter on the Merge tab.</summary>
        public string DetailText
        {
            get
            {
                var song = Representative;
                string kind = song.Entry.Kind == SongEntryKind.SngFile ? Strings.DedupKindSng : Strings.DedupKindFolder;
                string text = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.DedupMemberDetailFormat,
                    kind,
                    ByteSizeFormatter.Format((ulong)song.Stamp.TotalBytes),
                    song.Stamp.LastWriteTimeUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture));
                if (_group.Tier == DedupTier.MergeCandidate && song.Headline.Charter is { } charter)
                    text += " · " + string.Format(CultureInfo.CurrentCulture, Strings.DedupCharterFormat, charter);
                return text;
            }
        }

        /// <summary>The other copies deleted or kept along with this one.</summary>
        public string CopiesText => string.Format(CultureInfo.CurrentCulture, Strings.DedupCopiesFormat, Identical.Count - 1);

        public bool HasCopies => Identical.Count > 1;

        /// <summary>The copies of this chart whose metadata or other files differ from the one shown, on the Merge tab.</summary>
        public string OtherVersionsText => string.Format(CultureInfo.CurrentCulture, Strings.DedupOtherVersionsFormat, _entries.Count - Identical.Count);

        public bool HasOtherVersions => _entries.Count > Identical.Count;

        public bool HasDrafts => _entries.Any(e => e.HasDrafts);

        public bool HasExtras => _entries.Any(e => e.Extras.Count > 0);

        public string ExtrasText => string.Format(CultureInfo.CurrentCulture, Strings.DedupTagExtrasFormat, _entries.Max(e => e.Extras.Count));

        public bool HasSuffix => KeeperRules.HasDownloadSuffix(Representative.Entry.DisplayName);

        /// <summary>The keep buttons of one group form one radio group.</summary>
        public string GroupName => _group.GroupName;

        /// <summary>Once a group is merged, the merged song is what's kept, so there's no keep button.</summary>
        public bool ShowKeeper => _group.HasKeeperChoice;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanMark))]
        public partial bool IsKeeper { get; set; }

        [ObservableProperty]
        public partial bool IsMarked { get; set; }

        /// <summary>It can be marked for deletion: it isn't kept, has copies to delete and none with unsaved edits, and the group allows deleting.</summary>
        public bool CanMark => !IsKeeper && _group.CanMarkMembers && DeletableEntries is { Count: > 0 } deletable && !deletable.Any(e => e.HasDrafts);

        /// <summary>How it differs from the copy being kept, on the Same chart tab.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasDifference))]
        public partial string? DifferenceText { get; set; }

        public bool HasDifference => DifferenceText is not null;

        partial void OnIsKeeperChanged(bool value)
        {
            if (value && !_settingKeeper)
                _group.ChooseKeeper(this, overridden: true);
        }

        partial void OnIsMarkedChanged(bool value)
        {
            if (value && !CanMark)
            {
                IsMarked = false;
                return;
            }

            _group.OnMarksChanged();
        }

        internal void SetKeeper(bool value)
        {
            _settingKeeper = true;
            try
            {
                IsKeeper = value;
            }
            finally
            {
                _settingKeeper = false;
            }

            if (value)
                IsMarked = false;
        }

        internal void RefreshCanMark()
        {
            OnPropertyChanged(nameof(ShowKeeper));
            OnPropertyChanged(nameof(CanMark));
            if (IsMarked && !CanMark)
                IsMarked = false;
        }

        /// <summary>Takes a copy out; true when none is left to delete.</summary>
        internal bool Remove(string path)
        {
            bool IsPath(SongFingerprint e) => string.Equals(e.Entry.FullPath, path, StringComparison.Ordinal);
            if (_entries.RemoveAll(IsPath) == 0)
                return false;
            foreach (var variant in _variants)
                variant.RemoveAll(IsPath);
            _variants.RemoveAll(v => v.Count == 0);
            if (DeletableEntries.Count == 0)
                return true;

            ChooseRepresentative();
            OnPropertyChanged(string.Empty);
            return false;
        }

        /// <summary>
        /// The group was merged from <paramref name="sources"/>: from here on only the copies
        /// identical to the one of them this row holds are deleted with it.
        /// </summary>
        internal void SetMerged(IReadOnlyCollection<SongFingerprint> sources)
        {
            var merged = _variants.FirstOrDefault(v => v.Any(sources.Contains));
            _merged = new HashSet<string>(merged?.Select(e => e.Entry.FullPath) ?? [], StringComparer.Ordinal);
            ChooseRepresentative();
            OnPropertyChanged(string.Empty);
        }

        /// <summary>Shows the copy the keeper rules pick; once merged, among the copies identical to the one merged.</summary>
        internal void ChooseRepresentative()
        {
            var candidates = DeletableEntries;
            Representative = KeeperRules.Choose(candidates.Count > 0 ? candidates : _entries, _group.Preference);
        }
    }

    /// <summary>A group of duplicates: copies of which one is kept, or charts to merge.</summary>
    public sealed partial class DedupGroupViewModel : ViewModelBase
    {
        private static int s_nextId;

        private readonly DedupWindowViewModel _owner;
        private CancellationTokenSource? _describeCts;
        private ContentHasher? _hasher;
        private CancellationToken _windowToken;

        /// <param name="owner">The window.</param>
        /// <param name="tier">Which tab the group is on.</param>
        /// <param name="members">The variants each row stands for.</param>
        public DedupGroupViewModel(DedupWindowViewModel owner, DedupTier tier, IEnumerable<IEnumerable<DedupVariant>> members)
        {
            _owner = owner;
            Tier = tier;
            GroupName = "dedup-" + Interlocked.Increment(ref s_nextId).ToString(CultureInfo.InvariantCulture);
            foreach (var variants in members)
                Members.Add(new DedupMemberViewModel(this, variants));
            ApplyDefaultKeeper();
        }

        public DedupTier Tier { get; }

        public string GroupName { get; }

        internal DedupKindPreference Preference => _owner.Preference;

        /// <summary>The artist and title, or the name of the first copy when they're missing.</summary>
        public string Title
        {
            get
            {
                var song = Members.Count > 0 ? Members[0].Representative : null;
                if (song is null)
                    return "";
                return song.Headline is { Artist: { } artist, Name: { } name }
                    ? string.Format(CultureInfo.CurrentCulture, Strings.DedupTitleFormat, artist, name)
                    : song.Headline.Name ?? song.Entry.DisplayName;
            }
        }

        public AvaloniaList<DedupMemberViewModel> Members { get; } = [];

        public DedupMemberViewModel? Keeper { get; private set; }

        /// <summary>The user chose the keeper, so a change of preference leaves it alone.</summary>
        public bool IsKeeperOverridden { get; private set; }

        /// <summary>The merged song, once the group has been merged; it's kept in place of any copy.</summary>
        public DeletionKeeper? MergedOutput { get; private set; }

        public bool HasMerged => MergedOutput is not null;

        public string MergedText => MergedOutput is { } merged
            ? string.Format(CultureInfo.CurrentCulture, Strings.DedupMergedIntoFormat, merged.Path)
            : "";

        /// <summary>The group can be opened in the Merge window: charts to merge, or copies of one chart whose metadata or files differ.</summary>
        public bool ShowOpenInMerge => Tier != DedupTier.Exact;

        /// <summary>One of the group's own copies is chosen to be kept: until it's merged, and never on the Merge tab.</summary>
        public bool HasKeeperChoice => Tier != DedupTier.MergeCandidate && MergedOutput is null;

        /// <summary>Copies can be marked: always, except on the Merge tab before a merge.</summary>
        public bool CanMarkMembers => _owner.CanDelete && (Tier != DedupTier.MergeCandidate || MergedOutput is not null);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DeleteLabel))]
        [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
        public partial int MarkedCount { get; set; }

        /// <summary>The group's own delete button: on the Same chart tab, and on the Merge tab once merged.</summary>
        public bool ShowGroupDelete => Tier == DedupTier.SameChart || (Tier == DedupTier.MergeCandidate && MergedOutput is not null);

        public string DeleteLabel => string.Format(
            CultureInfo.CurrentCulture,
            MergedOutput is not null ? Strings.DedupDeleteSourcesFormat : Strings.DedupDeleteMarkedFormat,
            MarkedCount);

        [RelayCommand(CanExecute = nameof(CanDelete))]
        private Task DeleteAsync() => _owner.DeleteGroupAsync(this);

        private bool CanDelete() => ShowGroupDelete && MarkedCount > 0 && _owner.IsReady && _owner.CanDelete;

        [RelayCommand(CanExecute = nameof(CanOpenMerge))]
        private Task OpenInMergeAsync() => _owner.MergeAsync(this);

        private bool CanOpenMerge() => ShowOpenInMerge && MergedOutput is null && Members.Count > 1 && _owner.IsReady;

        /// <summary>Makes <paramref name="member"/> the copy that's kept.</summary>
        internal void ChooseKeeper(DedupMemberViewModel member, bool overridden)
        {
            if (!HasKeeperChoice)
                return;

            IsKeeperOverridden |= overridden;
            Keeper = member;
            foreach (var other in Members)
                other.SetKeeper(ReferenceEquals(other, member));
            OnMarksChanged();

            if (Tier == DedupTier.SameChart && _hasher is not null)
                _ = DescribeAsync(_hasher, _windowToken);
        }

        /// <summary>Keeps the copy the keeper rules pick, unless the user chose one.</summary>
        internal void ApplyDefaultKeeper()
        {
            foreach (var member in Members)
                member.ChooseRepresentative();
            OnPropertyChanged(nameof(Title));

            if (!HasKeeperChoice || Members.Count == 0 || (IsKeeperOverridden && Keeper is not null && Members.Contains(Keeper)))
                return;

            var best = KeeperRules.Choose([.. Members.Select(m => m.Representative)], Preference);
            ChooseKeeper(Members.First(m => ReferenceEquals(m.Representative, best)), overridden: false);
        }

        /// <summary>
        /// The merge wrote <paramref name="output"/> from <paramref name="sources"/>. It's now what's
        /// kept in place of any copy, so every copy identical to a source can be marked. Rows left
        /// with none are dropped.
        /// </summary>
        internal void SetMergedOutput(DeletionKeeper output, IReadOnlyCollection<SongFingerprint> sources)
        {
            MergedOutput = output;
            Keeper = null;
            foreach (var member in Members.ToList())
            {
                member.SetKeeper(false);
                member.SetMerged(sources);
                if (member.DeletableEntries.Count == 0)
                    Members.Remove(member);
            }

            OnMarksChanged();
            OnPropertyChanged(nameof(MergedOutput));
            OnPropertyChanged(nameof(HasKeeperChoice));
            OnPropertyChanged(nameof(DeleteLabel));
            OnPropertyChanged(nameof(HasMerged));
            OnPropertyChanged(nameof(MergedText));
            OnPropertyChanged(nameof(CanMarkMembers));
            OnPropertyChanged(nameof(ShowGroupDelete));
            foreach (var member in Members)
                member.RefreshCanMark();
            RefreshCommands();
        }

        /// <summary>The copies marked for deletion, each paired with what's kept.</summary>
        internal IReadOnlyList<DeletionItem> PlanDeletion()
        {
            var victims = Members.Where(m => m.IsMarked && m.CanMark).SelectMany(m => m.DeletableEntries).ToList();
            if (victims.Count == 0)
                return [];

            if (MergedOutput is { } merged)
                return DeletionPlan.Create(Tier, merged, victims);
            if (Tier == DedupTier.MergeCandidate)
                return [];

            return Keeper is { } keeper ? DeletionPlan.Create(Tier, DeletionKeeper.Of(keeper.Representative), victims) : [];
        }

        /// <summary>Takes a song out of the group; true when the group has nothing left to compare.</summary>
        internal bool RemoveEntry(string path)
        {
            bool changed = false;
            foreach (var member in Members.ToList())
            {
                if (!member.Entries.Any(e => string.Equals(e.Entry.FullPath, path, StringComparison.Ordinal)))
                    continue;

                changed = true;
                if (member.Remove(path))
                    Members.Remove(member);
            }

            if (changed && Keeper is not null && !Members.Contains(Keeper))
            {
                Keeper = null;
                IsKeeperOverridden = false;
            }

            if (changed)
            {
                ApplyDefaultKeeper();
                OnMarksChanged();
                RefreshCommands();
            }

            // A merged group stays while any of its sources is left to delete.
            return MergedOutput is not null ? Members.Count == 0 : Members.Count < 2;
        }

        internal void OnMarksChanged()
        {
            MarkedCount = Members.Count(m => m.IsMarked);
            _owner.OnMarksChanged(Tier);
        }

        internal void RefreshCommands()
        {
            DeleteCommand.NotifyCanExecuteChanged();
            OpenInMergeCommand.NotifyCanExecuteChanged();
        }

        /// <summary>Describes how every other variant differs from the one being kept.</summary>
        internal async Task DescribeAsync(ContentHasher hasher, CancellationToken windowToken)
        {
            _hasher = hasher;
            _windowToken = windowToken;
            _describeCts?.Cancel();
            _describeCts?.Dispose();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(windowToken);
            _describeCts = cts;

            if (Keeper is not { } keeper)
                return;

            keeper.DifferenceText = Strings.DedupDiffReference;
            var others = Members.Where(m => !ReferenceEquals(m, keeper)).ToList();
            foreach (var member in others)
                member.DifferenceText = Strings.DedupDiffComparing;

            foreach (var member in others)
            {
                try
                {
                    // Every group compares at once after the analysis, so they take turns reading files.
                    var gate = _owner.DescribeGate;
                    await gate.WaitAsync(cts.Token);
                    SongDifferences differences;
                    try
                    {
                        differences = await DedupDifferences.CompareAsync(keeper.Representative, member.Representative, hasher, cts.Token);
                    }
                    finally
                    {
                        gate.Release();
                    }

                    if (!cts.IsCancellationRequested)
                        member.DifferenceText = Describe(differences);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    member.DifferenceText = string.Format(CultureInfo.CurrentCulture, Strings.DedupDiffFailedFormat, ex.Message);
                }
            }
        }

        private static string Describe(SongDifferences differences)
        {
            var parts = new List<string>();
            if (differences.Metadata.Count > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.DedupDiffMetadataFormat, string.Join(", ", differences.Metadata.Select(m => m.Key))));
            AddFiles(parts, differences, FileDifferenceKind.Differs, Strings.DedupDiffFilesDifferFormat);
            AddFiles(parts, differences, FileDifferenceKind.OnlyInOther, Strings.DedupDiffOnlyHereFormat);
            AddFiles(parts, differences, FileDifferenceKind.OnlyInReference, Strings.DedupDiffMissingFormat);
            if (differences.IdenticalFiles > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.DedupDiffIdenticalFormat, differences.IdenticalFiles));
            return parts.Count == 0 ? Strings.DedupDiffNone : string.Join(" · ", parts);
        }

        private static void AddFiles(List<string> parts, SongDifferences differences, FileDifferenceKind kind, string format)
        {
            var names = differences.Files.Where(f => f.Kind == kind).Select(f => f.Name).ToList();
            if (names.Count > 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, format, string.Join(", ", names)));
        }
    }
}
