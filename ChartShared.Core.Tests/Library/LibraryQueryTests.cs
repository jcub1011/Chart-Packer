using ChartShared.Core.Library;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Tests.Library;

[TestClass]
public sealed class LibraryQueryTests
{
    private static readonly LibrarySongFacts Alpha = new(SongEntryKind.SngFile, "alpha_file", "Alpha", "Beyoncé", "First Album", "Pop", "Charter One", 2004, 180_000, 10_000_000)
    {
        Instruments = LibraryInstruments.Guitar | LibraryInstruments.Drums,
        Difficulties = LibraryDifficulties.Hard | LibraryDifficulties.Expert,
        Stems = LibraryStems.Song | LibraryStems.Guitar | LibraryStems.Drums,
        HasVideo = true,
        HasImage = false,
        HasAlbumArt = true,
    };

    private static readonly LibrarySongFacts Bravo = new(SongEntryKind.SongFolder, "bravo_folder", "Bravo", "The Band", "Second", "Rock", "Charter Two", 1999, 240_000, 50_000_000, HasDrafts: true)
    {
        Instruments = LibraryInstruments.Guitar | LibraryInstruments.Vocals,
        Difficulties = LibraryDifficulties.Expert,
        Stems = LibraryStems.Song,
        HasVideo = false,
        HasImage = true,
        HasAlbumArt = false,
    };

    // Not read yet, so its chart, files and assets aren't known.
    private static readonly LibrarySongFacts Charlie = new(SongEntryKind.SngFile, "charlie_file");

    private static readonly LibrarySongFacts Delta = new(SongEntryKind.SongFolder, "delta_folder", "alpha two", "Another", Year: 2010, SizeBytes: 1_000)
    {
        Instruments = LibraryInstruments.Bass,
        Difficulties = LibraryDifficulties.Easy,
        Stems = LibraryStems.Song | LibraryStems.Bass,
        HasVideo = false,
        HasImage = false,
        HasAlbumArt = true,
    };

    private static readonly LibrarySongFacts[] All = [Alpha, Bravo, Charlie, Delta];

    [TestMethod]
    public void DefaultKeepsEverythingInScanOrder()
    {
        Assert.AreSequenceEqual(All, Apply(LibraryQuery.Default));
        Assert.IsTrue(LibraryQuery.Default.KeepsScanOrder);
        Assert.IsFalse(LibraryQuery.Default.IsFiltering);
        Assert.AreEqual(0, LibraryQuery.Default.ActiveFilterCount);
    }

    [TestMethod]
    public void SearchMatchesFileNameTitleOrArtist()
    {
        Assert.AreSequenceEqual([Charlie], Apply(new() { Search = "CHARLIE" }));
        Assert.AreSequenceEqual([Alpha, Delta], Apply(new() { Search = "alpha" }));
        Assert.AreSequenceEqual([Bravo], Apply(new() { Search = "band" }));
    }

    [TestMethod]
    public void SearchIgnoresAccents() =>
        Assert.AreSequenceEqual([Alpha], Apply(new() { Search = "beyonce" }));

    [TestMethod]
    public void EverySearchWordMustMatch()
    {
        Assert.AreSequenceEqual([Delta], Apply(new() { Search = "alpha another" }));
        Assert.IsEmpty(Apply(new() { Search = "alpha missing" }));
    }

    [TestMethod]
    public void SearchDoesNotLookAtOtherFields() =>
        Assert.IsEmpty(Apply(new() { Search = "Rock" }));

    [TestMethod]
    public void FiltersByType()
    {
        Assert.AreSequenceEqual([Alpha, Charlie], Apply(new() { IncludeFolders = false }));
        Assert.AreSequenceEqual([Bravo, Delta], Apply(new() { IncludeSng = false }));
        Assert.IsEmpty(Apply(new() { IncludeSng = false, IncludeFolders = false }));
    }

    [TestMethod]
    public void TextFiltersMatchPartOfTheirField()
    {
        Assert.AreSequenceEqual([Alpha, Delta], Apply(new() { TitleContains = " ALPHA " }));
        Assert.AreSequenceEqual([Bravo], Apply(new() { ArtistContains = "band" }));
        Assert.AreSequenceEqual([Alpha], Apply(new() { AlbumContains = "first" }));
        Assert.AreSequenceEqual([Bravo], Apply(new() { GenreContains = "rock" }));
        Assert.AreSequenceEqual([Bravo], Apply(new() { CharterContains = "two" }));
    }

    [TestMethod]
    public void BlankTextFilterIsInactive()
    {
        var query = new LibraryQuery { TitleContains = "   " };

        Assert.AreSequenceEqual(All, Apply(query));
        Assert.AreEqual(0, query.ActiveFilterCount);
    }

    [TestMethod]
    public void RangesIncludeTheirBoundsAndExcludeMissingValues()
    {
        Assert.AreSequenceEqual([Alpha, Delta], Apply(new() { YearMin = 2004 }));
        Assert.AreSequenceEqual([Alpha, Bravo], Apply(new() { YearMax = 2004 }));
        Assert.AreSequenceEqual([Alpha], Apply(new() { YearMin = 2000, YearMax = 2005 }));
        Assert.AreSequenceEqual([Bravo], Apply(new() { LengthMinMs = 200_000 }));
        Assert.AreSequenceEqual([Alpha, Delta], Apply(new() { SizeMaxBytes = 10_000_000 }));
    }

    [TestMethod]
    public void FiltersByUnsavedChanges() =>
        Assert.AreSequenceEqual([Bravo], Apply(new() { OnlyUnsaved = true }));

    [TestMethod]
    public void FiltersCombine() =>
        Assert.AreSequenceEqual([Delta], Apply(new() { Search = "alpha", IncludeSng = false }));

    [TestMethod]
    public void CountsEachActiveFilterOnce()
    {
        var query = new LibraryQuery
        {
            Search = "ignored",
            IncludeSng = false,
            TitleContains = "a",
            YearMin = 1,
            YearMax = 2,
            SizeMinBytes = 1,
            OnlyUnsaved = true,
        };

        Assert.AreEqual(5, query.ActiveFilterCount);
    }

    [TestMethod]
    public void InstrumentsNeedEveryIncludedPartOrAnyOfThem()
    {
        const LibraryInstruments guitarDrums = LibraryInstruments.Guitar | LibraryInstruments.Drums;

        Assert.AreSequenceEqual([Alpha, Bravo], Apply(new() { Instruments = new(LibraryInstruments.Guitar, LibraryInstruments.None) }));
        Assert.AreSequenceEqual([Alpha], Apply(new() { Instruments = new(guitarDrums, LibraryInstruments.None, SetMatch.All) }));
        Assert.AreSequenceEqual([Alpha, Bravo], Apply(new() { Instruments = new(guitarDrums, LibraryInstruments.None, SetMatch.Any) }));
    }

    [TestMethod]
    public void ExcludedItemsHideSongsWithThemAndSongsNotReadYet()
    {
        Assert.AreSequenceEqual([Alpha, Delta], Apply(new() { Instruments = new(LibraryInstruments.None, LibraryInstruments.Vocals) }));
        Assert.AreSequenceEqual([Delta], Apply(new() { Difficulties = new(LibraryDifficulties.None, LibraryDifficulties.Expert) }));
    }

    [TestMethod]
    public void IncludedAndExcludedItemsCombine() =>
        Assert.AreSequenceEqual([Alpha], Apply(new() { Instruments = new(LibraryInstruments.Guitar, LibraryInstruments.Vocals) }));

    [TestMethod]
    public void FiltersByDifficultiesAndStems()
    {
        Assert.AreSequenceEqual([Alpha, Bravo], Apply(new() { Difficulties = new(LibraryDifficulties.Expert, LibraryDifficulties.None) }));
        Assert.AreSequenceEqual([Alpha, Delta], Apply(new() { Stems = new(LibraryStems.Drums | LibraryStems.Bass, LibraryStems.None, SetMatch.Any) }));

        // Only the song stem: it included, every other excluded.
        Assert.AreSequenceEqual([Bravo], Apply(new() { Stems = new(LibraryStems.Song, ~LibraryStems.Song) }));
    }

    [TestMethod]
    public void FiltersByBackgroundsAndAlbumArt()
    {
        Assert.AreSequenceEqual([Alpha], Apply(new() { Video = FilterPresence.Includes }));
        Assert.AreSequenceEqual([Bravo, Delta], Apply(new() { Video = FilterPresence.Excludes }));
        Assert.AreSequenceEqual([Bravo], Apply(new() { BackgroundImage = FilterPresence.Includes }));
        Assert.AreSequenceEqual([Alpha, Delta], Apply(new() { AlbumArt = FilterPresence.Includes }));
        Assert.AreSequenceEqual([Bravo], Apply(new() { AlbumArt = FilterPresence.Excludes }));
    }

    [TestMethod]
    public void ContentFiltersThatAreOffKeepSongsNotReadYet() =>
        Assert.AreSequenceEqual(All, Apply(new() { Instruments = new(LibraryInstruments.None, LibraryInstruments.None, SetMatch.Any), Video = FilterPresence.Either }));

    [TestMethod]
    public void CountsEachContentFilterOnce()
    {
        var query = new LibraryQuery
        {
            Instruments = new(LibraryInstruments.Guitar | LibraryInstruments.Bass, LibraryInstruments.Vocals),
            Difficulties = new(LibraryDifficulties.None, LibraryDifficulties.Easy),
            Stems = new(LibraryStems.Drums, LibraryStems.None),
            Video = FilterPresence.Includes,
            BackgroundImage = FilterPresence.Excludes,
            AlbumArt = FilterPresence.Includes,
        };

        Assert.AreEqual(6, query.ActiveFilterCount);
        Assert.AreEqual(0, new LibraryQuery { Instruments = new(LibraryInstruments.None, LibraryInstruments.None, SetMatch.Any) }.ActiveFilterCount);
    }

    [TestMethod]
    public void SearchAloneIsFiltering()
    {
        var query = new LibraryQuery { Search = "x" };

        Assert.IsTrue(query.IsFiltering);
        Assert.IsFalse(query.KeepsScanOrder);
    }

    [TestMethod]
    public void SortsByTitleIgnoringCaseWithMissingLast()
    {
        Assert.AreSequenceEqual([Alpha, Delta, Bravo, Charlie], Apply(new() { SortField = LibrarySortField.Title }));
        Assert.AreSequenceEqual([Bravo, Delta, Alpha, Charlie], Apply(new() { SortField = LibrarySortField.Title, Descending = true }));
    }

    [TestMethod]
    public void SortsNumbersWithMissingLast()
    {
        Assert.AreSequenceEqual([Bravo, Alpha, Delta, Charlie], Apply(new() { SortField = LibrarySortField.Year }));
        Assert.AreSequenceEqual([Delta, Alpha, Bravo, Charlie], Apply(new() { SortField = LibrarySortField.Year, Descending = true }));
        Assert.AreSequenceEqual([Bravo, Alpha, Charlie, Delta], Apply(new() { SortField = LibrarySortField.Length, Descending = true }));
        Assert.AreSequenceEqual([Delta, Alpha, Bravo, Charlie], Apply(new() { SortField = LibrarySortField.Size }));
    }

    [TestMethod]
    public void TiesKeepScanOrderInEitherDirection()
    {
        Assert.AreSequenceEqual([Alpha, Charlie, Bravo, Delta], Apply(new() { SortField = LibrarySortField.Type }));
        Assert.AreSequenceEqual([Bravo, Delta, Alpha, Charlie], Apply(new() { SortField = LibrarySortField.Type, Descending = true }));
    }

    [TestMethod]
    public void ScanOrderDescendingReverses() =>
        Assert.AreSequenceEqual([.. All.Reverse()], Apply(new() { Descending = true }));

    [TestMethod]
    public void SortsByFileName() =>
        Assert.AreSequenceEqual([Delta, Charlie, Bravo, Alpha], Apply(new() { SortField = LibrarySortField.FileName, Descending = true }));

    [TestMethod]
    public void MatchesAgreesWithApply()
    {
        var query = new LibraryQuery { GenreContains = "pop" };

        Assert.IsTrue(query.Matches(Alpha));
        Assert.IsFalse(query.Matches(Bravo));
    }

    [TestMethod]
    public void CreateMatcherAgreesWithMatches()
    {
        var query = new LibraryQuery { Search = "alpha", GenreContains = "pop" };
        var matcher = query.CreateMatcher();

        foreach (var facts in All)
            Assert.AreEqual(query.Matches(facts), matcher(facts));
    }

    [TestMethod]
    public void CreateComparerSortsLikeApply()
    {
        // Two songs alike in every sorted value but the file name, so ties have to fall back to scan order.
        var echo = new LibrarySongFacts(SongEntryKind.SngFile, "echo_file", "Alpha", "Beyoncé", "First Album", "Pop", "Charter One", 2004, 180_000, 10_000_000);
        LibrarySongFacts[] scanOrdered = [.. All, echo, Charlie with { FileName = "charlie_two" }];
        var indexOf = scanOrdered.Select((facts, i) => (facts, i)).ToDictionary(p => (object)p.facts, p => p.i, ReferenceEqualityComparer.Instance);
        var byScanOrder = Comparer<LibrarySongFacts>.Create((a, b) => indexOf[a].CompareTo(indexOf[b]));

        foreach (var field in Enum.GetValues<LibrarySortField>())
        {
            foreach (bool descending in new[] { false, true })
            {
                var query = new LibraryQuery { SortField = field, Descending = descending };
                var sorted = scanOrdered.ToList();
                sorted.Sort(query.CreateComparer(f => f, byScanOrder));

                Assert.AreSequenceEqual(query.Apply(scanOrdered, f => f), sorted, $"{field}, descending: {descending}");
            }
        }
    }

    private static List<LibrarySongFacts> Apply(LibraryQuery query) => query.Apply(All, f => f);
}
