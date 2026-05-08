# Agent Instructions — PBScriptNewCS

## Panoramica del progetto

`PBScriptNew` è un'applicazione desktop **Windows Forms** scritta in **C# / .NET 8**.  
Fornisce strumenti per esplorare database SQL, generare script SQL e produrre script di audit.

---

## Stack tecnologico

| Ambito | Tecnologia |
|---|---|
| Linguaggio | C# 12 |
| Framework | .NET 8 (net8.0-windows) |
| UI | Windows Forms |
| Config | `appsettings.json` |
| Build | `dotnet` CLI / Visual Studio 2022+ |

---

## Struttura del repository

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

## Regole architetturali

1. **Separazione UI / logica**: la logica di business risiede esclusivamente nei `Services/`. I `Forms/` chiamano i servizi, non accedono mai direttamente al database.
2. **Modelli immutabili**: i record in `Models/` devono restare DTO puri (nessuna logica di business al loro interno).
3. **Configurazione centralizzata**: utilizzare `GlobalConfig.cs` per costanti applicative e `appsettings.json` per valori di configurazione runtime.
4. **Nessun magic string SQL nei Form**: le query SQL vanno costruite esclusivamente in `SqlService` o `ScriptGeneratorService`.

---

## Convenzioni di codice

- Seguire le convenzioni C# standard (PascalCase per tipi e metodi, camelCase per variabili locali).
- Usare `async/await` per qualsiasi operazione I/O (accesso DB, lettura file).
- Gestire sempre le eccezioni nei servizi e restituire oggetti risultato (`SqlResult`) invece di lanciare eccezioni non gestite verso la UI.
- Aggiungere commenti XML (`/// <summary>`) ai metodi pubblici dei servizi.

---

## Comandi utili

```bash
# Ripristino dipendenze
dotnet restore

# Build Debug
dotnet build -c Debug

# Build Release
dotnet build -c Release

# Avvio
dotnet run --project PBScriptNewCS.csproj
```

---

## Sicurezza

- Non inserire credenziali o connection string hardcoded nel codice sorgente.
- Le impostazioni di connessione al database devono risiedere in `appsettings.json` (escluso dal controllo versione se contiene dati sensibili).
- Sanificare sempre gli input utente prima di passarli alle query SQL per prevenire SQL injection.
- Eseguire una scansione Snyk dopo ogni modifica rilevante al codice o alle dipendenze.

---

## Indicazioni per l'agente AI

- Prima di modificare un servizio, leggere l'interfaccia pubblica completa del servizio stesso e i modelli che utilizza.
- Quando aggiungi un nuovo servizio, registrarlo in `Program.cs` e documentarlo in questo file.
- Quando modifichi la logica di generazione script, verificare manualmente il risultato con un database di sviluppo.
- Non modificare il progetto `.csproj` direttamente a meno che non sia strettamente necessario aggiungere una dipendenza.
- Preferire l'estensione dei servizi esistenti alla creazione di nuovi, salvo cambiamenti di responsabilità netti.
