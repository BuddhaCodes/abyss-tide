# Ideas

Todo esto reutiliza el motor de Abyss Tide: la capa transparente pegada a una ventana, el seguimiento de su movimiento, el teclado, el ratón y la señal de error del prompt. Solo cambia lo que se dibuja.

## Animaciones que reaccionan en la terminal

- [ ] **Blobs (lámpara de lava):** metaballs de colores suaves que flotan, se fusionan y se separan.
  - Se quedan atrás al arrastrar la ventana y se aplastan contra el borde.
  - El cursor los atrae como un imán.
  - Cada tecla suelta una gotita que sube y se une al blob más cercano.
  - Un comando fallido tiñe un blob de rojo y lo parte en dos.
  - Se dibuja con degradados suaves, no con ASCII.
- [ ] **Aurora boreal:** cintas de luz que ondulan despacio en la parte alta de la terminal.
  - Escribir rápido las aviva y estar inactivo las apaga.
  - Mover la ventana las hace ondear como una cortina.
- [ ] **Campo de flujo:** miles de partículas finas que siguen corrientes invisibles y dejan estelas.
  - La ventana en movimiento revuelve el campo y el ratón crea remolinos.
- [ ] **Tinta en agua:** fluido 2D real (humo o tinta) que se arremolina al arrastrar el cursor o la ventana.
  - Cada comando suelta una gota de tinta, verde si sale bien y roja si falla.
- [ ] **Constelación:** puntos que se conectan con líneas finas cuando están cerca.
  - El cursor los atrae y teclear añade estrellas.
  - Un error rompe las conexiones con un destello.
- [ ] **Salto al hiperespacio:** campo de estrellas en profundidad.
  - Teclear acelera, Enter salta al hiperespacio y un error provoca un parpadeo rojo de alarma.
- [ ] **Reacción-difusión:** patrones orgánicos tipo coral o piel de animal que crecen muy despacio.
  - Cada tecla siembra una mancha.
  - Es hipnótico y muy elegante en tonos sobrios.
- [ ] **Vidrio líquido:** reflejos de luz como en el fondo de una piscina.
  - La luz se mueve con la ventana y el ratón actúa como una linterna.
- [ ] **Ventana de gelatina:** un brillo alrededor del borde de la terminal que tiembla como gelatina al moverla o redimensionarla.
- [ ] **Motor con modos:** convertir Abyss Tide en una plataforma de animaciones intercambiables (agua, blobs, aurora…) que se eligen desde la bandeja.

## Juguetes de escritorio útiles

- [ ] **Ventanas con física:** al lanzar una ventana con un gesto rápido, se desliza con inercia y rebota en los bordes. Al soltarla cerca de un borde, se ajusta.
- [ ] **Papelera hambrienta:** un monstruo en la esquina de la pantalla al que le arrastras archivos para borrarlos. Mastica según el tamaño del archivo.
- [ ] **Tareas pendientes con peso:** cada tarea es un bloque que cae y se apila en el borde inferior de la pantalla. Si la pila crece demasiado, vas atrasado. Al completarlas, explotan.
- [ ] **Portapapeles visible:** cada cosa que copias cae como una tarjeta a una bandeja lateral, y puedes arrastrarla de vuelta para pegarla.
- [ ] **Notificaciones con personalidad:** una criatura ASCII entra caminando a la pantalla y te da el aviso, en lugar del cuadro gris.

## Para mi entorno

- [ ] **Widget de Zebar:** muestra el estado de la animación, el último comando y si falló.
- [ ] **GlazeWM:** cambiar de espacio de trabajo agita la animación de todas las terminales.
- [ ] **SignalRGB:** los LEDs del teclado y de la torre siguen los colores de la animación; se ponen rojos cuando falla un comando y laten cuando escribes rápido.
- [ ] **Unity y Godot:** los errores de compilación del editor reaccionan igual que los de la terminal.
