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
  --manifest release-manifest.json `
  --ksp "F:\SteamLibrary\steamapps\common\Kerbal Space Race" `
  --launcher-data "C:\KSR"

# Aggiornamento reale
& dotnet run --project launcher/src/KsrLauncher.Cli -- update `
  --manifest release-manifest.json `
  --assets-base "https://github.com/fabioitaKSR/ksr-releases/releases/download/v1.0.0" `
  --ksp "F:\SteamLibrary\steamapps\common\Kerbal Space Race" `
  --launcher-data "C:\KSR" `
  --apply
```

Il prossimo livello aggiungera un client GitHub Releases che individua automaticamente la release stabile e scarica `ksr-release.json`.
