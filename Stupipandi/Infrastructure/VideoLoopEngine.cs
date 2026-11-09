using System;
using System.Diagnostics;
using System.Threading;
using OpenCvSharp;

namespace GestureFaceSimulator.Infrastructure;

/// <summary>
/// Orquestador del ciclo principal de adquisición de vídeo, despacho de eventos sincronizados y renderizado en pantalla.
/// Controla la frecuencia de muestreo (FPS), la captura de teclas de interrupción y garantiza la liberación estricta
/// de cada fotograma procesado mediante la interfaz de OpenCV.
/// </summary>
public sealed class VideoLoopEngine : IDisposable
{
    private readonly CameraManager _cameraManager;
    private readonly string _windowName;
    private readonly int _targetFps;
    private readonly int _frameDelayMs;
    private readonly object _stateLock = new();

    private volatile bool _isRunning;
    private bool _disposed;

    /// <summary>
    /// Evento sincronizado emitido en cada ciclo con el fotograma original (bruto) adquirido.
    /// Diseñado para desacoplar y alimentar en tiempo real a los módulos de:
    /// - Persona 2 (SkinSegmentation y GestureClassifier)
    /// - Persona 3 (FaceDetector, FaceRecognizer y OverlayRenderer)
    /// </summary>
    public event Action<Mat>? OnFrameReceived;

    /// <summary>
    /// Evento emitido cuando el usuario presiona una tecla en la ventana de OpenCV.
    /// Permite a módulos de estado (ej. ApplicationStateManager de Persona 3) alternar modos de ejecución.
    /// </summary>
    public event Action<char>? OnKeyPressed;

    /// <summary>
    /// Evento emitido al finalizar el ciclo de vídeo.
    /// </summary>
    public event Action? OnLoopExited;

    /// <summary>
    /// Indica si el bucle de renderizado se encuentra actualmente en ejecución activa.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Inicializa una nueva instancia del orquestador <see cref="VideoLoopEngine"/>.
    /// </summary>
    /// <param name="cameraManager">Instancia administrada de hardware de cámara.</param>
    /// <param name="windowName">Título de la ventana de visualización de OpenCV.</param>
    /// <param name="targetFps">Tasa de refresco objetivo por segundo (por defecto 30 FPS).</param>
    /// <exception cref="ArgumentNullException">Lanzado si cameraManager es nulo.</exception>
    public VideoLoopEngine(
        CameraManager cameraManager,
        string windowName = "Simulador de Gestos y Reconocimiento Facial",
        int targetFps = 30)
    {
        _cameraManager = cameraManager ?? throw new ArgumentNullException(nameof(cameraManager));
        _windowName = string.IsNullOrWhiteSpace(windowName) ? "Simulador de Gestos y Reconocimiento Facial" : windowName;
        _targetFps = targetFps > 0 ? targetFps : 30;
        _frameDelayMs = (int)Math.Round(1000.0 / _targetFps);
    }

    /// <summary>
    /// Inicia el bucle sincrónico de adquisición y renderizado. Este método bloquea el hilo de llamada
    /// hasta que el usuario envíe una señal de terminación (teclas ESC o 'Q'), se cancele el token o se invoque <see cref="Stop"/>.
    /// </summary>
    /// <param name="cancellationToken">Token opcional de cancelación cooperativa.</param>
    public void Run(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            ThrowIfDisposed();

            if (_isRunning)
            {
                throw new InvalidOperationException("[VideoLoopEngine] El ciclo de vídeo ya se encuentra en ejecución.");
            }

            _isRunning = true;
        }

        try
        {
            // Asegurar que la cámara esté iniciada
            if (!_cameraManager.IsOpened)
            {
                _cameraManager.Start();
            }

            // Inicializar ventana de renderizado interactiva
            Cv2.NamedWindow(_windowName, WindowFlags.GuiExpanded | WindowFlags.KeepRatio);

            var stopwatch = new Stopwatch();

            while (_isRunning && !cancellationToken.IsCancellationRequested)
            {
                stopwatch.Restart();

                // 1. Intento de captura de fotograma desde el hardware
                if (_cameraManager.TryReadFrame(out Mat? frameOriginal) && frameOriginal is not null)
                {
                    // 2. Gestión estricta de memoria no administrada para el fotograma del ciclo actual
                    using (frameOriginal)
                    {
                        // 3. Despacho a capas superiores (Persona 2 y Persona 3 pueden dibujar directamente en frameOriginal)
                        OnFrameReceived?.Invoke(frameOriginal);

                        // 4. Renderizado en tiempo real sobre la ventana activa
                        if (!_disposed && _isRunning)
                        {
                            Cv2.ImShow(_windowName, frameOriginal);
                        }
                    }
                }
                else
                {
                    // Pausa preventiva si la cámara no entregó frame para no saturar el hilo CPU
                    Thread.Sleep(5);
                }

                // 5. Control de tasa de refresco y captura de teclas de interacción
                stopwatch.Stop();
                int elapsedMs = (int)stopwatch.ElapsedMilliseconds;
                int waitDelay = Math.Max(1, _frameDelayMs - elapsedMs);

                int pressedKeyCode = Cv2.WaitKey(waitDelay);

                if (pressedKeyCode != -1)
                {
                    // Máscara para obtener el código ASCII puro en diferentes plataformas
                    int asciiCode = pressedKeyCode & 0xFF;

                    // Detección de teclas de interrupción: ESC (27), 'q' (113), 'Q' (81)
                    if (asciiCode == 27 || asciiCode == 'q' || asciiCode == 'Q')
                    {
                        break;
                    }

                    // Notificar a módulos superiores (ej. cambios de estado o presets de biometría)
                    OnKeyPressed?.Invoke((char)asciiCode);
                }
            }
        }
        finally
        {
            _isRunning = false;
            CloseRenderingWindow();
            OnLoopExited?.Invoke();
        }
    }

    /// <summary>
    /// Señaliza la detención controlada del bucle de ejecución.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
    }

    private void CloseRenderingWindow()
    {
        try
        {
            Cv2.DestroyWindow(_windowName);
        }
        catch
        {
            // Ignorar excepciones al cerrar la ventana en el desmontaje del entorno
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(VideoLoopEngine));
    }

    /// <summary>
    /// Libera los recursos del motor, cerrando ventanas gráficas y asegurando la detención del bucle.
    /// Nota: La instancia de <see cref="CameraManager"/> puede ser liberada de forma independiente o aquí si se desea.
    /// </summary>
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _isRunning = false;
            CloseRenderingWindow();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }
}
