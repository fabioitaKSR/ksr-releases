Are still kerbal — mods-v1.0.18

- SCANsat 21.1.0-ksr.1: cache persistente delle mappe altimetriche, riutilizzata dopo il riavvio del gioco.
- Lettura, verifica SHA256 e scrittura delle mappe su thread di lavoro; chiamate Unity/PQS sul thread principale.
- Cache invalidata quando cambiano gli input del terreno; fallback alla generazione normale per file non validi.
- Verifica live ripetuta nella beta: 11 mappe da disco, zero rigenerazioni/rifiuti, zero scritture pendenti.
- Fase mappe SCANsat: 234,779 s con generazione contro 15,898 s con cache, controllo dei file incluso. Non è una misura degli FPS o dell'avvio completo.
- Pacchetto stabile disponibile al launcher come overlay: preserva impostazioni, dati e altri file SCANsat. DLL identica byte per byte alla beta verificata; SHA256 4d4ddeed1a42e69e32d9acbca28f2530e27d7eeac25a7add5120631760c0c999.

Richiede SCANsat 21.1 già installato. Primo caricamento o modifiche agli input possono richiedere la rigenerazione. Sorgenti completi e licenze inclusi. Ripristino tramite backup del launcher.

