using System.Globalization;

namespace ChartPacker.Core.Settings;

/// <summary>
/// One setting in a <see cref="SettingsCatalog"/>: where it's listed, how it's described and searched,
/// and (in <see cref="SettingDefinition{T}"/>) how its value is read and written.
/// </summary>
/// <remarks>The concrete kind (toggle, integer, choice…) decides which editor the settings window shows.</remarks>
public abstract class SettingDefinition
{
    /// <summary>Unique within a <see cref="SettingsCatalog"/>, e.g. <c>output.overwritePolicy</c>.</summary>
    public required string Id { get; init; }

    public required SettingCategory Category { get; init; }

    public required string Label { get; init; }

    public string Description { get; init; } = "";

    /// <summary>Extra words search should find this setting by, beyond its label, description and category.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>
    /// The id of a <see cref="ToggleSetting"/> this setting only applies while it's on, or <see langword="null"/>.
    /// While it's staged off, this one can't be changed; see <see cref="SettingEdit.IsEnabled"/>.
    /// </summary>
    public string? RequiresId { get; init; }

    /// <summary>Starts staging a change to this setting, from its current value.</summary>
    public abstract SettingEdit BeginEdit();
}

/// <summary>A setting whose value is a <typeparamref name="T"/>.</summary>
public abstract class SettingDefinition<T> : SettingDefinition
{
    public required T DefaultValue { get; init; }

    /// <summary>Reads the live value.</summary>
    public required Func<T> Get { get; init; }

    /// <summary>Writes the live value. It is only ever passed a coerced value.</summary>
    public required Action<T> Set { get; init; }

    /// <summary>Brings <paramref name="value"/> into range, or back to the default if it can't be.</summary>
    public virtual T Coerce(T value) => value;

    public T Read() => Coerce(Get());

    public void Write(T value) => Set(Coerce(value));

    public override SettingEdit BeginEdit() => new SettingEdit<T>(this);
}

/// <summary>An on/off setting.</summary>
public sealed class ToggleSetting : SettingDefinition<bool>
{
}

/// <summary>A whole number between <see cref="Minimum"/> and <see cref="Maximum"/>.</summary>
public sealed class IntegerSetting : SettingDefinition<int>
{
    public required int Minimum { get; init; }

    public required int Maximum { get; init; }

    public override int Coerce(int value) => Math.Clamp(value, Minimum, Maximum);
}

/// <summary>A number between <see cref="Minimum"/> and <see cref="Maximum"/>, edited with a slider.</summary>
public sealed class RangeSetting : SettingDefinition<double>
{
    public required double Minimum { get; init; }

    public required double Maximum { get; init; }

    /// <summary>How far one arrow-key press moves the slider.</summary>
    public double SmallChange { get; init; } = 1;

    /// <summary>Formats the value for display, e.g. as a percentage. <see langword="null"/> uses the current culture's default.</summary>
    public Func<double, string>? Format { get; init; }

    public override double Coerce(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, Minimum, Maximum) : DefaultValue;

    public string FormatValue(double value) =>
        Format is { } format ? format(value) : value.ToString(CultureInfo.CurrentCulture);
}

/// <summary>One of a fixed list of values.</summary>
public sealed class ChoiceSetting<T> : SettingDefinition<T>
{
    public required IReadOnlyList<SettingChoice<T>> Choices { get; init; }

    /// <summary>A value that isn't one of the <see cref="Choices"/> becomes the default.</summary>
    public override T Coerce(T value) => IndexOf(value) >= 0 ? value : DefaultValue;

    public int IndexOf(T value)
    {
        for (int i = 0; i < Choices.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(Choices[i].Value, value))
                return i;
        }

        return -1;
    }

    public override SettingEdit BeginEdit() => new ChoiceSettingEdit<T>(this);
}

/// <summary>A value of a <see cref="ChoiceSetting{T}"/> and the text shown for it.</summary>
public sealed record SettingChoice<T>(T Value, string Label);

/// <summary>A folder path, or <see langword="null"/> for none.</summary>
public sealed class FolderSetting : SettingDefinition<string?>
{
    /// <summary>What to show when no folder is set, e.g. what happens instead.</summary>
    public string? EmptyLabel { get; init; }

    /// <summary>The title of the folder picker. <see langword="null"/> uses the <see cref="SettingDefinition.Label"/>.</summary>
    public string? PickerTitle { get; init; }

    /// <summary>A blank path means no folder.</summary>
    public override string? Coerce(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
