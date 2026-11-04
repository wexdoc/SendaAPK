using System.Xml;
using System.Xml.Linq;

namespace PulseAPK.Core.Services;

public sealed class ManifestAnalysisResult
{
    public string PackageName { get; set; } = "";
    public string TargetSdk { get; set; } = "No declarado";
    public List<string> Permissions { get; set; } = new();
    public List<CodeEvidence> Findings { get; set; } = new();
}

public static class ManifestAnalysisService
{
    private static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";
    private static readonly IReadOnlyDictionary<string, string> SensitivePermissions = new Dictionary<string, string>
    {
        ["READ_SMS"] = "Declara acceso a mensajes SMS; requiere comprobar si es necesario para su función.",
        ["RECEIVE_SMS"] = "Declara recepción de SMS; su declaración no prueba que tenga acceso concedido.",
        ["SEND_SMS"] = "Declara envío de SMS; revisa su justificación antes de conceder acceso.",
        ["READ_CONTACTS"] = "Declara lectura de contactos.",
        ["READ_CALL_LOG"] = "Declara acceso al registro de llamadas.",
        ["RECORD_AUDIO"] = "Declara acceso al micrófono.",
        ["CAMERA"] = "Declara acceso a la cámara.",
        ["ACCESS_FINE_LOCATION"] = "Declara acceso a ubicación precisa.",
        ["ACCESS_BACKGROUND_LOCATION"] = "Declara acceso a ubicación en segundo plano.",
        ["REQUEST_INSTALL_PACKAGES"] = "Declara capacidad para solicitar instalación de otros paquetes; no permite instalación silenciosa por sí sola.",
        ["SYSTEM_ALERT_WINDOW"] = "Declara solicitud de ventanas sobre otras aplicaciones; requiere acceso especial del usuario.",
        ["MANAGE_EXTERNAL_STORAGE"] = "Declara solicitud de acceso amplio al almacenamiento; depende de la versión de Android y del acceso concedido.",
        ["QUERY_ALL_PACKAGES"] = "Declara visibilidad amplia de aplicaciones instaladas.",
        ["RECEIVE_BOOT_COMPLETED"] = "Declara recepción del arranque del dispositivo; puede tener usos legítimos."
    };

    public static ManifestAnalysisResult Analyse(string path, CancellationToken token = default)
    {
        var options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10 * 1024 * 1024 };
        using var reader = XmlReader.Create(path, options);
        var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        var root = document.Root;
        if (root?.Name.LocalName != "manifest") throw new InvalidDataException("El XML no es un manifiesto Android válido.");
        var result = new ManifestAnalysisResult { PackageName = (string?)root.Attribute("package") ?? "No declarado" };
        result.TargetSdk = (string?)root.Element("uses-sdk")?.Attribute(Android + "targetSdkVersion") ?? "No declarado";
        // Apktool normally moves uses-sdk into apktool.yml even when decoding only the manifest.
        if (result.TargetSdk == "No declarado")
        {
            var metadata = Path.Combine(Path.GetDirectoryName(path)!, "apktool.yml");
            if (File.Exists(metadata) && new FileInfo(metadata).Length <= 10 * 1024 * 1024)
            {
                var sdk = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(metadata), @"(?m)^  targetSdkVersion:\s*['""']?(\d+)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(250));
                result.TargetSdk = sdk.Success ? sdk.Groups[1].Value + " (metadatos de Apktool)" : "No disponible en la salida decodificada";
            }
        }
        foreach (var element in root.Elements().Where(e => e.Name.LocalName is "uses-permission" or "uses-permission-sdk-23"))
        {
            token.ThrowIfCancellationRequested();
            var permission = (string?)element.Attribute(Android + "name") ?? "";
            if (permission.Length == 0) continue;
            result.Permissions.Add(permission);
            if (permission.StartsWith("android.permission.", StringComparison.Ordinal) && SensitivePermissions.TryGetValue(permission[19..], out var explanation))
                Add(element, "Permiso a revisar", explanation);
        }
        result.Permissions = result.Permissions.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var application = root.Element("application");
        if (application is null) return result;
        if (IsTrue(application, "debuggable")) Add(application, "Configuración", "Depuración habilitada explícitamente. Conviene revisar si corresponde a una versión de distribución.");
        if (IsTrue(application, "usesCleartextTraffic")) Add(application, "Configuración", "Declara permiso de tráfico sin cifrar. Una configuración de seguridad de red puede modificar su efecto; no demuestra tráfico real.");
        if (IsTrue(application, "testOnly")) Add(application, "Configuración", "Aplicación marcada explícitamente como solo para pruebas.");
        foreach (var component in application.Elements().Where(e => e.Name.LocalName is "activity" or "activity-alias" or "service" or "receiver" or "provider"))
        {
            token.ThrowIfCancellationRequested();
            var permission = (string?)component.Attribute(Android + "permission") ?? (string?)application.Attribute(Android + "permission");
            var disabled = string.Equals((string?)component.Attribute(Android + "enabled"), "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals((string?)application.Attribute(Android + "enabled"), "false", StringComparison.OrdinalIgnoreCase);
            if (!disabled && IsTrue(component, "exported") && string.IsNullOrWhiteSpace(permission))
                Add(component, "Componente exportado", "Exportado explícitamente sin permiso general declarado. Puede ser necesario para su función; no demuestra vulnerabilidad. Providers pueden usar permisos de lectura/escritura o rutas adicionales.");
            if ((string?)component.Attribute(Android + "permission") == "android.permission.BIND_ACCESSIBILITY_SERVICE")
                Add(component, "Servicio especial", "Declara un servicio de accesibilidad. El usuario debe habilitarlo; revisa su finalidad antes de conceder acceso.");
            if ((string?)component.Attribute(Android + "permission") == "android.permission.BIND_DEVICE_ADMIN")
                Add(component, "Servicio especial", "Declara un componente de administración del dispositivo. Revisa su finalidad antes de activarlo.");
        }
        return result;

        void Add(XElement element, string category, string explanation)
        {
            var line = ((IXmlLineInfo)element).LineNumber;
            var identity = (string?)element.Attribute(Android + "name");
            var evidence = element.Name.LocalName + (identity is null ? "" : " " + identity) + " " + string.Join(" ", element.Attributes().Select(a => a.Name.LocalName + "=" + a.Value));
            result.Findings.Add(new(category, explanation, "manifest/AndroidManifest.xml", line, evidence));
        }
    }
    private static bool IsTrue(XElement element, string attribute) => string.Equals((string?)element.Attribute(Android + attribute), "true", StringComparison.OrdinalIgnoreCase);
}
