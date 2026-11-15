using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stupipandi.Infrastructure;

/// <summary>
/// Gestor de reproducción de audio multiformato (.wav, .mp3) no bloqueante.
/// Soporta reproducción mediante MCI (winmm.dll) y PlaySound en Windows,
/// junto con síntesis procedural en memoria para melodías de fallback.
/// </summary>
public sealed class AudioManager : IDisposable
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [DllImport("winmm.dll", EntryPoint = "mciSendStringA", CharSet = CharSet.Ansi)]
    private static extern int MciSendString(string lpstrCommand, StringBuilder? lpstrReturnString, int uReturnLength, IntPtr hwndCallback);

    [DllImport("winmm.dll", EntryPoint = "PlaySound", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool PlaySoundFile(string? pszSound, IntPtr hmod, uint fdwSound);

    [DllImport("winmm.dll", EntryPoint = "PlaySound", SetLastError = true)]
    private static extern bool PlaySoundBytes(byte[]? ptrToSound, IntPtr hmod, uint fdwSound);

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_MEMORY = 0x0004;
    private const uint SND_PURGE = 0x0040;
    private const uint SND_FILENAME = 0x00020000;

    private string? _activeMciAlias;
    private CancellationTokenSource? _stopTimerCts;
    private readonly object _syncLock = new();
    private bool _disposed;

    /// <summary>
    /// Reproduce un archivo de audio (.wav o .mp3) de forma asíncrona y no bloqueante.
    /// Si se especifica una duración mayor a cero, se detiene automáticamente tras el tiempo indicado.
    /// </summary>
    /// <param name="filePath">Ruta absoluta o relativa del archivo de audio.</param>
    /// <param name="durationSeconds">Duración en segundos tras la cual se silenciará el audio (0 = hasta finalizar o cancelación manual).</param>
    public void Play(string filePath, double durationSeconds = 0)
    {
        lock (_syncLock)
        {
            StopInternal();

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return;
            }

            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            try
            {
                if (IsWindows)
                {
                    string alias = "StupipandiAudio_" + Guid.NewGuid().ToString("N");
                    string shortPath = Path.GetFullPath(filePath);

                    // Utilizar MCI para reproducción (.mp3 o .wav)
                    int openRes = MciSendString($"open \"{shortPath}\" type mpegvideo alias {alias}", null, 0, IntPtr.Zero);
                    if (openRes == 0)
                    {
                        MciSendString($"play {alias} from 0", null, 0, IntPtr.Zero);
                        _activeMciAlias = alias;
                    }
                    else if (ext == ".wav")
                    {
                        PlaySoundFile(shortPath, IntPtr.Zero, SND_ASYNC | SND_FILENAME);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AudioManager] Error al reproducir '{filePath}': {ex.Message}");
            }

            if (durationSeconds > 0)
            {
                _stopTimerCts = new CancellationTokenSource();
                var token = _stopTimerCts.Token;
                Task.Delay(TimeSpan.FromSeconds(durationSeconds), token)
                    .ContinueWith(_ =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            Stop();
                        }
                    }, TaskScheduler.Default);
            }
        }
    }

    /// <summary>
    /// Reproduce una secuencia de bytes de formato WAV estándar (PCM) en memoria.
    /// </summary>
    public void PlayWavStream(byte[] wavBytes, double durationSeconds = 0)
    {
        lock (_syncLock)
        {
            StopInternal();

            try
            {
                if (IsWindows && wavBytes.Length > 0)
                {
                    PlaySoundBytes(wavBytes, IntPtr.Zero, SND_ASYNC | SND_MEMORY);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AudioManager] Error al reproducir audio en memoria: {ex.Message}");
            }

            if (durationSeconds > 0)
            {
                _stopTimerCts = new CancellationTokenSource();
                var token = _stopTimerCts.Token;
                Task.Delay(TimeSpan.FromSeconds(durationSeconds), token)
                    .ContinueWith(_ =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            Stop();
                        }
                    }, TaskScheduler.Default);
            }
        }
    }

    /// <summary>
    /// Detiene de inmediato cualquier reproducción de audio activa y cancela temporizadores.
    /// </summary>
    public void Stop()
    {
        lock (_syncLock)
        {
            StopInternal();
        }
    }

    private void StopInternal()
    {
        try
        {
            _stopTimerCts?.Cancel();
            _stopTimerCts?.Dispose();
            _stopTimerCts = null;

            if (IsWindows)
            {
                PlaySoundFile(null, IntPtr.Zero, SND_PURGE);

                if (_activeMciAlias is not null)
                {
                    MciSendString($"stop {_activeMciAlias}", null, 0, IntPtr.Zero);
                    MciSendString($"close {_activeMciAlias}", null, 0, IntPtr.Zero);
                    _activeMciAlias = null;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AudioManager] Excepción en parada de audio: {ex.Message}");
        }
    }

    /// <summary>
    /// Genera y reproduce una melodía sintetizada proceduralmente en memoria (fallback si falta el archivo en disco).
    /// </summary>
    public void PlayProceduralAnthem(bool isHavaNagila, double durationSeconds)
    {
        byte[] wav = isHavaNagila
            ? GenerateHavaNagilaFallbackWav((int)durationSeconds)
            : GenerateHimnoEspanaFallbackWav((int)durationSeconds);

        PlayWavStream(wav, durationSeconds);
    }

    /// <summary>
    /// Sintetiza un archivo WAV (PCM 44.1 kHz, 16-bit mono) con la melodía de Hava Nagila.
    /// </summary>
    public static byte[] GenerateHavaNagilaFallbackWav(int seconds = 6)
    {
        (double Freq, int Ms)[] notes = [
            (329.63, 300), (329.63, 150), (349.23, 150), (392.00, 300), (349.23, 150), (329.63, 150),
            (293.66, 300), (293.66, 300), (293.66, 300), (349.23, 300),
            (329.63, 300), (293.66, 150), (261.63, 150), (293.66, 300), (329.63, 300),
            (261.63, 300), (246.94, 300), (220.00, 600)
        ];

        return SynthesizePcmWav(notes, seconds);
    }

    /// <summary>
    /// Sintetiza un archivo WAV (PCM 44.1 kHz, 16-bit mono) con la Marcha Real (Himno de España).
    /// </summary>
    public static byte[] GenerateHimnoEspanaFallbackWav(int seconds = 8)
    {
        (double Freq, int Ms)[] notes = [
            (261.63, 400), (329.63, 400), (392.00, 600), (329.63, 200), (261.63, 400),
            (349.23, 400), (392.00, 400), (440.00, 600), (392.00, 200), (349.23, 400),
            (329.63, 400), (293.66, 400), (261.63, 600), (293.66, 200), (329.63, 600),
            (261.63, 800)
        ];

        return SynthesizePcmWav(notes, seconds);
    }

    private static byte[] SynthesizePcmWav((double Freq, int Ms)[] melody, int targetSeconds)
    {
        int sampleRate = 44100;
        int totalSamples = sampleRate * Math.Max(1, targetSeconds);
        short[] audioData = new short[totalSamples];

        int currentSample = 0;
        int noteIndex = 0;

        while (currentSample < totalSamples)
        {
            var (freq, ms) = melody[noteIndex % melody.Length];
            noteIndex++;

            int noteSamples = (int)(sampleRate * (ms / 1000.0));
            int samplesToWrite = Math.Min(noteSamples, totalSamples - currentSample);

            for (int i = 0; i < samplesToWrite; i++)
            {
                double t = (double)i / sampleRate;
                double envelope = Math.Min(1.0, (double)i / 200.0) * Math.Max(0.0, 1.0 - (double)i / noteSamples);
                double wave = Math.Sin(2.0 * Math.PI * freq * t) * envelope;
                audioData[currentSample + i] = (short)(wave * 24000);
            }

            currentSample += samplesToWrite;
        }

        using var msOut = new MemoryStream();
        using var bw = new BinaryWriter(msOut);

        int subChunk2Size = audioData.Length * 2;
        int chunkSize = 36 + subChunk2Size;

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(chunkSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(subChunk2Size);

        for (int i = 0; i < audioData.Length; i++)
        {
            bw.Write(audioData[i]);
        }

        return msOut.ToArray();
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed) return;
            _disposed = true;
            StopInternal();
        }
    }
}
