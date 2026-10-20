namespace ChartShared.Core.Charting.Difficulty;

/// <summary>
/// The parameters and references a pass of the difficulty model works with, and the parts of it that don't depend on
/// notes: how strain decays, how endurance builds, and how the attributes are combined (docs/difficulty-model.md §3 to §5).
/// </summary>
/// <remarks>Immutable, so one is shared between threads.</remarks>
internal sealed class DifficultyModel
{
    private readonly double _strumLog, _noteLog, _rhythmLog, _patternLog, _enduranceLog;

    // What a raw strain is multiplied by to normalize it: 100 over its reference.
    private readonly double _enduranceScale;

    public DifficultyModel(DifficultyParameters parameters, DifficultyReferences references)
    {
        Parameters = parameters;
        References = references;
        _strumLog = Math.Log(DifficultyParameters.Retained(parameters.StrumHalfLife));
        _noteLog = Math.Log(DifficultyParameters.Retained(parameters.NoteHalfLife));
        _rhythmLog = Math.Log(DifficultyParameters.Retained(parameters.RhythmHalfLife));
        _patternLog = Math.Log(DifficultyParameters.Retained(parameters.PatternHalfLife));
        _enduranceLog = Math.Log(DifficultyParameters.Retained(parameters.EnduranceHalfLife));
        StrumScale = 100 / references.Strum;
        NoteScale = 100 / references.Note;
        RhythmScale = 100 / references.Rhythm;
        PatternScale = 100 / references.Pattern;
        _enduranceScale = 100 / references.Endurance;
        LoadWeights = [parameters.StrumWeight, parameters.NoteWeight];
        Weights = [parameters.StrumWeight, parameters.NoteWeight, parameters.RhythmWeight, parameters.PatternWeight, parameters.EnduranceWeight];
    }

    /// <summary>The model for <paramref name="parameters"/>, with the references worked out from its reference patterns.</summary>
    public static DifficultyModel For(DifficultyParameters parameters) => new(parameters, DifficultyReferences.For(parameters));

    public DifficultyParameters Parameters { get; }

    public DifficultyReferences References { get; }

    public double StrumScale { get; }

    public double NoteScale { get; }

    public double RhythmScale { get; }

    public double PatternScale { get; }

    /// <summary>The weights of strum and note, in that order, as the physical load combines them.</summary>
    public double[] LoadWeights { get; }

    /// <summary>The weights of the five attributes, in <see cref="DifficultyAttribute"/>'s order.</summary>
    public double[] Weights { get; }

    /// <summary>
    /// The weighted power mean of <paramref name="values"/> (Mₚ, §5.1): between their weighted average and the largest.
    /// Values below zero count as zero.
    /// </summary>
    public static double PowerMean(ReadOnlySpan<double> values, ReadOnlySpan<double> weights, double p)
    {
        double sum = 0, total = 0;

        // The default power, without Math.Pow, as endurance's integration takes thousands of these a track.
        if (p == 4)
        {
            for (int i = 0; i < values.Length; i++)
            {
                double x = Math.Max(0, values[i]);
                x *= x;
                sum += weights[i] * x * x;
                total += weights[i];
            }

            return total <= 0 ? 0 : Math.Sqrt(Math.Sqrt(sum / total));
        }

        for (int i = 0; i < values.Length; i++)
        {
            sum += weights[i] * Math.Pow(Math.Max(0, values[i]), p);
            total += weights[i];
        }

        return total <= 0 ? 0 : Math.Pow(sum / total, 1 / p);
    }

    /// <summary>
    /// The physical load endurance builds from (L, §4.4): the strumming and fretting hands' strain combined, then raised
    /// to <see cref="DifficultyParameters.EnduranceLoadPower"/> on the 0-to-100 scale. Strain grows with about the square
    /// of the note rate, so with the default of a half the load follows how fast the hands work, and steady strumming at a
    /// moderate pace counts for as much as it tires.
    /// </summary>
    public double Load(double strum, double note)
    {
        double mean = PowerMean([strum, note], LoadWeights, Parameters.PowerMean) / 100;
        double power = Parameters.EnduranceLoadPower;
        return 100 * (power == 0.5 ? Math.Sqrt(mean) : Math.Pow(mean, power));
    }

    /// <summary>The overall difficulty of <paramref name="levels"/> (D, §5.2): the five attributes combined.</summary>
    public double Combine(DifficultyAttributes levels) =>
        PowerMean([levels.Strum, levels.Note, levels.Rhythm, levels.Pattern, levels.Endurance], Weights, Parameters.PowerMean);

    /// <summary>
    /// <paramref name="levels"/> <paramref name="durationMs"/> later with no note between: each strain decays, and endurance
    /// builds from the load above the threshold, or recovers, in steps of at most the integration step.
    /// </summary>
    /// <remarks>
    /// Each step takes the load at its middle as constant, and advances endurance by that ODE's exact solution, so the
    /// result doesn't depend on the step for a constant load. The step count depends only on the duration, so going from
    /// a sample to a time always gives the same value.
    /// </remarks>
    public DifficultyAttributes Integrate(DifficultyAttributes levels, double durationMs)
    {
        if (!(durationMs > 0))
            return levels;
        int steps = Math.Max(1, (int)Math.Ceiling(durationMs / Parameters.IntegrationStepMs));
        double step = durationMs / steps;
        double kept = Math.Exp(_enduranceLog * step / 1000);
        double steady = Parameters.EnduranceHalfLife / Math.Log(2) * _enduranceScale;
        double endurance = levels.Endurance;

        // The physical strains at the middle of the first step, then decayed a step at a time.
        double half = step / 2000, whole = step / 1000;
        double strum = levels.Strum * Math.Exp(_strumLog * half), strumStep = Math.Exp(_strumLog * whole);
        double note = levels.Note * Math.Exp(_noteLog * half), noteStep = Math.Exp(_noteLog * whole);
        for (int s = 0; s < steps; s++)
        {
            double above = Math.Max(0, Load(strum, note) - Parameters.EnduranceThreshold);
            endurance = (endurance * kept) + (above * steady * (1 - kept));
            strum *= strumStep;
            note *= noteStep;
        }

        double seconds = durationMs / 1000;
        return new DifficultyAttributes(
            levels.Strum * Math.Exp(_strumLog * seconds),
            levels.Note * Math.Exp(_noteLog * seconds),
            levels.Rhythm * Math.Exp(_rhythmLog * seconds),
            levels.Pattern * Math.Exp(_patternLog * seconds),
            endurance);
    }
}
