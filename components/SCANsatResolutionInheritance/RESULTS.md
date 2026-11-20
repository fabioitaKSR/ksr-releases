# SCANsat: scansione alta include la bassa

Implementato il 19 novembre 2026. Non pubblicato su GitHub.

## Regola

- Altimetria alta (`2`) include altimetria bassa (`1`).
- Visuale alta (`64`) include visuale bassa (`4`).
- Risorse alta (`256`) include risorse bassa (`128`).
- Le altre capacita presenti nei bitmask combinati vengono conservate.

## Patch

- File: `KSR_SCANsat_HighIncludesLow.cfg`.
- Trasformazioni: `2 -> 3`, `10 -> 11`, `64 -> 68`, `80 -> 84`, `256 -> 384`.
- Gestiti sia `SCANsat` sia `ModuleSCANresourceScanner`.
- Installata in `D:/CODEX/Moddare_KSP_Project/KerbalSpaceRace/Patches` e nella copia di gioco.

## Verifiche

- Audit delle configurazioni sorgente: 17 definizioni interessate, incluse SCANsat, CactEye e DMagic.
- ModuleManager 4.2.3 ha caricato e applicato la patch senza errori relativi.
- KSP ha completato il caricamento fino al menu principale e SCANsat si e inizializzato.
- Cache finale: 16 moduli scanner effettivi.
- Scanner effettivi ad alta risoluzione: 2 altimetrici, 3 visuali e 4 risorse.
- Violazioni alta-senza-bassa nella cache finale: 0.
- Nessun valore precedente `2`, `10`, `64`, `80` o `256` rimasto nei moduli finali interessati.

## Limite

- Non e ancora stata eseguita una prova in missione osservando l'aumento simultaneo delle due coperture SCANsat.
