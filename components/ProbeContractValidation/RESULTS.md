# Contratti lander e rover: integrita e trasmissione

Implementato l'11 novembre 2026. Non pubblicato su GitHub.

## Modifica

- Aggiornati 26 contratti automatici: 13 contratti di prima corsa e 13 contratti avanzati.
- Aggiunto `VesselNotDestroyed` a 39 gruppi lander/rover.
- Aggiunto `CollectScience` a 39 gruppi con `location = Surface` e `recoveryMethod = Transmit`.
- Essendo figlio del medesimo `VesselParameterGroup`, `CollectScience` convalida il vessel associato al gruppo: non basta una trasmissione eseguita da un altro mezzo.
- I contratti rover con equipaggio IotaU1 e CetiU1 non sono stati modificati.

## Verifiche

- Audit statico: 26 file, 39 controlli di integrita e 39 trasmissioni legate al corpo previsto.
- Hash identici tra staging, sorgente mantenuta e installazione di gioco per tutti i 26 file.
- Avvio KSP e caricamento di un salvataggio completati.
- Contract Configurator 2.12.2 ha rigenerato tutti i 26 log interessati.
- Nei log risultano 39 `ProbeIntegrity`, 39 `ProbeTransmission` e zero warning/errori per i contratti interessati.
- KSP e stato chiuso senza salvare il salvataggio usato per il controllo.

## Backup

- `source_work/ProbeIntegrityContracts/backup/20261111-193121`

## Limiti

- La verifica dimostra il caricamento e il legame configurato dei parametri, non una missione completa in gameplay.
- La modifica vale per contratti nuovi generati dopo il riavvio. I contratti gia accettati non vengono migrati.
