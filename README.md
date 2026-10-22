# Abyss Tide 🌊

Tema para Windows Terminal: un fondo semitransparente con agua ASCII en 2D, vista de lado como una pecera, que **está viva**.

- **Mover la ventana:** al arrastrarla y soltarla de golpe, el agua choca contra la pared. Al sacudirla, hace olas. Con un tirón hacia abajo, el agua salta.
- **Escribir:** cada tecla deja caer una gota y Enter salpica.
- **Fallar un comando:** el agua se tiñe de rojo y se agita. Si sale bien, suben burbujas.
- **Pasar el ratón:** el cursor mueve el agua y los peces huyen.

El agua es un mar de caracteres que cambian más rápido donde está agitada. Tiene peces, una medusa, algas y arena.

![Vista previa: arrastre, tecleo, comando fallido, ratón y sacudida](preview.gif)

> La vista previa es la simulación real (`AbyssTide.cs`) renderizada fuera de Windows. El texto de la terminal es de ejemplo.

## Contenido del repo

```
AbyssTide.cs              motor: simulación, vida marina, capa transparente, menú de bandeja
abyss-tide.ps1            lanzador (compila AbyssTide.cs una vez, guarda la DLL y la ejecuta)
abyss-tide.config.json    ajustes (se aplican solos al guardar)
abyss-tide.fragment.json  esquema de color + perfiles para Windows Terminal
abyss-tide.hlsl           shader de respaldo (agua animada, sin interacción)
install.ps1               instalador / desinstalador
```

## Cómo funciona

Los shaders de Windows Terminal no guardan estado y no saben dónde está la ventana. Por eso un tema "normal" no puede reaccionar a nada. Abyss Tide tiene varias piezas:

| Pieza | Qué hace |
|---|---|
| Motor (`AbyssTide.cs`) | Corre en segundo plano y detecta cada ventana de Windows Terminal. Coloca justo debajo de cada una una capa transparente que no recibe clics. Simula el agua con ecuaciones de aguas someras en el marco de referencia de la ventana: al acelerarla aparece una pseudo-fuerza opuesta. Lo compila el propio Windows PowerShell 5.1, así que no hay que instalar nada. |
| Perfil **PowerShell · Abyss Tide** | Usa el esquema Abyss Tide con `opacity: 20` y sin acrílico, así que el fondo de la terminal deja ver el agua que hay debajo. |
| Hook del prompt | Unas líneas en tu `profile.ps1` que escriben `ok` o `error` en un archivo tras cada comando. El motor lo lee para teñir el agua. |
| `abyss-tide.hlsl` (opcional) | Shader de respaldo, para no ejecutar nada en segundo plano. Se usa con el perfil **PowerShell · Abyss Tide (solo shader)**. |

## Instalar

```powershell
# en la carpeta del repo
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

El instalador:
- copia todo a `%LOCALAPPDATA%\AbyssTide`
- añade el esquema y los dos perfiles mediante un *fragment* de Windows Terminal, sin tocar tu `settings.json`
- añade el hook del prompt al final de tu `profile.ps1`, tanto en Windows PowerShell como en PowerShell 7 (sáltalo con `-NoShellHook`)
- crea un acceso directo en Inicio para que arranque con Windows (sáltalo con `-NoAutostart`)
- lo arranca

Después, reinicia Windows Terminal y abre el perfil **PowerShell · Abyss Tide**. Para usarlo siempre, márcalo como perfil predeterminado. Si prefieres PowerShell 7, cambia su línea de comandos a `pwsh.exe`.

Desinstalar: `.\install.ps1 -Uninstall`. Quita todo, incluido el hook de tu perfil.

## Menú de la bandeja

Haz clic derecho en el icono de la ola, junto al reloj:

- **Paleta:**
  - Abismo
  - Tropical
  - Día / noche: turquesa de día y azul profundo de noche, según la hora
  - Lava
  - Tóxico
  - Matrix
  - Synthwave
  - Personalizada: usa los colores de la config
- **Sensibilidad, nivel del agua y peces:** se cambian en vivo. Al subir el nivel, el agua se llena poco a poco.
- **Opciones que se activan o desactivan:**
  - gotas al escribir
  - ondas con el ratón
  - fondo marino
  - medusa
  - agua roja si un comando falla
  - modo foco
  - modo eco
- **Guardar estos ajustes:** escribe lo elegido en la config.
- **Abrir configuración, Pausar, Salir.**

## Ajustes

`%LOCALAPPDATA%\AbyssTide\abyss-tide.config.json`. Los cambios se aplican solos al guardar el archivo. Si tiene un error, aparece un aviso y se mantienen los ajustes anteriores.

| Clave | Efecto |
|---|---|
| `Palette` | `abyss`, `tropical`, `daynight`, `lava`, `toxic`, `matrix`, `synthwave`, `custom` |
| `MoveSensitivity` | 2.0 = salpica mucho; 0.4 = más calmado |
| `FillLevel` | cuánta agua hay (0.05–0.9) |
| `Damping`, `Gravity` | qué tan rápido se calma y qué tan rápidas son las olas |
| `TiltAmount` | cuánto se inclina el agua según dónde esté la ventana en la pantalla |
| `SprayAmount`, `IdleDrips` | salpicaduras y gotas cuando está quieta |
| `Fish`, `Jellyfish`, `Seafloor` | peces por ventana, medusa, arena y algas |
| `TypingDrops`, `MouseRipples`, `ShellSignals` | reacciones al teclado, al ratón y a los comandos |
| `FocusMode`, `FocusDim`, `FocusDelay` | atenúa el agua mientras escribes y la recupera tras unos segundos sin teclear |
| `EcoMode`, `EcoFps` | baja los fps con el agua en calma o la batería por debajo del 25 % |
| `CharMode` | `sea` (mar de caracteres) o `ramp` (puntos por densidad) |
| `BackgroundAlpha`, `WaterAlpha` | opacidad del fondo y del agua (sube `BackgroundAlpha` si cuesta leer) |
| `BackgroundColor`, `SurfaceColor`, `DeepColor`, `FoamColor`, `SeaChars`, `SurfaceChars` | colores y caracteres con `Palette: custom`; prueba `SeaChars: "01"` |
| `FontPx`, `FontFamily` | tamaño y fuente de los caracteres del agua |
| `TopInsetPx` | alto de la barra de pestañas; el agua empieza debajo |

La transparencia final combina dos cosas: el `opacity` del perfil en Windows Terminal y el `BackgroundAlpha` de la config.

## Privacidad

"Gotas al escribir" y el modo foco usan un hook de teclado de bajo nivel. Ese hook **solo cuenta pulsaciones cuando una ventana de Windows Terminal está en primer plano**. Solo distingue Enter y Retroceso; no guarda ni envía ninguna tecla. Si lo prefieres, desactiva ambas opciones: el hook se desinstala al momento. Algunos antivirus pueden avisar de este tipo de hook.

## Opcional: barra de pestañas a juego

Los fragments no pueden traer temas de ventana. Si quieres la barra a juego, pega esto en `settings.json`:

```jsonc
"theme": "Abyss Tide",
"themes": [
  {
    "name": "Abyss Tide",
    "window": { "applicationTheme": "dark" },
    "tabRow": { "background": "#06121EFF", "unfocusedBackground": "#06121EFF" },
    "tab":    { "background": "#0B2133FF", "unfocusedBackground": "#06121EFF", "showCloseButton": "hover" }
  }
]
```

## Requisitos y notas

- Windows 10 1703 o posterior / Windows 11, y Windows Terminal 1.12 o posterior (necesario para `opacity` sin acrílico).
- La primera ejecución tarda unos segundos porque compila. Las siguientes usan la DLL guardada en `%LOCALAPPDATA%\AbyssTide\cache`.
- Funciona con varias ventanas de Terminal a la vez, monitores con distintos DPI, escritorios virtuales y ventanas minimizadas. El agua roja va a la ventana que tiene el foco.
- El shader de respaldo no tiene peces ni interacción: es solo una animación.
