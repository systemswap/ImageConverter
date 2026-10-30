# STL-Betrachter mit Messen (Konzept, noch nicht umgesetzt)

Wunsch vom 30.10.2026: ImageConverter ist auch ein Bildbetrachter; er soll STL-Modelle
anzeigen und Abstände zwischen Flächen messen können. Nur STL – STEP und andere
CAD-Formate bewusst nicht.

## 1. Verhalten im Programm

- Eine `.stl`-Datei öffnet sich wie ein Bild (Öffnen, Ziehen, „Öffnen mit“, Blättern im
  Ordner). Die Ordnerliste enthält Bilder **und** STL-Dateien.
- Statt der Bildfläche erscheint die 3D-Ansicht. Ausgeblendet bzw. gesperrt, solange ein
  Modell gezeigt wird: Bildeinstellungen, Bearbeitungsliste, Zuschneiden, KI, Vergrößern,
  Export, Drucken.
- Bleibt nutzbar: Hell/Dunkel, Vollbild (F11), Blättern (←/→, Bild auf/ab, Pos1/Ende),
  Infos. Die Infos zeigen für ein Modell: Maße (Breite × Tiefe × Höhe), Anzahl der
  Dreiecke, Oberfläche, Volumen, ob das Modell geschlossen („dicht“) ist.
- Vorschau in der Dateiliste: zunächst ein Symbol, später ein kleines gerendertes Bild.

## 2. Aufbau: fest eingebautes Plugin

Eigenes Projekt `ImageConverter.Viewer3D`, mitgebaut, aber nur über eine schmale
Schnittstelle mit dem Hauptprogramm verbunden:

```
IDocumentViewer
  bool CanOpen(string path)            // ".stl"
  Task<IDocument> OpenAsync(path, ct)
  Control CreateView(IDocument)         // ersetzt die Bildfläche
  IReadOnlyList<InfoRow> Info(IDocument)
```

- Das Hauptprogramm unterscheidet nur „Bild“ und „anderes Dokument“; alles zu 3D steckt
  im Plugin.
- Darin getrennt: Rechenteil ohne Oberfläche (Einlesen, Flächen, Messen – voll testbar)
  und Ansicht (OpenGL, Bedienung, Maßlinien).
- Kein Nachladen aus einem `plugins/`-Ordner: das Programm wird als Einzeldatei bzw.
  AppImage gebaut, Nachladen würde Bau, Versionen und Startprüfung verkomplizieren. Die
  Schnittstelle lässt es später zu.

## 3. Einlesen

- Binär und ASCII; Erkennung am Inhalt (Binärdateien beginnen manchmal auch mit
  `solid`): passt `84 + 50 × Anzahl` zur Dateigröße, ist es binär.
- Schutz gegen kaputte oder präparierte Dateien: Anzahl gegen Dateigröße prüfen, NaN/∞
  ablehnen, Obergrenzen für Zeilenlänge und Dreiecke (Vorschlag 30 Mio.) mit klarer
  Meldung, Lesen im Hintergrund mit Abbruch. Fuzz-Lauf mit Zeit- und Speicherwächter.
- Gleiche Eckpunkte werden zusammengeführt (Index-Puffer): weniger Speicher und die
  Nachbarschaft der Dreiecke für die Flächenerkennung.
- STL hat keine Einheit: angenommen werden Millimeter, umschaltbar auf Zoll.

## 4. Darstellung

- OpenGL über `OpenGlControlBase` aus Avalonia (Windows: ANGLE, Linux: EGL/GLX). **Kein
  neues Paket:** die rund 30 nötigen GL-Befehle werden selbst angebunden; Shader für
  GLSL ES 3.0 und Desktop-GL 3.3.
- Flache, kantenscharfe Schattierung; die Flächennormale entsteht im Fragment-Shader aus
  den Ableitungen der Position – so reichen geteilte Eckpunkte auch bei 10 Mio. Dreiecken.
  Optional feine Linien an Knicken.
- Bedienung: links ziehen = drehen, rechts/mitte ziehen = verschieben, Mausrad = zoomen
  zur Maus, Doppelklick = einpassen. Ein Klick ohne Ziehen wählt (siehe Messen).
- Knöpfe: Vorne, Oben, Seite, Iso, Einpassen; Orthografisch/Perspektive (zum Messen
  orthografisch voreingestellt). Hintergrund folgt Hell/Dunkel.
- Maßlinien und Beschriftungen als 2D-Ebene über der 3D-Ansicht, aus den 3D-Punkten
  projiziert – sie drehen mit.

## 5. Flächen

STL kennt nur einzelne Dreiecke; Flächen werden beim Laden im Hintergrund erkannt:

- **Ebene:** benachbarte Dreiecke mit fast gleicher Richtung (Toleranz etwa 0,5°) wachsen
  zu einer Fläche zusammen; Ebene per Ausgleichsrechnung.
- **Zylinder:** gleichmäßig gekrümmte Streifen (Bohrungen, Wellen); Achse und Radius per
  Ausgleichsrechnung, Abweichung als Gütemaß – zu große Abweichung → Freiform.
- **Freiform:** zusammenhängender Bereich ohne Ebene/Achse; dort zählt nur der kürzeste
  Abstand.
- Treffer beim Klick über einen Hüllkörperbaum (BVH) auf den Dreiecken.

## 6. Messen

Werkzeug **Messen** einschalten, dann:

1. Klick auf eine Fläche → sie leuchtet (A).
2. Klick auf eine zweite Fläche (B) → das Maß erscheint und **bleibt stehen**.
3. Der nächste Klick beginnt ein neues Paar; alle fertigen Maße bleiben sichtbar.
4. Klick auf eine schon gewählte Fläche des offenen Paares hebt die Wahl auf. Klick auf
   eine Fläche eines fertigen Maßes entfernt dieses Maß.
5. Esc hebt das offene Paar auf; „Alle Maße löschen“ räumt auf. Maße gelten nur für das
   gerade gezeigte Modell und werden nicht gespeichert.

Ein einzelner Klick auf einen Zylinder zeigt sofort den **Durchmesser** (Ø), der Zylinder
kann trotzdem als A für ein Paar dienen.

**Punkt zu Punkt:** mit gedrückter Umschalttaste zwei Punkte auf der Oberfläche anklicken
→ gerader Abstand, dazu ΔX/ΔY/ΔZ.

| A ↔ B | angezeigt |
|---|---|
| Ebene ∥ Ebene | senkrechter Abstand |
| Ebene ∦ Ebene | Winkel, dazu kürzester Abstand |
| Zylinder ↔ Zylinder, Achsen parallel | Achsabstand, dazu kürzester und größter Abstand |
| Zylinder ↔ Zylinder, sonst | Winkel der Achsen, kürzester Abstand |
| Zylinder ↔ Ebene | Abstand Achse–Ebene, Winkel |
| mit Freiform | kürzester Abstand |

„Parallel“ heißt: Winkel unter 0,1°; sonst wird der Winkel mit angezeigt. Gerechnet wird
mit den eingelesenen Dreiecken in doppelter Genauigkeit, nicht mit der Anzeige. Anzeige in
mm mit zwei Nachkommastellen (Zoll: drei), Winkel mit 0,01°.

## 7. Tests

- Die Tests erzeugen ihre Modelle selbst (Würfel, Quader mit Bohrung, gedrehter Zylinder,
  Freiform, binär und ASCII) und prüfen Einlesen, Infos (Volumen, Oberfläche, dicht),
  Flächenerkennung, jede Maßart, Auswahl/Abwahl, Punkt zu Punkt und kaputte Dateien.
- Die OpenGL-Ansicht lässt sich ohne Bildschirm nur eingeschränkt prüfen; Drehen und
  Klicken werden von Hand abgenommen.

## 8. Schritte (je eine eigene Beta)

| Schritt | Inhalt | Aufwand |
|---|---|---|
| 1 | Schnittstelle, STL-Leser, Infos, Sperren der Bildfunktionen, Tests | ~1 Tag |
| 2 | 3D-Ansicht, Bedienung, Ansichten, Einbau, Blättern | 2–3 Tage |
| 3 | Flächen, Auswahl, Messen mit Maßlinien, Ø, Punkt zu Punkt | 4–6 Tage |
| später | Vorschaubilder, Schnittebene, Wandstärke, Messwerte kopieren | nach Wunsch |

## 9. Lizenzen und Datenschutz

Kein neues Paket und kein fremder Code → keine Telemetrie, keine neuen Einträge in
`THIRD-PARTY-NOTICES.md`. Verfahren aus Fachliteratur (z. B. Zylinderanpassung) werden im
Code als Quelle genannt.
