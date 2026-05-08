# Agent Instructions — PBScriptNewCS

## Project Overview

`PBScriptNew` è un'applicazione desktop **Windows Forms** scritta in **C# / .NET 8**.  
Fornisce strumenti per esplorare database SQL, generare script SQL e produrre script di audit.

---

## Tech Stack

| Ambito | Tecnologia |
|---|---|
| Linguaggio | C# 12 |
| Framework | .NET 8 (net8.0-windows) |
| UI | Windows Forms |
| Config | `appsettings.json` |
| Build | `dotnet` CLI / Visual Studio 2022+ |

---

## Repository Structure

```
PBScriptNewCS/
├── Program.cs                  # Entrypoint
├── appsettings.json            # Configurazione runtime
├── Config/
│   └── GlobalConfig.cs         # Costanti e configurazioni globali — NON logica di business
├── Forms/                      # Solo UI: nessun accesso diretto al DB
│   ├── MainForm.cs             # Finestra principale
│   ├── LoginForm.cs            # Form di login
│   ├── AuditFilterDialog.cs    # Dialog filtro audit
│   └── ColumnSelectorDialog.cs # Dialog selezione colonne
├── Models/                     # DTO puri: nessuna logica, solo dati
│   ├── ApplicationOptions.cs
│   ├── AuditSettings.cs
│   ├── DatabaseConfig.cs
│   ├── DatabaseModels.cs
│   ├── SqlDataType.cs
│   └── SqlResult.cs
├── Services/                   # Tutta la logica di business e accesso dati
│   ├── DatabaseExplorerService.cs   # Esplorazione struttura DB
│   ├── ScriptGeneratorService.cs    # Generazione script SQL
│   ├── AuditScriptBuilder.cs        # Costruzione script di audit
│   ├── SqlService.cs                # Accesso diretto al DB
│   └── SettingsService.cs           # Lettura/scrittura impostazioni
└── Tests/                      # Test unitari (xUnit)
```

> **Cosa NON va dove:**
> - Nessuna query SQL nei `Forms/`
> - Nessuna logica di business nei `Models/`
> - Nessuna stringa di connessione hardcoded nel codice

---

## Architectural Rules

1. **Separazione UI / logica**: la logica di business risiede esclusivamente nei `Services/`. I `Forms/` chiamano i servizi, non accedono mai direttamente al database.
2. **Modelli immutabili**: i record in `Models/` devono restare DTO puri (nessuna logica di business al loro interno).
3. **Configurazione centralizzata**: utilizzare `GlobalConfig.cs` per costanti applicative e `appsettings.json` per valori di configurazione runtime.
4. **Nessun magic string SQL nei Form**: le query SQL vanno costruite esclusivamente in `SqlService` o `ScriptGeneratorService`.

---

## Coding Conventions

- Seguire le convenzioni C# standard (PascalCase per tipi e metodi, camelCase per variabili locali).
- Usare `async/await` per qualsiasi operazione I/O (accesso DB, lettura file).
- Gestire sempre le eccezioni nei servizi e restituire oggetti risultato (`SqlResult`) invece di lanciare eccezioni non gestite verso la UI.
- Aggiungere commenti XML (`/// <summary>`) ai metodi pubblici dei servizi.

### Variable Declarations

- **Non usare `var`**: dichiarare sempre il tipo esplicito.

### Formatting

- **Andare sempre a capo alla apertura di una graffa**
- **Lasciare una riga vuota dopo la chiusura di una graffa**

### Naming Conventions

- **Classi**: PascalCase (es. `ServiceLogger`, `KnosBaseService`)
- **Metodi**: PascalCase (es. `AppendLogKeyValue`, `CallWebService`)
- **Property pubbliche**: PascalCase (es. `LogLevel`, `MaxFileSize`)
- **Campi privati**: camelCase (es. `loggerLock`, `knosInstance`, `modulePath`)
- **Parametri**: camelCase (es. `logLevel`, `eventName`)
- **Costanti**: PascalCase o UPPER_CASE

### Best Practices

- **Evitare LINQ complesso**: preferire cicli `for` o `foreach` espliciti per migliorare la leggibilità.
- **Preferire codice esplicito e leggibile**: la chiarezza ha priorità sulla brevità.
- **Usare nomi di variabili e metodi descrittivi**: es. `LogFolderServicePath` invece di `Path`.
- **Commenti XML**: documentare classi e metodi pubblici con `<summary>` e `<remarks>`.
- **Gestione eccezioni**: usare parametri `string out message` per restituire errori senza sollevare eccezioni nei metodi critici (es. logging).
- **Lock per thread safety**: usare `lock` per accessi concorrenti a risorse condivise.
- **Esporre le variabili di classe come property**: invece che come campi pubblici.
- Non rimuovere i commenti nel codice ma aggiornarli se non sono più corretti.

### Regions

- Organizzare il codice in regioni. Ogni regione ha un nome diretto es. `#region LogFolderService`.
- Nella `#endregion` riportare sempre il nome della regione, es. `#endregion LogFolderService`.

### Resource Management

- Usare `using` per `StreamWriter`, `StreamReader`, `SqlConnection`.
- **Verificare sempre l'esistenza** di file e directory prima di operare.

### Database Access

- **Usare parametri SQL** per evitare SQL injection.
- **Gestire valori NULL dal database**.

### Performance Considerations

- Usare `StringBuilder` per concatenazione stringhe ripetitive.

### Canonical Patterns

Per i pattern canonici, fare riferimento ai file esistenti:

- Accesso al DB → vedere [`Services/SqlService.cs`](Services/SqlService.cs)
- Generazione script → vedere [`Services/ScriptGeneratorService.cs`](Services/ScriptGeneratorService.cs)
- Gestione impostazioni → vedere [`Services/SettingsService.cs`](Services/SettingsService.cs)
- Modello DTO → vedere [`Models/SqlResult.cs`](Models/SqlResult.cs)

---

## Commands

```bash
# Ripristino dipendenze
dotnet restore

# Build Debug
dotnet build -c Debug

# Build Release
dotnet build -c Release

# Test
dotnet test

# Avvio
dotnet run --project PBScriptNewCS.csproj
```

> Il file di soluzione è `PBScriptNewCS.sln`. Per build con MSBuild usare flag espliciti, es.:
> ```bash
> msbuild PBScriptNewCS.sln /p:Configuration=Release
> ```

---

## Testing

- Framework: **xUnit** (o il framework presente nel progetto).
- I test si trovano nella cartella `Tests/` (da creare se assente).
- Una build è considerata "passing" se: **0 test failing**, coverage minimo da definire.
- Aggiungere test per ogni nuovo metodo pubblico dei `Services/`.

---

## Git Workflow

- Branch principale: `main` (o `master`)
- Branch naming consigliato: `feature/<nome>`, `fix/<nome>`, `refactor/<nome>`
- Ogni commit deve essere atomico e descrivere chiaramente la modifica.
- Non committare `appsettings.json` con dati sensibili (connection string, credenziali).

---

## Boundaries

### NEVER (mai fare)

- Inserire migration DB, connection string o credenziali nel codice sorgente.
- Eseguire deploy script direttamente dall'applicazione senza conferma esplicita.
- Accedere al database direttamente dai `Forms/`.
- Modificare il `.csproj` senza necessità esplicita.

### ASK FIRST (chiedere prima)

- Aggiungere nuove dipendenze NuGet.
- Modificare la struttura del database o gli script di migrazione.
- Cambiare il comportamento di `SqlService` o `ScriptGeneratorService`.
- Introdurre nuovi pattern architetturali non descritti in questo file.

---

## Security

- Non inserire credenziali o connection string hardcoded nel codice sorgente.
- Le impostazioni di connessione al database devono risiedere in `appsettings.json` (escluso dal controllo versione se contiene dati sensibili).
- Sanificare sempre gli input utente prima di passarli alle query SQL per prevenire SQL injection.
- Eseguire una scansione Snyk dopo ogni modifica rilevante al codice o alle dipendenze.

---

## AI Agent Guidelines

- Prima di modificare un servizio, leggere l'interfaccia pubblica completa del servizio stesso e i modelli che utilizza.
- Quando aggiungi un nuovo servizio, registrarlo in `Program.cs` e documentarlo in questo file.
- Quando modifichi la logica di generazione script, verificare manualmente il risultato con un database di sviluppo.
- Non modificare il progetto `.csproj` direttamente a meno che non sia strettamente necessario aggiungere una dipendenza.
- Preferire l'estensione dei servizi esistenti alla creazione di nuovi, salvo cambiamenti di responsabilità netti.


---

## Tech Stack

| Ambito | Tecnologia |
|---|---|
| Linguaggio | C# 12 |
| Framework | .NET 8 (net8.0-windows) |
| UI | Windows Forms |
| Config | `appsettings.json` |
| Build | `dotnet` CLI / Visual Studio 2022+ |

---

## Repository Structure

```
PBScriptNewCS/
├── Program.cs                  # Entrypoint
├── appsettings.json            # Configurazione runtime
├── Config/
│   └── GlobalConfig.cs         # Costanti e configurazioni globali
├── Forms/
│   ├── MainForm.cs             # Finestra principale
│   ├── LoginForm.cs            # Form di login
│   ├── AuditFilterDialog.cs    # Dialog filtro audit
│   └── ColumnSelectorDialog.cs # Dialog selezione colonne
├── Models/
│   ├── ApplicationOptions.cs
│   ├── AuditSettings.cs
│   ├── DatabaseConfig.cs
│   ├── DatabaseModels.cs
│   ├── SqlDataType.cs
│   └── SqlResult.cs
└── Services/
    ├── DatabaseExplorerService.cs   # Esplorazione struttura DB
    ├── ScriptGeneratorService.cs    # Generazione script SQL
    ├── AuditScriptBuilder.cs        # Costruzione script di audit
    ├── SqlService.cs                # Accesso diretto al DB
    └── SettingsService.cs           # Lettura/scrittura impostazioni
```

---

## Architectural Rules

1. **Separazione UI / logica**: la logica di business risiede esclusivamente nei `Services/`. I `Forms/` chiamano i servizi, non accedono mai direttamente al database.
2. **Modelli immutabili**: i record in `Models/` devono restare DTO puri (nessuna logica di business al loro interno).
3. **Configurazione centralizzata**: utilizzare `GlobalConfig.cs` per costanti applicative e `appsettings.json` per valori di configurazione runtime.
4. **Nessun magic string SQL nei Form**: le query SQL vanno costruite esclusivamente in `SqlService` o `ScriptGeneratorService`.

---

## Coding Conventions

- Seguire le convenzioni C# standard (PascalCase per tipi e metodi, camelCase per variabili locali).
- Usare `async/await` per qualsiasi operazione I/O (accesso DB, lettura file).
- Gestire sempre le eccezioni nei servizi e restituire oggetti risultato (`SqlResult`) invece di lanciare eccezioni non gestite verso la UI.
- Aggiungere commenti XML (`/// <summary>`) ai metodi pubblici dei servizi.

### Variable Declarations

- **Non usare `var`**: dichiarare sempre il tipo esplicito.

### Formatting

- **Andare sempre a capo alla apertura di una graffa**
- **Lasciare una riga vuota dopo la chiusura di una graffa**

### Naming Conventions

- **Classi**: PascalCase (es. `ServiceLogger`, `KnosBaseService`)
- **Metodi**: PascalCase (es. `AppendLogKeyValue`, `CallWebService`)
- **Property pubbliche**: PascalCase (es. `LogLevel`, `MaxFileSize`)
- **Campi privati**: camelCase (es. `loggerLock`, `knosInstance`, `modulePath`)
- **Parametri**: camelCase (es. `logLevel`, `eventName`)
- **Costanti**: PascalCase o UPPER_CASE

### Best Practices

- **Evitare LINQ complesso**: preferire cicli `for` o `foreach` espliciti per migliorare la leggibilità.
- **Preferire codice esplicito e leggibile**: la chiarezza ha priorità sulla brevità.
- **Usare nomi di variabili e metodi descrittivi**: es. `LogFolderServicePath` invece di `Path`.
- **Commenti XML**: documentare classi e metodi pubblici con `<summary>` e `<remarks>`.
- **Gestione eccezioni**: usare parametri `string out message` per restituire errori senza sollevare eccezioni nei metodi critici (es. logging).
- **Lock per thread safety**: usare `lock` per accessi concorrenti a risorse condivise.
- **Esporre le variabili di classe come property**: invece che come campi pubblici.
- Non rimuovere i commenti nel codice ma aggiornarli se non sono più corretti.

### Regions

- Organizzare il codice in regioni. Ogni regione ha un nome diretto es. `#region LogFolderService`.
- Nella `#endregion` riportare sempre il nome della regione, es. `#endregion LogFolderService`.

### Resource Management

- Usare `using` per `StreamWriter`, `StreamReader`, `SqlConnection`.
- **Verificare sempre l'esistenza** di file e directory prima di operare.

### Database Access

- **Usare parametri SQL** per evitare SQL injection.
- **Gestire valori NULL dal database**.

### Performance Considerations

- Usare `StringBuilder` per concatenazione stringhe ripetitive.

---

## Commands

```bash
# Ripristino dipendenze
dotnet restore

# Build Debug
dotnet build -c Debug

# Build Release
dotnet build -c Release

# Test
dotnet test

# Avvio
dotnet run --project PBScriptNewCS.csproj
```

> Il file di soluzione è `PBScriptNewCS.sln`. Per build con MSBuild usare flag espliciti, es.:
> ```bash
> msbuild PBScriptNewCS.sln /p:Configuration=Release
> ```

---

## Testing

- Framework: **xUnit** (o il framework presente nel progetto).
- I test si trovano nella cartella `Tests/` (da creare se assente).
- Una build è considerata "passing" se: **0 test failing**, coverage minimo da definire.
- Aggiungere test per ogni nuovo metodo pubblico dei `Services/`.

---

## Structure

```
PBScriptNewCS/
├── Config/        # Costanti e configurazioni globali — NON logica di business
├── Forms/         # Solo UI: nessun accesso diretto al DB
├── Models/        # DTO puri: nessuna logica, solo dati
├── Services/      # Tutta la logica di business e accesso dati
└── Tests/         # Test unitari (xUnit)
```

> **Cosa NON va dove:**
> - Nessuna query SQL nei `Forms/`
> - Nessuna logica di business nei `Models/`
> - Nessuna stringa di connessione hardcoded nel codice

---

## Code Style

Per i pattern canonici, fare riferimento ai file esistenti:

- Accesso al DB → vedere [`Services/SqlService.cs`](Services/SqlService.cs)
- Generazione script → vedere [`Services/ScriptGeneratorService.cs`](Services/ScriptGeneratorService.cs)
- Gestione impostazioni → vedere [`Services/SettingsService.cs`](Services/SettingsService.cs)
- Modello DTO → vedere [`Models/SqlResult.cs`](Models/SqlResult.cs)

---

## Git Workflow

- Branch principale: `main` (o `master`)
- Branch naming consigliato: `feature/<nome>`, `fix/<nome>`, `refactor/<nome>`
- Ogni commit deve essere atomico e descrivere chiaramente la modifica.
- Non committare `appsettings.json` con dati sensibili (connection string, credenziali).

---

## Boundaries

### NEVER (mai fare)

- Inserire migration DB, connection string o credenziali nel codice sorgente.
- Eseguire deploy script direttamente dall'applicazione senza conferma esplicita.
- Accedere al database direttamente dai `Forms/`.
- Modificare il `.csproj` senza necessità esplicita.

### ASK FIRST (chiedere prima)

- Aggiungere nuove dipendenze NuGet.
- Modificare la struttura del database o gli script di migrazione.
- Cambiare il comportamento di `SqlService` o `ScriptGeneratorService`.
- Introdurre nuovi pattern architetturali non descritti in questo file.

---

## Security

- Non inserire credenziali o connection string hardcoded nel codice sorgente.
- Le impostazioni di connessione al database devono risiedere in `appsettings.json` (escluso dal controllo versione se contiene dati sensibili).
- Sanificare sempre gli input utente prima di passarli alle query SQL per prevenire SQL injection.
- Eseguire una scansione Snyk dopo ogni modifica rilevante al codice o alle dipendenze.

---

## AI Agent Guidelines

- Prima di modificare un servizio, leggere l'interfaccia pubblica completa del servizio stesso e i modelli che utilizza.
- Quando aggiungi un nuovo servizio, registrarlo in `Program.cs` e documentarlo in questo file.
- Quando modifichi la logica di generazione script, verificare manualmente il risultato con un database di sviluppo.
- Non modificare il progetto `.csproj` direttamente a meno che non sia strettamente necessario aggiungere una dipendenza.
- Preferire l'estensione dei servizi esistenti alla creazione di nuovi, salvo cambiamenti di responsabilità netti.
