**Sprache:** Deutsch · [English](README.md)

# DLSS-Manager-Sprachdateien


Dieser Ordner enthält die **Übersetzungen der Programmoberfläche**, unabhängig von den README-Sprachen. Er liegt im Ordner resources neben den weiteren Programmressourcen.

Standardmäßig verwendet der Manager die Windows-Anzeigesprache. Ist sie nicht vorhanden, wird eine passende regionale Sprachfamilie oder andernfalls Englisch geladen. Unter **Einstellungen → Allgemein → Sprache** kann eine feste Sprache gewählt werden. Die Auswahl wird gespeichert; zum Anwenden wird der Manager neu gestartet. Laufende Updates und Scans müssen vorher abgeschlossen sein.

Für weitere Sprachen `en-en.ini` kopieren, Dateiname sowie `[meta]` anpassen und ausschließlich die Werte unter `[strings]` übersetzen. Schlüssel, technische Dateinamen und Konfigurationswerte, Escape-Sequenzen sowie Leerzeichen am Anfang und Ende beibehalten. Als UTF-8 speichern. Fehlende Einträge werden auf Englisch ergänzt.

Die Sprachdateien benötigen den lokal vorbereiteten mehrsprachigen Manager-Code. Das Hinzufügen dieser Dateien allein aktualisiert keine ältere Manager-Version.

## Format und Übersetzungen

Die Sprachdateien liegen als UTF-8-INI im Ordner `resources/languages`. Die vollständige Formatbeschreibung und Beispiele stehen in der [englischen Anleitung](README.md#file-format).
