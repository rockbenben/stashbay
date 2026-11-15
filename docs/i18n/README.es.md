# StashBay

> Una bandeja ligera para trasladar archivos entre ventanas: arrastra hacia adentro, arrastra hacia afuera y listo, sin necesidad de colocar ventanas lado a lado.

[简体中文](../../README.md) · [English](../../README.en.md) · [繁體中文](README.zh-Hant.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · **Español** · [Français](README.fr.md) · [Deutsch](README.de.md) · [Português](README.pt.md) · [Русский](README.ru.md) · [Italiano](README.it.md) · [العربية](README.ar.md) · [हिन्दी](README.hi.md) · [বাংলা](README.bn.md) · [ไทย](README.th.md) · [Türkçe](README.tr.md) · [Tiếng Việt](README.vi.md) · [Bahasa Indonesia](README.id.md)

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](../../LICENSE) [![365 Open Source Plan #048](https://img.shields.io/badge/365%20Open%20Source%20Plan-%23048-1f6feb)](https://github.com/rockbenben/365opensource)

[⬇ Descargar última versión](https://github.com/rockbenben/stashbay/releases/latest)

![Bandeja compacta de StashBay: una tarjeta flotante siempre visible para guardar y transferir archivos](../../assets/shelf.png)

Estación de transferencia de archivos ligera para el escritorio de Windows. Se ejecuta completamente en local, sin cargas en la nube, sin conexión a Internet y sin necesidad de registro. Funciona tanto como una bandeja minimalista de usar y cerrar como una mesa de trabajo completa con copias de seguridad de instantáneas y restauración en un solo clic.

---

## Especificaciones de soporte

| Dimensión | Especificación |
| :--- | :--- |
| **Sistema operativo** | Windows 10 (1809+) / Windows 11 (soporte nativo para x64 y ARM64) |
| **Dependencias** | Las versiones oficial instalable y portable incluyen el entorno de ejecución; las versiones compactas requieren .NET 10 Desktop Runtime |
| **Objetos admitidos** | Archivos en cualquier formato y carpetas multinivel (conserva la jerarquía relativa) |
| **Métodos de entrada** | Selección múltiple del Explorador de Windows, lista de archivos del portapapeles, gesto de agitar el ratón |
| **Destinos de escritura** | Discos locales, carpetas compartidas de red (UNC / unidades asignadas), repositorios Git |
| **Archivos de gran tamaño** | Admite archivos individuales de más de 2 GB y rutas largas de Windows (> 260 caracteres) |
| **Permisos** | Se ejecuta de forma predeterminada con permisos de usuario estándar (instalación en `%LOCALAPPDATA%\Programs`); inicio automático con privilegios opcional |
| **Ubicación de datos** | Guardado de forma predeterminada en `%LOCALAPPDATA%\StashBay\` (totalmente compatible con la carpeta heredada `FileStash`) |

---

## Inicio rápido

1. **Obtener el programa**: Descarga el instalador `StashBay-vx.y.z-setup.exe` (no requiere permisos de administrador) o la versión portable `StashBay-vx.y.z-win-x64.zip` desde [Releases](https://github.com/rockbenben/stashbay/releases/latest).
2. **Abrir la bandeja**: Pulsa el atajo <kbd>Win+Alt+S</kbd> y la minibandeja se abrirá al instante junto al cursor del ratón en la pantalla activa.
3. **Guardar archivos**: Selecciona los archivos en el Explorador de archivos y vuelve a pulsar <kbd>Win+Alt+S</kbd> (o arrástralos a la bandeja, o agita el ratón) para que queden guardados inmediatamente.
4. **Arrastrar todo de una vez**: Arrastra el tirador `[⤹ Total X elementos]` en la parte inferior para transferir todos los archivos al destino o aplicación en un solo movimiento. Al quedar vacía, la bandeja se desvanece y se cierra automáticamente.

---

## Características principales

### Bandeja de tránsito minimalista (experiencia DropPoint)
* **Exportación en lote con un solo gesto**: Arrastra el asa con el contador para enviar todos los archivos a la vez al Explorador, chats de mensajería o formularios web.
* **Cierre automático al vaciar**: Los archivos transferidos se retiran automáticamente de la bandeja; al quedar vacía, la ventana desaparece suavemente.
* **Aparición junto al cursor**: El atajo global detecta en qué monitor se encuentra el cursor del ratón y despliega la bandeja en esa posición.
* **Agitar para guardar**: Al arrastrar archivos en el Explorador, agita el ratón ligeramente para abrir la bandeja y depositar la selección al instante.
* **Bola flotante magnética**: Un pequeño botón junto al borde de la pantalla que se abre con un clic o arrastre corto y se adhiere magnéticamente al borde.
* **Múltiples bandejas independientes**: Haz doble clic en el icono del área de notificación o pulsa <kbd>Ctrl+Shift+N</kbd> para abrir nuevas bandejas temporales; las bandejas vacías se limpian solas.

### Custodia de cambios de ramas Git y restauración segura
* **Detección inteligente de repositorios**: Reconoce la raíz del repositorio de los archivos guardados; al cambiar de rama, muestra un aviso para restaurarlos a su ubicación original.
* **Comprobación de bloqueos antes de escribir**: Utiliza el Administrador de reinicio de Windows para detectar archivos abiertos (por Word, Excel o IDE) y evitar escrituras corruptas.
* **Instantáneas de seguridad**: Antes de sobreescribir cualquier archivo, se realiza una copia de seguridad en un almacén de blobs local con restauración inmediata.
* **Omisión por hash idéntico**: Si el contenido no ha cambiado, omite la escritura para conservar la fecha de modificación original y evitar recompilaciones innecesarias.

### Mesa de trabajo completa
* **Cuatro modos de vista**: Vista en árbol (jerárquica), lista plana, agrupada por carpeta de origen y mosaico de iconos grandes, con memoria de estado.
* **Modos de restauración flexibles**: Restauración en la ubicación original, directorio especificado o nuevo directorio raíz; estructura con ruta relativa completa, solo nombre o aplanada.
* **Vista previa de conflictos**: Revisa en detalle las acciones antes de aplicar y elige entre preguntar, sobreescribir, omitir o renombrar automáticamente.
* **Historial de operaciones**: Registro detallado de cada lote de restauración; en caso de fallo parcial, puedes reintentar únicamente los elementos con error.

### 18 idiomas nativos y temas
* Compatibilidad nativa con 18 idiomas (100% de coincidencia de claves verificado por pruebas automatizadas) y soporte completo para diseño en espejo RTL en árabe (ar-SA).
* Adaptación perfecta a los modos claro y oscuro de Windows con paleta neutra de grafito.

---

## Atajos de teclado frecuentes

| Atajo | Ámbito | Descripción |
| :--- | :--- | :--- |
| <kbd>Win+Alt+S</kbd> | Global | Abre la minibandeja; si hay archivos seleccionados en el Explorador, los guarda directamente |
| <kbd>Win+Alt+W</kbd> | Global | Abre la mesa de trabajo completa |
| <kbd>Win+Alt+V</kbd> | Global | Añade directamente a la bandeja los archivos del portapapeles |
| <kbd>Ctrl+V</kbd> | En ventana | Pega los archivos del portapapeles en la bandeja activa |
| <kbd>Ctrl+Shift+N</kbd> | En ventana | Crea una nueva bandeja vacía independiente |
| <kbd>F11</kbd> / <kbd>Ctrl+E</kbd> | En ventana | Alterna entre la minibandeja y la mesa de trabajo completa |
| <kbd>Esc</kbd> | En ventana | En la mesa de trabajo, pliega a bandeja; en la bandeja, oculta al área de notificación |
| <kbd>Delete</kbd> | Lista | Elimina los elementos seleccionados de la bandeja |

> [!TIP]
> Los atajos globales se pueden personalizar o desactivar en Configuración. Si una combinación entra en conflicto con otra aplicación, StashBay avisará con un mensaje explícito.

---

## Parámetros de línea de comandos (CLI)

`StashBay.exe` ofrece una interfaz de línea de comandos pensada para integrarse con lanzadores externos, scripts o flujos de trabajo automatizados:

| Opción | Descripción |
| :--- | :--- |
| `--add <rutas...>` | Añade uno o más archivos o carpetas a la bandeja |
| `--group <nombre>` | Se usa junto con `--add` para especificar la bandeja de destino (se crea si no existe) |
| `--apply-here <directorio>` | Vuelca los archivos de la bandeja en el directorio indicado |
| `--apply-original` | Restaura los archivos en su ruta relativa original (ideal para Git) |
| `--show` | Abre y muestra la ventana principal |
| `--minimized` | Inicia minimizado en el área de notificación (para el inicio automático del sistema) |
| `--quit` | Cierra de forma segura la instancia en ejecución |
| `--unregister-shell` | Desregistra el menú contextual, el acceso directo Enviar a y el inicio automático |

---

## Limitaciones conocidas

* **Exclusivo para Windows**: Integrado profundamente con el Shell de Windows, la composición DWM y el control de accesos; no hay versión multiplataforma prevista.
* **Instantáneas en lugar de accesos directos**: Al guardar archivos se crea una copia del contenido al instante; los cambios posteriores en los archivos originales no alteran la bandeja.
* **Mecanismo de instancia única**: Ejecutar de nuevo la aplicación pasa los parámetros al proceso principal ya activo en la bandeja, evitando procesos redundantes.
* **Inicio automático como administrador**: Por defecto se ejecuta con permisos de usuario estándar. Si necesitas arrastrar archivos a ventanas con privilegios elevados, activa el inicio como administrador en Configuración y aprueba el diálogo de UAC. [Guía de inicio automático](../admin-startup.md).

---

## Sobre el Plan 365 de código abierto

Proyecto **#048** del [Plan 365 de código abierto](https://github.com/rockbenben/365opensource) — una persona + IA, más de 300 proyectos de código abierto en un año.

[Envía tu idea →](https://365.aishort.top/) · [Discord](https://discord.gg/PZTQfJ4GjX) · [Telegram](https://t.me/aishort_top)
