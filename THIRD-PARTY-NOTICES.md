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
| Magick.NET (Anbindung an ImageMagick, Mehrkern-Fassung Q16-OpenMP-x64) | 14.17.2 | Apache-2.0 | https://github.com/dlemstra/Magick.NET |
| Microsoft.ML.OnnxRuntime.Managed (Anbindung an die ONNX Runtime) | 1.24.4 | MIT | https://github.com/microsoft/onnxruntime |
| PDFsharp (Druck, „Als PDF speichern“) | 6.2.4 | MIT | https://github.com/empira/PDFsharp |
| MetadataExtractor (Bildinformationen anzeigen: EXIF, IPTC, XMP, ICC, Herstellerdaten) | 2.9.3 | Apache-2.0 | https://github.com/drewnoakes/metadata-extractor-dotnet |
| XmpCore (XMP lesen, von MetadataExtractor benutzt) | 6.1.10.1 | BSD-3-Clause (Adobe XMP) – `licenses/xmpcore-BSD-3-Clause.txt` | https://github.com/drewnoakes/xmp-core-dotnet |
| ILGPU, ILGPU.Algorithms (Bildfilter auf der Grafikkarte über OpenCL/CUDA) | 1.5.1 | University of Illinois/NCSA – `licenses/ilgpu-NCSA.txt` | https://github.com/m4rs-mt/ILGPU |
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
| `Tools/ImageMagick` | ImageMagick (Magick.Native, Q16, Mehrkern-Fassung mit OpenMP) mit den eingebauten Bibliotheken für die Bildformate | 7.1.2 (aus Magick.NET 14.17.2) | ImageMagick-Lizenz (Apache-2.0-artig); eingebaute Bibliotheken u. a. **LGPL** (librsvg, pango, glib, gdk-pixbuf, fribidi, libheif, libde265, libraw, cairo), außerdem BSD, MIT, zlib | `Tools/ImageMagick/Notice.txt` – alle Bestandteile mit Version und vollem Lizenztext |
| `Tools/ImageMagick` (nur Windows) | `vcomp140.dll` – OpenMP-Laufzeit von Microsoft Visual C++, aus dem Paket Magick.NET-Q16-OpenMP-x64 | 14.x | Microsoft Visual C++ Redistributable (Weitergabe in Anwendungen erlaubt) | – |
| `Tools/ImageMagick` (nur Linux) | `libgomp.so.1` – OpenMP-Laufzeit aus GCC (nur benutzt, wenn das System keine hat) | aus GCC 14.2 (Debian 13) | GPL-3.0 mit GCC-Laufzeit-Ausnahme (Weitergabe mit beliebigen Programmen erlaubt) | `licenses/libgomp-GPL-3.0.txt`, `licenses/libgomp-GCC-Runtime-Library-Exception.txt`; Quelltext: https://gcc.gnu.org |
| `Tools/OnnxRuntime` | ONNX Runtime (KI-Modelle lokal ausführen; unter Windows mit DirectML für die Grafikkarte) | 1.24.4 | MIT | `Tools/OnnxRuntime/LICENSE`, `ThirdPartyNotices.txt` |
| `Tools/OnnxRuntime` (nur Windows) | DirectML (Microsoft DirectX Machine Learning, KI auf der Grafikkarte) | 1.15.4 | Microsoft-Lizenz (Weitergabe in Anwendungen erlaubt, nur Windows) | `Tools/OnnxRuntime/DirectML-LICENSE.txt` |

ImageMagick wird unverändert so weitergegeben, wie es das NuGet-Paket
Magick.NET-Q16-OpenMP-x64 enthält. Quelltext und Build-Skripte dieser Fassung:
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
| Gesichtsbereiche (BiSeNet Face Parsing) | MIT | zllrunning (face-parsing.PyTorch), ONNX von FaceFusion |
| Gesichter – GFPGAN 1.4 und 1.3 | Apache-2.0 | TencentARC (GFPGAN), ONNX von FaceFusion |
| Gesichter – RestoreFormer++ | Apache-2.0 | Zhouxia Wang u. a. (RestoreFormer++), ONNX von FaceFusion |
| Gesichter – CodeFormer | **S-Lab License 1.0 – nur nicht-kommerzielle Nutzung** | Shangchen Zhou (CodeFormer), ONNX von FaceFusion |
| Entfernen – LaMa (big-lama) | Apache-2.0 | Roman Suvorov u. a. (LaMa, Samsung AI Center), ONNX von Carve (Hugging Face `Carve/LaMa-ONNX`, feste Fassung) |
| Entfernen – MI-GAN | MIT | Andranik Sargsyan u. a. (MI-GAN, Picsart AI Research), Hugging Face `andraniksargsyan/migan`, feste Fassung |
| Upscaling – 4x-UltraSharpV2 | **CC BY-NC-SA 4.0 – nur nicht-kommerzielle Nutzung**, Namensnennung | Kim2091 (UltraSharpV2), Hugging Face `Kim2091/UltraSharpV2`, feste Fassung |
| Upscaling – PiSA-SR (LoRA) | Apache-2.0 (laut README des Codes) | Lingchen Sun u. a. (PiSA-SR, CVPR 2025), Google Drive der Autoren; Code als Kopie in `extern/PiSA-SR/` |
| Upscaling – Stable Diffusion 2.1-base (UNet, VAE, Text-Encoder; Teile für PiSA-SR) | CreativeML Open RAIL++-M (Nutzungsbeschränkungen der Lizenz gelten) | Stability AI; das Original ist zurückgezogen, geladen vom Spiegel `Manojb/stable-diffusion-2-1-base` auf Hugging Face, feste Fassung |

## Netzzugriffe

ImageConverter sendet keine Nutzungsdaten. Ins Netz geht es nur, wenn man im
Programm ein KI-Modell herunterlädt (Adressen siehe oben); dabei wird nur die
Datei abgerufen, als Kennung schickt das Programm „ImageConverter“.
ImageMagick ist so eingestellt, dass es selbst nichts aus dem Netz lädt und
keine externen Programme aufruft.
