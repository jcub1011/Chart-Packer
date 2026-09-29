namespace ChartPacker.Audio.Spectrum;

/// <summary>Something whose sound can be shown as a spectrum.</summary>
public interface ISpectrumSource
{
    /// <summary>Fills <paramref name="levels"/>, <see cref="SpectrumAnalyzer.BandCount"/> of them, with the spectrum of what is heard now.</summary>
    /// <returns><see langword="false"/> when nothing is heard.</returns>
    bool TryReadSpectrum(Span<float> levels);
}
