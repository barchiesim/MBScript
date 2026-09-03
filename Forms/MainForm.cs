using MBScript.Models;
using MBScript.Services;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;

namespace MBScript.Forms;

public class MainForm : Form
{
    // ─── Services ────────────────────────────────────────────────────────────
    private readonly SqlService _sql;
    private readonly DatabaseConfig _config;
    private readonly DatabaseExplorerService _dbExplorer;

    // ─── State ───────────────────────────────────────────────────────────────
    private List<Dictionary<string, object?>> _queryResult = new();
    private List<string> _keyColumns = new();
    private HashSet<string> _identityColumns = new(StringComparer.OrdinalIgnoreCase);
    // Colonne calcolate e rowversion/timestamp: SQL Server le valorizza da sé, un
    // INSERT con valore esplicito fallisce ("Cannot insert an explicit value...").
    private HashSet<string> _nonInsertableColumns = new(StringComparer.OrdinalIgnoreCase);
    private List<TableInfo> _allTables = new();
    private TableInfo? _selectedTable = null;
    private string _auditFilter = string.Empty;
    private string _auditExclude = string.Empty;
    private bool _defaultConditionalUpdate = false;
    private string _lastAutoScript = string.Empty;

    // Guard: prevents re-entrant async calls
    private bool _busy = false;

    // ─── Controls ────────────────────────────────────────────────────────────
    private ToolStrip toolStrip = null!;
    private ComboBox cmbDatabases = null!;
    private TextBox txtTableSearch = null!;
    private ListBox lstTables = null!;
    private RichTextBox rtbSqlScript = null!;
    private DataGridView dgvResults = null!;
    private RichTextBox rtbGeneratedScript = null!;
    private ListBox lstMessages = null!;
    private TabControl tabResults = null!;
    private TabPage tabGrid = null!;
    private TabPage tabText = null!;
    private TabPage tabMessages = null!;
    private BindingSource _resultsBindingSource = new();
    private DataTable? _resultsTable;
    private readonly HashSet<DataRow> _insertedNewRows = new();
    private readonly HashSet<DataRow> _savingNewRows = new();
    private readonly Dictionary<string, ColumnFilter> _columnFilters = new(StringComparer.OrdinalIgnoreCase);
    private string? _sortColumn;
    private bool _sortAscending = true;
    private bool _filtersSuspended = false;
    private int _markerRowIndex = -1;
    private bool _cancellingEdit = false;
    private Panel pnlGridNav = null!;
    private Label lblNavPosition = null!;
    private TextBox txtNavRecord = null!;
    private TextBox txtNavSearch = null!;
    private Button btnNavFilterState = null!;
    private ContextMenuStrip gridContextMenu = null!;
    private const string RowIdxColumn = "__RowIdx";
    private StatusStrip statusBar = null!;
    private ToolStripStatusLabel lblStatusServer = null!;
    private ToolStripStatusLabel lblStatusDb = null!;
    private ToolStripStatusLabel lblStatusUser = null!;
    private ToolStripStatusLabel lblStatusLoading = null!;
    private ToolStripStatusLabel lblStatusLine = null!;
    private ToolStripStatusLabel lblStatusCol = null!;
    private SplitContainer splitMain = null!;
    private SplitContainer splitRight = null!;
    private Panel pnlSearch = null!;
    private TextBox txtSearch = null!;
    private Label lblSearchCount = null!;
    private Panel pnlSearchGen = null!;
    private TextBox txtSearchGen = null!;
    private Label lblSearchCountGen = null!;

    // ─── Syntax highlight state ───────────────────────────────────────────────
    private bool _isHighlighting = false;
    private System.Windows.Forms.Timer _syntaxTimer = null!;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, bool wParam, int lParam);
    private const int WM_SETREDRAW = 0x000B;

    private static readonly string _sqlKeywordPattern =
        @"\b(SELECT|FROM|WHERE|INSERT|UPDATE|DELETE|CREATE|DROP|ALTER|TABLE|INTO|VALUES|SET" +
        @"|JOIN|ON|AND|OR|NOT|IN|LIKE|ORDER|BY|GROUP|HAVING|AS|DISTINCT|TOP|INNER|LEFT|RIGHT" +
        @"|OUTER|FULL|CROSS|UNION|ALL|EXISTS|NULL|IS|BETWEEN|WITH|BEGIN|END|GO|USE|IF|ELSE" +
        @"|DECLARE|EXEC|PROCEDURE|TRIGGER|ENABLE|DISABLE|PRIMARY|KEY|FOREIGN|REFERENCES" +
        @"|CONSTRAINT|INDEX|VIEW|IDENTITY|DEFAULT|COUNT|SUM|MAX|MIN|AVG|CAST|CONVERT" +
        @"|ISNULL|COALESCE|CASE|WHEN|THEN|RETURN|NOLOCK|ROLLBACK|COMMIT|TRANSACTION" +
        @"|CHAR|VARCHAR|NVARCHAR|INT|BIGINT|SMALLINT|DATETIME|DATE|BIT|DECIMAL|FLOAT" +
        @"|MONEY|UNIQUEIDENTIFIER|VARBINARY|TEXT|NTEXT)\b";

    // ─── Constructor ─────────────────────────────────────────────────────────
    public MainForm(SqlService sqlService, DatabaseConfig config)
    {
        _sql = sqlService;
        _config = config;
        _dbExplorer = new DatabaseExplorerService(sqlService);
        // Carica le impostazioni audit salvate (incluso stato UI)
        var auditSettings = SettingsService.LoadAuditSettings();
        _auditFilter = auditSettings.AuditFilter;
        _auditExclude = auditSettings.AuditExclude;
        // Restore saved table search and default conditional update flag
        txtTableSearch = new TextBox(); // temporary until InitializeComponent sets the real one
        txtTableSearch.Text = auditSettings.TableSearch;
        // Store default flag to use when showing ColumnSelectorDialog
        _defaultConditionalUpdate = auditSettings.DefaultConditionalUpdate;
        // Restore last server/db if provided in config
        if (!string.IsNullOrEmpty(auditSettings.LastServer)) _config.Server = auditSettings.LastServer;
        if (!string.IsNullOrEmpty(auditSettings.LastDatabase)) _config.Database = auditSettings.LastDatabase;
        if (!string.IsNullOrEmpty(auditSettings.LastUser)) _config.User = auditSettings.LastUser;

        InitializeComponent();

        // Both splitter setup and initial data load happen after the form is fully visible
        Shown += OnFormShown;
    }

    private async void OnFormShown(object? sender, EventArgs e)
    {
        // Start data load immediately
        await GuardAsync(LoadInitialDataAsync);

        // Force layout to complete before setting splitters
        Application.DoEvents();

        // Imposta MinSize e SplitterDistance ora che il form ha dimensioni reali
        try
        {
            // Per lo splitter orizzontale
            splitMain.Panel1MinSize = 150;
            splitMain.Panel2MinSize = 400;

            int targetDistance = 250;
            int maxAllowed = splitMain.ClientSize.Width - splitMain.Panel2MinSize - splitMain.SplitterWidth;

            if (maxAllowed > splitMain.Panel1MinSize && targetDistance <= maxAllowed)
                splitMain.SplitterDistance = targetDistance;
            else if (maxAllowed > splitMain.Panel1MinSize)
                splitMain.SplitterDistance = splitMain.Panel1MinSize;

            splitMain.IsSplitterFixed = false;  // Sblocca lo splitter per permettere all'utente di spostarlo
        }
        catch { /* ignore splitter errors */ }

        try
        {
            // Per lo splitter verticale
            splitRight.Panel1MinSize = 60;
            splitRight.Panel2MinSize = 120;

            int targetDistance = 180;
            int maxAllowed = splitRight.ClientSize.Height - splitRight.Panel2MinSize - splitRight.SplitterWidth;

            if (maxAllowed > splitRight.Panel1MinSize && targetDistance <= maxAllowed)
                splitRight.SplitterDistance = targetDistance;
            else if (maxAllowed > splitRight.Panel1MinSize)
                splitRight.SplitterDistance = splitRight.Panel1MinSize;

            splitRight.IsSplitterFixed = false;  // Sblocca lo splitter per permettere all'utente di spostarlo
        }
        catch { /* ignore splitter errors */ }

        // Restore UI persisted settings
        try
        {
            var s = Services.SettingsService.LoadAuditSettings();
            if (!string.IsNullOrEmpty(s.TableSearch))
                txtTableSearch.Text = s.TableSearch;
            _defaultConditionalUpdate = s.DefaultConditionalUpdate;
        }
        catch { }
    }

    // ─── UI Build ────────────────────────────────────────────────────────────

    private void InitializeComponent()
    {
        Text = $"PBScript – SQL Explorer – {_config.Server} – {_config.Database}";
        Size = new Size(1200, 800);
        MinimumSize = new Size(800, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        // Build main layout first so Docking of ToolStrip/StatusStrip applied afterwards
        BuildMainLayout();
        BuildToolStrip();
        BuildStatusBar();
    }

    private void BuildToolStrip()
    {
        toolStrip = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };

        var btnEsegui = new ToolStripButton("▶ Esegui") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnEsegui.Click += async (_, _) => await GuardAsync(ExecuteQueryAsync);
        toolStrip.Items.Add(btnEsegui);
        toolStrip.Items.Add(new ToolStripSeparator());

        var btnScript = new ToolStripDropDownButton("📝 Script") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var miIns = new ToolStripMenuItem("Crea script INSERT"); miIns.Click += async (_, _) => await GuardAsync(() => GenerateScriptAsync("INSERT"));
        var miUpd = new ToolStripMenuItem("Crea script UPDATE"); miUpd.Click += async (_, _) => await GuardAsync(() => GenerateScriptAsync("UPDATE"));
        var miDel = new ToolStripMenuItem("Crea script DELETE"); miDel.Click += async (_, _) => await GuardAsync(() => GenerateScriptAsync("DELETE"));
        btnScript.DropDownItems.AddRange(new ToolStripItem[] { miIns, miUpd, miDel });
        toolStrip.Items.Add(btnScript);
        toolStrip.Items.Add(new ToolStripSeparator());

        var btnAudit = new ToolStripDropDownButton("📋 Audit") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var miAInit = new ToolStripMenuItem("Inizializza/Resetta db Audit_UPD (SETUP/INSTALLAZIONE)"); miAInit.Click += async (_, _) => await GuardAsync(AuditInitializeAsync);
        var miARem = new ToolStripMenuItem("Elimina sistema di Audit (DISINSTALLAZIONE)"); miARem.Click += (_, _) => AuditRemove();
        var miAOn = new ToolStripMenuItem("Attiva/Riattiva trigger Audit (INIZIO ATTIVITÀ)"); miAOn.Click += (_, _) => AuditActivate();
        var miAOff = new ToolStripMenuItem("Disattiva trigger Audit (PAUSA ATTIVITÀ)"); miAOff.Click += (_, _) => AuditDeactivate();
        var miAGen = new ToolStripMenuItem("Genera Script Audit da eSYS/eSYS_UPD (RILASCIO)"); miAGen.Click += async (_, _) => await GuardAsync(AuditGenerateScriptAsync);
        btnAudit.DropDownItems.AddRange(new ToolStripItem[] { miAInit, miARem, miAOn, miAOff, miAGen });
        toolStrip.Items.Add(btnAudit);
        toolStrip.Items.Add(new ToolStripSeparator());

        var btnLogout = new ToolStripButton("⬅ Logout") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnLogout.Click += (_, _) => { new LoginForm().Show(); Close(); };
        toolStrip.Items.Add(btnLogout);

        Controls.Add(toolStrip);
    }

    private void UpdateLineCol(RichTextBox rtb)
    {
        int pos  = rtb.SelectionStart;
        int line = rtb.GetLineFromCharIndex(pos) + 1;
        int col  = pos - rtb.GetFirstCharIndexFromLine(line - 1) + 1;
        lblStatusLine.Text = $"Ln {line}";
        lblStatusCol.Text  = $"Col {col}";
    }

    private void BuildStatusBar()
    {
        statusBar = new StatusStrip { Dock = DockStyle.Bottom };
        lblStatusServer = new ToolStripStatusLabel($"Server: {_config.Server}");
        lblStatusDb = new ToolStripStatusLabel($"Database: {_config.Database}");
        lblStatusUser = new ToolStripStatusLabel($"User: {(_config.IntegratedSecurity ? "Windows Auth" : _config.User)}");
        lblStatusLoading = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
        lblStatusLine = new ToolStripStatusLabel("Ln 1") { BorderSides = ToolStripStatusLabelBorderSides.Left, AutoSize = false, Width = 55, TextAlign = ContentAlignment.MiddleCenter };
        lblStatusCol  = new ToolStripStatusLabel("Col 1") { BorderSides = ToolStripStatusLabelBorderSides.Left, AutoSize = false, Width = 55, TextAlign = ContentAlignment.MiddleCenter };
        statusBar.Items.AddRange(new ToolStripItem[] { lblStatusServer, new ToolStripSeparator(), lblStatusDb, new ToolStripSeparator(), lblStatusUser, lblStatusLoading, lblStatusLine, lblStatusCol });
        Controls.Add(statusBar);
    }

    private void BuildMainLayout()
    {
        SuspendLayout();

        // ── Left panel ───────────────────────────────────────────────────────
        var pnlLeft = new Panel { Dock = DockStyle.Fill };
        var lblDb = new Label { Text = "Database:", Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI", 7.5f, FontStyle.Bold) };
        cmbDatabases = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 8f) };
        // NOTE: SelectedIndexChanged is wired AFTER population to avoid spurious calls
        var lblSearch = new Label { Text = "Cerca tabella:", Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI", 7.5f, FontStyle.Bold) };
        txtTableSearch = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Cerca tabella...", Font = new Font("Segoe UI", 8f) };
        txtTableSearch.TextChanged += (_, _) => FilterTableList();
        var lblTables = new Label { Text = "Tabelle:", Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI", 7.5f, FontStyle.Bold) };
        lstTables = new ListBox { Dock = DockStyle.Fill, Font = new Font("Courier New", 7.5f) };
        // Doppio clic: carica la SELECT e la esegue subito, come l'apertura di una
        // tabella in Access. Il singolo clic si limita a preparare lo script.
        lstTables.DoubleClick += (_, _) => { LoadTableStructure(); _ = GuardAsync(ExecuteQueryAsync); };
        lstTables.SelectedIndexChanged += (_, _) => LoadTableStructure();

        pnlLeft.Controls.Add(lstTables);
        pnlLeft.Controls.Add(lblTables);
        pnlLeft.Controls.Add(txtTableSearch);
        pnlLeft.Controls.Add(lblSearch);
        pnlLeft.Controls.Add(cmbDatabases);
        pnlLeft.Controls.Add(lblDb);

        // ── Outer split: left / right ─────────────────────────────────────
        splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 4,
            IsSplitterFixed = true  // Blocca temporaneamente per evitare validazione prematura
        };
        // MinSize impostato in OnFormShown per evitare validazione prematura
        splitMain.Panel1.Controls.Add(pnlLeft);

        // ── Inner split: SQL editor (top) / tabs (bottom) ─────────────────
        splitRight = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 4,
            IsSplitterFixed = true  // Blocca temporaneamente
        };
        // MinSize impostato in OnFormShown per evitare validazione prematura

        var pnlSql = new Panel { Dock = DockStyle.Fill };
        var lblSql = new Label { Text = "Script SQL:", Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI", 7.5f, FontStyle.Bold) };

        // ── Barra di ricerca (Ctrl+F) ──────────────────────────────────────
        pnlSearch = new Panel { Dock = DockStyle.Top, Height = 26, Visible = false, BackColor = SystemColors.Info, Padding = new Padding(2) };
        txtSearch = new TextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8.5f) };
        lblSearchCount = new Label { Dock = DockStyle.Right, Width = 70, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 7.5f) };
        Button btnFindNext = new Button { Dock = DockStyle.Right, Width = 26, Text = "▼", FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7f) };
        Button btnFindPrev = new Button { Dock = DockStyle.Right, Width = 26, Text = "▲", FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7f) };
        Button btnCloseSearch = new Button { Dock = DockStyle.Right, Width = 26, Text = "✕", FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7f) };
        btnFindNext.Click += (_, _) => FindInRtb(rtbSqlScript, txtSearch, lblSearchCount, forward: true);
        btnFindPrev.Click += (_, _) => FindInRtb(rtbSqlScript, txtSearch, lblSearchCount, forward: false);
        btnCloseSearch.Click += (_, _) => CloseSearchRtb(pnlSearch, lblSearchCount, rtbSqlScript);
        txtSearch.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; FindInRtb(rtbSqlScript, txtSearch, lblSearchCount, forward: true); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = true; CloseSearchRtb(pnlSearch, lblSearchCount, rtbSqlScript); }
            else if (e.Shift && e.KeyCode == Keys.F3) { e.Handled = true; FindInRtb(rtbSqlScript, txtSearch, lblSearchCount, forward: false); }
        };
        txtSearch.TextChanged += (_, _) => FindInRtb(rtbSqlScript, txtSearch, lblSearchCount, forward: true, resetPos: true);
        // ordine di aggiunta: destra per prima (z-order inverso per Dock.Right)
        pnlSearch.Controls.Add(txtSearch);
        pnlSearch.Controls.Add(lblSearchCount);
        pnlSearch.Controls.Add(btnCloseSearch);
        pnlSearch.Controls.Add(btnFindNext);
        pnlSearch.Controls.Add(btnFindPrev);

        // ── Editor SQL ─────────────────────────────────────────────────────
        rtbSqlScript = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Courier New", 8.5f),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both
        };

        // Syntax highlight con debounce 350ms
        _syntaxTimer = new System.Windows.Forms.Timer { Interval = 350 };
        _syntaxTimer.Tick += (_, _) => { _syntaxTimer.Stop(); ApplySyntaxHighlight(rtbSqlScript); };
        rtbSqlScript.TextChanged += (_, _) => { if (!_isHighlighting) { _syntaxTimer.Stop(); _syntaxTimer.Start(); } };
        rtbSqlScript.SelectionChanged += (_, _) => UpdateLineCol(rtbSqlScript);

        rtbSqlScript.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter) { e.Handled = true; _ = GuardAsync(ExecuteQueryAsync); }
            else if (e.Control && e.KeyCode == Keys.F) { e.Handled = true; ShowSearchRtb(pnlSearch, txtSearch); }
            else if (e.KeyCode == Keys.F3) { e.Handled = true; FindInRtb(rtbSqlScript, txtSearch, lblSearchCount, forward: !e.Shift); }
            else if (e.KeyCode == Keys.Escape && pnlSearch.Visible) { e.Handled = true; CloseSearchRtb(pnlSearch, lblSearchCount, rtbSqlScript); }
        };

        // Ordine aggiunta: lblSql (Top) → pnlSearch (Top, nascosto) → rtbSqlScript (Fill)
        pnlSql.Controls.Add(lblSql);
        pnlSql.Controls.Add(pnlSearch);
        pnlSql.Controls.Add(rtbSqlScript);
        rtbSqlScript.BringToFront(); // garantisce interattività fin dall'avvio
        splitRight.Panel1.Controls.Add(pnlSql);

        // ── Tab control ───────────────────────────────────────────────────
        tabResults = new TabControl { Dock = DockStyle.Fill };
        tabGrid = new TabPage("Griglia");
        tabText = new TabPage("Testo");
        tabMessages = new TabPage("Messaggi");

        dgvResults = new BufferedDataGridView
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
            DataSource = _resultsBindingSource,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(240, 240, 240),
                ForeColor = Color.FromArgb(30, 30, 30),
                Font = new Font("Calibri", 10f),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                SelectionBackColor = Color.FromArgb(204, 224, 245),
                SelectionForeColor = Color.FromArgb(30, 30, 30)
            }
        };
        dgvResults.RowPostPaint += (_, e) =>
        {
            // Indicatore record corrente stile Access: freccia sulla riga selezionata, altrimenti vuoto
            bool isCurrent = e.RowIndex == dgvResults.CurrentCell?.RowIndex;
            string marker = dgvResults.Rows[e.RowIndex].IsNewRow ? "*" : (isCurrent ? "\u25BA" : "");
            if (marker.Length == 0) return;

            // TextRenderer centra da s\u00E9: evita una MeasureString per riga a ogni frame
            Rectangle headerArea = new(e.RowBounds.Left, e.RowBounds.Top, dgvResults.RowHeadersWidth, e.RowBounds.Height);
            TextRenderer.DrawText(e.Graphics, marker, dgvResults.Font, headerArea, Color.Black,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        dgvResults.CurrentCellChanged += (_, _) => MoveCurrentRowMarker();
        dgvResults.CellEndEdit += (_, e) => _ = OnGridCellEndEditAsync(e.RowIndex, e.ColumnIndex);
        dgvResults.RowValidated += (_, e) => _ = OnGridRowValidatedAsync(e.RowIndex);
        // Intestazioni stile Access: la freccia a destra apre il menu di colonna,
        // il resto dell'intestazione ordina alternando crescente/decrescente.
        dgvResults.CellPainting += (_, e) => PaintColumnHeader(e);
        dgvResults.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex < 0) return;
            if (e.Button == MouseButtons.Right || IsHeaderArrowClick(e.ColumnIndex, e.X))
            {
                ShowColumnFilterMenu(e.ColumnIndex);
                return;
            }
            if (e.Button == MouseButtons.Left) SortByColumn(e.ColumnIndex);
        };
        dgvResults.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0)
            {
                dgvResults.CurrentCell = dgvResults.Rows[e.RowIndex].Cells[e.ColumnIndex];
            }
        };
        BuildGridContextMenu();
        dgvResults.ContextMenuStrip = gridContextMenu;

        // ── Esc: annulla la modifica della cella/riga in stile Access ──
        // Ctrl+C è gestito dal DataGridView: senza il testo delle intestazioni, così il
        // contenuto degli appunti è riutilizzabile da Ctrl+V.
        dgvResults.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;

        dgvResults.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.V && !dgvResults.IsCurrentCellInEditMode)
            {
                e.Handled = true;
                _ = GuardAsync(PasteFromClipboardIntoGridAsync);
                return;
            }
            if (e.KeyCode == Keys.Delete && !dgvResults.IsCurrentCellInEditMode)
            {
                e.Handled = true;
                _ = GuardAsync(DeleteSelectedGridRowsAsync);
                return;
            }
            if (e.KeyCode != Keys.Escape) return;
            try
            {
                _cancellingEdit = true;
                if (dgvResults.IsCurrentCellInEditMode)
                    dgvResults.CancelEdit();
                DataRow? dr = GetDataRowAt(dgvResults.CurrentCell?.RowIndex ?? -1);
                if (dr is not null && dr.RowState == DataRowState.Added && !_insertedNewRows.Contains(dr))
                {
                    _savingNewRows.Remove(dr);
                    dr.RejectChanges();
                }
                else
                {
                    _resultsBindingSource.CancelEdit();
                }
                e.Handled = true;
            }
            catch { /* best-effort cancel */ }
            finally { _cancellingEdit = false; }
        };

        // ── Colori righe alternate (stile Access: alternanza molto tenue) ──
        dgvResults.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(247, 247, 247)
        };
        dgvResults.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White,
            ForeColor = Color.FromArgb(20, 20, 20),
            SelectionBackColor = Color.FromArgb(204, 224, 245),
            SelectionForeColor = Color.FromArgb(20, 20, 20)
        };
        dgvResults.GridColor = Color.FromArgb(212, 212, 212);
        dgvResults.RowHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(240, 240, 240),
            ForeColor = Color.FromArgb(30, 30, 30),
            // Il selettore della riga selezionata si evidenzia, come in Access
            SelectionBackColor = Color.FromArgb(210, 222, 240),
            SelectionForeColor = Color.FromArgb(30, 30, 30)
        };
        // Griglia con linee sottili su tutte le celle (stile datasheet Access)
        dgvResults.RowTemplate.Height = 21;
        dgvResults.CellBorderStyle = DataGridViewCellBorderStyle.Single;
        dgvResults.DefaultCellStyle.Padding = new Padding(3, 1, 3, 1);
        dgvResults.ColumnHeadersDefaultCellStyle.Padding = new Padding(3, 0, 3, 0);
        dgvResults.BackgroundColor = Color.White;
        dgvResults.BorderStyle = BorderStyle.FixedSingle;

        dgvResults.DataBindingComplete += (_, _) => EnsureColumnHeaders();

        // ── Barra di navigazione record (stile Access) ─────────────────────
        // Layout: Record: |◄ ◄ [n] di N ► ►| ►*   Nessun filtro   Cerca: [   ]
        pnlGridNav = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = Color.FromArgb(240, 240, 240) };
        FlowLayoutPanel flowNav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4, 3, 0, 0)
        };

        static Button BuildNavButton(NavGlyph glyph, string tooltip)
        {
            Button b = new Button
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

        static Label BuildNavLabel(string text, int width) => new Label
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

        txtNavRecord = new TextBox
        {
            Width = 42,
            Height = 22,
            Margin = new Padding(2, 0, 2, 0),
            TextAlign = HorizontalAlignment.Center,
            Font = new Font("Segoe UI", 8f),
            BorderStyle = BorderStyle.FixedSingle
        };
        lblNavPosition = BuildNavLabel("di 0", 52);

        btnNavFilterState = new Button
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
        btnNavFilterState.FlatAppearance.BorderSize = 0;
        btnNavFilterState.FlatAppearance.MouseOverBackColor = Color.FromArgb(215, 228, 244);

        txtNavSearch = new TextBox
        {
            Width = 150,
            Height = 22,
            Margin = new Padding(2, 0, 2, 0),
            Font = new Font("Segoe UI", 8f),
            BorderStyle = BorderStyle.FixedSingle
        };

        btnNavFirst.Click += async (_, _) => await GuardAsync(() => NavigateGridAsync(() => _resultsBindingSource.MoveFirst()));
        btnNavPrev.Click += async (_, _) => await GuardAsync(() => NavigateGridAsync(() => _resultsBindingSource.MovePrevious()));
        btnNavNext.Click += async (_, _) => await GuardAsync(() => NavigateGridAsync(() => _resultsBindingSource.MoveNext()));
        btnNavLast.Click += async (_, _) => await GuardAsync(() => NavigateGridAsync(() => _resultsBindingSource.MoveLast()));
        btnNavNew.Click += async (_, _) => await GuardAsync(AddNewGridRowAsync);
        btnNavFilterState.Click += (_, _) => ToggleFilterState();
        txtNavRecord.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            GoToRecordFromNavBox();
        };
        txtNavRecord.Leave += (_, _) => GoToRecordFromNavBox();
        txtNavSearch.TextChanged += (_, _) => SearchInGrid(txtNavSearch.Text);
        _resultsBindingSource.PositionChanged += (_, _) => UpdateNavLabel();
        _resultsBindingSource.ListChanged += (_, _) => UpdateNavLabel();

        flowNav.Controls.Add(BuildNavLabel("Record:", 50));
        flowNav.Controls.Add(btnNavFirst);
        flowNav.Controls.Add(btnNavPrev);
        flowNav.Controls.Add(txtNavRecord);
        flowNav.Controls.Add(lblNavPosition);
        flowNav.Controls.Add(btnNavNext);
        flowNav.Controls.Add(btnNavLast);
        flowNav.Controls.Add(btnNavNew);
        flowNav.Controls.Add(btnNavFilterState);
        flowNav.Controls.Add(BuildNavLabel("Cerca:", 40));
        flowNav.Controls.Add(txtNavSearch);
        pnlGridNav.Controls.Add(flowNav);

        // ── Tab Testo: pannello con barra ricerca + editor generato ──────────
        Panel pnlGen = new Panel { Dock = DockStyle.Fill };

        pnlSearchGen = new Panel { Dock = DockStyle.Top, Height = 26, Visible = false, BackColor = SystemColors.Info, Padding = new Padding(2) };
        txtSearchGen = new TextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8.5f) };
        lblSearchCountGen = new Label { Dock = DockStyle.Right, Width = 70, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 7.5f) };
        Button btnGenFindNext = new Button { Dock = DockStyle.Right, Width = 26, Text = "▼", FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7f) };
        Button btnGenFindPrev = new Button { Dock = DockStyle.Right, Width = 26, Text = "▲", FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7f) };
        Button btnGenClose = new Button { Dock = DockStyle.Right, Width = 26, Text = "✕", FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7f) };
        btnGenFindNext.Click += (_, _) => FindInRtb(rtbGeneratedScript, txtSearchGen, lblSearchCountGen, forward: true);
        btnGenFindPrev.Click += (_, _) => FindInRtb(rtbGeneratedScript, txtSearchGen, lblSearchCountGen, forward: false);
        btnGenClose.Click += (_, _) => CloseSearchRtb(pnlSearchGen, lblSearchCountGen, rtbGeneratedScript);
        txtSearchGen.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; FindInRtb(rtbGeneratedScript, txtSearchGen, lblSearchCountGen, forward: true); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = true; CloseSearchRtb(pnlSearchGen, lblSearchCountGen, rtbGeneratedScript); }
            else if (e.Shift && e.KeyCode == Keys.F3) { e.Handled = true; FindInRtb(rtbGeneratedScript, txtSearchGen, lblSearchCountGen, forward: false); }
        };
        txtSearchGen.TextChanged += (_, _) => FindInRtb(rtbGeneratedScript, txtSearchGen, lblSearchCountGen, forward: true, resetPos: true);
        pnlSearchGen.Controls.Add(txtSearchGen);
        pnlSearchGen.Controls.Add(lblSearchCountGen);
        pnlSearchGen.Controls.Add(btnGenClose);
        pnlSearchGen.Controls.Add(btnGenFindNext);
        pnlSearchGen.Controls.Add(btnGenFindPrev);

        rtbGeneratedScript = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Courier New", 8.5f),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            AcceptsTab = true,
            DetectUrls = false
        };

        rtbGeneratedScript.SelectionChanged += (_, _) => UpdateLineCol(rtbGeneratedScript);
        rtbGeneratedScript.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.F) { e.Handled = true; ShowSearchRtb(pnlSearchGen, txtSearchGen); }
            else if (e.KeyCode == Keys.F3) { e.Handled = true; FindInRtb(rtbGeneratedScript, txtSearchGen, lblSearchCountGen, forward: !e.Shift); }
            else if (e.KeyCode == Keys.Escape && pnlSearchGen.Visible) { e.Handled = true; CloseSearchRtb(pnlSearchGen, lblSearchCountGen, rtbGeneratedScript); }
        };

        pnlGen.Controls.Add(pnlSearchGen);
        pnlGen.Controls.Add(rtbGeneratedScript);
        rtbGeneratedScript.BringToFront();

        lstMessages = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Courier New", 7.5f),
            HorizontalScrollbar = true
        };

        tabGrid.Controls.Add(pnlGridNav);
        tabGrid.Controls.Add(dgvResults);
        // Il controllo Fill va portato in primo piano, altrimenti il layout gli assegna
        // tutta l'area del tab e i pannelli agganciati coprono intestazioni e barra record.
        dgvResults.BringToFront();
        tabText.Controls.Add(pnlGen);
        tabMessages.Controls.Add(lstMessages);
        tabResults.TabPages.AddRange(new[] { tabGrid, tabText, tabMessages });
        splitRight.Panel2.Controls.Add(tabResults);

        splitMain.Panel2.Controls.Add(splitRight);
        Controls.Add(splitMain);

        // Rimuove manipolazioni manuali della z-order: lascia che il sistema di Dock gestisca il layout

        ResumeLayout(false);
    }

    // ─── Guard helper ────────────────────────────────────────────────────────
    /// <summary>Runs async operation; silently skips if already busy.</summary>
    private async Task GuardAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (Exception ex) { AddMessage($"❌ Errore: {ex.Message}"); tabResults.SelectedTab = tabMessages; }
        finally { _busy = false; }
    }

    // ─── Data loading ────────────────────────────────────────────────────────

    private async Task LoadInitialDataAsync()
    {
        SetLoading(true);
        try
        {
            var info = await _sql.GetServerInfoAsync();
            if (info is not null)
                Text = $"PBScript – SQL Explorer – {info.ServerName} – {_config.Database}";

            var dbResult = await _dbExplorer.GetDatabasesAsync();
            if (dbResult.Success && dbResult.Data is { Count: > 0 })
            {
                // Populate WITHOUT triggering SelectedIndexChanged
                cmbDatabases.Items.Clear();
                foreach (var db in dbResult.Data)
                    cmbDatabases.Items.Add(db.Name);

                // Set initial selection BEFORE wiring the event to prevent spurious OnDatabaseChanged
                // Prefer LastDatabase from settings (already applied to _config if present)
                int idx = cmbDatabases.Items.IndexOf(_config.Database);
                cmbDatabases.SelectedIndex = idx >= 0 ? idx : 0;

                // Wire the event only now – from here on it fires only on real user interaction
                cmbDatabases.SelectedIndexChanged += OnDatabaseChanged;

                // Load tables for the initially selected db explicitly
                await LoadTablesAsync(cmbDatabases.SelectedItem as string ?? _config.Database);
            }
            else
            {
                cmbDatabases.SelectedIndexChanged += OnDatabaseChanged;
            }
        }
        finally { SetLoading(false); }
    }

    private async void OnDatabaseChanged(object? sender, EventArgs e)
    {
        if (cmbDatabases.SelectedItem is string dbName)
            await GuardAsync(() => LoadTablesAsync(dbName));
    }

    private async Task LoadTablesAsync(string dbName)
    {
        lblStatusDb.Text = $"Database: {dbName}";
        SetLoading(true);
        try
        {
            _allTables = new();
            var result = await _dbExplorer.GetTablesAsync(dbName);
            if (result.Success && result.Data is not null)
                _allTables = result.Data;
            FilterTableList();
        }
        finally { SetLoading(false); }
    }

    private void FilterTableList()
    {
        var filter = txtTableSearch.Text.ToLowerInvariant();
        lstTables.BeginUpdate();
        lstTables.Items.Clear();
        foreach (var t in _allTables)
            if (t.TableName.ToLowerInvariant().Contains(filter))
                lstTables.Items.Add($"[{t.TableSchema}].[{t.TableName}]");
        lstTables.EndUpdate();
    }

    /// <summary>
    /// Aggiorna l'editor SQL con la SELECT della tabella selezionata nel ListBox.
    /// Carica chiavi e colonne identity per la generazione degli script.
    /// </summary>
    /// <summary>
    /// Aggiorna l'editor SQL con la SELECT della tabella selezionata.
    /// Sincrono: nessun I/O. Chiavi e identity vengono caricate in GenerateScriptAsync quando servono.
    /// </summary>
    private void LoadTableStructure()
    {
        if (lstTables.SelectedItem is not string item) return;
        Regex rx = new Regex(@"\[([^\]]+)\]\.\[([^\]]+)\]");
        Match m = rx.Match(item);
        if (!m.Success) return;
        string schema = m.Groups[1].Value;
        string tbl = m.Groups[2].Value;

        _selectedTable = new TableInfo { TableSchema = schema, TableName = tbl, TableType = "BASE TABLE" };
        _keyColumns = new List<string>();
        _identityColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _nonInsertableColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string scriptText = $"SELECT * FROM [{schema}].[{tbl}]";
        _lastAutoScript = scriptText;

        rtbSqlScript.Text = scriptText;
        rtbSqlScript.SelectionStart = 0;
        rtbSqlScript.ScrollToCaret();
        rtbSqlScript.BringToFront();
        rtbSqlScript.Refresh();
    }

    // ─── Query execution ─────────────────────────────────────────────────────

    private async Task ExecuteQueryAsync()
    {
        var script = rtbSqlScript.Text.Trim();
        if (string.IsNullOrEmpty(script))
        {
            AddMessage("Errore: nessuno script da eseguire");
            return;
        }

        SetLoading(true);
        var goCount = Regex.Matches(script, @"\bGO\b", RegexOptions.IgnoreCase).Count;
        var lineCount = script.Split('\n').Length;
        AddMessage($"📋 Inizio esecuzione ({lineCount} righe, {goCount} batch GO)");

        // Detect special operations (triggers) and add more descriptive messages for execution
        string? specialOp = null;
        try
        {
            var s = script.ToUpperInvariant();
            if (Regex.IsMatch(s, @"\bDISABLE\s+TRIGGER\b")) specialOp = "Disattivazione trigger";
            else if (Regex.IsMatch(s, @"\bENABLE\s+TRIGGER\b")) specialOp = "Attivazione trigger";
            else if (Regex.IsMatch(s, @"\bCREATE\s+TRIGGER\b")) specialOp = "Creazione trigger";
            else if (Regex.IsMatch(s, @"\bDROP\s+TRIGGER\b")) specialOp = "Eliminazione trigger";
            else if (Regex.IsMatch(s, @"\bALTER\s+TRIGGER\b")) specialOp = "Modifica trigger";
        }
        catch { specialOp = null; }

        if (!string.IsNullOrEmpty(specialOp))
            AddMessage($"▶ Inizio esecuzione operazione: {specialOp}");

        try
        {
            var result = await _sql.ExecuteQueryAsync(script);
            if (result.Success && result.Data is { Count: > 0 })
            {
                _queryResult = result.Data;
                PopulateGrid(_queryResult);
                AddMessage($"✅ {result.Data.Count} righe restituite");
                if (!string.IsNullOrEmpty(specialOp)) AddMessage($"▶ Fine esecuzione operazione: {specialOp}");
                tabResults.SelectedTab = tabGrid;
            }
            else if (result.Success)
            {
                _queryResult = new();
                await PrepareEmptyGridFromCurrentTableAsync();
                AddMessage("✅ Script eseguito con successo (nessun risultato, griglia pronta)");
                if (!string.IsNullOrEmpty(specialOp)) AddMessage($"▶ Fine esecuzione operazione: {specialOp}");
                tabResults.SelectedTab = tabGrid;
            }
            else
            {
                AddMessage($"❌ Errore: {result.Error}");
                tabResults.SelectedTab = tabMessages;
            }
        }
        finally { SetLoading(false); }
    }

    /// <summary>Azzera filtri e ordinamento prima di ricaricare la griglia.
    /// Va fatto sul BindingSource e non solo sullo stato interno: Filter e Sort
    /// restano puntati alle colonne della query precedente e, se la nuova query ha
    /// colonne diverse, l'assegnazione della DataSource fallisce
    /// ("Sort string contains a property that is not in the IBindingList").</summary>
    private void ResetGridFiltersAndSort()
    {
        _columnFilters.Clear();
        _filtersSuspended = false;
        _sortColumn = null;
        _sortAscending = true;
        _markerRowIndex = -1;
        _insertedNewRows.Clear();
        _savingNewRows.Clear();

        try { _resultsBindingSource.Filter = ""; }
        catch { /* nessuna sorgente associata */ }
        try { _resultsBindingSource.Sort = ""; }
        catch { /* nessuna sorgente associata */ }
    }

    private void PopulateGrid(List<Dictionary<string, object?>> rows)
    {
        ResetGridFiltersAndSort();

        if (rows.Count == 0)
        {
            _resultsTable = null;
            _resultsBindingSource.DataSource = null;
            UpdateNavLabel();
            return;
        }

        dgvResults.SuspendLayout();

        List<string> columnNames = new(rows[0].Keys);
        var dt = new DataTable();
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
        _resultsTable = dt;
        _resultsBindingSource.DataSource = dt;

        // Imposta larghezza colonne (nasconde la colonna tecnica di indice riga)
        int totalWidth = 0;
        foreach (DataGridViewColumn col in dgvResults.Columns)
        {
            if (col.Name == RowIdxColumn) { col.Visible = false; continue; }
            col.HeaderText = col.Name;
            // Lo spazio della freccia del menu di colonna va aggiunto alla larghezza utile
            col.Width = Math.Min(220, Math.Max(70, col.HeaderText.Length * 9 + HeaderArrowZoneWidth));
            totalWidth += col.Width;
        }

        // Forza il refresh delle scrollbars
        dgvResults.ResumeLayout();
        dgvResults.PerformLayout();

        // Se la larghezza totale supera la larghezza visibile serve un ridisegno.
        // Niente Application.DoEvents(): pompare i messaggi qui costa un giro di
        // layout completo e apre la porta a rientri durante il caricamento.
        if (totalWidth > dgvResults.ClientSize.Width)
            dgvResults.Invalidate();

        EnsureColumnHeaders();
        UpdateNavLabel();
    }

    private async Task PrepareEmptyGridFromCurrentTableAsync()
    {
        ResetGridFiltersAndSort();

        var dt = new DataTable();

        await EnsureTableKeysLoadedAsync();
        if (_selectedTable is not null)
        {
            string db = cmbDatabases.SelectedItem as string ?? _config.Database;
            var colsResult = await _dbExplorer.GetTableColumnsAsync(db, _selectedTable.TableSchema, _selectedTable.TableName);
            if (colsResult.Success && colsResult.Data is { Count: > 0 })
            {
                foreach (var c in colsResult.Data.OrderBy(c => c.OrdinalPosition))
                    dt.Columns.Add(c.ColumnName, typeof(string));
            }
        }

        dt.Columns.Add(RowIdxColumn, typeof(string));
        _resultsTable = dt;
        _resultsBindingSource.DataSource = dt;

        foreach (DataGridViewColumn col in dgvResults.Columns)
        {
            if (col.Name == RowIdxColumn) { col.Visible = false; continue; }
            col.HeaderText = col.Name;
            // Lo spazio della freccia del menu di colonna va aggiunto alla larghezza utile
            col.Width = Math.Min(220, Math.Max(70, col.HeaderText.Length * 9 + HeaderArrowZoneWidth));
        }

        UpdateNavLabel();
    }

    private void UpdateNavLabel()
    {
        if (lblNavPosition is null) return;
        int count = _resultsBindingSource.Count;
        int pos = count == 0 ? 0 : _resultsBindingSource.Position + 1;
        lblNavPosition.Text = $"di {count}";
        if (txtNavRecord is not null && !txtNavRecord.Focused)
            txtNavRecord.Text = pos.ToString();
        UpdateFilterStateButton();
    }

    /// <summary>Aggiorna l'indicatore "Nessun filtro / Filtrato / Non filtrato"
    /// con la stessa semantica della barra record di Access.</summary>
    private void UpdateFilterStateButton()
    {
        if (btnNavFilterState is null) return;

        bool hasFilters = _columnFilters.Count > 0;
        btnNavFilterState.Enabled = hasFilters;
        btnNavFilterState.ForeColor = hasFilters ? Color.FromArgb(30, 80, 160) : Color.FromArgb(120, 120, 120);
        btnNavFilterState.Text = !hasFilters
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
        if (txtNavRecord is null || _resultsBindingSource.Count == 0) return;

        if (!int.TryParse(txtNavRecord.Text.Trim(), out int requested))
        {
            UpdateNavLabel();
            return;
        }

        int target = Math.Clamp(requested, 1, _resultsBindingSource.Count) - 1;
        _resultsBindingSource.Position = target;
        UpdateNavLabel();
    }

    /// <summary>Ricerca incrementale su tutte le colonne visibili: porta la cella
    /// corrente sulla prima corrispondenza, come la casella "Cerca" di Access.</summary>
    private void SearchInGrid(string term)
    {
        if (term.Length == 0) return;

        foreach (DataGridViewRow row in dgvResults.Rows)
        {
            if (row.IsNewRow) continue;
            foreach (DataGridViewCell cell in row.Cells)
            {
                DataGridViewColumn col = dgvResults.Columns[cell.ColumnIndex];
                if (!col.Visible || col.Name == RowIdxColumn) continue;

                string text = Convert.ToString(cell.Value) ?? "";
                if (text.Length == 0 || text.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;

                dgvResults.CurrentCell = cell;
                return;
            }
        }
    }

    // ─── Script generation ───────────────────────────────────────────────────

    private async Task GenerateScriptAsync(string scriptType)
    {
        if (_queryResult.Count == 0)
        {
            MessageBox.Show("Esegui prima una SELECT per ottenere dati.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var selectedIndices = GetSelectedRowIndices();
        if (selectedIndices.Count == 0)
        {
            MessageBox.Show("Seleziona almeno una riga nella griglia.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var db = cmbDatabases.SelectedItem as string ?? _config.Database;

        SetLoading(true);
        try
        {
            await EnsureTableKeysLoadedAsync();
        }
        finally { SetLoading(false); }

        if (_selectedTable is null)
        {
            MessageBox.Show("Impossibile identificare la tabella. Selezionane una dalla lista.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var allCols = _queryResult[0].Keys.ToList();

        // Escludi automaticamente i campi di tipo VersionTs/rowversion/timestamp
        var versionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "VersionTs", "versionts", "rowversion", "timestamp" };
        var versionCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in allCols)
        {
            // Se il nome suggerisce una colonna version oppure il valore è un byte[] (rowversion)
            var val = _queryResult[0].GetValueOrDefault(col);
            if (versionNames.Contains(col) || val is byte[])
                versionCols.Add(col);
        }

        var filteredCols = allCols.Except(versionCols, StringComparer.OrdinalIgnoreCase).ToList();
        var filteredKeyCols = _keyColumns.Except(versionCols, StringComparer.OrdinalIgnoreCase).ToList();


        var selRows = selectedIndices.Select(i => _queryResult[i]).ToList();

        string script;
        if (scriptType == "DELETE")
        {
            // For DELETE do not show the column selector: use detected key columns. If none, fallback to all columns.
            var keyColsForDelete = filteredKeyCols.Count > 0 ? filteredKeyCols : filteredCols;
            if (keyColsForDelete.Count == 0)
            {
                MessageBox.Show("Impossibile determinare colonne chiave per DELETE.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            script = BuildDeleteScript(_selectedTable, keyColsForDelete, selRows);
        }
        else
        {
            using var dlg = new ColumnSelectorDialog(filteredCols, filteredKeyCols, $"{_selectedTable.TableSchema}.{_selectedTable.TableName}", scriptType, _defaultConditionalUpdate);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            // Remember user's choice for next time
            _defaultConditionalUpdate = dlg.ConditionalUpdate;

            if (scriptType == "INSERT")
                script = BuildInsertScript(_selectedTable, dlg.SelectedKeyColumns, dlg.SelectedColumns, selRows, _identityColumns);
            else // UPDATE
                script = dlg.ConditionalUpdate
                    ? BuildConditionalUpdateScript(_selectedTable, dlg.SelectedKeyColumns, dlg.SelectedColumns, selRows)
                    : BuildUpdateScript(_selectedTable, dlg.SelectedKeyColumns, dlg.SelectedColumns, selRows);
        }

        AppendToGeneratedScript(script);
        //  EnsureHorizontalScrollBar(rtbGeneratedScript);
        tabResults.SelectedTab = tabText;
        AddMessage($"Creazione script {scriptType} terminata ({selRows.Count} comandi)");
    }

    // Appends text to the generated script pane (thread-safe)
    private void AppendToGeneratedScript(string text)
    {
        if (rtbGeneratedScript is null) return;
        Action apply = () =>
        {
            if (!string.IsNullOrEmpty(rtbGeneratedScript.Text))
                rtbGeneratedScript.AppendText(Environment.NewLine + text);
            else
                rtbGeneratedScript.AppendText(text);
            rtbGeneratedScript.SelectionStart = rtbGeneratedScript.TextLength;
            rtbGeneratedScript.ScrollToCaret();
            rtbGeneratedScript.Refresh();
        };

        if (rtbGeneratedScript.InvokeRequired) rtbGeneratedScript.Invoke(apply);
        else apply();
    }

    /// <summary>
    /// Individua la tabella corrente dall'SQL nell'editor (se diversa da quella selezionata) e ne carica
    /// chiavi primarie e colonne identity, se non già caricate.
    /// </summary>
    private async Task EnsureTableKeysLoadedAsync()
    {
        var db = cmbDatabases.SelectedItem as string ?? _config.Database;

        // L'SQL nel box ha sempre la precedenza sulla tabella selezionata nella treeview.
        // Se il FROM dell'SQL corrente indica una tabella diversa da _selectedTable, aggiorna _selectedTable.
        Match sqlMatch = Regex.Match(rtbSqlScript.Text, @"FROM\s+(?:\[?(\w+)\]?\.)?\[?(\w+)\]?", RegexOptions.IgnoreCase);
        if (sqlMatch.Success)
        {
            string sch = sqlMatch.Groups[1].Success ? sqlMatch.Groups[1].Value : "dbo";
            string tbl = sqlMatch.Groups[2].Value;
            bool differs = _selectedTable is null
                || !string.Equals(_selectedTable.TableName, tbl, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_selectedTable.TableSchema, sch, StringComparison.OrdinalIgnoreCase);
            if (differs)
            {
                _selectedTable = new TableInfo { TableSchema = sch, TableName = tbl, TableType = "BASE TABLE" };
                _keyColumns = new List<string>();
                _identityColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _nonInsertableColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        if (_selectedTable is not null && _keyColumns.Count == 0)
        {
            _keyColumns = await _dbExplorer.GetTableKeyColumnsAsync(db, _selectedTable.TableSchema, _selectedTable.TableName);
            _identityColumns = new HashSet<string>(await _dbExplorer.GetIdentityColumnsAsync(db, _selectedTable.TableSchema, _selectedTable.TableName), StringComparer.OrdinalIgnoreCase);
            _nonInsertableColumns = new HashSet<string>(await _dbExplorer.GetNonInsertableColumnsAsync(db, _selectedTable.TableSchema, _selectedTable.TableName), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>True se la colonna non va mai inclusa in un INSERT: identity, calcolata
    /// o rowversion/timestamp. SQL Server la valorizza da sé.</summary>
    private bool IsExcludedFromInsert(string columnName) =>
        _identityColumns.Contains(columnName) || _nonInsertableColumns.Contains(columnName);

    private List<int> GetSelectedRowIndices()
    {
        var indices = new List<int>();
        foreach (DataGridViewRow row in dgvResults.SelectedRows)
        {
            int idx = GetOriginalRowIndex(row);
            if (idx >= 0) indices.Add(idx);
        }
        indices.Sort();
        return indices;
    }

    private async Task NavigateGridAsync(Action moveAction)
    {
        if (!await CommitCurrentGridRowAsync()) return;
        moveAction();
    }

    private async Task AddNewGridRowAsync()
    {
        if (_resultsTable is null)
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
        if (dgvResults.AllowUserToAddRows && dgvResults.Rows.Count > 0)
        {
            int newRowIndex = dgvResults.Rows.Count - 1;
            int firstEditableCol = -1;
            foreach (DataGridViewColumn col in dgvResults.Columns)
            {
                if (col.Visible && col.Name != RowIdxColumn && !col.ReadOnly) { firstEditableCol = col.Index; break; }
            }
            if (firstEditableCol >= 0)
            {
                dgvResults.CurrentCell = dgvResults.Rows[newRowIndex].Cells[firstEditableCol];
                dgvResults.BeginEdit(true);
            }
        }
        else
        {
            _resultsBindingSource.AddNew();
        }
    }

    /// <summary>True solo se esiste una riga in inserimento con dei valori: le righe
    /// Added vuote non sono inserimenti in sospeso e non devono bloccare i comandi
    /// (<see cref="CommitCurrentGridRowAsync"/> le salta, quindi non si sbloccherebbero mai).</summary>
    private bool HasPendingNewRow()
    {
        for (int i = 0; i < _resultsBindingSource.Count; i++)
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
        if (_resultsTable is null) return;

        List<DataRow> toDiscard = new();
        for (int i = 0; i < _resultsBindingSource.Count; i++)
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
        if (_resultsTable is null) return false;

        int currentRowIndex = dgvResults.CurrentCell?.RowIndex ?? -1;
        int currentColIndex = dgvResults.CurrentCell?.ColumnIndex ?? -1;
        bool wasEditingCurrentCell = dgvResults.IsCurrentCellInEditMode;

        if (wasEditingCurrentCell)
            dgvResults.EndEdit();

        dgvResults.EndEdit();
        _resultsBindingSource.EndEdit();

        if (wasEditingCurrentCell && currentRowIndex >= 0 && currentColIndex >= 0)
            await OnGridCellEndEditAsync(currentRowIndex, currentColIndex);

        DiscardEmptyPendingRows();

        // Valida solo la riga su cui si trovava il cursore, cioè quella che l'utente
        // sta effettivamente lasciando. Un errore di inserimento va mostrato una volta,
        // quando si esce dalla riga: se qui si tentasse di reinserire QUALSIASI riga
        // Added rimasta nella griglia, un inserimento fallito in precedenza
        // ripresenterebbe lo stesso errore durante un'azione successiva non correlata
        // (es. un Ctrl+V su un'altra riga). HasPendingNewRow, chiamato subito dopo da
        // chi invoca questo metodo, resta comunque a bloccare nuovi inserimenti finché
        // quella riga non viene sistemata.
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

    /// <summary>Legge l'indice originale (in _queryResult) memorizzato nella colonna tecnica nascosta della riga.</summary>
    private static int GetOriginalRowIndex(DataGridViewRow row)
    {
        object? v = row.Cells[RowIdxColumn]?.Value;
        return v is string s && int.TryParse(s, out int idx) ? idx : -1;
    }

    private DataRow? GetDataRowAt(int gridRowIndex)
    {
        if (gridRowIndex < 0 || gridRowIndex >= _resultsBindingSource.Count) return null;
        return (_resultsBindingSource[gridRowIndex] as DataRowView)?.Row;
    }

    private int FindGridRowByOriginalIndex(int origIdx)
    {
        for (int i = 0; i < _resultsBindingSource.Count; i++)
        {
            if ((_resultsBindingSource[i] as DataRowView)?.Row is DataRow dr &&
                dr[RowIdxColumn] is string s && int.TryParse(s, out int idx) && idx == origIdx)
                return i;
        }
        return -1;
    }

    // ─── Editing celle in griglia (stile foglio dati Access) ──────────────────

    private async Task OnGridCellEndEditAsync(int rowIndex, int columnIndex)
    {
        try
        {
            if (rowIndex < 0 || columnIndex < 0 || _resultsTable is null) return;
            DataGridViewRow gridRow = dgvResults.Rows[rowIndex];
            if (gridRow.IsNewRow) return;

            string colName = dgvResults.Columns[columnIndex].Name;
            if (colName == RowIdxColumn) return;

            int origIdx = GetOriginalRowIndex(gridRow);
            if (origIdx < 0 || origIdx >= _queryResult.Count) return;

            object? newVal = gridRow.Cells[columnIndex].Value;
            string? newStr = newVal is null || newVal == DBNull.Value ? null : Convert.ToString(newVal);
            object? oldVal = _queryResult[origIdx].GetValueOrDefault(colName);
            string? oldStr = oldVal is null ? null : Convert.ToString(oldVal);

            if (string.Equals(newStr, oldStr, StringComparison.Ordinal)) return;

            await EnsureTableKeysLoadedAsync();
            if (_selectedTable is null || _keyColumns.Count == 0)
            {
                MessageBox.Show("Impossibile determinare la chiave primaria della tabella: la modifica non può essere salvata.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                gridRow.Cells[columnIndex].Value = oldStr is null ? (object)DBNull.Value : oldStr;
                return;
            }

            var keyRow = _queryResult[origIdx];
            string fn = FullName(_selectedTable);
            string whereClause = WhereClause(_keyColumns, keyRow);
            string sql = $"UPDATE {fn} SET {colName} = {FmtVal(newStr)} WHERE {whereClause}";

            SetLoading(true);
            try
            {
                var result = await _sql.ExecuteCommandAsync(sql);
                if (!result.Success)
                {
                    MessageBox.Show($"Salvataggio non riuscito: {result.Error}\n\nCorreggi il valore e riprova.", "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    // Mantieni la riga in modifica: riporta il focus sulla cella per la correzione
                    try
                    {
                        dgvResults.CurrentCell = gridRow.Cells[columnIndex];
                        dgvResults.BeginEdit(true);
                    }
                    catch { /* focus best-effort */ }
                }
                else
                {
                    _queryResult[origIdx][colName] = newStr;
                    AddMessage($"✏️ {fn}: colonna {colName} aggiornata ({whereClause})");
                }
            }
            finally { SetLoading(false); }
        }
        catch (Exception ex) { AddMessage($"❌ Errore salvataggio modifica: {ex.Message}"); }
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

            await EnsureTableKeysLoadedAsync();
            if (_selectedTable is null)
            {
                MessageBox.Show("Impossibile determinare la tabella di destinazione.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var insertCols = dr.Table.Columns.Cast<DataColumn>()
                .Select(c => c.ColumnName)
                .Where(c => c != RowIdxColumn && !IsExcludedFromInsert(c))
                .ToList();

            string fn = FullName(_selectedTable);
            string cols = string.Join(", ", insertCols);
            string vals = string.Join(", ", insertCols.Select(c => FmtVal(dr[c] == DBNull.Value ? null : Convert.ToString(dr[c]))));
            string insert = $"INSERT INTO {fn} ( {cols} ) VALUES ( {vals} )";

            // La rilettura viaggia nello stesso batch dell'INSERT: SCOPE_IDENTITY() è valido
            // solo lì, e la SELECT dopo i trigger mostra identity, default e campi calcolati.
            string predicate = BuildInsertedRowPredicate(dr);

            SetLoading(true);
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
                    AddMessage($"➕ {fn}: nuova riga inserita (riga non rileggibile: nessuna chiave individuabile)");
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
                    AddMessage($"➕ {fn}: nuova riga inserita e riletta dal database");
                }
                else
                    AddMessage($"➕ {fn}: nuova riga inserita (rilettura senza risultati)");
            }
            finally { SetLoading(false); }
        }
        catch (Exception ex) { AddMessage($"❌ Errore inserimento riga: {ex.Message}"); }
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
    /// <c>_queryResult</c>, così la riga inserita diventa a tutti gli effetti una riga
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
        if (gridIndex >= 0) dgvResults.InvalidateRow(gridIndex);
    }

    private async Task DeleteSelectedGridRowsAsync()
    {
        var selectedGridRowIndexes = dgvResults.SelectedRows
            .Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow)
            .Select(r => r.Index)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        if (selectedGridRowIndexes.Count == 0 && dgvResults.CurrentRow is not null && !dgvResults.CurrentRow.IsNewRow)
            selectedGridRowIndexes.Add(dgvResults.CurrentRow.Index);

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

            int origIdx = GetOriginalRowIndex(dgvResults.Rows[gridIdx]);
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
                _resultsBindingSource.RemoveAt(gridIdx);
            }
            AddMessage($"🗑 {pendingGridOnlyRows.Count} riga/e nuova/e rimossa/e dalla griglia");
        }

        if (dbRowIndices.Count == 0)
            return;

        await EnsureTableKeysLoadedAsync();
        if (_selectedTable is null || _keyColumns.Count == 0)
        {
            MessageBox.Show("Impossibile determinare la chiave primaria della tabella: eliminazione non consentita.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string fn = FullName(_selectedTable);
        int deleted = 0;

        SetLoading(true);
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
                    if (gridRowIdx >= 0) _resultsBindingSource.RemoveAt(gridRowIdx);
                    _queryResult.RemoveAt(origIdx);
                }
                else AddMessage($"❌ Eliminazione riga fallita: {result.Error}");
            }
            AddMessage($"🗑 {deleted} riga/e eliminata/e da {fn}");
        }
        finally { SetLoading(false); }
    }

    // ─── Filtri, ordinamento e menu contestuale griglia (stile Access) ────────

    private void BuildGridContextMenu()
    {
        gridContextMenu = new ContextMenuStrip();
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
        miClearColFilter.Click += (_, _) => { if (dgvResults.CurrentCell is not null) ClearColumnFilter(dgvResults.Columns[dgvResults.CurrentCell.ColumnIndex].Name); };
        var miClearAllFilters = new ToolStripMenuItem("Rimuovi tutti i filtri");
        miClearAllFilters.Click += (_, _) => ClearAllGridFilters();
        var miDeleteRow = new ToolStripMenuItem("Elimina riga/e selezionata/e");
        miDeleteRow.Click += async (_, _) => await GuardAsync(DeleteSelectedGridRowsAsync);
        gridContextMenu.Items.AddRange(new ToolStripItem[]
        {
            miCopyCell, miCopyRow, miDuplicateRow, miPasteRow, new ToolStripSeparator(),
            miFilterSel, miClearColFilter, miClearAllFilters, new ToolStripSeparator(),
            miDeleteRow
        });
    }

    private void CopyGridSelectionToClipboard(bool cellOnly)
    {
        try
        {
            if (cellOnly)
            {
                string cellText = Convert.ToString(dgvResults.CurrentCell?.Value) ?? "";
                if (!string.IsNullOrEmpty(cellText))
                    Clipboard.SetText(cellText);
                return;
            }

            var sb = new StringBuilder();
            var rowsWithSelection = dgvResults.SelectedCells.Cast<DataGridViewCell>()
                .Select(c => c.RowIndex)
                .Distinct()
                .Where(idx => idx >= 0 && !dgvResults.Rows[idx].IsNewRow)
                .OrderBy(idx => idx)
                .ToList();
            if (rowsWithSelection.Count == 0 && dgvResults.CurrentRow is not null && !dgvResults.CurrentRow.IsNewRow)
                rowsWithSelection.Add(dgvResults.CurrentRow.Index);

            // Ordine di visualizzazione, lo stesso usato dall'incolla: le colonne
            // possono essere state riordinate dall'utente.
            List<DataGridViewColumn> columns = ClipboardColumns();
            foreach (int idx in rowsWithSelection)
            {
                var row = dgvResults.Rows[idx];
                var vals = columns.Select(c => Convert.ToString(row.Cells[c.Index].Value) ?? "");
                sb.AppendLine(string.Join("\t", vals));
            }
            if (sb.Length > 0)
                Clipboard.SetText(sb.ToString());
        }
        catch (Exception ex)
        {
            AddMessage($"❌ Copia negli appunti non riuscita: {ex.Message}");
        }
    }

    /// <summary>Colonne coinvolte in copia/incolla, nell'ordine in cui l'utente le vede.</summary>
    private List<DataGridViewColumn> ClipboardColumns() =>
        dgvResults.Columns.Cast<DataGridViewColumn>()
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
        DataRow? row = GetDataRowAt(dgvResults.CurrentCell?.RowIndex ?? -1);
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
        DataRow row = _resultsTable!.NewRow();
        foreach (KeyValuePair<string, object?> kv in values)
        {
            if (kv.Key == RowIdxColumn) continue;
            if (IsExcludedFromInsert(kv.Key)) continue;
            row[kv.Key] = kv.Value ?? (object)DBNull.Value;
        }
        row[RowIdxColumn] = DBNull.Value;
        _resultsTable.Rows.Add(row);
        return row;
    }

    /// <summary>Porta il cursore sulla prima cella modificabile della riga indicata.</summary>
    private void FocusPendingRow(DataRow row)
    {
        int gridIndex = FindGridRowByDataRow(row);
        if (gridIndex < 0) return;

        _resultsBindingSource.Position = gridIndex;
        foreach (DataGridViewColumn col in ClipboardColumns())
        {
            if (col.ReadOnly) continue;
            dgvResults.CurrentCell = dgvResults.Rows[gridIndex].Cells[col.Index];
            return;
        }
    }

    /// <summary>Incolla gli appunti: un valore singolo finisce nella cella corrente,
    /// una o più righe TSV diventano nuovi record in inserimento, pronti da modificare.</summary>
    private async Task PasteFromClipboardIntoGridAsync()
    {
        if (_resultsTable is null)
        {
            MessageBox.Show("Esegui prima una query.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (Exception ex)
        {
            AddMessage($"❌ Lettura degli appunti non riuscita: {ex.Message}");
            return;
        }

        List<string[]> clipboardRows = ParseClipboardGrid(text);
        if (clipboardRows.Count == 0) return;

        // Valore singolo su una riga esistente: si comporta come una normale modifica di cella
        bool singleValue = clipboardRows.Count == 1 && clipboardRows[0].Length == 1;
        if (singleValue && dgvResults.CurrentCell is not null && dgvResults.CurrentRow is not null
            && !dgvResults.CurrentRow.IsNewRow)
        {
            int rowIndex = dgvResults.CurrentCell.RowIndex;
            int colIndex = dgvResults.CurrentCell.ColumnIndex;
            dgvResults.CurrentCell.Value = clipboardRows[0][0];
            await OnGridCellEndEditAsync(rowIndex, colIndex);
            return;
        }

        // Se il cursore è già su una riga in inserimento (riga nuova "*" oppure riga
        // Added non ancora salvata) la prima riga degli appunti riempie QUELLA riga:
        // non si esce dalla riga e non si forza un salvataggio parziale.
        DataRow? pendingTarget = PendingRowAtCursor();
        bool onNewRow = dgvResults.CurrentRow?.IsNewRow == true;

        if (pendingTarget is null && !onNewRow)
        {
            if (!await CommitCurrentGridRowAsync()) return;
            if (HasPendingNewRow())
            {
                MessageBox.Show("Prima salva o completa la riga in inserimento corrente.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        await EnsureTableKeysLoadedAsync();

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
        AddMessage($"📋 {pasted} riga/e incollata/e come nuovo record: rivedi i valori e spostati su un'altra riga per confermare.");
    }

    /// <summary>Duplica la riga selezionata creando una nuova riga con gli stessi valori
    /// (escluse le colonne identity), pronta per l'inserimento come nuovo record.</summary>
    private async Task DuplicateSelectedRowAsNewAsync()
    {
        if (_resultsTable is null)
        {
            MessageBox.Show("Esegui prima una query.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int srcGridIndex = dgvResults.CurrentCell?.RowIndex ?? -1;
        if (srcGridIndex < 0 && dgvResults.SelectedRows.Count > 0)
            srcGridIndex = dgvResults.SelectedRows.Cast<DataGridViewRow>().Min(r => r.Index);

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

        await EnsureTableKeysLoadedAsync();

        // Copia i valori sorgente prima di aggiungere la nuova riga
        var snapshot = new Dictionary<string, object?>();
        foreach (DataColumn col in _resultsTable.Columns)
        {
            if (col.ColumnName == RowIdxColumn) continue;
            if (IsExcludedFromInsert(col.ColumnName)) continue;
            snapshot[col.ColumnName] = source[col];
        }

        // Porta il focus sulla nuova riga così l'utente può rivedere/modificare prima del commit
        DataRow newRow = AppendPendingRow(snapshot);
        FocusPendingRow(newRow);

        AddMessage("➕ Riga duplicata: rivedi i valori e spostati su un'altra riga per confermare l'inserimento.");
    }

    private int FindGridRowByDataRow(DataRow target)
    {
        for (int i = 0; i < _resultsBindingSource.Count; i++)
        {
            if ((_resultsBindingSource[i] as DataRowView)?.Row == target)
                return i;
        }
        return -1;
    }

    /// <summary>Reimposta il testo delle intestazioni colonna (perso quando le colonne
    /// vengono rigenerate automaticamente al rebinding) e nasconde la colonna tecnica.</summary>
    private void EnsureColumnHeaders()
    {
        // Riapplica lo stile intestazioni a runtime (necessario se il costruttore non è stato
        // rieseguito, es. con Hot Reload) così il testo resta leggibile.
        dgvResults.EnableHeadersVisualStyles = false;
        dgvResults.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 240, 240);
        dgvResults.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(30, 30, 30);
        dgvResults.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(204, 224, 245);
        dgvResults.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.FromArgb(30, 30, 30);

        foreach (DataGridViewColumn col in dgvResults.Columns)
        {
            if (col.Name == RowIdxColumn) { col.Visible = false; continue; }
            if (string.IsNullOrEmpty(col.HeaderText) || col.HeaderText != col.Name)
                col.HeaderText = col.Name;
        }
        InvalidateColumnHeaders();
    }

    private void FilterGridByCurrentCellValue()
    {
        if (dgvResults.CurrentCell is null) return;
        string colName = dgvResults.Columns[dgvResults.CurrentCell.ColumnIndex].Name;
        if (colName == RowIdxColumn) return;
        object? val = dgvResults.CurrentCell.Value;
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
        try { _resultsBindingSource.Filter = expression; }
        catch (Exception ex) { AddMessage($"⚠ Filtro non applicabile: {ex.Message}"); }
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


    private void SortByColumn(int columnIndex)
    {
        if (_resultsTable is null) return;
        string colName = dgvResults.Columns[columnIndex].Name;
        if (colName == RowIdxColumn) return;

        bool ascending = !string.Equals(_sortColumn, colName, StringComparison.OrdinalIgnoreCase) || !_sortAscending;
        SortColumn(colName, ascending);
    }

    private void SortColumn(string colName, bool ascending)
    {
        if (_resultsTable is null || colName == RowIdxColumn) return;

        _sortColumn = colName;
        _sortAscending = ascending;

        try { _resultsBindingSource.Sort = $"[{colName}] {(ascending ? "ASC" : "DESC")}"; }
        catch { /* colonna non ordinabile */ }
        InvalidateColumnHeaders(); // il riordino ridisegna già le celle: qui serve solo il glifo
        UpdateNavLabel();
    }

    // ─── Menu di colonna stile Access ───────────────────────────────────────

    /// <summary>Numero massimo di valori distinti elencati nel menu di colonna.</summary>
    private const int MaxDistinctFilterValues = 1000;

    /// <summary>Larghezza della zona cliccabile della freccia nell'intestazione.</summary>
    private const int HeaderArrowZoneWidth = 18;

    private void ShowColumnFilterMenu(int columnIndex)
    {
        if (_resultsTable is null) return;
        DataGridViewColumn column = dgvResults.Columns[columnIndex];
        string colName = column.Name;
        if (colName == RowIdxColumn) return;

        // Valori distinti: null e stringa vuota confluiscono in "(Vuoti)" come in Access.
        List<string?> distinctValues = _resultsTable.AsEnumerable()
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
        menu.Show(dgvResults, dgvResults.GetCellDisplayRectangle(columnIndex, -1, true));
    }

    // ─── Ridisegni mirati ───────────────────────────────────────────────────

    /// <summary>Sposta l'indicatore ► ridisegnando solo la riga che lo perde e quella
    /// che lo prende, invece di invalidare tutta la griglia a ogni movimento.</summary>
    private void MoveCurrentRowMarker()
    {
        int newRow = dgvResults.CurrentCell?.RowIndex ?? -1;
        if (newRow == _markerRowIndex) return;

        InvalidateRowHeader(_markerRowIndex);
        InvalidateRowHeader(newRow);
        _markerRowIndex = newRow;
    }

    private void InvalidateRowHeader(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= dgvResults.Rows.Count) return;

        Rectangle rect = dgvResults.GetRowDisplayRectangle(rowIndex, true);
        if (rect.Height <= 0) return; // riga fuori dall'area visibile
        dgvResults.Invalidate(new Rectangle(0, rect.Top, dgvResults.RowHeadersWidth, rect.Height));
    }

    /// <summary>Ridisegna la sola striscia delle intestazioni: serve dopo un cambio di
    /// filtro o ordinamento, che modifica i glifi ma non le celle.</summary>
    private void InvalidateColumnHeaders() =>
        dgvResults.Invalidate(new Rectangle(0, 0, dgvResults.ClientSize.Width, dgvResults.ColumnHeadersHeight));

    /// <summary>Disegna nell'intestazione la freccia del menu e gli indicatori di
    /// filtro/ordinamento, come nella visualizzazione Foglio dati di Access.</summary>
    private void PaintColumnHeader(DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex != -1 || e.ColumnIndex < 0 || e.Graphics is null) return;
        DataGridViewColumn column = dgvResults.Columns[e.ColumnIndex];
        if (column.Name == RowIdxColumn) return;

        // Sfondo e bordi restano quelli nativi: si sostituisce solo il testo per
        // riservare lo spazio della freccia a destra.
        e.Paint(e.CellBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);
        Rectangle textArea = new(
            e.CellBounds.Left + 3,
            e.CellBounds.Top,
            Math.Max(0, e.CellBounds.Width - HeaderArrowZoneWidth - 6),
            e.CellBounds.Height);
        TextRenderer.DrawText(e.Graphics, column.HeaderText, dgvResults.ColumnHeadersDefaultCellStyle.Font,
            textArea, dgvResults.ColumnHeadersDefaultCellStyle.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        int arrowCenterX = e.CellBounds.Right - HeaderArrowZoneWidth / 2 - 2;
        int centerY = e.CellBounds.Top + e.CellBounds.Height / 2;
        bool filtered = _columnFilters.ContainsKey(column.Name);
        bool sorted = string.Equals(_sortColumn, column.Name, StringComparison.OrdinalIgnoreCase);

        if (filtered) DrawFunnelGlyph(e.Graphics, arrowCenterX - 11, centerY);
        else if (sorted) DrawSortGlyph(e.Graphics, arrowCenterX - 11, centerY, _sortAscending);

        DrawDropDownGlyph(e.Graphics, arrowCenterX, centerY);
        e.Handled = true;
    }

    // Oggetti GDI dei glifi di intestazione: riusati invece di essere allocati
    // a ogni cella di ogni frame (una dozzina di allocazioni per ridisegno).
    private static readonly SolidBrush GlyphBrush = new(Color.FromArgb(70, 70, 70));
    private static readonly Pen AccentPen = new(Color.FromArgb(60, 90, 150));

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
        x >= dgvResults.Columns[columnIndex].Width - HeaderArrowZoneWidth;

    // ─── Script builders ─────────────────────────────────────────────────────

    private static string FmtVal(object? val)
    {
        if (val is null || val == DBNull.Value) return "NULL";
        if (val is bool b) return b ? "1" : "0";
        if (val is DateTime dt) return $" {{ts '{dt:yyyy-MM-dd HH:mm:ss.fff}'}}";
        if (val is Guid g) return $"'{g}'";
        if (val is byte[] bytes) return "0x" + Convert.ToHexString(bytes);
        if (val is string s)
        {
            if (s.Length >= 10 && DateTime.TryParse(s, out var dtp))
                return $" {{ts '{dtp:yyyy-MM-dd HH:mm:ss.fff}'}}";
            return $"'{s.Replace("'", "''")}'";
        }
        return Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
    }

    private static string FullName(TableInfo t) =>
        t.TableSchema.ToLower() == "dbo"
            ? t.TableName
            : $"{t.TableSchema}.{t.TableName}";

    private static string WhereClause(List<string> keys, Dictionary<string, object?> row) =>
        string.Join(" AND ", keys.Select(c =>
            row.GetValueOrDefault(c) is null or DBNull
                ? $"{c} IS NULL"
                : $"{c} = {FmtVal(row.GetValueOrDefault(c))}"));

    private static string BuildInsertScript(TableInfo tbl, List<string> keyCols, List<string> valCols, List<Dictionary<string, object?>> rows, HashSet<string>? identityCols = null)
    {
        var sb = new StringBuilder();
        var allCols = keyCols.Concat(valCols).ToList();
        var fn = FullName(tbl);
        var hasIdentity = identityCols is not null && allCols.Any(c => identityCols.Contains(c));
        if (hasIdentity)
        {
            sb.AppendLine($"SET IDENTITY_INSERT {fn} ON");
            sb.AppendLine();
        }
        foreach (var row in rows)
        {
            if (keyCols.Count > 0)
            {
                sb.AppendLine($"IF NOT EXISTS( SELECT 1 FROM {fn} WHERE {WhereClause(keyCols, row)} )");
                sb.AppendLine("BEGIN");
            }
            var tab = keyCols.Count > 0 ? "\t" : "";
            var cols = string.Join(", ", allCols);
            var vals = string.Join(", ", allCols.Select(c => FmtVal(row.GetValueOrDefault(c))));
            sb.AppendLine($"{tab}INSERT INTO {fn} ( {cols} )");
            sb.AppendLine($"{tab}SELECT {vals}");
            if (keyCols.Count > 0) sb.AppendLine("END");
            sb.AppendLine();
        }
        if (hasIdentity)
        {
            sb.AppendLine($"SET IDENTITY_INSERT {fn} OFF");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildUpdateScript(TableInfo tbl, List<string> keyCols, List<string> setCols, List<Dictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();
        var fn = FullName(tbl);
        foreach (var row in rows)
        {
            var set = string.Join(",\n\t", setCols.Select(c => $"{c} = {FmtVal(row.GetValueOrDefault(c))}"));
            sb.AppendLine($"UPDATE {fn}");
            sb.AppendLine($"SET\n\t{set}");
            sb.AppendLine($"WHERE {WhereClause(keyCols, row)}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // Builds conditional update similar to Audit: IF NOT EXISTS(check on keys+modified cols) BEGIN UPDATE ... END
    private static string BuildConditionalUpdateScript(TableInfo tbl, List<string> keyCols, List<string> setCols, List<Dictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();
        var fn = FullName(tbl);
        foreach (var row in rows)
        {
            // determine modified cols by comparing original values? here we don't have old values, so use setCols as modified set
            var modifiedCols = setCols;
            if (modifiedCols.Count == 0) continue;

            // build check conditions: keys + modified cols
            var checkConditions = new List<string>();
            foreach (var key in keyCols)
            {
                var val = row.GetValueOrDefault(key);
                if (val is null or DBNull) checkConditions.Add($"{key} IS NULL");
                else checkConditions.Add($"{key} = {FmtVal(val)}");
            }
            foreach (var col in modifiedCols)
            {
                var val = row.GetValueOrDefault(col);
                if (val is null or DBNull) checkConditions.Add($"{col} IS NULL");
                else checkConditions.Add($"{col} = {FmtVal(val)}");
            }

            var set = string.Join(", ", modifiedCols.Select(c => $"{c} = {FmtVal(row.GetValueOrDefault(c))}"));

            sb.AppendLine($"IF NOT EXISTS(SELECT 1 FROM {fn} WHERE {string.Join(" AND ", checkConditions)})");
            sb.AppendLine("BEGIN");
            sb.AppendLine($"  UPDATE {fn}");
            sb.AppendLine($"  SET {set}");
            sb.AppendLine($"  WHERE {WhereClause(keyCols, row)}");
            sb.AppendLine("END");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildDeleteScript(TableInfo tbl, List<string> keyCols, List<Dictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();
        var fn = FullName(tbl);
        foreach (var row in rows)
        {
            if (keyCols != null && keyCols.Count > 0)
            {
                sb.AppendLine($"IF EXISTS(SELECT 1 FROM {fn} WHERE {WhereClause(keyCols, row)})");
                sb.AppendLine("BEGIN");
                sb.AppendLine($"  DELETE FROM {fn} WHERE {WhereClause(keyCols, row)}");
                sb.AppendLine("END");
            }
            else
            {
                // Fallback: nessuna colonna chiave disponibile, impossibile generare un DELETE sicuro
                sb.AppendLine($"-- ATTENZIONE: nessuna colonna chiave per DELETE su {fn}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ─── Audit operations ────────────────────────────────────────────────────

    private async Task AuditInitializeAsync()
    {
        var db = cmbDatabases.SelectedItem as string;
        if (string.IsNullOrEmpty(db)) { MessageBox.Show("Seleziona prima un database."); return; }

        using var dlg = new AuditFilterDialog(db, _auditFilter, _auditExclude);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _auditFilter = dlg.AuditFilter;
        _auditExclude = dlg.AuditExclude;

        // Salva le impostazioni modificate
        SettingsService.SaveAuditSettings(new AuditSettings
        {
            AuditFilter = _auditFilter,
            AuditExclude = _auditExclude
        });

        var auditDb = $"{db}_UPD";
        AddMessage("Inizializza/Resetta db Audit_UPD (SETUP/INSTALLAZIONE)");
        SetLoading(true);
        try
        {
            var res = await _sql.ExecuteQueryAsync($"SELECT name FROM {db}..sysobjects WHERE type = 'U' {_auditFilter} {_auditExclude}");
            var tables = res.Success && res.Data is not null
                ? res.Data.Select(r => r.GetValueOrDefault("name")?.ToString() ?? "").Where(n => !string.IsNullOrEmpty(n)).ToList()
                : new List<string>();

            AddMessage($"Tabelle selezionate con filtri: {tables.Count}");

            var tableColumns = new Dictionary<string, List<ColumnInfo>>();
            foreach (var tbl in tables)
            {
                var colRes = await _dbExplorer.GetTableColumnsAsync(db, "dbo", tbl);
                tableColumns[tbl] = colRes.Success && colRes.Data is not null ? colRes.Data : new List<ColumnInfo>();
            }

            var script = AuditScriptBuilder.BuildInitScript(db, auditDb, tableColumns);
            SetSqlScript(script);
            AddMessage($"Script generato: {tables.Count} tabelle + {tables.Count * 3} trigger.");

            AddMessage("Esecuzione script in corso...");
            var execResult = await _sql.ExecuteScriptAsync(script);
            if (execResult.Success)
            {
                AddMessage($"✅ Script eseguito con successo.");
                SetSqlScript(string.Empty);
                rtbGeneratedScript?.Clear();
            }
            else
                AddMessage($"❌ Errore: {execResult.Error}");
        }
        finally { SetLoading(false); }
    }

    private void AuditRemove()
    {
        var db = cmbDatabases.SelectedItem as string;
        if (string.IsNullOrEmpty(db)) { MessageBox.Show("Seleziona prima un database."); return; }
        SetSqlScript(AuditScriptBuilder.BuildRemoveScript(db));
        AddMessage("Fine - Creazione script: Eliminazione sistema Audit (DISINSTALLAZIONE)");
    }

    private void AuditActivate()
    {
        var db = cmbDatabases.SelectedItem as string;
        if (string.IsNullOrEmpty(db)) { MessageBox.Show("Seleziona prima un database."); return; }
        SetSqlScript(AuditScriptBuilder.BuildActivateScript(db));
        AddMessage("Fine - Creazione script: Attivazione trigger Audit (INIZIO ATTIVITÀ)");
    }

    private void AuditDeactivate()
    {
        var db = cmbDatabases.SelectedItem as string;
        if (string.IsNullOrEmpty(db)) { MessageBox.Show("Seleziona prima un database."); return; }
        SetSqlScript(AuditScriptBuilder.BuildDeactivateScript(db));
        AddMessage("Fine - Creazione script: Disattivazione trigger Audit (PAUSA ATTIVITÀ)");
    }

    private async Task AuditGenerateScriptAsync()
    {
        var db = cmbDatabases.SelectedItem as string;
        if (string.IsNullOrEmpty(db)) { MessageBox.Show("Seleziona prima un database."); return; }

        var auditDb = db.EndsWith("_UPD") ? db : $"{db}_UPD";
        var originDb = auditDb.Replace("_UPD", "");
        AddMessage("Genera Script Audit da eSYS/eSYS_UPD (RILASCIO)");
        SetLoading(true);
        try
        {
            var chk = await _sql.ExecuteQueryAsync($"SELECT COUNT(*) AS cnt FROM sys.databases WHERE name = '{auditDb}'");
            if (chk.Success && Convert.ToInt32(chk.Data?[0].GetValueOrDefault("cnt") ?? 0) == 0)
            {
                AddMessage($"❌ Database audit '{auditDb}' non trovato.");
                tabResults.SelectedTab = tabMessages;
                return;
            }

            var tablesRes = await _sql.ExecuteQueryAsync($"SELECT name FROM {auditDb}.sys.tables ORDER BY name");
            var tableNames = tablesRes.Success && tablesRes.Data is not null
                ? tablesRes.Data.Select(r => r.GetValueOrDefault("name")?.ToString() ?? "").Where(n => !string.IsNullOrEmpty(n)).ToList()
                : new List<string>();
            AddMessage($"Tabelle trovate: {tableNames.Count}");

            var sb = new StringBuilder();
            int totalCmds = 0;
            var auditMeta = new HashSet<string>(
                new[] { "dba_tipo_comando", "dba_tipo_dato", "dba_macchina", "dba_utente", "dba_data", "dba_applicazione", "dba_guid", "dba_progupd" },
                StringComparer.OrdinalIgnoreCase);

            sb.AppendLine($"-- Script Audit  db:{originDb}  audit:{auditDb}  {DateTime.Now}");
            sb.AppendLine();

            foreach (var tbl in tableNames)
            {
                AddMessage($"Analisi {tbl}…");
                var rows = await _sql.ExecuteQueryAsync($"SELECT * FROM {auditDb}..{tbl} ORDER BY dba_data");
                if (!rows.Success || rows.Data is null || rows.Data.Count == 0) continue;

                var keysRes = await _dbExplorer.GetTableKeyColumnsAsync(originDb, "dbo", tbl);
                var dataCols = rows.Data[0].Keys.Where(k => !auditMeta.Contains(k)).ToList();
                var effKeys = keysRes.Count > 0 ? keysRes : dataCols;

                sb.AppendLine($"-- == {tbl} ({rows.Data.Count} righe) ==");

                var byGuid = new Dictionary<string, (Dictionary<string, object?>? Old, Dictionary<string, object?>? New)>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows.Data)
                {
                    var guid = row.GetValueOrDefault("dba_guid")?.ToString() ?? Guid.NewGuid().ToString();
                    var tipo = (row.GetValueOrDefault("dba_tipo_dato")?.ToString() ?? "").Trim().ToUpper();
                    if (!byGuid.ContainsKey(guid)) byGuid[guid] = (null, null);
                    var e = byGuid[guid];
                    byGuid[guid] = tipo == "OLD" ? (row, e.New) : (e.Old, row);
                }

                foreach (var (_, (oldRow, newRow)) in byGuid)
                {
                    var cmd = ((newRow ?? oldRow)?.GetValueOrDefault("dba_tipo_comando")?.ToString() ?? "").Trim().ToUpper();
                    var rec = newRow ?? oldRow;
                    if (rec is null) continue;

                    if (cmd == "I")
                    {
                        var cols = string.Join(", ", dataCols);
                        var vals = string.Join(", ", dataCols.Select(c => FmtVal(rec.GetValueOrDefault(c))));
                        sb.AppendLine($"IF NOT EXISTS(SELECT 1 FROM {tbl} WHERE {WhereClause(effKeys, rec)})");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine($"  INSERT INTO {tbl} ({cols}) VALUES ({vals})");
                        sb.AppendLine("END");
                    }
                    else if (cmd == "U")
                    {
                        if (oldRow is null || newRow is null) continue;

                        // Trova solo i campi che sono stati modificati
                        var modifiedCols = new List<string>();
                        foreach (var col in dataCols.Except(effKeys, StringComparer.OrdinalIgnoreCase))
                        {
                            var oldVal = oldRow.GetValueOrDefault(col);
                            var newVal = newRow.GetValueOrDefault(col);

                            // Confronta i valori
                            bool isDifferent = false;
                            if (oldVal is null && newVal is not null) isDifferent = true;
                            else if (oldVal is not null && newVal is null) isDifferent = true;
                            else if (oldVal is not null && newVal is not null)
                            {
                                isDifferent = !oldVal.Equals(newVal);
                            }

                            if (isDifferent)
                                modifiedCols.Add(col);
                        }

                        if (modifiedCols.Count == 0) continue;

                        // Costruisci la condizione IF NOT EXISTS con i campi modificati + chiavi
                        var checkConditions = new List<string>();
                        foreach (var key in effKeys)
                        {
                            var val = newRow.GetValueOrDefault(key);
                            if (val is null or DBNull)
                                checkConditions.Add($"{key} IS NULL");
                            else
                                checkConditions.Add($"{key} = {FmtVal(val)}");
                        }
                        foreach (var col in modifiedCols)
                        {
                            var val = newRow.GetValueOrDefault(col);
                            if (val is null or DBNull)
                                checkConditions.Add($"{col} IS NULL");
                            else
                                checkConditions.Add($"{col} = {FmtVal(val)}");
                        }

                        var setStr = string.Join(", ", modifiedCols.Select(c => $"{c} = {FmtVal(newRow.GetValueOrDefault(c))}"));

                        sb.AppendLine($"IF NOT EXISTS(SELECT 1 FROM {tbl} WHERE {string.Join(" AND ", checkConditions)})");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine($"  UPDATE {tbl}");
                        sb.AppendLine($"  SET {setStr}");
                        sb.AppendLine($"  WHERE {WhereClause(effKeys, newRow)}");
                        sb.AppendLine("END");
                    }
                    else if (cmd == "D")
                    {
                        var dr = oldRow ?? rec;
                        sb.AppendLine($"IF EXISTS(SELECT 1 FROM {tbl} WHERE {WhereClause(effKeys, dr)})");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine($"  DELETE FROM {tbl} WHERE {WhereClause(effKeys, dr)}");
                        sb.AppendLine("END");
                    }

                    sb.AppendLine();
                    totalCmds++;
                }
            }

            sb.AppendLine($"-- Comandi totali: {totalCmds}");
            rtbGeneratedScript.Text = sb.ToString();
            tabResults.SelectedTab = tabText;
            AddMessage($"✅ Script generato: {totalCmds} comandi da {tableNames.Count} tabelle");
        }
        finally { SetLoading(false); }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void SetLoading(bool loading)
    {
        if (InvokeRequired)
        {
            Invoke(() => SetLoading(loading));
            return;
        }

        lblStatusLoading.Text = loading ? "⏳ Caricamento…" : "";
        UseWaitCursor = loading;
    }

    // Appends text to the SQL editor in a thread-safe way
    private void AppendToSqlScript(string text)
    {
        if (rtbSqlScript is null) return;
        Action apply = () =>
        {
            if (!string.IsNullOrEmpty(rtbSqlScript.Text))
                rtbSqlScript.AppendText(Environment.NewLine + text);
            else
                rtbSqlScript.AppendText(text);
            rtbSqlScript.SelectionStart = rtbSqlScript.TextLength;
            rtbSqlScript.ScrollToCaret();
            rtbSqlScript.Refresh();
        };

        if (rtbSqlScript.InvokeRequired) rtbSqlScript.Invoke(apply);
        else apply();
    }

    // Replaces the SQL editor content in a thread-safe way
    private void SetSqlScript(string text)
    {
        if (rtbSqlScript is null) return;
        Action apply = () =>
        {
            _lastAutoScript = text;
            rtbSqlScript.Text = text;
            rtbSqlScript.SelectionStart = 0;
            rtbSqlScript.ScrollToCaret();
            rtbSqlScript.Refresh();
        };

        if (rtbSqlScript.InvokeRequired) rtbSqlScript.Invoke(apply);
        else apply();
    }

    private void AddMessage(string msg)
    {
        lstMessages.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        lstMessages.TopIndex = lstMessages.Items.Count - 1;
    }

    // ─── Syntax highlight ─────────────────────────────────────────────────────

    private void ApplySyntaxHighlight(RichTextBox rtb)
    {
        if (_isHighlighting || rtb == null) return;
        _isHighlighting = true;

        int selStart = rtb.SelectionStart;
        int selLen = rtb.SelectionLength;
        string text = rtb.Text;

        SendMessage(rtb.Handle, WM_SETREDRAW, false, 0);
        try
        {
            rtb.SelectAll();
            rtb.SelectionColor = SystemColors.WindowText;

            foreach (Match m in Regex.Matches(text, @"--[^\r\n]*"))
            {
                rtb.Select(m.Index, m.Length);
                rtb.SelectionColor = Color.DarkGreen;
            }

            foreach (Match m in Regex.Matches(text, @"'(?:[^']|'')*'"))
            {
                rtb.Select(m.Index, m.Length);
                rtb.SelectionColor = Color.DarkRed;
            }

            foreach (Match m in Regex.Matches(text, _sqlKeywordPattern, RegexOptions.IgnoreCase))
            {
                rtb.Select(m.Index, m.Length);
                if (rtb.SelectionColor != Color.DarkGreen && rtb.SelectionColor != Color.DarkRed)
                    rtb.SelectionColor = Color.Blue;
            }
        }
        finally
        {
            rtb.SelectionStart = selStart;
            rtb.SelectionLength = selLen;
            rtb.SelectionColor = SystemColors.WindowText;
            SendMessage(rtb.Handle, WM_SETREDRAW, true, 0);
            rtb.Invalidate();
            _isHighlighting = false;
        }
    }

    // ─── Ricerca generica su RichTextBox ─────────────────────────────────────

    private static void ShowSearchRtb(Panel pnl, TextBox txt)
    {
        pnl.Visible = true;
        txt.Focus();
        txt.SelectAll();
    }

    private static void CloseSearchRtb(Panel pnl, Label lblCount, RichTextBox rtb)
    {
        pnl.Visible = false;
        lblCount.Text = string.Empty;
        rtb.Focus();
    }

    private static void FindInRtb(RichTextBox rtb, TextBox txtSearch, Label lblCount, bool forward, bool resetPos = false)
    {
        string needle = txtSearch.Text;
        if (string.IsNullOrEmpty(needle))
        {
            lblCount.Text = string.Empty;
            return;
        }

        string haystack = rtb.Text;
        MatchCollection all = Regex.Matches(haystack, Regex.Escape(needle), RegexOptions.IgnoreCase);
        if (all.Count == 0)
        {
            lblCount.Text = "Non trovato";
            lblCount.ForeColor = Color.Red;
            return;
        }

        lblCount.ForeColor = SystemColors.WindowText;

        int startFrom = resetPos ? 0 : (forward
            ? rtb.SelectionStart + rtb.SelectionLength
            : rtb.SelectionStart - 1);

        int idx;
        if (forward)
        {
            idx = haystack.IndexOf(needle, Math.Max(0, startFrom), StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = haystack.IndexOf(needle, 0, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            int backFrom = Math.Max(0, startFrom);
            idx = backFrom > 0 ? haystack.LastIndexOf(needle, backFrom, StringComparison.OrdinalIgnoreCase) : -1;
            if (idx < 0) idx = haystack.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase);
        }

        if (idx >= 0)
        {
            rtb.Select(idx, needle.Length);
            rtb.ScrollToCaret();
        }

        int current = 0;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Index == idx) { current = i + 1; break; }
        }

        lblCount.Text = $"{current}/{all.Count}";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Persist UI settings (table search + default conditional update)
        try
        {
            var s = Services.SettingsService.LoadAuditSettings();
            s.TableSearch = txtTableSearch?.Text ?? string.Empty;
            s.DefaultConditionalUpdate = _defaultConditionalUpdate;
            // Save last connected server/database
            s.LastServer = _config.Server ?? string.Empty;
            s.LastDatabase = _config.Database ?? string.Empty;
            s.LastUser = _config.User ?? string.Empty;
            Services.SettingsService.SaveAuditSettings(s);
        }
        catch { }

        _sql.Dispose();
        base.OnFormClosed(e);
    }
}
