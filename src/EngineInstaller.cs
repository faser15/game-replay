using System.IO.Compression;
using System.Security.Cryptography;

namespace GameReplay;

public static class EngineInstaller
{
    public const string Version = "9.0.1";
    public const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.1-essentials_build.zip";
    public const string Sha256 = "fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9";

    public static async Task InstallAsync(string root, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        string engine = Path.Combine(root, "Engine");
        Directory.CreateDirectory(root);
        string work = Path.Combine(root, "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string archive = Path.Combine(work, "engine.zip");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GameReplay/1.3");
            using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            long expected = response.Content.Headers.ContentLength ?? 0;
            if (expected > 200L * 1024 * 1024) throw new IOException("Unexpected recording-engine download size.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(archive))
            {
                byte[] buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > 200L * 1024 * 1024) throw new IOException("Unexpected recording-engine download size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report($"Downloading recording engine… {total / 1048576} MB" + (expected > 0 ? $" / {expected / 1048576} MB" : ""));
                }
            }
            progress?.Report("Verifying recording engine…");
            await using (var input = File.OpenRead(archive))
            {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
                if (!hash.Equals(Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Recording-engine checksum did not match. No executable was installed.");
            }
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (string filename in new[] { "ffmpeg.exe", "ffprobe.exe", "LICENSE", "README.txt" })
                {
                    var entry = zip.Entries.Single(e => e.Name.Equals(filename, StringComparison.OrdinalIgnoreCase));
                    entry.ExtractToFile(Path.Combine(work, filename));
                }
            }
            Directory.CreateDirectory(engine);
            foreach (string filename in new[] { "ffmpeg.exe", "ffprobe.exe", "LICENSE", "README.txt" })
                File.Move(Path.Combine(work, filename), Path.Combine(engine, filename), true);
            progress?.Report("Recording engine ready.");
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
