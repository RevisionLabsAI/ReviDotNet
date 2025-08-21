namespace Revi;

public class ComparisonResult
{
    public string PropertyName { get; set; } = string.Empty; // The name of the property/field
    public object? OldValue { get; set; } // The old value
    public object? NewValue { get; set; } // The new value
    public bool Changed { get; set; }     // Did the property/field change?
    public string? Description { get; set; } // Human-readable description of the change
}