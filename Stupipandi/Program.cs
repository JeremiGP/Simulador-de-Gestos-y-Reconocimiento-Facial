using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenCvSharp;
using Stupipandi.Biometrics;
using Stupipandi.Game;
using Stupipandi.Gestures;
using Stupipandi.Infrastructure;
using Stupipandi.State;

namespace Stupipandi;

internal sealed class Program : IDisposable
{
    // ── Módulos de infraestructura, audio y multimedia ────────────────────────
    private readonly CameraManager _cameraManager = new(cameraIndex: 0);
    private readonly VideoLoopEngine _loopEngine;
    private readonly AudioManager _audioManager = new();
    private readonly FpsCounter _fpsCounter = new(alpha: 0.08);

    // ── Módulos biométricos ──────────────────────────────────────────────────
    private readonly FaceDetector _faceDetector = new(FaceDetector.FaceCascadeFile);
    private readonly FaceDetector _profileDetector = new(FaceDetector.ProfileFaceCascadeFile);
    private readonly FaceDetector _eyeDetector = new(FaceDetector.EyeCascadeFile);
    private readonly FaceDetector _smileDetector = new(FaceDetector.SmileCascadeFile);
    private readonly FaceRecognizer _faceRecognizer = new(threshold: 52.0);
    private readonly FaceStabilizer _faceStabilizer = new(spatialAlpha: 0.28, votingWindowSize: 7, maxDropoutFrames: 5, baseThreshold: 52.0);
    private readonly ExpressionAnalyzer _expressionAnalyzer;

    // ── Módulos de gestos y minijuego ────────────────────────────────────────
    private readonly GestureClassifier _gestureClassifier = new();
    private readonly GestureStabilizer _gestureStabilizer = new(spatialAlpha: 0.30, historyWindowSize: 7, debounceMilliseconds: 150);
    private readonly HighNoonGameEngine _highNoonEngine = new();

    // ── Estado y renderizado AR ──────────────────────────────────────────────
    private readonly ApplicationStateManager _stateManager = new(faceLossGraceFrames: 10);
    private readonly OverlayRenderer _overlayRenderer = new();

    // ── Configuración en tiempo de ejecución ─────────────────────────────────
    private SkinSegmentation.ColorSpace _activeColorSpace = SkinSegmentation.ColorSpace.Hsv;
    private volatile string? _activePrompt = null;
    private volatile int _enrolmentTargetSamples = 0;
    private string? _enrolmentName = null;
    private bool _disposed;

    private static string MediaDir =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Media");

    private static string FaceModelPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "face_model.xml");

    private Program()
    {
        _expressionAnalyzer = new ExpressionAnalyzer(_eyeDetector, _smileDetector, historyWindowSize: 5);
        _loopEngine = new VideoLoopEngine(_cameraManager, "Stupipandi - Simulador AR de Gestos y Reconocimiento Facial");

        _loopEngine.OnFrameReceived += ProcessFrame;
        _loopEngine.OnKeyPressed += HandleKey;
        _loopEngine.OnLoopExited += () => Console.WriteLine("\n[INFO] Bucle de video finalizado.");

        _cameraManager.OnDeviceError += msg =>
            Console.Error.WriteLine($"[HARDWARE] {msg}");

        // Conectar audio y estados al activarse cualquiera de los 6 disparadores
        _stateManager.TriggerActivated += OnTriggerActivated;
        _stateManager.TriggerDeactivated += OnTriggerDeactivated;

        _stateManager.StateChanged += (_, e) =>
        {
            string usuarioInfo = e.CurrentUser is null ? string.Empty : $" | Usuario: {e.CurrentUser} ({e.ConfidencePercent}% conf.)";
            Console.WriteLine($"[ESTADO] {e.PreviousMode} -> {e.CurrentMode}{usuarioInfo}");
        };
    }

    private static int Main()
    {
        Console.Title = "Stupipandi - Simulador AR de Gestos y Reconocimiento Facial";
        PrintBanner();

        // Inicializar y verificar todos los assets en Assets/Media
        MediaInitializer.EnsureAssetsExist();

        using var app = new Program();

        try
        {
            app.Startup();
            app._loopEngine.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"\n[ERROR] {ex.Message}\n{ex.StackTrace}");
            Console.ResetColor();
            return 1;
        }
        finally
        {
            Console.WriteLine("\nPresione cualquier tecla para cerrar...");
            try { Console.ReadKey(intercept: true); } catch { }
        }
    }

    private void Startup()
    {
        LogCascadeStatus("Rostro frontal", _faceDetector);
        LogCascadeStatus("Rostro perfil", _profileDetector);
        LogCascadeStatus("Ojos", _eyeDetector);
        LogCascadeStatus("Sonrisa", _smileDetector);

        try
        {
            if (_faceRecognizer.Load(FaceModelPath))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[BIOMETRIA] Modelo facial cargado ({_faceRecognizer.SampleCount} muestras).");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[BIOMETRIA] Sin modelo previo. Presione 'T' para enrolar su rostro (opcional para triggers AR).");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BIOMETRIA] Advertencia modelo: {ex.Message}");
        }

        _cameraManager.Start(requestedWidth: 1280, requestedHeight: 720);
        Console.WriteLine($"[CAMARA] Iniciada: {_cameraManager.FrameWidth}x{_cameraManager.FrameHeight}");
        Console.WriteLine("[CONTROL] ESC/Q=Salir | T=Enrolar | S=Guardar | R=Reiniciar | C=Color Piel | 1-6=Probar Trigger");
    }

    private static void LogCascadeStatus(string name, FaceDetector detector)
    {
        Console.WriteLine(detector.IsLoaded
            ? $"[CASCADA] {name}: OK ({Path.GetFileName(detector.ResolvedPath)})"
            : $"[CASCADA] {name}: NO DISPONIBLE");
    }

    private void OnTriggerActivated(ActiveTrigger trigger)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[TRIGGER ACTIVADO] {trigger}");
        Console.ResetColor();

        switch (trigger)
        {
            case ActiveTrigger.Trigger3_HighNoon:
                _highNoonEngine.Reset();
                _highNoonEngine.IsActive = true;
                break;

            case ActiveTrigger.Trigger5_SteepledFingertips:
                PlayMediaAudio("hava_nagila", isHavaNagila: true, durationSeconds: 6.0);
                break;

            case ActiveTrigger.Trigger6_MilitarySalute:
                PlayMediaAudio("himno_espana", isHavaNagila: false, durationSeconds: 8.3);
                break;
        }
    }

    private void OnTriggerDeactivated(ActiveTrigger trigger)
    {
        Console.WriteLine($"[TRIGGER FINALIZADO] {trigger}");

        if (trigger == ActiveTrigger.Trigger3_HighNoon)
        {
            _highNoonEngine.IsActive = false;
            _highNoonEngine.Reset();
        }
        else if (trigger is ActiveTrigger.Trigger5_SteepledFingertips or ActiveTrigger.Trigger6_MilitarySalute)
        {
            _audioManager.Stop();
        }
    }

    private void PlayMediaAudio(string baseName, bool isHavaNagila, double durationSeconds)
    {
        string mp3Path = Path.Combine(MediaDir, $"{baseName}.mp3");
        string wavPath = Path.Combine(MediaDir, $"{baseName}.wav");

        if (File.Exists(mp3Path))
        {
            _audioManager.Play(mp3Path, durationSeconds);
        }
        else if (File.Exists(wavPath))
        {
            _audioManager.Play(wavPath, durationSeconds);
        }
        else
        {
            _audioManager.PlayProceduralAnthem(isHavaNagila, durationSeconds);
        }
    }

    private void ProcessFrame(Mat frame)
    {
        _fpsCounter.Tick();
        double currentFps = _fpsCounter.Value;

        // Volteo espejo para interacción natural
        Cv2.Flip(frame, frame, FlipMode.Y);

        // 1. Normalización de imagen
        using Mat processed = FramePreprocessor.ProcessPipeline(frame);

        // 2. Detección multi-rostro (Frontal + Perfiles izquierdo y derecho)
        var rawDetections = DetectAllFacesWithProfiles(processed);
        var trackedFaces = _faceStabilizer.UpdateMultiFace(rawDetections, _expressionAnalyzer, processed, frame.Cols, frame.Rows);

        Rect? primaryFaceRect = trackedFaces.Count > 0 ? trackedFaces[0].SmoothedBounds : null;
        FaceRecognitionResult? primaryRecResult = null;
        ExpressionResult? primaryExpression = trackedFaces.Count > 0 ? trackedFaces[0].LastExpression : null;

        if (primaryFaceRect.HasValue)
        {
            Rect safeFace = ClampRect(primaryFaceRect.Value, frame.Cols, frame.Rows);
            using Mat faceCrop = processed[safeFace];
            var rawRec = _faceRecognizer.Predict(faceCrop);
            CheckEnrolmentSample(faceCrop);

            primaryRecResult = _faceStabilizer.Update(safeFace, rawRec, frame.Cols, frame.Rows);
            _stateManager.UpdateFaceResult(primaryRecResult);
        }
        else
        {
            _stateManager.OnNoFaceDetected();
            _expressionAnalyzer.Reset();
        }

        // 3. Segmentación de piel suprimiendo todos los rostros detectados
        using Mat skinMask = SkinSegmentation.CreateMask(frame, _activeColorSpace);

        foreach (var tf in trackedFaces)
        {
            Rect f = tf.SmoothedBounds;
            // Excluir cabeza y cuello para eliminar falsos positivos de dedos
            Rect suppression = ClampRect(
                new Rect(f.X - (int)(f.Width * 0.12), f.Y - (int)(f.Height * 0.15), (int)(f.Width * 1.24), (int)(f.Height * 1.45)),
                skinMask.Cols,
                skinMask.Rows);

            Cv2.Rectangle(skinMask, suppression, Scalar.Black, -1);
        }

        // 4. Análisis de contornos y clasificación de gestos
        var contours = ContourAnalyzer.AnalyzeAll(skinMask, minimumArea: 4_500);
        var classification = _gestureClassifier.ClassifyMultiContour(contours, trackedFaces, frame.Size());
        var stabilizedGesture = _gestureStabilizer.UpdateExtended(classification, frame.Cols, frame.Rows);

        GestureType activeGesture = stabilizedGesture.Gesture;
        Point? indexFingerTip = stabilizedGesture.IndexFingerTip;

        // 5. Evaluar los 6 disparadores interactivos
        ActiveTrigger activeTrigger = _stateManager.EvaluateTriggers(trackedFaces, activeGesture, primaryExpression);

        // 6. Renderizado de capas AR y efectos según el disparador activo
        if (activeTrigger == ActiveTrigger.Trigger4_PrayingPalms)
        {
            using Mat fullSkinMask = SkinSegmentation.CreateMask(frame, _activeColorSpace);
            _overlayRenderer.ReplaceBackground(frame, fullSkinMask, primaryFaceRect, "jesus_bg.jpg");
        }

        // Dibujar recuadros faciales y expresiones
        foreach (var tf in trackedFaces)
        {
            var rec = (tf == trackedFaces[0] && primaryRecResult is not null)
                ? primaryRecResult
                : new FaceRecognitionResult(false, "Persona", 0, double.PositiveInfinity, _faceRecognizer.SampleCount > 0);

            _overlayRenderer.DrawFace(frame, tf.SmoothedBounds, rec, tf.LastExpression);
        }

        // Dibujar contorno de mano estabilizado
        if (stabilizedGesture.SmoothedBounds.HasValue && activeGesture != GestureType.None)
        {
            _overlayRenderer.DrawGesture(frame, stabilizedGesture.SmoothedBounds.Value, activeGesture);
            _stateManager.TryProcessGesture(activeGesture);
        }
        else
        {
            _stateManager.TryProcessGesture(GestureType.None);
        }

        // Renderizar overlays específicos de cada Trigger
        switch (activeTrigger)
        {
            case ActiveTrigger.Trigger1_LionRoar:
                _overlayRenderer.DrawMemeOverlay(frame, "image_84e5ca.jpg");
                break;

            case ActiveTrigger.Trigger2_HandsOnHead:
                _overlayRenderer.DrawAnimatedGif(frame, "guy-funny.gif");
                break;

            case ActiveTrigger.Trigger3_HighNoon:
                _highNoonEngine.UpdateAndRender(frame, indexFingerTip);
                if (primaryFaceRect.HasValue)
                {
                    _overlayRenderer.DrawCowboyHat(frame, primaryFaceRect.Value);
                }
                break;

            case ActiveTrigger.Trigger5_SteepledFingertips:
                if (primaryFaceRect.HasValue)
                {
                    _overlayRenderer.DrawKippah(frame, primaryFaceRect.Value);
                }
                break;

            case ActiveTrigger.Trigger6_MilitarySalute:
                _overlayRenderer.DrawPictureInPictureVideo(frame, "video_1.mp4");
                _overlayRenderer.DrawDragoniteOverlay(frame, "dragonite_espanol.jpeg");
                break;
        }

        // 7. Renderizado del HUD
        _overlayRenderer.DrawHud(
            frame,
            _stateManager.CurrentMode,
            currentFps,
            _stateManager.CurrentUser,
            _stateManager.CurrentConfidence,
            _stateManager.LastGesture,
            _activeColorSpace.ToString().ToUpperInvariant(),
            _activePrompt,
            activeTrigger);
    }

    private List<(Rect Bounds, FaceOrientation Orientation)> DetectAllFacesWithProfiles(Mat processedGray)
    {
        var results = new List<(Rect Bounds, FaceOrientation Orientation)>();

        // 1. Rostros frontales
        Rect[] frontals = _faceDetector.DetectMultiScale(processedGray, 1.1, 5, HaarDetectionTypes.ScaleImage, new Size(60, 60));
        foreach (var r in frontals)
        {
            results.Add((r, FaceOrientation.Frontal));
        }

        // 2. Perfil derecho (imagen normal)
        if (_profileDetector.IsLoaded)
        {
            Rect[] rightProfiles = _profileDetector.DetectMultiScale(processedGray, 1.1, 4, HaarDetectionTypes.ScaleImage, new Size(60, 60));
            foreach (var r in rightProfiles)
            {
                if (!OverlapsExisting(results, r))
                {
                    results.Add((r, FaceOrientation.RightProfile));
                }
            }

            // 3. Perfil izquierdo (imagen volteada horizontalmente)
            using var flipped = new Mat();
            Cv2.Flip(processedGray, flipped, FlipMode.Y);
            Rect[] leftProfiles = _profileDetector.DetectMultiScale(flipped, 1.1, 4, HaarDetectionTypes.ScaleImage, new Size(60, 60));
            foreach (var r in leftProfiles)
            {
                Rect unflipped = new(processedGray.Cols - r.X - r.Width, r.Y, r.Width, r.Height);
                if (!OverlapsExisting(results, unflipped))
                {
                    results.Add((unflipped, FaceOrientation.LeftProfile));
                }
            }
        }

        return results;
    }

    private static bool OverlapsExisting(List<(Rect Bounds, FaceOrientation Orientation)> existing, Rect candidate)
    {
        foreach (var item in existing)
        {
            Rect inter = item.Bounds & candidate;
            double interArea = inter.Width * inter.Height;
            double minArea = Math.Min(item.Bounds.Width * item.Bounds.Height, candidate.Width * candidate.Height);
            if (minArea > 0 && (interArea / minArea) > 0.35)
                return true;
        }
        return false;
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
                    Console.WriteLine($"[ENROLAMIENTO] Completado para '{_enrolmentName}'. Presione 'S' para guardar.");
                    _activePrompt = $"Enrolado: {_enrolmentName}";
                    _enrolmentName = null;
                    Task.Delay(2500).ContinueWith(_ => _activePrompt = null);
                }
                else
                {
                    _activePrompt = $"Capturando muestras ({5 - _enrolmentTargetSamples}/5)...";
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ENROLAMIENTO] Error: {ex.Message}");
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
                _highNoonEngine.Reset();
                _audioManager.Stop();
                _activePrompt = "Sistema reiniciado";
                Task.Delay(1800).ContinueWith(_ => _activePrompt = null);
                break;

            case 'C':
                _activeColorSpace = _activeColorSpace == SkinSegmentation.ColorSpace.Hsv
                    ? SkinSegmentation.ColorSpace.YCrCb
                    : SkinSegmentation.ColorSpace.Hsv;
                _activePrompt = $"Espacio piel: {_activeColorSpace.ToString().ToUpperInvariant()}";
                Task.Delay(1500).ContinueWith(_ => _activePrompt = null);
                break;

            // Atajos de prueba directa de los 6 triggers (teclas 1..6)
            case '1': _stateManager.ActivateTrigger(ActiveTrigger.Trigger1_LionRoar, 5.0); break;
            case '2': _stateManager.ActivateTrigger(ActiveTrigger.Trigger2_HandsOnHead, 4.0); break;
            case '3': _stateManager.ActivateTrigger(ActiveTrigger.Trigger3_HighNoon, 15.0); break;
            case '4': _stateManager.ActivateTrigger(ActiveTrigger.Trigger4_PrayingPalms, 5.0); break;
            case '5': _stateManager.ActivateTrigger(ActiveTrigger.Trigger5_SteepledFingertips, 6.0); break;
            case '6': _stateManager.ActivateTrigger(ActiveTrigger.Trigger6_MilitarySalute, 8.3); break;
        }
    }

    private void BeginEnrolment()
    {
        Console.Write("\n[ENROLAMIENTO] Nombre a registrar: ");
        string? name = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        _enrolmentName = name;
        _enrolmentTargetSamples = 5;
        _activePrompt = $"Registrando a '{name}'...";
    }

    private void SaveModel()
    {
        try
        {
            _faceRecognizer.Save(FaceModelPath);
            _activePrompt = "Modelo guardado correctamente";
            Task.Delay(2000).ContinueWith(_ => _activePrompt = null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MODELO] Error al guardar: {ex.Message}");
        }
    }

    private static Rect ClampRect(Rect rect, int maxW, int maxH)
    {
        int x = Math.Clamp(rect.X, 0, maxW - 1);
        int y = Math.Clamp(rect.Y, 0, maxH - 1);
        int w = Math.Clamp(rect.Width, 1, maxW - x);
        int h = Math.Clamp(rect.Height, 1, maxH - y);
        return new Rect(x, y, w, h);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _loopEngine.OnFrameReceived -= ProcessFrame;
        _loopEngine.OnKeyPressed -= HandleKey;
        _stateManager.TriggerActivated -= OnTriggerActivated;
        _stateManager.TriggerDeactivated -= OnTriggerDeactivated;

        _audioManager.Dispose();
        _highNoonEngine.Dispose();
        _overlayRenderer.Dispose();
        _loopEngine.Dispose();
        _cameraManager.Dispose();
        _faceDetector.Dispose();
        _profileDetector.Dispose();
        _eyeDetector.Dispose();
        _smileDetector.Dispose();
        _expressionAnalyzer.Dispose();
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
  ==============================================================
    STUPIPANDI - SIMULADOR AR DE GESTOS Y RECONOCIMIENTO FACIAL
    Plataforma: .NET 10 | OpenCvSharp4 4.13 | 6 Triggers Activos
  ==============================================================");
        Console.ResetColor();
    }
}