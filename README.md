# PBScriptNew

Una applicazione desktop (Windows) per la creazione, gestione e generazione di script SQL e attività di audit.

## Panoramica

Questo repository contiene il sorgente di `PBScriptNew` (progetto C# .NET 8, Windows Forms). L'applicazione fornisce strumenti per esplorare database, generare script SQL e creare script per attività di audit.

## Caratteristiche principali

- Interfaccia grafica Windows Forms per la gestione delle operazioni.
- Esplorazione delle strutture di database e selezione di colonne.
- Generazione automatica di script SQL e script di audit.
- Servizi modulari per accesso DB, generazione script e impostazioni.

## Requisiti

- Windows 10/11
- .NET 8 SDK
- Visual Studio 2022/2023 o `dotnet` CLI per build ed esecuzione

## Build & Esecuzione

1. Ripristina i pacchetti e builda il progetto:

```bash
dotnet restore
dotnet build -c Debug
```

2. Avvia l'applicazione dalla CLI (root del repository):

```bash
dotnet run --project PBScriptNew-CS.csproj
```

3. Oppure apri il progetto in Visual Studio e avvialo in modalità Debug/Release.

Il binario compilato si trova in `bin/Debug/net8.0-windows/` o `bin/Release/net8.0-windows/`.

## Configurazione

- Il file `appsettings.json` nella root (e nelle cartelle bin) contiene impostazioni runtime. Verifica e aggiorna le impostazioni di connessione al database prima dell'esecuzione.

## Struttura del progetto (rilevante)

- `Program.cs` - entrypoint dell'applicazione.
- `Forms/` - moduli Windows Forms (MainForm, LoginForm, dialog ecc.).
- `Services/` - servizi applicativi (DatabaseExplorerService, ScriptGeneratorService, SqlService, ecc.).
- `Models/` - modelli e DTO usati dall'app.
- `Config/GlobalConfig.cs` - configurazioni globali.

## Note di sviluppo

- L'app utilizza architettura a servizi interni per separare UI e logica di business.
- Quando modifichi la logica di accesso al DB o la generazione script, aggiungi test e verifica manualmente con un database di sviluppo.

## Contribuire

- Apri una issue per discutere modifiche o bug.
- Invia pull request con descrizione chiara delle modifiche.

## Licenza

Licenza non specificata nel repository. Contatta gli autori/proprietari del codice per dettagli sulla licenza.

## Contatti

Per domande o richieste, apri una issue o contatta l'autore responsabile del repository.
