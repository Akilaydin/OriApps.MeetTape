param([string]$SessionDirectory, [string]$RunDirectory, [string]$Ffmpeg = 'ffmpeg', [switch]$DefinitionsOnly)
$ErrorActionPreference = 'Stop'
$echoSessionPath = $SessionDirectory
$echoRunPath = $RunDirectory
. (Join-Path $PSScriptRoot 'Compare-Audio.ps1')
Add-Type -TypeDefinition @'
using System;
using System.IO;
public static class SampleEchoMetrics
{
    public static object Analyze(string mePath, string themPath)
    {
        float[] me = Read(mePath), them = Read(themPath);
        if (me.Length != them.Length || me.Length == 0 || me.Length > 360000) throw new InvalidDataException("Invalid bounded sample lengths.");
        int n = me.Length;
        double meEnergy = 0, themEnergy = 0;
        foreach (float value in me) meEnergy += value * value;
        foreach (float value in them) themEnergy += value * value;
        bool[] active = new bool[n];
        for (int start = 0; start < n; start += 400)
        {
            int end = Math.Min(n, start + 400);
            double energy = 0;
            for (int i = start; i < end; i++) energy += them[i] * them[i];
            if (Math.Sqrt(energy / (end - start)) > 0.007)
                for (int i = start; i < end; i++) active[i] = true;
        }
        double best = -1, gain = 0;
        int bestLag = 0, activeCount = 0;
        for (int lag = -100; lag <= 500; lag++)
        {
            double x = 0, y = 0, xx = 0, yy = 0, xy = 0;
            int count = 0;
            for (int i = Math.Max(0, -lag); i < Math.Min(n, n - lag); i++)
            {
                if (!active[i]) continue;
                double a = them[i], b = me[i + lag];
                x += a; y += b; xx += a * a; yy += b * b; xy += a * b; count++;
            }
            if (count == 0) continue;
            double vx = xx - x * x / count, vy = yy - y * y / count, cov = xy - x * y / count;
            double correlation = vx > 0 && vy > 0 ? cov / Math.Sqrt(vx * vy) : 0;
            if (correlation > best) { best = correlation; bestLag = lag; gain = vx > 0 ? cov / vx : 0; activeCount = count; }
        }
        return new { sampleRate = 2000, sampleCount = n, durationSeconds = n / 2000.0,
            meRmsDb = Db(meEnergy / n), themRmsDb = Db(themEnergy / n),
            peakCorrelation = best, lagMilliseconds = bestLag / 2.0, linearProjectionGain = gain,
            themActiveSeconds = activeCount / 2000.0,
            note = "Correlation is a bleed proxy, not proof of isolated echo or AEC benefit; local speech and room filtering affect it." };
    }
    private static float[] Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length % 4 != 0 || data.Length > 1440000) throw new InvalidDataException("Invalid PCM fixture.");
        float[] result = new float[data.Length / 4];
        Buffer.BlockCopy(data, 0, result, 0, data.Length);
        return result;
    }
    private static double Db(double energy) => 10 * Math.Log10(Math.Max(energy, 1e-20));
}
'@
if ($DefinitionsOnly) { return }
$run = [IO.Path]::GetFullPath($echoRunPath)
$paths = @()
foreach ($name in @('me','them')) {
    $path = Join-Path $run ("echo-$name.f32")
    Invoke-ComparisonFfmpeg $Ffmpeg @('-hide_banner','-v','error','-nostdin','-n','-i',
        (Join-Path $echoSessionPath "$name.mp3"),'-t','180','-ar','2000','-ac','1','-f','f32le',$path)
    $paths += $path
}
$metrics = [SampleEchoMetrics]::Analyze($paths[0], $paths[1])
$json = $metrics | ConvertTo-Json -Depth 4
Write-ComparisonFile (Join-Path $run 'echo-metrics.json') $json
$json
