using AssetRipper.TextureDecoder.Bc;
using BCnEncoder.Shared;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using AssetRipper.TextureDecoder.Rgb.Formats;
using System.Diagnostics;

namespace Obr2Sse;

/// Turns Oblivion Remastered's PBR texture set into what Skyrim's shader expects, as DDS.
///
/// Oblivion packs NNRM: normal x and y in red and green, roughness in blue, metallic in alpha. The
/// normal's z is dropped and rebuilt, since a unit vector only needs two components. Skyrim wants a
/// three channel normal with specular in alpha, plus a separate mask driving cubemap reflection.
///
/// Decoding is managed (AssetRipper), the pixel work is done here, and compression is handed to
/// texconv (Microsoft's DirectXTex tool) - the same encoder the Skyrim modding tools use. It is far
/// faster at BC7 than a managed encoder and writes a spec-correct DDS: full mip chain, the flags a
/// mipmapped texture needs, and the sRGB tag a colour map needs.
public static class Textures
{
    /// Whether to spend texconv's exhaustive BC7 search. Worth it for a single hero asset; for a batch
    /// the default fast BC7 is already better than the BC3 Skyrim ships and quick enough to run.
    public static bool HighQuality { get; set; }

    /// How much of the inverted roughness reaches the specular channel.
    public static float SpecularStrength { get; set; } = 0.55f;

    public static void WriteDiffuse(UTexture2D texture, string outPath)
    {
        var pixels = Decode(texture);

        // Oblivion bakes a mask into the base colour's alpha (opacity, emissive, whatever the UE5
        // material used it for). Skyrim's inventory shader reads a diffuse's alpha as transparency, so
        // carried over it turns those weapons see-through in menus. Skyrim weapons are opaque, so the
        // alpha is forced solid here - the same thing vanilla's no-alpha weapon diffuses amount to.
        for (int y = 0; y < pixels.GetLength(0); y++)
            for (int x = 0; x < pixels.GetLength(1); x++)
            {
                var p = pixels[y, x];
                pixels[y, x] = new ColorRgba32(p.r, p.g, p.b, 255);
            }

        // A colour map, so BC7 sRGB. The pixels are already sRGB, so texconv is told as much and keeps
        // them as they are rather than gamma-converting into the sRGB target.
        Encode(pixels, outPath, "BC7_UNORM_SRGB", srgbInput: true);
    }

    public static void WriteNormal(UTexture2D texture, string outPath)
    {
        var pixels = Decode(texture);

        Parallel.For(0, pixels.GetLength(0), y =>
        {
            for (int x = 0; x < pixels.GetLength(1); x++)
            {
                var p = pixels[y, x];

                float nx = p.r / 255f * 2f - 1f;
                float ny = p.g / 255f * 2f - 1f;
                float nz = MathF.Sqrt(MathF.Max(0f, 1f - nx * nx - ny * ny));

                // Specular is roughness inverted, but Oblivion's metals are smooth enough that a
                // straight inversion pins it near white and the weapon glares. Pulled down to the
                // range vanilla Skyrim textures sit in.
                float specular = (1f - p.b / 255f) * SpecularStrength;
                pixels[y, x] = new ColorRgba32(p.r, p.g, (byte)((nz * 0.5f + 0.5f) * 255f),
                                               (byte)Math.Clamp(specular * 255f, 0f, 255f));
            }
        });

        // A normal map is linear data, not colour, so no sRGB. BC7 keeps the specular in alpha, which
        // BC5 could not.
        Encode(pixels, outPath, "BC7_UNORM", srgbInput: false);
    }

    /// How much of the metallic reflection reaches the mask. Oblivion's metals read as more
    /// reflective than Skyrim models them, so the cubemap term is pulled down to sit in the range
    /// vanilla weapon masks occupy (a mean near 0.15 rather than 0.35).
    public static float EnvStrength { get; set; } = 0.4f;

    /// How much cubemap reflection each pixel takes. Metal reflects, rough does not. A small floor
    /// keeps even the dull parts faintly reflective, as vanilla masks are.
    public static void WriteEnvironmentMask(UTexture2D texture, string outPath, float floor = 0.03f)
    {
        var pixels = Decode(texture);

        Parallel.For(0, pixels.GetLength(0), y =>
        {
            for (int x = 0; x < pixels.GetLength(1); x++)
            {
                var p = pixels[y, x];

                float roughness = p.b / 255f;
                float metallic = p.a / 255f;
                float mask = floor + metallic * (1f - roughness) * EnvStrength;

                byte v = (byte)Math.Clamp(mask * 255f, 0f, 255f);
                pixels[y, x] = new ColorRgba32(v, v, v, 255);
            }
        });

        // A single-channel greyscale mask, no alpha needed, so BC1 - a quarter the size of BC7.
        Encode(pixels, outPath, "BC1_UNORM", srgbInput: false);
    }

    /// Decodes mip 0 into straight rgba.
    private static ColorRgba32[,] Decode(UTexture2D texture)
    {
        var mip = texture.GetFirstMip() ?? throw new InvalidOperationException("texture has no mips");
        var data = mip.BulkData?.Data ?? throw new InvalidOperationException("mip has no data");

        int width = mip.SizeX;
        int height = mip.SizeY;
        var format = texture.Format;

        byte[] rgba = format switch
        {
            EPixelFormat.PF_DXT1 => DecodeBc(data, width, height, 1),
            EPixelFormat.PF_DXT5 => DecodeBc(data, width, height, 3),
            EPixelFormat.PF_BC5 => DecodeBc(data, width, height, 5),
            EPixelFormat.PF_BC7 => DecodeBc(data, width, height, 7),
            EPixelFormat.PF_B8G8R8A8 => Swizzle(data),
            _ => throw new NotSupportedException($"unsupported pixel format {format}")
        };

        var pixels = new ColorRgba32[height, width];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[y, x] = new ColorRgba32(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
            }
        }

        return pixels;
    }

    private static byte[] DecodeBc(byte[] data, int width, int height, int variant)
    {
        byte[] output = new byte[width * height * 4];

        switch (variant)
        {
            case 1: Bc1.Decompress<ColorRGBA<byte>, byte>(data, width, height, out output); break;
            case 3: Bc3.Decompress<ColorRGBA<byte>, byte>(data, width, height, out output); break;
            case 5: Bc5.Decompress<ColorRGBA<byte>, byte>(data, width, height, out output); break;
            case 7: Bc7.Decompress<ColorRGBA<byte>, byte>(data, width, height, out output); break;
        }

        return output;
    }

    private static byte[] Swizzle(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];

        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }

        return rgba;
    }

    /// texconv, bundled next to the tool. Resolved once.
    private static readonly string Texconv = Path.Combine(AppContext.BaseDirectory, "texconv.exe");

    /// Compresses the processed pixels to a Skyrim-ready DDS through texconv.
    ///
    /// The pixels are handed over as an uncompressed DDS in a scratch folder, and texconv writes the
    /// compressed result - full mip chain (-m 0), correct header - straight to the output folder under
    /// the same name. sRGB colour maps pass -srgbi so their already-encoded pixels are not gamma-shifted
    /// into the sRGB target. High quality spends BC7's exhaustive search (-bc x).
    private static void Encode(ColorRgba32[,] pixels, string outPath, string format, bool srgbInput)
    {
        // Unique per call so concurrent runs or an antivirus scan never share the input file. texconv
        // names its output after the input, so only the folder changes, not the name.
        var scratch = Path.Combine(Path.GetTempPath(), "obr2sse-tex", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        try
        {
            var input = Path.Combine(scratch, Path.GetFileName(outPath));
            WriteUncompressedDds(pixels, input);

            var args = new List<string> { "-nologo", "-y", "-f", format, "-m", "0" };
            if (srgbInput) args.Add("-srgbi");
            if (HighQuality) { args.Add("-bc"); args.Add("x"); }
            args.Add("-o");
            args.Add(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            args.Add(input);

            RunTexconv(args, outPath);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    /// Runs texconv, retrying a few times: an antivirus can briefly lock the input or output file, and
    /// -y makes each retry overwrite cleanly.
    private static void RunTexconv(IReadOnlyList<string> args, string outPath)
    {
        const int attempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            var start = new ProcessStartInfo(Texconv)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // texconv is a console program; without this it flashes a window on every texture.
                CreateNoWindow = true,
            };
            foreach (var a in args)
                start.ArgumentList.Add(a);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start texconv");
            string stderr = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0)
                return;

            if (attempt >= attempts)
                throw new InvalidOperationException($"texconv failed ({process.ExitCode}) for {outPath}: {stderr}");

            System.Threading.Thread.Sleep(150 * attempt);
        }
    }

    private static void DeleteScratch(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best effort; a folder still briefly held is harmless.
        }
    }

    /// Writes the pixels as a plain 32-bit uncompressed DDS (B8G8R8A8), the input texconv compresses.
    /// Only mip 0 - texconv builds the chain from it.
    private static void WriteUncompressedDds(ColorRgba32[,] pixels, string path)
    {
        int height = pixels.GetLength(0);
        int width = pixels.GetLength(1);

        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);

        w.Write(0x20534444u);                 // "DDS "
        w.Write(124u);                        // dwSize
        w.Write(0x0000100Fu);                 // CAPS | HEIGHT | WIDTH | PIXELFORMAT | PITCH
        w.Write((uint)height);
        w.Write((uint)width);
        w.Write((uint)(width * 4));           // pitch
        w.Write(0u);                          // depth
        w.Write(0u);                          // mip count
        for (int i = 0; i < 11; i++) w.Write(0u);

        w.Write(32u);                         // pixel format size
        w.Write(0x41u);                       // RGB | ALPHAPIXELS
        w.Write(0u);                          // fourCC
        w.Write(32u);                         // bit count
        w.Write(0x00FF0000u);                 // R mask
        w.Write(0x0000FF00u);                 // G mask
        w.Write(0x000000FFu);                 // B mask
        w.Write(0xFF000000u);                 // A mask

        w.Write(0x1000u);                     // caps: TEXTURE
        w.Write(0u);
        w.Write(0u);
        w.Write(0u);
        w.Write(0u);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var p = pixels[y, x];
                w.Write(p.b);
                w.Write(p.g);
                w.Write(p.r);
                w.Write(p.a);
            }
        }
    }
}
