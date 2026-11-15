using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Stupipandi.Infrastructure;

/// <summary>
/// Motor de audio nativo de Windows de latencia cero basado en WMPlayer.OCX (COM in-process).
/// Permite reproducción individual o simultánea de varias pistas precargadas (ej. vídeo + himno).
/// </summary>
public sealed class AudioManager : IDisposable
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private readonly Dictionary<string, dynamic> _players = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _stopTimerCts;
    private readonly object _syncLock = new();
    private bool _disposed;

    /// <summary>
    /// Precarga un archivo multimedia (.mp3, .wav, .mp4) en silencio para que no suene al iniciar.
    /// </summary>
    public void Preload(string key, string filePath)
    {
        if (!IsWindows || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        lock (_syncLock)
        {
            try
            {
                string fullPath = Path.GetFullPath(filePath);
                Type? wmpType = Type.GetTypeFromProgID("WMPlayer.OCX");
                if (wmpType is null)
                {
                    Console.Error.WriteLine("[AudioManager] WMPlayer.OCX no está disponible en este equipo.");
                    return;
                }

                if (_players.TryGetValue(key, out dynamic? oldPlayer))
                {
                    try
                    {
                        oldPlayer.settings.mute = true;
                        oldPlayer.controls.stop();
                        oldPlayer.close();
                    }
                    catch { }
                }

                dynamic player = Activator.CreateInstance(wmpType)!;

                player.settings.autoStart = false;
                player.settings.mute = true;
                player.settings.volume = 0;
                player.URL = fullPath;

                Thread.Sleep(60);
                player.controls.stop();
                player.controls.currentPosition = 0;

                _players[key] = player;
                Console.WriteLine($"[AUDIO PRECARGADO EN SILENCIO] {key} -> {Path.GetFileName(fullPath)}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AudioManager] Error al precargar '{filePath}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reproduce al instante una pista previamente precargada.
    /// Si stopOthers es false, permite mezclarla simultáneamente con otra pista activa.
    /// </summary>
    public void PlayPreloaded(string key, double durationSeconds = 0, bool stopOthers = true)
    {
        lock (_syncLock)
        {
            if (stopOthers)
            {
                CancelTimer();
                StopAllPlayersInternal();
            }

            if (!_players.TryGetValue(key, out dynamic? player))
            {
                Console.Error.WriteLine($"[AudioManager] Clave de audio no precargada: '{key}'");
                return;
            }

            try
            {
                player.controls.currentPosition = 0;
                player.settings.mute = false;
                player.settings.volume = 100;
                player.controls.play();
                Console.WriteLine($"[AUDIO PLAY] Reproduciendo '{key}'");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AudioManager] Error al reproducir '{key}': {ex.Message}");
            }

            if (stopOthers || _stopTimerCts is null)
            {
                ScheduleAutoStop(durationSeconds);
            }
        }
    }

    /// <summary>
    /// Reproduce cualquier archivo por ruta (lo precarga en silencio si aún no estaba en memoria).
    /// </summary>
    public void Play(string filePath, double durationSeconds = 0, bool stopOthers = true)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            Console.Error.WriteLine($"[AudioManager] Archivo no encontrado: {filePath}");
            return;
        }

        string key = Path.GetFileNameWithoutExtension(filePath);
        if (!_players.ContainsKey(key))
        {
            Preload(key, filePath);
        }

        PlayPreloaded(key, durationSeconds, stopOthers);
    }

    public void PlayProceduralAnthem(bool isHavaNagila, double durationSeconds, bool stopOthers = true)
    {
        byte[] wav = isHavaNagila
            ? GenerateHavaNagilaFallbackWav((int)Math.Ceiling(durationSeconds))
            : GenerateHimnoEspanaFallbackWav((int)Math.Ceiling(durationSeconds));

        string key = isHavaNagila ? "fallback_hava" : "fallback_himno";
        string tempWav = Path.Combine(Path.GetTempPath(), $"stupipandi_{key}.wav");
        File.WriteAllBytes(tempWav, wav);

        Preload(key, tempWav);
        PlayPreloaded(key, durationSeconds, stopOthers);
    }

    public void Stop()
    {
        lock (_syncLock)
        {
            CancelTimer();
            StopAllPlayersInternal();
        }
    }

    private void StopAllPlayersInternal()
    {
        foreach (var player in _players.Values)
        {
            try
            {
                player.settings.mute = true;
                player.settings.volume = 0;
                player.controls.stop();
                player.controls.currentPosition = 0;
            }
            catch { }
        }
    }

    private void CancelTimer()
    {
        try
        {
            _stopTimerCts?.Cancel();
            _stopTimerCts?.Dispose();
            _stopTimerCts = null;
        }
        catch { }
    }

    private void ScheduleAutoStop(double durationSeconds)
    {
        if (durationSeconds <= 0) return;

        CancelTimer();
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

    public static byte[] GenerateHimnoEspanaFallbackWav(int seconds = 9)
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
                audioData[currentSample + i] = (short)(wave * 26000);
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
            CancelTimer();

            foreach (var player in _players.Values)
            {
                try
                {
                    player.settings.mute = true;
                    player.controls.stop();
                    player.close();
                }
                catch { }
            }
            _players.Clear();
        }
    }
}