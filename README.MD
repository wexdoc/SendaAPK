# SendaAPK

SendaAPK es un proyecto de escritorio, actualmente **preliminar**, para explorar APK en el equipo. Se basa en [PulseAPK-Core](https://github.com/deemoun/PulseAPK-Core), de Dmitry Yarygin y sus colaboradores, y conserva su licencia Apache 2.0 y sus avisos.

Visita el [sitio web de SendaAPK](https://sendaapk.wexdoc.chatgpt.site/).

**Estado:** el producto no tiene una versión estable. El análisis no instala ni ejecuta el APK. Los permisos, las URL y los indicios del código no demuestran infección; ningún resultado garantiza que el archivo sea seguro. Defender depende de la configuración del sistema y puede no estar disponible en modo privado. La cuarentena no desinfecta el contenido.

El proyecto incluye pruebas automatizadas para el analizador local y otras funciones. La verificación independiente de firmas, el análisis ampliado de Ren'Py y parte de la renovación de la interfaz siguen pendientes.

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

## Tareas y archivos

El análisis, la descompilación, la compilación y el parcheo comparten una cola: solo una tarea pesada se ejecuta a la vez. La sección lateral muestra la tarea, la etapa y el tiempo, y conserva su estado al navegar. Puedes cancelar una tarea mientras espera o desde su sección mientras se ejecuta. Las descargas de herramientas también usan la cola.

Al cerrar con tareas pendientes, puedes seguir trabajando o solicitar la cancelación y esperar hasta que sea seguro cerrar. Las transferencias de cuarentena y restauración que ya comenzaron continúan hasta alcanzar un estado recuperable. Si una tarea quedó interrumpida, revísala en la barra lateral y confirma sus opciones antes de repetirla; no se ejecuta automáticamente. Si no se puede confirmar que un proceso terminó, la cola se pausa para que puedas revisarlo antes de reanudarla. Cancelar una operación de Defender no garantiza que Windows haya detenido un análisis que ya inició.

En Windows, una segunda instancia de esta versión solicita abrir el APK en la primera. La solicitud queda pendiente y no cambia el archivo de una tarea activa. Cierra cualquier versión anterior de SendaAPK antes de iniciar esta versión: las versiones sin coordinación de instancias no participan en este mecanismo.

Las salidas se generan por separado y no deben reemplazar el original ni otros resultados existentes. La descompilación requiere una carpeta de salida nueva. Si se cancela durante la firma después de completar la compilación, se conserva el APK compilado sin firmar. La cuarentena mantiene metadatos de recuperación; si una restauración interrumpida deja ocupada la ruta original, revisa ambos archivos antes de volver a intentar restaurar, ya que no se sobrescribirá el archivo existente.

La descarga integrada de Apktool aún no comprueba su integridad mediante una suma independiente ni fija una versión. Comprueba el origen y la integridad antes de utilizar la herramienta. Las herramientas existentes no se reemplazan automáticamente por una descarga.

## Sitio y patrocinio

El código del sitio está en `website/`. SendaAPK busca financiación voluntaria para su desarrollo; el enlace de patrocinio permanece pendiente mientras se verifica la configuración y la disponibilidad de los cobros. No se procesan tarjetas en el sitio.

## Créditos y licencias

La licencia del código original está en `LICENSE.md` y los avisos en `NOTICE`. El historial conserva la procedencia del código; los archivos modificados se identifican en `CHANGES-SENDAAPK.md`. Las dependencias y herramientas que se distribuyan por separado requieren revisión de sus propias licencias antes de publicar binarios. Este proyecto no está afiliado ni respaldado por el autor original.
