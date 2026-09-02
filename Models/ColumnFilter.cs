namespace MBScript.Models;

/// <summary>Operatori dei "Filtri per testo" in stile datasheet Access.</summary>
public enum ColumnFilterOperator
{
    /// <summary>Nessun filtro applicato.</summary>
    None = 0,
    /// <summary>Uguale a.</summary>
    Equals,
    /// <summary>Diverso da.</summary>
    NotEquals,
    /// <summary>Inizia con.</summary>
    BeginsWith,
    /// <summary>Non inizia con.</summary>
    NotBeginsWith,
    /// <summary>Contiene.</summary>
    Contains,
    /// <summary>Non contiene.</summary>
    NotContains,
    /// <summary>Termina con.</summary>
    EndsWith,
    /// <summary>Non termina con.</summary>
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
