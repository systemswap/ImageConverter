using System.Globalization;
using ImageConverter.Core.Imaging;
using ImageConverter.Core.Platform;
using ImageMagick;

// Haze lab of the dehaze filter: generated landscapes with known depth, hazed with the scattering model, dehazed with
// the program's settings and measured against the truth (see Lab.cs and docs/DUNSTFILTER.md).
//
//   lab [csv <file>]                               all scenes × densities × noise; median and worst case per metric
//   scene <seed> <sun 0|1> <beta> <noise> <prefix>  one scene as PNG: hazy, truth and result (example settings)
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
NativeLibraries.Configure();

switch (args.FirstOrDefault())
{
    case "lab":
        Lab.Run(args.Skip(1).ToArray());
        break;
    case "scene" when args.Length > 5:
    {
        var seed = int.Parse(args[1]);
        var scene = Lab.Make(seed, args[2] == "1");
        var hazy = Lab.Hazy(scene, double.Parse(args[3]), double.Parse(args[4]), seed, out _);
        Save(hazy, args[5] + "-dunstig.png");
        var truth = new byte[Lab.W * Lab.H * 4];
        for (var p = 0; p < Lab.W * Lab.H; p++)
        {
            truth[p * 4] = (byte)Math.Round(Lab.Enc(Math.Min(1, scene.Truth[p * 3 + 2])) * 255);
            truth[p * 4 + 1] = (byte)Math.Round(Lab.Enc(Math.Min(1, scene.Truth[p * 3 + 1])) * 255);
            truth[p * 4 + 2] = (byte)Math.Round(Lab.Enc(Math.Min(1, scene.Truth[p * 3])) * 255);
            truth[p * 4 + 3] = 255;
        }
        Save(truth, args[5] + "-wahrheit.png");
        Save(Adjust.Apply(hazy, Lab.W, Lab.H, Lab.Settings[1].Adjust), args[5] + "-ergebnis.png");
        break;
    }
    default:
        Console.WriteLine("Aufruf: lab [csv <datei>] | scene <seed> <sonne 0|1> <dichte> <rauschen> <präfix>");
        break;
}

static void Save(byte[] bgra, string path)
{
    using var image = new MagickImage(bgra, new PixelReadSettings((uint)Lab.W, (uint)Lab.H, StorageType.Char, "BGRA"));
    image.Write(path);
}
