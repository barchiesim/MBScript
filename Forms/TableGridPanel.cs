using MBScript.Models;
using MBScript.Services;
using System.Data;
using System.Text;
using static MBScript.Forms.SqlScriptHelpers;

namespace MBScript.Forms;

/// <summary>
/// Griglia completa per una tabella aperta: dati, barra di navigazione stile Access,
/// filtro/ordinamento da menu colonna, tasto destro (copia, duplica, incolla, elimina),
/// inserimento e modifica righe. Ogni scheda dell'area risultati ne ospita una propria
/// istanza indipendente, così tutte le tabelle aperte restano ugualmente operative.
/// </summary>
public class TableGridPanel : UserControl
{
    #region Dipendenze e stato tabella

    private readonly SqlService _sql;
    private readonly DatabaseExplorerService _dbExplorer;
    private readonly Func<string> _getDatabase;
    private readonly Action<string> _addMessage;
    private readonly Action<bool> _setLoading;

    private TableInfo? _tableInfo;
    private List<string> _keyColumns = new();
    private HashSet<string> _identityColumns = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _nonInsertableColumns = new(StringComparer.OrdinalIgnoreCase);
    private List<Dictionary<string, object?>> _queryResult = new();

    public TableInfo? Table => _tableInfo;
    public List<string> KeyColumns => _keyColumns;
    public HashSet<string> IdentityColumns => _identityColumns;
    public List<Dictionary<string, object?>> QueryResult => _queryResult;
    public DataGridView Grid => _grid;

    #endregion Dipendenze e stato tabella

    #region Campi UI e griglia

    private const string RowIdxColumn = "__RowIdx";
    private const int MaxDistinctFilterValues = 1000;
    private const int HeaderArrowZoneWidth = 18;

    // Tavolozza dell'intestazione: un blu pieno invece del grigio piatto precedente.
    // I glifi (imbuto/ordinamento/freccia) e il tinteggio di selezione colonna usano
    // tonalità più chiare per restare leggibili sopra questo sfondo.
    private static readonly Color HeaderBackColor = Color.FromArgb(42, 90, 150);
    private static readonly Color HeaderForeColor = Color.White;
    private static readonly Color HeaderSelectedBackColor = Color.FromArgb(76, 130, 196);

    // Selezione di celle/righe/riga nel corpo della griglia: un azzurro più chiaro e
    // vivo del precedente, della stessa famiglia del blu dell'intestazione.
    private static readonly Color CellSelectionBackColor = Color.FromArgb(160, 210, 255);
    private static readonly Color RowHeaderSelectionBackColor = Color.FromArgb(190, 225, 255);

    private readonly BufferedDataGridView _grid;
    private readonly BindingSource _bindingSource = new();
    private DataTable? _dataTable;
    private readonly HashSet<DataRow> _insertedNewRows = new();
    private readonly HashSet<DataRow> _savingNewRows = new();
    private readonly Dictionary<string, ColumnFilter> _columnFilters = new(StringComparer.OrdinalIgnoreCase);
    // Colonne ordinate, nell'ordine di ordinamento (prima chiave, seconda chiave, ...).
    // Una sola voce per l'ordinamento da menu di colonna; più voci per l'ordinamento
    // combinato su una selezione di colonne stile Access.
    private List<string> _sortColumns = new();
    private bool _sortAscending = true;
    private bool _filtersSuspended;
    private int _markerRowIndex = -1;
    private bool _cancellingEdit;

    // Selezione di colonne stile Access: un intervallo contiguo in ordine di
    // visualizzazione fra un'ancora (primo clic) e l'ultima colonna toccata
    // (Shift+clic per estendere). Usata per l'ordinamento su più colonne insieme.
    private int? _columnSelectionAnchorDisplayIndex;
    private int? _columnSelectionEndDisplayIndex;
    private bool _busy;

    private readonly Panel _navPanel;
    private readonly Label _lblNavPosition;
    private readonly TextBox _txtNavRecord;
    private readonly TextBox _txtNavSearch;
    private readonly Button _btnNavFilterState;
    private readonly ContextMenuStrip _contextMenu;

    // Disposizione colonne (ordine + larghezza): persistita per tabella e riapplicata
    // ogni volta che la si riapre.
    private readonly System.Windows.Forms.Timer _layoutSaveTimer;
    private bool _applyingSavedLayout;

    #endregion Campi UI e griglia

    #region Costruttore

    public TableGridPanel(SqlService sql, DatabaseExplorerService dbExplorer, Func<string> getDatabase,
        Action<string> addMessage, Action<bool> setLoading)
    {
        _sql = sql;
        _dbExplorer = dbExplorer;
        _getDatabase = getDatabase;
        _addMessage = addMessage;
        _setLoading = setLoading;

        Dock = DockStyle.Fill;

        _grid = BuildGrid();
        _contextMenu = BuildGridContextMenu();
        _grid.ContextMenuStrip = _contextMenu;

        // Debounce: un trascinamento o un ridimensionamento genera molti eventi in
        // rapida sequenza, non serve scrivere su disco ad ogni pixel.
        _layoutSaveTimer = new System.Windows.Forms.Timer { Interval = 600 };
        _layoutSaveTimer.Tick += (_, _) => { _layoutSaveTimer.Stop(); SaveColumnLayout(); };
        _grid.ColumnDisplayIndexChanged += (_, _) => ScheduleLayoutSave();
        _grid.ColumnWidthChanged += (_, _) => ScheduleLayoutSave();

        (_navPanel, _lblNavPosition, _txtNavRecord, _txtNavSearch, _btnNavFilterState) = BuildNavBar();

        Controls.Add(_navPanel);
        Controls.Add(_grid);
        // Il controllo Fill va portato in primo piano, altrimenti il layout gli assegna
        // tutta l'area del pannello e la barra di navigazione agganciata lo copre.
        _grid.BringToFront();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bindingSource.Dispose();
            _layoutSaveTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    #endregion Costruttore

    #region Guard helper

    private async Task GuardAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (Exception ex) { _addMessage($"❌ Errore: {ex.Message}"); }
        finally { _busy = false; }
    }

    #endregion Guard helper

    #region Costruzione griglia

    private BufferedDataGridView BuildGrid()
    {
        BufferedDataGridView grid = new()
        {
            Dock = DockStyle.Fill,
            ReadOnly = false,
            AllowUserToAddRows = true,
            // RowHeaderSelect = comportamento Access: clic sul selettore di riga seleziona
            // tutta la riga (Shift/Ctrl per più righe), le celle restano selezionabili
            // e modificabili singolarmente. Con CellSelect SelectedRows resta vuoto.
            SelectionMode = DataGridViewSelectionMode.RowHeaderSelect,
            MultiSelect = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            Font = new Font("Calibri", 10f),
            RowHeadersWidth = 26,
            ScrollBars = ScrollBars.Both,
            AllowUserToResizeColumns = true,
            AllowUserToResizeRows = true,
            AllowUserToOrderColumns = true,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 24,
            ColumnHeadersVisible = true,
            AutoGenerateColumns = true,
            EnableHeadersVisualStyles = false,
            DataSource = _bindingSource,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = HeaderBackColor,
                ForeColor = HeaderForeColor,
                Font = new Font("Calibri", 10f, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                SelectionBackColor = HeaderSelectedBackColor,
                SelectionForeColor = HeaderForeColor
            }
        };
        grid.RowPostPaint += (_, e) =>
        {
            // Indicatore record corrente stile Access: freccia sulla riga selezionata, altrimenti vuoto
            bool isCurrent = e.RowIndex == grid.CurrentCell?.RowIndex;
            string marker = grid.Rows[e.RowIndex].IsNewRow ? "*" : (isCurrent ? "►" : "");
            if (marker.Length == 0) return;

            // TextRenderer centra da sé: evita una MeasureString per riga a ogni frame
            Rectangle headerArea = new(e.RowBounds.Left, e.RowBounds.Top, grid.RowHeadersWidth, e.RowBounds.Height);
            TextRenderer.DrawText(e.Graphics, marker, grid.Font, headerArea, Color.Black,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        grid.CurrentCellChanged += (_, _) => MoveCurrentRowMarker();
        grid.CellEndEdit += (_, e) => _ = OnGridCellEndEditAsync(e.RowIndex, e.ColumnIndex);
        grid.RowValidated += (_, e) => _ = OnGridRowValidatedAsync(e.RowIndex);
        // Intestazioni stile Access: la freccia a destra apre il menu di colonna,
        // il resto dell'intestazione seleziona la colonna (stile Access): clic per una
        // colonna sola, Shift+clic per estendere a un intervallo di colonne adiacenti.
        // Tasto destro su una selezione di 2+ colonne ordina su tutte insieme.
        grid.CellPainting += (_, e) => PaintColumnHeader(e);
        grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex < 0) return;

            if (IsHeaderArrowClick(e.ColumnIndex, e.X))
            {
                ShowColumnFilterMenu(e.ColumnIndex);
                return;
            }

            if (e.Button == MouseButtons.Right)
            {
                ShowHeaderContextForColumn(e.ColumnIndex);
                return;
            }

            if (e.Button == MouseButtons.Left)
                SelectColumnHeader(e.ColumnIndex, extend: (Control.ModifierKeys & Keys.Shift) == Keys.Shift);
        };
        grid.CellMouseDown += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            // Interagire con una riga di dati abbandona la selezione di colonne in corso.
            ClearColumnSelection();
            if (e.Button == MouseButtons.Right && e.ColumnIndex >= 0)
                grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
        };

        // Ctrl+C è gestito dal DataGridView: senza il testo delle intestazioni, così il
        // contenuto degli appunti è riutilizzabile da Ctrl+V.
        grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;

        grid.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.V && !grid.IsCurrentCellInEditMode)
            {
                e.Handled = true;
                _ = GuardAsync(PasteFromClipboardIntoGridAsync);
                return;
            }
            if (e.KeyCode == Keys.Delete && !grid.IsCurrentCellInEditMode)
            {
                e.Handled = true;
                _ = GuardAsync(DeleteSelectedGridRowsAsync);
                return;
            }
            if (e.KeyCode != Keys.Escape) return;
            try
            {
                _cancellingEdit = true;
                if (grid.IsCurrentCellInEditMode)
                    grid.CancelEdit();
                DataRow? dr = GetDataRowAt(grid.CurrentCell?.RowIndex ?? -1);
                if (dr is not null && dr.RowState == DataRowState.Added && !_insertedNewRows.Contains(dr))
                {
                    _savingNewRows.Remove(dr);
                    dr.RejectChanges();
                }
                else
                {
                    _bindingSource.CancelEdit();
                }
                e.Handled = true;
            }
            catch { /* best-effort cancel */ }
            finally { _cancellingEdit = false; }
        };

        // ── Colori righe alternate (stile Access: alternanza molto tenue) ──
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(247, 247, 247)
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White,
            ForeColor = Color.FromArgb(20, 20, 20),
            SelectionBackColor = CellSelectionBackColor,
            SelectionForeColor = Color.FromArgb(20, 20, 20)
        };
        grid.GridColor = Color.FromArgb(212, 212, 212);
        grid.RowHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(240, 240, 240),
            ForeColor = Color.FromArgb(30, 30, 30),
            // Il selettore della riga selezionata si evidenzia, come in Access
            SelectionBackColor = RowHeaderSelectionBackColor,
            SelectionForeColor = Color.FromArgb(30, 30, 30)
        };
        // Griglia con linee sottili su tutte le celle (stile datasheet Access)
        grid.RowTemplate.Height = 21;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.Single;
        grid.DefaultCellStyle.Padding = new Padding(3, 1, 3, 1);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(3, 0, 3, 0);
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.FixedSingle;

        grid.DataBindingComplete += (_, _) => EnsureColumnHeaders();

        return grid;
    }

    private (Panel panel, Label lblPosition, TextBox txtRecord, TextBox txtSearch, Button btnFilterState) BuildNavBar()
    {
        // Layout: Record: |◄ ◄ [n] di N ► ►| ►*   Nessun filtro   Cerca: [   ]
        Panel navPanel = new() { Dock = DockStyle.Bottom, Height = 28, BackColor = Color.FromArgb(240, 240, 240) };
        FlowLayoutPanel flowNav = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4, 3, 0, 0)
        };

        static Button BuildNavButton(NavGlyph glyph, string tooltip)
        {
            Button b = new()
            {
                Text = "",
                Width = 24,
                Height = 22,
                Margin = new Padding(0, 0, 1, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(240, 240, 240),
                TabStop = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(215, 228, 244);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(195, 215, 240);
            // I glifi sono disegnati a mano: i caratteri Unicode ◄ ► dipendono dal
            // fallback dei font e risultano illeggibili a questa dimensione.
            b.Paint += (s, e) => DrawNavGlyph(e.Graphics, ((Button)s!).ClientRectangle, glyph, ((Button)s!).Enabled);
            new ToolTip().SetToolTip(b, tooltip);
            return b;
        }

        static Label BuildNavLabel(string text, int width) => new()
        {
            Text = text,
            AutoSize = false,
            Width = width,
            Height = 22,
            Margin = new Padding(2, 0, 2, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 8f),
            ForeColor = Color.FromArgb(40, 40, 40)
        };

        Button btnNavFirst = BuildNavButton(NavGlyph.First, "Primo record");
        Button btnNavPrev = BuildNavButton(NavGlyph.Previous, "Record precedente");
        Button btnNavNext = BuildNavButton(NavGlyph.Next, "Record successivo");
        Button btnNavLast = BuildNavButton(NavGlyph.Last, "Ultimo record");
        Button btnNavNew = BuildNavButton(NavGlyph.New, "Nuovo record (vuoto)");

        TextBox txtRecord = new()
        {
            Width = 42,
            Height = 22,
            Margin = new Padding(2, 0, 2, 0),
            TextAlign = HorizontalAlignment.Center,
            Font = new Font("Segoe UI", 8f),
            BorderStyle = BorderStyle.FixedSingle
        };
        Label lblPosition = BuildNavLabel("di 0", 52);

        Button btnFilterState = new()
        {
            Text = "Nessun filtro",
            Width = 86,
            Height = 22,
            Margin = new Padding(10, 0, 2, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(240, 240, 240),
            Font = new Font("Segoe UI", 8f),
            TextAlign = ContentAlignment.MiddleCenter,
            TabStop = false,
            Enabled = false
        };
        btnFilterState.FlatAppearance.BorderSize = 0;
        btnFilterState.FlatAppearance.MouseOverBackColor = Color.FromArgb(215, 228, 244);

        TextBox txtSearch = new()
        {
            Width = 150,
            Height = 22,
            Margin = new Padding(2, 0, 2, 0),
            Font = new Font("Segoe UI", 8f),
            BorderStyle = BorderStyle.FixedSingle
        };

        btnNavFirst.Click += async (_, _) => await GuardAsync(() => NavigateGridAsync(() => _bindingSource.MoveFirst()));
        btnNavPrev.Click += async (_, _) => await GuardAsync(() => NavigateGridAsync(() => _bindingSource.MovePrevious()));
        btnNavNext.Click += async (_, _) => await GuardAsync(() => NavigateGridAsync(() => _bindingSource.MoveNext()));
        btnNavLast.Click += async (_, _) => await GuardAsync(() => NavigateGridAsync(() => _bindingSource.MoveLast()));
        btnNavNew.Click += async (_, _) => await GuardAsync(AddNewGridRowAsync);
        btnFilterState.Click += (_, _) => ToggleFilterState();
        txtRecord.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            GoToRecordFromNavBox();
        };
        txtRecord.Leave += (_, _) => GoToRecordFromNavBox();
        txtSearch.TextChanged += (_, _) => SearchInGrid(txtSearch.Text);
        _bindingSource.PositionChanged += (_, _) => UpdateNavLabel();
        _bindingSource.ListChanged += (_, _) => UpdateNavLabel();

        flowNav.Controls.Add(BuildNavLabel("Record:", 50));
        flowNav.Controls.Add(btnNavFirst);
        flowNav.Controls.Add(btnNavPrev);
        flowNav.Controls.Add(txtRecord);
        flowNav.Controls.Add(lblPosition);
        flowNav.Controls.Add(btnNavNext);
        flowNav.Controls.Add(btnNavLast);
        flowNav.Controls.Add(btnNavNew);
        flowNav.Controls.Add(btnFilterState);
        flowNav.Controls.Add(BuildNavLabel("Cerca:", 40));
        flowNav.Controls.Add(txtSearch);
        navPanel.Controls.Add(flowNav);

        return (navPanel, lblPosition, txtRecord, txtSearch, btnFilterState);
    }

    #endregion Costruzione griglia

    #region Caricamento dati e metadati

    /// <summary>Carica una volta sola chiavi, identity e colonne non inseribili della
    /// tabella: restano fisse per tutta la vita di questa scheda, niente ri-derivazione
    /// da testo digitato altrove (era la causa di chiavi perse cambiando scheda).</summary>
    public async Task InitializeMetadataAsync(TableInfo? table)
    {
        _tableInfo = table;
        if (table is null) return;

        string db = _getDatabase();
        _keyColumns = await _dbExplorer.GetTableKeyColumnsAsync(db, table.TableSchema, table.TableName);
        _identityColumns = new HashSet<string>(await _dbExplorer.GetIdentityColumnsAsync(db, table.TableSchema, table.TableName), StringComparer.OrdinalIgnoreCase);
        _nonInsertableColumns = new HashSet<string>(await _dbExplorer.GetNonInsertableColumnsAsync(db, table.TableSchema, table.TableName), StringComparer.OrdinalIgnoreCase);
    }

    #region Disposizione colonne persistita

    /// <summary>Chiave con cui la disposizione delle colonne di questa tabella viene
    /// salvata e ritrovata: <c>null</c> se la scheda non è legata a una tabella
    /// (es. risultato di una query senza FROM riconoscibile).</summary>
    private string? TableKey => _tableInfo is null
        ? null
        : $"{_tableInfo.TableSchema}.{_tableInfo.TableName}".ToLowerInvariant();

    /// <summary>Riapplica l'ordine e la larghezza delle colonne salvati in precedenza
    /// per questa tabella. Le colonne non più presenti nel risultato vengono ignorate,
    /// quelle nuove restano nell'ordine di default in coda.
    /// Va chiamato quando il pannello fa già parte della gerarchia visibile (dopo che
    /// la scheda è stata aggiunta a tabResults): impostare DisplayIndex/Width prima che
    /// il controllo sia realizzato non resta valido, DataGridView li ricalcola non
    /// appena viene effettivamente mostrato.</summary>
    public void ApplySavedColumnLayout()
    {
        if (TableKey is not string key) return;
        List<GridColumnLayout>? saved = SettingsService.GetGridLayout(key);
        if (saved is null || saved.Count == 0) return;

        _applyingSavedLayout = true;
        try
        {
            int displayIndex = 0;
            foreach (GridColumnLayout entry in saved)
            {
                if (!_grid.Columns.Contains(entry.ColumnName)) continue;
                DataGridViewColumn col = _grid.Columns[entry.ColumnName];
                if (!col.Visible) continue; // la colonna tecnica non entra nell'ordine salvato
                col.DisplayIndex = Math.Min(displayIndex++, _grid.Columns.Count - 1);
                col.Width = Math.Max(20, entry.Width);
            }
        }
        finally { _applyingSavedLayout = false; }
    }

    /// <summary>Riavvia il timer di salvataggio: assorbe la raffica di eventi generata
    /// da un trascinamento o da un ridimensionamento in corso.</summary>
    private void ScheduleLayoutSave()
    {
        if (_applyingSavedLayout) return;
        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
    }

    private void SaveColumnLayout()
    {
        if (TableKey is not string key) return;

        List<GridColumnLayout> layout = _grid.Columns.Cast<DataGridViewColumn>()
            .Where(c => c.Name != RowIdxColumn)
            .OrderBy(c => c.DisplayIndex)
            .Select(c => new GridColumnLayout { ColumnName = c.Name, DisplayIndex = c.DisplayIndex, Width = c.Width })
            .ToList();

        SettingsService.SaveGridLayout(key, layout);
    }

    /// <summary>Riporta le colonne all'ordine e alla larghezza predefiniti (quello
    /// creato dalla query) e dimentica la disposizione salvata per questa tabella:
    /// la prossima volta che la si apre riparte da zero.</summary>
    public void ResetColumnLayout()
    {
        _applyingSavedLayout = true;
        try
        {
            foreach (DataGridViewColumn col in _grid.Columns)
            {
                // L'ordine di creazione (Index) coincide con l'ordine originale delle
                // colonne nel risultato della query.
                col.DisplayIndex = col.Index;
                if (col.Name == RowIdxColumn) continue;
                col.Width = Math.Min(220, Math.Max(70, col.HeaderText.Length * 9 + HeaderArrowZoneWidth));
            }
        }
        finally { _applyingSavedLayout = false; }

        if (TableKey is string key) SettingsService.RemoveGridLayout(key);
        InvalidateColumnHeaders();
    }

    #endregion Disposizione colonne persistita

    private void ResetFiltersAndSort()
    {
        _columnFilters.Clear();
        _filtersSuspended = false;
        _sortColumns.Clear();
        _sortAscending = true;
        _markerRowIndex = -1;
        _columnSelectionAnchorDisplayIndex = null;
        _columnSelectionEndDisplayIndex = null;
        _insertedNewRows.Clear();
        _savingNewRows.Clear();

        try { _bindingSource.Filter = ""; }
        catch { /* nessuna sorgente associata */ }
        try { _bindingSource.Sort = ""; }
        catch { /* nessuna sorgente associata */ }
    }

    public void Populate(List<Dictionary<string, object?>> rows)
    {
        _queryResult = rows;
        ResetFiltersAndSort();

        if (rows.Count == 0)
        {
            _dataTable = null;
            _bindingSource.DataSource = null;
            UpdateNavLabel();
            return;
        }

        _grid.SuspendLayout();

        List<string> columnNames = new(rows[0].Keys);
        DataTable dt = new();
        foreach (string name in columnNames)
            dt.Columns.Add(name, typeof(string));
        dt.Columns.Add(RowIdxColumn, typeof(string));

        // BeginLoadData sospende indici, vincoli e notifiche durante il caricamento;
        // Rows.Add(object[]) evita la ricerca della colonna per nome su ogni cella.
        dt.BeginLoadData();
        try
        {
            object[] buffer = new object[columnNames.Count + 1];
            for (int i = 0; i < rows.Count; i++)
            {
                Dictionary<string, object?> row = rows[i];
                for (int c = 0; c < columnNames.Count; c++)
                {
                    object? value = row.GetValueOrDefault(columnNames[c]);
                    buffer[c] = value is null ? DBNull.Value : Convert.ToString(value) ?? (object)DBNull.Value;
                }
                buffer[columnNames.Count] = i.ToString();
                dt.Rows.Add(buffer);
            }
        }
        finally { dt.EndLoadData(); }

        dt.AcceptChanges();
        _dataTable = dt;
        _bindingSource.DataSource = dt;

        // Imposta larghezza colonne (nasconde la colonna tecnica di indice riga)
        int totalWidth = 0;
        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (col.Name == RowIdxColumn) { col.Visible = false; continue; }
            col.HeaderText = col.Name;
            // Lo spazio della freccia del menu di colonna va aggiunto alla larghezza utile
            col.Width = Math.Min(220, Math.Max(70, col.HeaderText.Length * 9 + HeaderArrowZoneWidth));
            totalWidth += col.Width;
        }

        _grid.ResumeLayout();
        _grid.PerformLayout();

        // La disposizione salvata NON si applica qui: il pannello a questo punto non fa
        // ancora parte della gerarchia visibile (la scheda viene creata e aggiunta a
        // tabResults dopo Populate). Va richiamata da chi crea la scheda, a valle.

        // Se la larghezza totale supera la larghezza visibile serve un ridisegno.
        if (totalWidth > _grid.ClientSize.Width)
            _grid.Invalidate();

        EnsureColumnHeaders();
        UpdateNavLabel();
    }

    /// <summary>Prepara una griglia vuota con le sole colonne della tabella, pronta per
    /// l'inserimento di nuove righe: usata dopo uno script senza risultati (UPDATE/DELETE/DDL).</summary>
    public async Task PopulateEmptyAsync()
    {
        ResetFiltersAndSort();
        _queryResult = new();

        DataTable dt = new();
        if (_tableInfo is not null)
        {
            string db = _getDatabase();
            var colsResult = await _dbExplorer.GetTableColumnsAsync(db, _tableInfo.TableSchema, _tableInfo.TableName);
            if (colsResult.Success && colsResult.Data is { Count: > 0 })
            {
                foreach (var c in colsResult.Data.OrderBy(c => c.OrdinalPosition))
                    dt.Columns.Add(c.ColumnName, typeof(string));
            }
        }

        dt.Columns.Add(RowIdxColumn, typeof(string));
        _dataTable = dt;
        _bindingSource.DataSource = dt;

        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (col.Name == RowIdxColumn) { col.Visible = false; continue; }
            col.HeaderText = col.Name;
            col.Width = Math.Min(220, Math.Max(70, col.HeaderText.Length * 9 + HeaderArrowZoneWidth));
        }

        UpdateNavLabel();
    }

    #endregion Caricamento dati e metadati

    #region Barra di navigazione

    private void UpdateNavLabel()
    {
        int count = _bindingSource.Count;
        int pos = count == 0 ? 0 : _bindingSource.Position + 1;
        _lblNavPosition.Text = $"di {count}";
        if (!_txtNavRecord.Focused)
            _txtNavRecord.Text = pos.ToString();
        UpdateFilterStateButton();
    }

    /// <summary>Aggiorna l'indicatore "Nessun filtro / Filtrato / Non filtrato"
    /// con la stessa semantica della barra record di Access.</summary>
    private void UpdateFilterStateButton()
    {
        bool hasFilters = _columnFilters.Count > 0;
        _btnNavFilterState.Enabled = hasFilters;
        _btnNavFilterState.ForeColor = hasFilters ? Color.FromArgb(30, 80, 160) : Color.FromArgb(120, 120, 120);
        _btnNavFilterState.Text = !hasFilters
            ? "Nessun filtro"
            : _filtersSuspended ? "Non filtrato" : "Filtrato";
    }

    /// <summary>Attiva/disattiva i filtri conservandone la definizione, come in Access.</summary>
    private void ToggleFilterState()
    {
        if (_columnFilters.Count == 0) return;
        _filtersSuspended = !_filtersSuspended;
        ApplyColumnFilters();
    }

    /// <summary>Sposta la posizione corrente al numero di record digitato nella barra.</summary>
    private void GoToRecordFromNavBox()
    {
        if (_bindingSource.Count == 0) return;

        if (!int.TryParse(_txtNavRecord.Text.Trim(), out int requested))
        {
            UpdateNavLabel();
            return;
        }

        int target = Math.Clamp(requested, 1, _bindingSource.Count) - 1;
        _bindingSource.Position = target;
        UpdateNavLabel();
    }

    /// <summary>Ricerca incrementale su tutte le colonne visibili: porta la cella
    /// corrente sulla prima corrispondenza, come la casella "Cerca" di Access.</summary>
    private void SearchInGrid(string term)
    {
        if (term.Length == 0) return;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            foreach (DataGridViewCell cell in row.Cells)
            {
                DataGridViewColumn col = _grid.Columns[cell.ColumnIndex];
                if (!col.Visible || col.Name == RowIdxColumn) continue;

                string text = Convert.ToString(cell.Value) ?? "";
                if (text.Length == 0 || text.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;

                _grid.CurrentCell = cell;
                return;
            }
        }
    }

    private async Task NavigateGridAsync(Action moveAction)
    {
        if (!await CommitCurrentGridRowAsync()) return;
        moveAction();
    }

    private async Task AddNewGridRowAsync()
    {
        if (_dataTable is null)
        {
            MessageBox.Show("Esegui prima una query.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!await CommitCurrentGridRowAsync()) return;

        if (HasPendingNewRow())
        {
            MessageBox.Show("Prima salva o completa la riga in inserimento corrente.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Sposta il focus sulla riga nuova nativa (asterisco) in fondo alla griglia
        if (_grid.AllowUserToAddRows && _grid.Rows.Count > 0)
        {
            int newRowIndex = _grid.Rows.Count - 1;
            int firstEditableCol = -1;
            foreach (DataGridViewColumn col in _grid.Columns)
            {
                if (col.Visible && col.Name != RowIdxColumn && !col.ReadOnly) { firstEditableCol = col.Index; break; }
            }
            if (firstEditableCol >= 0)
            {
                _grid.CurrentCell = _grid.Rows[newRowIndex].Cells[firstEditableCol];
                _grid.BeginEdit(true);
            }
        }
        else
        {
            _bindingSource.AddNew();
        }
    }

    #endregion Barra di navigazione

    #region Inserimento e modifica righe

    /// <summary>True se la colonna non va mai inclusa in un INSERT: identity, calcolata
    /// o rowversion/timestamp. SQL Server la valorizza da sé.</summary>
    private bool IsExcludedFromInsert(string columnName) =>
        _identityColumns.Contains(columnName) || _nonInsertableColumns.Contains(columnName);

    /// <summary>True solo se esiste una riga in inserimento con dei valori: le righe
    /// Added vuote non sono inserimenti in sospeso e non devono bloccare i comandi
    /// (<see cref="CommitCurrentGridRowAsync"/> le salta, quindi non si sbloccherebbero mai).</summary>
    private bool HasPendingNewRow()
    {
        for (int i = 0; i < _bindingSource.Count; i++)
        {
            var row = GetDataRowAt(i);
            if (row is null) continue;
            if (row.RowState == DataRowState.Added && !_insertedNewRows.Contains(row)
                && !_savingNewRows.Contains(row) && DataRowHasValues(row))
                return true;
        }
        return false;
    }

    /// <summary>Elimina le righe in inserimento rimaste senza valori (es. riga nuova
    /// abbozzata e poi svuotata), che altrimenti restano in stato Added per sempre.</summary>
    private void DiscardEmptyPendingRows()
    {
        if (_dataTable is null) return;

        List<DataRow> toDiscard = new();
        for (int i = 0; i < _bindingSource.Count; i++)
        {
            DataRow? row = GetDataRowAt(i);
            if (row is null) continue;
            if (row.RowState != DataRowState.Added) continue;
            if (_insertedNewRows.Contains(row) || _savingNewRows.Contains(row)) continue;
            if (DataRowHasValues(row)) continue;
            toDiscard.Add(row);
        }

        foreach (DataRow row in toDiscard)
        {
            try { row.RejectChanges(); }
            catch { /* la riga potrebbe essere già stata rimossa */ }
        }
    }

    private async Task<bool> CommitCurrentGridRowAsync()
    {
        if (_dataTable is null) return false;

        int currentRowIndex = _grid.CurrentCell?.RowIndex ?? -1;
        int currentColIndex = _grid.CurrentCell?.ColumnIndex ?? -1;
        bool wasEditingCurrentCell = _grid.IsCurrentCellInEditMode;

        if (wasEditingCurrentCell)
            _grid.EndEdit();

        _grid.EndEdit();
        _bindingSource.EndEdit();

        if (wasEditingCurrentCell && currentRowIndex >= 0 && currentColIndex >= 0)
            await OnGridCellEndEditAsync(currentRowIndex, currentColIndex);

        DiscardEmptyPendingRows();

        // Valida solo la riga su cui si trovava il cursore, cioè quella che l'utente
        // sta effettivamente lasciando. Un errore di inserimento va mostrato una volta,
        // quando si esce dalla riga: se qui si tentasse di reinserire QUALSIASI riga
        // Added rimasta nella griglia, un inserimento fallito in precedenza
        // ripresenterebbe lo stesso errore durante un'azione successiva non correlata.
        DataRow? currentRow = GetDataRowAt(currentRowIndex);
        if (currentRow is null || currentRow.RowState != DataRowState.Added
            || _insertedNewRows.Contains(currentRow) || !DataRowHasValues(currentRow))
            return true;

        await OnGridRowValidatedAsync(currentRowIndex);

        currentRow = GetDataRowAt(currentRowIndex);
        return currentRow is null || currentRow.RowState != DataRowState.Added || _insertedNewRows.Contains(currentRow);
    }

    private static bool DataRowHasValues(DataRow dr)
    {
        foreach (DataColumn col in dr.Table.Columns)
        {
            if (col.ColumnName == RowIdxColumn) continue;
            if (dr[col] != DBNull.Value && !string.IsNullOrWhiteSpace(Convert.ToString(dr[col])))
                return true;
        }
        return false;
    }

    /// <summary>Legge l'indice originale (in QueryResult) memorizzato nella colonna tecnica nascosta della riga.</summary>
    private static int GetOriginalRowIndex(DataGridViewRow row)
    {
        object? v = row.Cells[RowIdxColumn]?.Value;
        return v is string s && int.TryParse(s, out int idx) ? idx : -1;
    }

    private DataRow? GetDataRowAt(int gridRowIndex)
    {
        if (gridRowIndex < 0 || gridRowIndex >= _bindingSource.Count) return null;
        return (_bindingSource[gridRowIndex] as DataRowView)?.Row;
    }

    private int FindGridRowByOriginalIndex(int origIdx)
    {
        for (int i = 0; i < _bindingSource.Count; i++)
        {
            if ((_bindingSource[i] as DataRowView)?.Row is DataRow dr &&
                dr[RowIdxColumn] is string s && int.TryParse(s, out int idx) && idx == origIdx)
                return i;
        }
        return -1;
    }

    private async Task OnGridCellEndEditAsync(int rowIndex, int columnIndex)
    {
        try
        {
            if (rowIndex < 0 || columnIndex < 0 || _dataTable is null) return;
            DataGridViewRow gridRow = _grid.Rows[rowIndex];
            if (gridRow.IsNewRow) return;

            string colName = _grid.Columns[columnIndex].Name;
            if (colName == RowIdxColumn) return;

            int origIdx = GetOriginalRowIndex(gridRow);
            if (origIdx < 0 || origIdx >= _queryResult.Count) return;

            object? newVal = gridRow.Cells[columnIndex].Value;
            string? newStr = newVal is null || newVal == DBNull.Value ? null : Convert.ToString(newVal);
            object? oldVal = _queryResult[origIdx].GetValueOrDefault(colName);
            string? oldStr = oldVal is null ? null : Convert.ToString(oldVal);

            if (string.Equals(newStr, oldStr, StringComparison.Ordinal)) return;

            if (_tableInfo is null || _keyColumns.Count == 0)
            {
                MessageBox.Show("Impossibile determinare la chiave primaria della tabella: la modifica non può essere salvata.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                gridRow.Cells[columnIndex].Value = oldStr is null ? (object)DBNull.Value : oldStr;
                return;
            }

            var keyRow = _queryResult[origIdx];
            string fn = FullName(_tableInfo);
            string whereClause = WhereClause(_keyColumns, keyRow);
            string sql = $"UPDATE {fn} SET {colName} = {FmtVal(newStr)} WHERE {whereClause}";

            _setLoading(true);
            try
            {
                var result = await _sql.ExecuteCommandAsync(sql);
                if (!result.Success)
                {
                    MessageBox.Show($"Salvataggio non riuscito: {result.Error}\n\nCorreggi il valore e riprova.", "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    // Mantieni la riga in modifica: riporta il focus sulla cella per la correzione
                    try
                    {
                        _grid.CurrentCell = gridRow.Cells[columnIndex];
                        _grid.BeginEdit(true);
                    }
                    catch { /* focus best-effort */ }
                }
                else
                {
                    _queryResult[origIdx][colName] = newStr;
                    _addMessage($"✏️ {fn}: colonna {colName} aggiornata ({whereClause})");
                }
            }
            finally { _setLoading(false); }
        }
        catch (Exception ex) { _addMessage($"❌ Errore salvataggio modifica: {ex.Message}"); }
    }

    private async Task OnGridRowValidatedAsync(int rowIndex)
    {
        DataRow? dr = null;
        bool trackingSave = false;
        try
        {
            if (_cancellingEdit) return;
            dr = GetDataRowAt(rowIndex);
            if (dr is null || dr.RowState != DataRowState.Added || _insertedNewRows.Contains(dr) || _savingNewRows.Contains(dr)) return;

            _savingNewRows.Add(dr);
            trackingSave = true;

            if (!DataRowHasValues(dr)) return;

            if (_tableInfo is null)
            {
                MessageBox.Show("Impossibile determinare la tabella di destinazione.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var insertCols = dr.Table.Columns.Cast<DataColumn>()
                .Select(c => c.ColumnName)
                .Where(c => c != RowIdxColumn && !IsExcludedFromInsert(c))
                .ToList();

            string fn = FullName(_tableInfo);
            string cols = string.Join(", ", insertCols);
            string vals = string.Join(", ", insertCols.Select(c => FmtVal(dr[c] == DBNull.Value ? null : Convert.ToString(dr[c]))));
            string insert = $"INSERT INTO {fn} ( {cols} ) VALUES ( {vals} )";

            // La rilettura viaggia nello stesso batch dell'INSERT: SCOPE_IDENTITY() è valido
            // solo lì, e la SELECT dopo i trigger mostra identity, default e campi calcolati.
            string predicate = BuildInsertedRowPredicate(dr);

            _setLoading(true);
            try
            {
                if (predicate.Length == 0)
                {
                    SqlResult<int> plain = await _sql.ExecuteCommandAsync(insert);
                    if (!plain.Success)
                    {
                        MessageBox.Show($"Inserimento non riuscito: {plain.Error}\n\nLa riga resta in modifica: correggi i dati e riprova.", "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    _insertedNewRows.Add(dr);
                    _addMessage($"➕ {fn}: nuova riga inserita (riga non rileggibile: nessuna chiave individuabile)");
                    return;
                }

                SqlResult<List<Dictionary<string, object?>>> result =
                    await _sql.ExecuteQueryAsync($"{insert};\nSELECT * FROM {fn} WHERE {predicate}");
                if (!result.Success)
                {
                    MessageBox.Show($"Inserimento non riuscito: {result.Error}\n\nLa riga resta in modifica: correggi i dati e riprova.", "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                _insertedNewRows.Add(dr);
                if (result.Data is { Count: > 0 })
                {
                    ApplyRefreshedRow(dr, result.Data[0]);
                    _addMessage($"➕ {fn}: nuova riga inserita e riletta dal database");
                }
                else
                    _addMessage($"➕ {fn}: nuova riga inserita (rilettura senza risultati)");
            }
            finally { _setLoading(false); }
        }
        catch (Exception ex) { _addMessage($"❌ Errore inserimento riga: {ex.Message}"); }
        finally
        {
            if (trackingSave && dr is not null)
                _savingNewRows.Remove(dr);
        }
    }

    /// <summary>Condizione WHERE per rileggere la riga appena inserita: SCOPE_IDENTITY()
    /// se la tabella ha una identity, altrimenti i valori della chiave primaria.
    /// Stringa vuota se la riga non è identificabile.</summary>
    private string BuildInsertedRowPredicate(DataRow dr)
    {
        List<string> identityCols = dr.Table.Columns.Cast<DataColumn>()
            .Select(c => c.ColumnName)
            .Where(c => c != RowIdxColumn && _identityColumns.Contains(c))
            .ToList();
        if (identityCols.Count == 1)
            return $"{identityCols[0]} = SCOPE_IDENTITY()";

        if (_keyColumns.Count == 0) return "";

        Dictionary<string, object?> keyValues = new();
        foreach (string key in _keyColumns)
        {
            // Chiave generata dal database e non identity singola: valore non prevedibile
            if (!dr.Table.Columns.Contains(key) || _identityColumns.Contains(key)) return "";
            keyValues[key] = dr[key] == DBNull.Value ? null : Convert.ToString(dr[key]);
        }
        return WhereClause(_keyColumns, keyValues);
    }

    /// <summary>Riporta nella griglia i valori riletti dal database e allinea
    /// QueryResult, così la riga inserita diventa a tutti gli effetti una riga
    /// normale: le modifiche successive generano UPDATE e finisce negli script.</summary>
    private void ApplyRefreshedRow(DataRow dr, Dictionary<string, object?> fresh)
    {
        foreach (KeyValuePair<string, object?> kv in fresh)
        {
            if (kv.Key == RowIdxColumn || !dr.Table.Columns.Contains(kv.Key)) continue;
            dr[kv.Key] = kv.Value is null ? (object)DBNull.Value : Convert.ToString(kv.Value) ?? (object)DBNull.Value;
        }

        Dictionary<string, object?> snapshot = new();
        foreach (DataColumn col in dr.Table.Columns)
        {
            if (col.ColumnName == RowIdxColumn) continue;
            snapshot[col.ColumnName] = dr[col] == DBNull.Value ? null : Convert.ToString(dr[col]);
        }
        _queryResult.Add(snapshot);

        dr[RowIdxColumn] = (_queryResult.Count - 1).ToString();
        dr.AcceptChanges();

        int gridIndex = FindGridRowByDataRow(dr);
        if (gridIndex >= 0) _grid.InvalidateRow(gridIndex);
    }

    private async Task DeleteSelectedGridRowsAsync()
    {
        var selectedGridRowIndexes = _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow)
            .Select(r => r.Index)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        if (selectedGridRowIndexes.Count == 0 && _grid.CurrentRow is not null && !_grid.CurrentRow.IsNewRow)
            selectedGridRowIndexes.Add(_grid.CurrentRow.Index);

        var pendingGridOnlyRows = new List<int>();
        var dbRowIndices = new List<int>();

        foreach (int gridIdx in selectedGridRowIndexes)
        {
            DataRow? dr = GetDataRowAt(gridIdx);
            if (dr is not null && dr.RowState == DataRowState.Added && !_insertedNewRows.Contains(dr))
            {
                pendingGridOnlyRows.Add(gridIdx);
                continue;
            }

            int origIdx = GetOriginalRowIndex(_grid.Rows[gridIdx]);
            if (origIdx >= 0) dbRowIndices.Add(origIdx);
        }

        if (pendingGridOnlyRows.Count == 0 && dbRowIndices.Count == 0)
        {
            MessageBox.Show("Seleziona almeno una riga nella griglia.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int totalSelected = pendingGridOnlyRows.Count + dbRowIndices.Count;
        if (MessageBox.Show($"Eliminare {totalSelected} riga/e selezionata/e?", "Conferma eliminazione", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (pendingGridOnlyRows.Count > 0)
        {
            foreach (int gridIdx in pendingGridOnlyRows.OrderByDescending(i => i))
            {
                DataRow? dr = GetDataRowAt(gridIdx);
                if (dr is not null)
                {
                    _insertedNewRows.Remove(dr);
                    _savingNewRows.Remove(dr);
                }
                _bindingSource.RemoveAt(gridIdx);
            }
            _addMessage($"🗑 {pendingGridOnlyRows.Count} riga/e nuova/e rimossa/e dalla griglia");
        }

        if (dbRowIndices.Count == 0)
            return;

        if (_tableInfo is null || _keyColumns.Count == 0)
        {
            MessageBox.Show("Impossibile determinare la chiave primaria della tabella: eliminazione non consentita.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string fn = FullName(_tableInfo);
        int deleted = 0;

        _setLoading(true);
        try
        {
            // Elimina dal DB in ordine decrescente di indice per non alterare gli indici non ancora processati
            foreach (int origIdx in dbRowIndices.OrderByDescending(i => i))
            {
                if (origIdx < 0 || origIdx >= _queryResult.Count) continue;
                var row = _queryResult[origIdx];
                string sql = $"DELETE FROM {fn} WHERE {WhereClause(_keyColumns, row)}";
                var result = await _sql.ExecuteCommandAsync(sql);
                if (result.Success)
                {
                    deleted++;
                    int gridRowIdx = FindGridRowByOriginalIndex(origIdx);
                    if (gridRowIdx >= 0) _bindingSource.RemoveAt(gridRowIdx);
                    _queryResult.RemoveAt(origIdx);
                }
                else _addMessage($"❌ Eliminazione riga fallita: {result.Error}");
            }
            _addMessage($"🗑 {deleted} riga/e eliminata/e da {fn}");
        }
        finally { _setLoading(false); }
    }

    #endregion Inserimento e modifica righe

    #region Menu contestuale, copia e incolla

    private ContextMenuStrip BuildGridContextMenu()
    {
        ContextMenuStrip menu = new();
        var miCopyCell = new ToolStripMenuItem("Copia cella");
        miCopyCell.Click += (_, _) => CopyGridSelectionToClipboard(cellOnly: true);
        var miCopyRow = new ToolStripMenuItem("Copia riga/e") { ShortcutKeyDisplayString = "Ctrl+C" };
        miCopyRow.Click += (_, _) => CopyGridSelectionToClipboard(cellOnly: false);
        var miDuplicateRow = new ToolStripMenuItem("Duplica riga come nuova");
        miDuplicateRow.Click += async (_, _) => await GuardAsync(DuplicateSelectedRowAsNewAsync);
        var miPasteRow = new ToolStripMenuItem("Incolla come nuova riga") { ShortcutKeyDisplayString = "Ctrl+V" };
        miPasteRow.Click += async (_, _) => await GuardAsync(PasteFromClipboardIntoGridAsync);
        var miFilterSel = new ToolStripMenuItem("Filtra per selezione");
        miFilterSel.Click += (_, _) => FilterGridByCurrentCellValue();
        var miClearColFilter = new ToolStripMenuItem("Rimuovi filtro da questa colonna");
        miClearColFilter.Click += (_, _) => { if (_grid.CurrentCell is not null) ClearColumnFilter(_grid.Columns[_grid.CurrentCell.ColumnIndex].Name); };
        var miClearAllFilters = new ToolStripMenuItem("Rimuovi tutti i filtri");
        miClearAllFilters.Click += (_, _) => ClearAllGridFilters();
        var miDeleteRow = new ToolStripMenuItem("Elimina riga/e selezionata/e");
        miDeleteRow.Click += async (_, _) => await GuardAsync(DeleteSelectedGridRowsAsync);
        var miResetColumnLayout = new ToolStripMenuItem("Ripristina disposizione colonne predefinita");
        miResetColumnLayout.Click += (_, _) => ResetColumnLayout();
        menu.Items.AddRange(new ToolStripItem[]
        {
            miCopyCell, miCopyRow, miDuplicateRow, miPasteRow, new ToolStripSeparator(),
            miFilterSel, miClearColFilter, miClearAllFilters, new ToolStripSeparator(),
            miDeleteRow, new ToolStripSeparator(),
            miResetColumnLayout
        });
        return menu;
    }

    private void CopyGridSelectionToClipboard(bool cellOnly)
    {
        try
        {
            if (cellOnly)
            {
                string cellText = Convert.ToString(_grid.CurrentCell?.Value) ?? "";
                if (!string.IsNullOrEmpty(cellText))
                    Clipboard.SetText(cellText);
                return;
            }

            var sb = new StringBuilder();
            var rowsWithSelection = _grid.SelectedCells.Cast<DataGridViewCell>()
                .Select(c => c.RowIndex)
                .Distinct()
                .Where(idx => idx >= 0 && !_grid.Rows[idx].IsNewRow)
                .OrderBy(idx => idx)
                .ToList();
            if (rowsWithSelection.Count == 0 && _grid.CurrentRow is not null && !_grid.CurrentRow.IsNewRow)
                rowsWithSelection.Add(_grid.CurrentRow.Index);

            // Ordine di visualizzazione, lo stesso usato dall'incolla: le colonne
            // possono essere state riordinate dall'utente.
            List<DataGridViewColumn> columns = ClipboardColumns();
            foreach (int idx in rowsWithSelection)
            {
                var row = _grid.Rows[idx];
                var vals = columns.Select(c => Convert.ToString(row.Cells[c.Index].Value) ?? "");
                sb.AppendLine(string.Join("\t", vals));
            }
            if (sb.Length > 0)
                Clipboard.SetText(sb.ToString());
        }
        catch (Exception ex)
        {
            _addMessage($"❌ Copia negli appunti non riuscita: {ex.Message}");
        }
    }

    /// <summary>Colonne coinvolte in copia/incolla, nell'ordine in cui l'utente le vede.</summary>
    private List<DataGridViewColumn> ClipboardColumns() =>
        _grid.Columns.Cast<DataGridViewColumn>()
            .Where(c => c.Visible && c.Name != RowIdxColumn)
            .OrderBy(c => c.DisplayIndex)
            .ToList();

    /// <summary>Spezza il testo degli appunti in righe e celle (formato TSV, come Excel/Access).</summary>
    private static List<string[]> ParseClipboardGrid(string text)
    {
        List<string[]> rows = new();
        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Length == 0) continue;
            rows.Add(line.Split('\t'));
        }
        return rows;
    }

    /// <summary>Riga in inserimento non ancora salvata su cui si trova il cursore,
    /// oppure <c>null</c> se il cursore è su una riga già esistente.</summary>
    private DataRow? PendingRowAtCursor()
    {
        DataRow? row = GetDataRowAt(_grid.CurrentCell?.RowIndex ?? -1);
        if (row is null) return null;
        if (row.RowState != DataRowState.Added) return null;
        if (_insertedNewRows.Contains(row) || _savingNewRows.Contains(row)) return null;
        return row;
    }

    /// <summary>Scrive i valori in una riga già in inserimento, senza aggiungerne altre.</summary>
    private void FillPendingRow(DataRow row, Dictionary<string, object?> values)
    {
        foreach (KeyValuePair<string, object?> kv in values)
        {
            if (kv.Key == RowIdxColumn) continue;
            if (IsExcludedFromInsert(kv.Key)) continue;
            row[kv.Key] = kv.Value ?? (object)DBNull.Value;
        }
    }

    /// <summary>Aggiunge alla griglia una riga in stato Added con i valori indicati:
    /// resta in inserimento, modificabile, e viene scritta sul database quando
    /// l'utente esce dalla riga. Le colonne identity, calcolate e rowversion/timestamp
    /// non vengono valorizzate: SQL Server le assegna da sé.</summary>
    private DataRow AppendPendingRow(Dictionary<string, object?> values)
    {
        DataRow row = _dataTable!.NewRow();
        foreach (KeyValuePair<string, object?> kv in values)
        {
            if (kv.Key == RowIdxColumn) continue;
            if (IsExcludedFromInsert(kv.Key)) continue;
            row[kv.Key] = kv.Value ?? (object)DBNull.Value;
        }
        row[RowIdxColumn] = DBNull.Value;
        _dataTable.Rows.Add(row);
        return row;
    }

    /// <summary>Porta il cursore sulla prima cella modificabile della riga indicata.</summary>
    private void FocusPendingRow(DataRow row)
    {
        int gridIndex = FindGridRowByDataRow(row);
        if (gridIndex < 0) return;

        _bindingSource.Position = gridIndex;
        foreach (DataGridViewColumn col in ClipboardColumns())
        {
            if (col.ReadOnly) continue;
            _grid.CurrentCell = _grid.Rows[gridIndex].Cells[col.Index];
            return;
        }
    }

    /// <summary>Incolla gli appunti: un valore singolo finisce nella cella corrente,
    /// una o più righe TSV diventano nuovi record in inserimento, pronti da modificare.</summary>
    private async Task PasteFromClipboardIntoGridAsync()
    {
        if (_dataTable is null)
        {
            MessageBox.Show("Esegui prima una query.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (Exception ex)
        {
            _addMessage($"❌ Lettura degli appunti non riuscita: {ex.Message}");
            return;
        }

        List<string[]> clipboardRows = ParseClipboardGrid(text);
        if (clipboardRows.Count == 0) return;

        // Valore singolo su una riga esistente: si comporta come una normale modifica di cella
        bool singleValue = clipboardRows.Count == 1 && clipboardRows[0].Length == 1;
        if (singleValue && _grid.CurrentCell is not null && _grid.CurrentRow is not null
            && !_grid.CurrentRow.IsNewRow)
        {
            int rowIndex = _grid.CurrentCell.RowIndex;
            int colIndex = _grid.CurrentCell.ColumnIndex;
            _grid.CurrentCell.Value = clipboardRows[0][0];
            await OnGridCellEndEditAsync(rowIndex, colIndex);
            return;
        }

        // Se il cursore è già su una riga in inserimento (riga nuova "*" oppure riga
        // Added non ancora salvata) la prima riga degli appunti riempie QUELLA riga:
        // non si esce dalla riga e non si forza un salvataggio parziale.
        DataRow? pendingTarget = PendingRowAtCursor();
        bool onNewRow = _grid.CurrentRow?.IsNewRow == true;

        if (pendingTarget is null && !onNewRow)
        {
            if (!await CommitCurrentGridRowAsync()) return;
            if (HasPendingNewRow())
            {
                MessageBox.Show("Prima salva o completa la riga in inserimento corrente.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        List<DataGridViewColumn> columns = ClipboardColumns();
        DataRow? firstPasted = null;
        int pasted = 0;

        foreach (string[] cells in clipboardRows)
        {
            Dictionary<string, object?> values = new();
            for (int i = 0; i < columns.Count && i < cells.Length; i++)
                values[columns[i].Name] = cells[i].Length == 0 ? null : cells[i];

            DataRow row;
            if (pendingTarget is not null)
            {
                FillPendingRow(pendingTarget, values);
                row = pendingTarget;
                pendingTarget = null; // le righe successive vengono accodate
            }
            else
                row = AppendPendingRow(values);

            firstPasted ??= row;
            pasted++;
        }

        if (firstPasted is not null) FocusPendingRow(firstPasted);
        _addMessage($"📋 {pasted} riga/e incollata/e come nuovo record: rivedi i valori e spostati su un'altra riga per confermare.");
    }

    /// <summary>Duplica la riga selezionata creando una nuova riga con gli stessi valori
    /// (escluse le colonne identity), pronta per l'inserimento come nuovo record.</summary>
    private async Task DuplicateSelectedRowAsNewAsync()
    {
        if (_dataTable is null)
        {
            MessageBox.Show("Esegui prima una query.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int srcGridIndex = _grid.CurrentCell?.RowIndex ?? -1;
        if (srcGridIndex < 0 && _grid.SelectedRows.Count > 0)
            srcGridIndex = _grid.SelectedRows.Cast<DataGridViewRow>().Min(r => r.Index);

        DataRow? source = GetDataRowAt(srcGridIndex);
        if (source is null || source.RowState == DataRowState.Detached)
        {
            MessageBox.Show("Seleziona una riga valida da duplicare.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Assicura che eventuali modifiche/inserimenti in sospeso siano salvati prima
        if (!await CommitCurrentGridRowAsync()) return;
        if (HasPendingNewRow())
        {
            MessageBox.Show("Prima salva o completa la riga in inserimento corrente.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Copia i valori sorgente prima di aggiungere la nuova riga
        var snapshot = new Dictionary<string, object?>();
        foreach (DataColumn col in _dataTable.Columns)
        {
            if (col.ColumnName == RowIdxColumn) continue;
            if (IsExcludedFromInsert(col.ColumnName)) continue;
            snapshot[col.ColumnName] = source[col];
        }

        // Porta il focus sulla nuova riga così l'utente può rivedere/modificare prima del commit
        DataRow newRow = AppendPendingRow(snapshot);
        FocusPendingRow(newRow);

        _addMessage("➕ Riga duplicata: rivedi i valori e spostati su un'altra riga per confermare l'inserimento.");
    }

    private int FindGridRowByDataRow(DataRow target)
    {
        for (int i = 0; i < _bindingSource.Count; i++)
        {
            if ((_bindingSource[i] as DataRowView)?.Row == target)
                return i;
        }
        return -1;
    }

    #endregion Menu contestuale, copia e incolla

    #region Intestazioni, filtri e ordinamento

    /// <summary>Reimposta il testo delle intestazioni colonna (perso quando le colonne
    /// vengono rigenerate automaticamente al rebinding) e nasconde la colonna tecnica.</summary>
    private void EnsureColumnHeaders()
    {
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = HeaderBackColor;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = HeaderForeColor;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = HeaderSelectedBackColor;
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = HeaderForeColor;

        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (col.Name == RowIdxColumn) { col.Visible = false; continue; }
            if (string.IsNullOrEmpty(col.HeaderText) || col.HeaderText != col.Name)
                col.HeaderText = col.Name;
        }
        InvalidateColumnHeaders();
    }

    /// <summary>Righe selezionate, come indici in QueryResult: usato dalla generazione
    /// script per sapere su quali righe operare.</summary>
    public List<int> GetSelectedRowIndices()
    {
        var indices = new List<int>();
        foreach (DataGridViewRow row in _grid.SelectedRows)
        {
            int idx = GetOriginalRowIndex(row);
            if (idx >= 0) indices.Add(idx);
        }
        indices.Sort();
        return indices;
    }

    private void FilterGridByCurrentCellValue()
    {
        if (_grid.CurrentCell is null) return;
        string colName = _grid.Columns[_grid.CurrentCell.ColumnIndex].Name;
        if (colName == RowIdxColumn) return;
        object? val = _grid.CurrentCell.Value;
        string? valStr = (val is null || val == DBNull.Value) ? null : Convert.ToString(val);
        SetColumnFilter(colName, new ColumnFilter { AllowedValues = new List<string?> { valStr } });
    }

    private void SetColumnFilter(string colName, ColumnFilter? filter)
    {
        if (filter is null || filter.IsEmpty) _columnFilters.Remove(colName);
        else
        {
            _columnFilters[colName] = filter;
            // Applicare un nuovo filtro riattiva il filtraggio, come in Access.
            _filtersSuspended = false;
        }
        ApplyColumnFilters();
    }

    private void ClearColumnFilter(string colName)
    {
        _columnFilters.Remove(colName);
        ApplyColumnFilters();
    }

    private void ClearAllGridFilters()
    {
        _columnFilters.Clear();
        _filtersSuspended = false;
        ApplyColumnFilters();
    }

    private void ApplyColumnFilters()
    {
        List<string> clauses = new();
        foreach (KeyValuePair<string, ColumnFilter> kv in _columnFilters)
        {
            string expr = ColumnExpression(kv.Key);

            if (kv.Value.AllowedValues is List<string?> allowed)
            {
                if (allowed.Count == 0)
                {
                    // Nessun valore spuntato: nessuna riga soddisfa il filtro.
                    clauses.Add("1 = 0");
                }
                else
                {
                    // IN (...) invece di una catena di OR: espressione molto più corta
                    // e più veloce da valutare per il DataView su tabelle grandi.
                    clauses.Add($"{expr} IN ({string.Join(", ", allowed.Select(v => $"'{EscapeLiteral(v ?? "")}'"))})");
                }
            }

            if (kv.Value.HasCriteria)
                clauses.Add(BuildCriteriaClause(expr, kv.Value.Operator, kv.Value.Value));
        }

        string expression = clauses.Count > 0 && !_filtersSuspended ? string.Join(" AND ", clauses) : "";
        try { _bindingSource.Filter = expression; }
        catch (Exception ex) { _addMessage($"⚠ Filtro non applicabile: {ex.Message}"); }
        InvalidateColumnHeaders(); // il cambio di filtro ridisegna già le celle: qui servono solo i glifi
        UpdateNavLabel();
    }

    /// <summary>Espressione DataView della colonna, normalizzata a stringa non nulla
    /// così che anche i criteri negati intercettino i valori vuoti (come in Access).</summary>
    private static string ColumnExpression(string colName) =>
        $"ISNULL(CONVERT([{colName.Replace("\\", "\\\\").Replace("]", "\\]")}], 'System.String'), '')";

    /// <summary>Escape per un letterale stringa in un'espressione DataView.</summary>
    private static string EscapeLiteral(string value) => value.Replace("'", "''");

    /// <summary>Escape per l'argomento di LIKE: i caratteri jolly vanno racchiusi tra parentesi quadre.</summary>
    private static string EscapePattern(string value) =>
        value.Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("*", "[*]");

    private static string BuildCriteriaClause(string expr, ColumnFilterOperator op, string value)
    {
        string literal = EscapeLiteral(value);
        string pattern = EscapePattern(value);
        return op switch
        {
            ColumnFilterOperator.Equals => $"{expr} = '{literal}'",
            ColumnFilterOperator.NotEquals => $"{expr} <> '{literal}'",
            ColumnFilterOperator.BeginsWith => $"{expr} LIKE '{pattern}*'",
            ColumnFilterOperator.NotBeginsWith => $"NOT ({expr} LIKE '{pattern}*')",
            ColumnFilterOperator.Contains => $"{expr} LIKE '*{pattern}*'",
            ColumnFilterOperator.NotContains => $"NOT ({expr} LIKE '*{pattern}*')",
            ColumnFilterOperator.EndsWith => $"{expr} LIKE '*{pattern}'",
            ColumnFilterOperator.NotEndsWith => $"NOT ({expr} LIKE '*{pattern}')",
            _ => "1 = 1"
        };
    }

    /// <summary>Ordinamento su una sola colonna, dal menu di intestazione (freccia).</summary>
    private void SortColumn(string colName, bool ascending)
    {
        if (_dataTable is null || colName == RowIdxColumn) return;

        _sortColumns = new List<string> { colName };
        _sortAscending = ascending;

        try { _bindingSource.Sort = $"[{colName}] {(ascending ? "ASC" : "DESC")}"; }
        catch { /* colonna non ordinabile */ }
        InvalidateColumnHeaders(); // il riordino ridisegna già le celle: qui serve solo il glifo
        UpdateNavLabel();
    }

    // ─── Selezione di colonne stile Access (ordinamento su più colonne) ────────

    /// <summary>Seleziona una colonna dell'intestazione. Con <paramref name="extend"/>
    /// estende l'intervallo dall'ancora (il primo clic) fino a questa colonna, come
    /// Shift+clic sulle intestazioni in Access.</summary>
    private void SelectColumnHeader(int columnIndex, bool extend)
    {
        DataGridViewColumn column = _grid.Columns[columnIndex];
        if (column.Name == RowIdxColumn) return;

        if (!extend || _columnSelectionAnchorDisplayIndex is null)
            _columnSelectionAnchorDisplayIndex = column.DisplayIndex;
        _columnSelectionEndDisplayIndex = column.DisplayIndex;
        InvalidateColumnHeaders();
    }

    private void ClearColumnSelection()
    {
        if (_columnSelectionAnchorDisplayIndex is null) return;
        _columnSelectionAnchorDisplayIndex = null;
        _columnSelectionEndDisplayIndex = null;
        InvalidateColumnHeaders();
    }

    private bool IsColumnHeaderSelected(int displayIndex)
    {
        if (_columnSelectionAnchorDisplayIndex is not int anchor || _columnSelectionEndDisplayIndex is not int end)
            return false;
        int lo = Math.Min(anchor, end);
        int hi = Math.Max(anchor, end);
        return displayIndex >= lo && displayIndex <= hi;
    }

    /// <summary>Colonne attualmente selezionate, in ordine di visualizzazione da
    /// sinistra a destra: è anche l'ordine delle chiavi di ordinamento combinato.</summary>
    private List<string> GetSelectedColumnNamesInOrder()
    {
        if (_columnSelectionAnchorDisplayIndex is not int anchor || _columnSelectionEndDisplayIndex is not int end)
            return new List<string>();
        int lo = Math.Min(anchor, end);
        int hi = Math.Max(anchor, end);

        return _grid.Columns.Cast<DataGridViewColumn>()
            .Where(c => c.Visible && c.Name != RowIdxColumn && c.DisplayIndex >= lo && c.DisplayIndex <= hi)
            .OrderBy(c => c.DisplayIndex)
            .Select(c => c.Name)
            .ToList();
    }

    /// <summary>Tasto destro su un'intestazione: se fa parte di una selezione di 2+
    /// colonne mostra il menu di ordinamento combinato, altrimenti si comporta come
    /// prima — seleziona solo questa colonna e apre il menu filtro/ordinamento singolo.</summary>
    private void ShowHeaderContextForColumn(int columnIndex)
    {
        DataGridViewColumn column = _grid.Columns[columnIndex];
        if (column.Name == RowIdxColumn) return;

        if (IsColumnHeaderSelected(column.DisplayIndex) && GetSelectedColumnNamesInOrder().Count >= 2)
        {
            ShowMultiColumnSortMenu(columnIndex);
            return;
        }

        SelectColumnHeader(columnIndex, extend: false);
        ShowColumnFilterMenu(columnIndex);
    }

    /// <summary>Menu con le sole azioni che hanno senso su più colonne insieme:
    /// il menu completo (valori/filtro) resta specifico di una singola colonna.</summary>
    private void ShowMultiColumnSortMenu(int columnIndex)
    {
        List<string> names = GetSelectedColumnNamesInOrder();
        if (names.Count == 0) return;

        ContextMenuStrip menu = new();
        menu.Closed += (_, _) => menu.BeginInvoke(new Action(menu.Dispose));

        ToolStripMenuItem miAsc = new($"Ordina crescente ({names.Count} colonne)");
        miAsc.Click += (_, _) => SortBySelectedColumns(true);
        ToolStripMenuItem miDesc = new($"Ordina decrescente ({names.Count} colonne)");
        miDesc.Click += (_, _) => SortBySelectedColumns(false);
        ToolStripMenuItem miClear = new("Deseleziona colonne");
        miClear.Click += (_, _) => ClearColumnSelection();

        menu.Items.AddRange(new ToolStripItem[] { miAsc, miDesc, new ToolStripSeparator(), miClear });
        Rectangle headerRect = _grid.GetCellDisplayRectangle(columnIndex, -1, true);
        menu.Show(_grid, new Point(headerRect.Left, headerRect.Bottom));
    }

    /// <summary>Ordina per tutte le colonne selezionate insieme, nell'ordine in cui sono
    /// disposte da sinistra a destra: prima chiave la più a sinistra, come in Access.</summary>
    private void SortBySelectedColumns(bool ascending)
    {
        List<string> names = GetSelectedColumnNamesInOrder();
        if (names.Count == 0 || _dataTable is null) return;

        _sortColumns = names;
        _sortAscending = ascending;

        string direction = ascending ? "ASC" : "DESC";
        string sortExpression = string.Join(", ", names.Select(n => $"[{n}] {direction}"));
        try { _bindingSource.Sort = sortExpression; }
        catch { /* combinazione non ordinabile */ }

        ClearColumnSelection();
        InvalidateColumnHeaders();
        UpdateNavLabel();
    }

    private void ShowColumnFilterMenu(int columnIndex)
    {
        if (_dataTable is null) return;
        DataGridViewColumn column = _grid.Columns[columnIndex];
        string colName = column.Name;
        if (colName == RowIdxColumn) return;

        // Valori distinti: null e stringa vuota confluiscono in "(Vuoti)" come in Access.
        List<string?> distinctValues = _dataTable.AsEnumerable()
            .Where(r => r.RowState != DataRowState.Deleted && r.RowState != DataRowState.Detached)
            .Select(r => r[colName] == DBNull.Value ? null : Convert.ToString(r[colName]))
            .Select(v => string.IsNullOrEmpty(v) ? null : v)
            .Distinct()
            .OrderBy(v => v is null ? 0 : 1)
            .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDistinctFilterValues)
            .ToList();

        _columnFilters.TryGetValue(colName, out ColumnFilter? current);

        AccessColumnMenu menu = new(colName, distinctValues, current);
        menu.SortRequested += ascending => SortColumn(colName, ascending);
        menu.FilterApplied += filter => SetColumnFilter(colName, filter);
        menu.Show(_grid, _grid.GetCellDisplayRectangle(columnIndex, -1, true));
    }

    #endregion Intestazioni, filtri e ordinamento

    #region Ridisegni mirati e glifi

    /// <summary>Sposta l'indicatore ► ridisegnando solo la riga che lo perde e quella
    /// che lo prende, invece di invalidare tutta la griglia a ogni movimento.</summary>
    private void MoveCurrentRowMarker()
    {
        int newRow = _grid.CurrentCell?.RowIndex ?? -1;
        if (newRow == _markerRowIndex) return;

        InvalidateRowHeader(_markerRowIndex);
        InvalidateRowHeader(newRow);
        _markerRowIndex = newRow;
    }

    private void InvalidateRowHeader(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return;

        Rectangle rect = _grid.GetRowDisplayRectangle(rowIndex, true);
        if (rect.Height <= 0) return; // riga fuori dall'area visibile
        _grid.Invalidate(new Rectangle(0, rect.Top, _grid.RowHeadersWidth, rect.Height));
    }

    /// <summary>Ridisegna la sola striscia delle intestazioni: serve dopo un cambio di
    /// filtro o ordinamento, che modifica i glifi ma non le celle.</summary>
    private void InvalidateColumnHeaders() =>
        _grid.Invalidate(new Rectangle(0, 0, _grid.ClientSize.Width, _grid.ColumnHeadersHeight));

    /// <summary>Disegna nell'intestazione la freccia del menu e gli indicatori di
    /// filtro/ordinamento, come nella visualizzazione Foglio dati di Access.</summary>
    private void PaintColumnHeader(DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex != -1 || e.ColumnIndex < 0 || e.Graphics is null) return;
        DataGridViewColumn column = _grid.Columns[e.ColumnIndex];
        if (column.Name == RowIdxColumn) return;

        bool columnSelected = IsColumnHeaderSelected(column.DisplayIndex);
        if (columnSelected)
        {
            // Tinta di selezione stile Access: una variante più chiara dell'azzurro
            // dell'intestazione, bordi nativi.
            using SolidBrush selectionBrush = new(HeaderSelectedBackColor);
            e.Graphics.FillRectangle(selectionBrush, e.CellBounds);
            e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
        }
        else
        {
            // Sfondo e bordi restano quelli nativi: si sostituisce solo il testo per
            // riservare lo spazio della freccia a destra.
            e.Paint(e.CellBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);
        }

        Rectangle textArea = new(
            e.CellBounds.Left + 3,
            e.CellBounds.Top,
            Math.Max(0, e.CellBounds.Width - HeaderArrowZoneWidth - 6),
            e.CellBounds.Height);
        TextRenderer.DrawText(e.Graphics, column.HeaderText, _grid.ColumnHeadersDefaultCellStyle.Font,
            textArea, _grid.ColumnHeadersDefaultCellStyle.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        int arrowCenterX = e.CellBounds.Right - HeaderArrowZoneWidth / 2 - 2;
        int centerY = e.CellBounds.Top + e.CellBounds.Height / 2;
        bool filtered = _columnFilters.ContainsKey(column.Name);
        bool sorted = _sortColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase);

        if (filtered) DrawFunnelGlyph(e.Graphics, arrowCenterX - 11, centerY);
        else if (sorted) DrawSortGlyph(e.Graphics, arrowCenterX - 11, centerY, _sortAscending);

        DrawDropDownGlyph(e.Graphics, arrowCenterX, centerY);
        e.Handled = true;
    }

    // Oggetti GDI dei glifi di intestazione: riusati invece di essere allocati
    // a ogni cella di ogni frame (una dozzina di allocazioni per ridisegno).
    // Toni chiari: i glifi si disegnano sopra lo sfondo blu dell'intestazione, non più
    // sul grigio chiaro di prima.
    private static readonly SolidBrush GlyphBrush = new(Color.White);
    private static readonly Pen AccentPen = new(Color.FromArgb(255, 202, 60));

    private static void DrawDropDownGlyph(Graphics g, int centerX, int centerY)
    {
        Point[] triangle =
        {
            new(centerX - 4, centerY - 2),
            new(centerX + 4, centerY - 2),
            new(centerX, centerY + 3)
        };
        g.FillPolygon(GlyphBrush, triangle);
    }

    private static void DrawFunnelGlyph(Graphics g, int centerX, int centerY)
    {
        Pen pen = AccentPen;
        g.DrawLine(pen, centerX - 4, centerY - 4, centerX + 4, centerY - 4);
        g.DrawLine(pen, centerX - 4, centerY - 4, centerX - 1, centerY);
        g.DrawLine(pen, centerX + 4, centerY - 4, centerX + 1, centerY);
        g.DrawLine(pen, centerX - 1, centerY, centerX - 1, centerY + 4);
        g.DrawLine(pen, centerX + 1, centerY, centerX + 1, centerY + 4);
        g.DrawLine(pen, centerX - 1, centerY + 4, centerX + 1, centerY + 4);
    }

    /// <summary>Glifi dei pulsanti della barra record, come in Access.</summary>
    private enum NavGlyph
    {
        First,
        Previous,
        Next,
        Last,
        New
    }

    private static void DrawNavGlyph(Graphics g, Rectangle bounds, NavGlyph glyph, bool enabled)
    {
        int cx = bounds.Width / 2;
        int cy = bounds.Height / 2;
        Color color = enabled ? Color.FromArgb(40, 40, 40) : Color.FromArgb(160, 160, 160);
        using SolidBrush brush = new(color);
        using Pen pen = new(color);

        // Le varianti con barra o asterisco spostano il triangolo per restare centrate
        int shift = glyph is NavGlyph.First or NavGlyph.Last or NavGlyph.New ? 2 : 0;
        bool pointsLeft = glyph is NavGlyph.First or NavGlyph.Previous;
        int tipX = pointsLeft ? cx - 4 + shift : cx + 4 - shift;
        int baseX = pointsLeft ? cx + 1 + shift : cx - 1 - shift;
        g.FillPolygon(brush, new Point[]
        {
            new(baseX, cy - 4),
            new(baseX, cy + 4),
            new(tipX, cy)
        });

        switch (glyph)
        {
            case NavGlyph.First:
                g.DrawLine(pen, cx - 6, cy - 4, cx - 6, cy + 4);
                break;
            case NavGlyph.Last:
                g.DrawLine(pen, cx + 5, cy - 4, cx + 5, cy + 4);
                break;
            case NavGlyph.New:
                // Asterisco a destra del triangolo, come il pulsante "nuovo record" di Access
                g.DrawLine(pen, cx + 4, cy - 4, cx + 4, cy + 1);
                g.DrawLine(pen, cx + 2, cy - 3, cx + 6, cy);
                g.DrawLine(pen, cx + 6, cy - 3, cx + 2, cy);
                break;
        }
    }

    /// <summary>Freccia con asta: si distingue a colpo d'occhio dal triangolo del menu.</summary>
    private static void DrawSortGlyph(Graphics g, int centerX, int centerY, bool ascending)
    {
        Pen pen = AccentPen;
        int tipY = ascending ? centerY - 4 : centerY + 4;
        int tailY = ascending ? centerY + 4 : centerY - 4;
        int headY = ascending ? tipY + 3 : tipY - 3;

        g.DrawLine(pen, centerX, tailY, centerX, tipY);
        g.DrawLine(pen, centerX - 3, headY, centerX, tipY);
        g.DrawLine(pen, centerX + 3, headY, centerX, tipY);
    }

    /// <summary>True se il clic è caduto sulla zona della freccia dell'intestazione.
    /// La coordinata è relativa all'angolo superiore sinistro della cella.</summary>
    private bool IsHeaderArrowClick(int columnIndex, int x) =>
        x >= _grid.Columns[columnIndex].Width - HeaderArrowZoneWidth;

    #endregion Ridisegni mirati e glifi
}
