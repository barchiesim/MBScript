namespace MBScript.Models;

/// <summary>Operatori dei "Filtri per testo" in stile datasheet Access.</summary>
public enum ColumnFilterOperator
{
    None = 0,
    Equals,
    NotEquals,
    BeginsWith,
    NotBeginsWith,
    Contains,
    NotContains,
    EndsWith,
    NotEndsWith
}

/// <summary>
/// Stato del filtro applicato a una singola colonna della griglia.
/// <see cref="AllowedValues"/> rappresenta la selezione a caselle di spunta
/// (null = nessun filtro sui valori), <see cref="Operator"/>/<see cref="Value"/>
/// il criterio testuale. Le due parti si combinano in AND.
/// </summary>
public class ColumnFilter
{
    public List<string?>? AllowedValues { get; set; }
    public ColumnFilterOperator Operator { get; set; } = ColumnFilterOperator.None;
    public string Value { get; set; } = "";

    public bool HasValueList => AllowedValues is not null;

    public bool HasCriteria => Operator != ColumnFilterOperator.None && Value.Length > 0;

    public bool IsEmpty => !HasValueList && !HasCriteria;
}
