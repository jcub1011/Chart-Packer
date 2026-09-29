using System.ComponentModel;

namespace ChartPacker.Core.Settings;

/// <summary>
/// A staged change to one setting. The live value is only written by <see cref="Apply"/>, so a settings
/// window can be cancelled without side effects.
/// </summary>
public abstract class SettingEdit : INotifyPropertyChanged
{
    protected SettingEdit(SettingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
    }

    public SettingDefinition Definition { get; }

    /// <summary>The staged value differs from the live value.</summary>
    public abstract bool IsModified { get; }

    /// <summary>The staged value is the setting's default.</summary>
    public abstract bool IsDefault { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Writes the staged value if it's modified, then takes the live value as the new starting point.</summary>
    /// <returns>Whether anything was written.</returns>
    public abstract bool Apply();

    /// <summary>Puts the staged value back to the live value.</summary>
    public abstract void Revert();

    /// <summary>Stages the setting's default. It isn't written until <see cref="Apply"/>.</summary>
    public abstract void ResetToDefault();

    protected void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>A staged change to a <see cref="SettingDefinition{T}"/>.</summary>
public class SettingEdit<T> : SettingEdit
{
    private readonly SettingDefinition<T> _definition;
    private T _live;
    private T _value;

    public SettingEdit(SettingDefinition<T> definition)
        : base(definition)
    {
        _definition = definition;
        _live = definition.Read();
        _value = _live;
    }

    /// <summary>The staged value. Setting it coerces it first.</summary>
    public T Value
    {
        get => _value;
        set => SetStaged(_definition.Coerce(value));
    }

    public override bool IsModified => !EqualityComparer<T>.Default.Equals(_value, _live);

    public override bool IsDefault => EqualityComparer<T>.Default.Equals(_value, _definition.DefaultValue);

    public override bool Apply()
    {
        if (!IsModified)
            return false;

        _definition.Write(_value);

        // Read back rather than assume, in case the owner of the live value adjusted it.
        bool wasDefault = IsDefault;
        _live = _definition.Read();
        bool valueChanged = !EqualityComparer<T>.Default.Equals(_value, _live);
        _value = _live;
        if (valueChanged)
            OnValueChanged();
        OnPropertyChanged(nameof(IsModified));
        if (IsDefault != wasDefault)
            OnPropertyChanged(nameof(IsDefault));
        return true;
    }

    public override void Revert() => SetStaged(_live);

    public override void ResetToDefault() => SetStaged(_definition.DefaultValue);

    /// <summary>Called after <see cref="Value"/> changes, before the modified and default flags are raised.</summary>
    protected virtual void OnValueChanged() => OnPropertyChanged(nameof(Value));

    private void SetStaged(T value)
    {
        if (EqualityComparer<T>.Default.Equals(_value, value))
            return;

        bool wasModified = IsModified;
        bool wasDefault = IsDefault;
        _value = value;
        OnValueChanged();
        if (IsModified != wasModified)
            OnPropertyChanged(nameof(IsModified));
        if (IsDefault != wasDefault)
            OnPropertyChanged(nameof(IsDefault));
    }
}

/// <summary>A <see cref="ChoiceSetting{T}"/>'s edit, seen by index so a view can bind to it without knowing <c>T</c>.</summary>
public interface IChoiceEdit
{
    IReadOnlyList<string> ChoiceLabels { get; }

    /// <summary>The index of the staged value in <see cref="ChoiceLabels"/>. An out-of-range index is ignored.</summary>
    int SelectedIndex { get; set; }
}

/// <summary>A staged change to a <see cref="ChoiceSetting{T}"/>.</summary>
public sealed class ChoiceSettingEdit<T> : SettingEdit<T>, IChoiceEdit
{
    private readonly ChoiceSetting<T> _definition;

    public ChoiceSettingEdit(ChoiceSetting<T> definition)
        : base(definition)
    {
        _definition = definition;
        ChoiceLabels = [.. definition.Choices.Select(c => c.Label)];
    }

    public IReadOnlyList<string> ChoiceLabels { get; }

    public int SelectedIndex
    {
        get => _definition.IndexOf(Value);
        set
        {
            if (value >= 0 && value < _definition.Choices.Count)
                Value = _definition.Choices[value].Value;
        }
    }

    protected override void OnValueChanged()
    {
        base.OnValueChanged();
        OnPropertyChanged(nameof(SelectedIndex));
    }
}
