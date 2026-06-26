# PBScriptNew

Applicazione desktop Windows Forms (.NET 8, C#) per esplorare database SQL Server, generare script DML e configurare un sistema di audit completo basato su trigger.

---

## Panoramica

PBScript fornisce un ambiente integrato per:

- Connettersi a istanze SQL Server con autenticazione SQL o Windows.
- Navigare database, tabelle e metadati di colonne/chiavi/indici.
- Eseguire query SQL con supporto batch (`GO`).
- Generare script `INSERT`, `UPDATE`, `DELETE` idempotenti a partire dai risultati di una query.
- Installare, gestire e sfruttare un sistema di audit tramite trigger DML che registra ogni modifica in un database parallelo (`{db}_UPD`).
- Produrre script di rilascio pronti all'uso dai dati registrati dall'audit.

---

## Requisiti

- Windows 10 / 11 / Server 2019+
- .NET 8 SDK
- SQL Server 2012 o superiore (target del database)
- Visual Studio 2022+ oppure `dotnet` CLI

---

## Build ed esecuzione

```bash
dotnet restore
dotnet build -c Debug
dotnet run --project PBScriptNewCS.csproj
```

Il binario compilato si trova in `bin/Debug/net8.0-windows/` o `bin/Release/net8.0-windows/`.

---

## Configurazione

Il file `appsettings.json` nella root contiene le impostazioni di connessione e le opzioni applicative. **Non committare credenziali reali.**

```json
{
  "Database": {
    "Server": "localhost",
    "User": "",
    "Password": "",
    "Database": "",
    "IntegratedSecurity": true
  },
  "Application": {
    "ColorSyntax": true,
    "EscludiTimestamp": false
  }
}
```

Le stesse chiavi possono essere sovrascritte tramite variabili d'ambiente: `SQL_SERVER`, `SQL_USER`, `SQL_PASSWORD`, `SQL_DATABASE`, `INTEGRATED_SECURITY`.

Le impostazioni utente (ultimo server, filtri audit, preferenze UI) vengono salvate automaticamente in `%APPDATA%/PBScriptNew/settings.json`.

---

## Struttura del progetto

```
Program.cs               Entrypoint
Forms/
  LoginForm.cs           Autenticazione e avvio connessione
  MainForm.cs            Interfaccia principale
  ColumnSelectorDialog.cs  Selezione colonne/chiavi per script DML
  AuditFilterDialog.cs   Configurazione filtri per il sistema audit
Services/
  SqlService.cs          Connessione, esecuzione query, formattazione valori SQL
  DatabaseExplorerService.cs  Metadati: database, tabelle, colonne, chiavi, indici
  ScriptGeneratorService.cs  Generazione DDL e INSERT da dati reali
  AuditScriptBuilder.cs  Generazione script trigger e gestione db audit
  SettingsService.cs     Persistenza impostazioni JSON
Models/
  SqlResult.cs           Wrapper risultato operazioni (Ok / Fail)
  DatabaseModels.cs      DTO per metadati DB
  AuditSettings.cs       Impostazioni persistenti audit e UI
  DatabaseConfig.cs      Parametri di connessione
  ApplicationOptions.cs  Opzioni applicative da appsettings.json
Config/
  GlobalConfig.cs        Singleton di configurazione (appsettings + env vars)
```

---

## Funzionalità dettagliate

### Autenticazione e connessione (LoginForm)

- Supporta **autenticazione SQL Server** (username + password) e **Windows Authentication** (Integrated Security).
- Carica automaticamente l'ultima connessione usata dai settings persistenti.
- Valida la connessione in modo asincrono prima di aprire la finestra principale.
- In caso di errore mostra un messaggio descrittivo senza crash.

---

### Esplorazione database (MainForm + DatabaseExplorerService)

- **Selezione database:** dropdown che elenca tutti i database utente dell'istanza (esclude i database di sistema).
- **Lista tabelle:** ListBox con tabelle nel formato `[schema].[nome_tabella]`, filtrabile tramite campo di ricerca in tempo reale.
- **Cambio database:** aggiorna automaticamente la lista tabelle.
- **Metadati tabella:** il servizio espone colonne, tipi, nullable, valori default, posizione ordinale, flag IDENTITY, chiavi primarie, foreign key e indici.

---

### Editor SQL

- **RichTextBox** con syntax highlighting SQL (parole chiave in blu, commenti in verde, stringhe in rosso), applicato con debounce di 350 ms per non bloccare la digitazione.
- Esecuzione con **Ctrl+Enter** o pulsante "Esegui".
- Supporto **batch SQL** separati da `GO` (case-insensitive), ognuno eseguito in sequenza con timeout indipendente (300 s per query, 600 s per script lunghi).
- Indicatore **riga/colonna** nel statusbar.
- **Ricerca inline** (Ctrl+F): barra con campo testo, navigazione precedente/successivo (F3 / Shift+F3), contatore occorrenze, chiusura con Esc. Disponibile sia nell'editor che nel tab "Testo".

---

### Visualizzazione risultati (tab Griglia, Testo, Messaggi)

- **Griglia:** DataGridView read-only con i risultati dell'ultima SELECT; supporta selezione multipla delle righe per la generazione script.
- **Testo:** RichTextBox dove vengono accumulati gli script generati, con ricerca inline indipendente.
- **Messaggi:** log timestampato delle operazioni (connessione, esecuzione, errori, righe restituite/modificate).

---

### Generazione script DML

Tutti gli script sono **idempotenti**: usano `IF NOT EXISTS`/`IF EXISTS` per garantire sicurezza in ambienti già popolati.

#### INSERT

- Apre **ColumnSelectorDialog** per scegliere le colonne da includere e le colonne chiave per la clausola `IF NOT EXISTS`.
- Aggiunge automaticamente `SET IDENTITY_INSERT ON/OFF` se la tabella ha colonne IDENTITY.
- Esclude colonne `rowversion`/`timestamp`.
- Gestisce correttamente `NULL`, date, GUID, `byte[]` (hex), stringhe con escape delle virgolette singole.

```sql
IF NOT EXISTS (SELECT 1 FROM [dbo].[Tabella] WHERE [Id] = 42)
BEGIN
    INSERT INTO [dbo].[Tabella] ([Id], [Nome]) SELECT 42, N'Esempio'
END
```

#### UPDATE

- Selezione separata di colonne chiave (WHERE) e colonne da aggiornare (SET).
- Opzione **UPDATE condizionato**: genera un `IF NOT EXISTS` che verifica che i valori siano già aggiornati, evitando scritture inutili.

```sql
-- UPDATE condizionato
IF NOT EXISTS (SELECT 1 FROM [dbo].[Tabella] WHERE [Id] = 42 AND [Nome] = N'Nuovo')
BEGIN
    UPDATE [dbo].[Tabella] SET [Nome] = N'Nuovo' WHERE [Id] = 42
END
```

#### DELETE

- Genera un `IF EXISTS` basato sulle colonne chiave selezionate.

```sql
IF EXISTS (SELECT 1 FROM [dbo].[Tabella] WHERE [Id] = 42)
BEGIN
    DELETE FROM [dbo].[Tabella] WHERE [Id] = 42
END
```

---

### Sistema di audit tramite trigger (AuditScriptBuilder + MainForm)

Il sistema registra ogni modifica DML (INSERT, UPDATE, DELETE) su un database parallelo `{NomeDB}_UPD`, mantenendo sia il valore OLD che il valore NEW per ogni operazione.

#### Inizializzazione / Reset (menu Audit → Inizializza/Resetta)

1. **AuditFilterDialog** consente di configurare:
   - **Filtro tabelle:** condizione SQL aggiuntiva sulla colonna `name` di `sys.tables` (es. `SUBSTRING(name,3,1) = '_'`).
   - **Esclusioni:** lista di tabelle da ignorare.
2. Vengono recuperati i metadati di tutte le tabelle che soddisfano i filtri.
3. Viene generato ed eseguito uno script che:
   - Elimina il database `{db}_UPD` se esiste e lo ricrea vuoto.
   - Dropa tutti i trigger `tr_AUDIT_*` esistenti nel database sorgente.
   - Per ogni tabella inclusa crea **tre trigger AFTER**: INSERT, UPDATE, DELETE.
4. Ogni trigger inserisce nel db audit le righe modificate con i seguenti metadati:
   - `dba_tipo_comando` — `I` / `U` / `D`
   - `dba_tipo_dato` — `NEW` (dati dopo) o `OLD` (dati prima)
   - `dba_macchina` — `HOST_NAME()`
   - `dba_utente` — `SYSTEM_USER`
   - `dba_data` — `GETDATE()`
   - `dba_applicazione` — `APP_NAME()`
   - `dba_guid` — `NEWID()` (identifica l'operazione logica)
   - `dba_progupd` — progressivo riga nell'operazione

#### Attivazione / Disattivazione (menu Audit)

- **Attiva trigger:** `ENABLE TRIGGER tr_AUDIT_*` — riprende il monitoraggio.
- **Disattiva trigger:** `DISABLE TRIGGER tr_AUDIT_*` — sospende il monitoraggio senza perdere dati già registrati.

#### Disinstallazione (menu Audit → Elimina sistema di Audit)

- Dropa tutti i trigger `tr_AUDIT_*` dal database sorgente.
- Elimina il database `{db}_UPD`.

#### Generazione script di rilascio (menu Audit → Genera Script Audit)

Analizza i dati nel db audit e produce script DML pronti all'uso:

1. Legge tutte le tabelle presenti in `{db}_UPD`.
2. Per ogni tabella raggruppa le righe per `dba_guid` (un GUID = un'operazione atomica).
3. In base a `dba_tipo_comando`:
   - **I (INSERT):** genera `IF NOT EXISTS ... INSERT INTO` con i valori registrati in NEW.
   - **U (UPDATE):** confronta i valori OLD vs NEW, identifica solo i campi effettivamente modificati e genera `IF NOT EXISTS ... UPDATE` condizionato.
   - **D (DELETE):** genera `IF EXISTS ... DELETE` basato sui valori chiave OLD.
4. Lo script completo viene visualizzato nel tab "Testo" pronto per copia o esecuzione.

---

### Persistenza impostazioni

`SettingsService` salva e carica un file JSON in `%APPDATA%/PBScriptNew/settings.json` con:

| Chiave | Descrizione |
|--------|-------------|
| `LastServer` | Ultimo server usato |
| `LastDatabase` | Ultimo database selezionato |
| `LastUser` | Ultimo utente SQL |
| `AuditFilter` | Filtro tabelle audit corrente |
| `AuditExclude` | Lista esclusioni audit corrente |
| `TableSearch` | Ultimo testo di ricerca tabelle |
| `DefaultConditionalUpdate` | Preferenza UPDATE condizionato |

---

## Architettura

```
Forms/    → UI only. Chiama servizi, non accede mai al DB direttamente.
Services/ → Tutta la logica di business e accesso dati.
Models/   → DTO puri (solo proprietà, nessuna logica).
Config/   → GlobalConfig.cs: singleton che carica appsettings.json + env vars.
```

Tutti i servizi restituiscono `SqlResult<T>`: le eccezioni non vengono mai propagate verso la UI. Il `MainForm` usa un guard flag `_busy` e il metodo `GuardAsync` per prevenire chiamate rientranti su operazioni async.

---

## Contatti e contributi

- Apri una issue per bug o richieste di funzionalità.
- Invia una pull request con descrizione chiara delle modifiche.
