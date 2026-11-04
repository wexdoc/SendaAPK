# SendaAPK

SendaAPK es un proyecto de escritorio, actualmente **preliminar**, para explorar APK en el equipo. Se basa en [PulseAPK-Core](https://github.com/deemoun/PulseAPK-Core), de Dmitry Yarygin y sus colaboradores, y conserva su licencia Apache 2.0 y sus avisos.

Visita el [sitio web de SendaAPK](https://sendaapk.wexdoc.chatgpt.site/).

**Estado:** el producto no tiene una versión estable. El análisis no instala ni ejecuta el APK. Los permisos, las URL y los indicios del código no demuestran infección; ningún resultado garantiza que el archivo sea seguro. Defender depende de la configuración del sistema y puede no estar disponible en modo privado. La cuarentena no desinfecta el contenido.

En la última comprobación en Windows con .NET 10 pasaron 42 pruebas del analizador local y 111 de la batería general. Esas cifras son una instantánea, no una garantía de funcionamiento. La verificación de firmas, el análisis ampliado de Ren'Py, la coordinación global de operaciones y la renovación de la interfaz están pendientes.

## Desarrollar

Se necesita el SDK de .NET 10. En Windows, desde la carpeta del proyecto:

```powershell
dotnet build src/PulseAPK.Avalonia/PulseAPK.Avalonia.csproj
dotnet run --project src/PulseAPK.Avalonia/PulseAPK.Avalonia.csproj
dotnet test tests/local/PulseAPK.Local.Tests.csproj
dotnet test tests/unit/PulseAPK.Tests/PulseAPK.Tests.csproj
```

Algunas funciones avanzadas requieren Java y herramientas externas configuradas por el usuario. Los APK personales, informes, credenciales y binarios de herramientas externas no deben añadirse al repositorio. Se conservan los directorios de datos `PulseAPK` para leer la configuración y los análisis de la instalación anterior.

La cuarentena usa controles de bloqueo e identidad específicos de Windows y, por ahora, solo está disponible en ese sistema. Conserva una copia verificable y permite restaurarla sin sobrescribir un archivo existente; no modifica ni limpia el APK.

## Sitio y patrocinio

El código del sitio está en `website/`. SendaAPK busca financiación voluntaria para su desarrollo; el enlace de patrocinio permanece pendiente mientras se verifica la configuración y la disponibilidad de los cobros. No se procesan tarjetas en el sitio.

## Créditos y licencias

La licencia del código original está en `LICENSE.md` y los avisos en `NOTICE`. El historial conserva la procedencia del código; los archivos modificados se identifican en `CHANGES-SENDAAPK.md`. Las dependencias y herramientas que se distribuyan por separado requieren revisión de sus propias licencias antes de publicar binarios. Este proyecto no está afiliado ni respaldado por el autor original.
