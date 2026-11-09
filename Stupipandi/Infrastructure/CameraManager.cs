using System;
using OpenCvSharp;

namespace GestureFaceSimulator.Infrastructure;

/// <summary>
/// Encapsula y gestiona el ciclo de vida del hardware de captura de vídeo (webcam integrada o dispositivos USB)
/// mediante OpenCvSharp4, garantizando un manejo robusto de excepciones de hardware y liberación de memoria nativa.
/// </summary>
public sealed class CameraManager : IDisposable
{
    private readonly int _cameraIndex;
    private readonly VideoCaptureAPIs _captureApi;
    private VideoCapture? _videoCapture;
    private readonly object _syncLock = new();
    private bool _disposed;

    /// <summary>
    /// Evento notificado cuando ocurre una desconexión o falla crítica de hardware en la cámara.
    /// </summary>
    public event Action<string>? OnDeviceError;

    /// <summary>
    /// Indica si el hardware de captura está actualmente inicializado y listo para transmitir fotogramas.
    /// </summary>
    public bool IsOpened
    {
        get
        {
            lock (_syncLock)
            {
                return _videoCapture is { IsDisposed: false } && _videoCapture.IsOpened();
            }
        }
    }

    /// <summary>
    /// Ancho del fotograma configurado en la cámara.
    /// </summary>
    public int FrameWidth
    {
        get
        {
            lock (_syncLock)
            {
                return _videoCapture is { IsDisposed: false } && _videoCapture.IsOpened()
                    ? (int)_videoCapture.Get(VideoCaptureProperties.FrameWidth)
                    : 0;
            }
        }
    }

    /// <summary>
    /// Alto del fotograma configurado en la cámara.
    /// </summary>
    public int FrameHeight
    {
        get
        {
            lock (_syncLock)
            {
                return _videoCapture is { IsDisposed: false } && _videoCapture.IsOpened()
                    ? (int)_videoCapture.Get(VideoCaptureProperties.FrameHeight)
                    : 0;
            }
        }
    }

    /// <summary>
    /// Inicializa una nueva instancia de <see cref="CameraManager"/>.
    /// </summary>
    /// <param name="cameraIndex">Índice del dispositivo de captura (0 para cámara por defecto).</param>
    /// <param name="captureApi">API de backend preferente para captura en Windows/SO (DirectShow por defecto para compatibilidad USB rápida).</param>
    public CameraManager(int cameraIndex = 0, VideoCaptureAPIs captureApi = VideoCaptureAPIs.DSHOW)
    {
        _cameraIndex = cameraIndex;
        _captureApi = captureApi;
    }

    /// <summary>
    /// Inicializa y abre el dispositivo de captura de vídeo con gestión preventiva de excepciones.
    /// </summary>
    /// <param name="requestedWidth">Ancho de resolución deseado (opcional).</param>
    /// <param name="requestedHeight">Alto de resolución deseado (opcional).</param>
    /// <exception cref="InvalidOperationException">Se lanza si el hardware no puede ser inicializado o no responde.</exception>
    public void Start(int? requestedWidth = null, int? requestedHeight = null)
    {
        lock (_syncLock)
        {
            ThrowIfDisposed();

            if (IsOpened)
            {
                return;
            }

            try
            {
                // Intento primario con la API especificada (ej. DirectShow para acceso inmediato en Windows)
                _videoCapture = new VideoCapture(_cameraIndex, _captureApi);

                // Fallback automático si la API preferente no abre el dispositivo
                if (!_videoCapture.IsOpened())
                {
                    _videoCapture.Dispose();
                    _videoCapture = new VideoCapture(_cameraIndex, VideoCaptureAPIs.ANY);
                }

                if (!_videoCapture.IsOpened())
                {
                    _videoCapture.Dispose();
                    _videoCapture = null;
                    throw new InvalidOperationException(
                        $"[CameraManager] No fue posible inicializar o acceder a la cámara en el índice {_cameraIndex}. Verifique la conexión física o los permisos del dispositivo.");
                }

                // Configuración opcional de resolución si se especifica
                if (requestedWidth.HasValue && requestedWidth.Value > 0)
                {
                    _videoCapture.Set(VideoCaptureProperties.FrameWidth, requestedWidth.Value);
                }

                if (requestedHeight.HasValue && requestedHeight.Value > 0)
                {
                    _videoCapture.Set(VideoCaptureProperties.FrameHeight, requestedHeight.Value);
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                _videoCapture?.Dispose();
                _videoCapture = null;
                string errorMsg = $"[CameraManager] Error crítico de hardware al intentar iniciar la cámara en el índice {_cameraIndex}: {ex.Message}";
                OnDeviceError?.Invoke(errorMsg);
                throw new InvalidOperationException(errorMsg, ex);
            }
        }
    }

    /// <summary>
    /// Detiene la captura y libera el acceso exclusivo al dispositivo de hardware.
    /// </summary>
    public void Stop()
    {
        lock (_syncLock)
        {
            if (_videoCapture is not null)
            {
                try
                {
                    if (!_videoCapture.IsDisposed && _videoCapture.IsOpened())
                    {
                        _videoCapture.Release();
                    }
                }
                catch (Exception ex)
                {
                    OnDeviceError?.Invoke($"[CameraManager] Advertencia al liberar el dispositivo de captura: {ex.Message}");
                }
                finally
                {
                    _videoCapture.Dispose();
                    _videoCapture = null;
                }
            }
        }
    }

    /// <summary>
    /// Intenta adquirir el siguiente fotograma del flujo de vídeo de forma síncrona y segura.
    /// </summary>
    /// <param name="frame">
    /// Matriz de destino donde se depositará el fotograma decodificado.
    /// Si el método devuelve true, el llamador asume la propiedad del ciclo de vida y DEBE liberar la instancia de <see cref="Mat"/>.
    /// Si devuelve false, este parámetro siempre será null.
    /// </param>
    /// <returns>True si el fotograma es válido, decodificado correctamente y no vacío; False en caso contrario.</returns>
    public bool TryReadFrame(out Mat? frame)
    {
        frame = null;

        lock (_syncLock)
        {
            if (_disposed || _videoCapture is null || _videoCapture.IsDisposed || !_videoCapture.IsOpened())
            {
                return false;
            }

            var candidateFrame = new Mat();

            try
            {
                // Read() decodifica y transfiere el buffer nativo a la estructura Mat
                bool success = _videoCapture.Read(candidateFrame);

                if (!success || candidateFrame.Empty() || candidateFrame.Cols == 0 || candidateFrame.Rows == 0)
                {
                    candidateFrame.Dispose();
                    OnDeviceError?.Invoke("[CameraManager] Se detectó un fotograma corrupto, vacío o pérdida de señal de la cámara.");
                    return false;
                }

                frame = candidateFrame;
                return true;
            }
            catch (Exception ex)
            {
                candidateFrame.Dispose();
                OnDeviceError?.Invoke($"[CameraManager] Excepción al leer fotograma de hardware: {ex.Message}");
                return false;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(CameraManager));
    }

    /// <summary>
    /// Libera los recursos no administrados del objeto <see cref="VideoCapture"/> subyacente.
    /// </summary>
    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }
}
