# Simulador de Gestos y Reconocimiento Facial (Stupipandi)

Aplicación de visión artificial interactiva en tiempo real desarrollada en **C# / .NET 10** utilizando **OpenCvSharp4**. El sistema combina autenticación biométrica facial, detección de puntos de referencia (ojos y sonrisa) y reconocimiento de gestos de manos mediante segmentación cromática y análisis de defectos de convexidad.

---

## Características Principales

1. **Autenticación Biométrica Facial (RMS Vector Matcher)**:
   - Detección de rostros en tiempo real mediante clasificadores en cascada de Haar (`haarcascade_frontalface_default.xml`).
   - Normalización de iluminación por ecualización de histograma en escala de grises.
   - Vectorización de características de 64x64 píxeles y comparación por distancia cuadrática media (RMS).
   - Enrolamiento interactivo en caliente con captura de ráfaga (5 muestras por usuario) y serialización persistente en XML (`Assets/Models/face_model.xml`).

2. **Detección de Expresiones y Puntos Faciales**:
   - Detección precisa de ojos dentro de la región de interés (ROI) facial (`haarcascade_eye.xml`).
   - Detección de sonrisa (`haarcascade_smile.xml`) con indicador en pantalla.

3. **Reconocimiento de Gestos de Mano**:
   - Segmentación de piel con soporte para espacios cromáticos **HSV** y **YCrCb** intercambiables en caliente.
   - Supresión automática de la región del rostro en la máscara binaria para prevenir falsos positivos generados por la cabeza o el cuello.
   - Extracción de contorno exterior mayor, cálculo de envolvente convexa (*Convex Hull*) y defectos de convexidad (*Convexity Defects*).
   - Clasificación semántica de gestos:
     - **Puño cerrado** (*Fist*)
     - **1 dedo** (Índice)
     - **2 dedos** (Paz / Victoria)
     - **3 dedos**
     - **4 dedos**
     - **Mano abierta** (5 dedos)

4. **Máquina de Estados de Acceso (`ApplicationStateManager`)**:
   - `AwaitingFace`: En espera de detectar un rostro.
   - `TrainingRequired`: El modelo facial no posee muestras entrenadas; guía al usuario para enrolarse.
   - `AccessDenied`: Rostro detectado pero no reconocido (distancia superior al umbral). Bloquea la interacción por gestos.
   - `GestureInteraction`: Usuario autenticado. Desbloquea el reconocimiento y procesamiento de gestos en tiempo real.

5. **Visualización y HUD**:
   - Barra superior con estado del sistema, usuario autenticado, FPS estabilizado (EMA) y espacio de color activo.
   - Barra inferior con guía de atajos de teclado.
   - Notificaciones y banners emergentes en pantalla durante el enrolamiento.

---

## Requisitos del Sistema

- **SDK**: [.NET 10.0](https://dotnet.microsoft.com/)
- **Cámara**: Webcam USB o integrada compatible con DirectShow / OpenCV.
- **Sistema Operativo**: Windows 10/11 x64.

---

## Estructura del Proyecto

```
Simulador-de-Gestos-y-Reconocimiento-Facial/
│
├── README.md                           # Documentación principal del repositorio
│
└── Stupipandi/
    ├── Stupipandi.csproj               # Configuración del proyecto .NET 10 y NuGet
    ├── Stupipandi.slnx                 # Solución de Visual Studio
    ├── Program.cs                      # Orquestador principal y bucle de eventos
    │
    ├── Assets/
    │   ├── Cascades/                   # Archivos XML de Haar Cascade
    │   │   ├── haarcascade_frontalface_default.xml
    │   │   ├── haarcascade_eye.xml
    │   │   └── haarcascade_smile.xml
    │   └── Models/                     # Almacenamiento del modelo de rostros
    │       └── face_model.xml          # Generado al guardar con la tecla 'S'
    │
    ├── Biometrics/
    │   ├── FaceDetector.cs             # Envoltorio seguro para CascadeClassifier
    │   ├── FaceRecognizer.cs           # Clasificador biométrico vectorial y persistencia XML
    │   └── OverlayRenderer.cs          # Renderizado de HUD, rectángulos y etiquetas
    │
    ├── Gestures/
    │   ├── ContourAnalyzer.cs          # Detección de contornos, envolvente y defectos
    │   ├── GestureClassifier.cs        # Traducción de defectos a tipos de gestos
    │   └── SkinSegmentation.cs         # Generación de máscaras binarias (HSV / YCrCb)
    │
    ├── Infrastructure/
    │   ├── CameraManager.cs            # Control del hardware de captura y ciclo de vida Mat
    │   ├── FramePreprocessor.cs        # Pipeline de escala de grises y ecualización
    │   ├── FpsCounter.cs               # Cálculo de FPS con promedio móvil exponencial (EMA)
    │   └── VideoLoopEngine.cs          # Ciclo sincrónico de adquisición y despacho de eventos
    │
    └── State/
        └── ApplicationStateManager.cs  # Gestión del modo de autenticación y acceso
```

---

## Compilación y Ejecución

### Compilación limpia:
```powershell
cd Stupipandi
dotnet build
```

### Ejecutar la aplicación:
```powershell
dotnet run
```

---

## Controles en Tiempo de Ejecución

| Tecla | Acción | Descripción |
| :---: | :--- | :--- |
| **`ESC`** / **`Q`** | **Salir** | Cierra la ventana y libera los recursos de hardware ordenadamente. |
| **`T`** | **Enrolar rostro** | Solicita el nombre en la consola y captura 5 muestras del rostro frente a la cámara. |
| **`S`** | **Guardar modelo** | Serializa el modelo entrenado a `Assets/Models/face_model.xml`. |
| **`R`** | **Reiniciar** | Restablece el estado de autenticación y los filtros de gestos. |
| **`C`** | **Espacio de color** | Alterna entre **HSV** y **YCrCb** para optimizar la detección de piel según la iluminación. |

---

## Licencia

Distribuido bajo los términos de la licencia especificada en el archivo [LICENSE](LICENSE).
