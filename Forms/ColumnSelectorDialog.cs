namespace MBScript.Forms;

public class ColumnSelectorDialog : Form
{
    #region Campi

    private readonly string[] _auditCols = ["dat_utente_cre", "cod_utente_cre", "dat_utente_mod", "cod_utente_mod", "cod_azienda_cre"];

    private CheckedListBox clbKeys = null!;
    private CheckedListBox clbCols = null!;

    #endregion Campi

    #region Proprietà

    public List<string> SelectedKeyColumns { get; private set; } = new();
    public List<string> SelectedColumns    { get; private set; } = new();
    public bool ConditionalUpdate { get; private set; } = false;

    #endregion Proprietà

    #region Costruttore

    public ColumnSelectorDialog(IEnumerable<string> allColumns, IEnumerable<string> keyColumns, string tableName, string scriptType = "INSERT", bool defaultConditional = false)
    {
        HashSet<string> keys = keyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> allCols = allColumns.ToList();
        bool isDel = scriptType.Equals("DELETE", StringComparison.OrdinalIgnoreCase);
        bool isUpd = scriptType.Equals("UPDATE", StringComparison.OrdinalIgnoreCase);

        // Layout a pannelli con misure ricavate dal testo (vedi DialogLayout): niente
        // coordinate assolute, quindi nessuna sovrapposizione a qualunque DPI.
        AutoScaleMode = AutoScaleMode.None;

        Text            = $"Seleziona colonne per {scriptType} – {tableName}";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual;
        Font            = new Font("Segoe UI", 9f);
        Padding         = new Padding(DialogLayout.Gap(this, 0.75f));

        int columnCount = isDel ? 1 : 2;
        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = columnCount,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        for (int i = 0; i < columnCount; i++)
        {
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columnCount));
        }

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        #region Liste

        clbKeys = BuildList(allCols.Where(c => keys.Contains(c)), _ => true);
        clbKeys.ForeColor = Color.DarkRed;
        root.Controls.Add(BuildHeader("Chiavi", clbKeys), 0, 0);
        root.Controls.Add(clbKeys, 0, 1);

        if (!isDel)
        {
            clbCols = BuildList(allCols.Where(c => !keys.Contains(c)), c => !_auditCols.Contains(c.ToLowerInvariant()));
            root.Controls.Add(BuildHeader("Colonne", clbCols), 1, 0);
            root.Controls.Add(clbCols, 1, 1);
        }
        else
        {
            clbCols = new CheckedListBox(); // unused but not null
        }

        SizeLists(allCols, columnCount);

        #endregion Liste

        #region Pulsanti

        CheckBox? chkConditional = null;
        if (isUpd)
        {
            chkConditional = new CheckBox
            {
                Text = "Genera UPDATE condizionato (controllo chiavi + campi modificati)",
                AutoSize = true,
                Checked = defaultConditional,
                Margin = new Padding(3, DialogLayout.Gap(this), 3, 3)
            };
            ConditionalUpdate = defaultConditional;
            root.Controls.Add(chkConditional, 0, 2);
            root.SetColumnSpan(chkConditional, columnCount);
        }

        int buttonWidth = DialogLayout.ButtonWidth(this, "Ok", "Annulla");
        Button btnOk = new Button { Text = "Ok", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(buttonWidth, 0) };
        Button btnCancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(buttonWidth, 0) };
        btnOk.Click += (_, _) =>
        {
            SelectedKeyColumns = clbKeys.CheckedItems.Cast<string>().ToList();
            SelectedColumns    = clbCols.CheckedItems.Cast<string>().ToList();
            if (chkConditional is not null)
            {
                ConditionalUpdate = chkConditional.Checked;
            }

        };

        FlowLayoutPanel buttonsPanel = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, DialogLayout.Gap(this, 0.5f), 0, 0)
        };
        buttonsPanel.Controls.Add(btnOk);
        buttonsPanel.Controls.Add(btnCancel);
        root.Controls.Add(buttonsPanel, 0, 3);
        root.SetColumnSpan(buttonsPanel, columnCount);

        #endregion Pulsanti

        Controls.Add(root);
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        DialogLayout.LockSizeAfterLoad(this);
    }

    #endregion Costruttore

    #region Costruzione controlli

    private CheckedListBox BuildList(IEnumerable<string> items, Func<string, bool> isChecked)
    {
        CheckedListBox list = new()
        {
            CheckOnClick = true,
            IntegralHeight = false,
            Font = new Font("Courier New", 9f),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(3)
        };
        foreach (string item in items)
        {
            list.Items.Add(item, isChecked(item));
        }

        return list;
    }

    /// <summary>Intestazione della lista: pulsanti seleziona tutto / nessuno + titolo.</summary>
    private FlowLayoutPanel BuildHeader(string title, CheckedListBox list)
    {
        int side = Font.Height + DialogLayout.Gap(this) * 2;
        Button btnAll = new Button { Text = "*", TabStop = false, Size = new Size(side, side), Margin = new Padding(0, 0, DialogLayout.Gap(this), 0) };
        Button btnNone = new Button { Text = "Ø", TabStop = false, Size = new Size(side, side), Margin = new Padding(0, 0, DialogLayout.Gap(this, 0.5f), 0) };
        btnAll.Click += (_, _) => SetAllChecked(list, true);
        btnNone.Click += (_, _) => SetAllChecked(list, false);

        Label lblTitle = new Label
        {
            Text = title,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Margin = new Padding(0)
        };

        FlowLayoutPanel header = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 0, 3, DialogLayout.Gap(this, 0.5f))
        };
        header.Controls.Add(btnAll);
        header.Controls.Add(btnNone);
        header.Controls.Add(lblTitle);
        return header;
    }

    /// <summary>
    /// Dimensione iniziale delle liste: larga quanto il nome colonna più lungo
    /// (almeno 24 caratteri) e alta per circa 18 righe, entro l'area dello schermo.
    /// </summary>
    private void SizeLists(List<string> allCols, int columnCount)
    {
        Font listFont = clbKeys.Font;
        int textWidth = TextRenderer.MeasureText(new string('W', 24), listFont).Width;
        foreach (string col in allCols)
        {
            textWidth = Math.Max(textWidth, TextRenderer.MeasureText(col, listFont).Width);
        }

        // Glifo della checkbox (~altezza riga) + scrollbar verticale + margine interno.
        int width = textWidth + listFont.Height * 2 + SystemInformation.VerticalScrollBarWidth;
        int visibleRows = Math.Clamp(allCols.Count, 10, 18);
        int height = clbKeys.ItemHeight * visibleRows + SystemInformation.Border3DSize.Height * 4;

        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        width = Math.Min(width, area.Width * 9 / 10 / columnCount);
        height = Math.Min(height, area.Height * 6 / 10);

        Size size = new Size(width, height);
        clbKeys.Size = size;
        clbCols.Size = size;
    }

    private static void SetAllChecked(CheckedListBox list, bool value)
    {
        for (int i = 0; i < list.Items.Count; i++)
        {
            list.SetItemChecked(i, value);
        }

    }

    #endregion Costruzione controlli
}
