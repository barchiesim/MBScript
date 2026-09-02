using MBScript.Models;

namespace MBScript.Forms;

/// <summary>
/// Menu a tendina dell'intestazione di colonna in stile datasheet Access:
/// ordinamento, rimozione filtro, sottomenu "Filtri per testo" e elenco dei
/// valori distinti con caselle di spunta più i pulsanti OK/Annulla.
/// </summary>
public class AccessColumnMenu
{
    #region Costanti

    private const string BlankLabel = "(Vuoti)";
    private const string SelectAllLabel = "(Seleziona tutto)";
    private const int MaxListHeight = 220;
    private const int MinListWidth = 190;
    private const int MaxListWidth = 340;

    #endregion Costanti

    #region Campi

    private readonly string _columnName;
    private readonly List<string?> _values;
    private readonly ColumnFilter? _current;
    private readonly ToolStripDropDown _dropDown;
    private readonly CheckedListBox _list;
    private readonly FlowLayoutPanel _buttonBar;
    private ToolStripControlHost _listHost = null!;
    private ToolStripControlHost _buttonHost = null!;
    private bool _syncingChecks;

    #endregion Campi

    #region Eventi

    /// <summary>Ordinamento richiesto (true = crescente).</summary>
    public event Action<bool>? SortRequested;

    /// <summary>Filtro confermato; <c>null</c> significa "rimuovi il filtro dalla colonna".</summary>
    public event Action<ColumnFilter?>? FilterApplied;

    #endregion Eventi

    #region Costruttore

    /// <param name="columnName">Nome della colonna a cui si riferisce il menu.</param>
    /// <param name="distinctValues">Valori distinti già ordinati; <c>null</c> rappresenta i valori vuoti.</param>
    /// <param name="current">Filtro attualmente applicato alla colonna, se presente.</param>
    public AccessColumnMenu(string columnName, List<string?> distinctValues, ColumnFilter? current)
    {
        _columnName = columnName;
        _values = distinctValues;
        _current = current;

        _list = BuildValueList();
        _buttonBar = BuildButtonBar();
        _dropDown = BuildDropDown();
    }

    #endregion Costruttore

    #region Visualizzazione

    /// <summary>Apre il menu ancorandolo al bordo inferiore dell'intestazione.</summary>
    public void Show(Control owner, Rectangle headerRectangle)
    {
        _dropDown.Show(owner, new Point(headerRectangle.Left, headerRectangle.Bottom));
        StretchHostedControls();
        _list.Focus();
    }

    /// <summary>Allarga elenco e pulsanti alla larghezza effettiva del menu, che dipende
    /// dalla voce di testo più lunga e si conosce solo dopo l'apertura.</summary>
    private void StretchHostedControls()
    {
        int target = _dropDown.ClientSize.Width - _listHost.Margin.Horizontal - 2;
        if (target <= _list.Width) return;

        _list.Width = target;
        _listHost.Size = _list.Size;
        _buttonBar.Width = target;
        _buttonHost.Size = _buttonBar.Size;
        _dropDown.PerformLayout();
    }

    #endregion Visualizzazione

    #region Costruzione menu

    private ToolStripDropDown BuildDropDown()
    {
        // ToolStripDropDownMenu (e non ToolStripDropDown) per avere il layout dei menu
        // veri: voci a tutta larghezza, margine icone e freccia dei sottomenu.
        ToolStripDropDownMenu dd = new ToolStripDropDownMenu
        {
            AutoClose = true,
            DropShadowEnabled = true,
            ShowImageMargin = false
        };
        // Il menu è usa-e-getta: la distruzione è differita al message pump per non
        // liberare i controlli ospitati mentre la chiusura è ancora in corso.
        dd.Closed += (_, _) => dd.BeginInvoke(new Action(dd.Dispose));

        ToolStripMenuItem miSortAsc = new ToolStripMenuItem("Ordina dalla A alla Z");
        miSortAsc.Click += (_, _) => { dd.Close(); SortRequested?.Invoke(true); };

        ToolStripMenuItem miSortDesc = new ToolStripMenuItem("Ordina dalla Z alla A");
        miSortDesc.Click += (_, _) => { dd.Close(); SortRequested?.Invoke(false); };

        ToolStripMenuItem miClear = new ToolStripMenuItem($"Cancella filtro da {_columnName}")
        {
            Enabled = _current is not null && !_current.IsEmpty
        };
        miClear.Click += (_, _) => { dd.Close(); FilterApplied?.Invoke(null); };

        ToolStripMenuItem miTextFilters = new ToolStripMenuItem("Filtri per testo");
        foreach (KeyValuePair<string, ColumnFilterOperator> entry in TextFilterEntries())
        {
            ColumnFilterOperator op = entry.Value;
            ToolStripMenuItem mi = new ToolStripMenuItem(entry.Key);
            mi.Click += (_, _) => { dd.Close(); PromptForCriteria(op); };
            miTextFilters.DropDownItems.Add(mi);
        }

        dd.Items.Add(miSortAsc);
        dd.Items.Add(miSortDesc);
        dd.Items.Add(new ToolStripSeparator());
        dd.Items.Add(miClear);
        dd.Items.Add(miTextFilters);
        _listHost = new ToolStripControlHost(_list)
        {
            Margin = new Padding(2, 0, 2, 0),
            AutoSize = false,
            Size = _list.Size
        };
        _buttonHost = new ToolStripControlHost(_buttonBar)
        {
            Margin = new Padding(2, 0, 2, 0),
            AutoSize = false,
            Size = _buttonBar.Size
        };

        dd.Items.Add(new ToolStripSeparator());
        dd.Items.Add(_listHost);
        dd.Items.Add(new ToolStripSeparator());
        dd.Items.Add(_buttonHost);

        return dd;
    }

    /// <summary>Voci del sottomenu "Filtri per testo", nell'ordine usato da Access.</summary>
    private static List<KeyValuePair<string, ColumnFilterOperator>> TextFilterEntries() => new()
    {
        new("Uguale a...", ColumnFilterOperator.Equals),
        new("Diverso da...", ColumnFilterOperator.NotEquals),
        new("Inizia con...", ColumnFilterOperator.BeginsWith),
        new("Non inizia con...", ColumnFilterOperator.NotBeginsWith),
        new("Contiene...", ColumnFilterOperator.Contains),
        new("Non contiene...", ColumnFilterOperator.NotContains),
        new("Termina con...", ColumnFilterOperator.EndsWith),
        new("Non termina con...", ColumnFilterOperator.NotEndsWith)
    };

    private CheckedListBox BuildValueList()
    {
        CheckedListBox list = new CheckedListBox
        {
            CheckOnClick = true,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9f),
            IntegralHeight = false,
            ThreeDCheckBoxes = false
        };

        list.Items.Add(SelectAllLabel);
        foreach (string? v in _values)
            list.Items.Add(v ?? BlankLabel);

        bool allChecked = _current is null || !_current.HasValueList;
        list.SetItemChecked(0, allChecked);
        for (int i = 0; i < _values.Count; i++)
        {
            bool isChecked = allChecked || _current!.AllowedValues!.Contains(_values[i]);
            list.SetItemChecked(i + 1, isChecked);
        }

        list.Size = new Size(MeasureListWidth(list), MeasureListHeight(list));
        list.ItemCheck += OnItemCheck;
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            ApplyValueSelection();
        };

        return list;
    }

    /// <summary>Barra OK/Annulla allineata a destra: il flusso destra→sinistra
    /// mantiene i pulsanti a filo anche quando il menu viene allargato.</summary>
    private FlowLayoutPanel BuildButtonBar()
    {
        FlowLayoutPanel panel = new FlowLayoutPanel
        {
            Size = new Size(_list.Width, 32),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 4, 2, 0),
            BackColor = SystemColors.Control
        };

        Button btnCancel = new Button { Text = "Annulla", Size = new Size(76, 24), Margin = new Padding(2, 0, 0, 0) };
        Button btnOk = new Button { Text = "OK", Size = new Size(76, 24), Margin = new Padding(2, 0, 0, 0) };
        btnOk.Click += (_, _) => ApplyValueSelection();
        btnCancel.Click += (_, _) => _dropDown.Close();

        panel.Controls.Add(btnCancel);
        panel.Controls.Add(btnOk);
        return panel;
    }

    #endregion Costruzione menu

    #region Interazione

    /// <summary>Tiene sincronizzata la voce "(Seleziona tutto)" con le singole spunte.</summary>
    private void OnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_syncingChecks) return;

        bool willBeChecked = e.NewValue == CheckState.Checked;
        _syncingChecks = true;
        try
        {
            if (e.Index == 0)
            {
                for (int i = 1; i < _list.Items.Count; i++)
                    _list.SetItemChecked(i, willBeChecked);
            }
            else
            {
                bool allChecked = willBeChecked;
                for (int i = 1; i < _list.Items.Count && allChecked; i++)
                {
                    if (i == e.Index) continue;
                    allChecked = _list.GetItemChecked(i);
                }
                _list.SetItemChecked(0, allChecked);
            }
        }
        finally { _syncingChecks = false; }
    }

    private void ApplyValueSelection()
    {
        // La selezione va letta prima di chiudere: la chiusura libera i controlli ospitati.
        List<string?> selected = new();
        for (int i = 0; i < _values.Count; i++)
        {
            if (_list.GetItemChecked(i + 1))
                selected.Add(_values[i]);
        }

        _dropDown.Close();

        // Tutti i valori spuntati equivale ad assenza di filtro sui valori:
        // si conserva l'eventuale criterio testuale già presente.
        ColumnFilter filter = new()
        {
            Operator = _current?.Operator ?? ColumnFilterOperator.None,
            Value = _current?.Value ?? "",
            AllowedValues = selected.Count == _values.Count ? null : selected
        };
        FilterApplied?.Invoke(filter.IsEmpty ? null : filter);
    }

    private void PromptForCriteria(ColumnFilterOperator op)
    {
        string initial = _current is not null && _current.Operator == op ? _current.Value : "";
        using FilterCriteriaDialog dlg = new(_columnName, op, initial);
        if (dlg.ShowDialog() != DialogResult.OK) return;

        ColumnFilter filter = new()
        {
            AllowedValues = _current?.AllowedValues,
            Operator = op,
            Value = dlg.CriteriaValue
        };
        FilterApplied?.Invoke(filter.IsEmpty ? null : filter);
    }

    #endregion Interazione

    #region Misure

    private static int MeasureListWidth(CheckedListBox list)
    {
        int widest = MinListWidth;
        foreach (object item in list.Items)
        {
            int w = TextRenderer.MeasureText(Convert.ToString(item) ?? "", list.Font).Width + 28;
            if (w > widest) widest = w;
        }
        return Math.Min(MaxListWidth, widest);
    }

    private static int MeasureListHeight(CheckedListBox list)
    {
        int itemHeight = list.ItemHeight > 0 ? list.ItemHeight : 16;
        return Math.Min(MaxListHeight, Math.Max(itemHeight * 2, itemHeight * list.Items.Count + 4));
    }

    #endregion Misure
}
