# SaveDock

SaveDock sucht, sichert und lädt lokale Spielstände unter Windows. Die Windows-x64-Version unter `dist\SaveDock.exe` enthält die Laufzeit und den Spielekatalog. Suche und Sicherung funktionieren offline.

## Bedienung

1. Beim Start sucht SaveDock nach installierten Spielen und bekannten Spielstandpfaden. Wähle einen Speicherort. SaveDock verwendet darin den Ordner `SaveDock Backups`.
2. Wähle die **Sicherungsart** und klicke **Backup** für ein Spiel oder **Alle sichern**. **Automatisch** erstellt eine Vollsicherung, dann bis zu fünf Differenzsicherungen und danach wieder eine Vollsicherung. **Vollsicherung** erstellt eine eigenständige ZIP. **Differenzsicherung** speichert Änderungen seit der letzten Vollsicherung und legt bei Bedarf zuerst eine Vollsicherung an.
3. **Laden** öffnet die Versionen eines Spiels. Wähle den gewünschten Sicherungszeitpunkt, prüfe die Zielpfade und schließe das Spiel vor der Bestätigung.
4. Über **Spiel hinzufügen** kannst du Dateien und Ordner sowie optional eine Spiel-EXE oder ein Icon auswählen. Bei einem bekannten Spielnamen ergänzen die eigenen Pfade den Spieleintrag.

SaveDock liest lokale Installationsdaten von Steam, Epic Games, GOG, Ubisoft Connect, EA app, Battle.net und Xbox (PC-Spielinstallationen mit MicrosoftGame.config). Du kannst nach einem Launcher filtern. Erkannte Installationen bleiben auch ohne bekannten Speicherpfad sichtbar. Wähle das Spiel aus und nutze **Spielstandpfad hinzufügen**, um einen Pfad zu ergänzen.

## Aufbewahrung und Geschwindigkeit

Unter **Einstellungen** wählst du die ZIP-Kompression, die Änderungsprüfung und die Aufbewahrung pro Spiel. **Schnell** ist die Standardkompression. Bei der optionalen Schnellprüfung liest SaveDock Dateien mit identischer Größe sowie identischem Erstellungs- und Änderungszeitpunkt seit dem letzten Backup nicht erneut. **Dateiinhalt prüfen** ist die Standardeinstellung und erkennt auch Änderungen bei identischen Dateidaten.

**Standardverlauf** behält bis zu drei Vollsicherungen mit jeweils fünf Differenzsicherungen. **Nur neueste Version** behält eine eigenständige ZIP pro Spiel. **Letzte X Versionen** behält X auswählbare Stände, soweit sie existieren. Die für einen dieser Stände nötige Vollsicherung kann als zusätzliche ZIP erhalten bleiben. Die Aufbewahrung wird beim nächsten erfolgreichen Backup des jeweiligen Spiels angewendet.

Jede native Sicherung liegt als datierte ZIP unter `SaveDock Backups\SaveDock\<Spiel>-<ID>`. Die Datei `index.json` enthält die Dateipfade, Prüfsummen und Zuordnungen der Versionen. Beim Laden wählst du einen Sicherungszeitpunkt. SaveDock liest bei einer Differenzversion die zugehörige Vollsicherung mit. Die Aufbewahrungseinstellungen beziehen sich auf native SaveDock-Sicherungen. ZIPs aus dem ersten Prototyp mit `mapping.yaml` bleiben separat im Verlauf.

Sicherungen aus dem ersten Prototyp bleiben im bisherigen Format mit `mapping.yaml` lesbar. Alte ZIPs mit Voll- oder Differenzsicherungen und den üblichen Windows-Registry-Werten stehen in derselben Versionsauswahl. Alte Sicherungen verwenden die ursprünglichen absoluten Dateipfade. Bei neuen Sicherungen werden bekannte Benutzerordner auf den aktuellen Windows-Benutzer aufgelöst. Die erste neue Sicherung pro Spiel ist eine Vollsicherung.

## Build

```powershell
dotnet run --project .\SaveDock.csproj
dotnet publish .\SaveDock.csproj -c Release -r win-x64 --self-contained true -o .\dist
```

Der mitgelieferte [Katalog-Snapshot](https://github.com/mtkennerly/ludusavi-manifest/tree/ff0faa56840b0081c11a5b2c28f44bd478b29faf) stammt aus dem Ludusavi-Manifest. Die Lizenz liegt in `Catalog`. `Catalog/build-catalog.py` kann aus einer Manifest-YAML einen neuen Windows-Katalog erzeugen und verwendet dafür PyYAML.
