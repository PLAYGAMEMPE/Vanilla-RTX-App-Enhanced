# Auditoria Tecnica de Portabilidad

Fecha: 2026-09-15

Rama de trabajo: `audit-portable-distribution`

## Resultado

La distribucion recomendada y validada es un unico `.exe` portable x64, no un MSIX ni una carpeta con dependencias. El artefacto final se genera en `artifacts/portable-x64/Vanilla RTX App.exe`.

Artefacto validado:

| Dato | Valor |
| --- | --- |
| Tamano | 170522719 bytes |
| SHA-256 | `76C627D02B400CC65B675FB03ACEEF722B29480259AA488243567577A19149D1` |
| Firma del exe | `NotSigned` |
| Arquitectura | x64 |

## Arquitectura auditada

| Aspecto | Resultado |
| --- | --- |
| Tipo | WinUI 3 desktop, .NET 10, Windows App SDK 2.4.0 |
| TFM | `net10.0-windows10.0.19041.0` |
| Runtime portable | `win-x64` |
| Empaquetado | Unpackaged, self-contained, single-file |
| .NET Runtime | Incluido dentro del exe |
| Windows App Runtime | Incluido dentro del exe |
| VC++ 2015-2022 x64 | Incluido y cargado desde el exe |
| MSIX para usuario final | No requerido |
| WebView2 Runtime | No requerido por el codigo actual; no hay uso de WebView2 |

La configuracion esta principalmente en `src/Vanilla RTX App.csproj` y `src/Properties/PublishProfiles/Portable-x64.pubxml`.

## Causas raiz

### Politica global de DLL incompatible con WinUI

El inicializador `NativeDependencyBootstrap` ejecutaba `SetDefaultDllDirectories` y agregaba solo `Runtimes/win-x64/vcredist`. En una publicacion single-file, WinUI y Windows App SDK residen en el directorio de autoextraccion de .NET. Ese cambio global de politica hacia que WinUI no resolviera recursos `ms-appx`.

El fallo fue reproducido con cierre inmediato `0xC000027B` y:

```text
Microsoft.UI.Xaml.Markup.XamlParseException (0x802B000A)
Cannot locate resource from 'ms-appx:///MainWindow.xaml'
```

Una prueba A/B con el mismo binario confirmo que sin esa rutina aparecia la ventana y con ella fallaba. Agregar la raiz de extraccion a esa politica tampoco resolvio el problema: la llamada global era la causa.

Correccion: `src/Core/NativeDependencyBootstrap.cs` ya no modifica la politica del cargador. Precarga por ruta absoluta las diez DLL VC++ incluidas. Los modulos posteriores de Windows App SDK resuelven las DLL por nombre desde la lista de modulos cargados, sin afectar a WinUI.

### Nombre del PRI ligado al nombre del exe

Una publicacion unpackaged con PRI por defecto funciona como `Vanilla RTX App.exe`, pero falla tras renombrarse porque busca un PRI asociado al nombre nuevo. Se reprodujo el caso y se comprobo que el mismo exe abre y cierra correctamente bajo un nombre de release al usar `resources.pri` estable.

Correccion: el perfil portable fija `ProjectPriFileName=resources.pri`. El cambio queda limitado al flujo portable y no altera el flujo MSIX de desarrollo.

### Runtime VC++ no garantizado en publish

Windows App SDK incluye componentes nativos que requieren VC++ 2015-2022. El equipo de desarrollo ya tenia ese runtime instalado, por lo que podia ocultar el problema. El perfil ahora marca las DLL de `src/Runtimes/win-x64/vcredist` para copiarse durante publish y el inicializador las precarga de forma explicita.

La inspeccion del proceso mostro las diez DLL cargadas desde `%TEMP%/.net/.../Runtimes/win-x64/vcredist`, no desde `System32`. Las DLL fuente tienen firma Authenticode `Valid` de Microsoft.

### APIs de almacenamiento que requieren identidad MSIX

`AssetUpdater` aun usaba `Windows.Storage.ApplicationData.Current`, que requiere identidad de paquete. Se sustituyo por `AppStorage`, que persiste en `%LocalAppData%/Vanilla RTX App` y funciona en la aplicacion unpackaged.

### Configuracion dependiente del equipo de desarrollo

`AppxPackageDir` apuntaba a `C:\Users\Administrator\Desktop`. Se reemplazo por `artifacts/msix` relativo al proyecto. Tambien se alineo `TargetPlatformMinVersion` con el TFM y la documentacion: Windows 10 build 19041.

## Cambios realizados

- `src/Core/NativeDependencyBootstrap.cs`: precarga local de VC++ sin `SetDefaultDllDirectories`, mas diagnostico temprano si el runtime incluido no se puede cargar.
- `src/Properties/PublishProfiles/Portable-x64.pubxml`: self-contained, Windows App SDK self-contained, single-file, PRI estable y copiado explicito de VC++.
- `src/Vanilla RTX App.csproj`: contenido de recursos marcado para publish single-file, directorio MSIX relativo y requisito minimo coherente.
- `src/Modules/Alchitex/Core/AssetUpdater.cs`: almacenamiento portable mediante `AppStorage`.
- `src/App.xaml.cs`: log de arranque con ejecutable, directorio base y Windows App SDK, mas cuadro de error bilingue ante errores fatales.
- `global.json`: SDK reproducible, .NET 10.0.401 con ultimo parche.
- `scripts/publish-portable.ps1`: publicacion limpia y validacion de un unico PE x64.
- `scripts/test-portable.ps1`: smoke test externo y renombrado.

## Contenido del exe final

La primera ejecucion extrae los componentes necesarios a `%TEMP%/.net/`. Es el comportamiento normal de .NET single-file; lo que se distribuye sigue siendo solamente un exe.

| Comprobacion | Resultado |
| --- | --- |
| Archivos extraidos | 771 |
| Tamano extraido | 319226236 bytes |
| PRI | `resources.pri` y cuatro PRI de Windows App SDK |
| XBF sueltos | 0; los XBF requeridos estan integrados en el PRI |
| DLL VC++ incluidas | 10 |
| Icono de la app | Incluido |
| `Modules/Alchitex/Assets/materials.json` | Incluido |
| Ruta `C:\Users\Dark` dentro del exe | No encontrada |

No hay `node_modules` ni se requiere Node.js para ejecutar o publicar el producto final.

## Pruebas ejecutadas

| Prueba | Resultado |
| --- | --- |
| Build y publish limpios desde script | PASS |
| Salida con exactamente un exe | PASS |
| PE x64 | PASS |
| Ejecucion fuera del repositorio | PASS |
| Directorio de trabajo `C:\Windows\Temp` | PASS |
| Copia renombrada a `Vanilla-RTX-App-Enhanced-Portable-x64.exe` | PASS |
| PATH reducido, sin SDK/Visual Studio heredado | PASS |
| Carga de interfaz WinUI | PASS, titulo `Vanilla RTX App` |
| Cierre normal | PASS, codigo 0 |
| .NET, WinAppSDK y VC++ desde cache del bundle | PASS |
| Escritura y lectura de configuracion | PASS, `%LocalAppData%/Vanilla RTX App/settings.json` |
| Recursos de interfaz y Alchitex | PASS |
| Rutas absolutas del equipo de desarrollo | No encontradas en el binario |
| Firma de DLL VC++ | PASS, `Valid` |
| Firma del exe | No firmado; limitacion conocida |

## Limitaciones

No habia Windows Sandbox disponible y la consulta del componente Sandbox requeria elevacion. Por eso no se pudo arrancar una VM Windows completamente limpia ni desinstalar los runtimes del host sin alterar el entorno del usuario.

El host tenia Windows App Runtime y VC++ instalados. Para evitar concluir solo por eso, se inspeccionaron las rutas de modulos del proceso: `Microsoft.WindowsAppRuntime.dll`, `System.Private.CoreLib.dll` y las diez DLL VC++ relevantes se cargaron desde la autoextraccion del exe. El smoke test elimina las rutas de SDK/Visual Studio del proceso, usa un directorio externo y renombra el unico archivo final.

No se puede garantizar el comportamiento de cada antivirus corporativo o SmartScreen sin distribuir y firmar una release real. El exe actual no tiene firma Authenticode; SmartScreen puede advertir o bloquear por reputacion aunque el empaquetado sea correcto.

## Requisitos de usuario final

- Windows 10 version 2004/build 19041 o posterior, o Windows 11, x64.
- Espacio temporal para aproximadamente 320 MB de extraccion y permiso de escritura en `%TEMP%` y `%LocalAppData%`.
- No instalar .NET, Windows App Runtime, VC++ Redistributable, SDK, IDE, Node.js ni MSIX.
- Minecraft Bedrock compatible solo para funciones que administran packs; no para abrir la app.
- Conexion a Internet solo para actualizaciones, anuncios y descargas remotas.

## Generar y distribuir futuras versiones

1. Instalar .NET SDK 10.0.401 o un parche compatible en el equipo de compilacion.
2. Ejecutar `pwsh -File .\scripts\publish-portable.ps1` en la raiz del repositorio.
3. Ejecutar `pwsh -File .\scripts\test-portable.ps1` y exigir resultado `PASS`.
4. Distribuir solamente `artifacts\portable-x64\Vanilla RTX App.exe`; puede cambiarse el nombre si se desea.
5. Publicar el SHA-256 mostrado por el script. Para una release publica, firmar el archivo con certificado Authenticode y sello de tiempo antes de publicarlo.

Un unico exe es tecnicamente viable y se valido para esta aplicacion. Un zip es opcional como contenedor de descarga o para acompanar el checksum; no se necesita para llevar DLL adicionales.

## Referencias

- Microsoft: [despliegue self-contained de Windows App SDK](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- Microsoft: [WinUI unpackaged](https://learn.microsoft.com/windows/apps/package-and-deploy/unpackage-winui-app)
- Microsoft: [publicacion single-file de .NET](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview)
