# StashBay

> Eine leichtgewichtige Ablage zum Hin- und Herschieben von Dateien zwischen Fenstern: reinwerfen, rausziehen, fertig — ohne Fenster nebeneinander anordnen zu müssen.

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](../../LICENSE) [![365 Open Source Plan #048](https://img.shields.io/badge/365%20Open%20Source%20Plan-%23048-1f6feb)](https://github.com/rockbenben/365opensource)

[⬇ Neueste Version herunterladen](https://github.com/rockbenben/stashbay/releases/latest)

[English](../../README.md) · [简体中文](../../README.zh.md) · [繁體中文](README.zh-Hant.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Español](README.es.md) · [Français](README.fr.md) · **Deutsch** · [Português](README.pt.md) · [Русский](README.ru.md) · [Italiano](README.it.md) · [العربية](README.ar.md) · [हिन्दी](README.hi.md) · [বাংলা](README.bn.md) · [ไทย](README.th.md) · [Türkçe](README.tr.md) · [Tiếng Việt](README.vi.md) · [Bahasa Indonesia](README.id.md)

![StashBay kompakte Ablage: eine kleine, immer im Vordergrund sichtbare Kartenablage — ablegen und weiterverwenden](../../assets/shelf.png)

Eine leichtgewichtige lokale Datei-Transitstation für Windows. Läuft vollständig offline ohne Uploads, Netzwerkzugriffe oder Benutzerkonten. Funktioniert sowohl als flüchtige Ablage für Zwischendurch als auch als vollwertige Datei-Workbench mit Git-Erkennung, Snapshot-Backups und One-Click-Rollback.

---

## Unterstützte Spezifikationen

| Bereich | Spezifikationen |
| :--- | :--- |
| **Betriebssystem** | Windows 10 (1809+) / Windows 11 (native x64- und ARM64-Unterstützung) |
| **Laufzeitumgebung** | Offizielle Installer- und Portable-Versionen enthalten die Laufzeitumgebung; Slim-Builds erfordern die .NET 10 Desktop-Runtime |
| **Dateitypen** | Beliebige Dateien und mehrstufige Ordner (vollständige relative Verzeichnisstruktur bleibt erhalten) |
| **Eingabequellen** | Datei-Explorer-Auswahl, Zwischenablage, Maus-Schüttel-Geste |
| **Zielorte** | Lokale Laufwerke, Netzwerkfreigaben (UNC / Netzlaufwerke), Git-Repositories |
| **Große Dateien** | Unterstützt Einzeldateien > 2 GB sowie lange Windows-Pfade (> 260 Zeichen) |
| **Berechtigungen** | Läuft standardmäßig mit normalen Benutzerrechten (Installation in `%LOCALAPPDATA%\Programs`); optionaler Administrator-Autostart |
| **Speicherort** | Standardmäßig in `%LOCALAPPDATA%\StashBay\` (abwärtskompatibel zum alten `FileStash`-Verzeichnis) |

---

## Schnellstart

1. **Programm herunterladen**: Laden Sie den Installer `StashBay-vx.y.z-setup.exe` (keine Admin-Rechte erforderlich) oder das portable ZIP `StashBay-vx.y.z-win-x64.zip` von [Releases](https://github.com/rockbenben/stashbay/releases/latest) herunter.
2. **Ablage aufrufen**: Drücken Sie <kbd>Win+Alt+S</kbd>. Die Mini-Ablage öffnet sich direkt am Mauszeiger auf dem aktiven Bildschirm.
3. **Dateien ablegen**: Wählen Sie Dateien im Datei-Explorer aus und drücken Sie erneut <kbd>Win+Alt+S</kbd> (oder ziehen Sie sie hinein bzw. schütteln Sie die Maus) — die Dateien landen sofort in der Ablage.
4. **Mit einem Griff herausziehen**: Ziehen Sie den Griff `[⤹ Gesamt X Elemente]` am unteren Rand, um alle Dateien in einem Zug in ein Zielverzeichnis oder eine Anwendung zu exportieren. Wenn alle Dateien entnommen sind, blendet sich die Ablage automatisch aus.

---

## Hauptmerkmale

### Minimalistische Transit-Ablage (DropPoint-Erlebnis)
* **One-Shot-Export aller Dateien**: Ziehen Sie am Zähler-Griff am unteren Rand, um die gesamte Ablage mit einer einzigen Geste in den Explorer, Chat-Programme oder Browser-Formulare zu übertragen.
* **Automatisches Schließen bei Leerstand**: Erfolgreich abgelegte Dateien werden sofort aus der Ablage entfernt; ist die Ablage leer, schließt sich das Fenster sanft.
* **Öffnen am Mauszeiger**: Der globale Hotkey öffnet die Ablage immer auf dem Monitor, auf dem sich die Maus gerade befindet.
* **Schütteln zum Ablegen**: Schütteln Sie den Mauszeiger beim Ziehen von Dateien kurz hin und her, um die Ablage aufzurufen und die Auswahl direkt zu sichern.
* **Magnetischer schwebender Ball**: Ein kompaktes Symbol am Bildschirmrand, das sich per Klick oder kurzem Ziehen öffnet und beim Loslassen am Bildschirmrand einrastet.
* **Unabhängige Mehrfach-Ablagen**: Doppelklick auf das Tray-Icon oder Drücken von <kbd>Ctrl+Shift+N</kbd> erstellt eine neue temporäre Ablage; leere temporäre Ablagen werden automatisch aufgeräumt.

### Git-Zweig-Änderungen sichern und sicher zurückschreiben
* **Repository-Erkennung**: Erkennt Git-Wurzelverzeichnisse automatisch; beim Zweigwechsel warnt StashBay und ermöglicht die Wiederherstellung mit einem Klick.
* **Sperrprüfung vor dem Schreiben**: Verwendet den Windows Restart Manager, um blockierte Dateien (z. B. in Word, Excel oder IDEs) vor dem Schreiben zu erkennen und unvollständige Überschreibungen zu verhindern.
* **Sicherheits-Snapshots**: Vor dem Überschreiben wird automatisch ein Snapshot im lokalen, inhaltsadressierten Blob-Store angelegt — jederzeit mit einem Klick rückgängig machbar.
* **Hash-Vergleich**: Überspringt das Schreiben, wenn der Inhalt identisch ist, bewahrt den ursprünglichen Zeitstempel und verhindert unnötige Builds.

### Vollständige Workbench
* **Vier Ansichtsmodi**: Baumstruktur (Hierarchie), Liste (flach), Nach Quellordner gruppiert und Große Symbole; der ausgewählte Modus wird dauerhaft gespeichert.
* **Flexible Wiederherstellung**: Zurück an den Ursprungsort, in ein Zielverzeichnis oder auf ein neues Basisverzeichnis; mit vollständigem Pfad, nur Dateinamen oder flach.
* **Konflikt-Vorschau**: Detaillierte Vorschau aller Schreibaktionen vor der Ausführung, mit Konfliktstrategien wie Nachfragen, Überschreiben, Überspringen oder Umbenennen.
* **Verlauf & Protokoll**: Detaillierte Protokolle für jede Wiederherstellung; bei Teilfehlern können fehlgeschlagene Einträge gezielt wiederholt werden.

### 18 native Sprachen & Themes
* Native Unterstützung für 18 Sprachen mit 100%iger Schlüsselübereinstimmung, inklusive vollständiger RTL-Spiegelung für Arabisch (ar-SA).
* Perfekt abgestimmt auf die dunklen und hellen Themes von Windows mit neutralen Graphit-Tönen.

---

## Häufige Tastenkürzel

| Tastenkürzel | Bereich | Beschreibung |
| :--- | :--- | :--- |
| <kbd>Win+Alt+S</kbd> | Global | Mini-Ablage aufrufen; legt im Explorer ausgewählte Dateien direkt ab |
| <kbd>Win+Alt+W</kbd> | Global | Vollständige Workbench aufrufen |
| <kbd>Win+Alt+V</kbd> | Global | Dateien aus der Zwischenablage direkt in die Ablage einfügen |
| <kbd>Ctrl+V</kbd> | Im Fenster | Dateien aus der Zwischenablage in die aktive Ablage einfügen |
| <kbd>Ctrl+Shift+N</kbd> | Im Fenster | Neue unabhängige leere Ablage erstellen |
| <kbd>F11</kbd> / <kbd>Ctrl+E</kbd> | Im Fenster | Zwischen Mini-Ablage und voller Workbench umschalten |
| <kbd>Esc</kbd> | Im Fenster | In der Workbench zur Ablage einklappen; in der Ablage ins Tray minimieren |
| <kbd>Delete</kbd> | Liste | Ausgewählte Einträge aus der Ablage entfernen |

> [!TIP]
> Globale Tastenkürzel können in den Einstellungen angepasst oder deaktiviert werden. Bei Konflikten mit anderen Programmen warnt StashBay ausdrücklich.

---

## Befehlszeilenparameter (CLI)

`StashBay.exe` bietet Befehlszeilenoptionen zur Integration in Skripte, Launcher oder Workflows:

| Parameter | Beschreibung |
| :--- | :--- |
| `--add <Pfade...>` | Eine oder mehrere Dateien/Ordner zur Ablage hinzufügen |
| `--group <Name>` | In Kombination mit `--add`, um eine Zielgruppe anzugeben (wird bei Bedarf erstellt) |
| `--apply-here <Ordner>` | Inhalt der Ablage in das angegebene Verzeichnis schreiben |
| `--apply-original` | Am ursprünglichen relativen Pfad wiederherstellen (ideal für Git) |
| `--show` | Hauptfenster in den Vordergrund bringen und anzeigen |
| `--minimized` | Minimiert im System-Tray starten (für Windows-Autostart) |
| `--quit` | Laufende StashBay-Instanz sicher beenden |
| `--unregister-shell` | Kontextmenüs, Senden-an-Verknüpfung und Autostart-Einträge entfernen |

---

## Bekannte Einschränkungen

* **Windows-exklusiv**: Tief in die Windows-Shell, DWM-Komposition und Rechteverwaltung integriert; nur unter Windows lauffähig.
* **Snapshot-Kopien statt Verknüpfungen**: Dateien werden beim Ablegen als Inhaltssnapshot kopiert; spätere Änderungen an Originaldateien verändern die Ablage nicht.
* **Einzelinstanz-Verhalten**: Ein erneuter Start leitet Parameter an die bereits laufende Tray-Instanz weiter, ohne zusätzliche Prozesse zu erzeugen.
* **Hinweis zum Administrator-Autostart**: Läuft standardmäßig mit normalen Benutzerrechten. Für Drag-and-Drop in erhöhte Admin-Fenster kann in den Einstellungen der Administrator-Autostart aktiviert und per UAC bestätigt werden. [Autostart-Leitfaden](../admin-startup.md).

---

## Über den 365 Open Source Plan

Projekt **#048** des [365 Open Source Plan](https://github.com/rockbenben/365opensource) — eine Person + KI, über 300 Open-Source-Projekte in einem Jahr.

[Reiche deine Idee ein →](https://365.aishort.top/) · [Discord](https://discord.gg/PZTQfJ4GjX) · [Telegram](https://t.me/aishort_top)
