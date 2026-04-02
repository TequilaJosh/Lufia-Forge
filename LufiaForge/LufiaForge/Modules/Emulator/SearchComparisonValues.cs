namespace LufiaForge.Modules.Emulator;

/// <summary>Exposes all SearchComparison values as a static array for XAML ComboBox binding.</summary>
public static class SearchComparisonValues
{
    public static readonly SearchComparison[] All =
    {
        SearchComparison.Exact,
        SearchComparison.Greater,
        SearchComparison.Less,
        SearchComparison.Changed,
        SearchComparison.Unchanged,
        SearchComparison.Increased,
        SearchComparison.Decreased,
        SearchComparison.Any,
    };
}
