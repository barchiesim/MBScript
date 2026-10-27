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
        // Layout a pannelli con misure ricavate dal testo (vedi DialogLayout).
        AutoScaleMode = AutoScaleMode.None;

        Text = "Filtro personalizzato";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Font = new Font("Segoe UI", 9f);
        Padding = new Padding(DialogLayout.Gap(this, 0.75f));

        int gap = DialogLayout.Gap(this);
        int fieldWidth = TextRenderer.MeasureText(new string('W', 32), Font).Width;

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        Label lblPrompt = new Label
        {
            Text = $"{columnName} {DescribeOperator(op)}:",
            AutoSize = true,
            MaximumSize = new Size(fieldWidth, 0),
            AutoEllipsis = true,
            Margin = new Padding(3, 0, 3, gap)
        };
        root.Controls.Add(lblPrompt, 0, 0);

        txtValue = new TextBox
        {
            Text = initialValue,
            Width = fieldWidth,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(3, 0, 3, gap * 3)
        };
        root.Controls.Add(txtValue, 0, 1);

        int buttonWidth = DialogLayout.ButtonWidth(this, "OK", "Annulla");
        Button btnOk = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(buttonWidth, 0)
        };
        Button btnCancel = new Button
        {
            Text = "Annulla",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            MinimumSize = new Size(buttonWidth, 0)
        };
        FlowLayoutPanel buttonsPanel = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0)
        };
        buttonsPanel.Controls.Add(btnOk);
        buttonsPanel.Controls.Add(btnCancel);
        root.Controls.Add(buttonsPanel, 0, 2);

        Controls.Add(root);
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        DialogLayout.LockSizeAfterLoad(this);
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
