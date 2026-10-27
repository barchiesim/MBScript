namespace MBScript.Forms;

public class AuditFilterDialog : Form
{
    #region Campi

    private TextBox txtFilter  = null!;
    private TextBox txtExclude = null!;

    #endregion Campi

    #region Proprietà

    public string AuditFilter  => txtFilter.Text;
    public string AuditExclude => txtExclude.Text;

    #endregion Proprietà

    #region Costruttore

    public AuditFilterDialog(string selectedDb, string initialFilter, string initialExclude)
    {
        // Layout a pannelli con misure ricavate dal testo (vedi DialogLayout).
        AutoScaleMode = AutoScaleMode.None;

        Text            = "Configurazione Filtri Audit";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual;
        Font            = new Font("Segoe UI", 9f);
        Padding         = new Padding(DialogLayout.Gap(this, 1f));

        Font boldFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        Font codeFont = new Font("Courier New", 9f);
        int gap = DialogLayout.Gap(this);
        int textWidth = TextRenderer.MeasureText(new string('W', 72), codeFont).Width;

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label lblDb = new Label { Text = $"Database: {selectedDb}  →  {selectedDb}_UPD", AutoSize = true, Font = boldFont, Margin = new Padding(3, 0, 3, gap * 2) };
        root.Controls.Add(lblDb, 0, 0);
        root.SetColumnSpan(lblDb, 2);

        Label lblFilter = new Label { Text = "FILTRO1 – Condizione SQL per includere tabelle:", AutoSize = true, Font = boldFont, Margin = new Padding(3, 0, 3, gap) };
        root.Controls.Add(lblFilter, 0, 1);
        root.SetColumnSpan(lblFilter, 2);

        txtFilter = new TextBox { Text = initialFilter, Font = codeFont, Width = textWidth, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 0, 3, gap * 3) };
        root.Controls.Add(txtFilter, 0, 2);
        root.SetColumnSpan(txtFilter, 2);

        Label lblExclude = new Label { Text = "ESCLUDI1 – Tabelle da escludere:", AutoSize = true, Font = boldFont, Margin = new Padding(3, 0, 3, gap) };
        root.Controls.Add(lblExclude, 0, 3);
        root.SetColumnSpan(lblExclude, 2);

        txtExclude = new TextBox { Text = initialExclude, Font = codeFont, Width = textWidth, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3, 0, 3, gap * 4) };
        root.Controls.Add(txtExclude, 0, 4);
        root.SetColumnSpan(txtExclude, 2);

        Button btnReset = new Button { Text = "🔄 Ripristina Standard", AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(gap, 0, gap, 0) };
        btnReset.Click += (_, _) =>
        {
            txtFilter.Text  = "and (SUBSTRING(name,3,1) = '_')";
            txtExclude.Text = "and name NOT IN ('NUMBERS', 'FW_ASYNC_SCHEDULER', 'FW_PROCESSING_DETAILS')";
        };
        root.Controls.Add(btnReset, 0, 5);

        int buttonWidth = DialogLayout.ButtonWidth(this, "Inizializza", "Annulla");
        Button btnOk     = new Button { Text = "Inizializza", DialogResult = DialogResult.OK,     AutoSize = true, MinimumSize = new Size(buttonWidth, 0) };
        Button btnCancel = new Button { Text = "Annulla",     DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(buttonWidth, 0) };
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
        root.Controls.Add(buttonsPanel, 1, 5);

        Controls.Add(root);
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        DialogLayout.LockSizeAfterLoad(this);
    }

    #endregion Costruttore
}
