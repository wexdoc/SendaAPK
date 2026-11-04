// Modified for SendaAPK (2026); original work remains credited in NOTICE.
using AlphaOmega.Debug;
using AlphaOmega.Debug.Dex;
using PulseAPK.Core.Abstractions.Patching;

namespace PulseAPK.Core.Services.Patching;

public sealed class DexMethodLookupService : IDexMethodLookupService
{
    public bool ContainsMethodReference(byte[] dexData, string classDescriptor, string methodName, string signature)
    {
        ArgumentNullException.ThrowIfNull(dexData);
        using var stream = new MemoryStream(dexData, writable: false);
        using var loader = new StreamLoader(stream);
        using var dexFile = new DexFile(loader);

        foreach (var method in dexFile.METHOD_ID_ITEM)
        {
            if (!string.Equals(method.class_idx.descriptor_idx.data, classDescriptor, StringComparison.Ordinal) ||
                !string.Equals(method.name_idx.data, methodName, StringComparison.Ordinal)) continue;

            var prototype = method.proto_idx;
            var parameters = prototype.parameters_off?.type_idx;
            var parameterTypes = parameters is null ? "" : string.Concat(parameters.Select(p => p.descriptor_idx.data));
            var actualSignature = $"({parameterTypes}){prototype.return_type_idx.descriptor_idx.data}";
            if (string.Equals(actualSignature, signature, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
