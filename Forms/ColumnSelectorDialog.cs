namespace MBScript.Forms;

public class ColumnSelectorDialog : Form
{
    private readonly string[] _auditCols = ["dat_utente_cre", "cod_utente_cre", "dat_utente_mod", "cod_utente_mod", "cod_azienda_cre"];

    private CheckedListBox clbKeys = null!;
    private CheckedListBox clbCols = null!;

    public List<string> SelectedKeyColumns { get; private set; } = new();
    public List<string> SelectedColumns    { get; private set; } = new();
    public bool ConditionalUpdate { get; private set; } = false;

    public ColumnSelectorDialog(IEnumerable<string> allColumns, IEnumerable<string> keyColumns, string tableName, string scriptType = "INSERT", bool defaultConditional = false)
    {
        var keys    = keyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allCols = allColumns.ToList();
        bool isDel  = scriptType.Equals("DELETE", StringComparison.OrdinalIgnoreCase);

        Text            = $"Seleziona colonne per {scriptType} – {tableName}";
        Size            = new Size(620, 520);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;
        Font            = new Font("Segoe UI", 9f);

        // ── Chiavi ────────────────────────────────────────────────────────
        var btnAllKeys = new Button { Text = "*", Location = new Point(12, 8), Size = new Size(24, 22), TabStop = false };
        var btnNoneKeys = new Button { Text = "Ø", Location = new Point(40, 8), Size = new Size(24, 22), TabStop = false };
        Controls.Add(new Label { Text = "Chiavi", Location = new Point(72, 12), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
        clbKeys = new CheckedListBox
        {
            Location     = new Point(12, 36),
            Size         = new Size(isDel ? 570 : 270, 380),
            CheckOnClick = true,
            Font         = new Font("Courier New", 9f),
            ForeColor    = Color.DarkRed
        };
        foreach (var c in allCols.Where(c => keys.Contains(c)))
            clbKeys.Items.Add(c, true);
        btnAllKeys.Click += (_, _) => { for (int i = 0; i < clbKeys.Items.Count; i++) clbKeys.SetItemChecked(i, true); };
        btnNoneKeys.Click += (_, _) => { for (int i = 0; i < clbKeys.Items.Count; i++) clbKeys.SetItemChecked(i, false); };
        Controls.Add(btnAllKeys);
        Controls.Add(btnNoneKeys);
        Controls.Add(clbKeys);

        // ── Colonne normali (nascoste per DELETE) ─────────────────────────
        if (!isDel)
        {
            var btnAllCols = new Button { Text = "*", Location = new Point(298, 8), Size = new Size(24, 22), TabStop = false };
            var btnNoneCols = new Button { Text = "Ø", Location = new Point(328, 8), Size = new Size(24, 22), TabStop = false };
            Controls.Add(new Label { Text = "Colonne", Location = new Point(360, 12), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
            clbCols = new CheckedListBox
            {
                Location     = new Point(298, 36),
                Size         = new Size(284, 380),
                CheckOnClick = true,
                Font         = new Font("Courier New", 9f)
            };
            foreach (var c in allCols.Where(c => !keys.Contains(c)))
                clbCols.Items.Add(c, !_auditCols.Contains(c.ToLowerInvariant()));
            btnAllCols.Click += (_, _) => { for (int i = 0; i < clbCols.Items.Count; i++) clbCols.SetItemChecked(i, true); };
            btnNoneCols.Click += (_, _) => { for (int i = 0; i < clbCols.Items.Count; i++) clbCols.SetItemChecked(i, false); };
            Controls.Add(btnAllCols);
            Controls.Add(btnNoneCols);
            Controls.Add(clbCols);
        }
        else
        {
            clbCols = new CheckedListBox(); // unused but not null
        }

        // ── Buttons ───────────────────────────────────────────────────────
        CheckBox chkConditional = null!;
        if (scriptType.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            chkConditional = new CheckBox { Text = "Genera UPDATE condizionato (controllo chiavi + campi modificati)", Location = new Point(12, 420), AutoSize = true };
            // initialize with provided default
            chkConditional.Checked = defaultConditional;
            ConditionalUpdate = defaultConditional;
            Controls.Add(chkConditional);
        }

        // OK/Cancel buttons (fixed positions to avoid alignment issues)
        var btnOk = new Button { Text = "Ok", DialogResult = DialogResult.OK, Location = new Point(220, 450), Size = new Size(90, 28) };
        var btnCancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, Location = new Point(320, 450), Size = new Size(90, 28) };
        btnOk.Click += (_, _) =>
        {
            SelectedKeyColumns = clbKeys.CheckedItems.Cast<string>().ToList();
            SelectedColumns    = clbCols.CheckedItems.Cast<string>().ToList();
            if (chkConditional is not null) ConditionalUpdate = chkConditional.Checked;
        };
        Controls.Add(btnOk);
        Controls.Add(btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }
}
