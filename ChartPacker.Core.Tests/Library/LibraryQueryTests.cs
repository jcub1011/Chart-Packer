using ChartPacker.Core.Library;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Tests.Library;

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
        CollectionAssert.AreEqual(All, Apply(LibraryQuery.Default));
        Assert.IsTrue(LibraryQuery.Default.KeepsScanOrder);
        Assert.IsFalse(LibraryQuery.Default.IsFiltering);
        Assert.AreEqual(0, LibraryQuery.Default.ActiveFilterCount);
    }

    [TestMethod]
    public void SearchMatchesFileNameTitleOrArtist()
    {
        CollectionAssert.AreEqual(new[] { Charlie }, Apply(new() { Search = "CHARLIE" }));
        CollectionAssert.AreEqual(new[] { Alpha, Delta }, Apply(new() { Search = "alpha" }));
        CollectionAssert.AreEqual(new[] { Bravo }, Apply(new() { Search = "band" }));
    }

    [TestMethod]
    public void SearchIgnoresAccents() =>
        CollectionAssert.AreEqual(new[] { Alpha }, Apply(new() { Search = "beyonce" }));

    [TestMethod]
    public void EverySearchWordMustMatch()
    {
        CollectionAssert.AreEqual(new[] { Delta }, Apply(new() { Search = "alpha another" }));
        Assert.IsEmpty(Apply(new() { Search = "alpha missing" }));
    }

    [TestMethod]
    public void SearchDoesNotLookAtOtherFields() =>
        Assert.IsEmpty(Apply(new() { Search = "Rock" }));

    [TestMethod]
    public void FiltersByType()
    {
        CollectionAssert.AreEqual(new[] { Alpha, Charlie }, Apply(new() { IncludeFolders = false }));
        CollectionAssert.AreEqual(new[] { Bravo, Delta }, Apply(new() { IncludeSng = false }));
        Assert.IsEmpty(Apply(new() { IncludeSng = false, IncludeFolders = false }));
    }

    [TestMethod]
    public void TextFiltersMatchPartOfTheirField()
    {
        CollectionAssert.AreEqual(new[] { Alpha, Delta }, Apply(new() { TitleContains = " ALPHA " }));
        CollectionAssert.AreEqual(new[] { Bravo }, Apply(new() { ArtistContains = "band" }));
        CollectionAssert.AreEqual(new[] { Alpha }, Apply(new() { AlbumContains = "first" }));
        CollectionAssert.AreEqual(new[] { Bravo }, Apply(new() { GenreContains = "rock" }));
        CollectionAssert.AreEqual(new[] { Bravo }, Apply(new() { CharterContains = "two" }));
    }

    [TestMethod]
    public void BlankTextFilterIsInactive()
    {
        var query = new LibraryQuery { TitleContains = "   " };

        CollectionAssert.AreEqual(All, Apply(query));
        Assert.AreEqual(0, query.ActiveFilterCount);
    }

    [TestMethod]
    public void RangesIncludeTheirBoundsAndExcludeMissingValues()
    {
        CollectionAssert.AreEqual(new[] { Alpha, Delta }, Apply(new() { YearMin = 2004 }));
        CollectionAssert.AreEqual(new[] { Alpha, Bravo }, Apply(new() { YearMax = 2004 }));
        CollectionAssert.AreEqual(new[] { Alpha }, Apply(new() { YearMin = 2000, YearMax = 2005 }));
        CollectionAssert.AreEqual(new[] { Bravo }, Apply(new() { LengthMinMs = 200_000 }));
        CollectionAssert.AreEqual(new[] { Alpha, Delta }, Apply(new() { SizeMaxBytes = 10_000_000 }));
    }

    [TestMethod]
    public void FiltersByUnsavedChanges() =>
        CollectionAssert.AreEqual(new[] { Bravo }, Apply(new() { OnlyUnsaved = true }));

    [TestMethod]
    public void FiltersCombine() =>
        CollectionAssert.AreEqual(new[] { Delta }, Apply(new() { Search = "alpha", IncludeSng = false }));

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

        CollectionAssert.AreEqual(new[] { Alpha, Bravo }, Apply(new() { Instruments = new(LibraryInstruments.Guitar, LibraryInstruments.None) }));
        CollectionAssert.AreEqual(new[] { Alpha }, Apply(new() { Instruments = new(guitarDrums, LibraryInstruments.None, SetMatch.All) }));
        CollectionAssert.AreEqual(new[] { Alpha, Bravo }, Apply(new() { Instruments = new(guitarDrums, LibraryInstruments.None, SetMatch.Any) }));
    }

    [TestMethod]
    public void ExcludedItemsHideSongsWithThemAndSongsNotReadYet()
    {
        CollectionAssert.AreEqual(new[] { Alpha, Delta }, Apply(new() { Instruments = new(LibraryInstruments.None, LibraryInstruments.Vocals) }));
        CollectionAssert.AreEqual(new[] { Delta }, Apply(new() { Difficulties = new(LibraryDifficulties.None, LibraryDifficulties.Expert) }));
    }

    [TestMethod]
    public void IncludedAndExcludedItemsCombine() =>
        CollectionAssert.AreEqual(new[] { Alpha }, Apply(new() { Instruments = new(LibraryInstruments.Guitar, LibraryInstruments.Vocals) }));

    [TestMethod]
    public void FiltersByDifficultiesAndStems()
    {
        CollectionAssert.AreEqual(new[] { Alpha, Bravo }, Apply(new() { Difficulties = new(LibraryDifficulties.Expert, LibraryDifficulties.None) }));
        CollectionAssert.AreEqual(new[] { Alpha, Delta }, Apply(new() { Stems = new(LibraryStems.Drums | LibraryStems.Bass, LibraryStems.None, SetMatch.Any) }));

        // Only the song stem: it included, every other excluded.
        CollectionAssert.AreEqual(new[] { Bravo }, Apply(new() { Stems = new(LibraryStems.Song, ~LibraryStems.Song) }));
    }

    [TestMethod]
    public void FiltersByBackgroundsAndAlbumArt()
    {
        CollectionAssert.AreEqual(new[] { Alpha }, Apply(new() { Video = FilterPresence.Includes }));
        CollectionAssert.AreEqual(new[] { Bravo, Delta }, Apply(new() { Video = FilterPresence.Excludes }));
        CollectionAssert.AreEqual(new[] { Bravo }, Apply(new() { BackgroundImage = FilterPresence.Includes }));
        CollectionAssert.AreEqual(new[] { Alpha, Delta }, Apply(new() { AlbumArt = FilterPresence.Includes }));
        CollectionAssert.AreEqual(new[] { Bravo }, Apply(new() { AlbumArt = FilterPresence.Excludes }));
    }

    [TestMethod]
    public void ContentFiltersThatAreOffKeepSongsNotReadYet() =>
        CollectionAssert.AreEqual(All, Apply(new() { Instruments = new(LibraryInstruments.None, LibraryInstruments.None, SetMatch.Any), Video = FilterPresence.Either }));

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
        CollectionAssert.AreEqual(new[] { Alpha, Delta, Bravo, Charlie }, Apply(new() { SortField = LibrarySortField.Title }));
        CollectionAssert.AreEqual(new[] { Bravo, Delta, Alpha, Charlie }, Apply(new() { SortField = LibrarySortField.Title, Descending = true }));
    }

    [TestMethod]
    public void SortsNumbersWithMissingLast()
    {
        CollectionAssert.AreEqual(new[] { Bravo, Alpha, Delta, Charlie }, Apply(new() { SortField = LibrarySortField.Year }));
        CollectionAssert.AreEqual(new[] { Delta, Alpha, Bravo, Charlie }, Apply(new() { SortField = LibrarySortField.Year, Descending = true }));
        CollectionAssert.AreEqual(new[] { Bravo, Alpha, Charlie, Delta }, Apply(new() { SortField = LibrarySortField.Length, Descending = true }));
        CollectionAssert.AreEqual(new[] { Delta, Alpha, Bravo, Charlie }, Apply(new() { SortField = LibrarySortField.Size }));
    }

    [TestMethod]
    public void TiesKeepScanOrderInEitherDirection()
    {
        CollectionAssert.AreEqual(new[] { Alpha, Charlie, Bravo, Delta }, Apply(new() { SortField = LibrarySortField.Type }));
        CollectionAssert.AreEqual(new[] { Bravo, Delta, Alpha, Charlie }, Apply(new() { SortField = LibrarySortField.Type, Descending = true }));
    }

    [TestMethod]
    public void ScanOrderDescendingReverses() =>
        CollectionAssert.AreEqual(All.Reverse().ToArray(), Apply(new() { Descending = true }));

    [TestMethod]
    public void SortsByFileName() =>
        CollectionAssert.AreEqual(new[] { Delta, Charlie, Bravo, Alpha }, Apply(new() { SortField = LibrarySortField.FileName, Descending = true }));

    [TestMethod]
    public void MatchesAgreesWithApply()
    {
        var query = new LibraryQuery { GenreContains = "pop" };

        Assert.IsTrue(query.Matches(Alpha));
        Assert.IsFalse(query.Matches(Bravo));
    }

    private static List<LibrarySongFacts> Apply(LibraryQuery query) => query.Apply(All, f => f);
}
