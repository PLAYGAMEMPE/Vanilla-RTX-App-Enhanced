# Vanilla RTX App — Enhanced (Fork)

> **⚠️ Este es un fork no oficial y personal.** No es la app oficial Vanilla RTX App, **no está afiliado, respaldado ni mantenido** por su autor original, y **no está monetizado de ninguna forma**. Lee [Origen y Licencia](#origen-y-licencia) antes de cualquier otra cosa.

Este repositorio es un fork personal de **[Cubeir/Vanilla-RTX-App](https://github.com/Cubeir/Vanilla-RTX-App)** — una app para Windows todo-en-uno que facilita activar, actualizar y ajustar el trazado de rayos (RTX) en Minecraft Bedrock. Todo el crédito por el concepto original, el diseño y la inmensa mayoría del código pertenece a **[Cubeir](https://github.com/Cubeir)**.

Este fork existe para llevar un pequeño conjunto de cambios personales sobre el proyecto original — se mantiene sincronizado con el upstream y se publica aquí únicamente por transparencia (para que los cambios sean visibles y auditables), no para presentarse como una alternativa o un reemplazo del original.

**Si buscas la app oficial, entra aquí: [github.com/Cubeir/Vanilla-RTX-App](https://github.com/Cubeir/Vanilla-RTX-App)**

---

## Qué cambia este fork

Todo lo demás — cada función, cada ventana, cada herramienta de resource packs — funciona exactamente igual que en el proyecto original. Los cambios aquí son aditivos y de alcance acotado:

- **Localización**: una capa de recursos de texto (`Core.Loc`) más archivos `.resw` en `en-US` / `es-ES`, para que la interfaz pueda leerse en español.
- **Modo portátil / sin empaquetar**: `AppStorage`, `PackageContext` y `AppRestart` sustituyen las APIs de `Windows.Storage.ApplicationData` (que exigen MSIX) donde hace falta, para que la app también pueda correr como un único `.exe` autocontenido sin instalador, junto a la distribución empaquetada (MSIX) del original.
- **`PackBackupService`**: una copia de seguridad automática del resource pack, hecha al primer toque, antes de que el Tuner modifique sus archivos en el sitio — así el ajuste siempre se puede deshacer aunque te olvides de exportar antes.
- **Corrección de trim-safety**: la persistencia JSON de `AppStorage` usa generación de código (`System.Text.Json` source generation) en vez de la API basada en reflexión, eliminando las advertencias del analizador de trimming en el build portátil.
- **DLSS Swapper**: sin cambios de comportamiento respecto al original — ahora también muestra la ruta del `nvngx_dlss.dll` del juego y dos accesos directos para abrir esa carpeta y la carpeta de caché de la app directamente en el Explorador, para copias de seguridad o inspección manual.

## Compilar desde el código fuente

Este es un proyecto WinUI 3 / Windows App SDK dirigido a `net10.0-windows`. Necesitas:

- Visual Studio 2022 (o posterior) con las cargas de trabajo **.NET Desktop Development** y **Windows App SDK**, o el SDK de .NET 10 + Windows App SDK instalados por separado
- Windows 10 (19041+) o Windows 11

Abre `src/Vanilla RTX App.csproj` (o una solución que lo referencie) y compílalo/ejecútalo con `dotnet build` / `dotnet publish`, o desde Visual Studio. Consulta la documentación del repositorio original para una guía completa de funciones — este fork no cambia nada de eso.

## Reportar problemas

Los errores o ideas sobre **funcionalidad heredada del original** (todo lo que no esté listado arriba en "Qué cambia este fork") deben reportarse en los [issues del repositorio original](https://github.com/Cubeir/Vanilla-RTX-App/issues) — ahí es donde el mantenedor que puede actuar sobre ellos los verá. Los problemas específicos de los cambios listados arriba son bienvenidos aquí.

---

## Origen y Licencia

- **Proyecto y copyright originales**: [Vanilla RTX App](https://github.com/Cubeir/Vanilla-RTX-App), Copyright (c) junio 2025 **Cubeir**.
- **Este repositorio**: un fork no oficial / build personal, mantenido de forma independiente a Cubeir, publicado por transparencia sobre los cambios concretos hechos sobre el original.
- **Sin reclamo de autoría**: este fork no reclama crédito ni propiedad sobre el proyecto original, su diseño, su marca ("Vanilla RTX App", su ícono/logo), ni los resource packs que gestiona ([Vanilla RTX](https://github.com/Cubeir/Vanilla-RTX)). La inmensa mayoría del código de este repositorio fue escrita por Cubeir y otros colaboradores del proyecto original.
- **Sin monetización**: este fork no se vende, no lleva publicidad, no solicita donaciones, y no se distribuye a través de la Microsoft Store ni ninguna otra tienda. Si quieres apoyar el trabajo del creador original, hazlo aquí: [Cubeir en Ko-fi](https://ko-fi.com/cubeir) / [Discord de Vanilla RTX](https://discord.gg/A4wv4wwYud).
- **Licencia**: igual que el original, este repositorio está licenciado bajo la **GNU General Public License v3.0** (ver [`LICENSE.txt`](LICENSE.txt)) — la misma licencia copyleft bajo la que se distribuye el proyecto original. La GPLv3 permite explícitamente redistribuir versiones modificadas como esta, siempre que se conserven la misma licencia, los avisos de copyright y la disponibilidad del código fuente, que es exactamente lo que hace este repositorio. Los componentes de terceros (Newtonsoft.Json, Magick.NET, WinUIEx) conservan sus propias licencias, enlazadas en `LICENSE.txt`.
- Conforme a la GPLv3 y a la cortesía habitual: **esta es una versión modificada del software original, señalada como tal, y no se presenta como un lanzamiento oficial.**

"Minecraft" es una marca registrada de Mojang Studios / Microsoft. Este proyecto **no es un producto oficial de Minecraft** y no cuenta con la aprobación ni el respaldo de Mojang o Microsoft.
