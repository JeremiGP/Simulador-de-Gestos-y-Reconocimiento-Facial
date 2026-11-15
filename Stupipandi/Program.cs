using System;
using System.IO;
using System.Threading.Tasks;
using OpenCvSharp;
using Stupipandi.Biometrics;
using Stupipandi.Gestures;
using Stupipandi.Infrastructure;
using Stupipandi.State;

namespace Stupipandi;

/// <summary>
/// Orquestador principal del Simulador de Gestos y Reconocimiento Facial (Stupipandi).
/// Integra la canalización de visión artificial con motores de estabilización anti-jitter,
/// reconocimiento biométrico facial por histéresis, análisis de expresiones faciales
/// ampliadas y clasificación estabilizada de posturas manuales.
/// </summary>
internal sealed class Program : IDisposable
{
    // ── Módulos de infraestructura y captura ─────────────────────────────────
    private readonly CameraManager _cameraManager = new(cameraIndex: 0);
    private readonly VideoLoopEngine _loopEngine;
    private readonly FpsCounter _fpsCounter = new(alpha: 0.08);

    // ── Módulos biométricos y clasificadores Haar ────────────────────────────
    private readonly FaceDetector _faceDetector = new(FaceDetector.FaceCascadeFile);
    private readonly FaceDetector _eyeDetector = new(FaceDetector.EyeCascadeFile);
    private readonly FaceDetector _smileDetector = new(FaceDetector.SmileCascadeFile);
    private readonly FaceRecognizer _faceRecognizer = new(threshold: 52.0);
    private readonly FaceStabilizer _faceStabilizer = new(spatialAlpha: 0.28, votingWindowSize: 7, maxDropoutFrames: 4, baseThreshold: 52.0);
    private readonly ExpressionAnalyzer _expressionAnalyzer;

    // ── Módulos de gestos y segmentación ─────────────────────────────────────
    private readonly GestureClassifier _gestureClassifier = new();
    private readonly GestureStabilizer _gestureStabilizer = new(spatialAlpha: 0.30, historyWindowSize: 7, debounceMilliseconds: 160);

    // ── Estado y renderizado ─────────────────────────────────────────────────
    private readonly ApplicationStateManager _stateManager = new(faceLossGraceFrames: 10);
    private readonly OverlayRenderer _overlayRenderer = new();

    // ── Configuración en tiempo de ejecución ─────────────────────────────────
    private SkinSegmentation.ColorSpace _activeColorSpace = SkinSegmentation.ColorSpace.Hsv;
    private volatile string? _activePrompt = null;
    private volatile int _enrolmentTargetSamples = 0;
    private string? _enrolmentName = null;
    private bool _disposed;

    private static string FaceModelPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "face_model.xml");

    private Program()
    {
        _expressionAnalyzer = new ExpressionAnalyzer(_eyeDetector, _smileDetector, historyWindowSize: 5);
        _loopEngine = new VideoLoopEngine(_cameraManager, "Stupipandi - Simulador de Gestos y Reconocimiento Facial");

        // Conectar eventos del ciclo de vídeo
        _loopEngine.OnFrameReceived += ProcessFrame;
        _loopEngine.OnKeyPressed    += HandleKey;
        _loopEngine.OnLoopExited    += () => Console.WriteLine("\n[INFO] Bucle de video finalizado con éxito.");

        // Notificaciones de hardware
        _cameraManager.OnDeviceError += msg =>
            Console.Error.WriteLine($"[HARDWARE] {msg}");

        // Registro de cambios de gestos consolidados
        _gestureClassifier.GestureChanged += (_, e) =>
        {
            if (e.Current != GestureType.None)
            {
                string nombreGesto = OverlayRenderer.FormatGestureName(e.Current);
                Console.WriteLine($"[GESTO] Cambio a: {nombreGesto}");
            }
        };

        // Registro de transiciones de estado
        _stateManager.StateChanged += (_, e) =>
        {
            string usuarioInfo = e.CurrentUser is null ? string.Empty : $" | Usuario: {e.CurrentUser} ({e.ConfidencePercent}% conf.)";
            Console.WriteLine($"[ESTADO] {e.PreviousMode} -> {e.CurrentMode}{usuarioInfo}");
        };

        // Notificación de gestos aceptados por la máquina de seguridad
        _stateManager.GestureAccepted += gesto =>
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[INTERACCION AUTORIZADA] Gesto procesado para '{_stateManager.CurrentUser}': {gesto}");
            Console.ResetColor();
        };
    }

    private static int Main()
    {
        Console.Title = "Stupipandi - Simulador de Gestos y Reconocimiento Facial";
        PrintBanner();

        using var app = new Program();

        try
        {
            app.Startup();
            app._loopEngine.Run(); // Bloquea hasta que el usuario presione ESC o Q
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"\n[ERROR CRITICO] {ex.Message}");
            Console.ResetColor();
            return 1;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"\n[EXCEPCION INESPERADA] {ex}");
            Console.ResetColor();
            return 2;
        }
        finally
        {
            Console.WriteLine("\nPresione cualquier tecla para cerrar...");
            try { Console.ReadKey(intercept: true); } catch { /* sin terminal interactiva */ }
        }
    }

    private void Startup()
    {
        // 1. Diagnóstico de cascadas Haar
        LogCascadeStatus("Rostro frontal", _faceDetector);
        LogCascadeStatus("Ojos", _eyeDetector);
        LogCascadeStatus("Sonrisa", _smileDetector);

        // 2. Cargar modelo biométrico persistido
        try
        {
            if (_faceRecognizer.Load(FaceModelPath))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[BIOMETRIA] Modelo facial cargado exitosamente ({_faceRecognizer.SampleCount} muestras enroladas).");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[BIOMETRIA] Sin modelo previo. Presione 'T' para enrolar su rostro.");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BIOMETRIA] Advertencia al leer archivo de modelo: {ex.Message}");
        }

        // 3. Iniciar cámara
        _cameraManager.Start(requestedWidth: 1280, requestedHeight: 720);
        Console.WriteLine($"[CAMARA] Dispositivo inicializado: {_cameraManager.FrameWidth}x{_cameraManager.FrameHeight}");
        Console.WriteLine("[CONTROL] Atajos: ESC/Q=Salir | T=Enrolar rostro | S=Guardar modelo | R=Reiniciar | C=Color");
    }

    private static void LogCascadeStatus(string name, FaceDetector detector)
    {
        if (detector.IsLoaded)
        {
            Console.WriteLine($"[CASCADA] {name}: OK ({Path.GetFileName(detector.ResolvedPath)})");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[CASCADA] {name}: NO DISPONIBLE (operación degradada)");
            Console.ResetColor();
        }
    }

    private void ProcessFrame(Mat frame)
    {
        _fpsCounter.Tick();
        double currentFps = _fpsCounter.Value;

        // ── 1. Pipeline de normalización de iluminación y contraste ──────────
        using Mat processed = FramePreprocessor.ProcessPipeline(frame);

        // ── 2. Detección multiescala del rostro ───────────────────────────────
        Rect[] rawFaces = _faceDetector.DetectMultiScale(
            processed,
            scaleFactor: 1.1,
            minNeighbors: 5,
            flags: HaarDetectionTypes.ScaleImage,
            minSize: new Size(60, 60));

        Rect? rawPrimaryFace = rawFaces.Length > 0 ? rawFaces[0] : null;
        FaceRecognitionResult? rawRecResult = null;
        ExpressionResult? expressionResult = null;

        // Si se detectó rostro crudo, obtener predicción biométrica
        if (rawPrimaryFace.HasValue)
        {
            Rect safeFace = ClampRect(rawPrimaryFace.Value, frame.Cols, frame.Rows);
            using Mat faceCrop = processed[safeFace];
            rawRecResult = _faceRecognizer.Predict(faceCrop);

            // Muestreo si está en proceso de enrolamiento
            CheckEnrolmentSample(faceCrop);
        }

        // ── 3. Estabilizador facial (Filtro espacial EMA + Histéresis + Votación) ─
        FaceRecognitionResult stableRecResult = _faceStabilizer.Update(
            rawPrimaryFace,
            rawRecResult,
            frame.Cols,
            frame.Rows);

        Rect? stableFaceRect = _faceStabilizer.CurrentSmoothedRect;

        if (stableFaceRect.HasValue)
        {
            Rect clampedStable = ClampRect(stableFaceRect.Value, frame.Cols, frame.Rows);
            using Mat faceRoi = processed[clampedStable];

            // ── 4. Análisis de expresiones faciales y estados oculares ───────
            expressionResult = _expressionAnalyzer.Analyze(faceRoi, clampedStable);

            // ── 5. Actualizar máquina de estados de seguridad ─────────────────
            _stateManager.UpdateFaceResult(stableRecResult);

            // ── 6. Renderizado de capas faciales ──────────────────────────────
            _overlayRenderer.DrawFace(frame, clampedStable, stableRecResult, expressionResult);
        }
        else
        {
            _stateManager.OnNoFaceDetected();
            _expressionAnalyzer.Reset();
        }

        // ── 7. Segmentación de piel y clasificación de gestos ────────────────
        using (Mat skinMask = SkinSegmentation.CreateMask(frame, _activeColorSpace))
        {
            // Supresión de la región de la cabeza para evitar falsos positivos
            if (stableFaceRect.HasValue)
            {
                Rect face = stableFaceRect.Value;
                Rect faceSuppression = ClampRect(
                    new Rect(face.X - 14, face.Y - 14, face.Width + 28, face.Height + 28),
                    skinMask.Cols,
                    skinMask.Rows);

                Cv2.Rectangle(skinMask, faceSuppression, Scalar.Black, -1);
            }

            // Análisis geométrico de contorno
            ContourAnalysis? contourAnalysis = ContourAnalyzer.Analyze(skinMask, minimumArea: 5_500);
            GestureType rawGesture = _gestureClassifier.Classify(contourAnalysis);

            // ── 8. Estabilizador de gestos (Filtro de moda + Debouncing + Bbox EMA) ──
            var (stableGesture, stableHandBox) = _gestureStabilizer.Update(
                contourAnalysis,
                rawGesture,
                frame.Cols,
                frame.Rows);

            if (stableHandBox.HasValue && stableGesture != GestureType.None)
            {
                _overlayRenderer.DrawGesture(frame, stableHandBox.Value, stableGesture);
                _stateManager.TryProcessGesture(stableGesture);
            }
            else
            {
                _stateManager.TryProcessGesture(GestureType.None);
            }
        }

        // ── 9. Renderizado del HUD informativo y telemetría ──────────────────
        _overlayRenderer.DrawHud(
            frame,
            _stateManager.CurrentMode,
            currentFps,
            _stateManager.CurrentUser,
            _stateManager.CurrentConfidence,
            _stateManager.LastGesture,
            _activeColorSpace.ToString().ToUpperInvariant(),
            _activePrompt);
    }

    private void CheckEnrolmentSample(Mat faceRoi)
    {
        if (_enrolmentTargetSamples > 0 && !string.IsNullOrWhiteSpace(_enrolmentName))
        {
            try
            {
                _faceRecognizer.AddSample(faceRoi, _enrolmentName);
                _enrolmentTargetSamples--;

                if (_enrolmentTargetSamples <= 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[ENROLAMIENTO] Completado con éxito para '{_enrolmentName}'. Total de muestras: {_faceRecognizer.SampleCount}.");
                    Console.WriteLine("[ENROLAMIENTO] Presione 'S' para persistir el modelo en disco.");
                    Console.ResetColor();

                    _activePrompt = $"Enrolado con exito: {_enrolmentName}";
                    _enrolmentName = null;

                    Task.Delay(3000).ContinueWith(_ => _activePrompt = null);
                }
                else
                {
                    _activePrompt = $"Capturando muestras ({5 - _enrolmentTargetSamples}/5)... Mire a la camara";
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ENROLAMIENTO] Error al procesar muestra: {ex.Message}");
                _enrolmentTargetSamples = 0;
                _activePrompt = null;
            }
        }
    }

    private void HandleKey(char key)
    {
        switch (char.ToUpperInvariant(key))
        {
            case 'T':
                BeginEnrolment();
                break;

            case 'S':
                SaveModel();
                break;

            case 'R':
                _stateManager.Reset();
                _faceStabilizer.Reset();
                _expressionAnalyzer.Reset();
                _gestureStabilizer.Reset();
                _fpsCounter.Reset();
                _activePrompt = "Sistema reiniciado";
                Task.Delay(2000).ContinueWith(_ => _activePrompt = null);
                Console.WriteLine("[CONTROL] Estado y estabilizadores reiniciados.");
                break;

            case 'C':
                ToggleColorSpace();
                break;
        }
    }

    private void BeginEnrolment()
    {
        Console.WriteLine("\n──────────────────────────────────────────────────────────");
        Console.Write("[ENROLAMIENTO] Ingrese el nombre de la persona a registrar: ");
        string? name = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[ENROLAMIENTO] Operación cancelada: nombre vacío.");
            Console.ResetColor();
            return;
        }

        _enrolmentName = name;
        _enrolmentTargetSamples = 5;
        _activePrompt = $"Registrando a '{name}'... Mire al frente";
        Console.WriteLine($"[ENROLAMIENTO] Adquiriendo 5 muestras de '{name}'. Mire fijamente a la cámara...");
    }

    private void SaveModel()
    {
        try
        {
            _faceRecognizer.Save(FaceModelPath);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[MODELO] Modelo guardado exitosamente en: {FaceModelPath}");
            Console.ResetColor();

            _activePrompt = "Modelo guardado correctamente";
            Task.Delay(2000).ContinueWith(_ => _activePrompt = null);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"[MODELO] Error al guardar el archivo: {ex.Message}");
            Console.ResetColor();
        }
    }

    private void ToggleColorSpace()
    {
        _activeColorSpace = _activeColorSpace == SkinSegmentation.ColorSpace.Hsv
            ? SkinSegmentation.ColorSpace.YCrCb
            : SkinSegmentation.ColorSpace.Hsv;

        string csName = _activeColorSpace.ToString().ToUpperInvariant();
        Console.WriteLine($"[COLOR] Espacio cromático cambiado a: {csName}");

        _activePrompt = $"Espacio piel: {csName}";
        Task.Delay(1800).ContinueWith(_ => _activePrompt = null);
    }

    private static Rect ClampRect(Rect rect, int maxW, int maxH)
    {
        int x = Math.Clamp(rect.X, 0, maxW - 1);
        int y = Math.Clamp(rect.Y, 0, maxH - 1);
        int w = Math.Clamp(rect.Width,  1, maxW - x);
        int h = Math.Clamp(rect.Height, 1, maxH - y);
        return new Rect(x, y, w, h);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _loopEngine.OnFrameReceived -= ProcessFrame;
        _loopEngine.OnKeyPressed    -= HandleKey;

        _loopEngine.Dispose();
        _cameraManager.Dispose();
        _faceDetector.Dispose();
        _eyeDetector.Dispose();
        _smileDetector.Dispose();
        _expressionAnalyzer.Dispose();
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
  ==============================================================
    STUPIPANDI - SIMULADOR DE GESTOS Y RECONOCIMIENTO FACIAL
    Plataforma: .NET 10 | OpenCvSharp4 4.13 | Vision por Computador
  ==============================================================");
        Console.ResetColor();
    }
}
