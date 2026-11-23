# StashBay

> Una mensola leggera per spostare file tra finestre: trascina dentro, trascina fuori e fatto — senza dover affiancare le finestre sullo schermo.

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](../../LICENSE) [![365 Open Source Plan #048](https://img.shields.io/badge/365%20Open%20Source%20Plan-%23048-1f6feb)](https://github.com/rockbenben/365opensource)

[⬇ Scarica l'ultima versione](https://github.com/rockbenben/stashbay/releases/latest)

[English](../../README.md) · [简体中文](../../README.zh.md) · [繁體中文](README.zh-Hant.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Español](README.es.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [Português](README.pt.md) · [Русский](README.ru.md) · **Italiano** · [العربية](README.ar.md) · [हिन्दी](README.hi.md) · [বাংলা](README.bn.md) · [ไทย](README.th.md) · [Türkçe](README.tr.md) · [Tiếng Việt](README.vi.md) · [Bahasa Indonesia](README.id.md)

![Mensola compatta StashBay: una scheda sempre in primo piano per parcheggiare e trasferire file](../../assets/shelf.png)

Una stazione di transito file locale e leggera per il desktop di Windows. Funziona interamente offline, senza caricamenti nel cloud, senza richieste di rete e senza account. È sia una mensola minimale da usare e chiudere subito, sia un banco di lavoro completo con supporto a Git, backup tramite snapshot e ripristino con un clic.

---

## Specifiche supportate

| Ambito | Specifica |
| :--- | :--- |
| **Sistema operativo** | Windows 10 (1809+) / Windows 11 (supporto nativo x64 e ARM64) |
| **Dipendenze** | Le versioni con installer e portatile includono il runtime; le versioni snelle richiedono .NET 10 Desktop Runtime |
| **Elementi supportati** | File di qualsiasi formato e cartelle multilivello (mantiene l'intera gerarchia relativa) |
| **Sorgenti di input** | Selezione in Esplora file, elenco file negli appunti, gesto di agitazione del mouse |
| **Destinazioni di scrittura** | Dischi locali, condivisioni di rete (UNC / unità mappate), repository Git |
| **File di grandi dimensioni** | Supporta singoli file superiori a 2 GB e percorsi lunghi di Windows (> 260 caratteri) |
| **Permessi** | Esecuzione con privilegi utente standard (installato in `%LOCALAPPDATA%\Programs`); avvio come amministratore opzionale |
| **Posizione dati** | Salvato in `%LOCALAPPDATA%\StashBay\` (pienamente compatibile con la vecchia cartella `FileStash`) |

---

## Guida rapida

1. **Scaricare il programma**: Scarica l'installer `StashBay-vx.y.z-setup.exe` (senza privilegi di amministratore) o la versione portatile `StashBay-vx.y.z-win-x64.zip` da [Releases](https://github.com/rockbenben/stashbay/releases/latest).
2. **Aprire la mensola**: Premi la combinazione di tasti <kbd>Win+Alt+S</kbd>; la mini-mensola si aprirà immediatamente vicino al puntatore del mouse sullo schermo attivo.
3. **Parcheggiare i file**: Seleziona i file in Esplora file e premi di nuovo <kbd>Win+Alt+S</kbd> (oppure trascinali nella mensola, o agita il mouse): i file verranno raccolti all'istante.
4. **Trascina tutto in un colpo solo**: Trascina la maniglia `[⤹ Totale X elementi]` nella parte inferiore per esportare tutti i file con un unico gesto nella finestra di destinazione o nell'applicazione desiderata. Una volta svuotata, la mensola si chiude automaticamente in dissolvenza.

---

## Caratteristiche principali

### Mensola di transito minimale (esperienza DropPoint)
* **Esportazione in blocco con un gesto**: Trascina la maniglia con il contatore per trasferire l'intero contenuto della mensola in Esplora file, chat o moduli del browser.
* **Chiusura automatica quando vuota**: Non appena i file vengono rilasciati a destinazione, lasciano la mensola; una volta vuota, la finestra svanisce dolcemente.
* **Apertura sulla posizione del cursore**: La scorciatoia globale individua il monitor su cui si trova il mouse e apre la mensola esattamente in quella posizione.
* **Agita il mouse per archiviare**: Mentre trascini file in Esplora file, agita brevemente il cursore per aprire la mensola e salvare direttamente la selezione.
* **Sfera fluttuante magnetica**: Un piccolo pulsante al bordo dello schermo che si apre con un clic o un breve trascinamento e si aggancia magneticamente al bordo.
* **Mense multiple indipendenti**: Fai doppio clic sull'icona nella barra delle applicazioni o premi <kbd>Ctrl+Shift+N</kbd> per aprire mensole temporanee separate; quelle vuote vengono rimosse automaticamente.

### Salvataggio delle modifiche ai rami Git e ripristino sicuro
* **Riconoscimento automatico del repository**: Riconosce la radice del repository Git per i file salvati; cambiando ramo, una notifica permette di ripristinarli con un clic nella posizione originale.
* **Controllo dei blocchi prima della scrittura**: Si interfaccia con il Gestore riavvio di Windows per rilevare file aperti da Office o IDE prima di scrivere, evitando file corrotti.
* **Snapshot di sicurezza prima della sovrascrittura**: Salva preventivamente i file sovrascritti in un archivio Blob locale, con annullamento immediato in un clic.
* **Confronto tramite hash**: Salta la scrittura se i file sono byte per byte identici, preservando la data di modifica originale ed evitando build inutili.

### Banco di lavoro completo
* **Quattro modalità di visualizzazione**: Albero (gerarchico), elenco piatto, raggruppato per cartella d'origine e tessere con icone grandi, con memoria delle preferenze.
* **Opzioni di ripristino flessibili**: Posizione originale, directory specifica o nuovo percorso radice; struttura con percorso relativo completo, solo nome o appiattita.
* **Anteprima dei conflitti**: Controlla in dettaglio le modifiche prima di applicarle e scegli tra Chiedi, Sovrascrivi, Salta e Rinomina automaticamente.
* **Cronologia delle operazioni**: Registro dettagliato di ogni sessione di ripristino; in caso di errore parziale, puoi riprovare solo gli elementi non riusciti.

### 18 lingue native e temi
* Supporto nativo per 18 lingue (100% di corrispondenza delle chiavi verificato da test) e supporto completo al layout specchiato RTL per l'arabo (ar-SA).
* Grafica perfettamente integrata con i temi scuro e chiaro di Windows grazie a una palette neutra in grafite.

---

## Scorciatoie da tastiera frequenti

| Scorciatoia | Ambito | Descrizione |
| :--- | :--- | :--- |
| <kbd>Win+Alt+S</kbd> | Globale | Mostra la mini-mensola; se ci sono file selezionati in Esplora file, li parcheggia subito |
| <kbd>Win+Alt+W</kbd> | Globale | Mostra il banco di lavoro completo |
| <kbd>Win+Alt+V</kbd> | Globale | Aggiunge alla mensola i file presenti negli appunti |
| <kbd>Ctrl+V</kbd> | Nella finestra | Incolla i file degli appunti nella mensola attiva |
| <kbd>Ctrl+Shift+N</kbd> | Nella finestra | Crea una nuova mensola temporanea vuota e indipendente |
| <kbd>F11</kbd> / <kbd>Ctrl+E</kbd> | Nella finestra | Passa dalla mini-mensola al banco di lavoro e viceversa |
| <kbd>Esc</kbd> | Nella finestra | Nel banco di lavoro, ripiega nella mensola; nella mensola, riduce nell'area di notifica |
| <kbd>Delete</kbd> | Elenco | Rimuove gli elementi selezionati dalla mensola |

> [!TIP]
> Tutte le scorciatoie globali possono essere personalizzate o disattivate nelle impostazioni. In caso di conflitto con un altro programma, StashBay mostra un avviso esplicito.

---

## Parametri della riga di comando (CLI)

`StashBay.exe` offre un set completo di argomenti della riga di comando per l'integrazione con lanciatori esterni o script:

| Parametro | Descrizione |
| :--- | :--- |
| `--add <percorsi...>` | Aggiunge uno o più file o cartelle alla mensola |
| `--group <nome>` | Usato con `--add` per specificare la mensola di destinazione (creata se non esiste) |
| `--apply-here <cartella>` | Scrive i file della mensola nella cartella specificata |
| `--apply-original` | Ripristina i file nel percorso relativo originale (ideale per Git) |
| `--show` | Porta in primo piano e mostra la finestra principale |
| `--minimized` | Avvia minimizzato nell'area di notifica (per l'avvio automatico di Windows) |
| `--quit` | Chiude in modo sicuro l'istanza in esecuzione |
| `--unregister-shell` | Rimuove il menu contestuale, il collegamento Invia a e la voce di avvio automatico |

---

## Limitazioni note

* **Esclusivo per Windows**: Integrato in profondità con la Shell di Windows, la composizione DWM e il controllo accessi; nessuna versione multipiattaforma pianificata.
* **Snapshot al posto di collegamenti**: I file vengono copiati come snapshot di contenuto al momento del parcheggio; le modifiche successive ai file originali non influiscono sulla mensola.
* **Istanza singola**: Riavviare il programma inoltra automaticamente gli argomenti al processo già attivo nella barra delle applicazioni, evitando processi orfani.
* **Avvio come amministratore**: Di base viene eseguito con privilegi standard. Se devi trascinare file in finestre con privilegi elevati, abilita l'avvio come amministratore nelle impostazioni e approva il controllo UAC. [Guida alla configurazione dell'avvio](../admin-startup.md).

---

## Informazioni sul 365 Open Source Plan

Progetto **#048** del [365 Open Source Plan](https://github.com/rockbenben/365opensource) — una persona + l'IA, oltre 300 progetti open source in un anno.

[Proponi la tua idea →](https://365.aishort.top/) · [Discord](https://discord.gg/PZTQfJ4GjX) · [Telegram](https://t.me/aishort_top)
