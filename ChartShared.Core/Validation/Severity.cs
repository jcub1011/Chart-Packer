namespace ChartShared.Core.Validation;

/// <summary>How serious a <see cref="Finding"/> is (project spec §6).</summary>
public enum Severity
{
    /// <summary>Breaks the spec or can't be read safely. Blocks packing and marks a <c>.sng</c> Invalid.</summary>
    Error,

    /// <summary>Allowed by the spec, but likely to cause problems or differs from the reference tool.</summary>
    Warning,

    /// <summary>For the user's awareness only.</summary>
    Info,
}
