namespace MBScript.Models;

/// <summary>Posizione e larghezza salvate di una colonna, per riapplicare la stessa
/// disposizione quando la tabella viene riaperta.</summary>
public class GridColumnLayout
{
    public string ColumnName { get; set; } = "";
    public int DisplayIndex { get; set; }
    public int Width { get; set; }
}
