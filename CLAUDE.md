# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

`PBScriptNew` è un'applicazione desktop **Windows Forms** (.NET 8, C# 12) per esplorare database SQL Server, generare script SQL (INSERT/UPDATE/DELETE) e produrre script di audit tramite trigger.

## Commands

```bash
dotnet restore
dotnet build -c Debug
dotnet build -c Release
dotnet run --project PBScriptNewCS.csproj
dotnet test
```

## Architecture

Architettura a livelli stretti — le responsabilità **non si attraversano**:

```
Forms/    → UI only. Chiama servizi, non accede mai al DB direttamente.
Services/ → Tutta la logica di business e accesso dati.
Models/   → DTO puri (solo proprietà, nessuna logica).
Config/   → GlobalConfig.cs: singleton che carica appsettings.json + env vars.
```

**Flusso avvio**: `Program.cs` → `LoginForm` → `MainForm(SqlService, DatabaseConfig)`

`MainForm` riceve `SqlService` e `DatabaseConfig` via costruttore. `DatabaseExplorerService` riceve `SqlService` come dipendenza. Non esiste un container DI: il wiring è manuale in `Program.cs`.

### Servizi chiave

| Servizio | Responsabilità |
|---|---|
| `SqlService` | Connessione, query, batch GO, formattazione valori SQL |
| `DatabaseExplorerService` | Metadati DB: tabelle, colonne, chiavi, indici |
| `ScriptGeneratorService` | Generazione DDL, INSERT/UPDATE/DELETE |
| `AuditScriptBuilder` | Script trigger DML per il sistema di audit |
| `SettingsService` | Persistenza impostazioni utente in `%APPDATA%/PBScriptNew/` |

### Pattern risultato

Tutti i servizi restituiscono `SqlResult<T>` (mai eccezioni verso la UI):
```csharp
SqlResult<List<TableInfo>>.Ok(data)
SqlResult<List<TableInfo>>.Fail(ex.Message)
```

### Async in MainForm

`MainForm` usa un guard flag `_busy` e il metodo `GuardAsync(Func<Task>)` per prevenire chiamate rientranti. I metodi che aggiornano la UI da event handler del `ListBox` usano `BeginInvoke` per differire il ridisegno della `RichTextBox` al prossimo ciclo del message pump — evitare di rimuovere questo pattern.

### Configurazione

- `appsettings.json` → connessione DB e flag applicativi (`FormatoEsteso`, `ForzaFlgStd`, ecc.)
- Variabili d'ambiente fallback: `SQL_SERVER`, `SQL_USER`, `SQL_PASSWORD`, `SQL_DATABASE`, `INTEGRATED_SECURITY`
- **Non committare `appsettings.json`** se contiene credenziali reali.

## Coding Conventions

- **Non usare `var`**: dichiarare sempre il tipo esplicito.
- La graffa di apertura va sempre su riga nuova (Allman style).
- Lasciare una riga vuota dopo la chiusura di una graffa.
- Usare `#region NomeRegione` / `#endregion NomeRegione` per organizzare le sezioni.
- `async/await` per qualsiasi I/O; `StringBuilder` per concatenazioni ripetitive.
- Nessuna query SQL nei `Forms/`; nessuna magic string SQL fuori da `SqlService` o `ScriptGeneratorService`.

## Boundaries

**Mai fare:**
- Query SQL direttamente nei `Forms/`
- Credenziali o connection string hardcoded nel codice
- Modificare `.csproj` senza necessità esplicita

**Chiedere prima di:**
- Aggiungere dipendenze NuGet
- Cambiare il comportamento di `SqlService` o `ScriptGeneratorService`
- Introdurre nuovi pattern architetturali

## Pattern canonici

- Accesso DB → [Services/SqlService.cs](Services/SqlService.cs)
- Generazione script → [Services/ScriptGeneratorService.cs](Services/ScriptGeneratorService.cs)
- Impostazioni → [Services/SettingsService.cs](Services/SettingsService.cs)
- DTO → [Models/SqlResult.cs](Models/SqlResult.cs)
