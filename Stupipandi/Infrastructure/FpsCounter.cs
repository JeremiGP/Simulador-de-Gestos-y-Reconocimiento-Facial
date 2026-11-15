using System;
using System.Diagnostics;

namespace Stupipandi.Infrastructure;

/// <summary>
/// Calcula los fotogramas por segundo (FPS) en tiempo real mediante un
/// promedio móvil exponencial (EMA) para estabilizar la visualización en pantalla.
/// </summary>
public sealed class FpsCounter
{
    private readonly double _alpha;
    private double _smoothedFps;
    private long _lastTicks;
    private bool _initialized;

    /// <summary>
    /// Inicializa una nueva instancia de <see cref="FpsCounter"/>.
    /// </summary>
    /// <param name="alpha">Factor de suavizado (0 &lt; alpha &lt;= 1). Un valor menor da mayor estabilidad.</param>
    public FpsCounter(double alpha = 0.08)
    {
        if (alpha is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(alpha), "El factor alpha debe estar en el rango (0, 1].");
        }

        _alpha = alpha;
    }

    /// <summary>
    /// Debe invocarse exactamente una vez por cada fotograma procesado.
    /// </summary>
    public void Tick()
    {
        long now = Stopwatch.GetTimestamp();

        if (!_initialized)
        {
            _initialized = true;
            _lastTicks = now;
            return;
        }

        long elapsedTicks = now - _lastTicks;
        _lastTicks = now;

        if (elapsedTicks <= 0) return;

        double instantFps = (double)Stopwatch.Frequency / elapsedTicks;
        _smoothedFps = _smoothedFps <= 0
            ? instantFps
            : _alpha * instantFps + (1 - _alpha) * _smoothedFps;
    }

    /// <summary>
    /// Obtiene el valor actual suavizado de FPS.
    /// </summary>
    public double Value => Math.Round(_smoothedFps, 1);

    /// <summary>
    /// Reinicia el contador de FPS.
    /// </summary>
    public void Reset()
    {
        _initialized = false;
        _smoothedFps = 0;
        _lastTicks = 0;
    }
}
