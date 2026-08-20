# KSR Launcher Core

Prototipo verificabile del motore di aggiornamento per Kerbal Space Race. La CLI e prudente: `update` e `rollback` non modificano file senza l'opzione esplicita `--apply`.

## Funzioni disponibili

- validazione dello schema manifest 1 e della cartella KSP;
- piano per tutti i componenti dichiarati nel manifest;
- asset da cartella locale oppure URL HTTP/HTTPS;
- verifica SHA-256 ed estrazione ZIP sicura;
- aggiornamento per gruppi transazionali;
- conservazione dei file indicati da `preserve`;
- eccezioni `managedFilesInsidePreservedPaths`;
- backup persistente, rollback e stato installato atomico.
- individuazione automatica della release stabile o beta tramite GitHub Releases.
- aggiornamento ordinario limitato ai componenti KSR gia presenti;
- installazione/riparazione dei componenti assenti soltanto con consenso esplicito;
- nessuna scansione, sostituzione o rimozione delle mod di terzi.

## Compilazione e test

Dal root del progetto:

```powershell
$env:DOTNET_CLI_HOME = "$PWD/.tools/dotnet-home"
& ./.tools/dotnet/dotnet.exe build ./github/ksr-releases-local/launcher/KsrLauncher.sln
& ./.tools/dotnet/dotnet.exe run --project ./github/ksr-releases-local/launcher/tests/KsrLauncher.Tests
```

## Esempio CLI

```powershell
# Solo simulazione
& dotnet run --project launcher/src/KsrLauncher.Cli -- plan `
  --repo "fabioitaKSR/ksr-releases" `
  --channel stable `
  --ksp "F:\SteamLibrary\steamapps\common\Kerbal Space Race" `
  --launcher-data "C:\KSR"

# Aggiornamento reale
& dotnet run --project launcher/src/KsrLauncher.Cli -- update `
  --repo "fabioitaKSR/ksr-releases" `
  --channel stable `
  --ksp "F:\SteamLibrary\steamapps\common\Kerbal Space Race" `
  --launcher-data "C:\KSR" `
  --apply
```

L'aggiornamento precedente ignora ogni componente KSR non installato. Per una prima installazione o una riparazione esplicitamente richiesta si aggiunge `--install-missing`. Questa opzione non estende mai il perimetro oltre i componenti elencati nel manifest ufficiale KSR.

Il repository deve pubblicare `ksr-release.json` insieme agli ZIP nella stessa GitHub Release. Finche non esiste una release pubblicata, la modalita GitHub segnala correttamente che non sono disponibili aggiornamenti.
