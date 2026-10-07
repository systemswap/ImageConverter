# Fremde Bestandteile und ihre Lizenzen

ImageConverter selbst steht unter der MIT-Lizenz (siehe `LICENSE`). Mit dem
Programm werden Bestandteile anderer Projekte weitergegeben; ihre Rechte
liegen bei den jeweiligen Autoren. Hier steht, was dabei ist, unter welcher
Lizenz es steht und wo der Lizenztext liegt.

## Im Programm (AppImage bzw. ImageConverter.exe)

| Bestandteil | Version | Lizenz | Projekt |
|---|---|---|---|
| .NET-Laufzeit | 10 | MIT | https://github.com/dotnet/runtime |
| Avalonia (UI, Fluent-Design, Skia-, X11-, Win32- und FreeDesktop-Anbindung) | 12.1.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Fonts.Inter | 12.1.1 | MIT | https://github.com/AvaloniaUI/Avalonia |
| Schrift Inter (in Avalonia.Fonts.Inter) | | SIL Open Font License 1.1 – `licenses/inter-OFL.txt` | https://github.com/rsms/inter |
| ANGLE (nur Windows, über Avalonia.Angle.Windows.Natives) | | BSD-3-Clause | https://chromium.googlesource.com/angle/angle |
| SkiaSharp / Skia | 3.119.4 | MIT / BSD-3-Clause | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp / HarfBuzz | 8.3.1.3 | MIT / „Old MIT“ | https://github.com/mono/SkiaSharp, https://github.com/harfbuzz/harfbuzz |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| Magick.NET (Anbindung an ImageMagick) | 14.17.2 | Apache-2.0 | https://github.com/dlemstra/Magick.NET |
| Microsoft.ML.OnnxRuntime.Managed (Anbindung an die ONNX Runtime) | 1.30.0 | MIT | https://github.com/microsoft/onnxruntime |
| PDFsharp (Druck, „Als PDF speichern“) | 6.2.4 | MIT | https://github.com/empira/PDFsharp |
| System.Drawing.Common (Drucken, nur Windows), System.Numerics.Tensors, Microsoft.Extensions.*, Microsoft.IO.RecyclableMemoryStream | | MIT | https://github.com/dotnet |
| Tmds.DBus.Protocol | 0.94.1 | MIT | https://github.com/tmds/Tmds.DBus |
| MicroCom.Runtime | 0.11.6 | MIT | https://github.com/kekekeks/MicroCom |

Für die MIT-lizenzierten Bestandteile gilt der folgende Text, jeweils mit dem
Copyright-Vermerk des Projekts:

> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

## Mitgelieferte Bibliotheken (Ordner `Tools/` neben dem Programm)

Fremde native Bibliotheken liegen nicht in der Programmdatei, sondern jede in
ihrem eigenen Ordner daneben, mit ihren Lizenztexten.

| Ordner | Bestandteil | Version | Lizenz | Lizenztexte |
|---|---|---|---|---|
| `Tools/ImageMagick` | ImageMagick (Magick.Native, Q16) mit den eingebauten Bibliotheken für die Bildformate | 7.1.2 (aus Magick.NET 14.17.2) | ImageMagick-Lizenz (Apache-2.0-artig); eingebaute Bibliotheken u. a. **LGPL** (librsvg, pango, glib, gdk-pixbuf, fribidi, libheif, libde265, libraw, cairo), außerdem BSD, MIT, zlib | `Tools/ImageMagick/Notice.txt` – alle Bestandteile mit Version und vollem Lizenztext |
| `Tools/OnnxRuntime` | ONNX Runtime (KI-Modelle lokal ausführen, CPU) | 1.30.0 | MIT | `Tools/OnnxRuntime/LICENSE`, `ThirdPartyNotices.txt` |

ImageMagick wird unverändert so weitergegeben, wie es das NuGet-Paket
Magick.NET-Q16-AnyCPU enthält. Quelltext und Build-Skripte dieser Fassung:
https://github.com/dlemstra/Magick.Native (Version passend zu Magick.NET
14.17.2) und https://github.com/ImageMagick/ImageMagick (7.1.2-32). Wer eine
andere Fassung verwenden will, legt sie in `Tools/ImageMagick` (gleicher
Dateiname) oder baut ImageConverter aus dem Quelltext mit ihr neu.

Die ONNX Runtime sendet in den offiziellen Builds sonst Nutzungsdaten an
Microsoft; ImageConverter schaltet das beim Start ab
(`ORT_DISABLE_TELEMETRY=1` und über die API), siehe
`docs/FEHLERGEDAECHTNIS.md`.

## KI-Modelle (nur auf Knopfdruck geladen)

Die Modelle gehören nicht zum Programm. Sie werden erst geladen, wenn man sie
im Programm herunterlädt, direkt von den genannten Quellen, mit Prüfsumme, und
liegen dann im Ordner `KI-Modelle/` neben dem Programm.

| Modell | Lizenz | Herkunft |
|---|---|---|
| Allround (Real-ESRGAN, klein) | BSD-3-Clause | Xintao Wang (Real-ESRGAN), ONNX von Qualcomm AI Hub |
| Gesichtserkennung (YuNet) | MIT | Shiqi Yu u. a. (OpenCV Zoo), ONNX von FaceFusion |
| Gesichter – GFPGAN | Apache-2.0 | TencentARC (GFPGAN), ONNX von FaceFusion |
| Gesichter – CodeFormer | **S-Lab License 1.0 – nur nicht-kommerzielle Nutzung** | Shangchen Zhou (CodeFormer), ONNX von FaceFusion |

## Netzzugriffe

ImageConverter sendet keine Nutzungsdaten. Ins Netz geht es nur, wenn man im
Programm ein KI-Modell herunterlädt (Adressen siehe oben); dabei wird nur die
Datei abgerufen, als Kennung schickt das Programm „ImageConverter“.
ImageMagick ist so eingestellt, dass es selbst nichts aus dem Netz lädt und
keine externen Programme aufruft.
