using MBScript.Models;

namespace MBScript.Forms;

/// <summary>
/// Dialogo "Filtro personalizzato" in stile Access: chiede il valore da confrontare
/// per un operatore dei filtri per testo.
/// </summary>
public class FilterCriteriaDialog : Form
{
    #region Campi

    private readonly TextBox txtValue;

    #endregion Campi

    #region Proprietà

    public string CriteriaValue => txtValue.Text;

    #endregion Proprietà

    #region Costruttore

    public FilterCriteriaDialog(string columnName, ColumnFilterOperator op, string initialValue)
    {
        // Layout a coordinate assolute pensato per 96 DPI: senza queste due righe, su
        // un monitor ad alto DPI i controlli restano sovrapposti (stessa causa del
        // disallineamento visto nell'intestazione della griglia).
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);

        Text = "Filtro personalizzato";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(400, 108);

        Label lblPrompt = new Label
        {
            Text = $"{columnName} {DescribeOperator(op)}:",
            Location = new Point(12, 14),
            Size = new Size(376, 18),
            AutoEllipsis = true
        };
        Controls.Add(lblPrompt);

        txtValue = new TextBox
        {
            Text = initialValue,
            Location = new Point(12, 36),
            Size = new Size(376, 23)
        };
        Controls.Add(txtValue);

        Button btnOk = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(212, 70),
            Size = new Size(84, 26)
        };
        Button btnCancel = new Button
        {
            Text = "Annulla",
            DialogResult = DialogResult.Cancel,
            Location = new Point(304, 70),
            Size = new Size(84, 26)
        };
        Controls.Add(btnOk);
        Controls.Add(btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    #endregion Costruttore

    #region Helper

    /// <summary>Etichetta dell'operatore usata nel prompt, come nel dialogo Access.</summary>
    private static string DescribeOperator(ColumnFilterOperator op) => op switch
    {
        ColumnFilterOperator.Equals => "è uguale a",
        ColumnFilterOperator.NotEquals => "è diverso da",
        ColumnFilterOperator.BeginsWith => "inizia con",
        ColumnFilterOperator.NotBeginsWith => "non inizia con",
        ColumnFilterOperator.Contains => "contiene",
        ColumnFilterOperator.NotContains => "non contiene",
        ColumnFilterOperator.EndsWith => "termina con",
        ColumnFilterOperator.NotEndsWith => "non termina con",
        _ => "corrisponde a"
    };

    #endregion Helper
}
